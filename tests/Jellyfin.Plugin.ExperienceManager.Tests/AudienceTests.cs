using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Audiences;
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

public class GroupLayoutTests
{
    private static readonly IReadOnlyList<StoredSection> Server = PageStore.HomeSeed;

    private static StoredSection Query(string id, string genre) => new(id, SectionTypes.Query, null, new JsonObject { ["Genres"] = new JsonArray(genre) });

    private static string Ids(IEnumerable<StoredSection> sections) => string.Join(",", sections.Select(s => s.Id));

    [Fact]
    public void AGroupLayoutRemovesReplacesAddsAndReordersWholeSectionsAndInheritsTheRest()
    {
        var entries = new List<GroupEntry>
        {
            new("nextup", GroupModes.Changed, new StoredSection("x", SectionTypes.NextUp, "Up next")),
            new("cartoons", GroupModes.Added, Query("x", "Animation")),
            new("featured", GroupModes.Inherited),
            new("mymedia", GroupModes.Removed),
        };

        var merged = GroupLayouts.Merge(Server, entries);

        // Latest is never mentioned, so it follows Next Up, the server section before it.
        Assert.Equal("nextup,latest,cartoons,featured,mymedia,continue", Ids(merged.Select(m => m.Section)));
        Assert.Equal(["changed", "inherited", "added", "inherited", "removed", "inherited"], merged.Select(m => m.Mark));
        Assert.Equal("Up next", merged[0].Section.Title);
        Assert.Equal("nextup,latest,cartoons,featured,continue", Ids(GroupLayouts.Resolve(Server, entries)));
    }

    [Fact]
    public void ASectionTheServerAddsLaterReachesTheGroupAfterTheServerSectionBeforeIt()
    {
        var entries = new List<GroupEntry> { new("mymedia", GroupModes.Removed), new("latest", GroupModes.Inherited), new("featured", GroupModes.Inherited) };
        StoredSection[] later = [Server[0], Server[1], Query("horror", "Horror"), Server[2], Server[3], Server[4]];

        Assert.Equal("continue,nextup,latest,featured", Ids(GroupLayouts.Resolve(Server, entries)));
        Assert.Equal("horror,continue,nextup,latest,featured", Ids(GroupLayouts.Resolve(later, entries)));
        Assert.Equal("featured,horror", Ids(GroupLayouts.Resolve([Server[0], Query("horror", "Horror")], [new("featured", GroupModes.Inherited)])));
    }

    [Theory]
    [InlineData("unknown mode")]
    [InlineData("inherits a missing section")]
    [InlineData("adds a server section")]
    [InlineData("changed without a section")]
    [InlineData("twice")]
    public void EntriesThatDoNotFitTheServerLayoutAreRefused(string fault)
    {
        GroupEntry[] entries = fault switch
        {
            "unknown mode" => [new("nextup", "pinned")],
            "inherits a missing section" => [new("tiles", GroupModes.Inherited)],
            "adds a server section" => [new("nextup", GroupModes.Added, Query("x", "Horror"))],
            "changed without a section" => [new("nextup", GroupModes.Changed)],
            _ => [new("nextup", GroupModes.Removed), new("NEXTUP", GroupModes.Inherited)],
        };

        Assert.NotNull(GroupLayouts.Validate(Server, entries));
    }
}

public sealed class AudienceTests : IDisposable
{
    private static readonly User Kid = new("kid", "auth", "reset") { Id = Guid.NewGuid() };
    private static readonly User Parent = new("parent", "auth", "reset") { Id = Guid.NewGuid() };

    private readonly TempDatabase _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
    private readonly AudienceStore _audiences;
    private readonly EventStore _events;
    private readonly EventTimeline _timeline;
    private readonly HomeCache _cache;
    private readonly PageStore _pages;
    private readonly IHomeSources _sources = Substitute.For<IHomeSources>();
    private readonly IUserManager _users = Substitute.For<IUserManager>();

    public AudienceTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _pages = _db.Store();
        _audiences = new AudienceStore(_db.Open());
        _events = new EventStore(_db.Open(), _audiences);
        _timeline = _db.Timeline(_time, _events);
        var tv = Substitute.For<ITVSeriesManager>();
        tv.GetNextUp(Arg.Any<NextUpQuery>(), Arg.Any<DtoOptions>()).Returns(new QueryResult<BaseItem>());
        _cache = new HomeCache(Substitute.For<ILibraryManager>(), tv, Substitute.For<IUserDataManager>(), _time);
        _sources.GetViews(Arg.Any<User>()).Returns([]);
        _users.GetUsers().Returns([Kid, Parent]);
        _users.GetUserById(Kid.Id).Returns(Kid);
        _users.GetUserById(Parent.Id).Returns(Parent);
    }

    public void Dispose()
    {
        _timeline.Dispose();
        _cache.Dispose();
        _db.Dispose();
    }

    private AudienceResolver Resolver() => new(_audiences, _time);

    private StoredPages Pages() => new(_pages, _timeline, _cache, _sources, _time, Resolver());

    private string Home(User user) => string.Join(",", Pages().SectionsFor(PageIds.Home, user, 0).Select(s => s.Id));

    private AdminAudiencesController Controller() => new(_audiences, Resolver(), _pages, _events, _users);

    [Fact]
    public void AudiencesAreKeptInOrderWithTheirMembersAndNewUsersIsBuiltIn()
    {
        Assert.Equal([("new-users", "New users", 14)], _audiences.All.Select(a => (a.Id, a.Name, a.Days ?? 0)));

        var kids = _audiences.Add("Kids", [Kid.Id], out _)!;
        var family = _audiences.Add("Family", [Kid.Id, Parent.Id], out _)!;
        Assert.Equal(["new-users", "kids", "family"], _audiences.All.Select(a => a.Id));
        Assert.Equal([Kid.Id], kids.Members);

        _audiences.Update("kids", "Children", [Parent.Id], null, out _);
        _audiences.Reorder(["family", "kids", "new-users"], out _);
        var reopened = new AudienceStore(_db.Open());
        Assert.Equal(["family", "kids", "new-users"], reopened.All.Select(a => a.Id));
        Assert.Equal(("Children", Parent.Id), (reopened.Get("kids")!.Name, reopened.Get("kids")!.Members.Single()));
        Assert.Equal(2, family.Members.Count);

        Assert.Null(_audiences.Update("new-users", null, [Kid.Id], 14, out var withMembers));
        Assert.NotNull(withMembers);
        Assert.Null(_audiences.Update("new-users", null, null, 0, out _));
        Assert.Equal(30, _audiences.Update("new-users", null, null, 30, out _)!.Days);
        Assert.False(_audiences.Delete("new-users", out var builtIn));
        Assert.NotNull(builtIn);
        Assert.Null(_audiences.Reorder(["family"], out _));
        Assert.True(_audiences.Delete("family", out _));
        Assert.Equal(["kids", "new-users"], _audiences.All.Select(a => a.Id));
    }

    [Fact]
    public void AnAccountTheServerCreatedAfterTheInstallIsANewUserForItsDaysAndOlderAccountsNeverAre()
    {
        new UserCreatedRecorder(_audiences, _time).OnEvent(new UserCreatedEventArgs(Kid));

        Assert.Equal(["new-users"], Resolver().AudiencesOf(Kid));
        Assert.Empty(Resolver().AudiencesOf(Parent));

        _time.Advance(TimeSpan.FromDays(13.9));
        Assert.Equal(["new-users"], Resolver().AudiencesOf(Kid));
        _time.Advance(TimeSpan.FromDays(0.2));
        Assert.Empty(Resolver().AudiencesOf(Kid));

        new UserDeletedRecorder(_audiences).OnEvent(new UserDeletedEventArgs(Kid));
        Assert.Null(_audiences.CreatedAt(Kid.Id));
    }

    [Fact]
    public void AUserGetsTheLayoutOfTheirFirstAudienceInPrecedenceOrderThatHasOneElseTheServers()
    {
        _audiences.Add("Kids", [Kid.Id], out _);
        _audiences.Add("Family", [Kid.Id, Parent.Id], out _);
        _audiences.SetLayout("kids", PageIds.Home, _pages.Get(PageIds.Home)!.Sections, [new("mymedia", GroupModes.Removed)], out _);
        _audiences.SetLayout("family", PageIds.Home, _pages.Get(PageIds.Home)!.Sections, [new("nextup", GroupModes.Removed)], out _);

        Assert.Equal("featured,continue,nextup", Home(Kid));
        Assert.Equal("featured,mymedia,continue", Home(Parent));
        Assert.Equal("featured,mymedia,continue,nextup", Home(new User("guest", "auth", "reset") { Id = Guid.NewGuid() }));

        _audiences.Reorder(["family", "kids", "new-users"], out _);
        Assert.Equal("featured,mymedia,continue", Home(Kid));
        Assert.NotEqual(Pages().LayoutVersion(PageIds.Home, 0, Kid), Pages().LayoutVersion(PageIds.Home, 0, new User("guest", "auth", "reset") { Id = Guid.NewGuid() }));

        Assert.Equal(PageStore.HomeSeed, _db.Store().Get(PageIds.Home)!.Sections);
    }

    [Fact]
    public void AnEventForAnAudienceAppliesOnlyToItsMembersAndTheirLayoutVersionShowsIt()
    {
        _audiences.Add("Kids", [Kid.Id], out _);
        var cartoons = new StoredSection("cartoons", SectionTypes.Query, "Cartoons", new JsonObject { ["Genres"] = new JsonArray("Animation") });
        var added = _events.Add(new ProgrammedEvent(string.Empty, "Saturday cartoons", true, "kids", new EventSchedule(ScheduleKinds.Yearly, "11-01", "11-30"), [new EventChange(ChangeKinds.Insert, null, cartoons, "nextup")]), out var error);
        Assert.Null(error);
        Assert.NotNull(added);

        Assert.Contains("cartoons", Home(Kid));
        Assert.DoesNotContain("cartoons", Home(Parent));
        Assert.Contains("-e", Pages().LayoutVersion(PageIds.Home, 0, Kid), StringComparison.Ordinal);
        Assert.Equal("r1", Pages().LayoutVersion(PageIds.Home, 0, Parent));

        Assert.Null(_events.Add(new ProgrammedEvent(string.Empty, "Teens", true, "teens", new EventSchedule(ScheduleKinds.Yearly, "11-01", "11-30"), [new EventChange(ChangeKinds.Hide, "mymedia")]), out var unknown));
        Assert.Contains("no audience", unknown, StringComparison.Ordinal);
        Assert.IsType<BadRequestObjectResult>(Controller().DeleteAudience("kids").Result);
    }

    [Fact]
    public void TheAdminRoutesListAudiencesAndMarkEachSectionOfAGroupLayout()
    {
        var controller = Controller();
        var added = controller.AddAudience(new AdminAudienceRequest("Kids", [Kid.Id], null)).Value!;
        Assert.Equal(["New users", "Kids"], added.Audiences.Select(a => a.Name));
        Assert.Equal("kid", added.Audiences[1].Members.Single().Name);
        Assert.IsType<BadRequestObjectResult>(controller.AddAudience(new AdminAudienceRequest("Ghosts", [Guid.NewGuid()], null)).Result);

        var none = controller.GetLayout("kids", "home").Value!;
        Assert.False(none.HasLayout);
        Assert.All(none.Sections, s => Assert.Equal(GroupModes.Inherited, s.Mark));

        var set = controller.SetLayout("kids", "home", new AdminGroupLayoutRequest([new("mymedia", GroupModes.Removed), new("nextup", GroupModes.Changed, new StoredSection("x", SectionTypes.NextUp, "Up next"))])).Value!;
        Assert.Equal((true, 1), (set.HasLayout, set.Revision));
        Assert.Equal(["inherited", "removed", "inherited", "changed", "inherited"], set.Sections.Select(s => s.Mark));
        Assert.IsType<BadRequestObjectResult>(controller.SetLayout("kids", "home", new AdminGroupLayoutRequest([new("tiles", GroupModes.Inherited)])).Result);

        var back = controller.SetLayout("kids", "home", new AdminGroupLayoutRequest([new("mymedia", GroupModes.Removed), new("nextup", GroupModes.Inherited)])).Value!;
        Assert.Equal((GroupModes.Inherited, (string?)null), (back.Sections.Single(s => s.Id == "nextup").Mark, back.Sections.Single(s => s.Id == "nextup").Title));
        Assert.True(controller.GetAudiences().Value!.Audiences[1].HasLayout);

        Assert.False(controller.ClearLayout("kids", "home").Value!.HasLayout);
        Assert.IsType<NotFoundResult>(controller.GetLayout("teens", "home").Result);
        Assert.IsType<NotFoundResult>(controller.GetLayout("kids", "movies").Result);
    }
}
