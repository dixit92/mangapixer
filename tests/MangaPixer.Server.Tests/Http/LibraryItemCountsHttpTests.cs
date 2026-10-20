namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 1.46.0 (lane B, measured cost #8): <c>GET /api/v1/libraries</c> remembers each library's archive count per catalog state
/// (<see cref="LibraryItemCounts"/>): the same number as a direct count, counted again once the library's catalog revision, its latest scan run
/// or its identity changes, and counted on every call while a scan of it runs (a scan writes nodes before it bumps the revision).
/// </summary>
public sealed class LibraryItemCountsHttpTests
{
    private sealed record Lib(long Id, string Name);

    [Fact]
    public async Task Counts_AreRemembered_PerCatalogState_AndAlwaysEqualADirectCount()
    {
        using var factory = new MangaPixerWebApplicationFactory();
        var client = await factory.LoginAsAdminWithChangedPasswordAsync();
        var cache = factory.Services.GetRequiredService<LibraryItemCounts>();
        var name = "Counted " + Guid.NewGuid().ToString("N")[..6];
        var lib = await DbAsync(factory, async db =>
        {
            var library = new LibraryEntity { PublicId = "cnt-" + name[^6..], DisplayName = name, RootPath = "/synthetic/counted", CreatedAt = DateTimeOffset.UtcNow };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            return new Lib(library.Id, name);
        });
        var empty = await DbAsync(factory, async db =>
        {
            var library = new LibraryEntity { PublicId = "cnt-e-" + name[^6..], DisplayName = name + " empty", RootPath = "/synthetic/empty", CreatedAt = DateTimeOffset.UtcNow };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            return new Lib(library.Id, library.DisplayName);
        });
        var serial = 0;
        Task AddAsync(int archives, int tombstoned = 0) => DbAsync(factory, async db =>
        {
            for (var i = 0; i < archives + tombstoned; i++)
            {
                serial++;
                db.CatalogNodes.Add(new CatalogNodeEntity
                {
                    PublicId = $"cnt-{name[^6..]}-{serial}",
                    LibraryId = lib.Id,
                    Kind = (int)CatalogNodeKind.Archive,
                    DisplayName = $"Archive {serial}",
                    RelativePath = $"a{serial}",
                    PathKey = $"a{serial}",
                    SortKey = $"1a{serial}",
                    Availability = i < archives ? (int)CatalogNodeAvailability.Available : (int)CatalogNodeAvailability.Tombstoned,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
            return await db.SaveChangesAsync();
        });
        Task BumpAsync() => DbAsync(factory, db =>
            db.Libraries.Where(l => l.Id == lib.Id).ExecuteUpdateAsync(u => u.SetProperty(l => l.CatalogRevision, l => l.CatalogRevision + 1)));
        Task<long> RunAsync(int status) => DbAsync(factory, async db =>
        {
            var run = new ScanRunEntity { LibraryId = lib.Id, ScanRevision = 1, Status = status, StartedAt = DateTimeOffset.UtcNow };
            db.ScanRuns.Add(run);
            await db.SaveChangesAsync();
            return run.Id;
        });
        Task SetRunAsync(long id, int status) => DbAsync(factory, db =>
            db.ScanRuns.Where(r => r.Id == id).ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, status)));

        async Task<(int Count, bool Scanning, int Hits)> ListAsync()
        {
            var hitsBefore = cache.Hits;
            var libraries = await client.GetFromJsonAsync<List<LibraryDto>>("/api/v1/libraries");
            var dto = libraries!.Single(l => l.Name == lib.Name);
            Assert.Equal(0, libraries!.Single(l => l.Name == empty.Name).ItemCount);
            // Every answer equals the direct count of that moment, unless the test changed nodes without a catalog change on purpose.
            return (dto.ItemCount ?? -1, dto.IsScanning, cache.Hits - hitsBefore);
        }
        Task<int> DirectAsync() => DbAsync(factory, db => db.CatalogNodes.CountAsync(n =>
            n.LibraryId == lib.Id && n.Kind == (int)CatalogNodeKind.Archive && n.Availability != (int)CatalogNodeAvailability.Tombstoned));

        await AddAsync(3, tombstoned: 1);
        var first = await ListAsync();
        Assert.Equal((3, false, 0), first); // counted (both libraries)
        var second = await ListAsync();
        Assert.Equal(3, second.Count);
        Assert.True(second.Hits >= 1); // remembered

        // A scan ends: the revision moves -> counted again.
        await AddAsync(2);
        await BumpAsync();
        Assert.Equal(await DirectAsync(), (await ListAsync()).Count);
        Assert.Equal(5, (await ListAsync()).Count);

        // While a scan runs every call counts (its nodes arrive before the revision moves) ...
        var run = await RunAsync(status: 1);
        await AddAsync(1);
        var during = await ListAsync();
        Assert.Equal((6, true), (during.Count, during.Scanning));
        await AddAsync(1);
        during = await ListAsync();
        Assert.Equal((7, true), (during.Count, during.Scanning));
        // ... and a scan that fails without bumping the revision leaves no stale count behind.
        await SetRunAsync(run, 3);
        var failed = await ListAsync();
        Assert.Equal((await DirectAsync(), false), (failed.Count, failed.Scanning));
        Assert.Equal(7, (await ListAsync()).Count);

        // A further scan run (even one that never bumped the revision) is a new catalog state.
        await AddAsync(1);
        await RunAsync(status: 4);
        var afterRun = await ListAsync();
        Assert.Equal(await DirectAsync(), afterRun.Count);
        Assert.Equal(8, afterRun.Count);
    }

    private static async Task<T> DbAsync<T>(MangaPixerWebApplicationFactory factory, Func<MangaPixerDbContext, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>());
    }
}
