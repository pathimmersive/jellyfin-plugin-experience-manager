using System.Collections.Generic;

namespace Jellyfin.Plugin.ExperienceManager.Native.Adapters.Planned;

/// <summary>
/// An adapter that is planned but not built: it shows in the reach matrix, does nothing, and cannot be switched on.
/// </summary>
public sealed class PlannedAdapter : ReachAdapter
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PlannedAdapter"/> class.
    /// </summary>
    /// <param name="id">The adapter's id.</param>
    /// <param name="name">Its name.</param>
    /// <param name="mechanism">How it will reach its clients.</param>
    /// <param name="families">The client families it will reach.</param>
    /// <param name="reaches">What it will give them.</param>
    public PlannedAdapter(string id, string name, ReachMechanism mechanism, IReadOnlyList<string> families, string reaches)
    {
        Id = id;
        Name = name;
        Mechanism = mechanism;
        Families = families;
        Reaches = reaches;
    }

    /// <summary>
    /// Gets every planned adapter.
    /// </summary>
    public static IReadOnlyList<PlannedAdapter> All { get; } =
    [
        new("moonfin-rows", "Moonfin home rows", ReachMechanism.Serve, ["moonfin"], "Experience Manager sections as Moonfin home rows."),
        new("wholphin-rows", "Wholphin home rows", ReachMechanism.Bridge, ["wholphin"], "Experience Manager sections as Wholphin home rows."),
        new("streamyfin-sections", "Streamyfin home sections", ReachMechanism.Bridge, ["streamyfin"], "Experience Manager sections as Streamyfin home sections."),
        new("stock-home-sections", "Stock home sections", ReachMechanism.Native, ["androidtv", "roku"], "Stock home sections written from the layout."),
        new("maintained-collections", "Maintained collections", ReachMechanism.Native, ["swiftfin", "infuse"], "Collections kept up to date from sections and events, which users can pin."),
    ];

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override ReachMechanism Mechanism { get; }

    /// <inheritdoc />
    public override IReadOnlyList<string> Families { get; }

    /// <inheritdoc />
    public override string Reaches { get; }

    /// <inheritdoc />
    public override bool Built => false;

    /// <inheritdoc />
    public override ReachCapabilities Capabilities => ReachCapabilities.None;

    /// <inheritdoc />
    public override ReachTarget Detect() => new(ReachState.NotBuilt, null, "Planned; not built yet.");

    /// <inheritdoc />
    protected override ReachPlan PlanFor(ResolvedExperience experience) => ReachPlan.Refuse("Planned; not built yet.");

    /// <inheritdoc />
    protected override ReachApplied ApplyPlan(ReachPlan plan) => new(0, "Planned; not built yet.");
}
