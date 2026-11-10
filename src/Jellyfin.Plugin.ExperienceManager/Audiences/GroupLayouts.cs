using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ExperienceManager.Layouts;

namespace Jellyfin.Plugin.ExperienceManager.Audiences;

/// <summary>
/// How a section stands in a group's layout.
/// </summary>
public static class GroupModes
{
    /// <summary>The server layout's section, as it is there.</summary>
    public const string Inherited = "inherited";

    /// <summary>The group's own copy of a server section, under the same id.</summary>
    public const string Changed = "changed";

    /// <summary>A section only in the group's layout.</summary>
    public const string Added = "added";

    /// <summary>A server section the group does not show.</summary>
    public const string Removed = "removed";

    /// <summary>Every mode.</summary>
    public static readonly IReadOnlyList<string> All = [Inherited, Changed, Added, Removed];
}

/// <summary>
/// One entry of a group's layout: a section id and how the group has it.
/// </summary>
/// <param name="Id">The section's id.</param>
/// <param name="Mode">One of <see cref="GroupModes"/>.</param>
/// <param name="Section">The group's own section, for changed and added entries.</param>
public sealed record GroupEntry(string Id, string Mode, StoredSection? Section = null);

/// <summary>
/// A section of a group's layout as the admin sees it, with its mark.
/// </summary>
/// <param name="Section">The section shown, or the server's for a removed one.</param>
/// <param name="Mark">One of <see cref="GroupModes"/>.</param>
public sealed record MarkedSection(StoredSection Section, string Mark);

/// <summary>
/// Group layouts store only whole-section differences from the server layout; these merge them back.
/// </summary>
public static class GroupLayouts
{
    /// <summary>
    /// Resolves a group's sections: its entries in their order, and every server section the group never mentions
    /// after the server section before it, so a section added to the server layout later reaches the group.
    /// </summary>
    /// <param name="server">The server layout's sections.</param>
    /// <param name="entries">The group's entries, in its order.</param>
    /// <returns>The sections the group's members see, with their marks; removed sections are listed where the group put them.</returns>
    public static IReadOnlyList<MarkedSection> Merge(IReadOnlyList<StoredSection> server, IReadOnlyList<GroupEntry> entries)
    {
        var byId = server.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        var mentioned = new HashSet<string>(entries.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        var merged = new List<MarkedSection>();
        foreach (var entry in entries)
        {
            switch (entry.Mode)
            {
                case GroupModes.Inherited or GroupModes.Removed when byId.TryGetValue(entry.Id, out var serverSection):
                    merged.Add(new MarkedSection(serverSection, entry.Mode));
                    break;
                case GroupModes.Changed or GroupModes.Added when entry.Section is not null:
                    merged.Add(new MarkedSection(entry.Section with { Id = entry.Id }, entry.Mode));
                    break;
            }
        }

        // Each unmentioned server section goes after the last server section before it that the group lists.
        for (var i = 0; i < server.Count; i++)
        {
            if (mentioned.Contains(server[i].Id))
            {
                continue;
            }

            var at = 0;
            for (var j = i - 1; j >= 0; j--)
            {
                var found = merged.FindIndex(m => string.Equals(m.Section.Id, server[j].Id, StringComparison.OrdinalIgnoreCase));
                if (found >= 0)
                {
                    at = found + 1;
                    break;
                }
            }

            merged.Insert(at, new MarkedSection(server[i], GroupModes.Inherited));
        }

        return merged;
    }

    /// <summary>
    /// Gets the sections a group's members see.
    /// </summary>
    /// <param name="server">The server layout's sections.</param>
    /// <param name="entries">The group's entries.</param>
    /// <returns>The sections, in order, without removed ones.</returns>
    public static IReadOnlyList<StoredSection> Resolve(IReadOnlyList<StoredSection> server, IReadOnlyList<GroupEntry> entries) =>
        Merge(server, entries).Where(m => m.Mark != GroupModes.Removed).Select(m => m.Section).ToList();

    /// <summary>
    /// Checks a group's entries against the server layout.
    /// </summary>
    /// <param name="server">The server layout's sections.</param>
    /// <param name="entries">The entries.</param>
    /// <returns>Why they cannot be stored, or null.</returns>
    public static string? Validate(IReadOnlyList<StoredSection> server, IReadOnlyList<GroupEntry>? entries)
    {
        if (entries is null)
        {
            return "entries are required";
        }

        var ids = new HashSet<string>(server.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Id))
            {
                return "every entry needs an id";
            }

            if (!GroupModes.All.Contains(entry.Mode))
            {
                return $"entry {entry.Id} has an unknown mode '{entry.Mode}'";
            }

            var onServer = ids.Contains(entry.Id);
            if (entry.Mode is GroupModes.Inherited or GroupModes.Removed or GroupModes.Changed && !onServer)
            {
                return $"entry {entry.Id} is {entry.Mode} but the server layout has no such section";
            }

            if (entry.Mode == GroupModes.Added && onServer)
            {
                return $"entry {entry.Id} is added but the server layout already has it; use changed";
            }

            if (entry.Mode is GroupModes.Changed or GroupModes.Added)
            {
                if (entry.Section is null)
                {
                    return $"entry {entry.Id} gives its section";
                }

                if (PageStore.Validate([entry.Section with { Id = entry.Id }]) is { } invalid)
                {
                    return invalid;
                }
            }
        }

        var duplicate = entries.GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            return $"entry {duplicate.Key} is listed twice";
        }

        var resolved = Resolve(server, entries);
        return resolved.Count(s => s.Type == SectionTypes.LatestLibraries) > 1 ? $"a page holds {SectionTypes.LatestLibraries} once" : null;
    }
}
