using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.ExperienceManager.Layouts;

namespace Jellyfin.Plugin.ExperienceManager.Announcements;

/// <summary>
/// The announcements each user dismissed, with the version they dismissed. A dismissal holds on every device until
/// the announcement changes.
/// </summary>
public sealed class DismissalStore
{
    /// <summary>The dismissals table.</summary>
    public static readonly Migration Migration = new(7, """
        CREATE TABLE dismissals (
            user_id TEXT NOT NULL,
            section_id TEXT NOT NULL,
            version TEXT NOT NULL,
            dismissed TEXT NOT NULL,
            PRIMARY KEY (user_id, section_id)
        );
        """);

    private readonly LayoutDatabase _database;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, IReadOnlyDictionary<string, string>> _byUser = new();
    private readonly object _write = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DismissalStore"/> class.
    /// </summary>
    /// <param name="database">The plugin's database.</param>
    public DismissalStore(LayoutDatabase database)
        : this(database, TimeProvider.System)
    {
    }

    internal DismissalStore(LayoutDatabase database, TimeProvider time)
    {
        _database = database;
        _time = time;
    }

    /// <summary>
    /// Gets a value indicating whether a user dismissed this version of an announcement.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="sectionId">The announcement's section id.</param>
    /// <param name="version">Its version now.</param>
    /// <returns>Whether it is dismissed.</returns>
    public bool IsDismissed(Guid userId, string sectionId, string version) =>
        Of(userId).TryGetValue(sectionId, out var dismissed) && dismissed == version;

    /// <summary>
    /// Records that a user dismissed a version of an announcement.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="sectionId">The announcement's section id.</param>
    /// <param name="version">The version dismissed.</param>
    public void Dismiss(Guid userId, string sectionId, string version)
    {
        lock (_write)
        {
            using (var connection = _database.Open())
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO dismissals (user_id, section_id, version, dismissed) VALUES ($user, $section, $version, $at)
                    ON CONFLICT (user_id, section_id) DO UPDATE SET version = excluded.version, dismissed = excluded.dismissed;
                    """;
                command.Parameters.AddWithValue("$user", userId.ToString("N"));
                command.Parameters.AddWithValue("$section", sectionId.ToLowerInvariant());
                command.Parameters.AddWithValue("$version", version);
                command.Parameters.AddWithValue("$at", _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }

            _byUser.TryRemove(userId, out _);
        }
    }

    /// <summary>
    /// Forgets a user's dismissals, when the account is deleted.
    /// </summary>
    /// <param name="userId">The user.</param>
    public void Forget(Guid userId)
    {
        lock (_write)
        {
            using (var connection = _database.Open())
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM dismissals WHERE user_id = $user;";
                command.Parameters.AddWithValue("$user", userId.ToString("N"));
                command.ExecuteNonQuery();
            }

            _byUser.TryRemove(userId, out _);
        }
    }

    /// <summary>
    /// Gets a short key for a user's dismissals, which joins their layout version; empty when there are none.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The key.</returns>
    public string Key(Guid userId)
    {
        var dismissed = Of(userId);
        if (dismissed.Count == 0)
        {
            return string.Empty;
        }

        var text = string.Join(';', dismissed.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => d.Key + "=" + d.Value));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];
    }

    private IReadOnlyDictionary<string, string> Of(Guid userId) => _byUser.GetOrAdd(userId, Load);

    private Dictionary<string, string> Load(Guid userId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT section_id, version FROM dismissals WHERE user_id = $user;";
        command.Parameters.AddWithValue("$user", userId.ToString("N"));
        using var reader = command.ExecuteReader();
        var dismissed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            dismissed[reader.GetString(0)] = reader.GetString(1);
        }

        return dismissed;
    }
}
