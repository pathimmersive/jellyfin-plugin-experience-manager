using System;
using System.IO;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.ExperienceManager;

/// <summary>
/// The home client's script and stylesheet, embedded in the plugin assembly.
/// </summary>
public class HomeClient
{
    /// <summary>
    /// The route the files are served under, relative to the server root.
    /// </summary>
    public const string Route = "ExperienceManager";

    /// <summary>
    /// The script's file name, under the plugin route.
    /// </summary>
    public const string ScriptFileName = "home.js";

    /// <summary>
    /// The script's content type.
    /// </summary>
    public const string ScriptContentType = "text/javascript; charset=utf-8";

    /// <summary>
    /// The stylesheet's file name, under the plugin route, beside the script.
    /// </summary>
    public const string StyleFileName = "home.css";

    /// <summary>
    /// The stylesheet's content type.
    /// </summary>
    public const string StyleContentType = "text/css; charset=utf-8";

    /// <summary>
    /// The admin page's Programming script, served beside the home client.
    /// </summary>
    public const string AdminScriptFileName = "admin-programming.js";

    private static readonly Lazy<string> _adminScriptETag = new(() => ComputeETag(AdminScriptFileName));

    private static readonly Lazy<string> _scriptETag = new(() => ComputeETag(ScriptFileName));

    private static readonly Lazy<string> _styleETag = new(() => ComputeETag(StyleFileName));

    /// <summary>
    /// Gets the plugin version, which the tags carry so an upgrade changes the files' URLs.
    /// </summary>
    public static string Version { get; } = typeof(HomeClient).Assembly.GetName().Version?.ToString() ?? "0";

    /// <summary>
    /// Gets the entity tag of the embedded script: a quoted prefix of its SHA-256.
    /// </summary>
    public string ScriptETag => _scriptETag.Value;

    /// <summary>
    /// Gets the entity tag of the embedded stylesheet, made the same way.
    /// </summary>
    public string StyleETag => _styleETag.Value;

    /// <summary>
    /// Opens the script.
    /// </summary>
    /// <returns>The script content.</returns>
    public Stream OpenScript() => OpenResource(ScriptFileName);

    /// <summary>
    /// Gets the admin Programming script's entity tag, from its content.
    /// </summary>
    public string AdminScriptETag => _adminScriptETag.Value;

    /// <summary>
    /// Opens the admin Programming script.
    /// </summary>
    /// <returns>The script.</returns>
    public Stream OpenAdminScript() => OpenResource(AdminScriptFileName);

    /// <summary>
    /// Opens the stylesheet.
    /// </summary>
    /// <returns>The stylesheet content.</returns>
    public Stream OpenStyle() => OpenResource(StyleFileName);

    private static Stream OpenResource(string name) =>
        typeof(HomeClient).Assembly.GetManifestResourceStream(name)
        ?? throw new FileNotFoundException($"{name} is not embedded in the plugin assembly.", name);

    private static string ComputeETag(string name)
    {
        using var stream = OpenResource(name);
        return "\"" + Convert.ToHexStringLower(SHA256.HashData(stream))[..16] + "\"";
    }
}
