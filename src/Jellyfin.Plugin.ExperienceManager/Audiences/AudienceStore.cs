using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.ExperienceManager.Layouts;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.ExperienceManager.Audiences;

/// <summary>
/// The kinds of audience.
/// </summary>
public static class AudienceKinds
{
    /// <summary>Users the admin picked.</summary>
    public const string Group = "group";

    /// <summary>The built-in New users: accounts younger than a number of days.</summary>
    public const string NewUsers = "new-users";
}

/// <summary>
/// An audience: a hand-picked group, or the built-in New users.
/// </summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind">One of <see cref="AudienceKinds"/>.</param>
/// <param name="Days">For New users, how many days an account counts as new.</param>
/// <param name="Members">For a group, its users.</param>
public sealed record Audience(string Id, string Name, string Kind, int? Days, IReadOnlyList<Guid> Members);

/// <summary>
/// A group's layout of one page.
/// </summary>
/// <param name="AudienceId">The audience.</param>
/// <param name="PageId">The page.</param>
/// <param name="Revision">Counts its edits.</param>
/// <param name="Entries">Its entries, in its order.</param>
public sealed record StoredGroupLayout(string AudienceId, string PageId, int Revision, IReadOnlyList<GroupEntry> Entries);

/// <summary>
/// Audiences in precedence order, their members, when accounts were created, and group layouts, in the plugin's
/// database. Reads come from memory.
/// </summary>
public sealed class AudienceStore
{
    /// <summary>The built-in New users audience's id.</summary>
    public const string NewUsersId = "new-users";

    /// <summary>How many days an account counts as new, unless the admin changes it.</summary>
    public const int DefaultNewUserDays = 14;

    /// <summary>
    /// Audiences, members, account creation times and group layouts; New users is built in.
    /// </summary>
    public static readonly Migration Migration = new(3, """
        CREATE TABLE audiences (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            kind TEXT NOT NULL CHECK (kind IN ('group', 'new-users')),
            position INTEGER NOT NULL,
            days INTEGER
        );
        CREATE TABLE audience_members (
            audience_id TEXT NOT NULL REFERENCES audiences(id) ON DELETE CASCADE,
            user_id TEXT NOT NULL,
            PRIMARY KEY (audience_id, user_id)
        );
        CREATE TABLE user_created (
            user_id TEXT PRIMARY KEY,
            created TEXT NOT NULL
        );
        CREATE TABLE group_layouts (
            audience_id TEXT NOT NULL REFERENCES audiences(id) ON DELETE CASCADE,
            page_id TEXT NOT NULL,
            revision INTEGER NOT NULL,
            entries TEXT NOT NULL,
            PRIMARY KEY (audience_id, page_id)
        );
        INSERT INTO audiences (id, name, kind, position, days) VALUES ('new-users', 'New users', 'new-users', 0, 14);
        """);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly LayoutDatabase _database;
    private readonly object _write = new();
    private volatile Snapshot _snapshot;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudienceStore"/> class.
    /// </summary>
    /// <param name="database">The plugin's database.</param>
    public AudienceStore(LayoutDatabase database)
    {
        _database = database;
        _snapshot = Load();
    }

    /// <summary>
    /// Raised after any change to audiences, members or group layouts.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Gets the audiences, in precedence order.
    /// </summary>
    public IReadOnlyList<Audience> All => _snapshot.Audiences;

    /// <summary>
    /// Gets an audience.
    /// </summary>
    /// <param name="id">Its id.</param>
    /// <returns>The audience, or null.</returns>
    public Audience? Get(string id) => _snapshot.Audiences.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gets when the plugin saw an account created.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The time, or null for an account that existed before the plugin, or that it never saw.</returns>
    public DateTimeOffset? CreatedAt(Guid userId) => _snapshot.Created.TryGetValue(userId, out var at) ? at : null;

    /// <summary>
    /// Gets a group's layout of a page.
    /// </summary>
    /// <param name="audienceId">The audience.</param>
    /// <param name="pageId">The page.</param>
    /// <returns>The layout, or null when the group uses the server layout.</returns>
    public StoredGroupLayout? Layout(string audienceId, string pageId) =>
        _snapshot.Layouts.FirstOrDefault(l => string.Equals(l.AudienceId, audienceId, StringComparison.OrdinalIgnoreCase) && string.Equals(l.PageId, pageId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds a group at the bottom of the order. Its id is made from its name.
    /// </summary>
    /// <param name="name">Its name.</param>
    /// <param name="members">Its users.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>The group, or null if refused.</returns>
    public Audience? Add(string? name, IReadOnlyList<Guid>? members, out string? error)
    {
        lock (_write)
        {
            error = string.IsNullOrWhiteSpace(name) ? "an audience needs a name" : null;
            if (error is not null)
            {
                return null;
            }

            var id = NewId(name!);
            Write(t =>
            {
                Run(t, "INSERT INTO audiences (id, name, kind, position) VALUES ($id, $name, 'group', $position);", ("$id", id), ("$name", name!.Trim()), ("$position", _snapshot.Audiences.Count));
                SetMembers(t, id, members ?? []);
            });
            return Get(id);
        }
    }

    /// <summary>
    /// Renames a group and sets its members, or sets New users' days.
    /// </summary>
    /// <param name="id">The audience.</param>
    /// <param name="name">Its new name; ignored for New users.</param>
    /// <param name="members">Its users; New users has none.</param>
    /// <param name="days">New users' days; ignored for a group.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>The audience, or null if refused or missing.</returns>
    public Audience? Update(string id, string? name, IReadOnlyList<Guid>? members, int? days, out string? error)
    {
        lock (_write)
        {
            error = null;
            var current = Get(id);
            if (current is null)
            {
                return null;
            }

            if (current.Kind == AudienceKinds.NewUsers)
            {
                if (members is { Count: > 0 })
                {
                    error = "New users has no hand-picked members";
                    return null;
                }

                if (days is not (>= 1 and <= 3650))
                {
                    error = "New users counts from 1 to 3650 days";
                    return null;
                }

                Write(t => Run(t, "UPDATE audiences SET days = $days WHERE id = $id;", ("$id", current.Id), ("$days", days.Value)));
                return Get(current.Id);
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                error = "an audience needs a name";
                return null;
            }

            Write(t =>
            {
                Run(t, "UPDATE audiences SET name = $name WHERE id = $id;", ("$id", current.Id), ("$name", name.Trim()));
                SetMembers(t, current.Id, members ?? current.Members);
            });
            return Get(current.Id);
        }
    }

    /// <summary>
    /// Deletes a group and its layouts. New users cannot be deleted.
    /// </summary>
    /// <param name="id">The audience.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>Whether it was deleted.</returns>
    public bool Delete(string id, out string? error)
    {
        lock (_write)
        {
            error = null;
            var current = Get(id);
            if (current is null)
            {
                return false;
            }

            if (current.Kind == AudienceKinds.NewUsers)
            {
                error = "New users is built in and cannot be deleted";
                return false;
            }

            var rest = _snapshot.Audiences.Where(a => a.Id != current.Id).Select(a => a.Id).ToList();
            Write(t =>
            {
                Run(t, "DELETE FROM audiences WHERE id = $id;", ("$id", current.Id));
                Order(t, rest);
            });
            return true;
        }
    }

    /// <summary>
    /// Puts the audiences in a new precedence order, highest first.
    /// </summary>
    /// <param name="ids">Every audience id once.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>The audiences in their new order, or null if refused.</returns>
    public IReadOnlyList<Audience>? Reorder(IReadOnlyList<string>? ids, out string? error)
    {
        lock (_write)
        {
            var known = _snapshot.Audiences.Select(a => a.Id).ToList();
            var resolved = ids?.Select(id => known.FirstOrDefault(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase))).ToList();
            if (resolved is null || resolved.Count != known.Count || resolved.Any(id => id is null) || resolved.Distinct().Count() != known.Count)
            {
                error = "the order lists every audience id once";
                return null;
            }

            error = null;
            Write(t => Order(t, resolved!));
            return _snapshot.Audiences;
        }
    }

    /// <summary>
    /// Replaces a group's layout of a page with new entries, checked against the server layout.
    /// </summary>
    /// <param name="audienceId">The audience.</param>
    /// <param name="pageId">The page.</param>
    /// <param name="server">The page's server layout.</param>
    /// <param name="entries">The group's entries, in its order.</param>
    /// <param name="error">Why they were refused.</param>
    /// <returns>The layout as stored, or null if refused or the audience is missing.</returns>
    public StoredGroupLayout? SetLayout(string audienceId, string pageId, IReadOnlyList<StoredSection> server, IReadOnlyList<GroupEntry>? entries, out string? error)
    {
        lock (_write)
        {
            error = null;
            var audience = Get(audienceId);
            if (audience is null)
            {
                return null;
            }

            error = GroupLayouts.Validate(server, entries);
            if (error is not null)
            {
                return null;
            }

            var page = pageId.ToLowerInvariant();
            var revision = (Layout(audience.Id, page)?.Revision ?? 0) + 1;
            Write(t => Run(
                t,
                """
                INSERT INTO group_layouts (audience_id, page_id, revision, entries) VALUES ($audience, $page, $revision, $entries)
                ON CONFLICT (audience_id, page_id) DO UPDATE SET revision = $revision, entries = $entries;
                """,
                ("$audience", audience.Id),
                ("$page", page),
                ("$revision", revision),
                ("$entries", JsonSerializer.Serialize(entries, Json))));
            return Layout(audience.Id, page);
        }
    }

    /// <summary>
    /// Removes a group's layout of a page, so its members see the server layout.
    /// </summary>
    /// <param name="audienceId">The audience.</param>
    /// <param name="pageId">The page.</param>
    /// <returns>Whether there was one.</returns>
    public bool ClearLayout(string audienceId, string pageId)
    {
        lock (_write)
        {
            var layout = Layout(audienceId, pageId);
            if (layout is null)
            {
                return false;
            }

            Write(t => Run(t, "DELETE FROM group_layouts WHERE audience_id = $audience AND page_id = $page;", ("$audience", layout.AudienceId), ("$page", layout.PageId)));
            return true;
        }
    }

    /// <summary>
    /// Records that an account was created now, so it can count as a new user.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="at">When it was created.</param>
    public void RecordCreated(Guid userId, DateTimeOffset at)
    {
        lock (_write)
        {
            Write(t => Run(
                t,
                "INSERT INTO user_created (user_id, created) VALUES ($user, $created) ON CONFLICT (user_id) DO NOTHING;",
                ("$user", userId.ToString("N")),
                ("$created", at.ToString("O", CultureInfo.InvariantCulture))));
        }
    }

    /// <summary>
    /// Forgets a deleted account: its creation time and its memberships.
    /// </summary>
    /// <param name="userId">The user.</param>
    public void ForgetUser(Guid userId)
    {
        lock (_write)
        {
            Write(t =>
            {
                Run(t, "DELETE FROM user_created WHERE user_id = $user;", ("$user", userId.ToString("N")));
                Run(t, "DELETE FROM audience_members WHERE user_id = $user;", ("$user", userId.ToString("N")));
            });
        }
    }

    private string NewId(string name)
    {
        var slug = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            slug.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        }

        var id = slug.ToString().Trim('-');
        id = id.Length == 0 ? "audience" : id;
        var unique = id;
        for (var n = 2; Get(unique) is not null; n++)
        {
            unique = id + "-" + n.ToString(CultureInfo.InvariantCulture);
        }

        return unique;
    }

    private static void SetMembers(SqliteTransaction t, string audienceId, IReadOnlyList<Guid> members)
    {
        Run(t, "DELETE FROM audience_members WHERE audience_id = $id;", ("$id", audienceId));
        foreach (var member in members.Where(m => !m.Equals(Guid.Empty)).Distinct())
        {
            Run(t, "INSERT INTO audience_members (audience_id, user_id) VALUES ($id, $user);", ("$id", audienceId), ("$user", member.ToString("N")));
        }
    }

    private static void Order(SqliteTransaction t, IReadOnlyList<string> ids)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            Run(t, "UPDATE audiences SET position = $position WHERE id = $id;", ("$id", ids[i]), ("$position", i));
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

    private void Write(Action<SqliteTransaction> write)
    {
        using (var connection = _database.Open())
        {
            using var transaction = connection.BeginTransaction();
            write(transaction);
            transaction.Commit();
        }

        _snapshot = Load();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Snapshot Load()
    {
        using var connection = _database.Open();
        var members = new Dictionary<string, List<Guid>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT audience_id, user_id FROM audience_members ORDER BY user_id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (Guid.TryParse(reader.GetString(1), out var user))
                {
                    (members.TryGetValue(reader.GetString(0), out var list) ? list : members[reader.GetString(0)] = []).Add(user);
                }
            }
        }

        var audiences = new List<Audience>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, name, kind, days FROM audiences ORDER BY position, id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                audiences.Add(new Audience(id, reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt32(3), members.GetValueOrDefault(id) ?? []));
            }
        }

        var created = new Dictionary<Guid, DateTimeOffset>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT user_id, created FROM user_created;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (Guid.TryParse(reader.GetString(0), out var user) && DateTimeOffset.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                {
                    created[user] = at;
                }
            }
        }

        var layouts = new List<StoredGroupLayout>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT audience_id, page_id, revision, entries FROM group_layouts;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                layouts.Add(new StoredGroupLayout(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), JsonSerializer.Deserialize<List<GroupEntry>>(reader.GetString(3), Json) ?? []));
            }
        }

        return new Snapshot(audiences, created, layouts);
    }

    private sealed record Snapshot(IReadOnlyList<Audience> Audiences, IReadOnlyDictionary<Guid, DateTimeOffset> Created, IReadOnlyList<StoredGroupLayout> Layouts);
}
