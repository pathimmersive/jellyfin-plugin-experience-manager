using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.ExperienceManager.Layouts;

/// <summary>
/// One section instance as stored: what it is and how it is set up, not its items.
/// </summary>
/// <param name="Id">Its id, unique on the page.</param>
/// <param name="Type">One of <see cref="SectionTypes.All"/>.</param>
/// <param name="Title">Its title, or null for the type's own.</param>
/// <param name="Config">The type's settings, or null for none.</param>
public sealed record StoredSection(string Id, string Type, string? Title = null, JsonObject? Config = null);

/// <summary>
/// A stored page: its server layout's sections in order, and a revision that changes with every edit.
/// </summary>
/// <param name="Id">The page id.</param>
/// <param name="Revision">Counts the edits since the page was seeded.</param>
/// <param name="Sections">The server layout's sections, in order.</param>
public sealed record StoredPage(string Id, int Revision, IReadOnlyList<StoredSection> Sections)
{
    /// <summary>
    /// Gets the layout version clients see: it changes whenever the layout does.
    /// </summary>
    public string LayoutVersion => "r" + Revision.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The page ids the plugin knows.
/// </summary>
public static class PageIds
{
    /// <summary>The home screen.</summary>
    public const string Home = "home";
}

/// <summary>
/// Pages in the plugin's database. Reads come from memory; only an edit touches the database.
/// </summary>
public sealed class PageStore
{
    /// <summary>
    /// Home as served before layouts were stored, and as a fresh install starts.
    /// </summary>
    public static readonly IReadOnlyList<StoredSection> HomeSeed =
    [
        new("featured", SectionTypes.Featured),
        new("mymedia", SectionTypes.MyMedia),
        new("continue", SectionTypes.ContinueWatching),
        new("nextup", SectionTypes.NextUp),
        new("latest", SectionTypes.LatestLibraries),
    ];

    private readonly LayoutDatabase _database;
    private readonly object _write = new();
    private volatile IReadOnlyDictionary<string, StoredPage> _pages;

    /// <summary>
    /// Initializes a new instance of the <see cref="PageStore"/> class, seeding Home if the database has none.
    /// </summary>
    /// <param name="database">The plugin's database.</param>
    public PageStore(LayoutDatabase database)
    {
        _database = database;
        using (var connection = _database.Open())
        {
            if (!Exists(connection, PageIds.Home))
            {
                using var transaction = connection.BeginTransaction();
                Insert(connection, transaction, PageIds.Home);
                WriteSections(connection, transaction, PageIds.Home, HomeSeed);
                transaction.Commit();
            }
        }

        _pages = Load();
    }

    /// <summary>
    /// Gets a stored page.
    /// </summary>
    /// <param name="pageId">The page id, in any case.</param>
    /// <returns>The page, or null if there is none.</returns>
    public StoredPage? Get(string pageId) => _pages.GetValueOrDefault(pageId.ToLowerInvariant());

    /// <summary>
    /// Replaces a page's server-layout sections. Nothing is written unless the whole list is valid.
    /// </summary>
    /// <param name="pageId">The page id.</param>
    /// <param name="sections">The new sections, in order.</param>
    /// <param name="error">Why the list was refused, when it was.</param>
    /// <returns>The page as now stored, or null if there is no such page or the list was refused.</returns>
    public StoredPage? Replace(string pageId, IReadOnlyList<StoredSection> sections, out string? error)
    {
        error = Validate(sections);
        var current = Get(pageId);
        if (current is null || error is not null)
        {
            return null;
        }

        lock (_write)
        {
            current = Get(pageId)!;
            if (Same(current.Sections, sections))
            {
                return current;
            }

            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();
            WriteSections(connection, transaction, current.Id, sections);
            Run(connection, transaction, "UPDATE pages SET revision = revision + 1, updated = $updated WHERE id = $page;", ("$page", current.Id), ("$updated", Now()));
            transaction.Commit();
            _pages = Load();
            return Get(pageId);
        }
    }

    internal static string? Validate(IReadOnlyList<StoredSection>? sections)
    {
        if (sections is null)
        {
            return "sections are required";
        }

        foreach (var section in sections)
        {
            if (section is null || string.IsNullOrWhiteSpace(section.Id))
            {
                return "every section needs an id";
            }

            if (!SectionTypes.All.Contains(section.Type))
            {
                return $"section {section.Id} has an unknown type '{section.Type}'";
            }

            if (SectionTypes.ValidateConfig(section) is { } config)
            {
                return config;
            }
        }

        var duplicate = sections.GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            return $"section id {duplicate.Key} is used twice";
        }

        // Its sections take their ids from the libraries, so a second one would repeat them.
        return sections.Count(s => s.Type == SectionTypes.LatestLibraries) > 1 ? $"a page holds {SectionTypes.LatestLibraries} once" : null;
    }

    private static bool Same(IReadOnlyList<StoredSection> a, IReadOnlyList<StoredSection> b) =>
        a.Count == b.Count && a.Zip(b).All(p =>
            p.First.Id == p.Second.Id && p.First.Type == p.Second.Type && p.First.Title == p.Second.Title
            && ConfigText(p.First.Config) == ConfigText(p.Second.Config));

    private static string ConfigText(JsonObject? config) => config?.ToJsonString() ?? "{}";

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static bool Exists(SqliteConnection connection, string pageId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pages WHERE id = $page;";
        command.Parameters.AddWithValue("$page", pageId);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, string pageId)
    {
        Run(connection, transaction, "INSERT INTO pages (id, revision, updated) VALUES ($page, 1, $updated);", ("$page", pageId), ("$updated", Now()));
        Run(connection, transaction, "INSERT INTO layouts (page_id, scope, scope_id) VALUES ($page, 'server', '');", ("$page", pageId));
    }

    private static void WriteSections(SqliteConnection connection, SqliteTransaction transaction, string pageId, IReadOnlyList<StoredSection> sections)
    {
        const string Layout = "(SELECT id FROM layouts WHERE page_id = $page AND scope = 'server' AND scope_id = '')";
        Run(connection, transaction, $"DELETE FROM sections WHERE layout_id = {Layout};", ("$page", pageId));
        for (var i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            Run(
                connection,
                transaction,
                $"INSERT INTO sections (layout_id, position, id, type, title, config) VALUES ({Layout}, $position, $id, $type, $title, $config);",
                ("$page", pageId),
                ("$position", i),
                ("$id", s.Id),
                ("$type", s.Type),
                ("$title", (object?)s.Title ?? DBNull.Value),
                ("$config", ConfigText(s.Config)));
        }
    }

    private static void Run(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private Dictionary<string, StoredPage> Load()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.revision, s.id, s.type, s.title, s.config
            FROM pages p
            JOIN layouts l ON l.page_id = p.id AND l.scope = 'server' AND l.scope_id = ''
            LEFT JOIN sections s ON s.layout_id = l.id
            ORDER BY p.id, s.position;
            """;
        var pages = new Dictionary<string, (int Revision, List<StoredSection> Sections)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var pageId = reader.GetString(0);
            if (!pages.TryGetValue(pageId, out var page))
            {
                page = (reader.GetInt32(1), []);
                pages[pageId] = page;
            }

            if (!reader.IsDBNull(2))
            {
                var config = JsonNode.Parse(reader.GetString(5)) as JsonObject;
                page.Sections.Add(new StoredSection(
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    config is { Count: > 0 } ? config : null));
            }
        }

        return pages.ToDictionary(p => p.Key, p => new StoredPage(p.Key, p.Value.Revision, p.Value.Sections));
    }
}
