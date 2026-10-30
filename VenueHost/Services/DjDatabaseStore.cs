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
    private const int CurrentSchemaVersion = 2;

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
            SELECT id, name, link
            FROM dj_database
            ORDER BY name COLLATE NOCASE;
            """;

        using var reader = command.ExecuteReader();
        var entries = new List<DjDatabaseEntry>();
        while (reader.Read())
        {
            entries.Add(new DjDatabaseEntry
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Link = reader.GetString(2),
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
                INSERT INTO dj_database (id, name, link, updated_at_utc)
                VALUES ($id, $name, $link, $updatedAtUtc)
                ON CONFLICT(name) DO UPDATE SET
                    id = excluded.id,
                    link = excluded.link,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            insert.Parameters.AddWithValue("$id", entry.Id);
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
            Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id,
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
                    id TEXT NOT NULL DEFAULT '',
                    name TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
                    link TEXT NOT NULL DEFAULT '',
                    updated_at_utc TEXT NOT NULL
                );
                """;
            schema.ExecuteNonQuery();
        }

        // Existing v0.8.6 SQLite databases did not yet have persistent IDs.
        using (var columns = connection.CreateCommand())
        {
            columns.Transaction = transaction;
            columns.CommandText = "PRAGMA table_info(dj_database);";
            using var reader = columns.ExecuteReader();
            var hasId = false;
            while (reader.Read())
                if (string.Equals(reader.GetString(1), "id", StringComparison.OrdinalIgnoreCase)) hasId = true;
            reader.Close();
            if (!hasId)
            {
                using var alter = connection.CreateCommand();
                alter.Transaction = transaction;
                alter.CommandText = "ALTER TABLE dj_database ADD COLUMN id TEXT NOT NULL DEFAULT '';";
                alter.ExecuteNonQuery();
            }
        }
        using (var missing = connection.CreateCommand())
        {
            missing.Transaction = transaction;
            missing.CommandText = "SELECT name FROM dj_database WHERE id = '';";
            using var reader = missing.ExecuteReader();
            var names = new List<string>();
            while (reader.Read()) names.Add(reader.GetString(0));
            reader.Close();
            foreach (var name in names)
            {
                using var assign = connection.CreateCommand();
                assign.Transaction = transaction;
                assign.CommandText = "UPDATE dj_database SET id = $id WHERE name = $name;";
                assign.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                assign.Parameters.AddWithValue("$name", name);
                assign.ExecuteNonQuery();
            }
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
