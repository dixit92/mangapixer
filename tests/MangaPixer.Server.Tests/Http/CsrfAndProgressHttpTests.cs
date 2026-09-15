namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests for CSRF enforcement and reading-progress contract repair.
/// </summary>
[Collection("HttpSerial")]
public sealed class CsrfAndProgressHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private readonly MangaPixerWebApplicationFactory _factory;
    private HttpClient? _authenticatedClient;

    public CsrfAndProgressHttpTests(MangaPixerWebApplicationFactory factory)
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

    private async Task<string> GetFreshCsrfTokenAsync()
    {
        var client = await GetAuthenticatedClientAsync();
        var csrfResponse = await client.GetAsync("/api/v1/auth/csrf");
        csrfResponse.EnsureSuccessStatusCode();
        var csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfTokenDto>();
        return csrf!.Token ?? string.Empty;
    }

    private async Task<(long nodeId, string publicId)> SeedItemAsync()
    {
        return await SeedUniqueItemAsync("c03item1");
    }

    private async Task<(long nodeId, string publicId)> SeedUniqueItemAsync(string publicId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();

        var existing = await db.CatalogNodes.FirstOrDefaultAsync(n => n.PublicId == publicId);
        if (existing is not null)
            return (existing.Id, existing.PublicId);

        var library = await db.Libraries.FirstOrDefaultAsync(l => l.PublicId == "c03lib1")
            ?? new LibraryEntity
            {
                PublicId = "c03lib1",
                DisplayName = "C03 Test Library",
                RootPath = "/tmp/c03-test",
                CreatedAt = DateTimeOffset.UtcNow,
            };
        if (library.Id == 0)
        {
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
        }

        var node = new CatalogNodeEntity
        {
            PublicId = publicId,
            LibraryId = library.Id,
            Kind = 1,
            DisplayName = $"{publicId}.cbz",
            RelativePath = $"{publicId}.cbz",
            PathKey = $"{publicId}.cbz",
            SortKey = $"1{publicId}.cbz",
            Availability = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();

        var archiveItem = new ArchiveItemEntity
        {
            NodeId = node.Id,
            ArchiveFormat = 0,
            ByteLength = 100,
            ModificationTicks = 0,
            ContentVersion = 1,
            AnalysisState = 0,
            PageCount = 10,
        };
        db.ArchiveItems.Add(archiveItem);
        await db.SaveChangesAsync();

        return (node.Id, node.PublicId);
    }

    // D2: CSRF cookie is HttpOnly — JS cannot read it.
    [Fact]
    public async Task CsrfCookie_IsHttpOnly()
    {
        // Verify the antiforgery options are configured with HttpOnly = true.
        // TestServer may not include the HttpOnly flag in Set-Cookie headers,
        // so we verify the configuration directly (audit defect D2).
        using var scope = _factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Antiforgery.AntiforgeryOptions>>();
        Assert.True(options.Value.Cookie.HttpOnly);
    }

    // D2: Non-GET request without CSRF header → 400
    [Fact]
    public async Task PostWithoutCsrfHeader_Returns400()
    {
        var client = await GetAuthenticatedClientAsync();
        // Remove the CSRF header that LoginAsAdminWithChangedPasswordAsync added
        client.DefaultRequestHeaders.Remove("X-MangaPixer-Csrf");

        var response = await client.PutAsJsonAsync("/api/v1/reading/preferences", new UserPreferencesDto());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // D2: Non-GET request with correct CSRF header → success
    [Fact]
    public async Task PostWithCsrfHeader_Succeeds()
    {
        var client = await GetAuthenticatedClientAsync();
        // The authenticated client already has the CSRF header
        var response = await client.PutAsJsonAsync("/api/v1/reading/preferences", new UserPreferencesDto());
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // D14/D32: GET progress for item with no progress returns 200 (not 404)
    [Fact]
    public async Task GetProgress_ForUnreadItem_Returns200Unread()
    {
        // Use a unique item to avoid interference from other tests
        // that share the same factory/database
        var (nodeId, publicId) = await SeedUniqueItemAsync("c03-unread-test");
        var client = await GetAuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/v1/reading/progress/{publicId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var progress = await response.Content.ReadFromJsonAsync<ReadingProgressDto>(TestJson.Web);
        Assert.NotNull(progress);
        Assert.Equal(ReadingState.Unread, progress!.State);
        Assert.Equal(0, progress.PageIndex);
        Assert.Equal(0, progress.Revision);
    }

    // D32: PUT progress with If-None-Match: * (first write) succeeds
    [Fact]
    public async Task PutProgress_FirstWriteWithIfNoneMatchStar_Succeeds()
    {
        var (nodeId, publicId) = await SeedUniqueItemAsync("c03-first-write-item");
        var client = await GetAuthenticatedClientAsync();
        var csrf = await GetFreshCsrfTokenAsync();

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/reading/progress/{publicId}")
        {
            Content = JsonContent.Create(new UpdateProgressRequest
            {
                PageIndex = 3,
                ExpectedContentVersion = 1,
                MutationId = "c03-first-write",
            }),
        };
        request.Headers.Add("X-MangaPixer-Csrf", csrf);
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // D32: PUT progress with wrong If-Match revision → 412
    [Fact]
    public async Task PutProgress_WrongIfMatchRevision_Returns412()
    {
        var (nodeId, publicId) = await SeedUniqueItemAsync("c03-412-test-item");
        var client = await GetAuthenticatedClientAsync();
        var csrf = await GetFreshCsrfTokenAsync();

        // First write
        var req1 = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/reading/progress/{publicId}")
        {
            Content = JsonContent.Create(new UpdateProgressRequest
            {
                PageIndex = 3,
                ExpectedContentVersion = 1,
                MutationId = "c03-412-test-1",
            }),
        };
        req1.Headers.Add("X-MangaPixer-Csrf", csrf);
        req1.Headers.TryAddWithoutValidation("If-None-Match", "*");
        var resp1 = await client.SendAsync(req1);
        resp1.EnsureSuccessStatusCode();

        // Second write with wrong revision
        var req2 = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/reading/progress/{publicId}")
        {
            Content = JsonContent.Create(new UpdateProgressRequest
            {
                PageIndex = 5,
                ExpectedContentVersion = 1,
                MutationId = "c03-412-test-2",
            }),
        };
        req2.Headers.Add("X-MangaPixer-Csrf", csrf);
        req2.Headers.TryAddWithoutValidation("If-Match", "\"999\""); // wrong revision

        var response = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    // D32: Duplicate MutationId → 200 same revision (idempotent)
    [Fact]
    public async Task PutProgress_DuplicateMutationId_ReturnsSameRevision()
    {
        var (nodeId, publicId) = await SeedUniqueItemAsync("c03-dup-test");
        var client = await GetAuthenticatedClientAsync();
        var csrf = await GetFreshCsrfTokenAsync();

        var mutationId = "c03-duplicate-test";

        // First write
        var req1 = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/reading/progress/{publicId}")
        {
            Content = JsonContent.Create(new UpdateProgressRequest
            {
                PageIndex = 3,
                ExpectedContentVersion = 1,
                MutationId = mutationId,
            }),
        };
        req1.Headers.Add("X-MangaPixer-Csrf", csrf);
        req1.Headers.TryAddWithoutValidation("If-None-Match", "*");
        var resp1 = await client.SendAsync(req1);
        resp1.EnsureSuccessStatusCode();
        var result1 = await resp1.Content.ReadFromJsonAsync<ProgressUpdateResponse>();
        Assert.NotNull(result1);
        var revision1 = result1!.Revision;

        // Second write with same MutationId
        var req2 = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/reading/progress/{publicId}")
        {
            Content = JsonContent.Create(new UpdateProgressRequest
            {
                PageIndex = 5,
                ExpectedContentVersion = 1,
                MutationId = mutationId,
            }),
        };
        req2.Headers.Add("X-MangaPixer-Csrf", csrf);
        var resp2 = await client.SendAsync(req2);
        resp2.EnsureSuccessStatusCode();
        var result2 = await resp2.Content.ReadFromJsonAsync<ProgressUpdateResponse>();
        Assert.NotNull(result2);

        Assert.Equal(revision1, result2!.Revision);
        Assert.True(result2.AlreadyApplied);
    }

    private sealed record ProgressUpdateResponse(long Revision, bool AlreadyApplied);
}
