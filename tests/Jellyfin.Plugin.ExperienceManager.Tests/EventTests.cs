using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Caching;
using Jellyfin.Plugin.ExperienceManager.Controllers;
using Jellyfin.Plugin.ExperienceManager.Home;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using Jellyfin.Plugin.ExperienceManager.Programming;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

public class ScheduleTests
{
    private static DateTime At(string local) => Schedules.LocalTime(local)!.Value;

    [Fact]
    public void AOnceEventIsOnFromItsStartUntilItsEnd()
    {
        var once = new EventSchedule(ScheduleKinds.Once, "2026-12-24T18:00", "2026-12-25T00:00");

        Assert.False(Schedules.IsActive(once, At("2026-12-24T17:59")));
        Assert.True(Schedules.IsActive(once, At("2026-12-24T18:00")));
        Assert.True(Schedules.IsActive(once, At("2026-12-24T23:59")));
        Assert.False(Schedules.IsActive(once, At("2026-12-25T00:00")));
        Assert.Equal(At("2026-12-24T18:00"), Schedules.NextBoundary(once, At("2026-12-01T00:00")));
        Assert.Null(Schedules.NextBoundary(once, At("2026-12-25T00:00")));
    }

    [Fact]
    public void AYearlyEventFromDecember20ToJanuary2RunsAcrossTheNewYear()
    {
        var holidays = new EventSchedule(ScheduleKinds.Yearly, "12-20", "01-02");

        Assert.False(Schedules.IsActive(holidays, At("2026-12-19T23:59")));
        Assert.True(Schedules.IsActive(holidays, At("2026-12-20T00:00")));
        Assert.True(Schedules.IsActive(holidays, At("2026-12-31T12:00")));
        Assert.True(Schedules.IsActive(holidays, At("2027-01-01T12:00")));
        Assert.True(Schedules.IsActive(holidays, At("2027-01-02T23:59")));
        Assert.False(Schedules.IsActive(holidays, At("2027-01-03T00:00")));
        Assert.Equal(At("2027-01-03T00:00"), Schedules.NextBoundary(holidays, At("2026-12-31T12:00")));
        Assert.Equal(At("2027-12-20T00:00"), Schedules.NextBoundary(holidays, At("2027-01-03T00:00")));
    }

    [Fact]
    public void AWeeklyEventIsOnItsDaysBetweenItsTimesAndCanRunPastMidnight()
    {
        var movieNight = new EventSchedule(ScheduleKinds.Weekly, Days: ["Friday"], From: "18:00", To: "23:00");
        var lateShow = new EventSchedule(ScheduleKinds.Weekly, Days: ["Saturday"], From: "22:00", To: "02:00");

        Assert.True(Schedules.IsActive(movieNight, At("2026-11-13T19:00")));
        Assert.False(Schedules.IsActive(movieNight, At("2026-11-13T23:00")));
        Assert.False(Schedules.IsActive(movieNight, At("2026-11-14T19:00")));
        Assert.True(Schedules.IsActive(lateShow, At("2026-11-15T01:30")));
        Assert.False(Schedules.IsActive(lateShow, At("2026-11-15T02:00")));
        Assert.False(Schedules.IsActive(lateShow, At("2026-11-14T01:30")));
        Assert.Equal(At("2026-11-15T02:00"), Schedules.NextBoundary(lateShow, At("2026-11-14T22:30")));
    }

    [Theory]
    [InlineData("once", "2026-12-25T00:00", "2026-12-24T00:00")]
    [InlineData("yearly", "13-01", "12-31")]
    [InlineData("weekly", null, null)]
    [InlineData("daily", "x", "y")]
    public void AScheduleThatCannotBeReadIsRefused(string kind, string? start, string? end) =>
        Assert.NotNull(Schedules.Validate(new EventSchedule(kind, start, end)));
}

public sealed class EventTests : IDisposable
{
    private static readonly User Viewer = new("viewer", "auth", "reset") { Id = Guid.NewGuid() };
    private static readonly Guid Films = Guid.NewGuid();
    private static readonly Guid Box = Guid.NewGuid();

    private readonly TempDatabase _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 12, 19, 23, 0, 0, TimeSpan.Zero));
    private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
    private readonly IUserDataManager _userData = Substitute.For<IUserDataManager>();
    private readonly IHomeSources _sources = Substitute.For<IHomeSources>();
    private readonly PageStore _pages;
    private readonly EventStore _events;
    private readonly EventTimeline _timeline;
    private readonly HomeCache _cache;

    public EventTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _sources.GetViews(Viewer).Returns([new LibraryView(Films, "Movies", CollectionType.movies)]);
        _sources.IsVisible(Arg.Any<BaseItem>(), Viewer).Returns(true);
        _pages = _db.Store();
        _events = _db.Events();
        _timeline = _db.Timeline(_time, _events);
        var tv = Substitute.For<ITVSeriesManager>();
        tv.GetNextUp(Arg.Any<NextUpQuery>(), Arg.Any<DtoOptions>()).Returns(new QueryResult<BaseItem>());
        _cache = new HomeCache(_library, tv, _userData, _time);
    }

    public void Dispose()
    {
        _timeline.Dispose();
        _cache.Dispose();
        _db.Dispose();
    }

    private StoredPages Pages() => new(_pages, _timeline, _cache, _sources, _time);

    private IReadOnlyList<string> HomeIds(int? offset = 0) => Pages().SectionsFor(PageIds.Home, Viewer, offset).Select(s => s.Id).ToList();

    private static StoredSection Query(string id, string genre, bool hideWatched = false) =>
        new(id, SectionTypes.Query, null, new JsonObject { ["Genres"] = new JsonArray(genre), ["HideWatched"] = hideWatched });

    private ProgrammedEvent Add(string name, EventSchedule schedule, params EventChange[] changes)
    {
        var added = _events.Add(new ProgrammedEvent(string.Empty, name, true, EventAudiences.Everyone, schedule, changes), out var error);
        Assert.Null(error);
        return added!;
    }

    private static readonly EventSchedule Holidays = new(ScheduleKinds.Yearly, "12-20", "01-02");

    [Fact]
    public void AFreshInstallHoldsHalloweenAndChristmasAsDraftsAndHomeIsUnchanged()
    {
        Assert.Equal(["halloween", "christmas"], _events.All.Select(e => e.Id));
        Assert.All(_events.All, e => Assert.False(e.Published));
        Assert.All(_events.All, e => Assert.Equal(ScheduleKinds.Yearly, e.Schedule.Kind));
        Assert.Equal(["Horror"], new SectionConfig(_events.All[0].Changes[0].Section!.Config).Strings("Genres"));
        Assert.Equal(["Holiday", "Christmas"], new SectionConfig(_events.All[1].Changes[0].Section!.Config).Strings("Genres"));

        _time.SetUtcNow(new DateTimeOffset(2026, 12, 24, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(["featured", "mymedia", "continue", "nextup", "latest-" + Films.ToString("N")], HomeIds());
        Assert.Equal("r1", Pages().LayoutVersion(PageIds.Home, 0));
    }

    [Fact]
    public void ThePublishedSeedRowShowsInItsWindowAndItsDatesCanBeEdited()
    {
        var christmas = _events.Get("christmas")!;
        _events.Update(christmas with { Schedule = Holidays }, out _);
        _events.SetPublished("christmas", true);

        Assert.DoesNotContain("christmas-classics", HomeIds());

        _time.SetUtcNow(new DateTimeOffset(2026, 12, 31, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(["featured", "mymedia", "continue", "nextup", "christmas-classics", "latest-" + Films.ToString("N")], HomeIds());
        Assert.Equal("Christmas Classics", Pages().SectionsFor(PageIds.Home, Viewer, 0).Single(s => s.Id == "christmas-classics").Title);

        _time.SetUtcNow(new DateTimeOffset(2027, 1, 3, 0, 0, 0, TimeSpan.Zero));
        Assert.DoesNotContain("christmas-classics", HomeIds());
    }

    [Fact]
    public void TheViewersLocalTimeDecidesWhetherAnEventIsOnAndTheServerZoneIsUsedWithoutOne()
    {
        Add("Eve", new EventSchedule(ScheduleKinds.Once, "2026-12-24T18:00", "2026-12-25T00:00"), new EventChange(ChangeKinds.Hide, "mymedia"));
        _time.SetUtcNow(new DateTimeOffset(2026, 12, 24, 20, 0, 0, TimeSpan.Zero));
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("UTC-8", TimeSpan.FromHours(-8), "UTC-8", "UTC-8"));

        Assert.DoesNotContain("mymedia", HomeIds(0));
        Assert.Contains("mymedia", HomeIds(-300));
        Assert.Contains("mymedia", HomeIds(null));
        Assert.DoesNotContain("mymedia", HomeIds(60));
        Assert.Contains("mymedia", HomeIds(99999));
    }

    [Fact]
    public void EachChangeKindAppliesWhileTheEventIsOn()
    {
        var collection = new StoredSection("tonight", SectionTypes.Collection, "Tonight", new JsonObject { ["Collection"] = Box.ToString("N") });
        Add(
            "Premiere week",
            Holidays,
            new EventChange(ChangeKinds.Swap, "nextup", Query("x", "Comedy")),
            new EventChange(ChangeKinds.Featured, null, collection),
            new EventChange(ChangeKinds.Insert, null, Query("horror", "Horror"), "continue"),
            new EventChange(ChangeKinds.Hide, "mymedia"));
        _time.SetUtcNow(new DateTimeOffset(2026, 12, 25, 12, 0, 0, TimeSpan.Zero));

        var sections = Pages().SectionsFor(PageIds.Home, Viewer, 0);

        Assert.Equal(["featured", "continue", "horror", "nextup", "latest-" + Films.ToString("N")], sections.Select(s => s.Id));
        Assert.Equal((SectionSources.Collection, "feature", "Tonight"), (sections[0].Source.Kind, sections[0].Display.Hint, sections[0].Title));
        Assert.Equal(Box.ToString("N"), sections[0].Source.ParentId);
        Assert.Equal((SectionSources.Query, "Horror"), (sections[2].Source.Kind, sections[2].Title));
        Assert.Equal((SectionSources.Query, "row"), (sections[3].Source.Kind, sections[3].Display.Hint));
    }

    [Fact]
    public void WhenAnEventEndsOrIsUnpublishedThePageIsExactlyAsBeforeAndTheStoredLayoutWasNeverWritten()
    {
        var before = (string.Join(",", HomeIds()), Pages().LayoutVersion(PageIds.Home, 0));
        Add("Holidays", Holidays, new EventChange(ChangeKinds.Hide, "nextup"), new EventChange(ChangeKinds.Insert, null, Query("h", "Holiday")));

        _time.SetUtcNow(new DateTimeOffset(2026, 12, 25, 12, 0, 0, TimeSpan.Zero));
        var during = (string.Join(",", HomeIds()), Pages().LayoutVersion(PageIds.Home, 0));
        Assert.NotEqual(before.Item1, during.Item1);
        Assert.StartsWith("r1-e", during.Item2, StringComparison.Ordinal);

        _time.SetUtcNow(new DateTimeOffset(2027, 1, 3, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(before, (string.Join(",", HomeIds()), Pages().LayoutVersion(PageIds.Home, 0)));

        _time.SetUtcNow(new DateTimeOffset(2027, 12, 25, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(during, (string.Join(",", HomeIds()), Pages().LayoutVersion(PageIds.Home, 0)));
        _events.SetPublished("holidays", false);
        Assert.Equal(before, (string.Join(",", HomeIds()), Pages().LayoutVersion(PageIds.Home, 0)));

        Assert.Equal(PageStore.HomeSeed, _db.Store().Get(PageIds.Home)!.Sections);
        Assert.Equal(1, _db.Store().Get(PageIds.Home)!.Revision);
    }

    [Fact]
    public void TheHigherEventWinsWhereTwoChangeTheSameSectionAndTheListSaysSoInOneLine()
    {
        Add("Holidays", Holidays, new EventChange(ChangeKinds.Swap, "nextup", Query("x", "Holiday")));
        Add("Movie night", new EventSchedule(ScheduleKinds.Weekly, Days: ["Thursday", "Friday"], From: "00:00", To: "23:59"), new EventChange(ChangeKinds.Hide, "nextup"));
        Add("Halloween party", new EventSchedule(ScheduleKinds.Yearly, "10-30", "10-31"), new EventChange(ChangeKinds.Hide, "continue"));
        _time.SetUtcNow(new DateTimeOffset(2026, 12, 25, 12, 0, 0, TimeSpan.Zero));

        Assert.Contains("nextup", HomeIds());
        var controller = new AdminEventsController(_events, _pages, _time);
        Assert.Equal(["Holidays and Movie night both change Next Up; Holidays wins, as it is higher in the list."], controller.GetEvents().Value!.Conflicts);

        controller.OrderEvents(new AdminEventOrderRequest(["halloween", "christmas", "movie-night", "holidays", "halloween-party"]));

        Assert.DoesNotContain("nextup", HomeIds());
        Assert.Equal(["Movie night and Holidays both change Next Up; Movie night wins, as it is higher in the list."], controller.GetEvents().Value!.Conflicts);
    }

    [Fact]
    public void ASeasonalRowShowsWatchedTitlesUnlessHideWatchedIsOn()
    {
        var watched = HomePageServiceTests.Film("watched");
        var fresh = HomePageServiceTests.Film("fresh");
        _library.GetItemList(Arg.Is<InternalItemsQuery>(q => q.Genres.Contains("Holiday"))).Returns([watched, fresh]);
        _userData.GetUserDataBatch(Arg.Any<IReadOnlyList<BaseItem>>(), Viewer)
            .Returns(new Dictionary<Guid, UserItemData> { [watched.Id] = new() { Key = "w", Played = true } });
        _pages.Replace(PageIds.Home, [Query("shown", "Holiday"), Query("hidden", "Holiday", hideWatched: true) with { Title = "Unwatched" }], out var error);
        Assert.Null(error);

        var sections = Pages().SectionsFor(PageIds.Home, Viewer, 0);

        Assert.Equal([watched, fresh], sections[0].Load(Viewer).Items);
        Assert.Equal([fresh], sections[1].Load(Viewer).Items);
    }

    [Fact]
    public void BetweenBoundariesARequestReadsTheSetTheTimerWorkedOutAtTheLastBoundary()
    {
        Add("Holidays", Holidays, new EventChange(ChangeKinds.Hide, "nextup"));
        var first = _timeline.For(0);
        Assert.Empty(first.Events);
        Assert.Equal(new DateTimeOffset(2026, 12, 20, 0, 0, 0, TimeSpan.Zero), first.Until);

        _time.Advance(TimeSpan.FromMinutes(30));
        Assert.Same(first, _timeline.For(0));

        // The boundary passes with no request: the timer works the set out.
        _time.Advance(TimeSpan.FromMinutes(31));
        var computed = _timeline.For(0);
        Assert.NotSame(first, computed);
        Assert.Equal(["holidays"], computed.Events.Select(e => e.Id));
        _time.Advance(TimeSpan.FromHours(5));
        Assert.Same(computed, _timeline.For(0));
    }

    [Fact]
    public void TheAdminListsCreatesEditsReordersPublishesAndUnpublishesEventsAndRefusesBadOnes()
    {
        var controller = new AdminEventsController(_events, _pages, _time);
        var night = new ProgrammedEvent(string.Empty, "Movie night", false, EventAudiences.Everyone, new EventSchedule(ScheduleKinds.Weekly, Days: ["Friday"], From: "18:00", To: "23:00"), [new EventChange(ChangeKinds.Hide, "mymedia")]);

        var added = controller.AddEvent(night).Value!;
        Assert.Equal(("movie-night", false), (added.Id, added.Published));
        Assert.Equal(["halloween", "christmas", "movie-night"], controller.GetEvents().Value!.Events.Select(e => e.Id));
        Assert.Equal("Friday night", controller.UpdateEvent("movie-night", night with { Name = "Friday night" }).Value!.Name);
        Assert.True(controller.PublishEvent("movie-night").Value!.Published);
        Assert.False(controller.UnpublishEvent("movie-night").Value!.Published);
        Assert.Equal(["movie-night", "halloween", "christmas"], controller.OrderEvents(new AdminEventOrderRequest(["movie-night", "halloween", "christmas"])).Value!.Events.Select(e => e.Id));
        Assert.Equal(["movie-night", "halloween", "christmas"], _db.Events().All.Select(e => e.Id));

        Assert.IsType<BadRequestObjectResult>(controller.AddEvent(night).Result);
        Assert.IsType<BadRequestObjectResult>(controller.AddEvent(night with { Id = "x", Audience = "kids" }).Result);
        Assert.IsType<BadRequestObjectResult>(controller.AddEvent(night with { Id = "y", Changes = [new EventChange("pin", "mymedia")] }).Result);
        Assert.IsType<BadRequestObjectResult>(controller.AddEvent(night with { Id = "z", Changes = [new EventChange(ChangeKinds.Insert, null, new StoredSection("q", SectionTypes.Query))] }).Result);
        Assert.IsType<BadRequestObjectResult>(controller.OrderEvents(new AdminEventOrderRequest(["movie-night"])).Result);
        Assert.IsType<NotFoundResult>(controller.UpdateEvent("nope", night).Result);
        Assert.IsType<NotFoundResult>(controller.PublishEvent("nope").Result);

        Assert.IsType<NoContentResult>(controller.DeleteEvent("movie-night"));
        Assert.Equal(["halloween", "christmas"], _db.Events().All.Select(e => e.Id));
    }
}
