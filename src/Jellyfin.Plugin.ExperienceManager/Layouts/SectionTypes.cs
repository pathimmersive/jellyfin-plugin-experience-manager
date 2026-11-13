using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.ExperienceManager.Layouts;

/// <summary>
/// The section types a page can store.
/// </summary>
public static class SectionTypes
{
    /// <summary>The featured banner.</summary>
    public const string Featured = "featured";

    /// <summary>The user's libraries.</summary>
    public const string MyMedia = "mymedia";

    /// <summary>Items the user started and has not finished.</summary>
    public const string ContinueWatching = "continue";

    /// <summary>The next episode of each show the user is watching.</summary>
    public const string NextUp = "nextup";

    /// <summary>
    /// The latest items of each movie and show library the user can see, one section per library, in the
    /// user's library order. A library added later gets its section without an edit.
    /// </summary>
    public const string LatestLibraries = "latest-libraries";

    /// <summary>The latest items of one library: config <c>Library</c>.</summary>
    public const string Latest = "latest";

    /// <summary>
    /// A library query by genre or tag: config <c>Genres</c> and or <c>Tags</c>, optionally <c>Library</c>,
    /// <c>ItemTypes</c> (Jellyfin's item kinds, comma separated) and <c>HideWatched</c>.
    /// </summary>
    public const string Query = "query";

    /// <summary>A collection or playlist's items: config <c>Collection</c>, optionally <c>HideWatched</c>.</summary>
    public const string Collection = "collection";

    /// <summary>
    /// An announcement: config <c>Text</c> (one line, or up to three tips one per line), optionally <c>Image</c> (an
    /// uploaded image's id), <c>ButtonLabel</c> with <c>ButtonTarget</c> (an item, collection or library id) and
    /// <c>Dismissible</c>.
    /// </summary>
    public const string Announcement = "announcement";

    /// <summary>Every type, in the order the admin API lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Featured, MyMedia, ContinueWatching, NextUp, LatestLibraries, Latest, Query, Collection, Announcement];

    /// <summary>
    /// Checks a section's config for its type.
    /// </summary>
    /// <param name="section">The section.</param>
    /// <returns>Why it cannot be used, or null.</returns>
    public static string? ValidateConfig(StoredSection section)
    {
        var config = new SectionConfig(section.Config);
        if (config.Strings("RulesOff").FirstOrDefault(r => !Rules.RuleIds.All.Contains(r)) is { } unknown)
        {
            return $"section {section.Id} opts out of an unknown rule '{unknown}'";
        }

        return section.Type switch
        {
            Latest when config.Guid("Library") is null => $"section {section.Id} needs a Library id",
            Query when config.Strings("Genres").Count + config.Strings("Tags").Count == 0 => $"section {section.Id} needs Genres or Tags",
            Query when config.Text("Library") is not null && config.Guid("Library") is null => $"section {section.Id} has a Library that is not an id",
            Query when config.Text("ItemTypes") is not null && config.ItemTypes() is null => $"section {section.Id} has an unknown item type",
            Collection when config.Guid("Collection") is null => $"section {section.Id} needs a Collection id",
            Announcement => Announcements.AnnouncementContent.Validate(section),
            _ => null,
        };
    }
}

/// <summary>
/// Reads a section's config, ignoring the case of its keys.
/// </summary>
/// <param name="Config">The config, or null.</param>
public readonly record struct SectionConfig(JsonObject? Config)
{
    /// <summary>Gets a text value.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The value, or null.</returns>
    public string? Text(string key) => Node(key) is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    /// <summary>Gets an id.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The id, or null.</returns>
    public Guid? Guid(string key) => System.Guid.TryParse(Text(key), out var id) && !id.Equals(System.Guid.Empty) ? id : null;

    /// <summary>Gets a flag.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The flag, false when missing.</returns>
    public bool Flag(string key) => Node(key) is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>Gets a list of text values.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The values, none when missing.</returns>
    public IReadOnlyList<string> Strings(string key) =>
        Node(key) is JsonArray a
            ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null).OfType<string>().Where(s => s.Length > 0).ToList()
            : [];

    /// <summary>Gets the item kinds in <c>ItemTypes</c>.</summary>
    /// <returns>The kinds, none when missing, or null if one is unknown.</returns>
    public IReadOnlyList<BaseItemKind>? ItemTypes()
    {
        var text = Text("ItemTypes");
        if (text is null)
        {
            return [];
        }

        var kinds = new List<BaseItemKind>();
        foreach (var name in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<BaseItemKind>(name, true, out var kind) || int.TryParse(name, out _))
            {
                return null;
            }

            kinds.Add(kind);
        }

        return kinds;
    }

    private JsonNode? Node(string key) =>
        Config?.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
}
