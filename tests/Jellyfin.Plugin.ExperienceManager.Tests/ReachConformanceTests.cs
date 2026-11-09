using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ExperienceManager.Home;
using Jellyfin.Plugin.ExperienceManager.Native;
using Jellyfin.Plugin.ExperienceManager.Native.Adapters;
using Xunit;
using Xunit.Sdk;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

/// <summary>
/// An adapter under test, with a view of its target: what is there now, what it did not write, and a way to
/// make the target's version supported or not.
/// </summary>
public sealed record ConformanceCase(string Name, IReachAdapter Adapter, Func<string> Target, Func<string> Foreign, Action<bool> Supported)
{
    public override string ToString() => Name;
}

/// <summary>
/// Shared pieces for the reach tests: switches kept in memory, a registry of the plugin's own adapters, a
/// resolved experience, and every case the conformance suite runs.
/// </summary>
public static class ReachKit
{
    public static readonly Version Supported = new(12, 2, 0);

    public static readonly Version Unsupported = new(10, 10, 7);

    public static ResolvedExperience Experience { get; } = new("home", "test-1", "web",
    [
        new ResolvedSection("featured", "Featured", new SectionDisplay("feature", "landscape"), new SectionSource(SectionSources.Featured), null),
        new ResolvedSection("continue", "Continue Watching", new SectionDisplay("row", "landscape"), new SectionSource(SectionSources.ContinueWatching), null),
        new ResolvedSection("latest-films", "Latest Movies", new SectionDisplay("row", "portrait"), new SectionSource(SectionSources.Latest, "films", "Movie"), null),
        new ResolvedSection("top10", "Top 10", new SectionDisplay("row", "portrait"), new SectionSource(SectionSources.Section), null),
    ]);

    public static ReachRegistry Registry(IReachSwitches? switches = null, params IReachAdapter[] extra) =>
        new(ReachAdapters.Create(() => Supported).Concat(extra), switches ?? new MemorySwitches());

    /// <summary>
    /// Every adapter the plugin registers, each with a server version it can change, plus a fake write adapter.
    /// </summary>
    public static IEnumerable<ConformanceCase> Cases()
    {
        var version = Supported;
        foreach (var adapter in ReachAdapters.Create(() => version))
        {
            yield return new ConformanceCase(adapter.Id, adapter, () => string.Empty, () => string.Empty, ok => version = ok ? Supported : Unsupported);
        }

        var target = new FakeTarget();
        yield return new ConformanceCase("fake-write", new FakeWriteAdapter(target), target.Snapshot, target.ForeignSnapshot, ok => target.Version = ok ? "1.1.0" : "0.9.0");
    }

    public static TheoryData<string> CaseNames() => new(Cases().Select(c => c.Name));

    public static ConformanceCase Case(string name) => Cases().Single(c => c.Name == name);
}

public sealed class MemorySwitches : IReachSwitches
{
    private readonly Dictionary<string, bool> _switches = [];

    public bool? Get(string adapterId) => _switches.TryGetValue(adapterId, out var on) ? on : null;

    public void Set(string adapterId, bool on) => _switches[adapterId] = on;
}

/// <summary>
/// A companion plugin's home rows, held in memory: rows the fake adapter writes, and a row the admin made there by hand.
/// </summary>
public sealed class FakeTarget
{
    public string Version { get; set; } = "1.1.0";

    public Dictionary<string, string> Rows { get; } = new() { ["admin-row"] = "made by hand" };

    public string Snapshot() => string.Join(";", Rows.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => r.Key + "=" + r.Value));

    public string ForeignSnapshot() => Rows["admin-row"];
}

/// <summary>
/// A Bridge adapter that writes one endpoint row per section, standing in for the companion-plugin bridges.
/// It supports target version 1.x only.
/// </summary>
public sealed class FakeWriteAdapter : ReachAdapter
{
    private readonly FakeTarget _target;
    private readonly Dictionary<string, string> _written = [];

    public FakeWriteAdapter(FakeTarget target)
    {
        _target = target;
    }

    public override string Id => "fake-write";

    public override string Name => "Fake home rows";

    public override ReachMechanism Mechanism => ReachMechanism.Bridge;

    public override IReadOnlyList<string> Families { get; } = ["wholphin"];

    public override string Reaches => "Sections as rows.";

    public override ReachCapabilities Capabilities { get; } = new([SectionSources.ContinueWatching, SectionSources.Latest], true, false, true, true, true, null);

    public override ReachTarget Detect() => _target.Version.StartsWith("1.", StringComparison.Ordinal)
        ? new ReachTarget(ReachState.Ready, _target.Version, "Installed.")
        : new ReachTarget(ReachState.UnsupportedVersion, _target.Version, $"Version {_target.Version} is not supported.");

    public override IReadOnlyList<ReachOperation> Drift() => _written
        .Where(w => !_target.Rows.TryGetValue(w.Key, out var value) || value != w.Value)
        .Select(w => new ReachOperation(w.Key, "write-row", w.Value))
        .ToList();

    protected override ReachPlan PlanFor(ResolvedExperience experience)
    {
        var operations = new List<ReachOperation>();
        var losses = new List<ReachLoss>();
        foreach (var section in experience.Sections)
        {
            if (section.Display.Hint == "feature")
            {
                losses.Add(new ReachLoss(section.Id, "hero shown as a row"));
            }

            var native = Capabilities.SourceKinds.Contains(section.Source.Kind);
            operations.Add(new ReachOperation("em-" + section.Id, "write-row", native ? "native:" + section.Source.Kind : "endpoint:/ExperienceManager/v1/Sections/" + section.Id + "/Items"));
        }

        return new ReachPlan(operations, losses, null);
    }

    protected override ReachApplied ApplyPlan(ReachPlan plan)
    {
        var changed = 0;
        foreach (var operation in plan.Operations)
        {
            if (!_target.Rows.TryGetValue(operation.SectionId, out var value) || value != operation.Detail)
            {
                _target.Rows[operation.SectionId] = operation.Detail;
                changed++;
            }

            _written[operation.SectionId] = operation.Detail;
        }

        return new ReachApplied(changed, null);
    }
}

/// <summary>
/// The checks every adapter passes. Each throws when an adapter breaks the contract.
/// </summary>
public static class ReachConformance
{
    public static void PlanIsPureAndRepeatable(ConformanceCase c)
    {
        var before = c.Target();
        var target = c.Adapter.Detect();
        var first = c.Adapter.Plan(ReachKit.Experience, target);
        var second = c.Adapter.Plan(ReachKit.Experience, target);

        Assert.True(first.Matches(second), $"{c.Name}: Plan gave different operations or losses for the same input");
        Assert.Equal(before, c.Target());
    }

    public static void ASecondApplyChangesNothing(ConformanceCase c)
    {
        var plan = c.Adapter.Plan(ReachKit.Experience, c.Adapter.Detect());
        c.Adapter.Apply(plan);
        var afterFirst = c.Target();

        var second = c.Adapter.Apply(plan);

        Assert.True(second.Changed == 0, $"{c.Name}: a second Apply changed {second.Changed} things");
        Assert.Equal(afterFirst, c.Target());
    }

    public static void DriftIsEmptyAfterApply(ConformanceCase c)
    {
        c.Adapter.Apply(c.Adapter.Plan(ReachKit.Experience, c.Adapter.Detect()));

        Assert.Empty(c.Adapter.Drift());
    }

    public static void ApplyTouchesOnlyWhatItWrote(ConformanceCase c)
    {
        var foreign = c.Foreign();
        c.Adapter.Apply(c.Adapter.Plan(ReachKit.Experience, c.Adapter.Detect()));

        Assert.Equal(foreign, c.Foreign());
    }

    public static void AnUnsupportedTargetMeansNothingIsDone(ConformanceCase c)
    {
        var planWhileSupported = c.Adapter.Plan(ReachKit.Experience, c.Adapter.Detect());
        c.Supported(false);
        var before = c.Target();

        var target = c.Adapter.Detect();
        var plan = c.Adapter.Plan(ReachKit.Experience, target);
        var applied = c.Adapter.Apply(planWhileSupported);

        Assert.NotEqual(ReachState.Ready, target.State);
        Assert.False(string.IsNullOrWhiteSpace(target.Reason));
        Assert.Empty(plan.Operations);
        Assert.False(string.IsNullOrWhiteSpace(plan.Refused));
        Assert.Equal(0, applied.Changed);
        Assert.False(string.IsNullOrWhiteSpace(applied.Refused));
        Assert.Equal(before, c.Target());
    }
}

public class ReachConformanceTests
{
    [Theory]
    [MemberData(nameof(ReachKit.CaseNames), MemberType = typeof(ReachKit))]
    public void Plan_is_pure_and_repeatable(string adapter) => ReachConformance.PlanIsPureAndRepeatable(ReachKit.Case(adapter));

    [Theory]
    [MemberData(nameof(ReachKit.CaseNames), MemberType = typeof(ReachKit))]
    public void A_second_apply_changes_nothing(string adapter) => ReachConformance.ASecondApplyChangesNothing(ReachKit.Case(adapter));

    [Theory]
    [MemberData(nameof(ReachKit.CaseNames), MemberType = typeof(ReachKit))]
    public void Drift_is_empty_after_apply(string adapter) => ReachConformance.DriftIsEmptyAfterApply(ReachKit.Case(adapter));

    [Theory]
    [MemberData(nameof(ReachKit.CaseNames), MemberType = typeof(ReachKit))]
    public void Apply_touches_only_what_it_wrote(string adapter) => ReachConformance.ApplyTouchesOnlyWhatItWrote(ReachKit.Case(adapter));

    [Theory]
    [MemberData(nameof(ReachKit.CaseNames), MemberType = typeof(ReachKit))]
    public void An_unsupported_target_means_nothing_is_done(string adapter) => ReachConformance.AnUnsupportedTargetMeansNothingIsDone(ReachKit.Case(adapter));

    [Fact]
    public void The_suite_runs_every_adapter_the_plugin_registers()
    {
        var registered = ReachAdapters.Create(() => ReachKit.Supported).Select(a => a.Id);
        var cases = ReachKit.Cases().Select(c => c.Name).ToHashSet();
        Assert.Subset(cases, registered.ToHashSet());
        Assert.Contains("fake-write", cases);
    }

    [Fact]
    public void The_suite_catches_an_apply_that_is_not_idempotent()
    {
        var target = new FakeTarget();
        var broken = new ConformanceCase("broken", new AppendingAdapter(target), target.Snapshot, target.ForeignSnapshot, _ => { });

        Assert.ThrowsAny<XunitException>(() => ReachConformance.ASecondApplyChangesNothing(broken));
    }

    [Fact]
    public void The_fake_write_adapter_lists_what_is_lost_and_reads_sections_it_cannot_show_from_their_endpoint()
    {
        var plan = new FakeWriteAdapter(new FakeTarget()).Plan(ReachKit.Experience, new ReachTarget(ReachState.Ready, "1.1.0", "Installed."));

        Assert.Equal([new ReachLoss("featured", "hero shown as a row")], plan.Losses);
        Assert.Equal("native:continue", plan.Operations.Single(o => o.SectionId == "em-continue").Detail);
        Assert.Equal("endpoint:/ExperienceManager/v1/Sections/top10/Items", plan.Operations.Single(o => o.SectionId == "em-top10").Detail);
    }

    // Writes a new row on every Apply: the kind of adapter the suite must refuse.
    private sealed class AppendingAdapter(FakeTarget target) : ReachAdapter
    {
        private int _applies;

        public override string Id => "broken";

        public override string Name => "Broken";

        public override ReachMechanism Mechanism => ReachMechanism.Bridge;

        public override IReadOnlyList<string> Families { get; } = ["wholphin"];

        public override string Reaches => "Rows.";

        public override ReachCapabilities Capabilities => ReachCapabilities.None;

        public override ReachTarget Detect() => new(ReachState.Ready, target.Version, "Installed.");

        protected override ReachPlan PlanFor(ResolvedExperience experience) => new([new ReachOperation("row", "write-row", "x")], [], null);

        protected override ReachApplied ApplyPlan(ReachPlan plan)
        {
            target.Rows["row-" + ++_applies] = "x";
            return new ReachApplied(1, null);
        }
    }
}
