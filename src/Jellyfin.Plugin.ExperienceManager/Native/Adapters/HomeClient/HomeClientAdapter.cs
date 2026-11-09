using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ExperienceManager.Home;

namespace Jellyfin.Plugin.ExperienceManager.Native.Adapters.HomeClient;

/// <summary>
/// Experience Manager's own home screen: jellyfin-web, and the apps that load it, draw Home from the
/// plugin's page call. It serves; it writes nothing, so changes reach clients on their next request.
/// </summary>
public sealed class HomeClientAdapter : ReachAdapter
{
    /// <summary>The adapter's id.</summary>
    public const string AdapterId = "home-client";

    /// <summary>The oldest Jellyfin the home client is built for, as the plugin's target ABI says.</summary>
    public static readonly Version MinimumServer = new(12, 0);

    private readonly Func<Version> _serverVersion;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeClientAdapter"/> class.
    /// </summary>
    /// <param name="serverVersion">Gets the running Jellyfin's version.</param>
    public HomeClientAdapter(Func<Version> serverVersion)
    {
        _serverVersion = serverVersion;
    }

    /// <inheritdoc />
    public override string Id => AdapterId;

    /// <inheritdoc />
    public override string Name => "Experience Manager home screen";

    /// <inheritdoc />
    public override ReachMechanism Mechanism => ReachMechanism.Serve;

    /// <inheritdoc />
    public override IReadOnlyList<string> Families { get; } = ["web", "webos", "tizen", "android", "ios"];

    /// <inheritdoc />
    public override string Reaches => "The full home screen: the featured banner and every section, as the layout sets them.";

    /// <inheritdoc />
    public override bool OnByDefault => true;

    /// <inheritdoc />
    public override ReachCapabilities Capabilities { get; } = new(
        [SectionSources.Featured, SectionSources.MyMedia, SectionSources.ContinueWatching, SectionSources.NextUp, SectionSources.Latest, SectionSources.Query, SectionSources.Collection, SectionSources.Section],
        EndpointRows: true,
        Hero: true,
        Order: true,
        PerUser: true,
        Live: true,
        RowLimit: null);

    /// <inheritdoc />
    public override ReachTarget Detect()
    {
        var version = _serverVersion();
        return version >= MinimumServer
            ? new ReachTarget(ReachState.Ready, version.ToString(), "Served by this plugin to jellyfin-web and the apps that load it.")
            : new ReachTarget(ReachState.UnsupportedVersion, version.ToString(), $"Jellyfin {version} is older than {MinimumServer}, which the home screen is built for.");
    }

    /// <inheritdoc />
    protected override ReachPlan PlanFor(ResolvedExperience experience) => new(
        experience.Sections.Select(s => new ReachOperation(s.Id, "serve", s.Title)).ToList(),
        [],
        null);

    // Clients pull the page; there is nothing to write.
    /// <inheritdoc />
    protected override ReachApplied ApplyPlan(ReachPlan plan) => new(0, null);
}
