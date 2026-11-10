using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Caching;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.ExperienceManager.Home;

/// <summary>
/// What a section shows, said without loading it, so a reach adapter can map it to a client's own row
/// types: a built-in kind, a library's latest items, a library query, a collection or playlist, or one of
/// Experience Manager's own sections.
/// </summary>
/// <param name="Kind">One of the <see cref="SectionSources"/> kinds.</param>
/// <param name="ParentId">The library, collection or playlist it reads, if any.</param>
/// <param name="ItemTypes">The item types it lists, as Jellyfin's <c>includeItemTypes</c>, if any.</param>
public sealed record SectionSource(string Kind, string? ParentId = null, string? ItemTypes = null);

/// <summary>
/// The kinds of <see cref="SectionSource"/>.
/// </summary>
public static class SectionSources
{
    /// <summary>The featured banner.</summary>
    public const string Featured = "featured";

    /// <summary>The user's libraries.</summary>
    public const string MyMedia = "mymedia";

    /// <summary>Items the user started and has not finished.</summary>
    public const string ContinueWatching = "continue";

    /// <summary>The next episode of each show the user is watching.</summary>
    public const string NextUp = "nextup";

    /// <summary>The latest items in one library.</summary>
    public const string Latest = "latest";

    /// <summary>A Jellyfin item query over one library.</summary>
    public const string Query = "query";

    /// <summary>A collection or playlist.</summary>
    public const string Collection = "collection";

    /// <summary>An Experience Manager section no client knows natively.</summary>
    public const string Section = "section";
}

/// <summary>
/// One section of a page and how to load its items for a user.
/// </summary>
/// <param name="Id">The section's stable id.</param>
/// <param name="Title">The title shown above it.</param>
/// <param name="Display">How to show it.</param>
/// <param name="Source">What it shows, without loading it.</param>
/// <param name="SeeAll">Where "see all" leads, or null.</param>
/// <param name="FirstPage">How many items the page call inlines.</param>
/// <param name="Load">Loads every item the user may see, in order, and whether they came from the cache.</param>
public sealed record PageSection(
    string Id,
    string Title,
    SectionDisplay Display,
    SectionSource Source,
    SeeAllTarget? SeeAll,
    int FirstPage,
    Func<User, (IReadOnlyList<BaseItem> Items, bool Hit)> Load);

/// <summary>
/// The pages, each resolved into its sections for a user.
/// </summary>
public interface IPages
{
    /// <summary>
    /// Gets a page's layout version, which changes whenever its layout does.
    /// </summary>
    /// <param name="pageId">The page id.</param>
    /// <returns>The version, or null if there is no such page.</returns>
    string? LayoutVersion(string pageId);

    /// <summary>
    /// Gets a page's sections for a user, in order.
    /// </summary>
    /// <param name="pageId">The page id.</param>
    /// <param name="user">The user.</param>
    /// <returns>The sections, or none if there is no such page.</returns>
    IReadOnlyList<PageSection> SectionsFor(string pageId, User user);
}

/// <summary>
/// The pages stored in the plugin's database, each section instance resolved for the user: Latest for each
/// library becomes one section per movie and show library the user can see, in the user's library order.
/// </summary>
public sealed class StoredPages : IPages
{
    /// <summary>How many featured items the page shows.</summary>
    internal const int FeaturedCount = 5;

    /// <summary>How many cards a row shows before "see all".</summary>
    internal const int RowCount = 16;

    private readonly PageStore _store;
    private readonly HomeCache _cache;
    private readonly IHomeSources _sources;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="StoredPages"/> class.
    /// </summary>
    /// <param name="store">The stored pages.</param>
    /// <param name="cache">The home cache.</param>
    /// <param name="sources">The user's libraries and what they may see.</param>
    public StoredPages(PageStore store, HomeCache cache, IHomeSources sources)
        : this(store, cache, sources, TimeProvider.System)
    {
    }

    internal StoredPages(PageStore store, HomeCache cache, IHomeSources sources, TimeProvider time)
    {
        _store = store;
        _cache = cache;
        _sources = sources;
        _time = time;
    }

    /// <inheritdoc />
    public string? LayoutVersion(string pageId) => _store.Get(pageId)?.LayoutVersion;

    /// <inheritdoc />
    public IReadOnlyList<PageSection> SectionsFor(string pageId, User user)
    {
        var page = _store.Get(pageId);
        if (page is null)
        {
            return [];
        }

        var views = new Lazy<IReadOnlyList<LibraryView>>(() => _sources.GetViews(user));
        var sections = new List<PageSection>();
        foreach (var stored in page.Sections)
        {
            switch (stored.Type)
            {
                case SectionTypes.Featured:
                    sections.Add(new(stored.Id, stored.Title ?? "Featured", new SectionDisplay("feature", "landscape"), new SectionSource(SectionSources.Featured), null, FeaturedCount, u => Featured(u)));
                    break;
                case SectionTypes.MyMedia:
                    sections.Add(new(stored.Id, stored.Title ?? "My Media", new SectionDisplay("row", "landscape"), new SectionSource(SectionSources.MyMedia), null, RowCount, _ => (views.Value.Select(v => v.Item).OfType<BaseItem>().ToList(), true)));
                    break;
                case SectionTypes.ContinueWatching:
                    sections.Add(new(
                        stored.Id,
                        stored.Title ?? "Continue Watching",
                        new SectionDisplay("row", "landscape"),
                        new SectionSource(SectionSources.ContinueWatching),
                        new SeeAllTarget(null, "Movie,Episode", "DatePlayed", "Descending", "IsResumable"),
                        RowCount,
                        u => Visible(u, _cache.ContinueWatching(u, out var hit), hit)));
                    break;
                case SectionTypes.NextUp:
                    sections.Add(new(stored.Id, stored.Title ?? "Next Up", new SectionDisplay("row", "landscape"), new SectionSource(SectionSources.NextUp), null, RowCount, u => Visible(u, _cache.NextUp(u, out var hit), hit)));
                    break;
                case SectionTypes.LatestLibraries:
                    sections.AddRange(Latest(views.Value));
                    break;
            }
        }

        return sections;
    }

    private IEnumerable<PageSection> Latest(IReadOnlyList<LibraryView> views)
    {
        foreach (var view in views.Where(v => v.Type is CollectionType.movies or CollectionType.tvshows))
        {
            var shows = view.Type == CollectionType.tvshows;
            var library = view.Id.ToString("N");
            yield return new PageSection(
                "latest-" + library,
                "Latest " + view.Name,
                new SectionDisplay("row", "portrait"),
                new SectionSource(SectionSources.Latest, library, shows ? "Series" : "Movie"),
                new SeeAllTarget(library, shows ? "Series" : "Movie", shows ? "DateLastContentAdded" : "DateCreated", "Descending", null),
                RowCount,
                u => Visible(u, _cache.Latest(view.Id, view.Type, out var hit), hit));
        }
    }

    // The featured items change within the day so the banner does not always open on the same film,
    // but stay the same for an hour so a page reload does not reshuffle it.
    private (IReadOnlyList<BaseItem> Items, bool Hit) Featured(User user)
    {
        var (visible, hit) = Visible(user, _cache.Featured(out var cacheHit), cacheHit);
        if (visible.Count <= FeaturedCount)
        {
            return (visible, hit);
        }

        var start = _time.GetUtcNow().Hour % (visible.Count - FeaturedCount + 1);
        return (visible.Skip(start).Concat(visible.Take(start)).ToList(), hit);
    }

    private (IReadOnlyList<BaseItem> Items, bool Hit) Visible(User user, IReadOnlyList<BaseItem> items, bool hit) =>
        (items.Where(i => _sources.IsVisible(i, user)).ToList(), hit);
}
