namespace com.lifepixer.mangapixer.Server.Persistence;

using com.lifepixer.mangapixer.Server.Logging;

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
    public static DbContextOptionsBuilder<MangaPixerDbContext> ConfigureSqlite(
        this DbContextOptionsBuilder<MangaPixerDbContext> builder,
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
    public static async Task ConfigureDatabaseAsync(MangaPixerDbContext db, ILogger? logger = null, CancellationToken ct = default)
    {
        // Enable foreign keys on this connection (1.47.0: every other connection gets the same settings from
        // SqliteConnectionSettingsInterceptor - before, only this start-up connection had them)
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;", ct);

        // WAL mode for concurrent readers + single writer
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", ct);

        // FULL synchronous for durability of private reading state
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous = FULL;", ct);

        // Bounded busy timeout (also set in connection string, but enforce here)
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout = 30000;", ct);

        // Temp store in memory for performance
        await db.Database.ExecuteSqlRawAsync("PRAGMA temp_store = MEMORY;", ct);

        // The plain search index (1.49.0, lane S): FTS5 trigram over the display NAME only, accents folded. Rebuilt once when its
        // definition changes (the stamp in fts_definitions) - CREATE ... IF NOT EXISTS alone never changed an existing table.
        await EnsureFtsDefinitionAsync(db, CatalogSearch, logger, ct);

        await ConfigureSeriesSearchAsync(db, logger, ct);

        // Set the schema version so startup validation can detect incompatible databases
        await SetSchemaVersionAsync(db, CurrentSchemaVersion, ct);
    }

    /// <summary>
    /// Alt-title search index (1.26.0): one FTS5 trigram row per title of each
    /// metadata record (Title plus every AltTitlesJson entry), kept in sync by
    /// triggers on metadata_records only. catalog_search and the catalog_nodes
    /// triggers are deliberately untouched (scan-performance invariant). The JSON
    /// is guarded with json_valid so a malformed value can never fail a record write.
    /// 1.49.0: accents folded (<c>remove_diacritics 1</c>), rebuilt once through the definition stamp.
    /// </summary>
    private static async Task ConfigureSeriesSearchAsync(MangaPixerDbContext db, ILogger? logger, CancellationToken ct)
    {
        // Databases migrated only to a point before metadata_records exists (tests) have nothing to index.
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'metadata_records';";
            if (await probe.ExecuteScalarAsync(ct) is null)
                return;
        }

        await EnsureFtsDefinitionAsync(db, SeriesSearch, logger, ct);
    }

    /// <summary>
    /// A raw-SQL FTS5 table with its sync triggers and backfills (1.49.0, lane S). <see cref="CreateTable"/> is also the definition
    /// stamped in <c>fts_definitions</c>: when the stored text differs (or there is none - a database from before 1.49.0), the table is
    /// dropped, recreated and backfilled at start-up (<see cref="EnsureFtsDefinitionAsync"/>). Change the text to rebuild the table once.
    /// </summary>
    internal sealed record FtsDefinition(
        string Name, string CreateTable, IReadOnlyList<string> TriggerNames, IReadOnlyList<string> CreateTriggers, string FullBackfill,
        string MissingBackfill);

    /// <summary>
    /// The plain search index (1.49.0): the display NAME only (decision 4 - names before paths; an archive found only through its folder
    /// is counted on that folder's card from the tree), accents folded. The update trigger fires only when a searchable column changes
    /// (1.5.0: every scan touches every node's LastSeenScanRevision).
    /// </summary>
    internal static readonly FtsDefinition CatalogSearch = new(
        "catalog_search",
        "CREATE VIRTUAL TABLE catalog_search USING fts5(display_name, library_id UNINDEXED, node_id UNINDEXED, tokenize='trigram remove_diacritics 1');",
        ["catalog_search_ai", "catalog_search_au", "catalog_search_ad"],
        [
            """
            CREATE TRIGGER IF NOT EXISTS catalog_search_ai
            AFTER INSERT ON catalog_nodes
            BEGIN
                INSERT INTO catalog_search(display_name, library_id, node_id)
                VALUES (new.DisplayName, new.LibraryId, new.Id);
            END;
            """,
            """
            CREATE TRIGGER IF NOT EXISTS catalog_search_au
            AFTER UPDATE OF DisplayName, LibraryId ON catalog_nodes
            BEGIN
                DELETE FROM catalog_search WHERE node_id = old.Id;
                INSERT INTO catalog_search(display_name, library_id, node_id)
                VALUES (new.DisplayName, new.LibraryId, new.Id);
            END;
            """,
            """
            CREATE TRIGGER IF NOT EXISTS catalog_search_ad
            AFTER DELETE ON catalog_nodes
            BEGIN
                DELETE FROM catalog_search WHERE node_id = old.Id;
            END;
            """,
        ],
        """
        INSERT INTO catalog_search(display_name, library_id, node_id)
        SELECT DisplayName, LibraryId, Id FROM catalog_nodes;
        """,
        """
        INSERT INTO catalog_search(display_name, library_id, node_id)
        SELECT DisplayName, LibraryId, Id
        FROM catalog_nodes
        WHERE Id NOT IN (SELECT node_id FROM catalog_search);
        """);

    /// <summary>The alt-title index (1.26.0; 1.49.0 accents folded): every title of each metadata record.</summary>
    internal static readonly FtsDefinition SeriesSearch = new(
        "series_search",
        "CREATE VIRTUAL TABLE series_search USING fts5(title, record_id UNINDEXED, tokenize='trigram remove_diacritics 1');",
        ["series_search_ai", "series_search_au", "series_search_ad"],
        [
            """
            CREATE TRIGGER IF NOT EXISTS series_search_ai
            AFTER INSERT ON metadata_records
            BEGIN
                INSERT INTO series_search(title, record_id) VALUES (new.Title, new.Id);
                INSERT INTO series_search(title, record_id)
                SELECT value, new.Id
                FROM json_each(CASE WHEN json_valid(new.AltTitlesJson) THEN new.AltTitlesJson ELSE '[]' END)
                WHERE type = 'text' AND value <> '';
            END;
            """,
            """
            CREATE TRIGGER IF NOT EXISTS series_search_au
            AFTER UPDATE OF Title, AltTitlesJson ON metadata_records
            BEGIN
                DELETE FROM series_search WHERE record_id = old.Id;
                INSERT INTO series_search(title, record_id) VALUES (new.Title, new.Id);
                INSERT INTO series_search(title, record_id)
                SELECT value, new.Id
                FROM json_each(CASE WHEN json_valid(new.AltTitlesJson) THEN new.AltTitlesJson ELSE '[]' END)
                WHERE type = 'text' AND value <> '';
            END;
            """,
            """
            CREATE TRIGGER IF NOT EXISTS series_search_ad
            AFTER DELETE ON metadata_records
            BEGIN
                DELETE FROM series_search WHERE record_id = old.Id;
            END;
            """,
        ],
        """
        INSERT INTO series_search(title, record_id)
        SELECT r.Title, r.Id FROM metadata_records r
        UNION ALL
        SELECT j.value, r.Id
        FROM metadata_records r,
             json_each(CASE WHEN json_valid(r.AltTitlesJson) THEN r.AltTitlesJson ELSE '[]' END) j
        WHERE j.type = 'text' AND j.value <> '';
        """,
        // One statement so the NOT IN list is built once (record_id is UNINDEXED).
        """
        INSERT INTO series_search(title, record_id)
        SELECT t, id FROM (
            SELECT r.Id AS id, r.Title AS t FROM metadata_records r
            UNION ALL
            SELECT r.Id, j.value
            FROM metadata_records r,
                 json_each(CASE WHEN json_valid(r.AltTitlesJson) THEN r.AltTitlesJson ELSE '[]' END) j
            WHERE j.type = 'text' AND j.value <> ''
        )
        WHERE id NOT IN (SELECT record_id FROM series_search);
        """);

    /// <summary>
    /// Brings one FTS table to its current definition (1.49.0, lane S). Same stamp and the table exists: the triggers are made sure of and
    /// only missing rows are added (as before 1.49.0). Otherwise - a changed definition, no stamp (a database from before 1.49.0) or no
    /// table - the triggers and the table are dropped, recreated and fully backfilled, and the stamp written, in ONE transaction (a crash
    /// leaves the old table and stamp, so the next start rebuilds again). Returns whether it rebuilt.
    /// </summary>
    internal static async Task<bool> EnsureFtsDefinitionAsync(MangaPixerDbContext db, FtsDefinition definition, ILogger? logger, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS fts_definitions (name TEXT NOT NULL PRIMARY KEY, definition TEXT NOT NULL);", ct);

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);
        string? stored;
        bool tableExists;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT (SELECT definition FROM fts_definitions WHERE name = $name),
                       EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);
                """;
            command.Parameters.Add(new SqliteParameter("$name", definition.Name));
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            stored = reader.IsDBNull(0) ? null : reader.GetString(0);
            tableExists = reader.GetInt64(1) != 0;
        }

        if (tableExists && string.Equals(stored, definition.CreateTable, StringComparison.Ordinal))
        {
            foreach (var trigger in definition.CreateTriggers)
                await db.Database.ExecuteSqlRawAsync(trigger, ct);
            await db.Database.ExecuteSqlRawAsync(definition.MissingBackfill, ct);
            return false;
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        int rows;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            // Names are the definition's own constants, never input.
            var drops = definition.TriggerNames.Select(t => "DROP TRIGGER IF EXISTS " + t + ";")
                .Append("DROP TABLE IF EXISTS " + definition.Name + ";");
            foreach (var drop in drops)
                await db.Database.ExecuteSqlRawAsync(drop, ct);
            await db.Database.ExecuteSqlRawAsync(definition.CreateTable, ct);
            foreach (var trigger in definition.CreateTriggers)
                await db.Database.ExecuteSqlRawAsync(trigger, ct);
            rows = await db.Database.ExecuteSqlRawAsync(definition.FullBackfill, ct);
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO fts_definitions(name, definition) VALUES ({0}, {1}) ON CONFLICT(name) DO UPDATE SET definition = excluded.definition;",
                new object[] { definition.Name, definition.CreateTable }, ct);
            await transaction.CommitAsync(ct);
        }
        logger?.LogInformation(LogEvents.Search.FtsRebuilt,
            "Search index {Index} rebuilt for its new definition: {Rows} rows in {ElapsedMs} ms (stamp was {Previous})",
            definition.Name, rows, started.ElapsedMilliseconds, stored is null ? (tableExists ? "missing" : "new") : "older");
        return true;
    }

    /// <summary>
    /// Returns the schema version from the database, or null if not initialized.
    /// Used by the startup coordinator to reject newer incompatible schemas.
    /// </summary>
    public static async Task<int?> GetSchemaVersionAsync(MangaPixerDbContext db, CancellationToken ct = default)
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
    public static async Task SetSchemaVersionAsync(MangaPixerDbContext db, int version, CancellationToken ct = default)
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
        MangaPixerDbContext db,
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
            var backupsDir = Path.Combine(dataRoot, "backups");
            Directory.CreateDirectory(backupsDir);
            var backupPath = Path.Combine(backupsDir,
                com.lifepixer.mangapixer.Server.Operations.SafetySnapshotPruner.UniqueName(backupsDir, "pre-migration", DateTime.UtcNow));
            logger?.LogInformation(
                LogEvents.Database.MigrationWithBackup, "Applying {Count} pending migration(s); pre-migration backup created first.", pending.Count);
            if (!await backupAsync(backupPath))
                throw new InvalidOperationException(
                    "Pre-migration backup failed; aborting migrate to protect existing data.");

            // Keep the newest few pre-migration snapshots (only after this one succeeded).
            var pruned = com.lifepixer.mangapixer.Server.Operations.SafetySnapshotPruner.PrunePreMigration(
                Path.GetDirectoryName(backupPath)!);
            if (pruned > 0)
                logger?.LogInformation(LogEvents.Backup.SafetySnapshotsPruned,
                    "Pruned {Count} old {Kind} safety snapshot(s)", pruned, "pre-migration");
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
        MangaPixerDbContext db, CancellationToken ct)
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
        MangaPixerDbContext db, string migrationId, CancellationToken ct)
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
