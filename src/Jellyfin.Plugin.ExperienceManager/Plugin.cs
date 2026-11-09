using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ExperienceManager.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ExperienceManager;

/// <summary>
/// The Experience Manager plugin entry point: its identity and its admin page.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The server's application paths.</param>
    /// <param name="xmlSerializer">The server's XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the loaded plugin, whose configuration holds the reach adapters' switches.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// The plugin id, also written to meta.json by the build.
    /// </summary>
    public const string PluginId = "49cf9109-2fbf-4d2e-9eb7-6def4f1efb1e";

    /// <summary>
    /// The admin page's name, as Jellyfin serves it at /web/ConfigurationPage?name=.
    /// </summary>
    public const string AdminPageName = "ExperienceManager";

    /// <inheritdoc />
    public override string Name => "Experience Manager";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginId);

    /// <inheritdoc />
    public override string Description => "Server-side control over what every user sees on the Jellyfin home screen.";

    /// <summary>
    /// The admin page, with its own entry in the Dashboard sidebar.
    /// </summary>
    /// <returns>The plugin's pages.</returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = AdminPageName,
            DisplayName = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Web.admin.html",
            EnableInMainMenu = true,
            MenuIcon = "dashboard_customize",
        };
    }
}
