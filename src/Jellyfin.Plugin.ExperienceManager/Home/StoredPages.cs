using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Audiences;
using Jellyfin.Plugin.ExperienceManager.Caching;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using Jellyfin.Plugin.ExperienceManager.Programming;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.ExperienceManager.Home;

/// <summary>
/// One section of a previewed page, and the event that changed it.
/// </summary>
/// <param name="Id">The section's id.</param>
/// <param name="Title">Its title.</param>
/// <param name="Source">What it shows: one of <see cref="SectionSources"/>.</param>
/// <param name="Display">How it is shown: <c>row</c> or <c>feature</c>.</param>
/// <param name="Event">The event that added or changed it, or null.</param>
/// <param name="Change">What that event did: one of the change kinds, or null.</param>
public sealed record PreviewSection(string Id, string Title, string Source, string Display, string? Event, string? Change);

/// <summary>
/// A section an event hid in a previewed page.
/// </summary>
/// <param name="Id">The section's id.</param>
/// <param name="Title">Its title.</param>
/// <param name="Event">The event that hid it.</param>
public sealed record PreviewHidden(string Id, string Title, string Event);

/// <summary>
/// Something the layout editor shows on a section's card.
/// </summary>
/// <param name="Id">The section's id.</param>
/// <param name="Kind"><c>event</c> or <c>source-missing</c>.</param>
/// <param name="Text">What the badge says.</param>
/// <param name="Title">The section's own title, if it has one; an event's added section is not in the layout.</param>
public sealed record SectionBadge(string Id, string Kind, string Text, string? Title = null);

/// <summary>
/// The pages stored in the plugin's database, each resolved for the user: their audience's layout or the server's,
/// Latest for each library as one section per movie and show library the user can see, in the user's library order,
/// then Home's events on now for the user's audiences laid over it.
/// </summary>
public sealed class StoredPages : IPages
{
    /// <summary>How many featured items the page shows.</summary>
    internal const int FeaturedCount = 5;

    /// <summary>How many cards a row shows before "see all".</summary>
    internal const int RowCount = 16;

    private readonly PageStore _store;
    private readonly EventTimeline _events;
    private readonly AudienceResolver? _audiences;
    private readonly HomeCache _cache;
    private readonly IHomeSources _sources;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="StoredPages"/> class.
    /// </summary>
    /// <param name="store">The stored pages.</param>
    /// <param name="events">Which of Home's events are on.</param>
    /// <param name="audiences">The user's audiences and their layouts.</param>
    /// <param name="cache">The home cache.</param>
    /// <param name="sources">The user's libraries and what they may see.</param>
    public StoredPages(PageStore store, EventTimeline events, AudienceResolver audiences, HomeCache cache, IHomeSources sources)
        : this(store, events, cache, sources, TimeProvider.System, audiences)
    {
    }

    internal StoredPages(PageStore store, EventTimeline events, HomeCache cache, IHomeSources sources, TimeProvider time, AudienceResolver? audiences = null)
    {
        _store = store;
        _events = events;
        _audiences = audiences;
        _cache = cache;
        _sources = sources;
        _time = time;
    }

    /// <inheritdoc />
    public string? LayoutVersion(string pageId, int? utcOffset = null, User? user = null)
    {
        var page = _store.Get(pageId);
        if (page is null)
        {
            return null;
        }

        var audiences = AudiencesOf(user);
        var layout = LayoutOf(page, audiences);
        var key = ForAudiences(Active(page.Id, utcOffset), audiences).Key;
        return page.LayoutVersion
            + (layout.AudienceId is null ? string.Empty : "-g" + layout.AudienceId + "." + layout.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture))
            + (key.Length == 0 ? string.Empty : "-e" + key);
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
        var audiences = AudiencesOf(user);
        return EventOverlay.Apply(Expand(LayoutOf(page, audiences).Sections, views), ForAudiences(Active(page.Id, utcOffset), audiences).Events)
            .Select(slot => Build(slot, views))
            .OfType<PageSection>()
            .ToList();
    }

    /// <summary>
    /// Resolves a page for a user with the given events on, instead of those on now, and says which event changed
    /// each section. Nothing is stored.
    /// </summary>
    /// <param name="pageId">The page id.</param>
    /// <param name="user">The user.</param>
    /// <param name="active">The events to apply, highest first; those for other audiences are left out.</param>
    /// <param name="audience">Preview as a member of this audience only, or null for the user's own audiences.</param>
    /// <param name="hidden">The sections the events hid.</param>
    /// <returns>The sections, or none if there is no such page.</returns>
    public IReadOnlyList<PreviewSection> Preview(string pageId, User user, IReadOnlyList<ProgrammedEvent> active, string? audience, out IReadOnlyList<PreviewHidden> hidden)
    {
        var page = _store.Get(pageId);
        if (page is null)
        {
            hidden = [];
            return [];
        }

        var views = new Lazy<IReadOnlyList<LibraryView>>(() => _sources.GetViews(user));
        var audiences = audience is null || audience == EventAudiences.Everyone ? AudiencesOf(user) : [audience];
        var expanded = Expand(LayoutOf(page, audiences).Sections, views);
        var titles = expanded.ToDictionary(s => s.Id, s => Build(new PageSlot(s, false), views)?.Title ?? s.Id, StringComparer.OrdinalIgnoreCase);
        var gone = new List<HiddenSection>();
        var sections = EventOverlay.Apply(expanded, active.Where(e => Applies(e, audiences)).ToList(), gone)
            .Select(slot => (Slot: slot, Section: Build(slot, views)))
            .Where(p => p.Section is not null)
            .Select(p => new PreviewSection(p.Section!.Id, p.Section.Title, p.Section.Source.Kind, p.Section.Display.Hint, p.Slot.Event, p.Slot.Change))
            .ToList();
        hidden = gone.Select(h => new PreviewHidden(h.Id, titles.GetValueOrDefault(h.Id) ?? h.Id, h.Event)).ToList();
        return sections;
    }

    /// <summary>
    /// Gets the badges for a layout's sections now: the event adding, changing or hiding each one, in the server's
    /// time zone, and Source missing where its library or collection no longer exists.
    /// </summary>
    /// <param name="pageId">The page id.</param>
    /// <param name="audience">The audience whose layout it is, or null for Everyone's.</param>
    /// <returns>The badges, or none if there is no such page.</returns>
    public IReadOnlyList<SectionBadge> Badges(string pageId, string? audience)
    {
        var page = _store.Get(pageId);
        if (page is null)
        {
            return [];
        }

        IReadOnlyList<string> audiences = audience is null || audience == EventAudiences.Everyone ? [] : [audience];
        var layout = LayoutOf(page, audiences);
        var gone = new List<HiddenSection>();
        var slots = EventOverlay.Apply(layout.Sections.Where(s => !s.Off).ToList(), ForAudiences(Active(page.Id, null), audiences).Events, gone);
        var badges = slots
            .Where(s => s.Event is not null)
            .Select(s => new SectionBadge(s.Section.Id, "event", (s.Change == ChangeKinds.Insert ? "Added by " : "Changed by ") + s.Event, s.Section.Title))
            .Concat(gone.Select(h => new SectionBadge(h.Id, "event", "Hidden by " + h.Event)))
            .ToList();
        foreach (var section in layout.Sections)
        {
            var config = new SectionConfig(section.Config);
            var source = section.Type switch
            {
                SectionTypes.Latest or SectionTypes.Query => config.Guid("Library"),
                SectionTypes.Collection => config.Guid("Collection"),
                _ => null,
            };
            if (source is { } id && !_cache.Exists(id))
            {
                badges.Add(new SectionBadge(section.Id, "source-missing", "Source missing"));
            }
        }

        return badges;
    }

    private static bool Applies(ProgrammedEvent value, IReadOnlyList<string> audiences) =>
        value.Audience == EventAudiences.Everyone || audiences.Contains(value.Audience, StringComparer.OrdinalIgnoreCase);

    // The events on now for these audiences; when all of them apply, the set and its key are shared.
    private static ActiveEvents ForAudiences(ActiveEvents active, IReadOnlyList<string> audiences)
    {
        if (active.Events.All(e => Applies(e, audiences)))
        {
            return active;
        }

        var events = active.Events.Where(e => Applies(e, audiences)).ToList();
        return new ActiveEvents(events, EventTimeline.KeyOf(events), active.Until);
    }

    private IReadOnlyList<string> AudiencesOf(User? user) => user is null || _audiences is null ? [] : _audiences.AudiencesOf(user);

    private ResolvedLayout LayoutOf(StoredPage page, IReadOnlyList<string> audiences) =>
        _audiences is null ? new ResolvedLayout(null, 0, page.Sections) : _audiences.LayoutFor(page, audiences);

    private static List<StoredSection> Expand(IReadOnlyList<StoredSection> sections, Lazy<IReadOnlyList<LibraryView>> views)
    {
        var expanded = new List<StoredSection>();
        foreach (var stored in sections.Where(s => !s.Off))
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

        return expanded;
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
