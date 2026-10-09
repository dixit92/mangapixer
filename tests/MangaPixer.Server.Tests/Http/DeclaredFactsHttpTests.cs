namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Declared;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) for declared facts (1.28.0): the admin endpoints at folder and library
/// scope (403 for a reader, 401 anonymously, validation codes, folders only), the node endpoint the Info panel
/// reads (404 for a non-member, direct access in Incognito, inheritance from the library, a conflict with a
/// linked record) and the DI wiring of <see cref="IDeclaredFactsReader"/>; 1.39.0: the folder's own edition facts (admin only, folders
/// only, saved apart from the type) and tracking off reaching the Missing report. Synthetic names only.
/// </summary>
public sealed class DeclaredFactsHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const string LibPubId = "dflib1";
    private const string OtherLibPubId = "dflib2";
    private readonly MangaPixerWebApplicationFactory _factory;

    public DeclaredFactsHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // Node public ids: dfShelf > dfSeries > dfArc (archive); dfLinked (folder linked to a web record);
    // dfOther (folder in the other library).
    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        if (await db.Libraries.AnyAsync(l => l.PublicId == LibPubId))
            return;

        var lib = new LibraryEntity { PublicId = LibPubId, DisplayName = "Declared Http", RootPath = "/synthetic/df1", CreatedAt = DateTimeOffset.UtcNow };
        var other = new LibraryEntity { PublicId = OtherLibPubId, DisplayName = "Declared Other", RootPath = "/synthetic/df2", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.AddRange(lib, other);
        await db.SaveChangesAsync();

        var shelf = Node("dfShelf", lib.Id, null, 0, "Synthetic Shelf");
        var linked = Node("dfLinked", lib.Id, null, 0, "Synthetic Linked");
        var otherFolder = Node("dfOther", other.Id, null, 0, "Other Folder");
        db.CatalogNodes.AddRange(shelf, linked, otherFolder);
        await db.SaveChangesAsync();
        var series = Node("dfSeries", lib.Id, shelf.Id, 0, "Synthetic Series");
        db.CatalogNodes.Add(series);
        await db.SaveChangesAsync();
        var arc = Node("dfArc", lib.Id, series.Id, 1, "Synthetic Series v01");
        db.CatalogNodes.Add(arc);
        await db.SaveChangesAsync();
        db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = arc.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 2 });

        var record = new MetadataRecordEntity
        {
            PublicId = "dfrec1",
            Provider = "mangaupdates",
            ExternalId = "515151",
            Title = "Synthetic Web Title",
            ProviderType = "Manga",
            Origin = (int)MetadataOrigin.Japan,
            Format = (int)MetadataFormat.Comic,
            CreatorsJson = "[{\"name\":\"Web Author\",\"role\":\"author\"}]",
            FetchedAt = DateTimeOffset.UtcNow,
        };
        db.MetadataRecords.Add(record);
        await db.SaveChangesAsync();
        db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
        {
            NodeId = linked.Id,
            LibraryId = lib.Id,
            State = (int)SeriesLinkState.Confirmed,
            RecordId = record.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static CatalogNodeEntity Node(string pub, long libId, long? parentId, int kind, string name) => new()
    {
        PublicId = pub,
        LibraryId = libId,
        ParentId = parentId,
        Kind = kind,
        DisplayName = name,
        RelativePath = pub,
        PathKey = pub,
        SortKey = (kind == 0 ? "0" : "1") + name,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<HttpClient> AdminAsync()
    {
        await SeedAsync();
        return await _factory.LoginAsAdminWithChangedPasswordAsync();
    }

    /// <summary>Creates (once) an activated reader account and returns a signed-in client.</summary>
    private async Task<(HttpClient Client, long UserId)> ReaderAsync(string username)
    {
        var admin = await AdminAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            if (!await db.Users.AnyAsync(u => u.NormalizedUserName == username.ToUpperInvariant()))
            {
                var created = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest { Username = username, IsAdmin = false });
                created.EnsureSuccessStatusCode();
                var result = await created.Content.ReadFromJsonAsync<CreateUserResponse>(TestJson.Web);
                var url = result!.ActivationUrl!;
                var token = Uri.UnescapeDataString(url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
                var activate = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/activate",
                    new ActivateAccountRequest { Token = token, Password = "ReaderPassword123!" });
                activate.EnsureSuccessStatusCode();
            }
        }

        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = username, Password = "ReaderPassword123!" })).EnsureSuccessStatusCode();
        var csrf = await (await client.GetAsync("/api/v1/auth/csrf")).Content.ReadFromJsonAsync<CsrfTokenDto>();
        client.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);

        using var scope2 = _factory.Services.CreateScope();
        var userId = await scope2.ServiceProvider.GetRequiredService<MangaPixerDbContext>()
            .Users.Where(u => u.NormalizedUserName == username.ToUpperInvariant()).Select(u => u.Id).SingleAsync();
        return (client, userId);
    }

    private async Task GrantAsync(long userId, string libraryPublicId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var libId = await db.Libraries.Where(l => l.PublicId == libraryPublicId).Select(l => l.Id).SingleAsync();
        if (!await db.LibraryGrants.AnyAsync(g => g.UserId == userId && g.LibraryId == libId))
        {
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = userId, LibraryId = libId, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
    }

    private static SetDeclaredFactsRequest Declare(DeclaredType? type, params string[] creators) => new()
    {
        Type = type,
        Creators = creators.Select(c => new DeclaredCreatorDto { Name = c }).ToList(),
    };

    private static async Task<T> OkAsync<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Web))!;
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string url, SetDeclaredFactsRequest request) =>
        client.PutAsJsonAsync(url, request, TestJson.Web);

    // --- authorization ---

    [Fact]
    public async Task AdminEndpoints_AreForbiddenForReaders_AndUnauthorizedAnonymously()
    {
        var (reader, readerId) = await ReaderAsync("dfreader1");
        await GrantAsync(readerId, LibPubId);

        foreach (var url in new[] { "/api/v1/admin/metadata/folders/dfSeries/declared", $"/api/v1/admin/metadata/libraries/{LibPubId}/declared" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(reader, url, Declare(DeclaredType.Manga))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync(url)).StatusCode);
            using var anon = _factory.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(url)).StatusCode);
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var libId = await db.Libraries.Where(l => l.PublicId == LibPubId).Select(l => l.Id).SingleAsync();
        Assert.False(await db.DeclaredFacts.AnyAsync(f => f.LibraryId == libId && f.Value == "manga"));
    }

    // --- admin: folder + library scope ---

    [Fact]
    public async Task Admin_SetsFolderAndLibrary_TheNodeEndpointShowsInheritance()
    {
        var admin = await AdminAsync();
        var (reader, readerId) = await ReaderAsync("dfreader2");

        var lib = await OkAsync<DeclaredFactsScopeDto>(await PutAsync(admin, $"/api/v1/admin/metadata/libraries/{LibPubId}/declared",
            Declare(DeclaredType.Manga, "Library Author")));
        Assert.Null(lib.NodeId);
        Assert.Equal(DeclaredType.Manga, lib.Own.Type);
        Assert.Equal("Declared Http", lib.DisplayName);

        var folder = await OkAsync<DeclaredFactsScopeDto>(await PutAsync(admin, "/api/v1/admin/metadata/folders/dfShelf/declared",
            Declare(DeclaredType.Manhwa)));
        Assert.Equal("dfShelf", folder.NodeId);
        Assert.Equal(DeclaredType.Manhwa, folder.Own.Type);
        Assert.Equal(DeclaredType.Manga, folder.Inherited.Type);
        Assert.Equal(DeclaredFactSource.Library, folder.Inherited.TypeSource);

        var series = await OkAsync<DeclaredFactsScopeDto>(await admin.GetAsync("/api/v1/admin/metadata/folders/dfSeries/declared"));
        Assert.Null(series.Own.Type);
        Assert.Equal(DeclaredType.Manhwa, series.Inherited.Type);
        Assert.Equal("Synthetic Shelf", series.Inherited.TypeFrom);

        // A reader of the library sees the effective facts on the archive (Info panel), non-members get 404.
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync("/api/v1/nodes/dfArc/declared-facts")).StatusCode);
        await GrantAsync(readerId, LibPubId);
        var arc = await OkAsync<NodeDeclaredFactsDto>(await reader.GetAsync("/api/v1/nodes/dfArc/declared-facts"));
        Assert.Equal(DeclaredType.Manhwa, arc.Effective.Type);
        Assert.Equal(DeclaredFactSource.Inherited, arc.Effective.TypeSource);
        Assert.Equal("Library Author", Assert.Single(arc.Effective.Creators).Name);
        Assert.Equal(DeclaredFactSource.Library, arc.Effective.CreatorsSource);
        Assert.Null(arc.Conflict);

        // Private + Incognito only filters discovery; direct access through the node still works.
        (await reader.PutAsJsonAsync("/api/v1/reading/private-libraries", new SetPrivateLibrariesRequest { LibraryIds = [LibPubId] })).EnsureSuccessStatusCode();
        reader.DefaultRequestHeaders.Add("X-Incognito", "true");
        Assert.Equal(DeclaredType.Manhwa, (await OkAsync<NodeDeclaredFactsDto>(await reader.GetAsync("/api/v1/nodes/dfArc/declared-facts"))).Effective.Type);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync("/api/v1/nodes/dfOther/declared-facts")).StatusCode);

        // Clearing the folder: the library's value applies again.
        var cleared = await OkAsync<DeclaredFactsScopeDto>(await admin.DeleteAsync("/api/v1/admin/metadata/folders/dfShelf/declared"));
        Assert.Null(cleared.Own.Type);
        Assert.Equal(DeclaredType.Manga, (await OkAsync<NodeDeclaredFactsDto>(await admin.GetAsync("/api/v1/nodes/dfSeries/declared-facts"))).Effective.Type);
        (await admin.DeleteAsync($"/api/v1/admin/metadata/libraries/{LibPubId}/declared")).EnsureSuccessStatusCode();
        Assert.Null((await OkAsync<NodeDeclaredFactsDto>(await admin.GetAsync("/api/v1/nodes/dfSeries/declared-facts"))).Effective.Type);
    }

    [Fact]
    public async Task Admin_Validation_And_NotFound()
    {
        var admin = await AdminAsync();

        var archive = await PutAsync(admin, "/api/v1/admin/metadata/folders/dfArc/declared", Declare(DeclaredType.Manga));
        Assert.Equal(HttpStatusCode.BadRequest, archive.StatusCode);
        Assert.Equal("not_a_folder", (await archive.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);

        var role = await admin.PutAsJsonAsync("/api/v1/admin/metadata/folders/dfSeries/declared",
            new { creators = new[] { new { name = "Someone", role = "colorist" } } });
        Assert.Equal(HttpStatusCode.BadRequest, role.StatusCode);
        Assert.Equal("creator_role_invalid", (await role.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);

        var blank = await PutAsync(admin, "/api/v1/admin/metadata/folders/dfSeries/declared", Declare(null, " "));
        Assert.Equal("creator_name_invalid", (await blank.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);

        // Types travel as their names.
        var unknownType = await admin.PutAsJsonAsync("/api/v1/admin/metadata/folders/dfSeries/declared", new { type = "LightNovel" });
        Assert.Equal(HttpStatusCode.BadRequest, unknownType.StatusCode);
        var named = await admin.PutAsJsonAsync("/api/v1/admin/metadata/folders/dfSeries/declared", new { type = "GraphicNovel" });
        Assert.Equal(DeclaredType.GraphicNovel, (await OkAsync<DeclaredFactsScopeDto>(named)).Own.Type);
        (await admin.DeleteAsync("/api/v1/admin/metadata/folders/dfSeries/declared")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/admin/metadata/folders/nope/declared")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/admin/metadata/libraries/nope/declared")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/nodes/nope/declared-facts")).StatusCode);
    }

    [Fact]
    public async Task NodeEndpoint_ShowsAConflictWithTheLinkedRecord()
    {
        var admin = await AdminAsync();

        (await PutAsync(admin, "/api/v1/admin/metadata/folders/dfLinked/declared", Declare(DeclaredType.Manga, "Web Author"))).EnsureSuccessStatusCode();
        Assert.Null((await OkAsync<NodeDeclaredFactsDto>(await admin.GetAsync("/api/v1/nodes/dfLinked/declared-facts"))).Conflict);

        (await PutAsync(admin, "/api/v1/admin/metadata/folders/dfLinked/declared", Declare(DeclaredType.Manhwa, "Someone Else"))).EnsureSuccessStatusCode();
        var dto = await OkAsync<NodeDeclaredFactsDto>(await admin.GetAsync("/api/v1/nodes/dfLinked/declared-facts"));
        Assert.Equal(DeclaredType.Manhwa, dto.Effective.Type);
        Assert.NotNull(dto.Conflict);
        Assert.True(dto.Conflict!.Type);
        Assert.Equal("Manga", dto.Conflict.RecordType);
        Assert.True(dto.Conflict.Creators);
        Assert.Equal(["Web Author"], dto.Conflict.RecordCreators);
        Assert.Equal("MangaUpdates", dto.Conflict.ProviderName);

        (await admin.DeleteAsync("/api/v1/admin/metadata/folders/dfLinked/declared")).EnsureSuccessStatusCode();
    }

    // --- 1.39.0: the folder's own edition facts ---

    [Fact]
    public async Task Edition_AdminOnly_FolderOnly_SavedApart_AndTrackingOffLeavesTheMissingReport()
    {
        var admin = await AdminAsync();
        var (reader, readerId) = await ReaderAsync("dfreader3");
        await GrantAsync(readerId, LibPubId);
        const string url = "/api/v1/admin/metadata/folders/dfLinked/declared/edition";
        var body = new { volumeTotal = 12, edition = "Omnibus", tracking = false };

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync(url, body)).StatusCode);
        using (var anon = _factory.CreateClient())
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsJsonAsync(url, body)).StatusCode);
        var invalid = await admin.PutAsJsonAsync(url, new { volumeTotal = 1000 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("volumes_invalid", (await invalid.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(url, new { edition = "Bunkoban" })).StatusCode);
        var archive = await admin.PutAsJsonAsync("/api/v1/admin/metadata/folders/dfArc/declared/edition", body);
        Assert.Equal("not_a_folder", (await archive.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPubId}/declared/edition", body)).StatusCode);

        (await PutAsync(admin, "/api/v1/admin/metadata/folders/dfLinked/declared", Declare(DeclaredType.Manga))).EnsureSuccessStatusCode();
        var saved = await OkAsync<DeclaredFactsScopeDto>(await admin.PutAsJsonAsync(url, body));
        Assert.Equal(DeclaredType.Manga, saved.Own.Type); // the type stays
        Assert.Equal(new DeclaredEditionDto { VolumeTotal = 12, Edition = DeclaredEdition.Omnibus, Tracking = false }, saved.Own.Edition);
        // A type save leaves the edition alone; readers see it on the folder's Info panel line.
        var retyped = await OkAsync<DeclaredFactsScopeDto>(await PutAsync(admin, "/api/v1/admin/metadata/folders/dfLinked/declared", Declare(DeclaredType.Manhwa)));
        Assert.Equal(12, retyped.Own.Edition!.VolumeTotal);
        Assert.Equal(DeclaredEdition.Omnibus, (await OkAsync<NodeDeclaredFactsDto>(await reader.GetAsync("/api/v1/nodes/dfLinked/declared-facts"))).Edition!.Edition);

        // Tracking off: the linked folder leaves the Missing report and is counted apart; its own row says why.
        var page = await OkAsync<MissingReportPageDto>(await admin.GetAsync("/api/v1/admin/metadata/missing?onlyMissing=false"));
        Assert.DoesNotContain(page.Items, i => i.NodeId == "dfLinked");
        Assert.Equal(1, page.Summary.NotTracked);

        var cleared = await OkAsync<DeclaredFactsScopeDto>(await admin.DeleteAsync("/api/v1/admin/metadata/folders/dfLinked/declared"));
        Assert.Null(cleared.Own.Type);
        Assert.Null(cleared.Own.Edition);
        var after = await OkAsync<MissingReportPageDto>(await admin.GetAsync("/api/v1/admin/metadata/missing?onlyMissing=false"));
        Assert.Contains(after.Items, i => i.NodeId == "dfLinked");
        Assert.Equal(0, after.Summary.NotTracked);
    }

    [Fact]
    public async Task Wiring_ReaderIsRegistered_AndSeesAdminDeclarations()
    {
        var admin = await AdminAsync();
        (await PutAsync(admin, $"/api/v1/admin/metadata/libraries/{OtherLibPubId}/declared", Declare(DeclaredType.Novel))).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var reader = scope.ServiceProvider.GetRequiredService<IDeclaredFactsReader>();
        var otherId = await db.Libraries.Where(l => l.PublicId == OtherLibPubId).Select(l => l.Id).SingleAsync();
        var folderId = await db.CatalogNodes.Where(n => n.PublicId == "dfOther").Select(n => n.Id).SingleAsync();

        var facts = await reader.EffectiveForLibraryAsync(otherId, CancellationToken.None);

        Assert.Equal("novel", facts[folderId].Type);
        Assert.Equal(DeclaredFactSource.Library, facts[folderId].TypeSource);
        (await admin.DeleteAsync($"/api/v1/admin/metadata/libraries/{OtherLibPubId}/declared")).EnsureSuccessStatusCode();
    }
}
