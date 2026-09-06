namespace com.lifepixer.mangaplex.Server.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Data;

/// <summary>
/// Database initialization and connection configuration.
/// Ensures WAL mode, foreign keys, busy timeout, and FTS5 trigram index are configured.
/// </summary>
public static class DatabaseInitialization
{
    /// <summary>
    /// Configures the SQLite connection string with WAL mode and busy timeout.
    /// </summary>
    public static string BuildConnectionString(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30, // 30-second busy timeout (bounded)
        };
        return builder.ConnectionString;
    }

    /// <summary>
    /// Configures EF Core DbContext options for SQLite with proper connection settings.
    /// </summary>
    public static DbContextOptionsBuilder<MangaPlexDbContext> ConfigureSqlite(
        this DbContextOptionsBuilder<MangaPlexDbContext> builder,
        string databasePath)
    {
        var connectionString = BuildConnectionString(databasePath);
        builder.UseSqlite(connectionString, sqlite =>
        {
            sqlite.CommandTimeout(30);
        });

        return builder;
    }

    /// <summary>
    /// Runs post-migration pragmas and FTS5 index creation.
    /// Called after migrations are applied.
    /// </summary>
    public static async Task ConfigureDatabaseAsync(MangaPlexDbContext db, CancellationToken ct = default)
    {
        // Enable foreign keys on every connection
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;", ct);

        // WAL mode for concurrent readers + single writer
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", ct);

        // FULL synchronous for durability of private reading state
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous = FULL;", ct);

        // Bounded busy timeout (also set in connection string, but enforce here)
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout = 30000;", ct);

        // Temp store in memory for performance
        await db.Database.ExecuteSqlRawAsync("PRAGMA temp_store = MEMORY;", ct);

        // Create FTS5 trigram search index for catalog nodes
        // Indexes display_name and relative_path for literal substring search
        await db.Database.ExecuteSqlRawAsync("""
            CREATE VIRTUAL TABLE IF NOT EXISTS catalog_search USING fts5(
                display_name,
                relative_path,
                library_id UNINDEXED,
                node_id UNINDEXED,
                tokenize='trigram'
            );
            """, ct);
    }

    /// <summary>
    /// Returns the schema version from the database, or null if not initialized.
    /// Used by the startup coordinator to reject newer incompatible schemas.
    /// </summary>
    public static async Task<int?> GetSchemaVersionAsync(MangaPlexDbContext db, CancellationToken ct = default)
    {
        try
        {
            using var connection = db.Database.GetDbConnection();
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            var result = await command.ExecuteScalarAsync(ct);
            return result is long l ? (int)l : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sets the schema version. Called after successful migration.
    /// </summary>
    public static async Task SetSchemaVersionAsync(MangaPlexDbContext db, int version, CancellationToken ct = default)
    {
        // PRAGMA doesn't support parameterized values; version is an internally-generated constant
        var sql = "PRAGMA user_version = " + version.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";";
        await db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    /// <summary>
    /// Current application schema version. Incremented when migrations change.
    /// The startup coordinator rejects databases with a higher schema version.
    /// </summary>
    public const int CurrentSchemaVersion = 1;
}
