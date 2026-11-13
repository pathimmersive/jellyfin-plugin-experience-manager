using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Announcements;
using Jellyfin.Plugin.ExperienceManager.Audiences;
using Jellyfin.Plugin.ExperienceManager.Caching;
using Jellyfin.Plugin.ExperienceManager.Controllers;
using Jellyfin.Plugin.ExperienceManager.Home;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using Jellyfin.Plugin.ExperienceManager.Programming;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

public sealed class AnnouncementTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private readonly User _host = new("host", "auth", "reset") { Id = Guid.NewGuid() };
    private readonly User _guest = new("guest", "auth", "reset") { Id = Guid.NewGuid() };
    private readonly TempDatabase _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
    private readonly PageStore _pages;
    private readonly AudienceStore _audiences;
    private readonly EventStore _events;
    private readonly EventTimeline _timeline;
    private readonly HomeCache _cache;
    private readonly DismissalStore _dismissals;
    private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
    private readonly IHomeSources _sources = Substitute.For<IHomeSources>();
    private readonly IUserManager _users = Substitute.For<IUserManager>();
    private readonly BoxSet _party = new() { Id = Guid.NewGuid(), Name = "Party" };
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "em-images-" + Guid.NewGuid().ToString("N"));

    public AnnouncementTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _pages = _db.Store();
        _audiences = new AudienceStore(_db.Open());
        _events = new EventStore(_db.Open(), _audiences);
        _timeline = _db.Timeline(_time, _events);
        var tv = Substitute.For<ITVSeriesManager>();
        tv.GetNextUp(Arg.Any<NextUpQuery>(), Arg.Any<DtoOptions>()).Returns(new QueryResult<BaseItem>());
        _cache = new HomeCache(_library, tv, Substitute.For<IUserDataManager>(), _time);
        _dismissals = new DismissalStore(_db.Open(), _time);
        _library.GetItemById(_party.Id).Returns(_party);
        _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([]);
        _sources.GetViews(Arg.Any<User>()).Returns([]);
        _sources.IsVisible(_party, _host).Returns(true);
        _sources.IsVisible(_party, _guest).Returns(false);
        _users.GetUserById(_host.Id).Returns(_host);
        _users.GetUserById(_guest.Id).Returns(_guest);
    }

    public void Dispose()
    {
        _timeline.Dispose();
        _cache.Dispose();
        _db.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private static StoredSection Notice(string text = "The server is down on Saturday from 2 to 4 am.", bool dismissible = true, string? image = null, Guid? target = null) =>
        new("notice", SectionTypes.Announcement, "Maintenance", new JsonObject
        {
            ["Text"] = text,
            ["Dismissible"] = dismissible,
            ["Image"] = image,
            ["ButtonLabel"] = target is null ? null : "Watch the party",
            ["ButtonTarget"] = target?.ToString("N"),
        });

    private void Layout(params StoredSection[] extra) =>
        Assert.NotNull(_pages.Replace(PageIds.Home, [PageStore.HomeSeed[0], .. extra, .. PageStore.HomeSeed.Skip(1)], out _));

    private StoredPages Stored() => new(_pages, _timeline, _cache, _sources, _time, new AudienceResolver(_audiences, _time), null, null, _dismissals);

    private HomePageService Service() => new(Stored(), Substitute.For<IDtoService>(), NullLogger<HomePageService>.Instance);

    private SectionResponse? Served(User user) =>
        Service().BuildPage(user, PageIds.Home, 0, ItemShape.Default, 0, out _)!.Sections.SingleOrDefault(s => s.Kind == "announcement");

    private HomeApiController Api(User user) =>
        new(Service(), _users, _dismissals, new AnnouncementImages(_folder))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(HomeApiController.UserIdClaim, user.Id.ToString())], "test")) } },
        };

    [Theory]
    [InlineData("", "needs Text")]
    [InlineData("one\ntwo\nthree\nfour", "more than 3 lines")]
    [InlineData("ok", null)]
    public void AnAnnouncementNeedsTextOfAtMostThreeLines(string text, string? error)
    {
        var problem = SectionTypes.ValidateConfig(Notice(text));
        if (error is null)
        {
            Assert.Null(problem);
        }
        else
        {
            Assert.Contains(error, problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnAnnouncementRefusesAnImageThatIsNotAnUploadAndAButtonWithoutBothParts()
    {
        Assert.Contains("uploaded image", SectionTypes.ValidateConfig(Notice(image: "../../etc/passwd")), StringComparison.Ordinal);
        var labelOnly = new StoredSection("notice", SectionTypes.Announcement, null, new JsonObject { ["Text"] = "Hi", ["ButtonLabel"] = "Go" });
        Assert.Contains("both ButtonLabel and ButtonTarget", SectionTypes.ValidateConfig(labelOnly), StringComparison.Ordinal);
        Assert.Contains("longer than 200", SectionTypes.ValidateConfig(Notice(new string('a', 201))), StringComparison.Ordinal);
    }

    [Fact]
    public void HomeServesTheAnnouncementWithItsTextImageAndAButtonOnlyForThoseWhoCanOpenIt()
    {
        Assert.Null(new AnnouncementImages(_folder).Save(new MemoryStream(Png), out var image));
        Layout(Notice("Welcome!\nPress OK to play\nHold OK for more", image: image, target: _party.Id));

        var host = Served(_host)!;
        Assert.Equal(("notice", "Maintenance"), (host.Id, host.Title));
        Assert.Null(host.Items);
        Assert.Equal(["Welcome!", "Press OK to play", "Hold OK for more"], host.Announcement!.Lines);
        Assert.Equal("/ExperienceManager/v1/Images/" + image, host.Announcement.Image);
        Assert.Equal(new AnnouncementButton("Watch the party", _party.Id.ToString("N"), "collection", null), host.Announcement.Button);
        Assert.Null(Served(_guest)!.Announcement!.Button);
    }

    [Fact]
    public void ADismissalHoldsForThatUserUntilTheAnnouncementChanges()
    {
        Layout(Notice());
        var before = Stored().LayoutVersion(PageIds.Home, 0, _host);

        Assert.IsType<NoContentResult>(Api(_host).Dismiss("notice", 0));

        Assert.Null(Served(_host));
        Assert.NotNull(Served(_guest));
        Assert.NotEqual(before, Stored().LayoutVersion(PageIds.Home, 0, _host));
        Assert.IsType<NotFoundResult>(Api(_host).Dismiss("notice", 0));

        // A new store reads the dismissal back from the database.
        Assert.True(new DismissalStore(_db.Open()).IsDismissed(_host.Id, "notice", AnnouncementContent.Of(Notice()).Version));

        Layout(Notice("The server is down on Sunday instead."));
        Assert.NotNull(Served(_host));
    }

    [Fact]
    public void OnlyADismissibleAnnouncementCanBeDismissed()
    {
        Layout(Notice(dismissible: false));
        Assert.IsType<NotFoundResult>(Api(_host).Dismiss("notice", 0));
        Assert.IsType<NotFoundResult>(Api(_host).Dismiss("featured", 0));
        Assert.NotNull(Served(_host));
    }

    [Fact]
    public void AWelcomeInNewUsersLayoutReachesOnlyNewAccounts()
    {
        _audiences.RecordCreated(_guest.Id, _time.GetUtcNow().AddDays(-1));
        _audiences.RecordCreated(_host.Id, _time.GetUtcNow().AddDays(-60));
        var welcome = new StoredSection("welcome", SectionTypes.Announcement, "Welcome to Casa Greaux", new JsonObject { ["Text"] = "Press OK to play\nHold OK for more", ["Dismissible"] = true });
        Assert.NotNull(_audiences.SetLayout(AudienceKinds.NewUsers, PageIds.Home, _pages.Get(PageIds.Home)!.Sections, [new GroupEntry("featured", GroupModes.Inherited), new GroupEntry("welcome", GroupModes.Added, welcome)], out var error));
        Assert.Null(error);

        Assert.Equal(["Press OK to play", "Hold OK for more"], Served(_guest)!.Announcement!.Lines);
        Assert.Null(Served(_host));
    }

    [Fact]
    public void AnEventShowsAnAnnouncementOnlyWhileItIsOn()
    {
        _events.Add(new ProgrammedEvent(string.Empty, "Maintenance notice", true, "everyone", new EventSchedule(ScheduleKinds.Once, "2026-11-13T00:00", "2026-11-16T00:00"), [new EventChange(ChangeKinds.Insert, "featured", Notice(), "continue")]), out var error);
        Assert.Null(error);

        Assert.Null(Served(_host));
        _time.Advance(TimeSpan.FromDays(2));
        Assert.NotNull(Served(_host));
        _time.Advance(TimeSpan.FromDays(3));
        Assert.Null(Served(_host));
    }

    [Fact]
    public void ThePreviewShowsTheAnnouncementEvenToSomeoneWhoDismissedIt()
    {
        Layout(Notice());
        _dismissals.Dismiss(_host.Id, "notice", AnnouncementContent.Of(Notice()).Version);
        var preview = Stored().Preview(PageIds.Home, _host, [], null, 0);
        Assert.Equal(["The server is down on Saturday from 2 to 4 am."], preview.Sections.Single(s => s.Id == "notice").Announcement!.Lines);
    }

    [Fact]
    public void UploadsAreTakenOnlyAsPngJpegOrWebpUpToFiveMegabytesAndServedByTheirContent()
    {
        var images = new AnnouncementImages(_folder);
        Assert.Null(images.Save(new MemoryStream(Png), out var id));
        Assert.Matches("^[0-9a-f]{32}\\.png$", id!);
        Assert.Null(images.Save(new MemoryStream(Png), out var again));
        Assert.Equal(id, again);
        Assert.Null(images.Save(new MemoryStream([0xFF, 0xD8, 0xFF, 0xE0]), out var jpeg));
        Assert.EndsWith(".jpg", jpeg, StringComparison.Ordinal);
        Assert.Null(images.Save(new MemoryStream([.. "RIFF"u8.ToArray(), 0, 0, 0, 0, .. "WEBP"u8.ToArray()]), out var webp));
        Assert.EndsWith(".webp", webp, StringComparison.Ordinal);

        Assert.Equal("Only PNG, JPEG and WebP images are taken.", images.Save(new MemoryStream("<svg/>"u8.ToArray()), out _));
        var big = new byte[AnnouncementImages.MaxBytes + 1];
        Png.CopyTo(big, 0);
        Assert.Equal("The image is larger than 5 MB.", images.Save(new MemoryStream(big), out _));
        Assert.Equal(3, Directory.GetFiles(_folder).Length);

        var served = Assert.IsType<FileStreamResult>(Api(_host).GetImage(id!));
        Assert.Equal("image/png", served.ContentType);
        served.FileStream.Dispose();
        Assert.IsType<NotFoundResult>(Api(_host).GetImage("..%2F" + id));
    }
}
