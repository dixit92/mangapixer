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
/// HTTP tests for the Home "New chapters" endpoint (1.11.0 Lane C):
/// GET /api/v1/home/recent-chapters. Verifies the endpoint is reachable and
/// authenticated, returns per-library grouping with the per-library cap and
/// newest-first ordering, the empty state, and that Incognito/Private
/// visibility excludes a Private library's items through the public surface.
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

        var libA = new LibraryEntity
        {
            PublicId = "reclibA",
            DisplayName = "Alpha Library",
            RootPath = "/tmp/recent-alpha",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var libB = new LibraryEntity
        {
            PublicId = "reclibB",
            DisplayName = "Beta Library",
            RootPath = "/tmp/recent-beta",
            CreatedAt = DateTimeOffset.UtcNow,
        };
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
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(series);
        await db.SaveChangesAsync();

        var baseTime = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        // Alpha: three archives inserted out of recency order; one tombstoned.
        db.CatalogNodes.Add(new CatalogNodeEntity
        {
            PublicId = "recA1", LibraryId = libA.Id, Kind = 1,
            DisplayName = "Old.cbz", RelativePath = "Old.cbz", PathKey = "Old.cbz",
            SortKey = "1Old", Availability = 0, CreatedAt = baseTime,
        });
        db.CatalogNodes.Add(new CatalogNodeEntity
        {
            PublicId = "recA3", LibraryId = libA.Id, Kind = 1, ParentId = series.Id,
            DisplayName = "Newest.cbz", RelativePath = "Series A/Newest.cbz",
            PathKey = "Series A/Newest.cbz", SortKey = "1Newest",
            Availability = 0, CreatedAt = baseTime.AddHours(2),
        });
        db.CatalogNodes.Add(new CatalogNodeEntity
        {
            PublicId = "recA2", LibraryId = libA.Id, Kind = 1,
            DisplayName = "Mid.cbz", RelativePath = "Mid.cbz", PathKey = "Mid.cbz",
            SortKey = "1Mid", Availability = 0, CreatedAt = baseTime.AddHours(1),
        });
        db.CatalogNodes.Add(new CatalogNodeEntity
        {
            PublicId = "recAtomb", LibraryId = libA.Id, Kind = 1,
            DisplayName = "Tomb.cbz", RelativePath = "Tomb.cbz", PathKey = "Tomb.cbz",
            SortKey = "1Tomb", Availability = 5, CreatedAt = baseTime.AddHours(3),
        });
        // Beta: one archive.
        db.CatalogNodes.Add(new CatalogNodeEntity
        {
            PublicId = "recB1", LibraryId = libB.Id, Kind = 1,
            DisplayName = "BetaCh.cbz", RelativePath = "BetaCh.cbz", PathKey = "BetaCh.cbz",
            SortKey = "1BetaCh", Availability = 0, CreatedAt = baseTime.AddHours(3),
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetRecentChapters_ReturnsGroupedCappedNewestFirst()
    {
        await SeedAsync();
        var client = await GetAuthenticatedClientAsync();
        client.DefaultRequestHeaders.Remove("X-Incognito");

        var response = await client.GetAsync("/api/v1/home/recent-chapters?perLibrary=2");
        response.EnsureSuccessStatusCode();

        var dto = await response.Content.ReadFromJsonAsync<RecentChaptersDto>();
        Assert.NotNull(dto);
        // Two library groups, ordered by display name (Alpha before Beta).
        Assert.Equal(new[] { "reclibA", "reclibB" }, dto!.Libraries.Select(g => g.LibraryId).ToArray());

        var alpha = dto.Libraries.First(g => g.LibraryId == "reclibA");
        Assert.Equal("Alpha Library", alpha.LibraryName);
        // Cap honored (3 live archives, cap 2); tombstoned excluded.
        Assert.Equal(2, alpha.Items.Count);
        // Newest first.
        Assert.Equal(new[] { "Newest.cbz", "Mid.cbz" }, alpha.Items.Select(i => i.DisplayName).ToArray());
        // Series label from the immediate parent folder.
        var newest = alpha.Items[0];
        Assert.Equal("recseriesA", newest.ParentId);
        Assert.Equal("Series A", newest.SeriesName);

        var beta = dto.Libraries.First(g => g.LibraryId == "reclibB");
        Assert.Single(beta.Items);
        Assert.Equal("BetaCh.cbz", beta.Items[0].DisplayName);
    }

    [Fact]
    public async Task GetRecentChapters_RequiresAuthentication()
    {
        await SeedAsync();
        // An unauthenticated client (no setup/login) gets 401.
        var anon = _factory.CreateClient();
        var response = await anon.GetAsync("/api/v1/home/recent-chapters");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetRecentChapters_EmptyState_WhenLibraryHasNoArchives()
    {
        // Seed a fresh library with no archives in its own scope.
        string emptyLibId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();
            var existing = await db.Libraries.FirstOrDefaultAsync(l => l.PublicId == "recemptylib");
            if (existing is null)
            {
                var lib = new LibraryEntity
                {
                    PublicId = "recemptylib",
                    DisplayName = "Empty Library",
                    RootPath = "/tmp/recent-empty",
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                db.Libraries.Add(lib);
                await db.SaveChangesAsync();
                emptyLibId = lib.PublicId;
            }
            else
            {
                emptyLibId = existing.PublicId;
            }
        }

        await SeedAsync();
        var client = await GetAuthenticatedClientAsync();
        client.DefaultRequestHeaders.Remove("X-Incognito");

        var response = await client.GetAsync("/api/v1/home/recent-chapters");
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<RecentChaptersDto>();
        Assert.NotNull(dto);
        // The empty library appears with an empty items list (frontend filters
        // empty groups, but the API surface keeps the per-library shape).
        var empty = dto!.Libraries.FirstOrDefault(g => g.LibraryId == emptyLibId);
        Assert.NotNull(empty);
        Assert.Empty(empty!.Items);
    }

    [Fact]
    public async Task GetRecentChapters_Incognito_ExcludesPrivateLibraryItems()
    {
        await SeedAsync();
        var client = await GetAuthenticatedClientAsync();
        client.DefaultRequestHeaders.Remove("X-Incognito");

        // Baseline (no private markings): both libraries present.
        var baseline = await client.GetAsync("/api/v1/home/recent-chapters");
        baseline.EnsureSuccessStatusCode();
        var baselineDto = await baseline.Content.ReadFromJsonAsync<RecentChaptersDto>();
        Assert.NotNull(baselineDto);
        Assert.Contains(baselineDto!.Libraries, g => g.LibraryId == "reclibA");
        Assert.Contains(baselineDto.Libraries, g => g.LibraryId == "reclibB");

        // Mark Beta as Private for the admin user.
        await client.PutAsJsonAsync("/api/v1/reading/private-libraries",
            new SetPrivateLibrariesRequest { LibraryIds = ["reclibB"] });

        // With X-Incognito: Beta (Private) is excluded entirely - no group, no items.
        client.DefaultRequestHeaders.Remove("X-Incognito");
        client.DefaultRequestHeaders.Add("X-Incognito", "1");
        var incog = await client.GetAsync("/api/v1/home/recent-chapters");
        incog.EnsureSuccessStatusCode();
        var incogDto = await incog.Content.ReadFromJsonAsync<RecentChaptersDto>();
        Assert.NotNull(incogDto);
        Assert.Contains(incogDto!.Libraries, g => g.LibraryId == "reclibA");
        Assert.DoesNotContain(incogDto.Libraries, g => g.LibraryId == "reclibB");

        // Cleanup: clear the private set so other tests start clean.
        client.DefaultRequestHeaders.Remove("X-Incognito");
        await client.PutAsJsonAsync("/api/v1/reading/private-libraries",
            new SetPrivateLibrariesRequest { LibraryIds = [] });
    }
}
