using Jellyfin.Plugin.ExperienceManager.Integration;
using Xunit;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

public class IndexPageTagsTests
{
    private const string Index = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><script defer=\"defer\" src=\"main.jellyfin.bundle.js\"></script></head><body></body></html>";

    private static readonly string Version = typeof(Plugin).Assembly.GetName().Version!.ToString();

    private static readonly string Tags = "<link rel=\"stylesheet\" href=\"../ExperienceManager/home.css?v=" + Version + "\">"
        + "<script async src=\"../ExperienceManager/home.js?v=" + Version + "\"></script>";

    [Fact]
    public void IndexGetsOneStylesheetAndOneScriptTagAtTheEndOfTheHead()
    {
        var changed = IndexPageTags.AddTo(Index);

        Assert.NotNull(changed);
        Assert.Contains(Tags + "</head>", changed);
        Assert.Equal(Index, changed.Replace(Tags, string.Empty));
    }

    [Fact]
    public void TheTagsCarryThePluginVersionSoAnUpgradeChangesTheirUrls()
    {
        var changed = IndexPageTags.AddTo(Index)!;

        Assert.Contains("home.js?v=0.1.0.0\"", changed);
        Assert.Contains("home.css?v=0.1.0.0\"", changed);
    }

    [Fact]
    public void IndexIsLeftAloneWhenAlreadyChangedOrWithoutAHead()
    {
        Assert.Null(IndexPageTags.AddTo(IndexPageTags.AddTo(Index)!));
        Assert.Null(IndexPageTags.AddTo(Index.Replace("</head>", "<script async src=\"../ExperienceManager/home.js?v=0.0.9.0\"></script></head>")));
        Assert.Null(IndexPageTags.AddTo("<html><body></body></html>"));
    }
}
