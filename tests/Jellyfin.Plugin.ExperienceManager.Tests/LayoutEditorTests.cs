using System;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Audiences;
using Jellyfin.Plugin.ExperienceManager.Caching;
using Jellyfin.Plugin.ExperienceManager.Home;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using Jellyfin.Plugin.ExperienceManager.Programming;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

public sealed class LayoutEditorTests : IDisposable
{
    private static readonly User Kid = new("kid", "auth", "reset") { Id = Guid.NewGuid() };
    private static readonly User Parent = new("parent", "auth", "reset") { Id = Guid.NewGuid() };
    private static readonly Guid Box = Guid.NewGuid();

    private readonly TempDatabase _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 12, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
    private readonly PageStore _pages;
    private readonly AudienceStore _audiences;
    private readonly EventStore _events;
    private readonly EventTimeline _timeline;
    private readonly HomeCache _cache;
    private readonly IHomeSources _sources = Substitute.For<IHomeSources>();

    public LayoutEditorTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _pages = _db.Store();
        _audiences = new AudienceStore(_db.Open());
        _events = new EventStore(_db.Open(), _audiences);
        _timeline = _db.Timeline(_time, _events);
        var tv = Substitute.For<ITVSeriesManager>();
        tv.GetNextUp(Arg.Any<NextUpQuery>(), Arg.Any<DtoOptions>()).Returns(new QueryResult<BaseItem>());
        _cache = new HomeCache(_library, tv, Substitute.For<IUserDataManager>(), _time);
        _sources.GetViews(Arg.Any<User>()).Returns([]);
    }

    public void Dispose()
    {
        _timeline.Dispose();
        _cache.Dispose();
        _db.Dispose();
    }

    private StoredPages Pages() => new(_pages, _timeline, _cache, _sources, _time, new AudienceResolver(_audiences, _time));

    private string Home(User user) => string.Join(",", Pages().SectionsFor(PageIds.Home, user, 0).Select(s => s.Id));

    [Fact]
    public void ASectionSwitchedOffIsKeptInTheLayoutAndNotServedInEveryonesOrAnInheritingGroup()
    {
        var sections = PageStore.HomeSeed.Select(s => s.Id == "mymedia" ? s with { Off = true } : s).ToList();
        _pages.Replace(PageIds.Home, sections, out _);
        _audiences.Add("Kids", [Kid.Id], out _);
        _audiences.SetLayout("kids", PageIds.Home, _pages.Get(PageIds.Home)!.Sections, [new("nextup", GroupModes.Removed)], out _);

        Assert.True(_db.Store().Get(PageIds.Home)!.Sections.Single(s => s.Id == "mymedia").Off);
        Assert.Equal("r2", _db.Store().Get(PageIds.Home)!.LayoutVersion);
        Assert.Equal("featured,continue,nextup", Home(Parent));
        Assert.Equal("featured,continue", Home(Kid));

        _audiences.SetLayout("kids", PageIds.Home, _pages.Get(PageIds.Home)!.Sections, [new("mymedia", GroupModes.Changed, new StoredSection("mymedia", SectionTypes.MyMedia))], out _);
        Assert.Equal("featured,mymedia,continue,nextup", Home(Kid));
    }

    [Fact]
    public void TheMigrationGivesEveryStoredSectionAnOffFlagThatIsOffByDefault()
    {
        Assert.Equal(LayoutDatabase.Migrations.Max(m => m.Version), _db.Open().SchemaVersion());
        Assert.All(_db.Store().Get(PageIds.Home)!.Sections, s => Assert.False(s.Off));
    }

    [Fact]
    public void BadgesNameTheEventChangingOrHidingASectionNowAndASourceThatNoLongerExists()
    {
        _library.GetItemById(Box).Returns((BaseItem?)null);
        var collection = new StoredSection("picks", SectionTypes.Collection, "Picks", new JsonObject { ["Collection"] = Box.ToString("N") });
        _pages.Replace(PageIds.Home, [.. PageStore.HomeSeed, collection], out _);
        _events.SetPublished("christmas", true);
        _events.Add(new ProgrammedEvent(string.Empty, "Quiet", true, EventAudiences.Everyone, new EventSchedule(ScheduleKinds.Yearly, "12-01", "12-31"), [new EventChange(ChangeKinds.Hide, "mymedia")]), out _);

        var badges = Pages().Badges(PageIds.Home, null);

        Assert.Contains(new SectionBadge("christmas-classics", "event", "Added by Christmas", "Christmas Classics"), badges);
        Assert.Contains(new SectionBadge("mymedia", "event", "Hidden by Quiet"), badges);
        Assert.Contains(new SectionBadge("picks", "source-missing", "Source missing"), badges);
        Assert.DoesNotContain(badges, b => b.Id == "nextup");

        _library.GetItemById(Box).Returns(new Folder { Id = Box });
        Assert.DoesNotContain(Pages().Badges(PageIds.Home, null), b => b.Kind == "source-missing");
    }

    [Fact]
    public void AGroupsBadgesFollowItsOwnLayoutAndTheEventsForIt()
    {
        _audiences.Add("Kids", [Kid.Id], out _);
        _events.Add(new ProgrammedEvent(string.Empty, "Kids party", true, "kids", new EventSchedule(ScheduleKinds.Yearly, "12-01", "12-31"), [new EventChange(ChangeKinds.Hide, "nextup")]), out _);

        Assert.Contains(new SectionBadge("nextup", "event", "Hidden by Kids party"), Pages().Badges(PageIds.Home, "kids"));
        Assert.DoesNotContain(Pages().Badges(PageIds.Home, null), b => b.Id == "nextup");
    }
}
