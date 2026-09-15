namespace com.lifepixer.mangaplex.Tests.Server.Scanning;

using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using com.lifepixer.mangaplex.Server.Scanning;
using com.lifepixer.mangaplex.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Tests for the maintained per-folder recency primitive
/// <see cref="CatalogNodeEntity.LatestDescendantAddedAt"/> (1.12.0): the scan-time bubble-up
/// on archive add, the recompute on tombstone, and the one-time backfill. The invariant under
/// test everywhere: a folder's value equals the MAX CreatedAt of its non-tombstoned descendant
/// archives, or null when it has none. Real filesystem + real file-backed SQLite.
/// </summary>
public sealed class LatestDescendantAddedAtTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _libRoot;
    private readonly string _dbPath;
    private readonly DbContextOptions<MangaPlexDbContext> _options;

    public LatestDescendantAddedAtTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangaplex-recency-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _libRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(_libRoot);
        _dbPath = Path.Combine(_tempDir, "test.db");
        _options = new DbContextOptionsBuilder<MangaPlexDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(_dbPath))
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
        var library = new LibraryEntity { PublicId = "lib1", DisplayName = "Test Library", RootPath = _libRoot, CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        return (db, library);
    }

    private async Task ScanAsync(MangaPlexDbContext db, long libraryId, long revision)
    {
        var coordinator = new LibraryScanCoordinator(
            db, new ReadOnlyLibraryFileSystem(_libRoot),
            new LibraryScanPolicy(), libraryId, revision, "test");
        var result = await coordinator.ScanAsync();
        Assert.True(result.Success);
        // The recency maintenance updates via raw SQL (bypassing the change tracker), so detach
        // tracked entities to force post-scan reads to observe the fresh DB values.
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Asserts the invariant for every non-tombstoned folder in the library: its stored
    /// LatestDescendantAddedAt equals the MAX CreatedAt over its non-tombstoned descendant
    /// archives (recursive), or null when it has none.
    /// </summary>
    private static async Task AssertInvariantAsync(MangaPlexDbContext db, long libraryId)
    {
        var nodes = await db.CatalogNodes
            .Where(n => n.LibraryId == libraryId)
            .Select(n => new { n.Id, n.ParentId, n.Kind, n.Availability, n.CreatedAt, n.LatestDescendantAddedAt })
            .ToListAsync();

        var childrenByParent = nodes
            .Where(n => n.ParentId != null)
            .GroupBy(n => n.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        DateTimeOffset? MaxDescendantArchive(long folderId)
        {
            DateTimeOffset? max = null;
            var stack = new Stack<long>();
            stack.Push(folderId);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!childrenByParent.TryGetValue(id, out var kids)) continue;
                foreach (var kid in kids)
                {
                    if (kid.Kind == (int)CatalogNodeKind.Archive && kid.Availability != (int)CatalogNodeAvailability.Tombstoned)
                        if (max is null || kid.CreatedAt > max) max = kid.CreatedAt;
                    if (kid.Kind == (int)CatalogNodeKind.Folder)
                        stack.Push(kid.Id);
                }
            }
            return max;
        }

        foreach (var folder in nodes.Where(n => n.Kind == (int)CatalogNodeKind.Folder
            && n.Availability != (int)CatalogNodeAvailability.Tombstoned))
        {
            var expected = MaxDescendantArchive(folder.Id);
            Assert.Equal(expected, folder.LatestDescendantAddedAt);
        }
    }

    [Fact]
    public async Task Scan_FirstRun_SetsInvariant_ForAllFolders()
    {
        Directory.CreateDirectory(Path.Combine(_libRoot, "Series A", "Volume 1"));
        File.WriteAllText(Path.Combine(_libRoot, "Series A", "Volume 1", "chapter01.cbz"), "a");
        File.WriteAllText(Path.Combine(_libRoot, "Series A", "volume 02.cbz"), "b");
        Directory.CreateDirectory(Path.Combine(_libRoot, "Series B"));
        File.WriteAllText(Path.Combine(_libRoot, "Series B", "special.cbz"), "c");
        Directory.CreateDirectory(Path.Combine(_libRoot, "Empty Series")); // folder with no archives

        var (db, library) = await SetupAsync();

        await ScanAsync(db, library.Id, 1);
        await AssertInvariantAsync(db, library.Id);

        // The empty folder has a null primitive; Series A has a non-null one.
        var empty = await db.CatalogNodes.FirstAsync(n => n.DisplayName == "Empty Series");
        Assert.Null(empty.LatestDescendantAddedAt);
        var seriesA = await db.CatalogNodes.FirstAsync(n => n.DisplayName == "Series A");
        Assert.NotNull(seriesA.LatestDescendantAddedAt);

        await db.DisposeAsync();
    }

    [Fact]
    public async Task Scan_ArchiveAdd_BubblesUpToAncestors()
    {
        Directory.CreateDirectory(Path.Combine(_libRoot, "Series A", "Volume 1"));
        File.WriteAllText(Path.Combine(_libRoot, "Series A", "Volume 1", "chapter01.cbz"), "a");

        var (db, library) = await SetupAsync();
        await ScanAsync(db, library.Id, 1);
        var before = (await db.CatalogNodes.FirstAsync(n => n.DisplayName == "Series A")).LatestDescendantAddedAt;
        Assert.NotNull(before);

        // A later scan adds a newer chapter; its CreatedAt (scan time) is strictly newer.
        await Task.Delay(10);
        File.WriteAllText(Path.Combine(_libRoot, "Series A", "Volume 1", "chapter02.cbz"), "b");
        await ScanAsync(db, library.Id, 2);

        await AssertInvariantAsync(db, library.Id);

        var newChapter = await db.CatalogNodes.FirstAsync(n => n.DisplayName == "chapter02.cbz");
        var seriesA = await db.CatalogNodes.FirstAsync(n => n.DisplayName == "Series A");
        var vol1 = await db.CatalogNodes.FirstAsync(n => n.DisplayName == "Volume 1");
        // Ancestors bubbled up to the newest chapter's CreatedAt.
        Assert.Equal(newChapter.CreatedAt, seriesA.LatestDescendantAddedAt);
        Assert.Equal(newChapter.CreatedAt, vol1.LatestDescendantAddedAt);
        Assert.True(seriesA.LatestDescendantAddedAt > before);

        await db.DisposeAsync();
    }

    [Fact]
    public async Task Scan_ArchiveTombstone_RecomputesAncestorFromRemaining()
    {
        Directory.CreateDirectory(Path.Combine(_libRoot, "Series A", "Volume 1"));
        File.WriteAllText(Path.Combine(_libRoot, "Series A", "Volume 1", "chapter01.cbz"), "a");
        // Second file added in a later scan so it becomes the (newer) current max.
        var (db, library) = await SetupAsync();
        await ScanAsync(db, library.Id, 1);
        await Task.Delay(10);
        File.WriteAllText(Path.Combine(_libRoot, "Series A", "Volume 1", "chapter02.cbz"), "b");
        await ScanAsync(db, library.Id, 2);

        var oldMax = (await db.CatalogNodes.FirstAsync(n => n.DisplayName == "chapter01.cbz")).CreatedAt;
        var seriesA0 = await db.CatalogNodes.FirstAsync(n => n.DisplayName == "Series A");
        var newMax = (await db.CatalogNodes.FirstAsync(n => n.DisplayName == "chapter02.cbz")).CreatedAt;
        Assert.Equal(newMax, seriesA0.LatestDescendantAddedAt); // sanity: newest is the current max

        // Delete the newest chapter; a rescan tombstones it and recomputes ancestors to the
        // remaining (older) chapter's CreatedAt.
        File.Delete(Path.Combine(_libRoot, "Series A", "Volume 1", "chapter02.cbz"));
        await ScanAsync(db, library.Id, 3);

        await AssertInvariantAsync(db, library.Id);

        var seriesA = await db.CatalogNodes.FirstAsync(n => n.DisplayName == "Series A");
        var vol1 = await db.CatalogNodes.FirstAsync(n => n.DisplayName == "Volume 1");
        Assert.Equal(oldMax, seriesA.LatestDescendantAddedAt);
        Assert.Equal(oldMax, vol1.LatestDescendantAddedAt);

        await db.DisposeAsync();
    }

    [Fact]
    public async Task Backfill_Recomputes_AllFolders_FromDescendantArchives()
    {
        var (db, library) = await SetupAsync();

        // Build a tree directly with controlled CreatedAt, leaving LatestDescendantAddedAt null.
        var t1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

        CatalogNodeEntity Folder(string pub, string name, long? parent) => new()
        {
            PublicId = pub,
            LibraryId = library.Id,
            ParentId = parent,
            Kind = (int)CatalogNodeKind.Folder,
            DisplayName = name,
            RelativePath = name,
            PathKey = pub,
            SortKey = "0" + name,
            Availability = 0,
            CreatedAt = t1,
        };
        CatalogNodeEntity Archive(string pub, string name, long? parent, DateTimeOffset at, int avail = 0) => new()
        {
            PublicId = pub,
            LibraryId = library.Id,
            ParentId = parent,
            Kind = (int)CatalogNodeKind.Archive,
            DisplayName = name,
            RelativePath = name,
            PathKey = pub,
            SortKey = "1" + name,
            Availability = avail,
            CreatedAt = at,
        };

        var seriesA = Folder("fa", "Series A", null);
        db.CatalogNodes.Add(seriesA); await db.SaveChangesAsync();
        var vol1 = Folder("fv", "Volume 1", seriesA.Id);
        var empty = Folder("fe", "Empty", null);
        db.CatalogNodes.AddRange(vol1, empty); await db.SaveChangesAsync();
        db.CatalogNodes.AddRange(
            Archive("c1", "Ch1", vol1.Id, t1),
            Archive("c3", "Ch3", vol1.Id, t3),                 // newest, deep under Series A
            Archive("c2", "Ch2", vol1.Id, t2),
            Archive("ct", "ChTomb", vol1.Id, t3.AddDays(10), avail: 5)); // tombstoned: ignored
        await db.SaveChangesAsync();

        // Before backfill: all null.
        Assert.Null((await db.CatalogNodes.FirstAsync(n => n.PublicId == "fa")).LatestDescendantAddedAt);

        await LatestDescendantAddedAtMaintenance.RecomputeAllFoldersAsync(db);
        // Reload from the database (raw SQL update bypasses the tracked entities).
        db.ChangeTracker.Clear();

        await AssertInvariantAsync(db, library.Id);
        Assert.Equal(t3, (await db.CatalogNodes.FirstAsync(n => n.PublicId == "fa")).LatestDescendantAddedAt);
        Assert.Equal(t3, (await db.CatalogNodes.FirstAsync(n => n.PublicId == "fv")).LatestDescendantAddedAt);
        Assert.Null((await db.CatalogNodes.FirstAsync(n => n.PublicId == "fe")).LatestDescendantAddedAt);

        await db.DisposeAsync();
    }
}
