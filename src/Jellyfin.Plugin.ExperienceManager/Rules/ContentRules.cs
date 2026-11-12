using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.ExperienceManager.Layouts;

namespace Jellyfin.Plugin.ExperienceManager.Rules;

/// <summary>
/// The ids a section names to opt out of a rule.
/// </summary>
public static class RuleIds
{
    /// <summary>Items with an excluded tag are never shown.</summary>
    public const string Tags = "tags";

    /// <summary>Feature sections show nothing rated above the ceiling, nor unrated items under one.</summary>
    public const string Ceiling = "ceiling";

    /// <summary>Feature sections need a backdrop, and a logo if asked.</summary>
    public const string FeatureArt = "feature-art";

    /// <summary>Film and show cards in rows need a poster.</summary>
    public const string Poster = "poster";

    /// <summary>Films that share a TMDb or IMDb id show once.</summary>
    public const string Versions = "versions";

    /// <summary>Continue Watching leaves out what was last played long ago, or nearly finished.</summary>
    public const string ContinueCleanup = "continue-cleanup";

    /// <summary>Every rule, in the order the admin page lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Tags, Ceiling, FeatureArt, Poster, Versions, ContinueCleanup];
}

/// <summary>
/// The content rules, set once for every section.
/// </summary>
/// <param name="ExcludeTags">Whether excluded tags apply.</param>
/// <param name="ExcludedTags">Tags whose items are never shown.</param>
/// <param name="CeilingOn">Whether the rating ceiling for featuring applies.</param>
/// <param name="EveryoneCeiling">The highest rating featured for everyone, as Jellyfin names it, or null for none.</param>
/// <param name="AudienceCeilings">The highest rating featured for an audience's members, by audience id; the first of a user's audiences with one wins.</param>
/// <param name="FeatureArt">Whether feature sections need a backdrop.</param>
/// <param name="RequireLogo">Whether feature sections also need a logo.</param>
/// <param name="Poster">Whether film and show cards in rows need a poster.</param>
/// <param name="Versions">Whether films that share a TMDb or IMDb id show once.</param>
/// <param name="ContinueCleanup">Whether Continue Watching is cleaned up.</param>
/// <param name="CleanupDays">Leave out what was last played more than this many days ago, or null.</param>
/// <param name="CleanupPercent">Leave out what was played past this percent, or null.</param>
public sealed record ContentRules(
    bool ExcludeTags = false,
    IReadOnlyList<string>? ExcludedTags = null,
    bool CeilingOn = false,
    string? EveryoneCeiling = null,
    IReadOnlyDictionary<string, string>? AudienceCeilings = null,
    bool FeatureArt = true,
    bool RequireLogo = false,
    bool Poster = true,
    bool Versions = true,
    bool ContinueCleanup = false,
    int? CleanupDays = null,
    int? CleanupPercent = null)
{
    /// <summary>
    /// Gets the rules a fresh install starts with: poster, versions and a backdrop for features on; the rest off.
    /// </summary>
    public static ContentRules Defaults { get; } = new();

    /// <summary>
    /// Checks the rules, returning why they cannot be stored.
    /// </summary>
    /// <param name="ratingKnown">Whether Jellyfin knows a rating name.</param>
    /// <returns>The reason, or null.</returns>
    public string? Validate(Func<string, bool> ratingKnown)
    {
        foreach (var rating in new[] { EveryoneCeiling }.Concat(AudienceCeilings?.Values ?? []).OfType<string>())
        {
            if (!ratingKnown(rating))
            {
                return $"Jellyfin does not know the rating '{rating}'";
            }
        }

        if (CleanupDays is < 1)
        {
            return "clean-up days start at 1";
        }

        if (CleanupPercent is < 1 or > 100)
        {
            return "the clean-up percent is from 1 to 100";
        }

        return ContinueCleanup && CleanupDays is null && CleanupPercent is null ? "clean-up needs days or a percent" : null;
    }
}

/// <summary>
/// The content rules in the plugin's database, as one row with a revision. Reads come from memory.
/// </summary>
public sealed class RulesStore
{
    /// <summary>
    /// The rules table, holding the defaults.
    /// </summary>
    public static readonly Migration Migration = new(5, """
        CREATE TABLE content_rules (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            revision INTEGER NOT NULL,
            rules TEXT NOT NULL
        );
        INSERT INTO content_rules (id, revision, rules) VALUES (1, 1, '{}');
        """);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly LayoutDatabase _database;
    private readonly object _write = new();
    private volatile Snapshot _snapshot;

    /// <summary>
    /// Initializes a new instance of the <see cref="RulesStore"/> class.
    /// </summary>
    /// <param name="database">The plugin's database.</param>
    public RulesStore(LayoutDatabase database)
    {
        _database = database;
        _snapshot = Load();
    }

    /// <summary>
    /// Gets the rules.
    /// </summary>
    public ContentRules Rules => _snapshot.Rules;

    /// <summary>
    /// Gets the rules' revision: 1 until they are first changed.
    /// </summary>
    public int Revision => _snapshot.Revision;

    /// <summary>
    /// Replaces the rules.
    /// </summary>
    /// <param name="rules">The new rules, already checked.</param>
    public void Set(ContentRules rules)
    {
        lock (_write)
        {
            if (JsonSerializer.Serialize(rules, Json) == JsonSerializer.Serialize(Rules, Json))
            {
                return;
            }

            using (var connection = _database.Open())
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE content_rules SET revision = revision + 1, rules = $rules WHERE id = 1;";
                command.Parameters.AddWithValue("$rules", JsonSerializer.Serialize(rules, Json));
                command.ExecuteNonQuery();
            }

            _snapshot = Load();
        }
    }

    private Snapshot Load()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision, rules FROM content_rules WHERE id = 1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new Snapshot(ContentRules.Defaults, 1);
        }

        var rules = JsonSerializer.Deserialize<ContentRules>(reader.GetString(1), Json) ?? ContentRules.Defaults;
        return new Snapshot(rules, Convert.ToInt32(reader.GetInt64(0), CultureInfo.InvariantCulture));
    }

    private sealed record Snapshot(ContentRules Rules, int Revision);
}
