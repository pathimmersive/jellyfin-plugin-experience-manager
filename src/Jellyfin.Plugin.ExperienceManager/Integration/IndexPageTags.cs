using System;

namespace Jellyfin.Plugin.ExperienceManager.Integration;

/// <summary>
/// The one change made to jellyfin-web as it is served: the home client's stylesheet and
/// script tags in index.html.
/// </summary>
public static class IndexPageTags
{
    // Relative to {base}/web/, so they resolve under any configured BaseUrl.
    private const string ScriptSource = "../" + HomeClient.Route + "/" + HomeClient.ScriptFileName;
    private const string StyleSource = "../" + HomeClient.Route + "/" + HomeClient.StyleFileName;

    // The version changes the files' URLs on every upgrade, so no client keeps running an old copy.
    // The script is async: it waits for jellyfin-web's home page itself, so it must not hold up the page.
    private static readonly string _tags =
        "<link rel=\"stylesheet\" href=\"" + StyleSource + "?v=" + HomeClient.Version + "\">"
        + "<script async src=\"" + ScriptSource + "?v=" + HomeClient.Version + "\"></script>";

    /// <summary>
    /// Adds the home client's tags to index.html, right before the end of its head.
    /// </summary>
    /// <param name="html">The served index.html.</param>
    /// <returns>The changed page, or null to leave it unchanged.</returns>
    public static string? AddTo(string html)
    {
        if (html.Contains(ScriptSource, StringComparison.Ordinal) || html.Contains(StyleSource, StringComparison.Ordinal))
        {
            return null;
        }

        var head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return head < 0 ? null : html.Insert(head, _tags);
    }
}
