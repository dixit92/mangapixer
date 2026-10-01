namespace com.lifepixer.mangapixer.Tests.Server.Scanning;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Scanning;
using com.lifepixer.mangapixer.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests for the tombstone lifecycle (1.31.0 step 0): a scan stamps <c>TombstonedAt</c> when it tombstones a
/// missing node (the start of the move window / trash retention) and clears it when the node is seen again. Each scan runs on a
/// fresh DbContext, as the scan launcher does.
/// </summary>
public sealed class TombstoneLifecycleTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _libRoot;
    private readonly DbContextOptions<MangaPixerDbContext> _options;
    private long _libraryId;

    public TombstoneLifecycleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-tombstone-" + Guid.NewGuid().ToString("N")[..8]);
        _libRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(_libRoot);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "test.db")))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private MangaPixerDbContext NewContext() => new(_options);

    private async Task SetupAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        var library = new LibraryEntity { PublicId = "lib1", DisplayName = "Test", RootPath = _libRoot, CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        _libraryId = library.Id;
    }

    private async Task<ScanResult> ScanAsync(long revision)
    {
        await using var db = NewContext();
        var coordinator = new LibraryScanCoordinator(db, new ReadOnlyLibraryFileSystem(_libRoot), new LibraryScanPolicy(), _libraryId, revision, "test");
        return await coordinator.ScanAsync();
    }

    private void WriteArchive(string name, int seed)
    {
        var data = new byte[4096];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(Path.Combine(_libRoot, name), data);
    }

    private async Task<CatalogNodeEntity> NodeAsync(string pathKey)
    {
        await using var db = NewContext();
        return await db.CatalogNodes.AsNoTracking().SingleAsync(n => n.LibraryId == _libraryId && n.PathKey == pathKey);
    }

    [Fact]
    public async Task Scan_StampsTombstonedAt_WhenItTombstones_AndClearsItWhenTheFileReturns()
    {
        await SetupAsync();
        WriteArchive("kept.cbz", 1);
        WriteArchive("gone.cbz", 2);
        Assert.Equal(2, (await ScanAsync(1)).NodesAdded);
        Assert.Null((await NodeAsync("gone.cbz")).TombstonedAt);

        var bytes = File.ReadAllBytes(Path.Combine(_libRoot, "gone.cbz"));
        File.Delete(Path.Combine(_libRoot, "gone.cbz"));
        var before = DateTimeOffset.UtcNow;
        Assert.Equal(1, (await ScanAsync(2)).NodesTombstoned);

        var tombstoned = await NodeAsync("gone.cbz");
        Assert.Equal(5, tombstoned.Availability);
        Assert.NotNull(tombstoned.TombstonedAt);
        Assert.True(tombstoned.TombstonedAt >= before.AddSeconds(-1));
        Assert.Equal(tombstoned.UpdatedAt, tombstoned.TombstonedAt);
        Assert.Null((await NodeAsync("kept.cbz")).TombstonedAt);

        // A later scan that still misses it leaves the original time alone (the window does not restart).
        await ScanAsync(3);
        Assert.Equal(tombstoned.TombstonedAt, (await NodeAsync("gone.cbz")).TombstonedAt);

        File.WriteAllBytes(Path.Combine(_libRoot, "gone.cbz"), bytes);
        await ScanAsync(4);
        var back = await NodeAsync("gone.cbz");
        Assert.Equal(0, back.Availability);
        Assert.Null(back.TombstonedAt);
    }

    [Fact]
    public async Task NoTombstoneHolds_HoldsNothing()
    {
        await SetupAsync();
        WriteArchive("kept.cbz", 1);
        WriteArchive("gone.cbz", 2);
        await ScanAsync(1);
        File.Delete(Path.Combine(_libRoot, "gone.cbz"));
        await ScanAsync(2);

        await using var db = NewContext();
        ITombstoneHolds holds = new NoTombstoneHolds(db);
        Assert.Empty(await holds.HeldNodeIds().ToListAsync());
        // Composes into a set-based query (what the trash does).
        var purgeable = await db.CatalogNodes.Where(n => n.Availability == 5 && !holds.HeldNodeIds().Contains(n.Id)).CountAsync();
        Assert.Equal(1, purgeable);
    }
}
