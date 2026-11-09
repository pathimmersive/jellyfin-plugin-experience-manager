using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ExperienceManager.Native.Adapters.HomeClient;
using Jellyfin.Plugin.ExperienceManager.Native.Adapters.Planned;

namespace Jellyfin.Plugin.ExperienceManager.Native.Adapters;

/// <summary>
/// Every adapter compiled into the plugin, in the order the admin page lists them. The plugin
/// registers these at start-up, and the conformance suite runs each of them.
/// </summary>
public static class ReachAdapters
{
    /// <summary>
    /// Creates every adapter.
    /// </summary>
    /// <param name="serverVersion">Gets the running Jellyfin's version.</param>
    /// <returns>The adapters.</returns>
    public static IReadOnlyList<IReachAdapter> Create(Func<Version> serverVersion) =>
        new IReachAdapter[] { new HomeClientAdapter(serverVersion) }.Concat(PlannedAdapter.All).ToList();
}
