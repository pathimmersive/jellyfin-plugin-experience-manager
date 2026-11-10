using System;
using System.Linq;
using System.Security.Claims;
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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

public sealed class AdminPreviewTests : IDisposable
{
    private static readonly User Admin = new("admin", "auth", "reset") { Id = Guid.NewGuid() };
    private static readonly Guid Films = Guid.NewGuid();

    private readonly TempDatabase _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
    private readonly PageStore _pages;
    private readonly EventStore _events;
    private readonly EventTimeline _timeline;
    private readonly HomeCache _cache;
    private readonly IUserManager _users = Substitute.For<IUserManager>();

    public AdminPreviewTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _pages = _db.Store();
        _events = _db.Events();
        _timeline = _db.Timeline(_time, _events);
        var tv = Substitute.For<ITVSeriesManager>();
        tv.GetNextUp(Arg.Any<NextUpQuery>(), Arg.Any<DtoOptions>()).Returns(new QueryResult<BaseItem>());
        _cache = new HomeCache(Substitute.For<ILibraryManager>(), tv, Substitute.For<IUserDataManager>(), _time);
        _users.GetUserById(Admin.Id).Returns(Admin);
    }

    public void Dispose()
    {
        _timeline.Dispose();
        _cache.Dispose();
        _db.Dispose();
    }

    private AdminPreviewController Controller()
    {
        var sources = Substitute.For<IHomeSources>();
        sources.GetViews(Admin).Returns([new LibraryView(Films, "Movies", CollectionType.movies)]);
        var pages = new StoredPages(_pages, _timeline, _cache, sources, _time);
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(HomeApiController.UserIdClaim, Admin.Id.ToString())], "test")) };
        return new AdminPreviewController(pages, _events, _users) { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    private static AdminPreviewResponse Ok(ActionResult<AdminPreviewResponse> result) => Assert.IsType<AdminPreviewResponse>(result.Value);

    [Fact]
    public void ADraftBeingEditedIsPreviewedOnItsDatesWithEachSectionMarkedAndNothingIsStored()
    {
        var christmas = _events.Get("christmas")!;
        var edited = christmas with
        {
            Changes = [.. christmas.Changes, new EventChange(ChangeKinds.Hide, "mymedia"), new EventChange(ChangeKinds.Swap, "nextup", new StoredSection("x", SectionTypes.Collection, "Holiday Picks", new JsonObject { ["Collection"] = Guid.NewGuid().ToString("N") }))],
        };

        var preview = Ok(Controller().Preview("home", new AdminPreviewRequest("2026-12-24T19:00", edited)));

        Assert.Equal(["Christmas"], preview.Events);
        Assert.Equal(["featured", "continue", "nextup", "christmas-classics", "latest-" + Films.ToString("N")], preview.Sections.Select(s => s.Id));
        Assert.Equal(("Christmas", ChangeKinds.Insert), (preview.Sections[3].Event, preview.Sections[3].Change));
        Assert.Equal(("Holiday Picks", "Christmas", ChangeKinds.Swap), (preview.Sections[2].Title, preview.Sections[2].Event, preview.Sections[2].Change));
        Assert.Null(preview.Sections[0].Event);
        Assert.Equal([new PreviewHidden("mymedia", "My Media", "Christmas")], preview.Hidden);

        Assert.False(_db.Events().Get("christmas")!.Published);
        Assert.Equal(christmas.Changes.Count, _db.Events().Get("christmas")!.Changes.Count);
        Assert.Equal(PageStore.HomeSeed, _db.Store().Get(PageIds.Home)!.Sections);
    }

    [Fact]
    public void OutsideItsDatesOrWithoutItTheDraftDoesNotApplyAndPublishedEventsDo()
    {
        Assert.Empty(Ok(Controller().Preview("home", new AdminPreviewRequest("2026-12-24T19:00", null))).Events);
        Assert.Empty(Ok(Controller().Preview("home", new AdminPreviewRequest("2026-11-24T19:00", _events.Get("christmas")))).Events);

        _events.SetPublished("halloween", true);
        var preview = Ok(Controller().Preview("home", new AdminPreviewRequest("2026-10-31T21:00", null)));
        Assert.Equal(["Halloween"], preview.Events);
        Assert.Contains(preview.Sections, s => s.Id == "spooky-season" && s.Event == "Halloween");
    }

    [Fact]
    public void ABadMomentOrEventIsRefusedAndAnUnknownPageIsNotFound()
    {
        Assert.IsType<BadRequestObjectResult>(Controller().Preview("home", new AdminPreviewRequest("24 Dec", null)).Result);
        var broken = _events.Get("christmas")! with { Changes = [new EventChange("pin", "nextup")] };
        Assert.IsType<BadRequestObjectResult>(Controller().Preview("home", new AdminPreviewRequest("2026-12-24T19:00", broken)).Result);
        Assert.IsType<NotFoundResult>(Controller().Preview("movies", new AdminPreviewRequest("2026-12-24T19:00", null)).Result);
    }
}
