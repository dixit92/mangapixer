namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP integration tests for the 1.4.0 Incognito/Private library feature.
/// Verifies the prefs endpoints, the X-Incognito header behavior on
/// continue-reading, library list, and search, and non-owner isolation.
/// </summary>
[Collection("HttpSerial")]
public sealed class IncognitoHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private readonly MangaPixerWebApplicationFactory _factory;
    private static readonly SemaphoreSlim _seedLock = new(1, 1);
    private static bool _seeded;
    private static string? _libAPubId;
    private static string? _libBPubId;

    public IncognitoHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> GetAdminClientAsync()
    {
        return await _factory.LoginAsAdminWithChangedPasswordAsync();
    }

    /// <summary>
    /// Clears the admin's private-library set and removes any X-Incognito
    /// header from the cached client. Must be called at the start of every
    /// test to ensure a clean baseline (xUnit does not guarantee test order
    /// and the admin client is cached per factory).
    /// </summary>
    private async Task ClearPrivateLibrariesAsync(HttpClient client)
    {
        client.DefaultRequestHeaders.Remove("X-Incognito");
        await client.PutAsJsonAsync("/api/v1/reading/private-libraries",
            new SetPrivateLibrariesRequest { LibraryIds = [] });
    }

    private async Task<HttpClient> CreateAndLoginReaderAsync(HttpClient adminClient, string username)
    {
        await adminClient.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = username,
            Password = "ReaderPass123!",
            IsAdmin = false,
        });

        var reader = _factory.CreateClient();
        await reader.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = username,
            Password = "ReaderPass123!",
        });

        var csrf = await reader.GetAsync("/api/v1/auth/csrf");
        var csrfDto = await csrf.Content.ReadFromJsonAsync<CsrfTokenDto>();
        reader.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrfDto!.Token);

        await reader.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "ReaderPass123!",
            NewPassword = "ReaderNewPass123!",
        });

        reader = _factory.CreateClient();
        await reader.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = username,
            Password = "ReaderNewPass123!",
        });
        var freshCsrf = await reader.GetAsync("/api/v1/auth/csrf");
        var freshDto = await freshCsrf.Content.ReadFromJsonAsync<CsrfTokenDto>();
        reader.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", freshDto!.Token);

        return reader;
    }

    /// <summary>
    /// Seeds two libraries via the admin API (so they go through the same
    /// pipeline as GetLibraries), then seeds archive nodes and FTS entries
    /// directly in the database. Idempotent — only seeds once per fixture.
    /// Returns the public IDs for use in API calls.
    /// </summary>
    private async Task<(string libAPubId, string libBPubId)> SeedLibrariesAsync(HttpClient adminClient)
    {
        await _seedLock.WaitAsync();
        try
        {
            if (_seeded && _libAPubId is not null && _libBPubId is not null)
                return (_libAPubId, _libBPubId);

            // Create temp directories for library root paths
            var tempRoot = Path.Combine(Path.GetTempPath(), "mangapixer-ig-" + Guid.NewGuid().ToString("N")[..8]);
            var dirA = Path.Combine(tempRoot, "lib-a");
            var dirB = Path.Combine(tempRoot, "lib-b");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            // Create libraries via admin API
            var respA = await adminClient.PostAsJsonAsync("/api/v1/admin/libraries",
                new RegisterLibraryRequest { DisplayName = "Incognito Public", RootPath = dirA });
            respA.EnsureSuccessStatusCode();
            var libADto = await respA.Content.ReadFromJsonAsync<LibraryDto>();
            Assert.NotNull(libADto);

            var respB = await adminClient.PostAsJsonAsync("/api/v1/admin/libraries",
                new RegisterLibraryRequest { DisplayName = "Incognito Private", RootPath = dirB });
            respB.EnsureSuccessStatusCode();
            var libBDto = await respB.Content.ReadFromJsonAsync<LibraryDto>();
            Assert.NotNull(libBDto);

            // Seed archive nodes directly in the database
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
                var libA = await db.Libraries.FirstAsync(l => l.PublicId == libADto!.Id);
                var libB = await db.Libraries.FirstAsync(l => l.PublicId == libBDto!.Id);

                foreach (var (pubId, libId, name) in new[]
                {
                    ("ignodeA", libA.Id, "Alpha Comic"),
                    ("ignodeB", libB.Id, "Beta Comic"),
                })
                {
                    if (await db.CatalogNodes.AnyAsync(n => n.PublicId == pubId))
                        continue;

                    var node = new CatalogNodeEntity
                    {
                        PublicId = pubId,
                        LibraryId = libId,
                        Kind = 1,
                        DisplayName = name,
                        RelativePath = $"{dirA}/{pubId}.cbz",
                        PathKey = $"{dirA}/{pubId}.cbz",
                        SortKey = pubId,
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
                        ModificationTicks = 0,
                        ContentVersion = 1,
                        AnalysisState = 0,
                        PageCount = 10,
                    });
                    await db.SaveChangesAsync();
                }
            }

            _seeded = true;
            _libAPubId = libADto!.Id;
            _libBPubId = libBDto!.Id;
            return (_libAPubId, _libBPubId);
        }
        finally
        {
            _seedLock.Release();
        }
    }

    private async Task SeedProgressAsync(string nodePublicId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();

        var node = await db.CatalogNodes.FirstOrDefaultAsync(n => n.PublicId == nodePublicId);
        if (node is null) return;

        var admin = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == "ADMIN");
        if (admin is null) return;

        var existing = await db.ReadingProgress.FirstOrDefaultAsync(p => p.UserId == admin.Id && p.ItemId == node.Id);
        if (existing is not null) return;

        db.ReadingProgress.Add(new ReadingProgressEntity
        {
            UserId = admin.Id,
            ItemId = node.Id,
            ContentVersion = 1,
            EntryKey = OpaqueId.Encode(5),
            Ordinal = 5,
            State = (int)ReadingState.InProgress,
            Revision = 1,
            LastMutationId = "ig-" + nodePublicId,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // --- Prefs endpoints ---

    [Fact]
    public async Task GetPrivateLibraries_EmptyByDefault()
    {
        var client = await GetAdminClientAsync();
        await SeedLibrariesAsync(client);
        await ClearPrivateLibrariesAsync(client);

        var response = await client.GetAsync("/api/v1/reading/private-libraries");
        response.EnsureSuccessStatusCode();

        var dto = await response.Content.ReadFromJsonAsync<PrivateLibrariesDto>();
        Assert.NotNull(dto);
        Assert.Empty(dto!.LibraryIds);
    }

    [Fact]
    public async Task SetPrivateLibraries_ReplacesEntireSet()
    {
        var client = await GetAdminClientAsync();
        var (_, libBPubId) = await SeedLibrariesAsync(client);
        await ClearPrivateLibrariesAsync(client);

        // Set libB as private
        var putResponse = await client.PutAsJsonAsync("/api/v1/reading/private-libraries",
            new SetPrivateLibrariesRequest { LibraryIds = [libBPubId] });
        Assert.Equal(HttpStatusCode.NoContent, putResponse.StatusCode);

        var getResponse = await client.GetAsync("/api/v1/reading/private-libraries");
        getResponse.EnsureSuccessStatusCode();
        var dto = await getResponse.Content.ReadFromJsonAsync<PrivateLibrariesDto>();
        Assert.NotNull(dto);
        Assert.Single(dto!.LibraryIds);
        Assert.Contains(libBPubId, dto.LibraryIds);

        // Clear
        await ClearPrivateLibrariesAsync(client);

        var emptyGet = await client.GetAsync("/api/v1/reading/private-libraries");
        emptyGet.EnsureSuccessStatusCode();
        var emptyDto = await emptyGet.Content.ReadFromJsonAsync<PrivateLibrariesDto>();
        Assert.Empty(emptyDto!.LibraryIds);
    }

    // --- X-Incognito header behavior ---

    [Fact]
    public async Task GetLibraries_Incognito_ExcludesPrivateLibraries()
    {
        var client = await GetAdminClientAsync();
        var (libAPubId, libBPubId) = await SeedLibrariesAsync(client);
        await ClearPrivateLibrariesAsync(client);

        // Without incognito and no private markings → both libraries visible
        var baselineResponse = await client.GetAsync("/api/v1/libraries");
        baselineResponse.EnsureSuccessStatusCode();
        var baselineLibs = await baselineResponse.Content.ReadFromJsonAsync<List<LibraryDto>>();
        Assert.NotNull(baselineLibs);
        Assert.Contains(baselineLibs!, l => l.Id == libAPubId);
        Assert.Contains(baselineLibs!, l => l.Id == libBPubId);

        // Mark libB as private
        await client.PutAsJsonAsync("/api/v1/reading/private-libraries",
            new SetPrivateLibrariesRequest { LibraryIds = [libBPubId] });

        // Without incognito → both libraries still visible (private is incognito-only)
        var normalResponse = await client.GetAsync("/api/v1/libraries");
        normalResponse.EnsureSuccessStatusCode();
        var normalLibs = await normalResponse.Content.ReadFromJsonAsync<List<LibraryDto>>();
        Assert.NotNull(normalLibs);
        Assert.Contains(normalLibs!, l => l.Id == libAPubId);
        Assert.Contains(normalLibs!, l => l.Id == libBPubId);

        // With X-Incognito → only public library visible
        client.DefaultRequestHeaders.Add("X-Incognito", "1");
        var incogResponse = await client.GetAsync("/api/v1/libraries");
        incogResponse.EnsureSuccessStatusCode();
        var incogLibs = await incogResponse.Content.ReadFromJsonAsync<List<LibraryDto>>();
        Assert.NotNull(incogLibs);
        Assert.Contains(incogLibs!, l => l.Id == libAPubId);
        Assert.DoesNotContain(incogLibs!, l => l.Id == libBPubId);

        // Cleanup
        await ClearPrivateLibrariesAsync(client);
    }

    [Fact]
    public async Task ContinueReading_Incognito_ExcludesPrivateLibraryItems()
    {
        var client = await GetAdminClientAsync();
        var (_, libBPubId) = await SeedLibrariesAsync(client);
        await ClearPrivateLibrariesAsync(client);

        // Seed progress in both libraries
        await SeedProgressAsync("ignodeA");
        await SeedProgressAsync("ignodeB");

        // Mark libB as private
        await client.PutAsJsonAsync("/api/v1/reading/private-libraries",
            new SetPrivateLibrariesRequest { LibraryIds = [libBPubId] });

        // Without incognito → both items
        var normalResponse = await client.GetAsync("/api/v1/reading/continue");
        normalResponse.EnsureSuccessStatusCode();
        var normalEntries = await normalResponse.Content.ReadFromJsonAsync<List<JsonElement>>();
        Assert.NotNull(normalEntries);
        Assert.True(normalEntries!.Count >= 2);

        // With X-Incognito → only public library item
        client.DefaultRequestHeaders.Add("X-Incognito", "1");
        var incogResponse = await client.GetAsync("/api/v1/reading/continue");
        incogResponse.EnsureSuccessStatusCode();
        var incogEntries = await incogResponse.Content.ReadFromJsonAsync<List<JsonElement>>();
        Assert.NotNull(incogEntries);
        Assert.Single(incogEntries!);
        var libId = incogEntries[0].GetProperty("libraryId").GetString();
        Assert.NotNull(libId);
        Assert.NotEqual(libBPubId, libId);

        // Cleanup
        await ClearPrivateLibrariesAsync(client);
    }

    [Fact]
    public async Task Search_Incognito_ExcludesPrivateLibraryResults()
    {
        var client = await GetAdminClientAsync();
        var (libAPubId, libBPubId) = await SeedLibrariesAsync(client);
        await ClearPrivateLibrariesAsync(client);

        // "Comic" matches both nodes (without incognito)
        var normalResponse = await client.GetAsync("/api/v1/search?q=Comic");
        normalResponse.EnsureSuccessStatusCode();
        var normalJson = await normalResponse.Content.ReadAsStringAsync();
        using var normalDoc = JsonDocument.Parse(normalJson);
        var normalCount = normalDoc.RootElement.GetProperty("totalCount").GetInt32();
        Assert.Equal(2, normalCount);

        // Mark libB as private
        await client.PutAsJsonAsync("/api/v1/reading/private-libraries",
            new SetPrivateLibrariesRequest { LibraryIds = [libBPubId] });

        // With X-Incognito → only public library result
        client.DefaultRequestHeaders.Add("X-Incognito", "1");
        var incogResponse = await client.GetAsync("/api/v1/search?q=Comic");
        incogResponse.EnsureSuccessStatusCode();
        var incogJson = await incogResponse.Content.ReadAsStringAsync();
        using var incogDoc = JsonDocument.Parse(incogJson);
        var incogCount = incogDoc.RootElement.GetProperty("totalCount").GetInt32();
        Assert.Equal(1, incogCount);
        var incogLibId = incogDoc.RootElement.GetProperty("items")[0].GetProperty("libraryId").GetString();
        Assert.Equal(libAPubId, incogLibId);

        // Cleanup
        await ClearPrivateLibrariesAsync(client);
    }

    // --- Non-owner isolation ---

    [Fact]
    public async Task PrivateLibraries_DoNotAffectOtherUsers()
    {
        var adminClient = await GetAdminClientAsync();
        var (libAPubId, libBPubId) = await SeedLibrariesAsync(adminClient);
        await ClearPrivateLibrariesAsync(adminClient);

        // Admin marks libB as private
        await adminClient.PutAsJsonAsync("/api/v1/reading/private-libraries",
            new SetPrivateLibrariesRequest { LibraryIds = [libBPubId] });

        // Create and login as a second user (reader)
        var readerClient = await CreateAndLoginReaderAsync(adminClient, "igreader");

        // Grant reader access to both libraries
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var reader = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == "IGREADER");
            if (reader is not null)
            {
                var libA = await db.Libraries.FirstAsync(l => l.PublicId == libAPubId);
                var libB = await db.Libraries.FirstAsync(l => l.PublicId == libBPubId);
                if (!await db.LibraryGrants.AnyAsync(g => g.UserId == reader.Id && g.LibraryId == libA.Id))
                {
                    db.LibraryGrants.Add(new LibraryGrantEntity
                    {
                        UserId = reader.Id,
                        LibraryId = libA.Id,
                        GrantedAt = DateTimeOffset.UtcNow,
                    });
                }
                if (!await db.LibraryGrants.AnyAsync(g => g.UserId == reader.Id && g.LibraryId == libB.Id))
                {
                    db.LibraryGrants.Add(new LibraryGrantEntity
                    {
                        UserId = reader.Id,
                        LibraryId = libB.Id,
                        GrantedAt = DateTimeOffset.UtcNow,
                    });
                }
                await db.SaveChangesAsync();
            }
        }

        // Reader's private-libraries should be empty (admin's set is private to admin)
        var readerPrefs = await readerClient.GetAsync("/api/v1/reading/private-libraries");
        readerPrefs.EnsureSuccessStatusCode();
        var readerDto = await readerPrefs.Content.ReadFromJsonAsync<PrivateLibrariesDto>();
        Assert.Empty(readerDto!.LibraryIds);

        // Reader sees both libraries even with X-Incognito (no private set)
        readerClient.DefaultRequestHeaders.Add("X-Incognito", "1");
        var readerLibsResponse = await readerClient.GetAsync("/api/v1/libraries");
        readerLibsResponse.EnsureSuccessStatusCode();
        var readerLibs = await readerLibsResponse.Content.ReadFromJsonAsync<List<LibraryDto>>();
        Assert.NotNull(readerLibs);
        Assert.Contains(readerLibs!, l => l.Id == libAPubId);
        Assert.Contains(readerLibs!, l => l.Id == libBPubId);

        // Cleanup
        await ClearPrivateLibrariesAsync(adminClient);
    }
}
