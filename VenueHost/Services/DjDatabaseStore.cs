using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using VenueHost.Models;

namespace VenueHost.Services;

/// <summary>
/// Durable SQLite-backed storage for reusable DJ names and stream links.
///
/// The DJ database used to live inside Dalamud's JSON configuration. Keeping it in a
/// dedicated SQLite file avoids losing or corrupting the database when the plugin
/// configuration schema changes, and gives us database-level uniqueness guarantees.
/// </summary>
public sealed class DjDatabaseStore : IDisposable
{
    private const int CurrentSchemaVersion = 1;

    private static readonly object BatteriesInitLock = new();
    private static bool batteriesInitialized;

    private readonly string connectionString;

    public DjDatabaseStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? ".");

        this.connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = false,
        }.ToString();

        EnsureSqliteInitialized();
        this.InitializeSchema();
    }

    public IReadOnlyList<DjDatabaseEntry> Load()
    {
        using var connection = this.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, link
            FROM dj_database
            ORDER BY name COLLATE NOCASE;
            """;

        using var reader = command.ExecuteReader();
        var entries = new List<DjDatabaseEntry>();
        while (reader.Read())
        {
            entries.Add(new DjDatabaseEntry
            {
                Name = reader.GetString(0),
                Link = reader.GetString(1),
            });
        }

        return entries;
    }

    public void ReplaceAll(IEnumerable<DjDatabaseEntry> entries)
    {
        using var connection = this.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM dj_database;";
            delete.ExecuteNonQuery();
        }

        foreach (var entry in entries
            .Select(Normalize)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .GroupBy(entry => entry.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO dj_database (name, link, updated_at_utc)
                VALUES ($name, $link, $updatedAtUtc)
                ON CONFLICT(name) DO UPDATE SET
                    link = excluded.link,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            insert.Parameters.AddWithValue("$name", entry.Name);
            insert.Parameters.AddWithValue("$link", entry.Link);
            insert.Parameters.AddWithValue("$updatedAtUtc", DateTime.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public bool IsEmpty()
    {
        using var connection = this.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dj_database;";
        return Convert.ToInt64(command.ExecuteScalar()) == 0;
    }

    public void Dispose()
    {
        // Pooling is disabled in the connection string to avoid delayed background
        // pruning in Dalamud/FFXIV. Clearing here is a defensive no-op for this store,
        // but also protects older config/runtime paths if they ever opened a pooled
        // SQLite connection before this fix.
        SqliteConnection.ClearAllPools();
    }

    private static void EnsureSqliteInitialized()
    {
        if (batteriesInitialized)
        {
            return;
        }

        lock (BatteriesInitLock)
        {
            if (batteriesInitialized)
            {
                return;
            }

            SQLitePCL.Batteries_V2.Init();
            batteriesInitialized = true;
        }
    }

    private static DjDatabaseEntry Normalize(DjDatabaseEntry entry)
        => new()
        {
            Name = entry.Name.Trim(),
            Link = entry.Link.Trim(),
        };

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(this.connectionString);
        connection.Open();
        return connection;
    }

    private void InitializeSchema()
    {
        using var connection = this.OpenConnection();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL;";
            pragma.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();

        using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS metadata (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS dj_database (
                    name TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
                    link TEXT NOT NULL DEFAULT '',
                    updated_at_utc TEXT NOT NULL
                );
                """;
            schema.ExecuteNonQuery();
        }

        using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = """
                INSERT INTO metadata (key, value)
                VALUES ('schema_version', $version)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            version.Parameters.AddWithValue("$version", CurrentSchemaVersion.ToString());
            version.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
