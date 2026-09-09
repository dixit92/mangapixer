namespace com.lifepixer.mangaplex.Tests.Server.Media;

using com.lifepixer.mangaplex.Server.Media;
using com.lifepixer.mangaplex.TestSupport;
using Xunit;

/// <summary>
/// Unit tests for the durable ThumbnailStore: atomic publication, read,
/// delete, sharding, and persistence across re-instantiation (simulating a
/// container restart). The store has no budget and no eviction — thumbnails
/// must survive indefinitely (owner requirement, 2026-09-09).
/// </summary>
public sealed class ThumbnailStoreTests : IDisposable
{
    private readonly string _tempDir;

    public ThumbnailStoreTests()
    {
        _tempDir = TestSupport.CreateTempTestRoot("mangaplex-thumb-store");
    }

    public void Dispose()
    {
        TestSupport.CleanupDirectory(_tempDir);
    }

    [Fact]
    public void Initialize_CreatesThumbnailsRoot()
    {
        var root = Path.Combine(_tempDir, "thumbnails");
        var store = new ThumbnailStore(root);

        store.Initialize();

        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public async Task PublishAsync_CreatesShardedFile()
    {
        var store = new ThumbnailStore(Path.Combine(_tempDir, "thumbnails"));
        store.Initialize();

        var sourceFile = Path.Combine(_tempDir, "source.webp");
        await File.WriteAllBytesAsync(sourceFile, new byte[] { 0x52, 0x49, 0x46, 0x46 });

        await store.PublishAsync(itemId: 12345, contentVersion: 1, sourceFile);

        // The file should exist and be sharded under a 2-char prefix
        var path = store.GetThumbnailPath(12345, 1);
        Assert.True(File.Exists(path));
        Assert.NotEqual(Path.Combine(_tempDir, "thumbnails"), Path.GetDirectoryName(path));
        // The file is in a subdirectory (sharded)
        var relativeDir = Path.GetRelativePath(Path.Combine(_tempDir, "thumbnails"), Path.GetDirectoryName(path)!);
        Assert.Equal(2, relativeDir.Length); // 2-char shard prefix
    }

    [Fact]
    public async Task HasThumbnail_ReturnsTrueAfterPublish_FalseBefore()
    {
        var store = new ThumbnailStore(Path.Combine(_tempDir, "thumbnails"));
        store.Initialize();

        Assert.False(store.HasThumbnail(42, 1));

        var sourceFile = Path.Combine(_tempDir, "source.webp");
        await File.WriteAllBytesAsync(sourceFile, new byte[] { 0x01, 0x02 });

        await store.PublishAsync(42, 1, sourceFile);

        Assert.True(store.HasThumbnail(42, 1));
        Assert.False(store.HasThumbnail(42, 2)); // different content version
    }

    [Fact]
    public async Task OpenRead_ReturnsStreamForExistingFile_NullForMissing()
    {
        var store = new ThumbnailStore(Path.Combine(_tempDir, "thumbnails"));
        store.Initialize();

        Assert.Null(store.OpenRead(99, 1));

        var sourceFile = Path.Combine(_tempDir, "source.webp");
        var content = new byte[] { 0xAB, 0xCD, 0xEF };
        await File.WriteAllBytesAsync(sourceFile, content);

        await store.PublishAsync(99, 1, sourceFile);

        using var stream = store.OpenRead(99, 1);
        Assert.NotNull(stream);
        using var ms = new MemoryStream();
        await stream!.CopyToAsync(ms);
        Assert.Equal(content, ms.ToArray());
    }

    [Fact]
    public async Task Delete_RemovesThumbnail()
    {
        var store = new ThumbnailStore(Path.Combine(_tempDir, "thumbnails"));
        store.Initialize();

        var sourceFile = Path.Combine(_tempDir, "source.webp");
        await File.WriteAllBytesAsync(sourceFile, new byte[] { 0x01 });

        await store.PublishAsync(77, 1, sourceFile);
        Assert.True(store.HasThumbnail(77, 1));

        store.Delete(77, 1);
        Assert.False(store.HasThumbnail(77, 1));
    }

    [Fact]
    public async Task PublishAsync_OverwritesExistingThumbnailForSameKey()
    {
        var store = new ThumbnailStore(Path.Combine(_tempDir, "thumbnails"));
        store.Initialize();

        var source1 = Path.Combine(_tempDir, "s1.webp");
        await File.WriteAllBytesAsync(source1, new byte[] { 0x01 });
        var source2 = Path.Combine(_tempDir, "s2.webp");
        await File.WriteAllBytesAsync(source2, new byte[] { 0x02 });

        await store.PublishAsync(55, 1, source1);
        await store.PublishAsync(55, 1, source2); // overwrite

        using var stream = store.OpenRead(55, 1);
        Assert.NotNull(stream);
        using var ms = new MemoryStream();
        await stream!.CopyToAsync(ms);
        Assert.Equal(new byte[] { 0x02 }, ms.ToArray());
    }

    [Fact]
    public async Task Thumbnails_PersistAcrossReinstantiation_SimulatingRestart()
    {
        var root = Path.Combine(_tempDir, "thumbnails");

        var store1 = new ThumbnailStore(root);
        store1.Initialize();

        var sourceFile = Path.Combine(_tempDir, "source.webp");
        var content = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        await File.WriteAllBytesAsync(sourceFile, content);

        await store1.PublishAsync(888, 3, sourceFile);

        // Simulate a restart: create a new store instance pointing at the same root
        var store2 = new ThumbnailStore(root);
        store2.Initialize();

        Assert.True(store2.HasThumbnail(888, 3));
        using var stream = store2.OpenRead(888, 3);
        Assert.NotNull(stream);
        using var ms = new MemoryStream();
        await stream!.CopyToAsync(ms);
        Assert.Equal(content, ms.ToArray());
    }

    [Fact]
    public async Task CacheServiceEviction_DoesNotDeleteDurableThumbnails()
    {
        // The durable thumbnail store is completely independent of CacheService.
        // An eviction pass on the cache must not touch thumbnail files.
        var cacheRoot = Path.Combine(_tempDir, "cache");
        var thumbRoot = Path.Combine(_tempDir, "thumbnails");

        var cache = new CacheService(cacheRoot, budgetBytes: 100); // tiny budget to force eviction
        cache.Initialize();

        var store = new ThumbnailStore(thumbRoot);
        store.Initialize();

        // Publish a thumbnail
        var thumbSource = Path.Combine(_tempDir, "thumb.webp");
        await File.WriteAllBytesAsync(thumbSource, new byte[] { 0xFF });
        await store.PublishAsync(100, 1, thumbSource);

        // Fill the cache to trigger eviction
        var bigSource = Path.Combine(_tempDir, "big.webp");
        var bigBytes = new byte[200]; // exceeds the 100-byte budget
        bigBytes.AsSpan().Fill(0x42);
        await File.WriteAllBytesAsync(bigSource, bigBytes);
        await cache.PublishAsync("cache:key:1", bigSource, "image/webp");

        // The cache should have evicted; the thumbnail must still be present
        Assert.True(store.HasThumbnail(100, 1));
    }
}
