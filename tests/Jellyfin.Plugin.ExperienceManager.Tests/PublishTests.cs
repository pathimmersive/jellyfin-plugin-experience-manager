using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Audiences;
using Jellyfin.Plugin.ExperienceManager.Caching;
using Jellyfin.Plugin.ExperienceManager.Controllers;
using Jellyfin.Plugin.ExperienceManager.Home;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using Jellyfin.Plugin.ExperienceManager.Programming;
using Jellyfin.Plugin.ExperienceManager.Publishing;
using Jellyfin.Plugin.ExperienceManager.Rules;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

public sealed class PublishTests : IDisposable
{
    private static readonly User Kid = new("kid", "auth", "reset") { Id = Guid.NewGuid() };
    private static readonly User Admin = new("admin", "auth", "reset") { Id = Guid.NewGuid() };

    private readonly TempDatabase _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 12, 19, 23, 0, 0, TimeSpan.Zero));
    private readonly PageStore _pages;
    private readonly AudienceStore _audiences;
    private readonly EventStore _events;
    private readonly RulesStore _rules;
    private readonly PublishStore _publish;
    private readonly EventTimeline _timeline;
    private readonly HomeCache _cache;
    private readonly IHomeSources _sources = Substitute.For<IHomeSources>();

    public PublishTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _pages = _db.Store();
        _audiences = new AudienceStore(_db.Open());
        _events = new EventStore(_db.Open(), _audiences);
        _rules = new RulesStore(_db.Open());
        _publish = new PublishStore(_db.Open(), _pages, _audiences, _events, _rules, _time);
        _timeline = new EventTimeline(() => _publish.Live.Events, _time);
        _publish.Published += (_, _) => _timeline.For(null);
        var tv = Substitute.For<ITVSeriesManager>();
        tv.GetNextUp(Arg.Any<NextUpQuery>(), Arg.Any<DtoOptions>()).Returns(new QueryResult<BaseItem>());
        _cache = new HomeCache(Substitute.For<ILibraryManager>(), tv, Substitute.For<IUserDataManager>(), _time);
        _sources.GetViews(Arg.Any<User>()).Returns([]);
    }

    public void Dispose()
    {
        _timeline.Dispose();
        _cache.Dispose();
        _db.Dispose();
    }

    private StoredPages Pages() =>
        new(_pages, new EventTimeline(() => _publish.Live.Events, _time), _cache, _sources, _time, new AudienceResolver(_audiences, _time), null, _publish);

    private string Home(User user) => string.Join(",", Pages().SectionsFor(PageIds.Home, user, 0).Select(s => s.Id));

    private static readonly EventSchedule Holidays = new(ScheduleKinds.Yearly, "12-20", "01-02");

    [Fact]
    public void AServerWithoutVersionsStartsAtVersionOneFromWhatItHadAndHasNoDraft()
    {
        Assert.Equal(1, _publish.Version);
        Assert.False(_publish.HasDraft);
        Assert.Empty(_publish.Changes());
        Assert.Equal(PageStore.HomeSeed, _publish.Live.Sections);
        Assert.Equal(1, new PublishStore(_db.Open(), _pages, _audiences, _events, _rules, _time).Version);
    }

    [Fact]
    public void EditsStayInTheDraftUntilPublishThenHomeServesThemWithANewLayoutVersion()
    {
        // The next November, before Christmas's dates, so publishing it adds nothing yet.
        _time.SetUtcNow(new DateTimeOffset(2027, 11, 10, 12, 0, 0, TimeSpan.Zero));
        var before = (Home(Admin), Pages().LayoutVersion(PageIds.Home, 0, Admin));

        _pages.Replace(PageIds.Home, PageStore.HomeSeed.Where(s => s.Id != "mymedia").ToList(), out _);
        _audiences.Add("Kids", [Kid.Id], out _);
        _audiences.SetLayout("kids", PageIds.Home, _pages.Get(PageIds.Home)!.Sections, [new("nextup", GroupModes.Removed)], out _);
        _events.SetPublished("christmas", true);
        _rules.Set(ContentRules.Defaults with { RequireLogo = true });

        Assert.True(_publish.HasDraft);
        Assert.Equal(["1 change in Everyone's Home", "Kids's Home changed", "Published event: Christmas", "Content rules changed"], _publish.Changes());
        Assert.Equal(before, (Home(Admin), Pages().LayoutVersion(PageIds.Home, 0, Admin)));
        Assert.Contains("mymedia", Home(Kid));
        Assert.False(_publish.Live.Rules.RequireLogo);

        var version = _publish.Publish("admin")!;

        Assert.Equal((2, "admin"), (version.Id, version.Author));
        Assert.Contains("Published event: Christmas", version.Summary, StringComparison.Ordinal);
        Assert.False(_publish.HasDraft);
        Assert.Equal("featured,continue,nextup", Home(Admin));
        Assert.Equal("featured,continue", Home(Kid));
        Assert.NotEqual(before.Item2, Pages().LayoutVersion(PageIds.Home, 0, Admin));
        Assert.StartsWith("p", Pages().LayoutVersion(PageIds.Home, 0, Admin), StringComparison.Ordinal);
        Assert.True(_publish.Live.Rules.RequireLogo);
        Assert.Null(_publish.Publish("admin"));
    }

    [Fact]
    public void AnEventsPublishSwitchAppliesOnlyOnceTheDraftIsPublished()
    {
        _events.Update(_events.Get("christmas")! with { Schedule = Holidays }, out _);
        _events.SetPublished("christmas", true);
        _time.SetUtcNow(new DateTimeOffset(2026, 12, 24, 12, 0, 0, TimeSpan.Zero));

        Assert.DoesNotContain("christmas-classics", Home(Admin));
        _publish.Publish("admin");
        Assert.Contains("christmas-classics", Home(Admin));
    }

    [Fact]
    public void PublishingAnEventThatIsNotOnLeavesHomesLayoutVersionAlone()
    {
        var before = Pages().LayoutVersion(PageIds.Home, 0, Admin);
        _events.Update(_events.Get("christmas")! with { Schedule = Holidays }, out _);
        _events.SetPublished("christmas", true);
        _publish.Publish("admin");

        Assert.Equal(before, Pages().LayoutVersion(PageIds.Home, 0, Admin));

        _time.Advance(TimeSpan.FromHours(2));
        Assert.NotEqual(before, Pages().LayoutVersion(PageIds.Home, 0, Admin));
    }

    [Fact]
    public void UndoStepsTheDraftBackOneChangeAndDiscardDropsItWithoutTouchingWhatIsPublished()
    {
        _pages.Replace(PageIds.Home, PageStore.HomeSeed.Where(s => s.Id != "mymedia").ToList(), out _);
        _rules.Set(ContentRules.Defaults with { Poster = false });
        _audiences.Add("Kids", [Kid.Id], out _);

        Assert.True(_publish.CanUndo);
        Assert.True(_publish.Undo());
        Assert.True(_rules.Rules.Poster);
        Assert.Equal(["1 change in Everyone's Home"], _publish.Changes());
        Assert.NotNull(_audiences.Get("kids"));

        _publish.Discard();
        Assert.False(_publish.HasDraft);
        Assert.False(_publish.CanUndo);
        Assert.Equal(PageStore.HomeSeed, _pages.Get(PageIds.Home)!.Sections);
        Assert.Equal(1, _publish.Version);
    }

    [Fact]
    public void RestoreMakesAPublishedVersionTheDraftToPublishAgain()
    {
        _pages.Replace(PageIds.Home, PageStore.HomeSeed.Where(s => s.Id != "mymedia").ToList(), out _);
        _events.Add(new ProgrammedEvent(string.Empty, "Movie night", true, EventAudiences.Everyone, Holidays, [new EventChange(ChangeKinds.Hide, "nextup")]), out _);
        _publish.Publish("admin");

        Assert.True(_publish.Restore(1));
        Assert.Equal(PageStore.HomeSeed, _pages.Get(PageIds.Home)!.Sections);
        Assert.Null(_events.Get("movie-night"));
        Assert.Equal("featured,continue,nextup", Home(Admin));

        var edited = Pages().LayoutVersion(PageIds.Home, 0, Admin);
        _publish.Publish("admin");
        Assert.Equal(3, _publish.Version);
        Assert.Equal("featured,mymedia,continue,nextup", Home(Admin));
        var restored = Pages().LayoutVersion(PageIds.Home, 0, Admin);
        Assert.NotEqual(edited, restored);

        // The same content published again gives clients the same layout version.
        _publish.Restore(2);
        _publish.Publish("admin");
        Assert.Equal(edited, Pages().LayoutVersion(PageIds.Home, 0, Admin));
        Assert.False(_publish.Restore(99));
    }

    [Fact]
    public void HistoryKeepsTheLastHundredVersionsNewestFirst()
    {
        for (var i = 0; i < 101; i++)
        {
            _rules.Set(ContentRules.Defaults with { CleanupDays = i + 1, ContinueCleanup = true });
            _publish.Publish("admin");
        }

        var versions = _publish.Versions();
        Assert.Equal(PublishStore.Keep, versions.Count);
        Assert.Equal((102, 3), (versions[0].Id, versions[^1].Id));
    }

    [Fact]
    public void AudiencesAndTheirMembersChangeAtOnceOutsideTheDraft()
    {
        _audiences.Add("Kids", [Kid.Id], out _);
        _audiences.Update("new-users", null, null, 30, out _);

        Assert.False(_publish.HasDraft);
        Assert.Equal(["new-users", "kids"], new AudienceResolver(_audiences, _time) is { } r ? _audiences.All.Select(a => a.Id) : []);
    }

    [Fact]
    public async System.Threading.Tasks.Task AnEventStartingOrEndingIsWrittenToHistoryAndToJellyfinsActivityLog()
    {
        var activity = Substitute.For<IActivityManager>();
        var log = new HistoryLog(_timeline, _publish, activity, NullLogger<HistoryLog>.Instance, _time);
        await log.StartAsync(default);
        _events.Update(_events.Get("christmas")! with { Schedule = Holidays }, out _);
        _events.SetPublished("christmas", true);
        _publish.Publish("admin");
        Assert.Empty(_publish.Entries());

        _time.Advance(TimeSpan.FromHours(1.5));

        var entry = Assert.Single(_publish.Entries());
        Assert.Equal("Christmas started 20 Dec 00:00", entry.Text);
        await activity.Received(1).CreateAsync(Arg.Is<ActivityLog>(a => a.Name.Contains("Christmas started 20 Dec 00:00", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheAdminRoutesPublishWithTheAuthorsNameAndRefuseAnEmptyPublish()
    {
        var users = Substitute.For<IUserManager>();
        users.GetUserById(Admin.Id).Returns(Admin);
        var controller = new AdminPublishController(_publish, users)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(HomeApiController.UserIdClaim, Admin.Id.ToString())], "test")) } },
        };

        Assert.IsType<BadRequestObjectResult>(controller.Publish().Result);
        _rules.Set(ContentRules.Defaults with { Versions = false });
        Assert.Equal(["Content rules changed"], controller.GetDraft().Value!.Changes);
        Assert.Equal(("admin", "Content rules changed"), (controller.Publish().Value!.Author, controller.GetHistory().Value!.Versions[0].Summary));
        Assert.IsType<NotFoundResult>(controller.Restore(42).Result);
        Assert.IsType<BadRequestObjectResult>(controller.Undo().Result);
    }
}
