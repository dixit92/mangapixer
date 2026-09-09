namespace com.lifepixer.mangaplex.Server.Persistence;

using com.lifepixer.mangaplex.Server.Logging;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Data.Common;

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
            // PRIVATE cache (the default) — NOT shared. WAL mode gives
            // 1-writer/N-reader concurrency via MVCC snapshots; shared-cache mode
            // would layer table-level locking on top and make readers fail with
            // SQLITE_LOCKED ("table is locked") while a scan writes catalog_nodes.
            // Private cache + WAL + busy_timeout is the correct concurrent config:
            // reads never block on the writer, and writers serialize with a bounded
            // wait. (Fixes 30s read timeouts during a library scan.)
            Cache = SqliteCacheMode.Private,
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

        // FTS5 triggers to keep catalog_search in sync with catalog_nodes
        // (audit defect D28 — the FTS table was created but never populated).
        // Column names are PascalCase (EF Core default for SQLite).
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER IF NOT EXISTS catalog_search_ai
            AFTER INSERT ON catalog_nodes
            BEGIN
                INSERT INTO catalog_search(display_name, relative_path, library_id, node_id)
                VALUES (new.DisplayName, new.RelativePath, new.LibraryId, new.Id);
            END;
            """, ct);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER IF NOT EXISTS catalog_search_au
            AFTER UPDATE ON catalog_nodes
            BEGIN
                DELETE FROM catalog_search WHERE node_id = old.Id;
                INSERT INTO catalog_search(display_name, relative_path, library_id, node_id)
                VALUES (new.DisplayName, new.RelativePath, new.LibraryId, new.Id);
            END;
            """, ct);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER IF NOT EXISTS catalog_search_ad
            AFTER DELETE ON catalog_nodes
            BEGIN
                DELETE FROM catalog_search WHERE node_id = old.Id;
            END;
            """, ct);

        // One-time backfill for nodes created before the triggers existed
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO catalog_search(display_name, relative_path, library_id, node_id)
            SELECT DisplayName, RelativePath, LibraryId, Id
            FROM catalog_nodes
            WHERE Id NOT IN (SELECT node_id FROM catalog_search);
            """, ct);

        // Set the schema version so startup validation can detect incompatible databases
        await SetSchemaVersionAsync(db, CurrentSchemaVersion, ct);
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
    ///
    /// Version 2: <c>DateTimeOffset</c> columns are now stored as a comparable
    /// <c>long</c> via <see cref="Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter"/>
    /// so EF Core SQLite can translate comparisons on <c>DateTimeOffset</c>
    /// columns (audit defect D26). Pre-release databases created under
    /// version 1 must be recreated; <see cref="JobRecoveryService.ValidateSchemaAsync"/>
    /// logs a clear warning when <c>user_version</c> is 1.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    // --- Migration orchestration (2026-09-08) ---
    //
    // Replaces the old EnsureCreated() path. EnsureCreated builds the schema only
    // on a brand-new database and never evolves an existing one, so post-1.0 schema
    // changes could not reach a deployed DB. We now use EF Core migrations, with a
    // one-time "adopt" step that stamps databases originally built by EnsureCreated
    // (every 1.0.0 instance) as already having the baseline migration applied — so
    // their tables are not recreated and no data is lost.

    /// <summary>
    /// Brings the database schema to the latest EF Core migration, safely for both
    /// fresh installs and EnsureCreated-era (pre-migrations) databases.
    ///
    /// 1. Adopt: an existing app schema (has <c>users</c>) with no
    ///    <c>__EFMigrationsHistory</c> and a recognised schema version (>= 2, i.e. a
    ///    real 1.0.0 DB) is stamped with the baseline migration as already applied.
    /// 2. Back up: if real data is present and migrations are pending, take a
    ///    consistent snapshot first (rollback point); abort the migrate if it fails.
    /// 3. Migrate: apply any pending migrations.
    /// Fresh installs skip 1 and 2 and migrate from empty.
    /// </summary>
    public static async Task MigrateToLatestAsync(
        MangaPlexDbContext db,
        string dataRoot,
        Func<string, Task<bool>> backupAsync,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var (hasAppSchema, hasHistory, schemaVersion) = await ReadSchemaStateAsync(db, ct);

        if (hasAppSchema && !hasHistory && schemaVersion >= 2)
        {
            var baseline = db.Database.GetMigrations().FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "No migrations found in the assembly; cannot adopt the existing database.");
            await SeedMigrationsHistoryBaselineAsync(db, baseline, ct);
            logger?.LogInformation(
                LogEvents.Database.MigrationBaselineAdopted, "Adopted an existing pre-migrations database into the EF migration timeline (baseline {Baseline}).",
                baseline);
        }

        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();

        // Only back up when there is real data to protect AND schema will change.
        if (pending.Count > 0 && hasAppSchema)
        {
            var backupPath = Path.Combine(
                dataRoot, "backups", $"pre-migration-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            logger?.LogInformation(
                LogEvents.Database.MigrationWithBackup, "Applying {Count} pending migration(s); pre-migration backup created first.", pending.Count);
            if (!await backupAsync(backupPath))
                throw new InvalidOperationException(
                    "Pre-migration backup failed; aborting migrate to protect existing data.");
        }
        else if (pending.Count > 0)
        {
            logger?.LogInformation(LogEvents.Database.MigrationFreshDatabase, "Applying {Count} migration(s) to a fresh database.", pending.Count);
        }

        await db.Database.MigrateAsync(ct);
    }

    /// <summary>
    /// Reads whether the app schema exists (has <c>users</c>), whether the EF
    /// migrations-history table exists, and the <c>PRAGMA user_version</c> — using a
    /// single connection open.
    /// </summary>
    private static async Task<(bool HasAppSchema, bool HasHistory, int UserVersion)> ReadSchemaStateAsync(
        MangaPlexDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        try
        {
            var hasAppSchema = await TableExistsAsync(connection, "users", ct);
            var hasHistory = await TableExistsAsync(connection, "__EFMigrationsHistory", ct);

            int userVersion;
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA user_version;";
                var result = await cmd.ExecuteScalarAsync(ct);
                userVersion = result is long l ? (int)l : 0;
            }
            return (hasAppSchema, hasHistory, userVersion);
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
    }

    private static async Task<bool> TableExistsAsync(DbConnection connection, string tableName, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table','view') AND name = $name;";
        var p = cmd.CreateParameter();
        p.ParameterName = "$name";
        p.Value = tableName;
        cmd.Parameters.Add(p);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l && l > 0;
    }

    /// <summary>
    /// Creates the EF migrations-history table (if absent) and records the baseline
    /// migration as already applied, without running its DDL — the tables already
    /// exist (created by EnsureCreated). Idempotent via INSERT OR IGNORE.
    /// </summary>
    private static async Task SeedMigrationsHistoryBaselineAsync(
        MangaPlexDbContext db, string migrationId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL
            );
            """, ct);
        await db.Database.ExecuteSqlRawAsync(
            """INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ({0}, {1});""",
            new object[] { migrationId, ProductInfo.GetVersion() }, ct);
    }
}
