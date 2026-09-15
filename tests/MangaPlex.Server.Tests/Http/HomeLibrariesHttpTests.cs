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
/// HTTP tests for the Home library-visibility preference (1.12.0):
/// GET/PUT /api/v1/reading/home-libraries, plus its effect on
/// GET /api/v1/home/recent-chapters. Verifies replacement semantics, validation
/// (unknown/inaccessible ids skipped), exclusion from the home surface, and auth.
/// </summary>
[Collection("HttpSerial")]
public sealed class HomeLibrariesHttpTests : IClassFixture<MangaPlexWebApplicationFactory>
{
    private readonly MangaPlexWebApplicationFactory _factory;
    private HttpClient? _client;

    public HomeLibrariesHttpTests(MangaPlexWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> ClientAsync()
        => _client ??= await _factory.LoginAsAdminWithChangedPasswordAsync();

    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();
        if (await db.Libraries.AnyAsync(l => l.PublicId == "homelibA"))
            return;

        var now = DateTimeOffset.UtcNow;
        var libA = new LibraryEntity { PublicId = "homelibA", DisplayName = "Home Alpha", RootPath = "/tmp/home-alpha", CreatedAt = now };
        var libB = new LibraryEntity { PublicId = "homelibB", DisplayName = "Home Beta", RootPath = "/tmp/home-beta", CreatedAt = now };
        db.Libraries.AddRange(libA, libB);
        await db.SaveChangesAsync();

        db.CatalogNodes.AddRange(
            new CatalogNodeEntity { PublicId = "homeA1", LibraryId = libA.Id, Kind = 1, DisplayName = "A.cbz", RelativePath = "A.cbz", PathKey = "A.cbz", SortKey = "1A", Availability = 0, CreatedAt = now.AddHours(-1) },
            new CatalogNodeEntity { PublicId = "homeB1", LibraryId = libB.Id, Kind = 1, DisplayName = "B.cbz", RelativePath = "B.cbz", PathKey = "B.cbz", SortKey = "1B", Availability = 0, CreatedAt = now.AddHours(-1) });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task PutGet_ReplacementSemantics_And_ValidationSkipsUnknown()
    {
        await SeedAsync();
        var client = await ClientAsync();

        // Set the excluded set, then read it back.
        var put = await client.PutAsJsonAsync("/api/v1/reading/home-libraries",
            new HomeLibraryVisibilityDto { ExcludedLibraryIds = ["homelibB"] });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var get = await client.GetAsync("/api/v1/reading/home-libraries");
        get.EnsureSuccessStatusCode();
        var dto = await get.Content.ReadFromJsonAsync<HomeLibraryVisibilityDto>();
        Assert.NotNull(dto);
        Assert.Contains("homelibB", dto!.ExcludedLibraryIds);
        Assert.DoesNotContain("homelibA", dto.ExcludedLibraryIds);

        // Replacement + validation: an unknown id is silently skipped; homelibA replaces the set.
        await client.PutAsJsonAsync("/api/v1/reading/home-libraries",
            new HomeLibraryVisibilityDto { ExcludedLibraryIds = ["homelibA", "does-not-exist"] });
        var get2 = await client.GetAsync("/api/v1/reading/home-libraries");
        var dto2 = await get2.Content.ReadFromJsonAsync<HomeLibraryVisibilityDto>();
        Assert.Equal(["homelibA"], dto2!.ExcludedLibraryIds);

        // Cleanup.
        await client.PutAsJsonAsync("/api/v1/reading/home-libraries",
            new HomeLibraryVisibilityDto { ExcludedLibraryIds = [] });
    }

    [Fact]
    public async Task ExcludedLibrary_DisappearsFromRecentChapters()
    {
        await SeedAsync();
        var client = await ClientAsync();
        client.DefaultRequestHeaders.Remove("X-Incognito");

        // Baseline: both home libraries present.
        await client.PutAsJsonAsync("/api/v1/reading/home-libraries",
            new HomeLibraryVisibilityDto { ExcludedLibraryIds = [] });
        var baseline = await (await client.GetAsync("/api/v1/home/recent-chapters")).Content.ReadFromJsonAsync<RecentChaptersDto>();
        Assert.Contains(baseline!.Libraries, g => g.LibraryId == "homelibA");
        Assert.Contains(baseline.Libraries, g => g.LibraryId == "homelibB");

        // Hide Home Beta from home.
        await client.PutAsJsonAsync("/api/v1/reading/home-libraries",
            new HomeLibraryVisibilityDto { ExcludedLibraryIds = ["homelibB"] });
        var hidden = await (await client.GetAsync("/api/v1/home/recent-chapters")).Content.ReadFromJsonAsync<RecentChaptersDto>();
        Assert.Contains(hidden!.Libraries, g => g.LibraryId == "homelibA");
        Assert.DoesNotContain(hidden.Libraries, g => g.LibraryId == "homelibB");

        // Cleanup.
        await client.PutAsJsonAsync("/api/v1/reading/home-libraries",
            new HomeLibraryVisibilityDto { ExcludedLibraryIds = [] });
    }

    [Fact]
    public async Task HomeLibraries_RequiresAuthentication()
    {
        var anon = _factory.CreateClient();
        var response = await anon.GetAsync("/api/v1/reading/home-libraries");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
