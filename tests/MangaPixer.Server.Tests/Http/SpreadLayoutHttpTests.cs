namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP integration tests for the shared per-archive double-page layout (1.23.0):
/// <c>PUT /api/v1/items/{itemId}/spread-layout</c> and the additive
/// <c>spreadStarts</c> field on the item manifest. Covers authentication, the global
/// antiforgery filter, validation, the ContentVersion precondition, 404 for a reader
/// without a grant (no existence leak) and for unknown ids, sharing between users, and
/// the reset once the file changes.
/// </summary>
[Collection("HttpSerial")]
public sealed class SpreadLayoutHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private readonly MangaPixerWebApplicationFactory _factory;

    public SpreadLayoutHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Seeds a fresh library holding one analyzed archive (10 synthetic pages at
    /// content version 1) and returns the archive's public id and the library row id.
    /// Every call makes a NEW archive so tests sharing the fixture stay independent.
    /// </summary>
    private async Task<(string ItemId, long LibraryId)> SeedArchiveAsync(int pageCount = 10)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();

        var lib = new LibraryEntity
        {
            PublicId = "spl" + Guid.NewGuid().ToString("N")[..10],
            DisplayName = "Spread Lib",
            RootPath = "/synthetic/spread",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();

        var pub = "spa" + Guid.NewGuid().ToString("N")[..10];
        var node = new CatalogNodeEntity
        {
            PublicId = pub,
            LibraryId = lib.Id,
            Kind = 1,
            DisplayName = "vol01.cbz",
            RelativePath = "vol01.cbz",
            PathKey = pub,
            SortKey = pub,
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();

        db.ArchiveItems.Add(new ArchiveItemEntity
        {
            NodeId = node.Id,
            ArchiveFormat = 0,
            ByteLength = 1024,
            ModificationTicks = 1,
            ContentVersion = 1,
            AnalysisState = 0,
            PageCount = pageCount,
            LastAnalyzedAt = DateTimeOffset.UtcNow,
        });
        for (var i = 0; i < pageCount; i++)
        {
            db.PageEntries.Add(new PageEntryEntity
            {
                ItemId = node.Id,
                ContentVersion = 1,
                Ordinal = i,
                EntryKey = OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue)),
                SourceEntryLocator = $"p{i:D3}.png",
                MediaType = "image/png",
                Width = 100,
                Height = 150,
                ByteSize = 100,
            });
        }
        await db.SaveChangesAsync();
        return (pub, lib.Id);
    }

    private async Task<HttpClient> CreateReaderAsync(HttpClient admin, string username, long? grantLibraryId)
    {
        var create = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = username,
            Password = "ReaderPass123!",
            IsAdmin = false,
        });
        create.EnsureSuccessStatusCode();

        var reader = await LoginAsync(username, "ReaderPass123!");
        (await reader.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "ReaderPass123!",
            NewPassword = "ReaderNewPass123!",
        })).EnsureSuccessStatusCode();
        reader = await LoginAsync(username, "ReaderNewPass123!");

        if (grantLibraryId is long libId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var user = await db.Users.SingleAsync(u => u.NormalizedUserName == username.ToUpperInvariant());
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = user.Id, LibraryId = libId, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        return reader;
    }

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = username,
            Password = password,
        })).EnsureSuccessStatusCode();
        var csrf = await client.GetFromJsonAsync<CsrfTokenDto>("/api/v1/auth/csrf");
        client.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);
        return client;
    }

    private static Task<HttpResponseMessage> PutLayoutAsync(HttpClient client, string itemId, long version, int[] starts) =>
        client.PutAsJsonAsync($"/api/v1/items/{itemId}/spread-layout", new SetSpreadLayoutRequest
        {
            ExpectedContentVersion = version,
            SpreadStarts = starts,
        });

    /// <summary>The manifest's spreadStarts: null when absent/null, else the indices.</summary>
    private static async Task<int[]?> GetManifestStartsAsync(HttpClient client, string itemId)
    {
        var response = await client.GetAsync($"/api/v1/items/{itemId}/manifest");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("spreadStarts", out var starts) || starts.ValueKind == JsonValueKind.Null)
            return null;
        return starts.EnumerateArray().Select(e => e.GetInt32()).ToArray();
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        return error?.Error;
    }

    [Fact]
    public async Task Put_Anonymous_Returns401()
    {
        var (itemId, _) = await SeedArchiveAsync();
        using var anon = _factory.CreateClient();

        var response = await PutLayoutAsync(anon, itemId, 1, [1]);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Put_WithoutCsrfHeader_IsRejectedByAntiforgery()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var (itemId, _) = await SeedArchiveAsync();

        // A separate client for the same admin session, minus the CSRF header.
        var noCsrf = await LoginAsync("admin", "TestPassword123!");
        noCsrf.DefaultRequestHeaders.Remove("X-MangaPixer-Csrf");

        var response = await PutLayoutAsync(noCsrf, itemId, 1, [1]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await GetManifestStartsAsync(admin, itemId));
    }

    [Fact]
    public async Task Put_ThenManifest_CarriesTheLayout_AndNoLayoutReadsAsNull()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var (itemId, _) = await SeedArchiveAsync();

        Assert.Null(await GetManifestStartsAsync(admin, itemId));

        var response = await PutLayoutAsync(admin, itemId, 1, [1, 6]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<SpreadLayoutDto>();
        Assert.Equal(itemId, dto!.ItemId);
        Assert.Equal(1, dto.ContentVersion);
        Assert.Equal([1, 6], dto.SpreadStarts);

        Assert.Equal(new[] { 1, 6 }, await GetManifestStartsAsync(admin, itemId));

        // An explicit empty set is kept as [] (not null): "no shifts" overrides the device fallback.
        Assert.Equal(HttpStatusCode.OK, (await PutLayoutAsync(admin, itemId, 1, [])).StatusCode);
        Assert.Equal(Array.Empty<int>(), await GetManifestStartsAsync(admin, itemId));
    }

    [Theory]
    [InlineData(new[] { 5, 2 })]      // unsorted
    [InlineData(new[] { 3, 3 })]      // duplicate
    [InlineData(new[] { 0 })]         // page 0 always starts
    [InlineData(new[] { 10 })]        // == pageCount
    public async Task Put_InvalidStarts_Returns400Invalid(int[] starts)
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var (itemId, _) = await SeedArchiveAsync();

        var response = await PutLayoutAsync(admin, itemId, 1, starts);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid", await ErrorCodeAsync(response));
        Assert.Null(await GetManifestStartsAsync(admin, itemId));
    }

    [Fact]
    public async Task Put_StaleContentVersion_Returns409StaleContent()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var (itemId, _) = await SeedArchiveAsync();

        var response = await PutLayoutAsync(admin, itemId, 99, [1]);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("stale_content", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Put_UnknownItem_Returns404()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await PutLayoutAsync(admin, "no-such-item", 1, [1]);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReaderWithoutGrant_Gets404_AndCannotChangeTheLayout()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var (itemId, _) = await SeedArchiveAsync();
        Assert.Equal(HttpStatusCode.OK, (await PutLayoutAsync(admin, itemId, 1, [2])).StatusCode);
        var outsider = await CreateReaderAsync(admin, "spread-outsider", grantLibraryId: null);

        var put = await PutLayoutAsync(outsider, itemId, 1, [3]);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await outsider.GetAsync($"/api/v1/items/{itemId}/manifest")).StatusCode);

        Assert.Equal(new[] { 2 }, await GetManifestStartsAsync(admin, itemId));
    }

    [Fact]
    public async Task Layout_IsShared_AGrantedReaderSeesAndChangesTheSameLayout()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var (itemId, libId) = await SeedArchiveAsync();
        var reader = await CreateReaderAsync(admin, "spread-reader", grantLibraryId: libId);

        Assert.Equal(HttpStatusCode.OK, (await PutLayoutAsync(admin, itemId, 1, [1])).StatusCode);
        Assert.Equal(new[] { 1 }, await GetManifestStartsAsync(reader, itemId));

        Assert.Equal(HttpStatusCode.OK, (await PutLayoutAsync(reader, itemId, 1, [4])).StatusCode);
        Assert.Equal(new[] { 4 }, await GetManifestStartsAsync(admin, itemId));
    }

    [Fact]
    public async Task ChangedFile_ResetsTheLayout_InTheManifest()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var (itemId, _) = await SeedArchiveAsync();
        Assert.Equal(HttpStatusCode.OK, (await PutLayoutAsync(admin, itemId, 1, [3])).StatusCode);

        // Simulate the scanner detecting a changed file (ContentVersion bump) and re-analysis.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var node = await db.CatalogNodes.SingleAsync(n => n.PublicId == itemId);
            var item = await db.ArchiveItems.SingleAsync(a => a.NodeId == node.Id);
            item.ContentVersion = 2;
            await db.SaveChangesAsync();
        }

        Assert.Null(await GetManifestStartsAsync(admin, itemId));
        // The old version is now rejected; the new one is accepted.
        Assert.Equal(HttpStatusCode.Conflict, (await PutLayoutAsync(admin, itemId, 1, [3])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutLayoutAsync(admin, itemId, 2, [5])).StatusCode);
        Assert.Equal(new[] { 5 }, await GetManifestStartsAsync(admin, itemId));
    }
}
