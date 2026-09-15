using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace com.lifepixer.mangaplex.Tests.Server.Http;

/// <summary>
/// HTTP tests for the Home "New chapters" endpoint after the 1.12.0 stacking rewrite:
/// GET /api/v1/home/recent-chapters. Verifies the reshaped (stacked) DTO through the public
/// surface — top-level stacking, standalone loose archives, NewCount, newest-first ordering,
/// tombstone exclusion, the empty state, authentication, and Incognito/Private exclusion.
/// </summary>
[Collection("HttpSerial")]
public sealed class RecentChaptersHttpTests : IClassFixture<MangaPlexWebApplicationFactory>
{
    private readonly MangaPlexWebApplicationFactory _factory;
    private HttpClient? _authenticatedClient;

    public RecentChaptersHttpTests(MangaPlexWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> GetAuthenticatedClientAsync()
    {
        if (_authenticatedClient is not null)
            return _authenticatedClient;
        _authenticatedClient = await _factory.LoginAsAdminWithChangedPasswordAsync();
        return _authenticatedClient;
    }

    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();

        if (await db.Libraries.AnyAsync(l => l.PublicId == "reclibA"))
            return;

        var now = DateTimeOffset.UtcNow;

        var libA = new LibraryEntity { PublicId = "reclibA", DisplayName = "Alpha Library", RootPath = "/tmp/recent-alpha", CreatedAt = now };
        var libB = new LibraryEntity { PublicId = "reclibB", DisplayName = "Beta Library", RootPath = "/tmp/recent-beta", CreatedAt = now };
        db.Libraries.AddRange(libA, libB);
        await db.SaveChangesAsync();

        var series = new CatalogNodeEntity
        {
            PublicId = "recseriesA",
            LibraryId = libA.Id,
            Kind = 0,
            DisplayName = "Series A",
            RelativePath = "Series A",
            PathKey = "Series A",
            SortKey = "0Series A",
            Availability = 0,
            CreatedAt = now,
        };
        db.CatalogNodes.Add(series);
        await db.SaveChangesAsync();

        db.CatalogNodes.AddRange(
            new CatalogNodeEntity
            {
                PublicId = "recA_new",
                LibraryId = libA.Id,
                Kind = 1,
                ParentId = series.Id,
                DisplayName = "Newest.cbz",
                RelativePath = "Series A/Newest.cbz",
                PathKey = "Series A/Newest.cbz",
                SortKey = "1Newest",
                Availability = 0,
                CreatedAt = now.AddHours(-1),
            },
            new CatalogNodeEntity
            {
                PublicId = "recA_old",
                LibraryId = libA.Id,
                Kind = 1,
                ParentId = series.Id,
                DisplayName = "Older.cbz",
                RelativePath = "Series A/Older.cbz",
                PathKey = "Series A/Older.cbz",
                SortKey = "1Older",
                Availability = 0,
                CreatedAt = now.AddHours(-3),
            },
            new CatalogNodeEntity
            {
                PublicId = "recA_loose",
                LibraryId = libA.Id,
                Kind = 1,
                DisplayName = "Loose.cbz",
                RelativePath = "Loose.cbz",
                PathKey = "Loose.cbz",
                SortKey = "1Loose",
                Availability = 0,
                CreatedAt = now.AddHours(-2),
            },
            new CatalogNodeEntity
            {
                PublicId = "recA_tomb",
                LibraryId = libA.Id,
                Kind = 1,
                ParentId = series.Id,
                DisplayName = "Tomb.cbz",
                RelativePath = "Series A/Tomb.cbz",
                PathKey = "Series A/Tomb.cbz",
                SortKey = "1Tomb",
                Availability = 5,
                CreatedAt = now.AddMinutes(-30),
            },
            new CatalogNodeEntity
            {
                PublicId = "recB1",
                LibraryId = libB.Id,
                Kind = 1,
                DisplayName = "BetaCh.cbz",
                RelativePath = "BetaCh.cbz",
                PathKey = "BetaCh.cbz",
                SortKey = "1BetaCh",
                Availability = 0,
                CreatedAt = now.AddHours(-1),
            });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetRecentChapters_ReturnsStackedNewestFirst()
    {
        await SeedAsync();
        var client = await GetAuthenticatedClientAsync();
        client.DefaultRequestHeaders.Remove("X-Incognito");

        var response = await client.GetAsync("/api/v1/home/recent-chapters");
        response.EnsureSuccessStatusCode();

        var dto = await response.Content.ReadFromJsonAsync<RecentChaptersDto>();
        Assert.NotNull(dto);
        // Other tests in the shared collection may add libraries; assert presence, not an exact set.
        Assert.Contains(dto!.Libraries, g => g.LibraryId == "reclibA");
        Assert.Contains(dto.Libraries, g => g.LibraryId == "reclibB");

        var alpha = dto.Libraries.First(g => g.LibraryId == "reclibA");
        Assert.Equal("Alpha Library", alpha.LibraryName);
        // Two stacks: Series A (folder, 2 chapters) then the loose archive; newest activity first.
        Assert.Equal(new[] { "recseriesA", "recA_loose" }, alpha.Stacks.Select(s => s.Id).ToArray());

        var seriesStack = alpha.Stacks[0];
        Assert.True(seriesStack.IsFolder);
        Assert.Equal("Series A", seriesStack.DisplayName);
        Assert.Equal(2, seriesStack.NewCount);                  // tombstoned chapter excluded
        Assert.Equal("recA_new", seriesStack.LatestItemId);
        Assert.Equal("Newest.cbz", seriesStack.LatestItemName);
        Assert.NotNull(seriesStack.CoverUrl);

        var looseStack = alpha.Stacks[1];
        Assert.False(looseStack.IsFolder);
        Assert.Equal("recA_loose", looseStack.Id);
        Assert.Equal("recA_loose", looseStack.LatestItemId);
        Assert.Equal(1, looseStack.NewCount);

        var beta = dto.Libraries.First(g => g.LibraryId == "reclibB");
        var betaStack = Assert.Single(beta.Stacks);
        Assert.False(betaStack.IsFolder);
        Assert.Equal("recB1", betaStack.Id);
    }

    [Fact]
    public async Task GetRecentChapters_RequiresAuthentication()
    {
        await SeedAsync();
        var anon = _factory.CreateClient();
        var response = await anon.GetAsync("/api/v1/home/recent-chapters");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetRecentChapters_EmptyState_WhenLibraryHasNoRecentArchives()
    {
        string emptyLibId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();
            var existing = await db.Libraries.FirstOrDefaultAsync(l => l.PublicId == "recemptylib");
            if (existing is null)
            {
                var lib = new LibraryEntity { PublicId = "recemptylib", DisplayName = "Empty Library", RootPath = "/tmp/recent-empty", CreatedAt = DateTimeOffset.UtcNow };
                db.Libraries.Add(lib);
                await db.SaveChangesAsync();
                emptyLibId = lib.PublicId;
            }
            else emptyLibId = existing.PublicId;
        }

        await SeedAsync();
        var client = await GetAuthenticatedClientAsync();
        client.DefaultRequestHeaders.Remove("X-Incognito");

        var response = await client.GetAsync("/api/v1/home/recent-chapters");
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<RecentChaptersDto>();
        Assert.NotNull(dto);
        var empty = dto!.Libraries.FirstOrDefault(g => g.LibraryId == emptyLibId);
        Assert.NotNull(empty);
        Assert.Empty(empty!.Stacks);
    }

    [Fact]
    public async Task GetRecentChapters_Incognito_ExcludesPrivateLibrary()
    {
        await SeedAsync();
        var client = await GetAuthenticatedClientAsync();
        client.DefaultRequestHeaders.Remove("X-Incognito");

        var baseline = await client.GetAsync("/api/v1/home/recent-chapters");
        baseline.EnsureSuccessStatusCode();
        var baselineDto = await baseline.Content.ReadFromJsonAsync<RecentChaptersDto>();
        Assert.Contains(baselineDto!.Libraries, g => g.LibraryId == "reclibB");

        await client.PutAsJsonAsync("/api/v1/reading/private-libraries",
            new SetPrivateLibrariesRequest { LibraryIds = ["reclibB"] });

        client.DefaultRequestHeaders.Remove("X-Incognito");
        client.DefaultRequestHeaders.Add("X-Incognito", "1");
        var incog = await client.GetAsync("/api/v1/home/recent-chapters");
        incog.EnsureSuccessStatusCode();
        var incogDto = await incog.Content.ReadFromJsonAsync<RecentChaptersDto>();
        Assert.Contains(incogDto!.Libraries, g => g.LibraryId == "reclibA");
        Assert.DoesNotContain(incogDto.Libraries, g => g.LibraryId == "reclibB");

        // Cleanup for other tests in the collection.
        client.DefaultRequestHeaders.Remove("X-Incognito");
        await client.PutAsJsonAsync("/api/v1/reading/private-libraries",
            new SetPrivateLibrariesRequest { LibraryIds = [] });
    }
}
