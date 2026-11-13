using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Audiences;
using Jellyfin.Plugin.ExperienceManager.Caching;
using Jellyfin.Plugin.ExperienceManager.Home;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using Jellyfin.Plugin.ExperienceManager.Programming;
using Jellyfin.Plugin.ExperienceManager.Rules;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ExperienceManager.Health;

/// <summary>
/// Something wrong with Home, found before viewers meet it.
/// </summary>
/// <param name="Kind"><c>source-missing</c>, <c>empty-for</c>, <c>empty-for-some</c>, <c>starts-soon</c> or <c>slow</c>.</param>
/// <param name="Title">The section's or event's name.</param>
/// <param name="Text">What is wrong, as the badge says it.</param>
/// <param name="Where">Where it is: <c>Everyone's layout</c>, an audience's layout, or an event.</param>
/// <param name="Page">The admin page that fixes it: <c>layout</c> or <c>programming</c>.</param>
/// <param name="AudienceId">The audience whose layout it is in, or null.</param>
/// <param name="SectionId">The section, or null.</param>
/// <param name="EventId">The event, or null.</param>
/// <param name="Users">For <c>empty-for-some</c>, the members who see it empty.</param>
public sealed record HealthFinding(string Kind, string Title, string Text, string Where, string Page, string? AudienceId = null, string? SectionId = null, string? EventId = null, IReadOnlyList<string>? Users = null);

/// <summary>
/// Every finding, and the slowest sections.
/// </summary>
/// <param name="Findings">The findings.</param>
/// <param name="Slowest">The slowest sections, slowest first.</param>
/// <param name="Checked">When the checks ran.</param>
public sealed record HealthReport(IReadOnlyList<HealthFinding> Findings, IReadOnlyList<SectionSpeed> Slowest, DateTimeOffset Checked);

/// <summary>
/// Finds broken sources, sections empty for an audience, events starting soon with problems, and slow sections, on
/// the draft the admin is editing.
/// </summary>
public sealed class HealthChecks
{
    /// <summary>A section is Slow above this p95, the warm budget for the whole page call.</summary>
    public const double SlowMilliseconds = 150;

    /// <summary>How far ahead events are checked.</summary>
    public static readonly TimeSpan Ahead = TimeSpan.FromDays(7);

    private static readonly TimeSpan Kept = TimeSpan.FromSeconds(60);

    private readonly PageStore _pages;
    private readonly AudienceStore _audiences;
    private readonly AudienceResolver _resolver;
    private readonly EventStore _events;
    private readonly StoredPages _stored;
    private readonly HomeCache _cache;
    private readonly RulesStore _rules;
    private readonly SectionTimings _timings;
    private readonly IUserManager _users;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private HealthReport? _last;

    /// <summary>
    /// Initializes a new instance of the <see cref="HealthChecks"/> class.
    /// </summary>
    /// <param name="pages">The stored pages, the draft.</param>
    /// <param name="audiences">The audiences.</param>
    /// <param name="resolver">Who is in which audience.</param>
    /// <param name="events">The events, the draft.</param>
    /// <param name="stored">Resolves pages and loads sections.</param>
    /// <param name="cache">The home cache, to see whether an item exists.</param>
    /// <param name="rules">The draft content rules, for the artwork they ask for.</param>
    /// <param name="timings">The sections' load times.</param>
    /// <param name="users">Jellyfin's users.</param>
    public HealthChecks(PageStore pages, AudienceStore audiences, AudienceResolver resolver, EventStore events, StoredPages stored, HomeCache cache, RulesStore rules, SectionTimings timings, IUserManager users)
        : this(pages, audiences, resolver, events, stored, cache, rules, timings, users, TimeProvider.System)
    {
    }

    internal HealthChecks(PageStore pages, AudienceStore audiences, AudienceResolver resolver, EventStore events, StoredPages stored, HomeCache cache, RulesStore rules, SectionTimings timings, IUserManager users, TimeProvider time)
    {
        _pages = pages;
        _audiences = audiences;
        _resolver = resolver;
        _events = events;
        _stored = stored;
        _cache = cache;
        _rules = rules;
        _timings = timings;
        _users = users;
        _time = time;
    }

    /// <summary>
    /// Runs the checks, or gives the last run's when it is under a minute old.
    /// </summary>
    /// <param name="refresh">Whether to run them even so.</param>
    /// <returns>The report.</returns>
    public HealthReport Report(bool refresh = false)
    {
        lock (_lock)
        {
            if (!refresh && _last is not null && _time.GetUtcNow() - _last.Checked < Kept)
            {
                return _last;
            }

            var findings = new List<HealthFinding>();
            findings.AddRange(MissingSources());
            // A section whose source is gone is empty for everyone: Source missing says it once.
            var gone = findings.Select(f => f.SectionId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            findings.AddRange(EmptyForAudiences().Where(f => !gone.Contains(f.SectionId ?? string.Empty)));
            findings.AddRange(StartingSoon());
            var speeds = _timings.Speeds().Select(s => s with { Title = TitleOf(s.Id) }).ToList();
            findings.AddRange(speeds.Where(s => s.P95 > SlowMilliseconds).Select(s =>
                new HealthFinding("slow", TitleOf(s.Id), string.Create(CultureInfo.InvariantCulture, $"Slow: {s.P95:0} ms at p95"), "Home", "layout", SectionId: s.Id)));
            _last = new HealthReport(findings, speeds.Take(10).ToList(), _time.GetUtcNow());
            return _last;
        }
    }

    private static string TitleOf(StoredSection section) => section.Title ?? section.Type switch
    {
        SectionTypes.Featured => "Featured",
        SectionTypes.MyMedia => "My Media",
        SectionTypes.ContinueWatching => "Continue Watching",
        SectionTypes.NextUp => "Next Up",
        SectionTypes.LatestLibraries => "Latest for each library",
        SectionTypes.Announcement => "Announcement",
        SectionTypes.TopFilms => "Top 10 Films",
        SectionTypes.TopShows => "Top 10 Shows",
        SectionTypes.FinishCollections => "Finish These Collections",
        _ => section.Id,
    };

    private string TitleOf(string sectionId)
    {
        var section = _pages.Get(PageIds.Home)?.Sections.FirstOrDefault(s => string.Equals(s.Id, sectionId, StringComparison.OrdinalIgnoreCase));
        if (section is not null)
        {
            return TitleOf(section);
        }

        return sectionId.StartsWith("latest-", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(sectionId[7..], out var library)
            ? "Latest " + (_cache.ItemName(library) ?? "library")
            : sectionId;
    }

    private string AudienceName(string id) => _audiences.Get(id)?.Name ?? id;

    private bool Missing(StoredSection section) => StoredPages.SourceIds(section).Any(id => !_cache.Exists(id));

    private IEnumerable<HealthFinding> MissingSources()
    {
        var everyone = _pages.Get(PageIds.Home)?.Sections ?? [];
        foreach (var section in everyone.Where(Missing))
        {
            yield return new HealthFinding("source-missing", TitleOf(section), "Source missing", "Everyone's layout", "layout", SectionId: section.Id);
        }

        foreach (var audience in _audiences.All)
        {
            var own = _audiences.Layout(audience.Id, PageIds.Home)?.Entries.Select(e => e.Section).OfType<StoredSection>() ?? [];
            foreach (var section in own.Where(Missing))
            {
                yield return new HealthFinding("source-missing", TitleOf(section), "Source missing", audience.Name + "'s layout", "layout", audience.Id, section.Id);
            }
        }

        foreach (var ev in _events.All)
        {
            foreach (var section in ev.Changes.Select(c => c.Section).OfType<StoredSection>().Where(Missing))
            {
                yield return new HealthFinding("source-missing", ev.Name, TitleOf(section) + ": source missing", "event " + ev.Name, "programming", EventId: ev.Id);
            }
        }
    }

    // A member's Home as their audience gets it, on the draft: which sections they would see empty. Continue
    // Watching, Next Up and Finish these collections are personal and often empty, so they are left out.
    private IEnumerable<HealthFinding> EmptyForAudiences()
    {
        var users = _users.GetUsers().ToList();
        foreach (var audience in _audiences.All)
        {
            var members = users.Where(u => _resolver.AudiencesOf(u).Contains(audience.Id, StringComparer.OrdinalIgnoreCase)).ToList();
            if (members.Count == 0)
            {
                continue;
            }

            var emptyFor = new Dictionary<string, (string Title, List<string> Users)>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var member in members)
            {
                foreach (var section in _stored.Preview(PageIds.Home, member, [], audience.Id, 1).Sections)
                {
                    if (section.Items is null || section.Source is SectionSources.ContinueWatching or SectionSources.NextUp or SectionSources.Finish)
                    {
                        continue;
                    }

                    if (!emptyFor.TryGetValue(section.Id, out var entry))
                    {
                        entry = (section.Title, []);
                        emptyFor[section.Id] = entry;
                        order.Add(section.Id);
                    }

                    if (section.Items.Count == 0)
                    {
                        entry.Users.Add(member.Username);
                    }
                }
            }

            foreach (var id in order)
            {
                var (title, empty) = emptyFor[id];
                if (empty.Count == members.Count)
                {
                    yield return new HealthFinding("empty-for", title, "Empty for " + audience.Name, audience.Name + "'s layout", "layout", audience.Id, id);
                }
                else if (empty.Count > 0)
                {
                    yield return new HealthFinding(
                        "empty-for-some",
                        title,
                        string.Create(CultureInfo.InvariantCulture, $"Empty for {empty.Count} of {members.Count} in {audience.Name}: {string.Join(", ", empty)}"),
                        audience.Name + "'s layout",
                        "layout",
                        audience.Id,
                        id,
                        Users: empty);
                }
            }
        }
    }

    // Events switched on whose next start is within a week: a gone or empty source, or items without the artwork
    // the content rules ask for.
    private IEnumerable<HealthFinding> StartingSoon()
    {
        var now = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone).DateTime;
        var rules = _rules.Rules;
        foreach (var ev in _events.All.Where(e => e.Published))
        {
            if (Schedules.IsActive(ev.Schedule, now))
            {
                continue;
            }

            var start = Schedules.Boundaries(ev.Schedule, now, now + Ahead).Select(t => (DateTime?)t).FirstOrDefault(t => Schedules.IsActive(ev.Schedule, t!.Value));
            if (start is not { } at)
            {
                continue;
            }

            var viewer = ViewerFor(ev);
            var problems = new List<string>();
            foreach (var change in ev.Changes.Where(c => c.Section is not null))
            {
                var feature = change.Kind == ChangeKinds.Featured;
                var section = feature ? change.Section! with { Id = change.Target ?? "featured" } : change.Section!;
                var title = feature ? "Featured" : TitleOf(section);
                if (Missing(section))
                {
                    problems.Add(title + "'s source is gone");
                    continue;
                }

                var items = viewer is null || section.Type == SectionTypes.Announcement ? null : _stored.RawItems(section, feature, viewer);
                if (items is not null && items.Count == 0)
                {
                    problems.Add(title + " is empty");
                    continue;
                }

                if (items is null)
                {
                    continue;
                }

                if (feature && rules.FeatureArt)
                {
                    var shown = items.Take(StoredPages.FeaturedCount).ToList();
                    Count(problems, shown.Count(i => !i.HasImage(ImageType.Backdrop, 0)), "a backdrop");
                    if (rules.RequireLogo)
                    {
                        Count(problems, shown.Count(i => !i.HasImage(ImageType.Logo, 0)), "a logo");
                    }
                }
                else if (!feature && rules.Poster)
                {
                    Count(problems, items.Take(StoredPages.RowCount).Count(i => i is Movie or Series or BoxSet && !i.HasImage(ImageType.Primary, 0)), "a poster");
                }
            }

            if (problems.Count > 0)
            {
                yield return new HealthFinding("starts-soon", ev.Name, Starts(now, at) + ": " + string.Join("; ", problems), "event " + ev.Name, "programming", EventId: ev.Id);
            }
        }
    }

    private static void Count(List<string> problems, int count, string what)
    {
        if (count > 0)
        {
            problems.Add(count == 1 ? "1 item lacks " + what : string.Create(CultureInfo.InvariantCulture, $"{count} items lack {what}"));
        }
    }

    internal static string Starts(DateTime now, DateTime start) => (start.Date - now.Date).Days switch
    {
        0 => "Starts today",
        1 => "Starts tomorrow",
        var days => string.Create(CultureInfo.InvariantCulture, $"Starts in {days} days"),
    };

    // Who the event's sections are loaded for: a member of its audience, else an administrator, who sees every library.
    private User? ViewerFor(ProgrammedEvent ev)
    {
        var users = _users.GetUsers().ToList();
        if (ev.Audience != EventAudiences.Everyone && users.FirstOrDefault(u => _resolver.AudiencesOf(u).Contains(ev.Audience, StringComparer.OrdinalIgnoreCase)) is { } member)
        {
            return member;
        }

        return users.FirstOrDefault(u => u.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator)) ?? users.FirstOrDefault();
    }
}
