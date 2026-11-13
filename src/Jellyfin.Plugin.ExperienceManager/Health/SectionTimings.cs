using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ExperienceManager.Health;

/// <summary>
/// How long a section took to load, per request.
/// </summary>
/// <param name="Id">The section's id.</param>
/// <param name="P95">The 95th percentile of its recent load times, in milliseconds.</param>
/// <param name="Requests">How many recent loads that is over.</param>
/// <param name="Title">The section's name, when it is known.</param>
public sealed record SectionSpeed(string Id, double P95, int Requests, string? Title = null);

/// <summary>
/// Each section's recent load times on the server, kept in memory, for Slow and the slowest sections.
/// </summary>
public sealed class SectionTimings
{
    /// <summary>How many recent loads are kept per section.</summary>
    public const int Kept = 50;

    /// <summary>The fewest loads a section's speed is judged on.</summary>
    public const int Fewest = 5;

    private readonly ConcurrentDictionary<string, Queue<double>> _loads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records a section's load.
    /// </summary>
    /// <param name="sectionId">The section.</param>
    /// <param name="milliseconds">How long it took.</param>
    public void Record(string sectionId, double milliseconds)
    {
        var loads = _loads.GetOrAdd(sectionId, _ => new Queue<double>());
        lock (loads)
        {
            loads.Enqueue(milliseconds);
            while (loads.Count > Kept)
            {
                loads.Dequeue();
            }
        }
    }

    /// <summary>
    /// Gets every section's speed, slowest first, for those loaded often enough to judge.
    /// </summary>
    /// <returns>The speeds.</returns>
    public IReadOnlyList<SectionSpeed> Speeds()
    {
        var speeds = new List<SectionSpeed>();
        foreach (var (id, loads) in _loads)
        {
            double[] times;
            lock (loads)
            {
                times = loads.ToArray();
            }

            if (times.Length >= Fewest)
            {
                speeds.Add(new SectionSpeed(id, P95(times), times.Length));
            }
        }

        return speeds.OrderByDescending(s => s.P95).ToList();
    }

    // The nearest-rank 95th percentile.
    internal static double P95(IReadOnlyCollection<double> times)
    {
        var sorted = times.Order().ToArray();
        var rank = (int)Math.Ceiling(0.95 * sorted.Length);
        return Math.Round(sorted[Math.Clamp(rank, 1, sorted.Length) - 1], 1);
    }
}
