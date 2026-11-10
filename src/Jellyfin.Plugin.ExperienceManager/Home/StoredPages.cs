using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Caching;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using Jellyfin.Plugin.ExperienceManager.Programming;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.ExperienceManager.Home;

/// <summary>
/// The pages stored in the plugin's database, each resolved for the user: Latest for each library becomes one
/// section per movie and show library the user can see, in the user's library order, then Home's events on now
/// are laid over it.
/// </summary>
public sealed class StoredPages : IPages
{
    /// <summary>How many featured items the page shows.</summary>
    internal const int FeaturedCount = 5;

    /// <summary>How many cards a row shows before "see all".</summary>
    internal const int RowCount = 16;

    private readonly PageStore _store;
    private readonly EventTimeline _events;
    private readonly HomeCache _cache;
    private readonly IHomeSources _sources;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="StoredPages"/> class.
    /// </summary>
    /// <param name="store">The stored pages.</param>
    /// <param name="events">Which of Home's events are on.</param>
    /// <param name="cache">The home cache.</param>
    /// <param name="sources">The user's libraries and what they may see.</param>
    public StoredPages(PageStore store, EventTimeline events, HomeCache cache, IHomeSources sources)
        : this(store, events, cache, sources, TimeProvider.System)
    {
    }

    internal StoredPages(PageStore store, EventTimeline events, HomeCache cache, IHomeSources sources, TimeProvider time)
    {
        _store = store;
        _events = events;
        _cache = cache;
        _sources = sources;
        _time = time;
    }

    /// <inheritdoc />
    public string? LayoutVersion(string pageId, int? utcOffset = null)
    {
        var page = _store.Get(pageId);
        if (page is null)
        {
            return null;
        }

        var key = Active(page.Id, utcOffset).Key;
        return key.Length == 0 ? page.LayoutVersion : page.LayoutVersion + "-e" + key;
    }

    /// <inheritdoc />
    public IReadOnlyList<PageSection> SectionsFor(string pageId, User user, int? utcOffset = null)
    {
        var page = _store.Get(pageId);
        if (page is null)
        {
            return [];
        }

        var views = new Lazy<IReadOnlyList<LibraryView>>(() => _sources.GetViews(user));
        var expanded = new List<StoredSection>();
        foreach (var stored in page.Sections)
        {
            if (stored.Type == SectionTypes.LatestLibraries)
            {
                expanded.AddRange(views.Value
                    .Where(v => v.Type is CollectionType.movies or CollectionType.tvshows)
                    .Select(v => new StoredSection("latest-" + v.Id.ToString("N"), SectionTypes.Latest, null, new JsonObject { ["Library"] = v.Id.ToString("N") })));
            }
            else
            {
                expanded.Add(stored);
            }
        }

        return EventOverlay.Apply(expanded, Active(page.Id, utcOffset).Events)
            .Select(slot => Build(slot, views))
            .OfType<PageSection>()
            .ToList();
    }

    private ActiveEvents Active(string pageId, int? utcOffset) =>
        pageId == PageIds.Home ? _events.For(utcOffset) : new ActiveEvents([], string.Empty, null);

    private PageSection? Build(PageSlot slot, Lazy<IReadOnlyList<LibraryView>> views)
    {
        var stored = slot.Section;
        var config = new SectionConfig(stored.Config);
        var row = new SectionDisplay("row", "landscape");
        var poster = new SectionDisplay("row", "portrait");
        var feature = new SectionDisplay("feature", "landscape");
        PageSection Section(string title, SectionDisplay display, SectionSource source, SeeAllTarget? seeAll, Func<User, (IReadOnlyList<BaseItem> Items, bool Hit)> load) =>
            slot.Feature
                ? new(stored.Id, title, feature, source, seeAll, FeaturedCount, load)
                : new(stored.Id, title, display, source, seeAll, RowCount, load);

        switch (stored.Type)
        {
            case SectionTypes.Featured:
                return Section(stored.Title ?? "Featured", feature, new SectionSource(SectionSources.Featured), null, Featured);
            case SectionTypes.MyMedia:
                return Section(stored.Title ?? "My Media", row, new SectionSource(SectionSources.MyMedia), null, _ => (views.Value.Select(v => v.Item).OfType<BaseItem>().ToList(), true));
            case SectionTypes.ContinueWatching:
                return Section(
                    stored.Title ?? "Continue Watching",
                    row,
                    new SectionSource(SectionSources.ContinueWatching),
                    new SeeAllTarget(null, "Movie,Episode", "DatePlayed", "Descending", "IsResumable"),
                    u => Visible(u, _cache.ContinueWatching(u, out var hit), hit));
            case SectionTypes.NextUp:
                return Section(stored.Title ?? "Next Up", row, new SectionSource(SectionSources.NextUp), null, u => Visible(u, _cache.NextUp(u, out var hit), hit));
            case SectionTypes.Latest:
                return Latest(stored, config, views, Section, poster);
            case SectionTypes.Query:
                var query = new LibraryQuery(config.Guid("Library"), config.Strings("Genres"), config.Strings("Tags"), config.ItemTypes() ?? []);
                return Section(
                    stored.Title ?? string.Join(", ", query.Genres.Concat(query.Tags)),
                    poster,
                    new SectionSource(SectionSources.Query, query.Library?.ToString("N"), query.ItemTypes.Count > 0 ? string.Join(",", query.ItemTypes) : "Movie,Series"),
                    null,
                    u => Watched(u, config, Visible(u, _cache.Query(query, out var hit), hit)));
            case SectionTypes.Collection:
                var collection = config.Guid("Collection")!.Value;
                return Section(
                    stored.Title ?? _cache.ItemName(collection) ?? "Collection",
                    poster,
                    new SectionSource(SectionSources.Collection, collection.ToString("N")),
                    null,
                    u => Watched(u, config, Visible(u, _cache.Collection(collection, out var hit), hit)));
            default:
                return null;
        }
    }

    // A library the user cannot see has no section.
    private PageSection? Latest(
        StoredSection stored,
        SectionConfig config,
        Lazy<IReadOnlyList<LibraryView>> views,
        Func<string, SectionDisplay, SectionSource, SeeAllTarget?, Func<User, (IReadOnlyList<BaseItem> Items, bool Hit)>, PageSection> section,
        SectionDisplay poster)
    {
        var id = config.Guid("Library");
        var view = views.Value.FirstOrDefault(v => v.Id.Equals(id) && v.Type is CollectionType.movies or CollectionType.tvshows);
        if (view is null)
        {
            return null;
        }

        var shows = view.Type == CollectionType.tvshows;
        var library = view.Id.ToString("N");
        return section(
            stored.Title ?? "Latest " + view.Name,
            poster,
            new SectionSource(SectionSources.Latest, library, shows ? "Series" : "Movie"),
            new SeeAllTarget(library, shows ? "Series" : "Movie", shows ? "DateLastContentAdded" : "DateCreated", "Descending", null),
            u => Visible(u, _cache.Latest(view.Id, view.Type, out var hit), hit));
    }

    // Hide watched is off by default: seasonal favourites are rewatched.
    private (IReadOnlyList<BaseItem> Items, bool Hit) Watched(User user, SectionConfig config, (IReadOnlyList<BaseItem> Items, bool Hit) loaded) =>
        config.Flag("HideWatched") ? (_cache.Unwatched(user, loaded.Items), loaded.Hit) : loaded;

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
