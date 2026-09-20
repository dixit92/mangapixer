namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP integration tests for the per-user Favorites feature (1.21.0): the three
/// endpoints authed vs anonymous, toggle round-trip + idempotency, GET favorites
/// keyset paging, the IsFavorite flag in browse and search responses, X-Incognito
/// visibility, and persistence of the two new opt-in preferences through the existing
/// library-preferences endpoint.
/// </summary>
[Collection("HttpSerial")]
public sealed class FavoritesHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private readonly MangaPixerWebApplicationFactory _factory;
    private HttpClient? _authenticatedClient;

    private const string LibPubId = "favlib1";
    private const string PrivLibPubId = "favprivlib1";

    public FavoritesHttpTests(MangaPixerWebApplicationFactory factory)
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

    /// <summary>
    /// Seeds a public library (folder + two archives) and a separate library holding one
    /// archive that a test can mark Private. Idempotent across the shared fixture.
    /// Node public ids: favFolder, favArcA, favArcB (public lib); favPriv (private lib).
    /// </summary>
    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();

        if (await db.Libraries.AnyAsync(l => l.PublicId == LibPubId))
            return;

        var lib = new LibraryEntity { PublicId = LibPubId, DisplayName = "Fav Lib", RootPath = "/tmp/fav", CreatedAt = DateTimeOffset.UtcNow };
        var privLib = new LibraryEntity { PublicId = PrivLibPubId, DisplayName = "Fav Private", RootPath = "/tmp/fav-priv", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.AddRange(lib, privLib);
        await db.SaveChangesAsync();

        db.CatalogNodes.AddRange(
            NewNode("favFolder", lib.Id, 0, "FavZeta Folder", "0FavZeta"),
            NewNode("favArcA", lib.Id, 1, "FavAlpha 001.cbz", "1FavAlpha 001"),
            NewNode("favArcB", lib.Id, 1, "FavAlpha 002.cbz", "1FavAlpha 002"),
            NewNode("favPriv", privLib.Id, 1, "FavAlpha Private.cbz", "1FavAlpha Private"));
        await db.SaveChangesAsync();
    }

    private static CatalogNodeEntity NewNode(string pub, long libId, int kind, string name, string sortKey) => new()
    {
        PublicId = pub,
        LibraryId = libId,
        Kind = kind,
        DisplayName = name,
        RelativePath = name,
        PathKey = name,
        SortKey = sortKey,
        Availability = 0,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Removes every favorite owned by the admin so count-sensitive tests start
    /// clean. No-op before the admin exists (call after authenticating).</summary>
    private async Task ClearFavoritesAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var admin = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == "ADMIN");
        if (admin is null)
            return;
        var rows = await db.Favorites.Where(f => f.UserId == admin.Id).ToListAsync();
        db.Favorites.RemoveRange(rows);
        await db.SaveChangesAsync();
    }

    // --- Auth ---

    [Fact]
    public async Task Endpoints_RequireAuthentication()
    {
        await SeedAsync();
        using var anon = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/favorites")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsync("/api/v1/nodes/favArcA/favorite", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.DeleteAsync("/api/v1/nodes/favArcA/favorite")).StatusCode);
    }

    // --- Toggle round-trip + idempotency ---

    [Fact]
    public async Task Toggle_RoundTrip_AddsThenRemoves()
    {
        await SeedAsync();
        var client = await GetAuthenticatedClientAsync();
        await ClearFavoritesAsync();

        // Add (idempotent: twice).
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/nodes/favArcA/favorite", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/nodes/favArcA/favorite", null)).StatusCode);

        var afterAdd = await GetFavoritesAsync(client);
        Assert.Single(afterAdd.Items);
        Assert.Equal("favArcA", afterAdd.Items[0].Id);
        Assert.True(afterAdd.Items[0].IsFavorite);

        // Remove (idempotent: twice).
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/nodes/favArcA/favorite")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/nodes/favArcA/favorite")).StatusCode);

        var afterRemove = await GetFavoritesAsync(client);
        Assert.Empty(afterRemove.Items);
    }

    [Fact]
    public async Task AddFavorite_UnknownNode_Returns404()
    {
        await SeedAsync();
        var client = await GetAuthenticatedClientAsync();
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync("/api/v1/nodes/no-such-node/favorite", null)).StatusCode);
    }

    // --- GET favorites paging + ordering ---

    [Fact]
    public async Task GetFavorites_PagesNewestFirst()
    {
        await SeedAsync();
        var client = await GetAuthenticatedClientAsync();
        await ClearFavoritesAsync();

        // Favorite folder, then A, then B — newest first should be B, A, folder.
        await client.PostAsync("/api/v1/nodes/favFolder/favorite", null);
        await Task.Delay(10);
        await client.PostAsync("/api/v1/nodes/favArcA/favorite", null);
        await Task.Delay(10);
        await client.PostAsync("/api/v1/nodes/favArcB/favorite", null);

        // Page 1 (size 2).
        var page1 = await GetFavoritesAsync(client, pageSize: 2);
        Assert.Equal(3, page1.TotalCount);
        Assert.True(page1.HasMore);
        Assert.Equal(new[] { "favArcB", "favArcA" }, page1.Items.Select(i => i.Id).ToArray());

        // Page 2 via cursor.
        var page2 = await GetFavoritesAsync(client, pageSize: 2, cursor: page1.NextCursor);
        Assert.Equal(new[] { "favFolder" }, page2.Items.Select(i => i.Id).ToArray());
        Assert.False(page2.HasMore);
    }

    // --- IsFavorite in browse + search ---

    [Fact]
    public async Task Browse_ReflectsIsFavorite()
    {
        await SeedAsync();
        var client = await GetAuthenticatedClientAsync();
        await ClearFavoritesAsync();
        await client.PostAsync("/api/v1/nodes/favArcA/favorite", null);

        var response = await client.GetAsync($"/api/v1/libraries/{LibPubId}/browse");
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<PageResponse<CatalogNodeDto>>(TestJson.Web);
        Assert.NotNull(page);

        Assert.True(page!.Items.Single(i => i.Id == "favArcA").IsFavorite);
        Assert.False(page.Items.Single(i => i.Id == "favArcB").IsFavorite);
    }

    [Fact]
    public async Task Search_ReflectsIsFavorite()
    {
        await SeedAsync();
        var client = await GetAuthenticatedClientAsync();
        await ClearFavoritesAsync();
        await client.PostAsync("/api/v1/nodes/favArcA/favorite", null);

        var response = await client.GetAsync("/api/v1/search?q=FavAlpha&libraryId=" + LibPubId);
        response.EnsureSuccessStatusCode();
        var results = await response.Content.ReadFromJsonAsync<SearchResultsDto>(TestJson.Web);
        Assert.NotNull(results);

        Assert.True(results!.Items.Single(i => i.Id == "favArcA").IsFavorite);
        Assert.False(results.Items.Single(i => i.Id == "favArcB").IsFavorite);
    }

    // --- X-Incognito visibility ---

    [Fact]
    public async Task GetFavorites_Incognito_HidesPrivateLibraryFavorite()
    {
        await SeedAsync();
        var client = await GetAuthenticatedClientAsync();
        await ClearFavoritesAsync();

        await client.PostAsync("/api/v1/nodes/favArcA/favorite", null);
        await client.PostAsync("/api/v1/nodes/favPriv/favorite", null);

        // Mark the private library Private for the admin.
        await client.PutAsJsonAsync("/api/v1/reading/private-libraries",
            new SetPrivateLibrariesRequest { LibraryIds = [PrivLibPubId] });

        try
        {
            // Normal session: both favorites visible.
            var normal = await GetFavoritesAsync(client);
            Assert.Equal(2, normal.TotalCount);
            Assert.Contains(normal.Items, i => i.Id == "favPriv");

            // Incognito session: the private-library favorite is hidden.
            client.DefaultRequestHeaders.Add("X-Incognito", "1");
            var incognito = await GetFavoritesAsync(client);
            Assert.Equal(1, incognito.TotalCount);
            Assert.DoesNotContain(incognito.Items, i => i.Id == "favPriv");
            Assert.Contains(incognito.Items, i => i.Id == "favArcA");
        }
        finally
        {
            client.DefaultRequestHeaders.Remove("X-Incognito");
            await client.PutAsJsonAsync("/api/v1/reading/private-libraries",
                new SetPrivateLibrariesRequest { LibraryIds = [] });
        }
    }

    // --- Preferences ---

    [Fact]
    public async Task LibraryPreferences_PersistFavoritesToggles()
    {
        var client = await GetAuthenticatedClientAsync();

        // Read current, flip both favorites toggles on, echo the whole blob back.
        var current = await (await client.GetAsync("/api/v1/reading/library-preferences"))
            .Content.ReadFromJsonAsync<LibraryViewPreferencesDto>(TestJson.Web);
        Assert.NotNull(current);

        var updated = current! with { ShowFavoritesHomeRow = true, FavoritesSearchProminence = true };
        var put = await client.PutAsJsonAsync("/api/v1/reading/library-preferences", updated);
        put.EnsureSuccessStatusCode();

        var reloaded = await (await client.GetAsync("/api/v1/reading/library-preferences"))
            .Content.ReadFromJsonAsync<LibraryViewPreferencesDto>(TestJson.Web);
        Assert.NotNull(reloaded);
        Assert.True(reloaded!.ShowFavoritesHomeRow);
        Assert.True(reloaded.FavoritesSearchProminence);

        // Reset so other tests see defaults.
        await client.PutAsJsonAsync("/api/v1/reading/library-preferences",
            reloaded with { ShowFavoritesHomeRow = false, FavoritesSearchProminence = false });
    }

    private static async Task<PageResponse<CatalogNodeDto>> GetFavoritesAsync(
        HttpClient client, int? pageSize = null, string? cursor = null)
    {
        var url = "/api/v1/favorites";
        var qs = new List<string>();
        if (pageSize is not null) qs.Add($"pageSize={pageSize}");
        if (cursor is not null) qs.Add($"cursor={Uri.EscapeDataString(cursor)}");
        if (qs.Count > 0) url += "?" + string.Join("&", qs);

        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<PageResponse<CatalogNodeDto>>(TestJson.Web);
        Assert.NotNull(page);
        return page!;
    }
}
