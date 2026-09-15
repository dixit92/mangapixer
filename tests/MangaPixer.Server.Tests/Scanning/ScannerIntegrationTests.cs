namespace com.lifepixer.mangaplex.Tests.Server.Scanning;

using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using com.lifepixer.mangaplex.Server.Scanning;
using com.lifepixer.mangaplex.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Integration tests for the scanner, reconciliation, and lease recovery.
/// Uses synthetic filesystem trees and real file-backed SQLite.
/// </summary>
public sealed class ScannerIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _libRoot;
    private readonly string _dbPath;
    private readonly DbContextOptions<MangaPlexDbContext> _options;

    public ScannerIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangaplex-scan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _libRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(_libRoot);
        _dbPath = Path.Combine(_tempDir, "test.db");
        var connectionString = DatabaseInitialization.BuildConnectionString(_dbPath);
        _options = new DbContextOptionsBuilder<MangaPlexDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPlexDbContext db, LibraryEntity library)> SetupAsync()
    {
        var db = new MangaPlexDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var library = new LibraryEntity
        {
            PublicId = "lib1",
            DisplayName = "Test Library",
            RootPath = _libRoot,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        return (db, library);
    }

    private void CreateSyntheticLibrary()
    {
        // Create a synthetic library structure
        Directory.CreateDirectory(Path.Combine(_libRoot, "Series A"));
        Directory.CreateDirectory(Path.Combine(_libRoot, "Series A", "Volume 1"));
        File.WriteAllText(Path.Combine(_libRoot, "Series A", "Volume 1", "chapter01.cbz"), "fake archive");
        File.WriteAllText(Path.Combine(_libRoot, "Series A", "volume 02.cbz"), "fake archive 2");
        Directory.CreateDirectory(Path.Combine(_libRoot, "Series B"));
        File.WriteAllText(Path.Combine(_libRoot, "Series B", "special.cbz"), "fake special");
        File.WriteAllText(Path.Combine(_libRoot, "loose.txt"), "not an archive");
    }

    [Fact]
    public async Task Scan_FirstRun_AddsAllNodes()
    {
        CreateSyntheticLibrary();
        var (db, library) = await SetupAsync();

        var fs = new ReadOnlyLibraryFileSystem(_libRoot);
        var policy = new LibraryScanPolicy();
        var coordinator = new LibraryScanCoordinator(db, fs, policy, library.Id, scanRevision: 1, leaseOwner: "test");

        var result = await coordinator.ScanAsync();

        Assert.True(result.Success);
        // 3 folders (Series A, Series A/Volume 1, Series B) + 3 archives = 6
        Assert.Equal(6, result.NodesObserved);
        Assert.True(result.NodesAdded > 0);

        // Verify nodes were created
        var nodes = await db.CatalogNodes.Where(n => n.LibraryId == library.Id).ToListAsync();
        Assert.Contains(nodes, n => n.DisplayName == "Series A" && n.Kind == 0);
        Assert.Contains(nodes, n => n.DisplayName == "Series B" && n.Kind == 0);
        Assert.Contains(nodes, n => n.DisplayName == "Volume 1" && n.Kind == 0);
        Assert.Contains(nodes, n => n.DisplayName == "chapter01.cbz" && n.Kind == 1);
        Assert.Contains(nodes, n => n.DisplayName == "volume 02.cbz" && n.Kind == 1);
        Assert.Contains(nodes, n => n.DisplayName == "special.cbz" && n.Kind == 1);

        // loose.txt should NOT be in the catalog (not an archive)
        Assert.DoesNotContain(nodes, n => n.DisplayName == "loose.txt");
    }

    [Fact]
    public async Task Scan_SecondRun_IsIdempotent()
    {
        CreateSyntheticLibrary();
        var (db, library) = await SetupAsync();

        var fs = new ReadOnlyLibraryFileSystem(_libRoot);
        var policy = new LibraryScanPolicy();

        // First scan
        var coord1 = new LibraryScanCoordinator(db, fs, policy, library.Id, 1, "test");
        var result1 = await coord1.ScanAsync();
        Assert.True(result1.Success);

        var nodesAfterFirst = await db.CatalogNodes.Where(n => n.LibraryId == library.Id).ToListAsync();
        var firstNodeIds = nodesAfterFirst.Select(n => n.Id).ToHashSet();

        // Second scan (no changes)
        var coord2 = new LibraryScanCoordinator(db, fs, policy, library.Id, 2, "test");
        var result2 = await coord2.ScanAsync();
        Assert.True(result2.Success);
        Assert.Equal(0, result2.NodesAdded); // No new nodes

        // IDs should be preserved
        var nodesAfterSecond = await db.CatalogNodes.Where(n => n.LibraryId == library.Id).ToListAsync();
        var secondNodeIds = nodesAfterSecond.Select(n => n.Id).ToHashSet();
        Assert.Equal(firstNodeIds, secondNodeIds);
    }

    [Fact]
    public async Task Scan_NewFile_AddedOnNextScan()
    {
        CreateSyntheticLibrary();
        var (db, library) = await SetupAsync();

        var fs = new ReadOnlyLibraryFileSystem(_libRoot);
        var policy = new LibraryScanPolicy();

        var coord1 = new LibraryScanCoordinator(db, fs, policy, library.Id, 1, "test");
        await coord1.ScanAsync();

        var countAfterFirst = await db.CatalogNodes.CountAsync(n => n.LibraryId == library.Id);

        // Add a new archive
        File.WriteAllText(Path.Combine(_libRoot, "new.cbz"), "new archive");

        var coord2 = new LibraryScanCoordinator(db, fs, policy, library.Id, 2, "test");
        var result2 = await coord2.ScanAsync();

        Assert.True(result2.Success);
        Assert.Equal(1, result2.NodesAdded);

        var countAfterSecond = await db.CatalogNodes.CountAsync(n => n.LibraryId == library.Id);
        Assert.Equal(countAfterFirst + 1, countAfterSecond);
    }

    [Fact]
    public async Task Scan_RemovedFile_Tombstoned()
    {
        CreateSyntheticLibrary();
        var (db, library) = await SetupAsync();

        var fs = new ReadOnlyLibraryFileSystem(_libRoot);
        var policy = new LibraryScanPolicy();

        var coord1 = new LibraryScanCoordinator(db, fs, policy, library.Id, 1, "test");
        await coord1.ScanAsync();

        // Remove an archive
        File.Delete(Path.Combine(_libRoot, "Series B", "special.cbz"));

        var coord2 = new LibraryScanCoordinator(db, fs, policy, library.Id, 2, "test");
        var result2 = await coord2.ScanAsync();

        Assert.True(result2.Success);

        // The removed file should be tombstoned (availability = 5)
        var tombstoned = await db.CatalogNodes
            .FirstOrDefaultAsync(n => n.LibraryId == library.Id && n.DisplayName == "special.cbz");
        Assert.NotNull(tombstoned);
        Assert.Equal(5, tombstoned!.Availability);
    }

    [Fact]
    public async Task Scan_ReappearingFile_RegainsAvailability()
    {
        CreateSyntheticLibrary();
        var (db, library) = await SetupAsync();

        var fs = new ReadOnlyLibraryFileSystem(_libRoot);
        var policy = new LibraryScanPolicy();

        // First scan
        var coord1 = new LibraryScanCoordinator(db, fs, policy, library.Id, 1, "test");
        await coord1.ScanAsync();

        // Remove and tombstone
        File.Delete(Path.Combine(_libRoot, "Series B", "special.cbz"));
        var coord2 = new LibraryScanCoordinator(db, fs, policy, library.Id, 2, "test");
        await coord2.ScanAsync();

        // Re-create the file
        File.WriteAllText(Path.Combine(_libRoot, "Series B", "special.cbz"), "restored archive");
        var coord3 = new LibraryScanCoordinator(db, fs, policy, library.Id, 3, "test");
        await coord3.ScanAsync();

        // Should be available again
        var restored = await db.CatalogNodes
            .FirstOrDefaultAsync(n => n.LibraryId == library.Id && n.DisplayName == "special.cbz");
        Assert.NotNull(restored);
        Assert.Equal(0, restored!.Availability); // available
    }

    [Fact]
    public async Task Scan_RootUnavailable_ReturnsError()
    {
        var (db, library) = await SetupAsync();

        // Point to a non-existent root
        var fs = new ReadOnlyLibraryFileSystem(Path.Combine(_tempDir, "nonexistent"));
        var policy = new LibraryScanPolicy();
        var coordinator = new LibraryScanCoordinator(db, fs, policy, library.Id, 1, "test");

        var result = await coordinator.ScanAsync();

        Assert.False(result.Success);
        Assert.Equal("root_unavailable", result.Error);
    }

    [Fact]
    public async Task Scan_IgnoredDirectories_NotScanned()
    {
        CreateSyntheticLibrary();
        Directory.CreateDirectory(Path.Combine(_libRoot, ".yacreader"));
        File.WriteAllText(Path.Combine(_libRoot, ".yacreader", "metadata.ini"), "ignored");

        var (db, library) = await SetupAsync();
        var fs = new ReadOnlyLibraryFileSystem(_libRoot);
        var policy = new LibraryScanPolicy();
        var coordinator = new LibraryScanCoordinator(db, fs, policy, library.Id, 1, "test");

        var result = await coordinator.ScanAsync();
        Assert.True(result.Success);

        var nodes = await db.CatalogNodes.Where(n => n.LibraryId == library.Id).ToListAsync();
        Assert.DoesNotContain(nodes, n => n.DisplayName == ".yacreader");
    }

    [Fact]
    public async Task ScanLease_AcquireAndRelease()
    {
        var (db, library) = await SetupAsync();
        var leaseService = new ScanLeaseService(db);

        var lease = await leaseService.AcquireLeaseAsync(library.Id, "test-worker", TimeSpan.FromMinutes(10));
        Assert.NotNull(lease);
        Assert.Equal("test-worker", lease!.LeaseOwner);

        // Cannot acquire another lease while one is active
        var secondLease = await leaseService.AcquireLeaseAsync(library.Id, "other-worker", TimeSpan.FromMinutes(10));
        Assert.Null(secondLease);

        // Release the lease
        await leaseService.ReleaseLeaseAsync(lease.Id, success: true);

        // Now can acquire again
        var thirdLease = await leaseService.AcquireLeaseAsync(library.Id, "test-worker", TimeSpan.FromMinutes(10));
        Assert.NotNull(thirdLease);
    }

    [Fact]
    public async Task ScanCancel_RunningScan_MarksCancelledNotFailed()
    {
        var (db, library) = await SetupAsync();
        var leaseService = new ScanLeaseService(db);

        var lease = await leaseService.AcquireLeaseAsync(library.Id, "test-worker", TimeSpan.FromMinutes(10));
        Assert.NotNull(lease);

        // Cancelling a running scan marks it cancelled (4), not failed (3).
        // This is the terminal state the cancel handler must produce — a prior
        // bug let a subsequent ReleaseLeaseAsync(success:false) overwrite it to
        // failed (audit defect D34 / D39 cancel UX).
        var cancelled = await leaseService.CancelScanAsync(lease!.Id);
        Assert.True(cancelled);

        var scanRun = await db.ScanRuns.FirstAsync(s => s.Id == lease.Id);
        Assert.Equal(4, scanRun.Status); // cancelled
        Assert.Null(scanRun.LeaseExpiry); // lease released
    }

    [Fact]
    public async Task ScanCancel_CompletedScan_IsNoOp()
    {
        var (db, library) = await SetupAsync();
        var leaseService = new ScanLeaseService(db);

        var lease = await leaseService.AcquireLeaseAsync(library.Id, "test-worker", TimeSpan.FromMinutes(10));
        await leaseService.ReleaseLeaseAsync(lease!.Id, success: true); // status = completed (2)

        // A late cancel (e.g. only the post-scan analysis enqueue was cancelled)
        // must not downgrade an already-completed scan. CancelScanAsync is guarded
        // on Status==1, so it is a no-op here.
        var cancelled = await leaseService.CancelScanAsync(lease.Id);
        Assert.False(cancelled);

        var scanRun = await db.ScanRuns.FirstAsync(s => s.Id == lease.Id);
        Assert.Equal(2, scanRun.Status); // still completed
    }

    [Fact]
    public async Task ScanLease_ExpiredLease_RecoveredOnStartup()
    {
        var (db, library) = await SetupAsync();
        var leaseService = new ScanLeaseService(db);

        // Acquire a lease with a very short duration
        var lease = await leaseService.AcquireLeaseAsync(library.Id, "crashed-worker", TimeSpan.FromMilliseconds(1));
        Assert.NotNull(lease);

        // Wait for it to expire
        await Task.Delay(100);

        // Recover expired leases
        var recovered = await leaseService.RecoverExpiredLeasesAsync();
        Assert.Equal(1, recovered);

        // The lease should be marked as interrupted
        var scanRun = await db.ScanRuns.FirstAsync(s => s.Id == lease!.Id);
        Assert.Equal(5, scanRun.Status); // interrupted
    }

    [Fact]
    public async Task ScanMaintenance_EnterAndExit()
    {
        var (db, library) = await SetupAsync();
        var maintenance = new LibraryMaintenanceService(db);

        await maintenance.EnterMaintenanceAsync(library.Id);
        Assert.True(await maintenance.IsInMaintenanceAsync(library.Id));

        await maintenance.ExitMaintenanceAsync(library.Id);
        Assert.False(await maintenance.IsInMaintenanceAsync(library.Id));
    }
}
