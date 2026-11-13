using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Audiences;
using Jellyfin.Plugin.ExperienceManager.Caching;
using Jellyfin.Plugin.ExperienceManager.Health;
using Jellyfin.Plugin.ExperienceManager.Home;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using Jellyfin.Plugin.ExperienceManager.Programming;
using Jellyfin.Plugin.ExperienceManager.Rules;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

public sealed class HealthTests : IDisposable
{
    private readonly User _admin = new("admin", "auth", "reset") { Id = Guid.NewGuid() };
    private readonly User _kid = new("kid", "auth", "reset") { Id = Guid.NewGuid() };
    private readonly User _teen = new("teen", "auth", "reset") { Id = Guid.NewGuid() };
    private readonly TempDatabase _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));
    private readonly PageStore _pages;
    private readonly AudienceStore _audiences;
    private readonly EventStore _events;
    private readonly RulesStore _rules;
    private readonly EventTimeline _timeline;
    private readonly HomeCache _cache;
    private readonly SectionTimings _timings = new();
    private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
    private readonly IHomeSources _sources = Substitute.For<IHomeSources>();
    private readonly IUserManager _users = Substitute.For<IUserManager>();
    private readonly Movie _grown = Film("grown", backdrop: true);
    private readonly Movie _bare = Film("bare", backdrop: true);

    public HealthTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _pages = _db.Store();
        _audiences = new AudienceStore(_db.Open());
        _events = new EventStore(_db.Open(), _audiences);
        _rules = new RulesStore(_db.Open());
        _timeline = _db.Timeline(_time, _events);
        var tv = Substitute.For<ITVSeriesManager>();
        tv.GetNextUp(Arg.Any<NextUpQuery>(), Arg.Any<DtoOptions>()).Returns(new QueryResult<BaseItem>());
        _cache = new HomeCache(_library, tv, Substitute.For<IUserDataManager>(), _time);
        _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([_grown, _bare]);
        _sources.GetViews(Arg.Any<User>()).Returns([]);
        _sources.IsVisible(Arg.Any<BaseItem>(), _admin).Returns(true);
        _users.GetUsers().Returns([_admin, _kid, _teen]);
        _audiences.Add("Kids", [_kid.Id, _teen.Id], out _);
    }

    public void Dispose()
    {
        _timeline.Dispose();
        _cache.Dispose();
        _db.Dispose();
    }

    private static Movie Film(string name, bool backdrop) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ImageInfos = backdrop
            ? [new ItemImageInfo { Type = ImageType.Primary, Path = "p" }, new ItemImageInfo { Type = ImageType.Backdrop, Path = "b" }]
            : [new ItemImageInfo { Type = ImageType.Primary, Path = "p" }],
    };

    private static StoredSection Picks(string id = "picks") => new(id, SectionTypes.Query, "Picks", new JsonObject { ["Genres"] = new JsonArray("Drama") });

    private HealthChecks Checks()
    {
        var resolver = new AudienceResolver(_audiences, _time);
        var stored = new StoredPages(_pages, _timeline, _cache, _sources, _time, resolver);
        return new HealthChecks(_pages, _audiences, resolver, _events, stored, _cache, _rules, _timings, _users, _time);
    }

    private void Layout(params StoredSection[] extra) =>
        Assert.NotNull(_pages.Replace(PageIds.Home, [.. PageStore.HomeSeed, .. extra], out _));

    [Fact]
    public void ASourceThatIsGoneIsFoundInEveryonesLayoutAnAudiencesAndAnEvent()
    {
        var gone = Guid.NewGuid();
        var missing = new StoredSection("box", SectionTypes.Collection, "Gone box", new JsonObject { ["Collection"] = gone.ToString("N") });
        Layout(missing);
        _audiences.SetLayout("kids", PageIds.Home, _pages.Get(PageIds.Home)!.Sections, [new GroupEntry("kids-box", GroupModes.Added, missing with { Id = "kids-box" })], out _);
        _events.Add(new ProgrammedEvent(string.Empty, "Box night", false, "everyone", new EventSchedule(ScheduleKinds.Yearly, "12-01", "12-02"), [new EventChange(ChangeKinds.Insert, null, missing with { Id = "box-night" }, "continue")]), out _);

        var found = Checks().Report().Findings.Where(f => f.Kind == "source-missing").Select(f => (f.Where, f.SectionId, f.EventId)).ToList();

        Assert.Equal([("Everyone's layout", "box", null), ("Kids's layout", "kids-box", null), ("event Box night", null, "box-night")], found);

        // Empty too, for every member of Kids, but Source missing says it once.
        _sources.IsVisible(Arg.Any<BaseItem>(), Arg.Any<User>()).Returns(true);
        Assert.DoesNotContain(Checks().Report().Findings, f => f.Kind.StartsWith("empty-for", StringComparison.Ordinal) && f.SectionId is "box" or "kids-box");
    }

    [Fact]
    public void ADeletedCollectionLeftOnDiskWithoutAParentIsStillMissing()
    {
        var orphan = new BoxSet { Id = Guid.NewGuid(), Name = "Deleted box" };
        var kept = new BoxSet { Id = Guid.NewGuid(), Name = "Kept box", ParentId = Guid.NewGuid() };
        _library.GetItemById(orphan.Id).Returns(orphan);
        _library.GetItemById(kept.Id).Returns(kept);
        Layout(
            new StoredSection("orphan", SectionTypes.Collection, null, new JsonObject { ["Collection"] = orphan.Id.ToString("N") }),
            new StoredSection("kept", SectionTypes.Collection, null, new JsonObject { ["Collection"] = kept.Id.ToString("N") }));

        Assert.Equal(["orphan"], Checks().Report().Findings.Where(f => f.Kind == "source-missing").Select(f => f.SectionId));
    }

    [Fact]
    public void ASectionNoMemberCanSeeIsEmptyForTheAudienceAndOneSomeCannotSeeNamesThem()
    {
        Layout(Picks());
        _sources.IsVisible(Arg.Any<BaseItem>(), _kid).Returns(false);
        _sources.IsVisible(Arg.Any<BaseItem>(), _teen).Returns(false);
        var empty = Checks().Report().Findings.Single(f => f.SectionId == "picks");
        Assert.Equal(("empty-for", "Empty for Kids", "kids"), (empty.Kind, empty.Text, empty.AudienceId));

        _sources.IsVisible(Arg.Any<BaseItem>(), _teen).Returns(true);
        var some = Checks().Report().Findings.Single(f => f.SectionId == "picks");
        Assert.Equal(("empty-for-some", "Empty for 1 of 2 in Kids: kid"), (some.Kind, some.Text));
        Assert.Equal(["kid"], some.Users);

        // Continue Watching and Next Up are personal: empty for everyone here, and never a finding.
        Assert.DoesNotContain(Checks().Report(refresh: true).Findings, f => f.SectionId is "continue" or "nextup");
    }

    [Fact]
    public void AnEventStartingWithinAWeekSaysWhatItsItemsLack()
    {
        _rules.Set(ContentRules.Defaults with { RequireLogo = true });
        _bare.ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = "p" }];
        var soon = new EventSchedule(ScheduleKinds.Once, "2026-11-16T00:00", "2026-11-20T00:00");
        _events.Add(new ProgrammedEvent(string.Empty, "Drama week", true, "everyone", soon, [new EventChange(ChangeKinds.Featured, null, Picks("drama"))]), out _);
        _events.Add(new ProgrammedEvent(string.Empty, "Later week", true, "everyone", new EventSchedule(ScheduleKinds.Once, "2026-11-21T00:00", "2026-11-25T00:00"), [new EventChange(ChangeKinds.Featured, null, Picks("later"))]), out _);
        _events.Add(new ProgrammedEvent(string.Empty, "Off week", false, "everyone", soon, [new EventChange(ChangeKinds.Featured, null, Picks("off"))]), out _);

        var soonFindings = Checks().Report().Findings.Where(f => f.Kind == "starts-soon").ToList();

        var drama = Assert.Single(soonFindings);
        Assert.Equal(("Drama week", "Starts in 3 days: 1 item lacks a backdrop; 2 items lack a logo", "drama-week"), (drama.Title, drama.Text, drama.EventId));
    }

    [Fact]
    public void AnEventWhoseSourceIsEmptySaysSo()
    {
        _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([]);
        _events.Add(new ProgrammedEvent(string.Empty, "Empty week", true, "everyone", new EventSchedule(ScheduleKinds.Once, "2026-11-14T00:00", "2026-11-15T00:00"), [new EventChange(ChangeKinds.Insert, null, Picks("empty"), "continue")]), out _);
        Assert.Equal("Starts tomorrow: Picks is empty", Checks().Report().Findings.Single(f => f.Kind == "starts-soon").Text);
    }

    [Fact]
    public void ASectionOver150MillisecondsAtP95IsSlowAndTheSlowestAreListed()
    {
        Layout(Picks());
        for (var i = 0; i < 20; i++)
        {
            _timings.Record("picks", i < 18 ? 40 : 400);
            _timings.Record("featured", 20);
        }

        _timings.Record("nextup", 900);
        var report = Checks().Report();

        Assert.Equal(("slow", "Picks", "Slow: 400 ms at p95"), report.Findings.Where(f => f.Kind == "slow").Select(f => (f.Kind, f.Title, f.Text)).Single());
        Assert.Equal([("picks", "Picks"), ("featured", "Featured")], report.Slowest.Select(s => (s.Id, s.Title)));
        Assert.Equal(400, SectionTimings.P95([.. Enumerable.Repeat(40.0, 18), 400, 400]));
        Assert.Equal(40, SectionTimings.P95([.. Enumerable.Repeat(40.0, 19), 400]));
    }

    [Fact]
    public void ChecksAreKeptForAMinuteUnlessAskedAgain()
    {
        var checks = Checks();
        var first = checks.Report();
        Layout(new StoredSection("box", SectionTypes.Collection, null, new JsonObject { ["Collection"] = Guid.NewGuid().ToString("N") }));
        Assert.Same(first, checks.Report());
        Assert.Contains(checks.Report(refresh: true).Findings, f => f.SectionId == "box");
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Contains(checks.Report().Findings, f => f.SectionId == "box");
    }

    [Fact]
    public async Task TheDailyTaskWritesEachEventProblemToTheActivityLog()
    {
        _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([]);
        _events.Add(new ProgrammedEvent(string.Empty, "Empty week", true, "everyone", new EventSchedule(ScheduleKinds.Once, "2026-11-14T00:00", "2026-11-15T00:00"), [new EventChange(ChangeKinds.Insert, null, Picks("empty"), "continue")]), out _);
        Layout(new StoredSection("box", SectionTypes.Collection, null, new JsonObject { ["Collection"] = Guid.NewGuid().ToString("N") }));
        var activity = Substitute.For<IActivityManager>();

        await new HealthTask(Checks(), activity).ExecuteAsync(new Progress<double>(), CancellationToken.None);

        await activity.Received(1).CreateAsync(Arg.Is<ActivityLog>(a => a.Name == "Experience Manager: Empty week: Starts tomorrow: Picks is empty" && a.Type == "ExperienceManagerHealth"));
        await activity.Received(1).CreateAsync(Arg.Any<ActivityLog>());
    }

    [Fact]
    public void StartsSaysTodayTomorrowOrHowManyDays()
    {
        var now = new DateTime(2026, 11, 13, 23, 0, 0);
        Assert.Equal("Starts today", HealthChecks.Starts(now, now.AddMinutes(30)));
        Assert.Equal("Starts tomorrow", HealthChecks.Starts(now, now.AddHours(2)));
        Assert.Equal("Starts in 7 days", HealthChecks.Starts(now, now.AddDays(7)));
    }
}
