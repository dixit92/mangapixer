namespace com.lifepixer.mangapixer.Tests.Server.Media;

using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests for the thumbnail generation query and state tracking
/// (1.2.0). These exercise the DB-facing parts of ThumbnailGenerationService
/// that don't require a running media worker:
/// - GetItemsNeedingThumbnailsAsync identifies ready items lacking thumbnails
/// - Content-version invalidation: a source change marks the thumbnail stale
/// - ThumbnailState is tracked correctly on the entity
/// </summary>
public sealed class ThumbnailGenerationServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ThumbnailGenerationServiceTests()
    {
        _tempDir = TestSupport.CreateTempTestRoot("mangapixer-thumb-gen");
    }

    public void Dispose()
    {
        TestSupport.CleanupDirectory(_tempDir);
    }

    private static MangaPixerDbContext NewContext(string dbPath)
    {
        var options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .ConfigureSqlite(dbPath)
            .Options;
        var db = new MangaPixerDbContext(options);
        db.Database.Migrate();
        return db;
    }

    private static async Task<(long LibraryId, long NodeId)> SeedReadyItemAsync(MangaPixerDbContext db)
    {
        var library = new LibraryEntity
        {
            PublicId = "lib_pub_1",
            DisplayName = "Test",
            RootPath = "/media/test",
            CaseComparisonPolicy = "ordinal",
            State = "active",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        var node = new CatalogNodeEntity
        {
            PublicId = "node_pub_1",
            LibraryId = library.Id,
            Kind = 1,
            DisplayName = "test.cbz",
            RelativePath = "test.cbz",
            PathKey = "test.cbz",
            SortKey = "test",
            Availability = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();

        var item = new ArchiveItemEntity
        {
            NodeId = node.Id,
            ContentVersion = 1,
            ArchiveFormat = 0,
            ByteLength = 1024,
            ModificationTicks = DateTimeOffset.UtcNow.Ticks,
            AnalysisState = 0, // ready
            PageCount = 3,
            ThumbnailState = 0, // none
        };
        db.ArchiveItems.Add(item);
        await db.SaveChangesAsync();

        return (library.Id, node.Id);
    }

    [Fact]
    public async Task GetItemsNeedingThumbnails_FindsReadyItemsWithoutThumbnail()
    {
        using var db = NewContext(Path.Combine(_tempDir, "test1.db"));
        var (libraryId, nodeId) = await SeedReadyItemAsync(db);

        var items = await ThumbnailGenerationService.GetItemsNeedingThumbnailsAsync(
            db, libraryId, limit: 100);

        Assert.Single(items);
        Assert.Equal(nodeId, items[0]);
    }

    [Fact]
    public async Task GetItemsNeedingThumbnails_ExcludesItemsWithCurrentThumbnail()
    {
        using var db = NewContext(Path.Combine(_tempDir, "test2.db"));
        var (libraryId, nodeId) = await SeedReadyItemAsync(db);

        // Mark the thumbnail as current
        var item = await db.ArchiveItems.FirstAsync(a => a.NodeId == nodeId);
        item.ThumbnailState = 1;
        item.ThumbnailContentVersion = item.ContentVersion;
        await db.SaveChangesAsync();

        var items = await ThumbnailGenerationService.GetItemsNeedingThumbnailsAsync(
            db, libraryId, limit: 100);

        Assert.Empty(items);
    }

    [Fact]
    public async Task GetItemsNeedingThumbnails_IncludesItemsWithStaleThumbnail()
    {
        using var db = NewContext(Path.Combine(_tempDir, "test3.db"));
        var (libraryId, nodeId) = await SeedReadyItemAsync(db);

        // Mark thumbnail as ready but for an OLD content version (source changed)
        var item = await db.ArchiveItems.FirstAsync(a => a.NodeId == nodeId);
        item.ThumbnailState = 1;
        item.ThumbnailContentVersion = 1;
        item.ContentVersion = 2; // source changed — thumbnail is stale
        await db.SaveChangesAsync();

        var items = await ThumbnailGenerationService.GetItemsNeedingThumbnailsAsync(
            db, libraryId, limit: 100);

        Assert.Single(items); // stale thumbnail needs regeneration
        Assert.Equal(nodeId, items[0]);
    }

    [Fact]
    public async Task GetItemsNeedingThumbnails_IncludesItemsWithFailedThumbnail()
    {
        using var db = NewContext(Path.Combine(_tempDir, "test4.db"));
        var (libraryId, nodeId) = await SeedReadyItemAsync(db);

        // Mark thumbnail as failed
        var item = await db.ArchiveItems.FirstAsync(a => a.NodeId == nodeId);
        item.ThumbnailState = 2; // failed
        await db.SaveChangesAsync();

        var items = await ThumbnailGenerationService.GetItemsNeedingThumbnailsAsync(
            db, libraryId, limit: 100);

        Assert.Single(items);
    }

    [Fact]
    public async Task GetItemsNeedingThumbnails_ExcludesNonReadyItems()
    {
        using var db = NewContext(Path.Combine(_tempDir, "test5.db"));
        var (libraryId, nodeId) = await SeedReadyItemAsync(db);

        // Mark item as pending analysis
        var item = await db.ArchiveItems.FirstAsync(a => a.NodeId == nodeId);
        item.AnalysisState = 1; // pending
        await db.SaveChangesAsync();

        var items = await ThumbnailGenerationService.GetItemsNeedingThumbnailsAsync(
            db, libraryId, limit: 100);

        Assert.Empty(items); // not ready — no thumbnail needed yet
    }

    [Fact]
    public async Task GetItemsNeedingThumbnails_RespectsLimit()
    {
        using var db = NewContext(Path.Combine(_tempDir, "test6.db"));

        var library = new LibraryEntity
        {
            PublicId = "lib_pub",
            DisplayName = "Test",
            RootPath = "/media/test",
            CaseComparisonPolicy = "ordinal",
            State = "active",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        for (int i = 0; i < 10; i++)
        {
            var node = new CatalogNodeEntity
            {
                PublicId = $"node_{i}",
                LibraryId = library.Id,
                Kind = 1,
                DisplayName = $"test{i}.cbz",
                RelativePath = $"test{i}.cbz",
                PathKey = $"test{i}.cbz",
                SortKey = $"test{i}",
                Availability = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.CatalogNodes.Add(node);
            await db.SaveChangesAsync();

            db.ArchiveItems.Add(new ArchiveItemEntity
            {
                NodeId = node.Id,
                ContentVersion = 1,
                ArchiveFormat = 0,
                ByteLength = 1024,
                ModificationTicks = DateTimeOffset.UtcNow.Ticks,
                AnalysisState = 0,
                ThumbnailState = 0,
            });
        }
        await db.SaveChangesAsync();

        var items = await ThumbnailGenerationService.GetItemsNeedingThumbnailsAsync(
            db, library.Id, limit: 3);

        Assert.Equal(3, items.Count);
    }

    [Fact]
    public async Task GetItemsNeedingThumbnails_ExcludesTombstonedNodes()
    {
        using var db = NewContext(Path.Combine(_tempDir, "test7.db"));
        var (libraryId, nodeId) = await SeedReadyItemAsync(db);

        // Tombstone the node
        var node = await db.CatalogNodes.FirstAsync(n => n.Id == nodeId);
        node.Availability = 5; // tombstoned
        await db.SaveChangesAsync();

        var items = await ThumbnailGenerationService.GetItemsNeedingThumbnailsAsync(
            db, libraryId, limit: 100);

        Assert.Empty(items);
    }

    [Fact]
    public async Task CountItemsNeedingThumbnails_FindsReadyItemsWithoutThumbnail()
    {
        using var db = NewContext(Path.Combine(_tempDir, "test8.db"));
        var (libraryId, nodeId) = await SeedReadyItemAsync(db);

        var count = await ThumbnailGenerationService.CountItemsNeedingThumbnailsAsync(db, libraryId);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CountItemsNeedingThumbnails_ExcludesItemsWithCurrentThumbnail()
    {
        using var db = NewContext(Path.Combine(_tempDir, "test9.db"));
        var (libraryId, nodeId) = await SeedReadyItemAsync(db);

        var item = await db.ArchiveItems.FirstAsync(a => a.NodeId == nodeId);
        item.ThumbnailState = 1;
        item.ThumbnailContentVersion = item.ContentVersion;
        await db.SaveChangesAsync();

        var count = await ThumbnailGenerationService.CountItemsNeedingThumbnailsAsync(db, libraryId);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CountItemsNeedingThumbnails_CountsAllNeedingItems_Uncapped()
    {
        // The continuous backfill (post-1.2.0) is uncapped; the count query
        // must report all needing items, not a bounded subset.
        using var db = NewContext(Path.Combine(_tempDir, "test10.db"));

        var library = new LibraryEntity
        {
            PublicId = "lib_count",
            DisplayName = "Test",
            RootPath = "/media/test",
            CaseComparisonPolicy = "ordinal",
            State = "active",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        for (int i = 0; i < 600; i++)
        {
            var node = new CatalogNodeEntity
            {
                PublicId = $"node_{i}",
                LibraryId = library.Id,
                Kind = 1,
                DisplayName = $"test{i}.cbz",
                RelativePath = $"test{i}.cbz",
                PathKey = $"test{i}.cbz",
                SortKey = $"test{i}",
                Availability = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.CatalogNodes.Add(node);
            await db.SaveChangesAsync();

            db.ArchiveItems.Add(new ArchiveItemEntity
            {
                NodeId = node.Id,
                ContentVersion = 1,
                ArchiveFormat = 0,
                ByteLength = 1024,
                ModificationTicks = DateTimeOffset.UtcNow.Ticks,
                AnalysisState = 0,
                ThumbnailState = 0,
            });
        }
        await db.SaveChangesAsync();

        var count = await ThumbnailGenerationService.CountItemsNeedingThumbnailsAsync(db, library.Id);

        Assert.Equal(600, count);
    }
}
