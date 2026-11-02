using System.IO;
using Jellyfin.Plugin.ExperienceManager.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

public class HomeClientControllerTests
{
    private static HomeClientController Controller(string? ifNoneMatch = null)
    {
        var context = new DefaultHttpContext();
        if (ifNoneMatch is not null)
        {
            context.Request.Headers.IfNoneMatch = ifNoneMatch;
        }

        return new HomeClientController(new HomeClient()) { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    [Fact]
    public void TheScriptIsTheEmbeddedFileWithNoCacheAndAnETag()
    {
        var controller = Controller();

        var file = Assert.IsType<FileStreamResult>(controller.GetScript());

        Assert.Equal(HomeClient.ScriptContentType, file.ContentType);
        Assert.Equal(File.ReadAllText(Path.Combine(RepoRoot(), "src", "home", "home.js")).ReplaceLineEndings(), new StreamReader(file.FileStream).ReadToEnd().ReplaceLineEndings());
        Assert.Equal("no-cache", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal(new HomeClient().ScriptETag, controller.Response.Headers.ETag.ToString());
    }

    [Fact]
    public void TheStylesheetIsServedTheSameWay()
    {
        var controller = Controller();

        var file = Assert.IsType<FileStreamResult>(controller.GetStyle());

        Assert.Equal(HomeClient.StyleContentType, file.ContentType);
        Assert.Equal(new HomeClient().StyleETag, controller.Response.Headers.ETag.ToString());
    }

    [Fact]
    public void AClientWithTheFileGets304()
    {
        var etag = new HomeClient().ScriptETag;

        var result = Assert.IsType<StatusCodeResult>(Controller("\"other\", " + etag).GetScript());

        Assert.Equal(StatusCodes.Status304NotModified, result.StatusCode);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Jellyfin.Plugin.ExperienceManager.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root");
    }
}
