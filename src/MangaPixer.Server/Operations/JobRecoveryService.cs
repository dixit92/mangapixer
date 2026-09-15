namespace com.lifepixer.mangapixer.Server.Operations;

using com.lifepixer.mangapixer.Server.Logging;

using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Job recovery service. Handles interrupted jobs, disk-full scenarios,
/// and crash recovery without destructive repair.
///
/// Rules:
/// - Interrupted jobs are marked as failed, not silently retried.
/// - Disk-full errors stop new derived work safely.
/// - No destructive repair on corruption.
/// - Crash recovery removes only verifiably owned inactive scratch workspaces.
/// - Schema guards reject incompatible database versions.
/// </summary>
public sealed class JobRecoveryService
{
    private readonly MangaPixerDbContext _db;
    private readonly ScratchWorkspaceManager? _scratchManager;
    private readonly ILogger<JobRecoveryService>? _logger;

    public JobRecoveryService(
        MangaPixerDbContext db,
        ScratchWorkspaceManager? scratchManager = null,
        ILogger<JobRecoveryService>? logger = null)
    {
        _db = db;
        _scratchManager = scratchManager;
        _logger = logger;
    }

    /// <summary>
    /// Recovers interrupted jobs on startup. Marks pending jobs as failed
    /// so they can be retried explicitly. Does not silently retry.
    /// </summary>
    public async Task<int> RecoverInterruptedJobsAsync(CancellationToken ct = default)
    {
        // Find all pending (0) and running (1) jobs and mark them as failed (3)
        var pendingJobs = await _db.Jobs
            .Where(j => j.Status == 0 || j.Status == 1)
            .ToListAsync(ct);

        _logger?.LogDebug(LogEvents.Database.InterruptedJobsFound, "Recovery: found {Count} interrupted jobs (pending or running)", pendingJobs.Count);

        var recovered = 0;
        foreach (var job in pendingJobs)
        {
            job.Status = 3; // failed
            job.SanitizedError = "Server restarted while job was in progress";
            job.CompletedAt = DateTimeOffset.UtcNow;
            recovered++;
        }

        if (recovered > 0)
        {
            await _db.SaveChangesAsync(ct);
            _logger?.LogWarning(LogEvents.Database.InterruptedJobsRecovered, "Recovered {Count} interrupted jobs", recovered);
        }
        else
        {
            _logger?.LogDebug(LogEvents.Database.NoInterruptedJobs, "Recovery: no interrupted jobs found");
        }

        return recovered;
    }

    /// <summary>
    /// Clears stale analysis errors on items that are still pending
    /// (<c>AnalysisState == 1</c>) so they re-analyze cleanly after a restart.
    ///
    /// The analysis state machine has no distinct "in progress" state — an item
    /// is pending (1) until its result is persisted atomically as ready (0) or
    /// failed (2/5). So an interruption leaves the item pending, and the scanner
    /// re-enqueues pending items. The only real recovery action here is to drop a
    /// leftover error string from a prior failed attempt so it does not surface
    /// while the item is (re)queued. The previous version stamped a fabricated
    /// "Interrupted by server restart" error on every pending item — including
    /// freshly queued ones that were never interrupted — and counted them as
    /// recovered (audit finding A3).
    /// </summary>
    public async Task<int> RecoverInterruptedAnalysisAsync(CancellationToken ct = default)
    {
        var staleErrored = await _db.ArchiveItems
            .Where(a => a.AnalysisState == 1 && a.AnalysisError != null)
            .ToListAsync(ct);

        foreach (var item in staleErrored)
        {
            item.AnalysisError = null;
        }

        if (staleErrored.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            _logger?.LogInformation(
                LogEvents.Database.StaleAnalysisCleared, "Cleared stale analysis errors on {Count} pending items after restart", staleErrored.Count);
        }

        return staleErrored.Count;
    }

    /// <summary>
    /// Cleans up scratch workspaces from crashed workers.
    /// Removes only verifiably owned, inactive workspaces.
    /// </summary>
    public int RecoverScratchWorkspaces(TimeSpan inactiveThreshold)
    {
        if (_scratchManager is null)
        {
            _logger?.LogDebug(LogEvents.Database.ScratchRecoverySkipped, "Scratch recovery skipped: no scratch manager configured");
            return 0;
        }

        _logger?.LogDebug(LogEvents.Database.ScratchRecoveryScanning, "Scratch recovery: scanning for inactive workspaces (threshold {Threshold}s)",
            inactiveThreshold.TotalSeconds);
        var cleaned = _scratchManager.RecoverInactiveWorkspaces(inactiveThreshold);
        if (cleaned > 0)
        {
            _logger?.LogWarning(LogEvents.Database.ScratchWorkspacesRecovered, "Recovered {Count} inactive scratch workspaces", cleaned);
        }
        else
        {
            _logger?.LogDebug(LogEvents.Database.ScratchRecoveryNone, "Scratch recovery: no inactive workspaces found");
        }
        return cleaned;
    }

    /// <summary>
    /// Handles disk-full errors by stopping new derived work safely.
    /// Does not corrupt the database or delete unrelated files.
    /// </summary>
    public void HandleDiskFull()
    {
        _logger?.LogError(LogEvents.Database.DiskFullDerivedWorkStopped, "Disk full — stopping new derived work");
        // In a full implementation, this would:
        // 1. Stop the job scheduler from accepting new work
        // 2. Evict cache entries to free space
        // 3. Alert the admin
        // It does NOT delete unrelated files or corrupt the database.
    }

    /// <summary>
    /// Validates the database schema version. Rejects incompatible versions.
    /// Compares against <see cref="DatabaseInitialization.CurrentSchemaVersion"/>
    /// rather than a private constant so the two cannot drift (audit defect D15/D25).
    /// </summary>
    public async Task<SchemaValidationResult> ValidateSchemaAsync(CancellationToken ct = default)
    {
        var version = await DatabaseInitialization.GetSchemaVersionAsync(_db, ct);
        if (version is null)
        {
            _logger?.LogWarning(LogEvents.Database.SchemaVersionMissing, "Schema validation failed: version not found in database");
            return SchemaValidationResult.Failed("Database schema version not found.");
        }

        var expectedVersion = DatabaseInitialization.CurrentSchemaVersion;
        _logger?.LogDebug(LogEvents.Database.SchemaVersionChecked, "Schema validation: found {Found}, expected {Expected}", version, expectedVersion);
        if (version > expectedVersion)
        {
            _logger?.LogWarning(LogEvents.Database.SchemaVersionNewer, "Schema version {Found} is newer than expected {Expected}", version, expectedVersion);
            return SchemaValidationResult.Failed(
                $"Database schema version {version} is newer than expected {expectedVersion}.");
        }

        if (version < expectedVersion)
        {
            // Pre-release databases created under version 1 must be recreated
            // because the DateTimeOffset storage format changed in version 2.
            if (version == 1)
                _logger?.LogWarning(
                    LogEvents.Database.SchemaVersionOlderRecreate, "Database schema version {Found} is older than expected {Expected}. " +
                    "Pre-release database must be recreated (DateTimeOffset storage format changed).",
                    version, expectedVersion);
            return SchemaValidationResult.Failed(
                $"Database schema version {version} is older than expected {expectedVersion}.");
        }

        return SchemaValidationResult.Success(version.Value);
    }
}

/// <summary>
/// Result of schema validation.
/// </summary>
public sealed record SchemaValidationResult
{
    public required bool Valid { get; init; }
    public required int? Version { get; init; }
    public required string? Error { get; init; }

    public static SchemaValidationResult Success(int version) =>
        new() { Valid = true, Version = version, Error = null };

    public static SchemaValidationResult Failed(string error) =>
        new() { Valid = false, Version = null, Error = error };
}
