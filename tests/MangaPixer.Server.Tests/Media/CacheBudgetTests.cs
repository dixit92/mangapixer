namespace com.lifepixer.mangapixer.Tests.Server.Media;

using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.TestSupport;
using Xunit;

/// <summary>
/// Page cache budget (1.42.0, win 6): evicted entries leave the in-memory index (they used to stay there for the
/// whole process lifetime), and the budget changes live - a lower budget evicts at once.
/// </summary>
public sealed class CacheBudgetTests : IDisposable
{
    private readonly string _tempDir = TestSupport.CreateTempTestRoot("mangapixer-cachebudget");

    public void Dispose() => TestSupport.CleanupDirectory(_tempDir);

    private async Task PublishAsync(CacheService cache, int i, int bytes)
    {
        var source = Path.Combine(_tempDir, $"src-{i}.bin");
        await File.WriteAllBytesAsync(source, new byte[bytes]);
        await cache.PublishAsync(CacheService.BuildCacheKey(i, 1, "page", "webp"), source, "image/webp");
        File.Delete(source);
        await Task.Delay(5); // distinct last-access times, so the LRU order is defined
    }

    [Fact]
    public async Task EvictedEntries_LeaveTheIndex()
    {
        using var metrics = new MediaMetrics();
        var cache = new CacheService(Path.Combine(_tempDir, "cache"), budgetBytes: 2500, metrics: metrics);
        cache.Initialize();

        for (var i = 0; i < 6; i++)
            await PublishAsync(cache, i, 1000);

        var s = metrics.Snapshot().Cache;
        Assert.Equal(2, s.ActiveEntries);
        Assert.True(s.UsageBytes <= 2500);
        // Only the live entries are tracked: the four evicted ones are gone from the index.
        Assert.Equal(s.ActiveEntries, s.TrackedEntries);
    }

    [Fact]
    public async Task LowerBudget_EvictsAtOnce_AndAHigherOneLetsTheCacheGrow()
    {
        var cache = new CacheService(Path.Combine(_tempDir, "cache"), budgetBytes: 100_000);
        cache.Initialize();
        for (var i = 0; i < 5; i++)
            await PublishAsync(cache, i, 1000);
        Assert.Equal(5, cache.ActiveCount);

        cache.SetBudget(2000);

        Assert.Equal(2000, cache.BudgetBytes);
        Assert.Equal(2, cache.ActiveCount);
        Assert.True(cache.GetCurrentUsageBytes() <= 2000);
        // The most recently used entries survive.
        Assert.True(cache.TryGet(CacheService.BuildCacheKey(4, 1, "page", "webp"), out _));
        Assert.False(cache.TryGet(CacheService.BuildCacheKey(0, 1, "page", "webp"), out _));

        cache.SetBudget(10_000);
        for (var i = 5; i < 10; i++)
            await PublishAsync(cache, i, 1000);
        Assert.Equal(7, cache.ActiveCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.SetBudget(0));
    }
}
