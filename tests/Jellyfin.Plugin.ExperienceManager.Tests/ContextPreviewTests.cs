using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Audiences;
using Jellyfin.Plugin.ExperienceManager.Caching;
using Jellyfin.Plugin.ExperienceManager.Controllers;
using Jellyfin.Plugin.ExperienceManager.Home;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using Jellyfin.Plugin.ExperienceManager.Programming;
using Jellyfin.Plugin.ExperienceManager.Rules;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

public sealed class ContextPreviewTests : IDisposable
{
    private static readonly User Admin = new("admin", "auth", "reset") { Id = Guid.NewGuid() };
    private static readonly User Kid = new("kid", "auth", "reset") { Id = Guid.NewGuid() };

    private readonly TempDatabase _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
    private readonly PageStore _pages;
    private readonly AudienceStore _audiences;
    private readonly EventStore _events;
    private readonly RulesStore _rules;
    private readonly EventTimeline _timeline;
    private readonly HomeCache _cache;
    private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
    private readonly IHomeSources _sources = Substitute.For<IHomeSources>();
    private readonly IUserManager _users = Substitute.For<IUserManager>();
    private readonly Movie _hidden = Film("hidden");
    private readonly Movie _shown = Film("shown");
    private readonly Movie _tagged = Film("tagged");

    public ContextPreviewTests()
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
        _tagged.Tags = ["hidden"];
        _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([_shown, _hidden, _tagged]);
        _sources.GetViews(Arg.Any<User>()).Returns([]);
        _sources.IsVisible(Arg.Any<BaseItem>(), Admin).Returns(true);
        _sources.IsVisible(Arg.Any<BaseItem>(), Kid).Returns(c => c.Arg<BaseItem>() != _hidden);
        _users.GetUserById(Admin.Id).Returns(Admin);
        _users.GetUserById(Kid.Id).Returns(Kid);
        _audiences.Add("Kids", [Kid.Id], out _);
        _audiences.SetLayout("kids", PageIds.Home, _pages.Get(PageIds.Home)!.Sections, [new("mymedia", GroupModes.Removed)], out _);
        _pages.Replace(PageIds.Home, [.. PageStore.HomeSeed, new StoredSection("picks", SectionTypes.Query, "Picks", new JsonObject { ["Genres"] = new JsonArray("Drama") })], out _);
        _rules.Set(ContentRules.Defaults with { ExcludeTags = true, ExcludedTags = ["hidden"] });
    }

    public void Dispose()
    {
        _timeline.Dispose();
        _cache.Dispose();
        _db.Dispose();
    }

    private static Movie Film(string name) => new() { Id = Guid.NewGuid(), Name = name, ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = "p" }] };

    private AdminPreviewController Controller()
    {
        // Home serves the published defaults; the preview applies the draft, which excludes the tag.
        var stored = new StoredPages(_pages, _timeline, _cache, _sources, _time, new AudienceResolver(_audiences, _time), new RuleFilter(() => ContentRules.Defaults, () => 1, Substitute.For<ILocalizationManager>(), Substitute.For<IUserDataManager>(), _time, () => _rules.Rules));
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(HomeApiController.UserIdClaim, Admin.Id.ToString())], "test")) };
        return new AdminPreviewController(stored, _events, _users, _audiences) { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    private static AdminPreviewResponse Ok(ActionResult<AdminPreviewResponse> result) => Assert.IsType<AdminPreviewResponse>(result.Value);

    [Fact]
    public void APreviewAsAUserShowsTheirLayoutTheirItemsAndWhy()
    {
        var kid = Ok(Controller().Preview("home", new AdminPreviewRequest("2026-11-12T12:00", null, null, Kid.Id, 12)));

        Assert.Equal(("kid", "Kids's", "kids"), (kid.Viewer, kid.Layout, kid.LayoutAudience));
        Assert.Equal(["Kids"], kid.Audiences);
        Assert.DoesNotContain(kid.Sections, s => s.Id == "mymedia");
        var picks = kid.Sections.Single(s => s.Id == "picks");
        Assert.Equal(["shown"], picks.Items!.Select(i => i.Name));
        Assert.Equal(["Excluded tags left out 1 item"], picks.Notes);
        Assert.True(picks.Items![0].HasPrimary);
    }

    [Fact]
    public void ThePreviewForTheAdminOrNoUserIsEveryonesAndItemsComeOnlyWhenAskedFor()
    {
        var admin = Ok(Controller().Preview("home", new AdminPreviewRequest("2026-11-12T12:00", null, null, null, 12)));
        Assert.Equal(("admin", "Everyone's", (string?)null), (admin.Viewer, admin.Layout, admin.LayoutAudience));
        Assert.Contains(admin.Sections, s => s.Id == "mymedia");
        Assert.Equal(["shown", "hidden"], admin.Sections.Single(s => s.Id == "picks").Items!.Select(i => i.Name));

        var bare = Ok(Controller().Preview("home", new AdminPreviewRequest("2026-11-12T12:00", null)));
        Assert.All(bare.Sections, s => Assert.Null(s.Items));
    }

    [Fact]
    public void AFutureDateAppliesItsEventsForTheViewersAudiences()
    {
        var party = new StoredSection("party", SectionTypes.Query, "Party", new JsonObject { ["Genres"] = new JsonArray("Comedy") });
        _events.Add(new ProgrammedEvent(string.Empty, "Kids party", true, "kids", new EventSchedule(ScheduleKinds.Once, "2026-12-24T00:00", "2026-12-25T00:00"), [new EventChange(ChangeKinds.Insert, null, party, "continue")]), out _);

        Assert.DoesNotContain(Ok(Controller().Preview("home", new AdminPreviewRequest("2026-11-12T12:00", null, null, Kid.Id))).Sections, s => s.Id == "party");
        var christmasEve = Ok(Controller().Preview("home", new AdminPreviewRequest("2026-12-24T12:00", null, null, Kid.Id)));
        Assert.Equal(["Kids party"], christmasEve.Events);
        Assert.Contains(christmasEve.Sections, s => s.Id == "party" && s.Event == "Kids party");
        Assert.DoesNotContain(Ok(Controller().Preview("home", new AdminPreviewRequest("2026-12-24T12:00", null, null, Admin.Id))).Sections, s => s.Id == "party");
    }

    [Fact]
    public void AnUnknownUserIsRefused() =>
        Assert.IsType<BadRequestObjectResult>(Controller().Preview("home", new AdminPreviewRequest("2026-11-12T12:00", null, null, Guid.NewGuid())).Result);
}
