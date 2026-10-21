namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Home;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 1.46.0 lane HC: <c>GET /api/v1/home/recent-chapters</c> serves each library's user-independent stack source from the host's
/// <see cref="HomeStackCache"/> (the DI singleton): the second answer reuses it, and a catalog change (the revision a scan raises) is
/// in the next answer.
/// </summary>
public sealed class HomeStackCacheHttpTests
{
    [Fact]
    public async Task TheRow_ReusesTheKeptSource_AndShowsANewArchiveAfterTheRevisionMoves()
    {
        using var factory = new MangaPixerWebApplicationFactory();
        var client = await factory.LoginAsAdminWithChangedPasswordAsync();
        var cache = factory.Services.GetRequiredService<HomeStackCache>();
        var tag = Guid.NewGuid().ToString("N")[..6];
        var libraryId = await DbAsync(factory, async db =>
        {
            var library = new LibraryEntity { PublicId = "hsc-" + tag, DisplayName = "Kept " + tag, RootPath = "/synthetic/kept", CreatedAt = DateTimeOffset.UtcNow };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            return library.Id;
        });
        var serial = 0;
        Task AddAsync(string name) => DbAsync(factory, async db =>
        {
            serial++;
            db.CatalogNodes.Add(new CatalogNodeEntity
            {
                PublicId = $"hsc-{tag}-{serial}",
                LibraryId = libraryId,
                Kind = (int)CatalogNodeKind.Archive,
                DisplayName = name,
                RelativePath = $"a{serial}.cbz",
                PathKey = $"a{serial}.cbz",
                SortKey = $"1a{serial}",
                Availability = (int)CatalogNodeAvailability.Available,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-serial),
            });
            return await db.SaveChangesAsync();
        });
        async Task<string[]> RowAsync()
        {
            var dto = await client.GetFromJsonAsync<RecentChaptersDto>("/api/v1/home/recent-chapters");
            return dto!.Libraries.Single(l => l.LibraryId == "hsc-" + tag).Stacks.Select(s => s.DisplayName).ToArray();
        }

        await AddAsync("First.cbz");
        Assert.Equal(new[] { "First.cbz" }, await RowAsync());
        var hits = cache.Hits;
        Assert.Equal(new[] { "First.cbz" }, await RowAsync());
        Assert.True(cache.Hits > hits, "the second answer reused the kept source");

        // A scan adds an archive and raises the revision when it ends.
        await AddAsync("Second.cbz");
        await DbAsync(factory, db =>
            db.Libraries.Where(l => l.Id == libraryId).ExecuteUpdateAsync(u => u.SetProperty(l => l.CatalogRevision, l => l.CatalogRevision + 1)));
        Assert.Equal(new[] { "First.cbz", "Second.cbz" }, await RowAsync());
    }

    private static async Task<T> DbAsync<T>(MangaPixerWebApplicationFactory factory, Func<MangaPixerDbContext, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>());
    }
}
