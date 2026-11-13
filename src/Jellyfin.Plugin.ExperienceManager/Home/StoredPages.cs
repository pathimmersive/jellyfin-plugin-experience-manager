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
using Jellyfin.Plugin.ExperienceManager.Rules;
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
/// <param name="Items">Its first items, when the preview asked for them.</param>
/// <param name="Notes">What each content rule left out of it.</param>
/// <param name="Announcement">For an announcement, what it says.</param>
public sealed record PreviewSection(string Id, string Title, string Source, string Display, string? Event, string? Change, IReadOnlyList<PreviewItem>? Items = null, IReadOnlyList<string>? Notes = null, Announcements.Announcement? Announcement = null);

/// <summary>
/// An item in a previewed section, enough to draw its card.
/// </summary>
/// <param name="Id">The item's id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Year">Its production year, if known.</param>
/// <param name="HasPrimary">Whether it has a poster.</param>
/// <param name="HasBackdrop">Whether it has a backdrop.</param>
public sealed record PreviewItem(Guid Id, string Name, int? Year, bool HasPrimary, bool HasBackdrop);

/// <summary>
/// A previewed page and why it looks that way.
/// </summary>
/// <param name="Sections">The sections, in order.</param>
/// <param name="Hidden">The sections events hid.</param>
/// <param name="Audiences">The audiences the viewer is in, highest first.</param>
/// <param name="LayoutAudience">The audience whose layout the viewer gets, or null for Everyone's.</param>
public sealed record PreviewPage(IReadOnlyList<PreviewSection> Sections, IReadOnlyList<PreviewHidden> Hidden, IReadOnlyList<string> Audiences, string? LayoutAudience);

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

    /// <summary>How many a Top 10 shows.</summary>
    internal const int TopCount = 10;

    private readonly PageStore _store;
    private readonly EventTimeline _events;
    private readonly AudienceResolver? _audiences;
    private readonly RuleFilter? _rules;
    private readonly Publishing.PublishStore? _published;
    private readonly HomeCache _cache;
    private readonly IHomeSources _sources;
    private readonly TimeProvider _time;
    private readonly Announcements.DismissalStore? _dismissals;
    private readonly PlayedRows? _played;

    /// <summary>
    /// Initializes a new instance of the <see cref="StoredPages"/> class.
    /// </summary>
    /// <param name="store">The stored pages.</param>
    /// <param name="events">Which of Home's events are on.</param>
    /// <param name="audiences">The user's audiences and their layouts.</param>
    /// <param name="rules">The content rules.</param>
    /// <param name="published">The published version, which Home serves; previews and badges read the draft.</param>
    /// <param name="cache">The home cache.</param>
    /// <param name="sources">The user's libraries and what they may see.</param>
    /// <param name="dismissals">The announcements each user dismissed.</param>
    /// <param name="played">Top 10 and Finish these collections, from what people play.</param>
    public StoredPages(PageStore store, EventTimeline events, AudienceResolver audiences, RuleFilter rules, Publishing.PublishStore published, HomeCache cache, IHomeSources sources, Announcements.DismissalStore dismissals, PlayedRows played)
        : this(store, events, cache, sources, TimeProvider.System, audiences, rules, published, dismissals, played)
    {
    }

    internal StoredPages(PageStore store, EventTimeline events, HomeCache cache, IHomeSources sources, TimeProvider time, AudienceResolver? audiences = null, RuleFilter? rules = null, Publishing.PublishStore? published = null, Announcements.DismissalStore? dismissals = null, PlayedRows? played = null)
    {
        _dismissals = dismissals;
        _played = played;
        _published = published;
        _store = store;
        _events = events;
        _audiences = audiences;
        _rules = rules;
        _cache = cache;
        _sources = sources;
        _time = time;
    }

    /// <inheritdoc />
    public string? LayoutVersion(string pageId, int? utcOffset = null, User? user = null)
    {
        var page = Live(pageId);
        if (page is null)
        {
            return null;
        }

        var audiences = AudiencesOf(user);
        var layout = LayoutOf(page, audiences, live: true);
        var key = ForAudiences(Active(page.Id, utcOffset), audiences).Key;
        return (_published is null ? page.LayoutVersion : "p" + _published.ContentKey)
            + (layout.AudienceId is null ? string.Empty : "-g" + layout.AudienceId + "." + layout.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture))
            + (_published is null && _rules is { Revision: > 1 } ? "-c" + _rules.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty)
            + (key.Length == 0 ? string.Empty : "-e" + key)
            + (user is null || _dismissals?.Key(user.Id) is not { Length: > 0 } dismissed ? string.Empty : "-d" + dismissed);
    }

    /// <inheritdoc />
    public IReadOnlyList<PageSection> SectionsFor(string pageId, User user, int? utcOffset = null)
    {
        var page = Live(pageId);
        if (page is null)
        {
            return [];
        }

        var views = new Lazy<IReadOnlyList<LibraryView>>(() => _sources.GetViews(user));
        var audiences = AudiencesOf(user);
        return EventOverlay.Apply(Expand(LayoutOf(page, audiences, live: true).Sections, views), ForAudiences(Active(page.Id, utcOffset), audiences).Events)
            .Select(slot => Build(slot, views, viewer: user, dismissals: true))
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
        var preview = Preview(pageId, user, active, audience, 0);
        hidden = preview.Hidden;
        return preview.Sections;
    }

    /// <summary>
    /// Resolves a page for a viewer on the draft with the given events, and says why it looks that way: the viewer's
    /// audiences, whose layout they get, the event behind each section, what each content rule left out, and, when
    /// asked, each section's first items. Nothing is stored.
    /// </summary>
    /// <param name="pageId">The page id.</param>
    /// <param name="user">The viewer: their libraries, parental controls and audiences count.</param>
    /// <param name="active">The events to apply, highest first; those for other audiences are left out.</param>
    /// <param name="audience">Preview as a member of this audience only, as no audience's member for 'everyone', or null for the viewer's own audiences.</param>
    /// <param name="items">How many items to give each section; none when 0.</param>
    /// <returns>The page, or an empty one if there is no such page.</returns>
    public PreviewPage Preview(string pageId, User user, IReadOnlyList<ProgrammedEvent> active, string? audience, int items)
    {
        var page = _store.Get(pageId);
        if (page is null)
        {
            return new PreviewPage([], [], [], null);
        }

        var views = new Lazy<IReadOnlyList<LibraryView>>(() => _sources.GetViews(user));
        IReadOnlyList<string> audiences = audience is null ? AudiencesOf(user) : audience == EventAudiences.Everyone ? [] : [audience];
        var layout = LayoutOf(page, audiences);
        var expanded = Expand(layout.Sections, views);
        var titles = expanded.ToDictionary(s => s.Id, s => Build(new PageSlot(s, false), views)?.Title ?? s.Id, StringComparer.OrdinalIgnoreCase);
        var gone = new List<HiddenSection>();
        var raws = new Dictionary<string, (RuleTarget Target, Func<User, (IReadOnlyList<BaseItem> Items, bool Hit)> Load)>(StringComparer.OrdinalIgnoreCase);
        var sections = EventOverlay.Apply(expanded, active.Where(e => Applies(e, audiences)).ToList(), gone)
            .Select(slot => (Slot: slot, Section: Build(slot, views, raws, user)))
            .Where(p => p.Section is not null)
            .Select(p => Describe(p.Section!, p.Slot, raws.GetValueOrDefault(p.Section!.Id), user, audiences, items))
            .ToList();
        var hidden = gone.Select(h => new PreviewHidden(h.Id, titles.GetValueOrDefault(h.Id) ?? h.Id, h.Event)).ToList();
        return new PreviewPage(sections, hidden, audiences, layout.AudienceId);
    }

    private PreviewSection Describe(PageSection section, PageSlot slot, (RuleTarget Target, Func<User, (IReadOnlyList<BaseItem> Items, bool Hit)> Load) raw, User user, IReadOnlyList<string> audiences, int count)
    {
        var notes = new List<string>();
        List<PreviewItem>? items = null;
        if (count > 0 && raw.Load is not null)
        {
            var loaded = raw.Load(user).Items;
            var kept = _rules is null ? loaded : _rules.Apply(raw.Target, user, audiences, loaded, notes, draft: true);
            items = kept.Take(slot.Feature ? FeaturedCount : count)
                .Select(i => new PreviewItem(i.Id, i.Name, i.ProductionYear, i.HasImage(MediaBrowser.Model.Entities.ImageType.Primary, 0), i.HasImage(MediaBrowser.Model.Entities.ImageType.Backdrop, 0)))
                .ToList();
        }

        return new PreviewSection(section.Id, section.Title, section.Source.Kind, section.Display.Hint, slot.Event, slot.Change, items, notes, section.Announcement);
    }

    /// <summary>
    /// Gets the badges for a layout's sections now: the event adding, changing or hiding each one, in the server's
    /// time zone, and Source missing where its library or collection no longer exists.
    /// </summary>
    /// <param name="pageId">The page id.</param>
    /// <param name="audience">The audience whose layout it is, or null for Everyone's.</param>
    /// <param name="active">The events on now; the published ones when left out.</param>
    /// <returns>The badges, or none if there is no such page.</returns>
    public IReadOnlyList<SectionBadge> Badges(string pageId, string? audience, IReadOnlyList<ProgrammedEvent>? active = null)
    {
        var page = _store.Get(pageId);
        if (page is null)
        {
            return [];
        }

        IReadOnlyList<string> audiences = audience is null || audience == EventAudiences.Everyone ? [] : [audience];
        var layout = LayoutOf(page, audiences);
        var gone = new List<HiddenSection>();
        var slots = EventOverlay.Apply(layout.Sections.Where(s => !s.Off).ToList(), ForAudiences(active is null ? Active(page.Id, null) : new ActiveEvents(active, string.Empty, null), audiences).Events, gone);
        var badges = slots
            .Where(s => s.Event is not null)
            .Select(s => new SectionBadge(s.Section.Id, "event", (s.Change == ChangeKinds.Insert ? "Added by " : "Changed by ") + s.Event, s.Section.Title))
            .Concat(gone.Select(h => new SectionBadge(h.Id, "event", "Hidden by " + h.Event)))
            .ToList();
        foreach (var section in layout.Sections)
        {
            if (SourceIds(section).Any(id => !_cache.Exists(id)))
            {
                badges.Add(new SectionBadge(section.Id, "source-missing", "Source missing"));
            }
        }

        return badges;
    }

    /// <summary>
    /// Gets what a section shows or opens from the server: its library, its collection, or its button's target.
    /// </summary>
    /// <param name="section">The section.</param>
    /// <returns>The ids, none for a section that needs nothing on the server.</returns>
    public static IEnumerable<Guid> SourceIds(StoredSection section)
    {
        var config = new SectionConfig(section.Config);
        var id = section.Type switch
        {
            SectionTypes.Latest or SectionTypes.Query => config.Guid("Library"),
            SectionTypes.Collection => config.Guid("Collection"),
            SectionTypes.Announcement => config.Guid("ButtonTarget"),
            _ => null,
        };
        return id is { } found ? [found] : [];
    }

    /// <summary>
    /// Loads a section's items for a user before the content rules, such as an event's section ahead of its start.
    /// </summary>
    /// <param name="section">The section.</param>
    /// <param name="feature">Whether it is shown as the featured banner.</param>
    /// <param name="user">The user.</param>
    /// <returns>The items, or null when the user cannot see its source.</returns>
    public IReadOnlyList<BaseItem>? RawItems(StoredSection section, bool feature, User user)
    {
        var views = new Lazy<IReadOnlyList<LibraryView>>(() => _sources.GetViews(user));
        var raws = new Dictionary<string, (RuleTarget Target, Func<User, (IReadOnlyList<BaseItem> Items, bool Hit)> Load)>(StringComparer.OrdinalIgnoreCase);
        if (Build(new PageSlot(section, feature), views, raws, user) is null)
        {
            return null;
        }

        return raws.TryGetValue(section.Id, out var raw) ? raw.Load(user).Items : [];
    }

    // The content rules run on the items the section loaded, for the viewer's audiences.
    private Func<User, (IReadOnlyList<BaseItem> Items, bool Hit)> Ruled(RuleTarget target, Func<User, (IReadOnlyList<BaseItem> Items, bool Hit)> load) =>
        _rules is null
            ? load
            : u =>
            {
                var (items, hit) = load(u);
                return (_rules.Apply(target, u, AudiencesOf(u), items), hit);
            };

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

    private ResolvedLayout LayoutOf(StoredPage page, IReadOnlyList<string> audiences, bool live = false) =>
        _audiences is null
            ? new ResolvedLayout(null, 0, page.Sections)
            : _audiences.LayoutFor(page, audiences, live && _published is not null ? _published.LiveLayout : null);

    // What Home serves: the published version once publishing is on, else the stored page.
    private StoredPage? Live(string pageId) => _published is null ? _store.Get(pageId) : _published.LivePage(pageId);

    // Each library's Latest keeps the instance's opt-outs from the content rules.
    private static JsonObject LibraryConfig(Guid library, JsonObject? instance)
    {
        var config = new JsonObject { ["Library"] = library.ToString("N") };
        var off = new SectionConfig(instance).Strings("RulesOff");
        if (off.Count > 0)
        {
            config["RulesOff"] = new JsonArray(off.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
        }

        return config;
    }

    private static List<StoredSection> Expand(IReadOnlyList<StoredSection> sections, Lazy<IReadOnlyList<LibraryView>> views)
    {
        var expanded = new List<StoredSection>();
        foreach (var stored in sections.Where(s => !s.Off))
        {
            if (stored.Type == SectionTypes.LatestLibraries)
            {
                expanded.AddRange(views.Value
                    .Where(v => v.Type is CollectionType.movies or CollectionType.tvshows)
                    .Select(v => new StoredSection("latest-" + v.Id.ToString("N"), SectionTypes.Latest, null, LibraryConfig(v.Id, stored.Config))));
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

    // viewer: whose button targets count; dismissals: leave out what the viewer dismissed, as Home does and previews do not.
    private PageSection? Build(PageSlot slot, Lazy<IReadOnlyList<LibraryView>> views, IDictionary<string, (RuleTarget Target, Func<User, (IReadOnlyList<BaseItem> Items, bool Hit)> Load)>? raws = null, User? viewer = null, bool dismissals = false)
    {
        var stored = slot.Section;
        var config = new SectionConfig(stored.Config);
        var row = new SectionDisplay("row", "landscape");
        var poster = new SectionDisplay("row", "portrait");
        var feature = new SectionDisplay("feature", "landscape");
        PageSection Section(string title, SectionDisplay display, SectionSource source, SeeAllTarget? seeAll, Func<User, (IReadOnlyList<BaseItem> Items, bool Hit)> load)
        {
            var target = new RuleTarget(stored.Type, slot.Feature, config.Strings("RulesOff"));
            if (raws is not null)
            {
                raws[stored.Id] = (target, load);
            }

            var ruled = Ruled(target, load);
            return slot.Feature
                ? new(stored.Id, title, feature, source, seeAll, FeaturedCount, ruled)
                : new(stored.Id, title, display, source, seeAll, RowCount, ruled);
        }

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
            case SectionTypes.TopFilms when _played is not null:
                return Ranked(Section(stored.Title ?? "Top 10 Films", poster, new SectionSource(SectionSources.Top10, null, "Movie"), null, u => Visible(u, _played.TopFilms(out var hit), hit)));
            case SectionTypes.TopShows when _played is not null:
                return Ranked(Section(stored.Title ?? "Top 10 Shows", poster, new SectionSource(SectionSources.Top10, null, "Series"), null, u => Visible(u, _played.TopShows(out var hit), hit)));
            case SectionTypes.FinishCollections when _played is not null:
                return Section(stored.Title ?? "Finish These Collections", poster, new SectionSource(SectionSources.Finish), null, u => Visible(u, _played.FinishCollections(u, out var hit), hit))
                    with { Chips = _played.FinishChips };
            case SectionTypes.Announcement:
                var content = Announcements.AnnouncementContent.Of(stored);
                if (dismissals && viewer is not null && content.Dismissible && _dismissals?.IsDismissed(viewer.Id, stored.Id, content.Version) == true)
                {
                    return null;
                }

                return new PageSection(stored.Id, content.Title ?? string.Empty, row, new SectionSource(SectionSources.Section), null, 0, _ => ([], true), Announce(content, viewer, views));
            default:
                return null;
        }
    }

    // A Top 10: the viewer's first ten, after what they may see and the content rules, numbered.
    private static PageSection Ranked(PageSection section)
    {
        var load = section.Load;
        return section with { Rank = true, FirstPage = Math.Min(section.FirstPage, TopCount), Load = u => load(u) is var (items, hit) ? (items.Take(TopCount).ToList(), hit) : default };
    }

    // What an announcement says to a viewer: its button only when they can open what it opens.
    private Announcements.Announcement Announce(Announcements.AnnouncementContent content, User? viewer, Lazy<IReadOnlyList<LibraryView>> views)
    {
        Announcements.AnnouncementButton? button = null;
        if (content.ButtonTarget is { } target && content.ButtonLabel is { } label)
        {
            var view = viewer is null ? null : views.Value.FirstOrDefault(v => v.Id.Equals(target));
            if (view is not null)
            {
                button = new(label, target.ToString("N"), "library", view.Type?.ToString());
            }
            else if (_cache.Item(target) is { } item && item is not ICollectionFolder && (viewer is null || _sources.IsVisible(item, viewer)))
            {
                button = new(label, target.ToString("N"), item is MediaBrowser.Controller.Entities.Movies.BoxSet ? "collection" : "item", null);
            }
        }

        var image = content.ImageId is null ? null : "/ExperienceManager/v1/Images/" + content.ImageId;
        return new Announcements.Announcement(content.Lines, image, button, content.Dismissible, content.Version);
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
