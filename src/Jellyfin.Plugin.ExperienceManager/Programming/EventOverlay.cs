using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ExperienceManager.Layouts;

namespace Jellyfin.Plugin.ExperienceManager.Programming;

/// <summary>
/// One place on the page after events are applied.
/// </summary>
/// <param name="Section">What it shows.</param>
/// <param name="Feature">Whether it is shown as the featured banner.</param>
public sealed record PageSlot(StoredSection Section, bool Feature);

/// <summary>
/// Applies the active events to a page's sections, never to the stored layout.
/// </summary>
public static class EventOverlay
{
    /// <summary>
    /// Applies events, highest first: a section one event has changed is left alone by the events below it.
    /// </summary>
    /// <param name="sections">The page's sections, with Latest for each library already one section per library.</param>
    /// <param name="active">The events on now, highest first.</param>
    /// <returns>The page's places.</returns>
    public static List<PageSlot> Apply(IReadOnlyList<StoredSection> sections, IReadOnlyList<ProgrammedEvent> active)
    {
        var slots = sections.Select(s => new PageSlot(s, s.Type == SectionTypes.Featured)).ToList();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var insertedAfter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int Find(string? id) => id is null ? -1 : slots.FindIndex(s => string.Equals(s.Section.Id, id, StringComparison.OrdinalIgnoreCase));

        foreach (var change in active.SelectMany(e => e.Changes))
        {
            switch (change.Kind)
            {
                case ChangeKinds.Hide:
                    var hidden = Find(change.Target);
                    if (hidden >= 0 && claimed.Add(slots[hidden].Section.Id))
                    {
                        slots.RemoveAt(hidden);
                    }

                    break;
                case ChangeKinds.Swap or ChangeKinds.Featured:
                    var at = change.Kind == ChangeKinds.Featured && change.Target is null ? slots.FindIndex(s => s.Feature) : Find(change.Target);
                    if (at >= 0 && claimed.Add(slots[at].Section.Id))
                    {
                        var place = slots[at];
                        slots[at] = new PageSlot(change.Section! with { Id = place.Section.Id }, place.Feature || change.Kind == ChangeKinds.Featured);
                    }

                    break;
                case ChangeKinds.Insert:
                    var section = change.Section!;
                    if (Find(section.Id) >= 0 || !claimed.Add(section.Id))
                    {
                        break;
                    }

                    // After its anchor and the sections higher events put there; without one, after the banner.
                    var anchor = Find(change.After);
                    var key = anchor >= 0 ? slots[anchor].Section.Id : string.Empty;
                    if (anchor < 0)
                    {
                        anchor = slots.FindIndex(s => s.Feature);
                    }

                    var before = insertedAfter.GetValueOrDefault(key);
                    insertedAfter[key] = before + 1;
                    slots.Insert(Math.Min(anchor + 1 + before, slots.Count), new PageSlot(section, false));
                    break;
            }
        }

        return slots;
    }

    /// <summary>
    /// Says, one line per pair, where two published events change the same section while both are on.
    /// </summary>
    /// <param name="events">The events, highest first.</param>
    /// <param name="sections">The page's stored sections.</param>
    /// <param name="now">The server's local time, from which to look ahead.</param>
    /// <returns>The lines.</returns>
    public static IReadOnlyList<string> Conflicts(IReadOnlyList<ProgrammedEvent> events, IReadOnlyList<StoredSection> sections, DateTime now)
    {
        var featured = sections.FirstOrDefault(s => s.Type == SectionTypes.Featured)?.Id ?? SectionTypes.Featured;
        var published = events.Where(e => e.Published).ToList();
        var lines = new List<string>();
        for (var i = 0; i < published.Count; i++)
        {
            for (var j = i + 1; j < published.Count; j++)
            {
                var (higher, lower) = (published[i], published[j]);
                var shared = Touched(higher, featured).Intersect(Touched(lower, featured), StringComparer.OrdinalIgnoreCase).ToList();
                if (shared.Count > 0 && Overlap(higher.Schedule, lower.Schedule, now))
                {
                    lines.Add($"{higher.Name} and {lower.Name} both change {string.Join(", ", shared)}; {higher.Name} wins, as it is higher in the list.");
                }
            }
        }

        return lines;
    }

    private static IEnumerable<string> Touched(ProgrammedEvent value, string featured) =>
        value.Changes.Select(c => c.Kind switch
        {
            ChangeKinds.Insert => c.Section?.Id,
            ChangeKinds.Featured => c.Target ?? featured,
            _ => c.Target,
        }).OfType<string>();

    // Two schedules are on together if both are on now or at a moment one of them changes: where their times
    // meet, the later start is such a moment.
    private static bool Overlap(EventSchedule a, EventSchedule b, DateTime now)
    {
        var until = now + Schedules.Horizon;
        return new[] { now }
            .Concat(Schedules.Boundaries(a, now, until))
            .Concat(Schedules.Boundaries(b, now, until))
            .Any(t => Schedules.IsActive(a, t) && Schedules.IsActive(b, t));
    }
}
