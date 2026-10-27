namespace com.lifepixer.mangapixer.Tests.Server.Http;

using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP-host tests (1.49.0 lane M): the running application's shared <see cref="IMemoryCache"/> has the size limit
/// (<see cref="SharedMemoryCache.SizeLimit"/>) - a sizeless entry is refused with the exception every unsized call site would hit -,
/// sized entries are kept, and EF Core's own queries (its compiled-query cache sizes its entries) still run against the host.
/// The source sweep <c>SharedMemoryCacheContractTests</c> keeps every write sized; the existing HTTP suites drive each call site.
/// </summary>
public sealed class SharedMemoryCacheHttpTests
{
    [Fact]
    public async Task Host_SharedCache_IsSizeLimited_RefusesASizelessEntry_KeepsSizedOnes()
    {
        using var factory = new MangaPixerWebApplicationFactory();
        using var client = factory.CreateClient();
        Assert.True((await client.GetAsync("/health")).IsSuccessStatusCode);

        var cache = factory.Services.GetRequiredService<IMemoryCache>();
        var refused = Assert.Throws<InvalidOperationException>(() => cache.Set("lane-m:sizeless", 1));
        Assert.Contains("Size", refused.Message, StringComparison.Ordinal);

        cache.SetSized("lane-m:sized", 42, TimeSpan.FromMinutes(1), size: 1);
        Assert.Equal(42, cache.Get<int>("lane-m:sized"));

        // EF Core queries keep working with the limit on (if EF shares the application's cache, its entries carry their own size).
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        Assert.Equal(0, await db.Libraries.CountAsync(l => l.Id < 0));
        Assert.Equal(0, await db.Libraries.CountAsync(l => l.Id < -1));
    }

    [Fact]
    public void SetSized_OverTheBudget_RefusesQuietly_NeverThrows()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 4 });
        cache.SetSized("a", 1, TimeSpan.FromMinutes(1), size: 3);
        cache.SetSized("b", 2, TimeSpan.FromMinutes(1), size: 3); // over the budget: not kept, no exception (the cache then compacts in the background)
        Assert.False(cache.TryGetValue("b", out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.SetSized("c", 3, TimeSpan.FromMinutes(1), size: 0));
    }
}
