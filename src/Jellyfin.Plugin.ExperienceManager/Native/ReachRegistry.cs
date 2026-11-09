using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ExperienceManager.Configuration;

namespace Jellyfin.Plugin.ExperienceManager.Native;

/// <summary>
/// Where adapters' on/off switches are kept.
/// </summary>
public interface IReachSwitches
{
    /// <summary>
    /// Gets an adapter's switch.
    /// </summary>
    /// <param name="adapterId">The adapter.</param>
    /// <returns>Whether an admin switched it on or off, or null when nobody has.</returns>
    bool? Get(string adapterId);

    /// <summary>
    /// Sets an adapter's switch.
    /// </summary>
    /// <param name="adapterId">The adapter.</param>
    /// <param name="on">Whether it is on.</param>
    void Set(string adapterId, bool on);
}

/// <summary>
/// The switches kept in the plugin's configuration, one set per server.
/// </summary>
public sealed class ConfigurationReachSwitches : IReachSwitches
{
    private readonly Func<PluginConfiguration> _configuration;
    private readonly Action _save;
    private readonly object _gate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationReachSwitches"/> class.
    /// </summary>
    /// <param name="configuration">Gets the plugin's configuration.</param>
    /// <param name="save">Saves it.</param>
    public ConfigurationReachSwitches(Func<PluginConfiguration> configuration, Action save)
    {
        _configuration = configuration;
        _save = save;
    }

    /// <inheritdoc />
    public bool? Get(string adapterId)
    {
        lock (_gate)
        {
            return _configuration().ReachSwitches.FirstOrDefault(s => s.AdapterId == adapterId)?.On;
        }
    }

    /// <inheritdoc />
    public void Set(string adapterId, bool on)
    {
        lock (_gate)
        {
            var switches = _configuration().ReachSwitches;
            var existing = switches.FirstOrDefault(s => s.AdapterId == adapterId);
            if (existing is null)
            {
                switches.Add(new ReachSwitch { AdapterId = adapterId, On = on });
            }
            else
            {
                existing.On = on;
            }

            _save();
        }
    }
}

/// <summary>
/// One adapter as the admin page shows it.
/// </summary>
/// <param name="Id">The adapter's id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Mechanism">How it reaches its clients.</param>
/// <param name="Families">The ids of the client families it reaches.</param>
/// <param name="Reaches">What it gives them.</param>
/// <param name="Built">Whether it is built.</param>
/// <param name="On">Whether it is on.</param>
/// <param name="Status">Its Detect status: ready, not installed, unsupported version, conflict, off or not built yet.</param>
/// <param name="Reason">Why, in words an admin reads.</param>
/// <param name="Version">Its target's version, if known.</param>
public sealed record ReachAdapterStatus(string Id, string Name, string Mechanism, IReadOnlyList<string> Families, string Reaches, bool Built, bool On, string Status, string Reason, string? Version);

/// <summary>
/// Every adapter the plugin has, with its switch. Writes go through here, so an adapter that is off writes nothing.
/// </summary>
public sealed class ReachRegistry
{
    private readonly IReachSwitches _switches;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReachRegistry"/> class.
    /// </summary>
    /// <param name="adapters">The adapters.</param>
    /// <param name="switches">Their switches.</param>
    public ReachRegistry(IEnumerable<IReachAdapter> adapters, IReachSwitches switches)
    {
        Adapters = adapters.ToList();
        _switches = switches;
    }

    /// <summary>
    /// Gets every adapter, in the order they were registered.
    /// </summary>
    public IReadOnlyList<IReachAdapter> Adapters { get; }

    /// <summary>
    /// Gets whether an adapter is on: built, and switched on or on by default.
    /// </summary>
    /// <param name="adapter">The adapter.</param>
    /// <returns>Whether it is on.</returns>
    public bool IsOn(IReachAdapter adapter) => adapter.Built && (_switches.Get(adapter.Id) ?? adapter.OnByDefault);

    /// <summary>
    /// Switches an adapter on or off. One not built yet cannot be switched on.
    /// </summary>
    /// <param name="adapterId">The adapter.</param>
    /// <param name="on">Whether it is on.</param>
    /// <returns>Whether the switch was set.</returns>
    public bool SetOn(string adapterId, bool on)
    {
        var adapter = Find(adapterId);
        if (adapter is null || (on && !adapter.Built))
        {
            return false;
        }

        _switches.Set(adapter.Id, on);
        return true;
    }

    /// <summary>
    /// Gets an adapter's status: not built yet, off, or what its Detect finds.
    /// </summary>
    /// <param name="adapter">The adapter.</param>
    /// <returns>Its status.</returns>
    public ReachTarget Status(IReachAdapter adapter)
    {
        if (!adapter.Built)
        {
            return new ReachTarget(ReachState.NotBuilt, null, "Planned; not built yet.");
        }

        return IsOn(adapter) ? adapter.Detect() : new ReachTarget(ReachState.Off, null, "Switched off; it writes nothing.");
    }

    /// <summary>
    /// Carries out a plan through its adapter, only when the adapter is on.
    /// </summary>
    /// <param name="adapterId">The adapter.</param>
    /// <param name="plan">The plan.</param>
    /// <returns>What it did.</returns>
    public ReachApplied Apply(string adapterId, ReachPlan plan)
    {
        var adapter = Find(adapterId);
        if (adapter is null)
        {
            return new ReachApplied(0, "No such adapter.");
        }

        var status = Status(adapter);
        return status.State is ReachState.Off or ReachState.NotBuilt ? new ReachApplied(0, status.Reason) : adapter.Apply(plan);
    }

    /// <summary>
    /// Gets every adapter's status, for the admin page.
    /// </summary>
    /// <returns>The statuses, in registration order.</returns>
    public IReadOnlyList<ReachAdapterStatus> Statuses() => Adapters
        .Select(a =>
        {
            var status = Status(a);
            return new ReachAdapterStatus(a.Id, a.Name, a.Mechanism.ToString(), a.Families, a.Reaches, a.Built, IsOn(a), status.Label, status.Reason, status.Version);
        })
        .ToList();

    private IReachAdapter? Find(string adapterId) =>
        Adapters.FirstOrDefault(a => string.Equals(a.Id, adapterId, StringComparison.OrdinalIgnoreCase));
}
