namespace com.lifepixer.mangaplex.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests for search: results return once FTS5 triggers populate
/// catalog_search, literal query safety, and pagination.
/// </summary>
[Collection("HttpSerial")]
public sealed class SearchHttpTests : IClassFixture<MangaPlexWebApplicationFactory>
{
    private readonly MangaPlexWebApplicationFactory _factory;
    private HttpClient? _authenticatedClient;

    public SearchHttpTests(MangaPlexWebApplicationFactory factory)
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

    private async Task<long> SeedLibraryWithNodesAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();

        // Idempotent: check if the library already exists (shared factory)
        var existing = await db.Libraries.FirstOrDefaultAsync(l => l.PublicId == "searchlib1");
        if (existing is not null)
            return existing.Id;

        var library = new LibraryEntity
        {
            PublicId = "searchlib1",
            DisplayName = "Search Test Library",
            RootPath = "/tmp/search-test",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        // 4 archives with "Alpha" in the name
        db.CatalogNodes.Add(new CatalogNodeEntity
        {
            PublicId = "alpha1",
            LibraryId = library.Id,
            Kind = 1,
            DisplayName = "Alpha 001.cbz",
            RelativePath = "Alpha 001.cbz",
            PathKey = "Alpha 001.cbz",
            SortKey = "1Alpha 001.cbz",
            Availability = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.CatalogNodes.Add(new CatalogNodeEntity
        {
            PublicId = "alpha2",
            LibraryId = library.Id,
            Kind = 1,
            DisplayName = "Alpha 002.cbz",
            RelativePath = "Alpha 002.cbz",
            PathKey = "Alpha 002.cbz",
            SortKey = "1Alpha 002.cbz",
            Availability = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.CatalogNodes.Add(new CatalogNodeEntity
        {
            PublicId = "alpha3",
            LibraryId = library.Id,
            Kind = 1,
            DisplayName = "Alpha Special.cbz",
            RelativePath = "sub/Alpha Special.cbz",
            PathKey = "sub/Alpha Special.cbz",
            SortKey = "1Alpha Special.cbz",
            Availability = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.CatalogNodes.Add(new CatalogNodeEntity
        {
            PublicId = "alpha4",
            LibraryId = library.Id,
            Kind = 1,
            DisplayName = "Beta Alpha.cbz",
            RelativePath = "Beta Alpha.cbz",
            PathKey = "Beta Alpha.cbz",
            SortKey = "1Beta Alpha.cbz",
            Availability = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        // 2 folders with "Vol" in the name
        db.CatalogNodes.Add(new CatalogNodeEntity
        {
            PublicId = "vol1",
            LibraryId = library.Id,
            Kind = 0,
            DisplayName = "Vol 1",
            RelativePath = "Vol 1",
            PathKey = "Vol 1",
            SortKey = "0Vol 1",
            Availability = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.CatalogNodes.Add(new CatalogNodeEntity
        {
            PublicId = "vol2",
            LibraryId = library.Id,
            Kind = 0,
            DisplayName = "Vol 2",
            RelativePath = "Vol 2",
            PathKey = "Vol 2",
            SortKey = "0Vol 2",
            Availability = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        // A tombstoned archive that should NOT appear in search
        db.CatalogNodes.Add(new CatalogNodeEntity
        {
            PublicId = "alphatomb",
            LibraryId = library.Id,
            Kind = 1,
            DisplayName = "Alpha Tombstoned.cbz",
            RelativePath = "Alpha Tombstoned.cbz",
            PathKey = "Alpha Tombstoned.cbz",
            SortKey = "1Alpha Tombstoned.cbz",
            Availability = 5, // tombstoned
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        return library.Id;
    }

    [Fact]
    public async Task Search_ForAlpha_ReturnsFourArchives()
    {
        await SeedLibraryWithNodesAsync();
        var client = await GetAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/v1/search?q=alpha");
        response.EnsureSuccessStatusCode();

        var results = await response.Content.ReadFromJsonAsync<SearchResultsDto>(TestJson.Web);
        Assert.NotNull(results);
        // 4 archives with "Alpha" + 1 tombstoned (excluded) = 4
        // "Beta Alpha" also contains "Alpha" so it's included
        Assert.Equal(4, results!.TotalCount);
    }

    [Fact]
    public async Task Search_ForVol_ReturnsTwoFolders()
    {
        await SeedLibraryWithNodesAsync();
        var client = await GetAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/v1/search?q=Vol");
        response.EnsureSuccessStatusCode();

        var results = await response.Content.ReadFromJsonAsync<SearchResultsDto>(TestJson.Web);
        Assert.NotNull(results);
        Assert.Equal(2, results!.TotalCount);
        Assert.All(results.Items, item => Assert.Equal(CatalogNodeKind.Folder, item.Kind));
    }

    [Fact]
    public async Task Search_WithQuoteAndAsterisk_Returns200()
    {
        await SeedLibraryWithNodesAsync();
        var client = await GetAuthenticatedClientAsync();

        // FTS5 special characters should be treated as literal by BuildFtsQuery
        var response = await client.GetAsync("/api/v1/search?q=%22alpha%22%2A");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Search_ExcludesTombstonedNodes()
    {
        await SeedLibraryWithNodesAsync();
        var client = await GetAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/v1/search?q=Tombstoned");
        response.EnsureSuccessStatusCode();

        var results = await response.Content.ReadFromJsonAsync<SearchResultsDto>(TestJson.Web);
        Assert.NotNull(results);
        // The tombstoned "Alpha Tombstoned.cbz" should NOT appear
        Assert.Equal(0, results!.TotalCount);
    }

    [Fact]
    public async Task Search_WithLibraryId_FiltersByLibrary()
    {
        await SeedLibraryWithNodesAsync();
        var client = await GetAuthenticatedClientAsync();

        // Search within the seeded library
        var response = await client.GetAsync("/api/v1/search?q=alpha&libraryId=searchlib1");
        response.EnsureSuccessStatusCode();

        var results = await response.Content.ReadFromJsonAsync<SearchResultsDto>(TestJson.Web);
        Assert.NotNull(results);
        Assert.Equal(4, results!.TotalCount);

        // Search within a non-existent library
        var response2 = await client.GetAsync("/api/v1/search?q=alpha&libraryId=nonexistent");
        Assert.Equal(HttpStatusCode.NotFound, response2.StatusCode);
    }
}

/// <summary>
/// DB tests: FTS5 triggers fire on insert, update, and delete.
/// </summary>
public sealed class SearchTriggerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly DbContextOptions<MangaPlexDbContext> _options;

    public SearchTriggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangaplex-c02-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        var connectionString = DatabaseInitialization.BuildConnectionString(_dbPath);
        _options = new DbContextOptionsBuilder<MangaPlexDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<MangaPlexDbContext> SetupAsync()
    {
        var db = new MangaPlexDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var library = new LibraryEntity
        {
            PublicId = "lib1",
            DisplayName = "Test",
            RootPath = "/tmp/test",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        return db;
    }

    private async Task<int> CountFtsRowsAsync(MangaPlexDbContext db, long nodeId)
    {
        using var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM catalog_search WHERE node_id = @nodeId";
        command.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@nodeId", nodeId));
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<int> CountFtsMatchesAsync(MangaPlexDbContext db, string ftsQuery)
    {
        using var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM catalog_search WHERE catalog_search MATCH @query";
        command.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@query", ftsQuery));
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Trigger_Insert_AddsToFtsIndex()
    {
        using var db = await SetupAsync();
        var library = await db.Libraries.FirstAsync();

        db.CatalogNodes.Add(new CatalogNodeEntity
        {
            PublicId = "n1",
            LibraryId = library.Id,
            Kind = 1,
            DisplayName = "Test Archive.cbz",
            RelativePath = "Test Archive.cbz",
            PathKey = "Test Archive.cbz",
            SortKey = "1Test Archive.cbz",
            Availability = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var node = await db.CatalogNodes.FirstAsync(n => n.PublicId == "n1");
        var count = await CountFtsRowsAsync(db, node.Id);
        Assert.Equal(1, count);

        var matches = await CountFtsMatchesAsync(db, "\"Test Archive\"");
        Assert.Equal(1, matches);
    }

    [Fact]
    public async Task Trigger_Update_RenamesInFtsIndex()
    {
        using var db = await SetupAsync();
        var library = await db.Libraries.FirstAsync();

        db.CatalogNodes.Add(new CatalogNodeEntity
        {
            PublicId = "n1",
            LibraryId = library.Id,
            Kind = 1,
            DisplayName = "OldName.cbz",
            RelativePath = "OldName.cbz",
            PathKey = "OldName.cbz",
            SortKey = "1OldName.cbz",
            Availability = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        // Verify old name is indexed
        var oldMatches = await CountFtsMatchesAsync(db, "\"OldName\"");
        Assert.Equal(1, oldMatches);

        // Rename to something with no shared trigrams
        var node = await db.CatalogNodes.FirstAsync(n => n.PublicId == "n1");
        node.DisplayName = "Renamed.cbz";
        node.RelativePath = "Renamed.cbz";
        await db.SaveChangesAsync();

        // Old name should no longer match
        var oldMatchesAfter = await CountFtsMatchesAsync(db, "\"OldName\"");
        Assert.Equal(0, oldMatchesAfter);

        // New name should match
        var newMatches = await CountFtsMatchesAsync(db, "\"Renamed\"");
        Assert.Equal(1, newMatches);
    }

    [Fact]
    public async Task Trigger_Delete_RemovesFromFtsIndex()
    {
        using var db = await SetupAsync();
        var library = await db.Libraries.FirstAsync();

        db.CatalogNodes.Add(new CatalogNodeEntity
        {
            PublicId = "n1",
            LibraryId = library.Id,
            Kind = 1,
            DisplayName = "Delete Me.cbz",
            RelativePath = "Delete Me.cbz",
            PathKey = "Delete Me.cbz",
            SortKey = "1Delete Me.cbz",
            Availability = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var node = await db.CatalogNodes.FirstAsync(n => n.PublicId == "n1");
        var countBefore = await CountFtsRowsAsync(db, node.Id);
        Assert.Equal(1, countBefore);

        db.CatalogNodes.Remove(node);
        await db.SaveChangesAsync();

        var countAfter = await CountFtsRowsAsync(db, node.Id);
        Assert.Equal(0, countAfter);
    }
}
