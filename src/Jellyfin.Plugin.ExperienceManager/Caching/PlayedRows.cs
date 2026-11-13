using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ExperienceManager.Caching;

/// <summary>
/// A title in a Top 10, with how many different users played it in the window.
/// </summary>
/// <param name="Item">The film or show.</param>
/// <param name="Users">How many different users played it.</param>
/// <param name="Last">When it was last played.</param>
public sealed record RankedTitle(BaseItem Item, int Users, DateTime Last);

/// <summary>
/// The rows made from what people play: Top 10 Films and Shows, counted once for everyone, and each user's
/// Finish these collections.
/// </summary>
public sealed class PlayedRows : IDisposable
{
    /// <summary>How far back plays count towards Top 10.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>How many titles are kept per Top 10, so a viewer who may not see some still gets 10.</summary>
    internal const int Kept = 50;

    // A user's most recently played items read per count: far more than a week of viewing.
    private const int PerUser = 500;

    private static readonly TimeSpan SharedMaxAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UserMaxAge = TimeSpan.FromMinutes(5);

    private readonly ILibraryManager _library;
    private readonly IUserDataManager _userData;
    private readonly IUserManager _users;
    private readonly Home.IHomeSources _sources;
    private readonly Func<Folder, IEnumerable<BaseItem>> _children;
    private readonly TimeProvider _time;
    private readonly SharedList<RankedTitle> _top;
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, IReadOnlyList<BaseItem> Items, IReadOnlyDictionary<Guid, string> Chips)> _finish = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayedRows"/> class.
    /// </summary>
    /// <param name="library">The library.</param>
    /// <param name="userData">The users' play data, whose saves refresh the rows.</param>
    /// <param name="users">The users.</param>
    /// <param name="sources">What each user may see.</param>
    public PlayedRows(ILibraryManager library, IUserDataManager userData, IUserManager users, Home.IHomeSources sources)
        : this(library, userData, users, sources, TimeProvider.System)
    {
    }

    internal PlayedRows(ILibraryManager library, IUserDataManager userData, IUserManager users, Home.IHomeSources sources, TimeProvider time, Func<Folder, IEnumerable<BaseItem>>? children = null)
    {
        _library = library;
        _userData = userData;
        _users = users;
        _sources = sources;
        _children = children ?? (f => f.GetLinkedChildren());
        _time = time;
        _top = new SharedList<RankedTitle>(BuildTop, SharedMaxAge, Debounce, time);
        _userData.UserDataSaved += OnUserDataSaved;
    }

    /// <summary>
    /// Gets the films ranked by how many different users played them in the last 7 days, highest first.
    /// </summary>
    /// <param name="hit">Whether the counts came from the cache.</param>
    /// <returns>The films.</returns>
    public IReadOnlyList<BaseItem> TopFilms(out bool hit) => _top.Get(out hit).Where(t => t.Item is Movie).Select(t => t.Item).ToList();

    /// <summary>
    /// Gets the shows ranked by how many different users played an episode of them in the last 7 days, highest first.
    /// </summary>
    /// <param name="hit">Whether the counts came from the cache.</param>
    /// <returns>The shows.</returns>
    public IReadOnlyList<BaseItem> TopShows(out bool hit) => _top.Get(out hit).Where(t => t.Item is Series).Select(t => t.Item).ToList();

    /// <summary>
    /// Gets the collections a user has started and not finished, most recently watched first.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="hit">Whether the list came from the cache.</param>
    /// <returns>The collections.</returns>
    public IReadOnlyList<BaseItem> FinishCollections(User user, out bool hit) => Finish(user, out hit).Items;

    /// <summary>
    /// Gets the "N of M watched" chip for each collection in a user's Finish these collections.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The chips, by collection id.</returns>
    public IReadOnlyDictionary<Guid, string> FinishChips(User user) => Finish(user, out _).Chips;

    /// <inheritdoc />
    public void Dispose()
    {
        _userData.UserDataSaved -= OnUserDataSaved;
        _top.Dispose();
    }

    // Each user's plays in the window: a user counts once per title however often they played, and an episode
    // counts for its show.
    private IReadOnlyList<RankedTitle> BuildTop()
    {
        var cutoff = _time.GetUtcNow().UtcDateTime - Window;
        var titles = new Dictionary<Guid, RankedTitle>();
        foreach (var user in _users.GetUsers())
        {
            var played = _library.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false,
                OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
                Limit = PerUser,
            });
            if (played.Count == 0)
            {
                continue;
            }

            var data = _userData.GetUserDataBatch(played, user);
            var counted = new HashSet<Guid>();
            foreach (var item in played)
            {
                if (data.GetValueOrDefault(item.Id)?.LastPlayedDate is not { } at || at < cutoff)
                {
                    continue;
                }

                var title = item is Episode episode ? _library.GetItemById(episode.SeriesId) as Series : item;
                if (title is null || !counted.Add(title.Id))
                {
                    continue;
                }

                titles[title.Id] = titles.TryGetValue(title.Id, out var seen)
                    ? seen with { Users = seen.Users + 1, Last = at > seen.Last ? at : seen.Last }
                    : new RankedTitle(title, 1, at);
            }
        }

        return titles.Values
            .GroupBy(t => t.Item is Movie)
            .SelectMany(g => g.OrderByDescending(t => t.Users).ThenByDescending(t => t.Last).Take(Kept))
            .ToList();
    }

    private (IReadOnlyList<BaseItem> Items, IReadOnlyDictionary<Guid, string> Chips) Finish(User user, out bool hit)
    {
        var now = _time.GetUtcNow();
        if (_finish.TryGetValue(user.Id, out var cached) && now - cached.At < UserMaxAge)
        {
            hit = true;
            return (cached.Items, cached.Chips);
        }

        var started = new List<(BaseItem Box, DateTime Last, int Played, int Count)>();
        foreach (var box in _library.GetItemList(new InternalItemsQuery(user) { IncludeItemTypes = [BaseItemKind.BoxSet], Recursive = true }).OfType<Folder>())
        {
            var films = _children(box).Where(c => c is Movie && _sources.IsVisible(c, user)).ToList();
            if (films.Count == 0)
            {
                continue;
            }

            var data = _userData.GetUserDataBatch(films, user);
            var played = films.Where(f => data.GetValueOrDefault(f.Id)?.Played == true).ToList();
            if (played.Count == 0 || played.Count == films.Count)
            {
                continue;
            }

            var last = played.Select(f => data.GetValueOrDefault(f.Id)?.LastPlayedDate ?? DateTime.MinValue).Max();
            started.Add((box, last, played.Count, films.Count));
        }

        var ordered = started.OrderByDescending(s => s.Last).Take(HomeCache.UserLimit).ToList();
        var items = ordered.Select(s => s.Box).ToList();
        var chips = ordered.ToDictionary(s => s.Box.Id, s => string.Create(CultureInfo.InvariantCulture, $"{s.Played} of {s.Count} watched"));
        _finish[user.Id] = (now, items, chips);
        hit = false;
        return (items, chips);
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        _finish.TryRemove(e.UserId, out _);
        _top.Changed();
    }
}
