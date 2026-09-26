namespace com.lifepixer.mangapixer.Tests.Server.Media;

using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.TestSupport;
using Xunit;

/// <summary>
/// Integration tests for CacheService: atomic publication, LRU eviction,
/// active-file pinning, and cache miss recovery.
/// </summary>
public sealed class CacheServiceTests : IDisposable
{
    private readonly string _tempDir;

    public CacheServiceTests()
    {
        _tempDir = TestSupport.CreateTempTestRoot("mangapixer-cache");
    }

    public void Dispose()
    {
        TestSupport.CleanupDirectory(_tempDir);
    }

    [Fact]
    public void Initialize_CreatesCacheRoot()
    {
        var cacheRoot = Path.Combine(_tempDir, "cache");
        var cache = new CacheService(cacheRoot, 1024 * 1024);

        cache.Initialize();

        Assert.True(Directory.Exists(cacheRoot));
    }

    [Fact]
    public async Task PublishAsync_CreatesCacheFile()
    {
        var cacheRoot = Path.Combine(_tempDir, "cache");
        var cache = new CacheService(cacheRoot, 10 * 1024 * 1024);
        cache.Initialize();

        // Create a source file
        var sourceFile = Path.Combine(_tempDir, "source.png");
        File.WriteAllText(sourceFile, "fake image data");

        var cacheKey = CacheService.BuildCacheKey(1, 1, "page001.png", "original");
        await cache.PublishAsync(cacheKey, sourceFile, "image/png");

        Assert.True(cache.TryGet(cacheKey, out _));
        Assert.Equal(1, cache.ActiveCount);
    }

    [Fact]
    public async Task OpenRead_ReturnsStreamForCachedFile()
    {
        var cacheRoot = Path.Combine(_tempDir, "cache");
        var cache = new CacheService(cacheRoot, 10 * 1024 * 1024);
        cache.Initialize();

        var sourceFile = Path.Combine(_tempDir, "source.png");
        var content = "fake image data for streaming test";
        File.WriteAllText(sourceFile, content);

        var cacheKey = CacheService.BuildCacheKey(1, 1, "page001.png", "original");
        await cache.PublishAsync(cacheKey, sourceFile, "image/png");

        using var stream = cache.OpenRead(cacheKey);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var text = reader.ReadToEnd();
        Assert.Equal(content, text);
    }

    [Fact]
    public async Task OpenRead_ReturnsNullForMissingKey()
    {
        var cacheRoot = Path.Combine(_tempDir, "cache");
        var cache = new CacheService(cacheRoot, 10 * 1024 * 1024);
        cache.Initialize();

        var stream = cache.OpenRead("nonexistent-key");
        Assert.Null(stream);
    }

    [Fact]
    public async Task TryGet_ReturnsFalseForMissingKey()
    {
        var cacheRoot = Path.Combine(_tempDir, "cache");
        var cache = new CacheService(cacheRoot, 10 * 1024 * 1024);
        cache.Initialize();

        Assert.False(cache.TryGet("missing", out var entry));
        Assert.Null(entry);
    }

    [Fact]
    public async Task PublishAsync_AtomicRename_DoesNotLeaveTempFiles()
    {
        var cacheRoot = Path.Combine(_tempDir, "cache");
        var cache = new CacheService(cacheRoot, 10 * 1024 * 1024);
        cache.Initialize();

        var sourceFile = Path.Combine(_tempDir, "source.png");
        File.WriteAllText(sourceFile, "data");

        var cacheKey = "test:1:page:original";
        await cache.PublishAsync(cacheKey, sourceFile, "image/png");

        // No .tmp files should remain
        var tempFiles = Directory.GetFiles(cacheRoot, "*.tmp", SearchOption.AllDirectories);
        Assert.Empty(tempFiles);
    }

    [Fact]
    public async Task PublishStreamAsync_PublishesFromStream()
    {
        var cacheRoot = Path.Combine(_tempDir, "cache");
        var cache = new CacheService(cacheRoot, 10 * 1024 * 1024);
        cache.Initialize();

        var data = System.Text.Encoding.UTF8.GetBytes("streamed image data");
        using var sourceStream = new MemoryStream(data);

        var cacheKey = CacheService.BuildCacheKey(2, 1, "page002.png", "original");
        await cache.PublishStreamAsync(cacheKey, sourceStream, "image/png");

        Assert.True(cache.TryGet(cacheKey, out _));
    }

    [Fact]
    public async Task LRU_EvictsLeastRecentlyAccessedWhenOverBudget()
    {
        var cacheRoot = Path.Combine(_tempDir, "cache");
        // Small budget: 3 files of 1KB each, budget is 2KB → should evict oldest
        var cache = new CacheService(cacheRoot, 2048);
        cache.Initialize();

        // Publish 3 files
        for (int i = 0; i < 3; i++)
        {
            var sourceFile = Path.Combine(_tempDir, $"source{i}.png");
            File.WriteAllText(sourceFile, new string('x', 1024));
            var cacheKey = CacheService.BuildCacheKey(i, 1, $"page{i}.png", "original");
            await cache.PublishAsync(cacheKey, sourceFile, "image/png");
        }

        // Budget is 2048, we have 3072 → at least one should be evicted
        var usage = cache.GetCurrentUsageBytes();
        Assert.True(usage <= 2048);
    }

    [Fact]
    public async Task LRU_DoesNotEvictPinnedFiles()
    {
        var cacheRoot = Path.Combine(_tempDir, "cache");
        var cache = new CacheService(cacheRoot, 2048);
        cache.Initialize();

        // Publish first file and pin it (by opening a stream)
        var sourceFile1 = Path.Combine(_tempDir, "source1.png");
        File.WriteAllText(sourceFile1, new string('x', 1024));
        var key1 = CacheService.BuildCacheKey(1, 1, "page1.png", "original");
        await cache.PublishAsync(key1, sourceFile1, "image/png");

        // Keep stream open (pins the file)
        var pinnedStream = cache.OpenRead(key1);
        Assert.NotNull(pinnedStream);

        try
        {
            // Publish two more files to trigger eviction
            for (int i = 2; i <= 3; i++)
            {
                var sourceFile = Path.Combine(_tempDir, $"source{i}.png");
                File.WriteAllText(sourceFile, new string('x', 1024));
                var cacheKey = CacheService.BuildCacheKey(i, 1, $"page{i}.png", "original");
                await cache.PublishAsync(cacheKey, sourceFile, "image/png");
            }

            // The pinned file should still be accessible
            Assert.True(cache.TryGet(key1, out _));
        }
        finally
        {
            pinnedStream?.Dispose();
        }
    }

    [Fact]
    public async Task BuildCacheKey_IsDeterministic()
    {
        var key1 = CacheService.BuildCacheKey(1, 1, "page001.png", "original");
        var key2 = CacheService.BuildCacheKey(1, 1, "page001.png", "original");
        Assert.Equal(key1, key2);

        var key3 = CacheService.BuildCacheKey(1, 2, "page001.png", "original");
        Assert.NotEqual(key1, key3);
    }

    [Fact]
    public async Task GetCurrentUsageBytes_ReportsActiveEntries()
    {
        var cacheRoot = Path.Combine(_tempDir, "cache");
        var cache = new CacheService(cacheRoot, 10 * 1024 * 1024);
        cache.Initialize();

        var sourceFile = Path.Combine(_tempDir, "source.png");
        File.WriteAllText(sourceFile, new string('x', 500));
        var cacheKey = CacheService.BuildCacheKey(1, 1, "page.png", "original");
        await cache.PublishAsync(cacheKey, sourceFile, "image/png");

        var usage = cache.GetCurrentUsageBytes();
        Assert.True(usage >= 500);
    }

    [Fact]
    public async Task PublishAsync_WritesUnderThisRunsFolder()
    {
        var cacheRoot = Path.Combine(_tempDir, "cache");
        var cache = new CacheService(cacheRoot, 10 * 1024 * 1024);
        cache.Initialize();

        var sourceFile = Path.Combine(_tempDir, "source.png");
        File.WriteAllText(sourceFile, "fake image data");
        var cacheKey = CacheService.BuildCacheKey(1, 1, "page001.png", "original");
        await cache.PublishAsync(cacheKey, sourceFile, "image/png");

        var path = cache.GetCacheFilePath(cacheKey);
        Assert.True(File.Exists(path));
        Assert.StartsWith(cache.RunDirectory + Path.DirectorySeparatorChar, path);
        Assert.StartsWith(cache.RunDirectory + Path.DirectorySeparatorChar, cache.ScratchDirectory);
        Assert.Equal("run-", Path.GetFileName(cache.RunDirectory)[..4]);
    }

    [Fact]
    public async Task DeleteStaleRuns_RemovesEarlierRunsAndLegacyLayout_KeepsCurrentRunAndForeignFiles()
    {
        var cacheRoot = Path.Combine(_tempDir, "cache");
        var legacyName = new string('a', 64);

        // An earlier run of the new layout, with a nested page and a temp file.
        var earlier = new CacheService(cacheRoot, 10 * 1024 * 1024);
        earlier.Initialize();
        var sourceFile = Path.Combine(_tempDir, "source.png");
        File.WriteAllText(sourceFile, new string('x', 100));
        var earlierKey = CacheService.BuildCacheKey(1, 1, "old.png", "original");
        await earlier.PublishAsync(earlierKey, sourceFile, "image/png");
        File.WriteAllText(Path.Combine(earlier.ScratchDirectory, "page-abc.bin"), "tmp");

        // The pre-1.24.1 flat layout: two-hex folders, 64-hex files, _tmp.
        Directory.CreateDirectory(Path.Combine(cacheRoot, "aa"));
        File.WriteAllText(Path.Combine(cacheRoot, "aa", legacyName), new string('y', 50));
        File.WriteAllText(Path.Combine(cacheRoot, "aa", legacyName + ".tmp"), "z");
        Directory.CreateDirectory(Path.Combine(cacheRoot, "_tmp"));
        File.WriteAllText(Path.Combine(cacheRoot, "_tmp", "thumb-1.bin"), "t");

        // Things that are not cache-shaped must survive a misconfigured root.
        Directory.CreateDirectory(Path.Combine(cacheRoot, "bb"));
        File.WriteAllText(Path.Combine(cacheRoot, "bb", "notes.txt"), "keep");
        Directory.CreateDirectory(Path.Combine(cacheRoot, "photos"));
        File.WriteAllText(Path.Combine(cacheRoot, "photos", legacyName), "keep");
        File.WriteAllText(Path.Combine(cacheRoot, "readme.txt"), "keep");

        var current = new CacheService(cacheRoot, 10 * 1024 * 1024);
        current.Initialize();
        var currentKey = CacheService.BuildCacheKey(2, 1, "new.png", "original");
        await current.PublishAsync(currentKey, sourceFile, "image/png");

        var (files, bytes) = current.DeleteStaleRuns();

        Assert.Equal(5, files);
        Assert.Equal(100 + 3 + 50 + 1 + 1, bytes);
        Assert.False(Directory.Exists(earlier.RunDirectory));
        Assert.False(Directory.Exists(Path.Combine(cacheRoot, "aa")));
        Assert.False(Directory.Exists(Path.Combine(cacheRoot, "_tmp")));
        Assert.True(File.Exists(Path.Combine(cacheRoot, "bb", "notes.txt")));
        Assert.True(File.Exists(Path.Combine(cacheRoot, "photos", legacyName)));
        Assert.True(File.Exists(Path.Combine(cacheRoot, "readme.txt")));
        Assert.True(current.TryGet(currentKey, out _));
        Assert.True(File.Exists(current.GetCacheFilePath(currentKey)));

        // A second pass has nothing left to do.
        Assert.Equal((0, 0L), current.DeleteStaleRuns());
    }

    [Fact]
    public void DeleteStaleRuns_MissingRoot_IsANoOp()
    {
        var cache = new CacheService(Path.Combine(_tempDir, "never-created"), 1024);
        Assert.Equal((0, 0L), cache.DeleteStaleRuns());
    }
}
