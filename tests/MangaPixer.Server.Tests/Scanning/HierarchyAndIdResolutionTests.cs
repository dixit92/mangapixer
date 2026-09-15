namespace com.lifepixer.mangapixer.Tests.Server.Scanning;

using System.Threading;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Scanning;
using com.lifepixer.mangapixer.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Tests for catalog hierarchy, public-ID resolution,
/// scan truth, entry keys, and scan cancellation.
/// </summary>
public sealed class HierarchyAndIdResolutionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _libRoot;
    private readonly string _dbPath;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public HierarchyAndIdResolutionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-c01-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _libRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(_libRoot);
        _dbPath = Path.Combine(_tempDir, "test.db");
        var connectionString = DatabaseInitialization.BuildConnectionString(_dbPath);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPixerDbContext db, LibraryEntity library)> SetupAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var library = new LibraryEntity
        {
            PublicId = "libpub1",
            DisplayName = "Test Library",
            RootPath = _libRoot,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        return (db, library);
    }

    private void CreateNestedLibrary()
    {
        // Series Alpha/Vol 1/Alpha 001.cbz
        // Series Alpha/Vol 1/Alpha 002.cbz
        // Series Alpha/Vol 2/Beta 001.cbz
        // Loose Stuff/Random.cbz
        // Standalone One-Shot.cbz
        Directory.CreateDirectory(Path.Combine(_libRoot, "Series Alpha"));
        Directory.CreateDirectory(Path.Combine(_libRoot, "Series Alpha", "Vol 1"));
        Directory.CreateDirectory(Path.Combine(_libRoot, "Series Alpha", "Vol 2"));
        Directory.CreateDirectory(Path.Combine(_libRoot, "Loose Stuff"));
        File.WriteAllText(Path.Combine(_libRoot, "Series Alpha", "Vol 1", "Alpha 001.cbz"), "fake");
        File.WriteAllText(Path.Combine(_libRoot, "Series Alpha", "Vol 1", "Alpha 002.cbz"), "fake");
        File.WriteAllText(Path.Combine(_libRoot, "Series Alpha", "Vol 2", "Beta 001.cbz"), "fake");
        File.WriteAllText(Path.Combine(_libRoot, "Loose Stuff", "Random.cbz"), "fake");
        File.WriteAllText(Path.Combine(_libRoot, "Standalone One-Shot.cbz"), "fake");
    }

    // D1: Folder hierarchy is preserved (not flattened).
    [Fact]
    public async Task Scan_NestedFolders_PreservesHierarchy()
    {
        CreateNestedLibrary();
        var (db, library) = await SetupAsync();

        var fs = new ReadOnlyLibraryFileSystem(_libRoot);
        var policy = new LibraryScanPolicy();
        var coordinator = new LibraryScanCoordinator(db, fs, policy, library.Id, 1, "test");
        var result = await coordinator.ScanAsync();

        Assert.True(result.Success);
        // 4 folders (Series Alpha, Vol 1, Vol 2, Loose Stuff) + 5 archives = 9
        Assert.Equal(9, result.NodesObserved);

        // Verify parent chain: Vol 1 → Series Alpha → null
        var vol1 = await db.CatalogNodes.FirstAsync(n => n.LibraryId == library.Id && n.DisplayName == "Vol 1");
        var seriesAlpha = await db.CatalogNodes.FirstAsync(n => n.LibraryId == library.Id && n.DisplayName == "Series Alpha");
        var alpha001 = await db.CatalogNodes.FirstAsync(n => n.LibraryId == library.Id && n.DisplayName == "Alpha 001.cbz");

        Assert.Null(seriesAlpha.ParentId); // root-level folder
        Assert.Equal(seriesAlpha.Id, vol1.ParentId); // Vol 1's parent is Series Alpha
        Assert.Equal(vol1.Id, alpha001.ParentId); // Alpha 001's parent is Vol 1
    }

    // D1: Root-level items have null ParentId.
    [Fact]
    public async Task Scan_RootItems_HaveNullParentId()
    {
        CreateNestedLibrary();
        var (db, library) = await SetupAsync();

        var fs = new ReadOnlyLibraryFileSystem(_libRoot);
        var policy = new LibraryScanPolicy();
        var coordinator = new LibraryScanCoordinator(db, fs, policy, library.Id, 1, "test");
        await coordinator.ScanAsync();

        var standalone = await db.CatalogNodes.FirstAsync(n => n.LibraryId == library.Id && n.DisplayName == "Standalone One-Shot.cbz");
        var looseStuff = await db.CatalogNodes.FirstAsync(n => n.LibraryId == library.Id && n.DisplayName == "Loose Stuff");

        Assert.Null(standalone.ParentId);
        Assert.Null(looseStuff.ParentId);
    }

    // D5/D29: Browse with public library ID returns root items.
    [Fact]
    public async Task Browse_WithPublicLibraryId_ReturnsRootItems()
    {
        CreateNestedLibrary();
        var (db, library) = await SetupAsync();

        var fs = new ReadOnlyLibraryFileSystem(_libRoot);
        var policy = new LibraryScanPolicy();
        var coordinator = new LibraryScanCoordinator(db, fs, policy, library.Id, 1, "test");
        await coordinator.ScanAsync();

        // Create a user and grant access
        var user = new UserEntity
        {
            PublicId = "upub1",
            UserName = "testuser",
            NormalizedUserName = "TESTUSER",
            PasswordHash = "hash",
            SecurityStamp = "stamp",
            IsActive = true,
            IsAdmin = true,
            ForcePasswordChange = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var browseService = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
        var result = await browseService.BrowseAsync(user.Id, library.Id, parentId: null, cursor: null, pageSize: 50);

        // Root should contain: Series Alpha, Loose Stuff, Standalone One-Shot.cbz
        Assert.Equal(3, result.TotalCount);
        var names = result.Items.Select(i => i.DisplayName).ToList();
        Assert.Contains("Series Alpha", names);
        Assert.Contains("Loose Stuff", names);
        Assert.Contains("Standalone One-Shot.cbz", names);
    }

    // D5/D29: Browse with parentId (public ID) returns children.
    [Fact]
    public async Task Browse_WithParentPublicId_ReturnsChildren()
    {
        CreateNestedLibrary();
        var (db, library) = await SetupAsync();

        var fs = new ReadOnlyLibraryFileSystem(_libRoot);
        var policy = new LibraryScanPolicy();
        var coordinator = new LibraryScanCoordinator(db, fs, policy, library.Id, 1, "test");
        await coordinator.ScanAsync();

        var user = new UserEntity
        {
            PublicId = "upub1",
            UserName = "testuser",
            NormalizedUserName = "TESTUSER",
            PasswordHash = "hash",
            SecurityStamp = "stamp",
            IsActive = true,
            IsAdmin = true,
            ForcePasswordChange = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var seriesAlpha = await db.CatalogNodes.FirstAsync(n => n.LibraryId == library.Id && n.DisplayName == "Series Alpha");
        var browseService = new CatalogBrowseService(db, new LibraryAuthorizationService(db));

        // Browse into Series Alpha
        var result = await browseService.BrowseAsync(user.Id, library.Id, parentId: seriesAlpha.Id, cursor: null, pageSize: 50);

        Assert.Equal(2, result.TotalCount);
        var names = result.Items.Select(i => i.DisplayName).ToList();
        Assert.Contains("Vol 1", names);
        Assert.Contains("Vol 2", names);
    }

    // D29: DTO ParentId is the parent's PublicId, not an encoded row ID.
    [Fact]
    public async Task Browse_DtoParentId_IsParentPublicId()
    {
        CreateNestedLibrary();
        var (db, library) = await SetupAsync();

        var fs = new ReadOnlyLibraryFileSystem(_libRoot);
        var policy = new LibraryScanPolicy();
        var coordinator = new LibraryScanCoordinator(db, fs, policy, library.Id, 1, "test");
        await coordinator.ScanAsync();

        var user = new UserEntity
        {
            PublicId = "upub1",
            UserName = "testuser",
            NormalizedUserName = "TESTUSER",
            PasswordHash = "hash",
            SecurityStamp = "stamp",
            IsActive = true,
            IsAdmin = true,
            ForcePasswordChange = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var seriesAlpha = await db.CatalogNodes.FirstAsync(n => n.LibraryId == library.Id && n.DisplayName == "Series Alpha");
        var browseService = new CatalogBrowseService(db, new LibraryAuthorizationService(db));

        var result = await browseService.BrowseAsync(user.Id, library.Id, parentId: seriesAlpha.Id, cursor: null, pageSize: 50);

        // Every child's ParentId should equal Series Alpha's PublicId
        Assert.All(result.Items, item => Assert.Equal(seriesAlpha.PublicId, item.ParentId));
    }

    // D7: Breadcrumbs return the correct trail.
    [Fact]
    public async Task Breadcrumbs_ReturnCorrectTrail()
    {
        CreateNestedLibrary();
        var (db, library) = await SetupAsync();

        var fs = new ReadOnlyLibraryFileSystem(_libRoot);
        var policy = new LibraryScanPolicy();
        var coordinator = new LibraryScanCoordinator(db, fs, policy, library.Id, 1, "test");
        await coordinator.ScanAsync();

        var user = new UserEntity
        {
            PublicId = "upub1",
            UserName = "testuser",
            NormalizedUserName = "TESTUSER",
            PasswordHash = "hash",
            SecurityStamp = "stamp",
            IsActive = true,
            IsAdmin = true,
            ForcePasswordChange = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var alpha001 = await db.CatalogNodes.FirstAsync(n => n.LibraryId == library.Id && n.DisplayName == "Alpha 001.cbz");
        var browseService = new CatalogBrowseService(db, new LibraryAuthorizationService(db));

        var breadcrumbs = await browseService.GetBreadcrumbsAsync(user.Id, alpha001.Id);

        Assert.NotNull(breadcrumbs);
        Assert.Equal(2, breadcrumbs!.Trail.Count);
        Assert.Equal("Series Alpha", breadcrumbs.Trail[0].DisplayName);
        Assert.Equal("Vol 1", breadcrumbs.Trail[1].DisplayName);
    }

    // D7: Neighbors return prev/next archives in the same folder.
    [Fact]
    public async Task Neighbors_ReturnPrevAndNext()
    {
        CreateNestedLibrary();
        var (db, library) = await SetupAsync();

        var fs = new ReadOnlyLibraryFileSystem(_libRoot);
        var policy = new LibraryScanPolicy();
        var coordinator = new LibraryScanCoordinator(db, fs, policy, library.Id, 1, "test");
        await coordinator.ScanAsync();

        var user = new UserEntity
        {
            PublicId = "upub1",
            UserName = "testuser",
            NormalizedUserName = "TESTUSER",
            PasswordHash = "hash",
            SecurityStamp = "stamp",
            IsActive = true,
            IsAdmin = true,
            ForcePasswordChange = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var alpha001 = await db.CatalogNodes.FirstAsync(n => n.LibraryId == library.Id && n.DisplayName == "Alpha 001.cbz");
        var browseService = new CatalogBrowseService(db, new LibraryAuthorizationService(db));

        var neighbors = await browseService.GetNeighborsAsync(user.Id, alpha001.Id);

        Assert.NotNull(neighbors);
        Assert.Null(neighbors!.Previous); // First in folder
        Assert.NotNull(neighbors.Next);
        Assert.Equal("Alpha 002.cbz", neighbors.Next!.DisplayName);
    }

    // D4: Page entry keys are deterministic ordinals, not random.
    [Fact]
    public async Task PageEntryKeys_AreDeterministicOrdinals()
    {
        // Verify the PageEntryKey contract: ToOpaque produces the same
        // string for the same ordinal, and FromOpaque round-trips.
        Assert.Equal(new PageEntryKey(0).ToOpaque(), new PageEntryKey(0).ToOpaque());
        Assert.Equal(new PageEntryKey(5).ToOpaque(), new PageEntryKey(5).ToOpaque());
        Assert.NotEqual(new PageEntryKey(0).ToOpaque(), new PageEntryKey(1).ToOpaque());

        // Round-trip
        var key = new PageEntryKey(42);
        var roundTripped = PageEntryKey.FromOpaque(key.ToOpaque());
        Assert.Equal(42, roundTripped.Ordinal);
    }

    // D8: Scan counters are persisted to ScanRunEntity.
    [Fact]
    public async Task ScanCounters_PersistedToScanRun()
    {
        CreateNestedLibrary();
        var (db, library) = await SetupAsync();

        // Create a scan run record
        var scanRun = new ScanRunEntity
        {
            LibraryId = library.Id,
            ScanRevision = 1,
            Status = 1, // running
            StartedAt = DateTimeOffset.UtcNow,
        };
        db.ScanRuns.Add(scanRun);
        await db.SaveChangesAsync();

        var fs = new ReadOnlyLibraryFileSystem(_libRoot);
        var policy = new LibraryScanPolicy();
        var coordinator = new LibraryScanCoordinator(db, fs, policy, library.Id, 1, "test");
        var result = await coordinator.ScanAsync();

        // Simulate what AdminController.TriggerScan does: persist counters
        scanRun.NodesObserved = result.NodesObserved;
        scanRun.NodesAdded = result.NodesAdded;
        scanRun.NodesUpdated = result.NodesUpdated;
        scanRun.NodesTombstoned = result.NodesTombstoned;
        scanRun.Status = 2; // completed
        scanRun.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        // Verify the persisted counters
        var saved = await db.ScanRuns.FirstAsync(s => s.Id == scanRun.Id);
        Assert.Equal(9, saved.NodesObserved);
        Assert.True(saved.NodesAdded > 0);
        Assert.Equal(2, saved.Status); // completed
    }

    // D34: Scan cancellation cooperatively cancels the scan.
    //
    // Deterministic (no timing window): a controllable filesystem blocks the
    // scan's root enumeration until the test releases it, so the scan is
    // guaranteed to be mid-flight when cancellation is requested. The scan's
    // first ct.ThrowIfCancellationRequested() (at the top of the Observe loop,
    // immediately after root enumeration returns) then observes the
    // already-cancelled token and throws OperationCanceledException. The
    // relative ordering of "cancel", "release", and "scan reaches the ct
    // check" does not matter: cancellation always lands before the scan can
    // pass the check, because the scan cannot enumerate past the root until
    // released, and the test cancels before releasing.
    [Fact]
    public async Task ScanCancellation_CancelsCooperatively()
    {
        CreateNestedLibrary();
        var (db, library) = await SetupAsync();

        var registry = new ScanRunRegistry();
        var scanRunId = 42L;
        var ct = registry.Register(scanRunId);

        var fs = new ControllableLibraryFileSystem(new ReadOnlyLibraryFileSystem(_libRoot));
        var policy = new LibraryScanPolicy();
        var coordinator = new LibraryScanCoordinator(db, fs, policy, library.Id, 1, "test");

        // Start the scan on a background task. It blocks inside
        // EnumerateEntries("") waiting for the root enumeration gate.
        var scanTask = Task.Run(() => coordinator.ScanAsync(ct));

        // Cancel while the scan is blocked (deterministic: the scan cannot
        // progress past root enumeration until we release it below).
        registry.Cancel(scanRunId);

        // Release the scan. It returns from EnumerateEntries("") and hits the
        // first ct.ThrowIfCancellationRequested() in the Observe loop with the
        // token already cancelled, throwing OperationCanceledException.
        fs.ReleaseRootEnumeration();

        await Assert.ThrowsAsync<OperationCanceledException>(() => scanTask);
        registry.Complete(scanRunId);
    }

    /// <summary>
    /// Wraps a <see cref="ReadOnlyLibraryFileSystem"/> and blocks the root
    /// directory enumeration (relativePath == "") until
    /// <see cref="ReleaseRootEnumeration"/> is called. Used by
    /// <see cref="ScanCancellation_CancelsCooperatively"/> to make the scan's
    /// start deterministic so cancellation can be requested while the scan is
    /// mid-flight, instead of racing the scan to completion against an
    /// immediate Cancel() (the scan of a tiny fixture library can finish
    /// before the cancellation lands).
    /// </summary>
    private sealed class ControllableLibraryFileSystem : IReadOnlyLibraryFileSystem
    {
        private readonly IReadOnlyLibraryFileSystem _inner;
        private readonly ManualResetEventSlim _rootGate = new(initialState: false);

        public ControllableLibraryFileSystem(IReadOnlyLibraryFileSystem inner) => _inner = inner;

        public void ReleaseRootEnumeration() => _rootGate.Set();

        public bool RootExists() => _inner.RootExists();
        public DirectoryEntry? GetRoot() => _inner.GetRoot();

        public IReadOnlyList<FileSystemEntry> EnumerateEntries(string relativePath)
        {
            if (relativePath == "")
                _rootGate.Wait();
            return _inner.EnumerateEntries(relativePath);
        }

        public FileSystemEntry? GetEntry(string relativePath) => _inner.GetEntry(relativePath);
        public Stream OpenRead(string relativePath) => _inner.OpenRead(relativePath);
        public SourceStamp GetSourceStamp(string relativePath) => _inner.GetSourceStamp(relativePath);
        public string? GetRootIdentity() => _inner.GetRootIdentity();
    }

    // D34: ScanRunRegistry.Cancel returns false for unknown run.
    [Fact]
    public void ScanRunRegistry_CancelUnknownRun_ReturnsFalse()
    {
        var registry = new ScanRunRegistry();
        Assert.False(registry.Cancel(999));
    }

    // D34: ScanRunRegistry.IsRunning tracks active scans.
    [Fact]
    public void ScanRunRegistry_IsRunning_TracksActiveScans()
    {
        var registry = new ScanRunRegistry();
        var runId = 1L;

        Assert.False(registry.IsRunning(runId));
        registry.Register(runId);
        Assert.True(registry.IsRunning(runId));
        registry.Complete(runId);
        Assert.False(registry.IsRunning(runId));
    }
}
