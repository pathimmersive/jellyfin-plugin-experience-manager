using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ExperienceManager.Native;

/// <summary>
/// How an adapter reaches its clients.
/// </summary>
public enum ReachMechanism
{
    /// <summary>Clients pull from an endpoint the plugin serves.</summary>
    Serve,

    /// <summary>Writes a companion plugin's configuration.</summary>
    Bridge,

    /// <summary>Writes Jellyfin's own data: display preferences, collections, library order.</summary>
    Native,

    /// <summary>Rewrites Jellyfin's own responses.</summary>
    Shape,
}

/// <summary>
/// The state of an adapter's target, as <see cref="IReachAdapter.Detect"/> finds it.
/// </summary>
public enum ReachState
{
    /// <summary>The target is present and supported.</summary>
    Ready,

    /// <summary>The target is not installed.</summary>
    NotInstalled,

    /// <summary>The target's version is unknown or unsupported.</summary>
    UnsupportedVersion,

    /// <summary>Something installed beside the target conflicts with it.</summary>
    Conflict,

    /// <summary>The adapter is switched off.</summary>
    Off,

    /// <summary>The adapter is planned but not built yet.</summary>
    NotBuilt,
}

/// <summary>
/// What an adapter found on its target.
/// </summary>
/// <param name="State">The target's state.</param>
/// <param name="Version">The target's version, if known.</param>
/// <param name="Reason">Why, in words an admin reads.</param>
public sealed record ReachTarget(ReachState State, string? Version, string Reason)
{
    /// <summary>Gets the state as the admin page shows it.</summary>
    public string Label => State switch
    {
        ReachState.Ready => "ready",
        ReachState.NotInstalled => "not installed",
        ReachState.UnsupportedVersion => "unsupported version",
        ReachState.Conflict => "conflict",
        ReachState.Off => "off",
        _ => "not built yet",
    };
}

/// <summary>
/// What an adapter's target can show.
/// </summary>
/// <param name="SourceKinds">The <see cref="Home.SectionSources"/> kinds it shows natively.</param>
/// <param name="EndpointRows">Whether it can show a row read from an endpoint.</param>
/// <param name="Hero">Whether it can show the featured banner as a banner.</param>
/// <param name="Order">Whether it follows the layout's section order.</param>
/// <param name="PerUser">Whether it can differ from user to user.</param>
/// <param name="Live">Whether changes reach it on the next request, without a re-apply.</param>
/// <param name="RowLimit">How many sections it shows at most, or null for no limit.</param>
public sealed record ReachCapabilities(IReadOnlyList<string> SourceKinds, bool EndpointRows, bool Hero, bool Order, bool PerUser, bool Live, int? RowLimit)
{
    /// <summary>Gets the capabilities of an adapter not built yet: none.</summary>
    public static ReachCapabilities None { get; } = new([], false, false, false, false, false, null);
}

/// <summary>
/// One thing an adapter would do on its target.
/// </summary>
/// <param name="SectionId">The section it concerns.</param>
/// <param name="Action">What it does, such as <c>serve</c> or <c>write-row</c>.</param>
/// <param name="Detail">How, in words an admin reads.</param>
public sealed record ReachOperation(string SectionId, string Action, string Detail);

/// <summary>
/// Something the layout has that a target cannot show.
/// </summary>
/// <param name="SectionId">The section.</param>
/// <param name="What">What is lost, such as "hero shown as a row".</param>
public sealed record ReachLoss(string SectionId, string What);

/// <summary>
/// What an adapter would do: its operations and what is lost, or why it does nothing.
/// </summary>
/// <param name="Operations">The operations, in order.</param>
/// <param name="Losses">What the target cannot show.</param>
/// <param name="Refused">Why the adapter does nothing, or null.</param>
public sealed record ReachPlan(IReadOnlyList<ReachOperation> Operations, IReadOnlyList<ReachLoss> Losses, string? Refused)
{
    /// <summary>
    /// A plan that does nothing, with the reason.
    /// </summary>
    /// <param name="reason">Why.</param>
    /// <returns>The plan.</returns>
    public static ReachPlan Refuse(string reason) => new([], [], reason);

    /// <summary>
    /// Whether two plans hold the same operations, losses and reason.
    /// </summary>
    /// <param name="other">The other plan.</param>
    /// <returns>Whether they are the same.</returns>
    public bool Matches(ReachPlan other) =>
        Operations.SequenceEqual(other.Operations) && Losses.SequenceEqual(other.Losses) && Refused == other.Refused;
}

/// <summary>
/// What an Apply did.
/// </summary>
/// <param name="Changed">How many things it changed on the target.</param>
/// <param name="Refused">Why it did nothing, or null.</param>
public sealed record ReachApplied(int Changed, string? Refused);

/// <summary>
/// One way of reaching clients. Every adapter passes the same conformance suite: Plan is pure and
/// repeatable, a second Apply changes nothing, Drift is empty after Apply, and a target it does not
/// support makes Plan and Apply do nothing, with the reason.
/// </summary>
public interface IReachAdapter
{
    /// <summary>Gets the adapter's stable id.</summary>
    string Id { get; }

    /// <summary>Gets the name an admin knows it by.</summary>
    string Name { get; }

    /// <summary>Gets how it reaches its clients.</summary>
    ReachMechanism Mechanism { get; }

    /// <summary>Gets the ids of the client families it reaches.</summary>
    IReadOnlyList<string> Families { get; }

    /// <summary>Gets what it gives those clients, in words an admin reads.</summary>
    string Reaches { get; }

    /// <summary>Gets a value indicating whether it is built; one that is not can never be switched on.</summary>
    bool Built { get; }

    /// <summary>Gets a value indicating whether it is on before an admin switches it.</summary>
    bool OnByDefault { get; }

    /// <summary>Gets what its target can show.</summary>
    ReachCapabilities Capabilities { get; }

    /// <summary>
    /// Finds whether the target is present and which version it is.
    /// </summary>
    /// <returns>What was found.</returns>
    ReachTarget Detect();

    /// <summary>
    /// Works out what the adapter would do for a resolved experience. Pure: it reads and writes nothing.
    /// </summary>
    /// <param name="experience">The resolved experience.</param>
    /// <param name="target">What <see cref="Detect"/> found.</param>
    /// <returns>The plan.</returns>
    ReachPlan Plan(ResolvedExperience experience, ReachTarget target);

    /// <summary>
    /// Carries out a plan. Idempotent; it touches only what the adapter wrote before.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <returns>What it did.</returns>
    ReachApplied Apply(ReachPlan plan);

    /// <summary>
    /// Finds what changed on the target since the last Apply.
    /// </summary>
    /// <returns>The differences, as the operations that would undo them.</returns>
    IReadOnlyList<ReachOperation> Drift();
}

/// <summary>
/// The base every adapter builds on. It holds the gates the contract requires: no plan and no write
/// unless the target is ready, checked again at Apply.
/// </summary>
public abstract class ReachAdapter : IReachAdapter
{
    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract ReachMechanism Mechanism { get; }

    /// <inheritdoc />
    public abstract IReadOnlyList<string> Families { get; }

    /// <inheritdoc />
    public abstract string Reaches { get; }

    /// <inheritdoc />
    public virtual bool Built => true;

    /// <inheritdoc />
    public virtual bool OnByDefault => false;

    /// <inheritdoc />
    public abstract ReachCapabilities Capabilities { get; }

    /// <inheritdoc />
    public abstract ReachTarget Detect();

    /// <inheritdoc />
    public ReachPlan Plan(ResolvedExperience experience, ReachTarget target) =>
        target.State == ReachState.Ready ? PlanFor(experience) : ReachPlan.Refuse(target.Reason);

    /// <inheritdoc />
    public ReachApplied Apply(ReachPlan plan)
    {
        if (plan.Refused is not null)
        {
            return new ReachApplied(0, plan.Refused);
        }

        var target = Detect();
        return target.State == ReachState.Ready ? ApplyPlan(plan) : new ReachApplied(0, target.Reason);
    }

    /// <inheritdoc />
    public virtual IReadOnlyList<ReachOperation> Drift() => [];

    /// <summary>
    /// Works out the plan for a ready target. Pure.
    /// </summary>
    /// <param name="experience">The resolved experience.</param>
    /// <returns>The plan.</returns>
    protected abstract ReachPlan PlanFor(ResolvedExperience experience);

    /// <summary>
    /// Carries out a plan on a ready target. Idempotent.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <returns>What it did.</returns>
    protected abstract ReachApplied ApplyPlan(ReachPlan plan);
}
