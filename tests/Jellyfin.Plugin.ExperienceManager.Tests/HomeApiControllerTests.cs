using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ExperienceManager.Controllers;
using Jellyfin.Plugin.ExperienceManager.Home;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

public class HomeApiControllerTests
{
    private static readonly User Viewer = new("viewer", "auth", "reset") { Id = Guid.NewGuid() };

    private static readonly BaseItem Latest = HomePageServiceTests.Film("a");

    private readonly IUserManager _users = Substitute.For<IUserManager>();
    private readonly IHomePage _page = Substitute.For<IHomePage>();

    public HomeApiControllerTests()
    {
        _users.GetUserById(Viewer.Id).Returns(Viewer);
        _page.LayoutVersion.Returns("test-1");
        _page.SectionsFor(Viewer).Returns(
        [
            new PageSection("latest", "Latest", new SectionDisplay("row", "portrait"), new SectionSource(SectionSources.Latest, null, "Movie"), null, 16, _ => ([Latest], true)),
        ]);
    }

    private HomeApiController Controller(Guid? userId, string? ifNoneMatch = null)
    {
        var dto = Substitute.For<IDtoService>();
        dto.GetBaseItemDtos(Arg.Any<IReadOnlyList<BaseItem>>(), Arg.Any<DtoOptions>(), Arg.Any<User?>(), Arg.Any<BaseItem?>())
            .Returns(c => c.Arg<IReadOnlyList<BaseItem>>().Select(i => new BaseItemDto { Id = i.Id, Name = i.Name }).ToList());
        var context = new DefaultHttpContext();
        if (userId is { } id)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(HomeApiController.UserIdClaim, id.ToString())], "test"));
        }

        if (ifNoneMatch is not null)
        {
            context.Request.Headers.IfNoneMatch = ifNoneMatch;
        }

        return new HomeApiController(new HomePageService(_page, dto, NullLogger<HomePageService>.Instance), _users)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
    }

    [Fact]
    public void ThePageIsTheSignedInUsersWithAnETagThatGets304()
    {
        var controller = Controller(Viewer.Id);
        var result = controller.GetPage("home", "items", null, null, null, null, null);

        var page = Assert.IsType<PageResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("latest", page.Sections.Single().Id);
        var etag = controller.Response.Headers.ETag.ToString();
        Assert.StartsWith("\"", etag, StringComparison.Ordinal);
        Assert.Contains("latest;desc=\"hit\";dur=", controller.Response.Headers["Server-Timing"].ToString(), StringComparison.Ordinal);

        var again = Controller(Viewer.Id, etag).GetPage("home", "items", null, null, null, null, null);
        Assert.Equal(StatusCodes.Status304NotModified, Assert.IsType<StatusCodeResult>(again.Result).StatusCode);
    }

    [Fact]
    public void ARequestWithoutAUserGetsNothing()
    {
        Assert.IsType<ForbidResult>(Controller(null).GetPage("home", null, null, null, null, null, null).Result);
        Assert.IsType<ForbidResult>(Controller(Guid.NewGuid()).GetSectionItems("latest", null, null, null, null, null, null).Result);
    }

    [Fact]
    public void UnknownPagesAndSectionsAre404()
    {
        Assert.IsType<NotFoundResult>(Controller(Viewer.Id).GetPage("movies", null, null, null, null, null, null).Result);
        Assert.IsType<NotFoundResult>(Controller(Viewer.Id).GetSectionItems("nope", null, null, null, null, null, null).Result);
    }

    [Fact]
    public void InfoReportsTheVersionAndTheApiVersions()
    {
        var info = Controller(null).GetInfo().Value!;

        Assert.Equal(HomeClient.Version, info.Version);
        Assert.Equal(["v1"], info.ApiVersions);
    }

    [Fact]
    public void ListsAreReadAsJellyfinWritesThemAndUnknownNamesAreIgnored()
    {
        Assert.Equal([ItemFields.Overview, ItemFields.Genres], HomeApiController.ParseList<ItemFields>("Overview, genres,NotAField"));
        Assert.Null(HomeApiController.ParseList<ImageType>(null));
    }

    [Fact]
    public void ServerTimingNamesEachSectionWithItsCacheResult()
    {
        Assert.Equal(
            "featured;desc=\"miss\";dur=12.3, latest;desc=\"hit\";dur=0.5",
            HomeApiController.ServerTiming([new SectionTiming("featured", false, 12.34), new SectionTiming("latest", true, 0.5)]));
    }
}
