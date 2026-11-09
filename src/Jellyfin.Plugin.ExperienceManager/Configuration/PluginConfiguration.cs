using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ExperienceManager.Configuration;

/// <summary>
/// The plugin's configuration, stored by Jellyfin once per server.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets the reach adapters an admin switched on or off. An adapter not listed takes its default:
    /// off, except Experience Manager's own home screen.
    /// </summary>
    public List<ReachSwitch> ReachSwitches { get; } = [];
}

/// <summary>
/// One reach adapter's switch.
/// </summary>
public class ReachSwitch
{
    /// <summary>Gets or sets the adapter's id.</summary>
    public string AdapterId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether it is on.</summary>
    public bool On { get; set; }
}
