using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace com.lifepixer.mangapixer.Tests.Server.Http;

/// <summary>
/// HTTP tests for the jump-index endpoint.
/// Verifies the endpoint is reachable, authenticated, returns the right shape,
/// and that the bucket cursor actually lands on the bucket's first node via the
/// browse endpoint.
/// </summary>
[Collection("HttpSerial")]
public sealed class JumpIndexHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private readonly MangaPixerWebApplicationFactory _factory;
    private HttpClient? _authenticatedClient;

    public JumpIndexHttpTests(MangaPixerWebApplicationFactory factory)
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
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();

        var existing = await db.Libraries.FirstOrDefaultAsync(l => l.PublicId == "jumplib1");
        if (existing is not null)
            return;

        var library = new LibraryEntity
        {
            PublicId = "jumplib1",
            DisplayName = "Jump Test Library",
            RootPath = "/tmp/jump-test",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        db.CatalogNodes.Add(new CatalogNodeEntity
        {
            PublicId = "jumpA1",
            LibraryId = library.Id,
            Kind = 1,
            DisplayName = "Alpha",
            RelativePath = "Alpha.cbz",
            PathKey = "Alpha.cbz",
            SortKey = "0\u001f1Alpha",
            Availability = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.CatalogNodes.Add(new CatalogNodeEntity
        {
            PublicId = "jumpB1",
            LibraryId = library.Id,
            Kind = 1,
            DisplayName = "Beta",
            RelativePath = "Beta.cbz",
            PathKey = "Beta.cbz",
            SortKey = "0\u001f1Beta",
            Availability = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.CatalogNodes.Add(new CatalogNodeEntity
        {
            PublicId = "jumpKana1",
            LibraryId = library.Id,
            Kind = 1,
            DisplayName = "あいうえお",
            RelativePath = "あいうえお.cbz",
            PathKey = "あいうえお.cbz",
            SortKey = "0\u001f1あいうえお",
            Availability = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetJumpIndex_ReturnsBucketsInRailOrder()
    {
        await SeedAsync();
        var client = await GetAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/v1/libraries/jumplib1/jump-index");
        response.EnsureSuccessStatusCode();

        var index = await response.Content.ReadFromJsonAsync<JumpIndexDto>();
        Assert.NotNull(index);
        Assert.Equal("jumplib1", index!.LibraryId);

        var labels = index.Buckets.Select(b => b.Label).ToList();
        // Rail order: A, B, then Kana.
        Assert.Equal(new[] { "A", "B", "Kana" }, labels);
    }

    [Fact]
    public async Task GetJumpIndex_FirstBucketHasNullCursor()
    {
        await SeedAsync();
        var client = await GetAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/v1/libraries/jumplib1/jump-index");
        response.EnsureSuccessStatusCode();
        var index = await response.Content.ReadFromJsonAsync<JumpIndexDto>();

        Assert.NotNull(index);
        Assert.Null(index!.Buckets[0].FirstCursor);
    }

    [Fact]
    public async Task GetJumpIndex_BucketCursorLandsOnFirstNodeViaBrowse()
    {
        await SeedAsync();
        var client = await GetAuthenticatedClientAsync();

        var indexResponse = await client.GetAsync("/api/v1/libraries/jumplib1/jump-index");
        indexResponse.EnsureSuccessStatusCode();
        var index = await indexResponse.Content.ReadFromJsonAsync<JumpIndexDto>();
        Assert.NotNull(index);

        var bBucket = index!.Buckets.First(b => b.Label == "B");
        Assert.NotNull(bBucket.FirstCursor);

        // Pass the cursor to the browse endpoint with sort=name.
        var browseResponse = await client.GetAsync(
            $"/api/v1/libraries/jumplib1/browse?sort=name&cursor={Uri.EscapeDataString(bBucket.FirstCursor!)}");
        browseResponse.EnsureSuccessStatusCode();
        var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        jsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var page = await browseResponse.Content.ReadFromJsonAsync<PageResponse<CatalogNodeDto>>(jsonOptions);
        Assert.NotNull(page);
        Assert.NotEmpty(page!.Items);
        Assert.Equal("Beta", page.Items[0].DisplayName);
    }

    [Fact]
    public async Task GetJumpIndex_UnknownLibraryId_Returns404()
    {
        var client = await GetAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/libraries/nonexistent/jump-index");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetJumpIndex_Unauthenticated_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/libraries/jumplib1/jump-index");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
