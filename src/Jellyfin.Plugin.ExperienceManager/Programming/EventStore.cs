using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.ExperienceManager.Programming;

/// <summary>
/// One change an event makes to the page while it is on. The stored layout is never edited.
/// </summary>
/// <param name="Kind">One of <see cref="ChangeKinds"/>.</param>
/// <param name="Target">The section it changes: for swap and hide, and for featured when the page has more than one.</param>
/// <param name="Section">What it shows: the new source for swap and featured, the new section for insert.</param>
/// <param name="After">For insert, the section it goes after; when missing from the page, it goes after the featured banner.</param>
public sealed record EventChange(string Kind, string? Target = null, StoredSection? Section = null, string? After = null);

/// <summary>
/// The kinds of <see cref="EventChange"/>.
/// </summary>
public static class ChangeKinds
{
    /// <summary>Shows another source in a section's place.</summary>
    public const string Swap = "swap";

    /// <summary>Shows another source in the featured banner.</summary>
    public const string Featured = "featured";

    /// <summary>Adds a section.</summary>
    public const string Insert = "insert";

    /// <summary>Hides a section.</summary>
    public const string Hide = "hide";

    /// <summary>Every kind.</summary>
    public static readonly IReadOnlyList<string> All = [Swap, Featured, Insert, Hide];
}

/// <summary>
/// The audiences an event can have.
/// </summary>
public static class Audiences
{
    /// <summary>Every user. The only audience until groups exist.</summary>
    public const string Everyone = "everyone";
}

/// <summary>
/// A named set of changes over a schedule.
/// </summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name, as the admin sees it.</param>
/// <param name="Published">Whether it applies; a draft never does.</param>
/// <param name="Audience">Who it applies to.</param>
/// <param name="Schedule">When it is on.</param>
/// <param name="Changes">What it changes.</param>
public sealed record ProgrammedEvent(
    string Id,
    string Name,
    bool Published,
    string Audience,
    EventSchedule Schedule,
    IReadOnlyList<EventChange> Changes);

/// <summary>
/// Home's events in the plugin's database, as an ordered list: an event higher in the list wins where two touch
/// the same section. Reads come from memory.
/// </summary>
public sealed class EventStore
{
    /// <summary>
    /// The events table, and Halloween and Christmas as drafts, written once when the database reaches this version.
    /// </summary>
    public static readonly Migration Migration = new(2, """
        CREATE TABLE events (
            id TEXT PRIMARY KEY,
            page_id TEXT NOT NULL,
            position INTEGER NOT NULL,
            name TEXT NOT NULL,
            published INTEGER NOT NULL DEFAULT 0,
            audience TEXT NOT NULL DEFAULT 'everyone',
            schedule TEXT NOT NULL,
            changes TEXT NOT NULL
        );
        INSERT INTO events (id, page_id, position, name, published, audience, schedule, changes) VALUES
            ('halloween', 'home', 0, 'Halloween', 0, 'everyone',
             '{"Kind":"yearly","Start":"10-01","End":"10-31"}',
             '[{"Kind":"insert","Section":{"Id":"spooky-season","Type":"query","Title":"Spooky Season","Config":{"Genres":["Horror"],"ItemTypes":"Movie,Series"}},"After":"nextup"}]'),
            ('christmas', 'home', 1, 'Christmas', 0, 'everyone',
             '{"Kind":"yearly","Start":"12-01","End":"12-26"}',
             '[{"Kind":"insert","Section":{"Id":"christmas-classics","Type":"query","Title":"Christmas Classics","Config":{"Genres":["Holiday","Christmas"],"ItemTypes":"Movie,Series"}},"After":"nextup"}]');
        """);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly LayoutDatabase _database;
    private readonly object _write = new();
    private volatile IReadOnlyList<ProgrammedEvent> _events;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventStore"/> class.
    /// </summary>
    /// <param name="database">The plugin's database.</param>
    public EventStore(LayoutDatabase database)
    {
        _database = database;
        _events = Load();
    }

    /// <summary>
    /// Raised after any change to the events.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Gets Home's events, highest first.
    /// </summary>
    public IReadOnlyList<ProgrammedEvent> All => _events;

    /// <summary>
    /// Gets an event.
    /// </summary>
    /// <param name="id">Its id.</param>
    /// <returns>The event, or null.</returns>
    public ProgrammedEvent? Get(string id) => _events.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds an event at the bottom of the list. With no id, one is made from its name.
    /// </summary>
    /// <param name="value">The event.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>The event as stored, or null if refused.</returns>
    public ProgrammedEvent? Add(ProgrammedEvent value, out string? error)
    {
        lock (_write)
        {
            var id = string.IsNullOrWhiteSpace(value.Id) ? NewId(value.Name) : value.Id.Trim();
            value = value with { Id = id };
            error = Validate(value) ?? (Get(id) is null ? null : $"an event with id {id} exists");
            if (error is not null)
            {
                return null;
            }

            Write(connection => Upsert(connection, value, _events.Count));
            return Get(id);
        }
    }

    /// <summary>
    /// Replaces an event, keeping its place in the list.
    /// </summary>
    /// <param name="value">The event, with the id of the one it replaces.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>The event as stored, or null if refused or missing.</returns>
    public ProgrammedEvent? Update(ProgrammedEvent value, out string? error)
    {
        lock (_write)
        {
            error = null;
            var current = Get(value.Id);
            if (current is null)
            {
                return null;
            }

            value = value with { Id = current.Id };
            error = Validate(value);
            if (error is not null)
            {
                return null;
            }

            Write(connection => Upsert(connection, value, IndexOf(current.Id)));
            return Get(current.Id);
        }
    }

    /// <summary>
    /// Publishes or unpublishes an event.
    /// </summary>
    /// <param name="id">Its id.</param>
    /// <param name="published">Whether it applies.</param>
    /// <returns>The event as stored, or null if missing.</returns>
    public ProgrammedEvent? SetPublished(string id, bool published)
    {
        lock (_write)
        {
            var current = Get(id);
            if (current is null)
            {
                return null;
            }

            Write(connection => Upsert(connection, current with { Published = published }, IndexOf(current.Id)));
            return Get(current.Id);
        }
    }

    /// <summary>
    /// Deletes an event.
    /// </summary>
    /// <param name="id">Its id.</param>
    /// <returns>Whether there was one.</returns>
    public bool Delete(string id)
    {
        lock (_write)
        {
            var current = Get(id);
            if (current is null)
            {
                return false;
            }

            var rest = _events.Where(e => e.Id != current.Id).Select(e => e.Id).ToList();
            Write(connection =>
            {
                Run(connection, "DELETE FROM events WHERE id = $id;", ("$id", current.Id));
                Reorder(connection, rest);
            });
            return true;
        }
    }

    /// <summary>
    /// Puts the events in a new order, highest first.
    /// </summary>
    /// <param name="ids">Every event id, once each.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>The events in their new order, or null if refused.</returns>
    public IReadOnlyList<ProgrammedEvent>? Reorder(IReadOnlyList<string>? ids, out string? error)
    {
        lock (_write)
        {
            var known = _events.Select(e => e.Id).ToList();
            var resolved = ids?.Select(id => known.FirstOrDefault(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase))).ToList();
            if (resolved is null || resolved.Count != known.Count || resolved.Any(id => id is null) || resolved.Distinct().Count() != known.Count)
            {
                error = "the order lists every event id once";
                return null;
            }

            error = null;
            Write(connection => Reorder(connection, resolved!));
            return _events;
        }
    }

    internal static string? Validate(ProgrammedEvent value)
    {
        if (string.IsNullOrWhiteSpace(value.Name))
        {
            return "an event needs a name";
        }

        if (value.Audience != Audiences.Everyone)
        {
            return $"the audience is {Audiences.Everyone} until groups exist";
        }

        if (Schedules.Validate(value.Schedule) is { } schedule)
        {
            return schedule;
        }

        if (value.Changes is not { Count: > 0 })
        {
            return "an event makes at least one change";
        }

        foreach (var change in value.Changes)
        {
            if (change is null || !ChangeKinds.All.Contains(change.Kind))
            {
                return $"unknown change kind '{change?.Kind}'";
            }

            if (change.Kind is ChangeKinds.Swap or ChangeKinds.Hide && string.IsNullOrWhiteSpace(change.Target))
            {
                return $"a {change.Kind} change names its Target section";
            }

            if (change.Kind is ChangeKinds.Swap or ChangeKinds.Featured or ChangeKinds.Insert)
            {
                if (change.Section is null)
                {
                    return $"a {change.Kind} change gives its Section";
                }

                var section = change.Kind == ChangeKinds.Insert ? change.Section : change.Section with { Id = change.Target ?? "featured" };
                if (PageStore.Validate([section]) is { } invalid)
                {
                    return invalid;
                }

                if (section.Type is SectionTypes.LatestLibraries)
                {
                    return $"an event cannot add {SectionTypes.LatestLibraries}";
                }
            }
        }

        return null;
    }

    private static string NewId(string name)
    {
        var slug = new StringBuilder();
        foreach (var c in (name ?? string.Empty).ToLowerInvariant())
        {
            slug.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        }

        var id = slug.ToString().Trim('-');
        return id.Length == 0 ? Guid.NewGuid().ToString("N")[..8] : id;
    }

    private static void Upsert(SqliteTransaction connection, ProgrammedEvent value, int position) =>
        Run(
            connection,
            """
            INSERT INTO events (id, page_id, position, name, published, audience, schedule, changes)
            VALUES ($id, 'home', $position, $name, $published, $audience, $schedule, $changes)
            ON CONFLICT (id) DO UPDATE SET name = $name, published = $published, audience = $audience, schedule = $schedule, changes = $changes;
            """,
            ("$id", value.Id),
            ("$position", position),
            ("$name", value.Name.Trim()),
            ("$published", value.Published ? 1 : 0),
            ("$audience", value.Audience),
            ("$schedule", JsonSerializer.Serialize(value.Schedule, Json)),
            ("$changes", JsonSerializer.Serialize(value.Changes, Json)));

    private static void Reorder(SqliteTransaction connection, IReadOnlyList<string> ids)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            Run(connection, "UPDATE events SET position = $position WHERE id = $id;", ("$id", ids[i]), ("$position", i));
        }
    }

    private static void Run(SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private int IndexOf(string id) => _events.Select(e => e.Id).ToList().IndexOf(id);

    private void Write(Action<SqliteTransaction> write)
    {
        using (var connection = _database.Open())
        {
            using var transaction = connection.BeginTransaction();
            write(transaction);
            transaction.Commit();
        }

        _events = Load();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private List<ProgrammedEvent> Load()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, published, audience, schedule, changes FROM events WHERE page_id = 'home' ORDER BY position, id;";
        var events = new List<ProgrammedEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            events.Add(new ProgrammedEvent(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2) != 0,
                reader.GetString(3),
                JsonSerializer.Deserialize<EventSchedule>(reader.GetString(4), Json)!,
                JsonSerializer.Deserialize<List<EventChange>>(reader.GetString(5), Json)!));
        }

        return events;
    }
}
