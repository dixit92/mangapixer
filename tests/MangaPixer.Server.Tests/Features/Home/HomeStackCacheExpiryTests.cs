using com.lifepixer.mangapixer.Server.Features.Home;
using Xunit;

namespace com.lifepixer.mangapixer.Tests.Server.Features.Home;

/// <summary>
/// Unit tests (1.49.0 lane M): <see cref="HomeStackCache"/> drops a kept source once it is past <see cref="HomeStackCache.MaxAge"/> -
/// it could never serve again - instead of holding it until the same library and window are built again. Kept sources inside the
/// bound are untouched and still served.
/// </summary>
public sealed class HomeStackCacheExpiryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly HomeStackStamp Stamp = new(Revision: 3, LibraryCreatedAt: T0.AddDays(-30), LastScanRun: 7, ScanActive: false, []);

    private static HomeStackSource Source() => new([], new Dictionary<long, HomeStackNode>());

    [Fact]
    public void Store_DropsOtherSourcesPastTheAgeBound_KeepsTheYoungOnes()
    {
        var cache = new HomeStackCache();
        cache.Store(libraryId: 1, windowDays: 14, Stamp, cutoffKey: 0, T0, Source());
        cache.Store(libraryId: 2, windowDays: 14, Stamp, cutoffKey: 0, T0.AddMinutes(5), Source());
        Assert.Equal(2, cache.Count);

        // Library 1 is now past the bound, library 2 is not: storing library 3 drops only library 1.
        var now = T0 + HomeStackCache.MaxAge;
        cache.Store(libraryId: 3, windowDays: 7, Stamp, cutoffKey: 0, now, Source());
        Assert.Equal(2, cache.Count);
        Assert.Null(cache.TryGet(1, 14, Stamp, cutoffKey: 0, now));
        Assert.NotNull(cache.TryGet(2, 14, Stamp, cutoffKey: 0, now));
        Assert.NotNull(cache.TryGet(3, 7, Stamp, cutoffKey: 0, now));
    }

    [Fact]
    public void TryGet_PastTheAgeBound_RefusesAndDropsTheSource()
    {
        var cache = new HomeStackCache();
        cache.Store(libraryId: 1, windowDays: 14, Stamp, cutoffKey: 0, T0, Source());

        Assert.NotNull(cache.TryGet(1, 14, Stamp, cutoffKey: 0, T0 + HomeStackCache.MaxAge - TimeSpan.FromSeconds(1)));
        Assert.Equal(1, cache.Count);

        Assert.Null(cache.TryGet(1, 14, Stamp, cutoffKey: 0, T0 + HomeStackCache.MaxAge));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void TryGet_InsideTheBound_ButRefusedForItsInputs_KeepsTheSource()
    {
        var cache = new HomeStackCache();
        cache.Store(libraryId: 1, windowDays: 14, Stamp, cutoffKey: 0, T0, Source());

        // A changed revision is refused (the next build replaces the source), but a young source is not dropped by a refusal.
        Assert.Null(cache.TryGet(1, 14, Stamp with { Revision = 4 }, cutoffKey: 0, T0.AddMinutes(1)));
        Assert.Equal(1, cache.Count);
    }
}
