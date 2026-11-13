using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.ExperienceManager.Caching;

/// <summary>
/// A library query by genre or tag, as a section stores it.
/// </summary>
/// <param name="Library">The library to look in, or null for all.</param>
/// <param name="Genres">Items with any of these genres.</param>
/// <param name="Tags">Items with any of these tags.</param>
/// <param name="ItemTypes">The item kinds; none for films and shows.</param>
public sealed record LibraryQuery(Guid? Library, IReadOnlyList<string> Genres, IReadOnlyList<string> Tags, IReadOnlyList<BaseItemKind> ItemTypes)
{
    /// <summary>
    /// Gets the query as one string, the same for the same query.
    /// </summary>
    public string Key => string.Join(
        "|",
        Library?.ToString("N") ?? string.Empty,
        string.Join(",", Genres.Select(g => g.ToLowerInvariant()).Order(StringComparer.Ordinal)),
        string.Join(",", Tags.Select(t => t.ToLowerInvariant()).Order(StringComparer.Ordinal)),
        string.Join(",", ItemTypes.Order()));
}

/// <summary>
/// The home page's data. Lists that are the same for everyone (featured candidates, Latest per
/// library) are built once and filtered per user at request time; lists that differ per user
/// (Continue Watching, Next Up) are kept per user and dropped when that user's watch data changes.
/// Every query sets a limit and sorts in the database; nothing is looked up one item at a time.
/// </summary>
public sealed class HomeCache : IDisposable
{
    /// <summary>The most items a shared list holds, before per-user filtering.</summary>
    internal const int SharedLimit = 120;

    /// <summary>The most items a per-user list holds.</summary>
    internal const int UserLimit = 50;

    // Recent episodes read to find the shows with something new; grouped by series, this is enough
    // for a full row even when one show adds a whole season at once.
    internal const int RecentEpisodes = 600;

    private static readonly TimeSpan SharedMaxAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan UserMaxAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);

    private readonly ILibraryManager _library;
    private readonly ITVSeriesManager _tv;
    private readonly IUserDataManager _userData;
    private readonly TimeProvider _time;
    private readonly SharedList<BaseItem> _featured;
    private readonly ConcurrentDictionary<Guid, SharedList<BaseItem>> _latest = new();
    private readonly ConcurrentDictionary<string, SharedList<BaseItem>> _sources = new();
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, IReadOnlyList<BaseItem> Items)> _resume = new();
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, IReadOnlyList<BaseItem> Items)> _nextUp = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeCache"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="tv">The TV series manager, for Next Up.</param>
    /// <param name="userData">The user data manager, whose saves clear a user's lists.</param>
    public HomeCache(ILibraryManager library, ITVSeriesManager tv, IUserDataManager userData)
        : this(library, tv, userData, TimeProvider.System)
    {
    }

    internal HomeCache(ILibraryManager library, ITVSeriesManager tv, IUserDataManager userData, TimeProvider time)
    {
        _library = library;
        _tv = tv;
        _userData = userData;
        _time = time;
        _featured = new SharedList<BaseItem>(BuildFeatured, SharedMaxAge, Debounce, time);
        _library.ItemAdded += OnLibraryChanged;
        _library.ItemRemoved += OnLibraryChanged;
        _library.ItemUpdated += OnLibraryChanged;
        _userData.UserDataSaved += OnUserDataSaved;
    }

    /// <summary>
    /// Gets the featured candidates: the newest films with a backdrop, for everyone.
    /// </summary>
    /// <param name="hit">Whether the list came from the cache.</param>
    /// <returns>The candidates, newest first.</returns>
    public IReadOnlyList<BaseItem> Featured(out bool hit) => _featured.Get(out hit);

    /// <summary>
    /// Gets a library's newest films, or its shows with the newest episodes, for everyone.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="type">What the library holds.</param>
    /// <param name="hit">Whether the list came from the cache.</param>
    /// <returns>The items, newest first.</returns>
    public IReadOnlyList<BaseItem> Latest(Guid libraryId, CollectionType? type, out bool hit) =>
        _latest.GetOrAdd(libraryId, id => new SharedList<BaseItem>(() => BuildLatest(id, type), SharedMaxAge, Debounce, _time)).Get(out hit);

    /// <summary>
    /// Gets the items a library query finds, shared by every user and refreshed as the library changes.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <param name="hit">Whether the items came from the cache.</param>
    /// <returns>The items, by name.</returns>
    public IReadOnlyList<BaseItem> Query(LibraryQuery query, out bool hit) =>
        Shared("query:" + query.Key, () => BuildQuery(query), out hit);

    /// <summary>
    /// Gets a collection's or playlist's items in their own order, shared by every user.
    /// </summary>
    /// <param name="collectionId">The collection or playlist.</param>
    /// <param name="hit">Whether the items came from the cache.</param>
    /// <returns>The items.</returns>
    public IReadOnlyList<BaseItem> Collection(Guid collectionId, out bool hit) =>
        Shared("collection:" + collectionId.ToString("N"), () => (_library.GetItemById(collectionId) as Folder)?.GetLinkedChildren().Take(SharedLimit).ToList() ?? [], out hit);

    /// <summary>
    /// Gets an item's name.
    /// </summary>
    /// <param name="id">The item.</param>
    /// <returns>Its name, or null if there is no such item.</returns>
    public string? ItemName(Guid id) => _library.GetItemById(id)?.Name;

    /// <summary>
    /// Gets an item, such as what an announcement's button opens.
    /// </summary>
    /// <param name="id">The item.</param>
    /// <returns>The item, or null if there is no such item.</returns>
    public BaseItem? Item(Guid id) => _library.GetItemById(id);

    /// <summary>
    /// Gets a value indicating whether an item, such as a library or collection, still exists.
    /// </summary>
    /// <param name="id">The item.</param>
    /// <returns>Whether it exists.</returns>
    public bool Exists(Guid id) => _library.GetItemById(id) is not null;

    /// <summary>
    /// Leaves out the items a user has watched: those marked played for them.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="items">The items.</param>
    /// <returns>The items the user has not watched, in order.</returns>
    public IReadOnlyList<BaseItem> Unwatched(User user, IReadOnlyList<BaseItem> items)
    {
        if (items.Count == 0)
        {
            return items;
        }

        var data = _userData.GetUserDataBatch(items, user);
        return items.Where(i => data.GetValueOrDefault(i.Id)?.Played != true).ToList();
    }

    /// <summary>
    /// Gets what the user has started and not finished, most recently played first.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="hit">Whether the list came from the cache.</param>
    /// <returns>The items.</returns>
    public IReadOnlyList<BaseItem> ContinueWatching(User user, out bool hit) => PerUser(_resume, user, BuildResume, out hit);

    /// <summary>
    /// Gets the next episode of each show the user is watching.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="hit">Whether the list came from the cache.</param>
    /// <returns>The episodes.</returns>
    public IReadOnlyList<BaseItem> NextUp(User user, out bool hit) => PerUser(_nextUp, user, BuildNextUp, out hit);

    /// <summary>
    /// Builds the shared lists that have never been built: featured candidates, and Latest for every
    /// movie and show library.
    /// </summary>
    public void Warm()
    {
        _featured.Warm();
        foreach (var folder in _library.GetVirtualFolders())
        {
            if (Guid.TryParse(folder.ItemId, out var id) && folder.CollectionType is { } options
                && Enum.TryParse<CollectionType>(options.ToString(), true, out var type)
                && type is CollectionType.movies or CollectionType.tvshows)
            {
                Latest(id, type, out _);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _library.ItemAdded -= OnLibraryChanged;
        _library.ItemRemoved -= OnLibraryChanged;
        _library.ItemUpdated -= OnLibraryChanged;
        _userData.UserDataSaved -= OnUserDataSaved;
        _featured.Dispose();
        foreach (var list in _latest.Values.Concat(_sources.Values))
        {
            list.Dispose();
        }
    }

    /// <summary>
    /// Builds Latest for a show library: the series of the newest episodes, in the order their
    /// newest episode arrived, fetched in one query.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="libraryId">The show library.</param>
    /// <returns>The series, newest first.</returns>
    internal static IReadOnlyList<BaseItem> LatestShows(ILibraryManager library, Guid libraryId)
    {
        var episodes = library.GetItemList(new InternalItemsQuery
        {
            ParentId = libraryId,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Episode],
            IsVirtualItem = false,
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
            Limit = RecentEpisodes,
        });
        var seriesIds = episodes.OfType<Episode>()
            .Select(e => e.SeriesId)
            .Where(id => !id.Equals(Guid.Empty))
            .Distinct()
            .Take(SharedLimit)
            .ToArray();
        if (seriesIds.Length == 0)
        {
            return [];
        }

        var byId = library.GetItemList(new InternalItemsQuery { ItemIds = seriesIds, IncludeItemTypes = [BaseItemKind.Series] })
            .ToDictionary(s => s.Id);
        return seriesIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    // The newest films, then those with a backdrop: the image check reads the item already in memory.
    private IReadOnlyList<BaseItem> BuildFeatured() =>
        _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            Recursive = true,
            IsVirtualItem = false,
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
            Limit = SharedLimit,
        }).Where(i => i.HasImage(ImageType.Backdrop, 0)).ToList();

    private IReadOnlyList<BaseItem> BuildLatest(Guid libraryId, CollectionType? type) =>
        type == CollectionType.tvshows
            ? LatestShows(_library, libraryId)
            : _library.GetItemList(new InternalItemsQuery
            {
                ParentId = libraryId,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie],
                IsVirtualItem = false,
                OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
                Limit = SharedLimit,
            });

    private IReadOnlyList<BaseItem> Shared(string key, Func<IReadOnlyList<BaseItem>> build, out bool hit) =>
        _sources.GetOrAdd(key, _ => new SharedList<BaseItem>(build, SharedMaxAge, Debounce, _time)).Get(out hit);

    private IReadOnlyList<BaseItem> BuildQuery(LibraryQuery query)
    {
        var items = new InternalItemsQuery
        {
            Recursive = true,
            Genres = query.Genres.ToArray(),
            Tags = query.Tags.ToArray(),
            IncludeItemTypes = query.ItemTypes.Count > 0 ? query.ItemTypes.ToArray() : [BaseItemKind.Movie, BaseItemKind.Series],
            IsVirtualItem = false,
            OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
            Limit = SharedLimit,
        };
        if (query.Library is { } library)
        {
            items.ParentId = library;
        }

        return _library.GetItemList(items);
    }

    private IReadOnlyList<BaseItem> BuildResume(User user) =>
        _library.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Recursive = true,
            IsResumable = true,
            IsVirtualItem = false,
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            Limit = UserLimit,
        });

    private IReadOnlyList<BaseItem> BuildNextUp(User user) =>
        _tv.GetNextUp(new NextUpQuery { User = user, Limit = UserLimit, EnableTotalRecordCount = false }, new DtoOptions(false)).Items;

    private IReadOnlyList<BaseItem> PerUser(
        ConcurrentDictionary<Guid, (DateTimeOffset At, IReadOnlyList<BaseItem> Items)> cache,
        User user,
        Func<User, IReadOnlyList<BaseItem>> build,
        out bool hit)
    {
        var now = _time.GetUtcNow();
        if (cache.TryGetValue(user.Id, out var cached) && now - cached.At < UserMaxAge)
        {
            hit = true;
            return cached.Items;
        }

        var items = build(user);
        cache[user.Id] = (now, items);
        hit = false;
        return items;
    }

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        _featured.Changed();
        foreach (var list in _latest.Values.Concat(_sources.Values))
        {
            list.Changed();
        }
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        _resume.TryRemove(e.UserId, out _);
        _nextUp.TryRemove(e.UserId, out _);
    }
}
