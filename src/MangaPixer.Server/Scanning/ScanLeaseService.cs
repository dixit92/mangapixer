namespace com.lifepixer.mangapixer.Server.Scanning;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Manages scan leases for crash recovery. A lease is acquired before scanning
/// and released after completion. On startup, expired leases are recovered.
/// </summary>
public sealed class ScanLeaseService
{
    private readonly MangaPixerDbContext _db;

    public ScanLeaseService(MangaPixerDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Acquires a lease for scanning a library. Returns null if a lease is already held.
    /// </summary>
    public async Task<ScanRunEntity?> AcquireLeaseAsync(
        long libraryId,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        // Check for an existing active lease
        var existingLease = await _db.ScanRuns
            .FirstOrDefaultAsync(s => s.LibraryId == libraryId && s.Status == 1, ct); // running

        if (existingLease is not null && existingLease.LeaseExpiry > DateTimeOffset.UtcNow)
            return null; // Active lease exists

        // Create a new scan run
        var library = await _db.Libraries.FirstAsync(l => l.Id == libraryId, ct);
        var scanRun = new ScanRunEntity
        {
            LibraryId = libraryId,
            ScanRevision = library.CatalogRevision + 1,
            Status = 1, // running
            LeaseOwner = owner,
            LeaseExpiry = DateTimeOffset.UtcNow.Add(leaseDuration),
            StartedAt = DateTimeOffset.UtcNow,
        };

        _db.ScanRuns.Add(scanRun);
        await _db.SaveChangesAsync(ct);
        return scanRun;
    }

    /// <summary>
    /// Releases a scan lease, marking the scan as completed or failed.
    /// </summary>
    public async Task ReleaseLeaseAsync(long scanRunId, bool success, string? error = null, CancellationToken ct = default)
    {
        var scanRun = await _db.ScanRuns.FirstOrDefaultAsync(s => s.Id == scanRunId, ct);
        if (scanRun is null) return;

        scanRun.Status = success ? 2 : 3; // completed or failed
        scanRun.CompletedAt = DateTimeOffset.UtcNow;
        scanRun.SanitizedError = error;
        scanRun.LeaseExpiry = null;

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Recovers expired leases on startup. Marks interrupted scans as incomplete.
    /// </summary>
    public async Task<int> RecoverExpiredLeasesAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var runningLeases = await _db.ScanRuns
            .Where(s => s.Status == 1)
            .ToListAsync(ct);
        var expiredLeases = runningLeases
            .Where(s => s.LeaseExpiry < now)
            .ToList();

        foreach (var lease in expiredLeases)
        {
            lease.Status = 5; // interrupted
            lease.SanitizedError = "Lease expired (possible crash)";
            lease.CompletedAt = now;
            lease.LeaseExpiry = null;
        }

        if (expiredLeases.Count > 0)
            await _db.SaveChangesAsync(ct);

        return expiredLeases.Count;
    }

    /// <summary>
    /// Cancels a running scan.
    /// </summary>
    public async Task<bool> CancelScanAsync(long scanRunId, CancellationToken ct = default)
    {
        var scanRun = await _db.ScanRuns.FirstOrDefaultAsync(s => s.Id == scanRunId, ct);
        if (scanRun is null || scanRun.Status != 1) return false;

        scanRun.Status = 4; // cancelled
        scanRun.CompletedAt = DateTimeOffset.UtcNow;
        scanRun.LeaseExpiry = null;

        await _db.SaveChangesAsync(ct);
        return true;
    }
}
