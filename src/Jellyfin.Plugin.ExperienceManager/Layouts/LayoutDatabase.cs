using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.ExperienceManager.Layouts;

/// <summary>
/// One schema change, applied once and in order.
/// </summary>
/// <param name="Version">The schema version it brings the database to.</param>
/// <param name="Sql">The statements it runs.</param>
public sealed record Migration(int Version, string Sql);

/// <summary>
/// The plugin's own SQLite database. Jellyfin ships SQLite, so the plugin carries none of its own.
/// </summary>
public sealed class LayoutDatabase
{
    /// <summary>
    /// The schema, one migration per version. A migration is never edited once released; a change is a new one.
    /// </summary>
    public static readonly IReadOnlyList<Migration> Migrations =
    [
        new(1, """
            CREATE TABLE pages (
                id TEXT PRIMARY KEY,
                revision INTEGER NOT NULL,
                updated TEXT NOT NULL
            );
            CREATE TABLE layouts (
                id INTEGER PRIMARY KEY,
                page_id TEXT NOT NULL REFERENCES pages(id) ON DELETE CASCADE,
                scope TEXT NOT NULL CHECK (scope IN ('server', 'group', 'user')),
                scope_id TEXT NOT NULL DEFAULT '',
                UNIQUE (page_id, scope, scope_id)
            );
            CREATE TABLE sections (
                layout_id INTEGER NOT NULL REFERENCES layouts(id) ON DELETE CASCADE,
                position INTEGER NOT NULL,
                id TEXT NOT NULL,
                type TEXT NOT NULL,
                title TEXT,
                config TEXT NOT NULL DEFAULT '{}',
                PRIMARY KEY (layout_id, id)
            );
            """),
        Programming.EventStore.Migration,
        Audiences.AudienceStore.Migration,
        SectionMigrations.Off,
    ];

    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="LayoutDatabase"/> class and brings its schema up to date.
    /// </summary>
    /// <param name="path">The database file, created with its folder if missing.</param>
    public LayoutDatabase(string path)
        : this(path, Migrations)
    {
    }

    internal LayoutDatabase(string path, IReadOnlyList<Migration> migrations)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        FilePath = path;
        Migrate(migrations);
    }

    /// <summary>
    /// Gets the database file.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Gets where the server keeps the database: under its data folder, not the plugin's own folder, which is
    /// named after the plugin version and replaced when the plugin is updated.
    /// </summary>
    /// <param name="paths">The server's paths.</param>
    /// <returns>The database file.</returns>
    public static string DefaultPath(IApplicationPaths paths) => Path.Combine(paths.DataPath, "experience-manager", "experience-manager.db");

    /// <summary>
    /// Opens a connection with foreign keys enforced.
    /// </summary>
    /// <returns>The open connection.</returns>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, null, "PRAGMA foreign_keys = ON;");
        return connection;
    }

    /// <summary>
    /// Gets the schema version the database is at.
    /// </summary>
    /// <returns>The highest migration applied.</returns>
    public int SchemaVersion()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    internal static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // Each migration runs in its own transaction with its version row, so a failed one leaves the
    // database at the version before it.
    private void Migrate(IReadOnlyList<Migration> migrations)
    {
        using var connection = Open();
        Execute(connection, null, "PRAGMA journal_mode = WAL;");
        Execute(connection, null, "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied TEXT NOT NULL);");
        var current = SchemaVersion();
        foreach (var migration in migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, migration.Sql);
            using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO schema_migrations (version, applied) VALUES ($version, $applied);";
            record.Parameters.AddWithValue("$version", migration.Version);
            record.Parameters.AddWithValue("$applied", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            record.ExecuteNonQuery();
            transaction.Commit();
        }
    }
}
