namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) for series metadata stage 1 (1.24.0, lane B1):
/// <c>GET nodes/{id}/series-info</c> access rules (404 for a non-member, direct
/// access in Incognito), the admin endpoints (403 for readers, validation codes),
/// the "Show series information" toggle across series-info and browse, links /
/// Don't match / precedence / purge through the API, and the DI wiring.
/// </summary>
[Collection("HttpSerial")]
public sealed class MetadataHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const string LibPubId = "mdlib1";
    private const string OtherLibPubId = "mdlib2";
    private readonly MangaPixerWebApplicationFactory _factory;

    public MetadataHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // Node public ids: mdFolder (CI series folder), mdArc1/mdArc2 (its archives),
    // mdPlain (folder without info), mdOther (folder in the other library).
    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        if (await db.Libraries.AnyAsync(l => l.PublicId == LibPubId))
            return;

        var lib = new LibraryEntity { PublicId = LibPubId, DisplayName = "Meta Http", RootPath = "/synthetic/md1", CreatedAt = DateTimeOffset.UtcNow };
        var other = new LibraryEntity { PublicId = OtherLibPubId, DisplayName = "Meta Other", RootPath = "/synthetic/md2", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.AddRange(lib, other);
        await db.SaveChangesAsync();

        var folder = Node("mdFolder", lib.Id, null, 0, "Synthetic Series");
        var plain = Node("mdPlain", lib.Id, null, 0, "Plain Folder");
        var otherFolder = Node("mdOther", other.Id, null, 0, "Other Folder");
        db.CatalogNodes.AddRange(folder, plain, otherFolder);
        await db.SaveChangesAsync();

        var a1 = Node("mdArc1", lib.Id, folder.Id, 1, "Synthetic 01");
        var a2 = Node("mdArc2", lib.Id, folder.Id, 1, "Synthetic 02");
        db.CatalogNodes.AddRange(a1, a2);
        await db.SaveChangesAsync();
        foreach (var (node, number) in new[] { (a1, "1"), (a2, "2") })
        {
            db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = node.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 2 });
            await ComicInfoPersister.StageAsync(db, node.Id, 1, new ComicInfoOutcome
            {
                Status = ComicInfoStatus.Parsed,
                Payload = new ComicInfoPayload { Series = "Synthetic Series", Number = number, Year = 2020, Title = "Chapter " + number },
            }, DateTimeOffset.UtcNow);
        }

        db.MetadataRecords.Add(new MetadataRecordEntity
        {
            PublicId = "mdrec1",
            Provider = "mangaupdates",
            ExternalId = "424242",
            Title = "Synthetic Web Title",
            Description = "Web description.",
            FetchedAt = DateTimeOffset.UtcNow,
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

    private static async Task<SeriesInfoDto> InfoAsync(HttpClient client, string nodeId, bool includeItems = false)
    {
        var response = await client.GetAsync($"/api/v1/nodes/{nodeId}/series-info" + (includeItems ? "?includeItems=true" : ""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SeriesInfoDto>(TestJson.Web))!;
    }

    // --- series-info access ---

    [Fact]
    public async Task SeriesInfo_Anonymous_Is401_UnknownNode_Is404()
    {
        await SeedAsync();
        using var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/nodes/mdFolder/series-info")).StatusCode);

        var admin = await AdminAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/nodes/does-not-exist/series-info")).StatusCode);
    }

    [Fact]
    public async Task SeriesInfo_Admin_SeesTheComicInfoSeries_WithItems()
    {
        var admin = await AdminAsync();

        var info = await InfoAsync(admin, "mdFolder", includeItems: true);

        Assert.Equal(SeriesInfoState.ComicInfo, info.State);
        Assert.Equal("Synthetic Series", info.Title);
        Assert.Equal(LibPubId, info.LibraryId);
        Assert.Equal(2, info.ComicInfo!.ItemsWithComicInfo);
        Assert.Equal(["1", "2"], info.Items.Select(i => i.Number));

        var archive = await InfoAsync(admin, "mdArc2");
        Assert.Equal("mdFolder", archive.AnchorNodeId);
        Assert.Equal("Chapter 2", archive.Item!.Title);
    }

    [Fact]
    public async Task SeriesInfo_NonMember_Is404_Member_Is200_EvenInIncognito()
    {
        var (reader, readerId) = await ReaderAsync("mdreader1");

        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync("/api/v1/nodes/mdFolder/series-info")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync("/api/v1/nodes/mdOther/series-info")).StatusCode);

        await GrantAsync(readerId, LibPubId);
        Assert.Equal(SeriesInfoState.ComicInfo, (await InfoAsync(reader, "mdFolder")).State);

        // Incognito with the library marked Private only filters discovery surfaces;
        // direct access through the node still works.
        (await reader.PutAsJsonAsync("/api/v1/reading/private-libraries", new SetPrivateLibrariesRequest { LibraryIds = [LibPubId] })).EnsureSuccessStatusCode();
        reader.DefaultRequestHeaders.Add("X-Incognito", "true");
        Assert.Equal(SeriesInfoState.ComicInfo, (await InfoAsync(reader, "mdFolder")).State);
    }

    // --- admin endpoints: authorization + validation ---

    [Fact]
    public async Task AdminEndpoints_AreForbiddenForReaders_AndUnauthorizedAnonymously()
    {
        var (reader, _) = await ReaderAsync("mdreader2");
        using var anon = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/admin/metadata/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync("/api/v1/admin/metadata/nodes/mdFolder/dont-match", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync("/api/v1/admin/metadata/folders/mdFolder/precedence",
            new SetMetadataPrecedenceRequest { Precedence = MetadataPrecedence.ComicInfoFirst })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/admin/metadata/settings")).StatusCode);
    }

    [Fact]
    public async Task Settings_Defaults_And_Validation()
    {
        var admin = await AdminAsync();

        var settings = await admin.GetFromJsonAsync<MetadataSettingsDto>("/api/v1/admin/metadata/settings", TestJson.Web);
        Assert.True(settings!.ShowSeriesInfo);
        Assert.False(settings.FetchEnabled);
        Assert.Equal(5000, settings.DailyBudget);
        Assert.Equal(1, settings.CurrentConsentVersion);
        Assert.Contains(settings.Libraries, l => l.LibraryId == LibPubId && !l.FetchEnabled && l.ShowSeriesInfo);

        var noConsent = await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings", new UpdateMetadataSettingsRequest { FetchEnabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, noConsent.StatusCode);
        Assert.Equal("consent_required", (await noConsent.Content.ReadFromJsonAsync<ApiError>())!.Error);

        var badBudget = await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings", new UpdateMetadataSettingsRequest { DailyBudget = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, badBudget.StatusCode);
        Assert.Equal("invalid_daily_budget", (await badBudget.Content.ReadFromJsonAsync<ApiError>())!.Error);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/v1/admin/metadata/libraries/nope", new UpdateMetadataLibraryRequest { FetchEnabled = true })).StatusCode);
    }

    [Fact]
    public async Task ShowSeriesInfoOff_HidesSeriesInfo_AndTheBrowseFlag_ThenComesBack()
    {
        var admin = await AdminAsync();
        static async Task<bool> FolderFlagAsync(HttpClient c)
        {
            var page = await c.GetFromJsonAsync<PageResponse<CatalogNodeDto>>($"/api/v1/libraries/{LibPubId}/browse", TestJson.Web);
            return page!.Items.Single(i => i.Id == "mdFolder").HasSeriesInfo;
        }

        Assert.True(await FolderFlagAsync(admin));
        try
        {
            (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings", new UpdateMetadataSettingsRequest { ShowSeriesInfo = false })).EnsureSuccessStatusCode();

            var hidden = await InfoAsync(admin, "mdFolder");
            Assert.Equal(SeriesInfoState.None, hidden.State);
            Assert.Null(hidden.Title);
            Assert.False(await FolderFlagAsync(admin));

            (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings", new UpdateMetadataSettingsRequest { ShowSeriesInfo = true })).EnsureSuccessStatusCode();
            (await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPubId}", new UpdateMetadataLibraryRequest { ShowSeriesInfo = false })).EnsureSuccessStatusCode();
            Assert.Equal(SeriesInfoState.None, (await InfoAsync(admin, "mdArc1")).State);
        }
        finally
        {
            (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings", new UpdateMetadataSettingsRequest { ShowSeriesInfo = true })).EnsureSuccessStatusCode();
            (await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPubId}", new UpdateMetadataLibraryRequest { ShowSeriesInfo = true })).EnsureSuccessStatusCode();
        }

        Assert.Equal(SeriesInfoState.ComicInfo, (await InfoAsync(admin, "mdFolder")).State);
        Assert.True(await FolderFlagAsync(admin));
    }

    // --- links ---

    [Fact]
    public async Task Link_Unlink_DontMatch_ThroughTheApi()
    {
        var admin = await AdminAsync();

        // A provider with no registered implementation cannot fetch: record_not_found.
        // (An unstored MangaUpdates record is fetched first - lane B2, MetadataIdentifyHttpTests.)
        var missing = await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/mdPlain/link", new LinkSeriesRequest { Provider = "anilist", ExternalId = "1" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("record_not_found", (await missing.Content.ReadFromJsonAsync<ApiError>())!.Error);

        var invalid = await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/mdPlain/link", new LinkSeriesRequest { Provider = "Bad Provider", ExternalId = "1" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        try
        {
            var linked = await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/mdFolder/link", new LinkSeriesRequest { Provider = "mangaupdates", ExternalId = "424242" });
            Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
            var change = await linked.Content.ReadFromJsonAsync<NodeSeriesLinkChangeDto>(TestJson.Web);
            Assert.Equal(SeriesLinkState.Confirmed, change!.Link!.State);
            Assert.Equal("mdrec1", change.Link.RecordId);

            var info = await InfoAsync(admin, "mdArc1");
            Assert.Equal(SeriesInfoState.WebAndComicInfo, info.State);
            Assert.Equal("Synthetic Web Title", info.Title);
            Assert.True(info.Link!.Inherited);

            var dont = await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/mdArc1/dont-match", new { });
            Assert.Equal(HttpStatusCode.OK, dont.StatusCode);
            var arcInfo = await InfoAsync(admin, "mdArc1");
            Assert.Equal(SeriesInfoState.ComicInfo, arcInfo.State); // no web, own ComicInfo still shows
            Assert.Equal(SeriesLinkState.DontMatch, arcInfo.Link!.State);

            Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/api/v1/admin/metadata/nodes/mdArc1/dont-match")).StatusCode);
            Assert.Equal(SeriesInfoState.WebAndComicInfo, (await InfoAsync(admin, "mdArc1")).State);
        }
        finally
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/api/v1/admin/metadata/nodes/mdFolder/link")).StatusCode);
        }

        Assert.Equal(SeriesInfoState.ComicInfo, (await InfoAsync(admin, "mdFolder")).State);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/v1/admin/metadata/nodes/nope/link")).StatusCode);

        // The record went with its last link (B1 cannot re-fetch it); re-seed for other tests.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        Assert.False(await db.MetadataRecords.AnyAsync(r => r.PublicId == "mdrec1"));
        var audit = await db.AuditEvents.Where(a => a.Action.StartsWith("metadata.")).Select(a => a.Action).ToListAsync();
        Assert.Contains("metadata.link", audit);
        Assert.Contains("metadata.dont_match", audit);
        Assert.Contains("metadata.unlink", audit);
        db.MetadataRecords.Add(new MetadataRecordEntity { PublicId = "mdrec1", Provider = "mangaupdates", ExternalId = "424242", Title = "Synthetic Web Title", FetchedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    // --- precedence + purge ---

    [Fact]
    public async Task Precedence_FolderAndLibrary_ThroughTheApi()
    {
        var admin = await AdminAsync();

        var onArchive = await admin.PutAsJsonAsync("/api/v1/admin/metadata/folders/mdArc1/precedence",
            new SetMetadataPrecedenceRequest { Precedence = MetadataPrecedence.ComicInfoFirst });
        Assert.Equal(HttpStatusCode.BadRequest, onArchive.StatusCode);
        Assert.Equal("not_a_folder", (await onArchive.Content.ReadFromJsonAsync<ApiError>())!.Error);

        var missing = await admin.PutAsJsonAsync("/api/v1/admin/metadata/folders/mdFolder/precedence", new SetMetadataPrecedenceRequest());
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var set = await admin.PutAsJsonAsync("/api/v1/admin/metadata/folders/mdFolder/precedence",
            new SetMetadataPrecedenceRequest { Precedence = MetadataPrecedence.ComicInfoFirst });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var info = await InfoAsync(admin, "mdArc1");
        Assert.Equal(MetadataPrecedence.ComicInfoFirst, info.Precedence);
        Assert.Equal(MetadataPrecedenceSource.Folder, info.PrecedenceSource);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/v1/admin/metadata/folders/mdFolder/precedence")).StatusCode);
        Assert.Equal(MetadataPrecedenceSource.Default, (await InfoAsync(admin, "mdArc1")).PrecedenceSource);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPubId}/precedence",
            new SetMetadataPrecedenceRequest { Precedence = MetadataPrecedence.ComicInfoFirst })).StatusCode);
        Assert.Equal(MetadataPrecedenceSource.Library, (await InfoAsync(admin, "mdArc1")).PrecedenceSource);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPubId}/precedence",
            new SetMetadataPrecedenceRequest { Precedence = null })).StatusCode);
        Assert.Equal(MetadataPrecedenceSource.Default, (await InfoAsync(admin, "mdArc1")).PrecedenceSource);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/v1/admin/metadata/libraries/nope/precedence",
            new SetMetadataPrecedenceRequest())).StatusCode);
    }

    [Fact]
    public async Task Purge_ReportsCounts_AndRejectsAnUnknownLibrary()
    {
        var admin = await AdminAsync();

        var unknown = await admin.PostAsJsonAsync("/api/v1/admin/metadata/purge", new MetadataPurgeRequest { LibraryId = "nope" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var purge = await admin.PostAsJsonAsync("/api/v1/admin/metadata/purge", new MetadataPurgeRequest { LibraryId = OtherLibPubId });
        Assert.Equal(HttpStatusCode.OK, purge.StatusCode);
        var result = await purge.Content.ReadFromJsonAsync<MetadataPurgeResultDto>();
        Assert.Equal(0, result!.LinksRemoved);
    }

    // --- wiring ---

    [Fact]
    public async Task Wiring_ServicesResolve_AndOnlyMangaUpdatesIsRegistered()
    {
        await SeedAsync();
        using var scope = _factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<SeriesInfoResolver>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<MetadataLinkService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<MetadataSettingsService>());
        Assert.NotNull(_factory.Services.GetRequiredService<ComicInfoBackfillService>());
        // Lane B2: exactly one provider, reachable only through the gateway, plus the
        // poster store registered to delete images with their records.
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<MetadataGateway>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<MetadataIdentifyService>());
        Assert.Equal(["mangaupdates"], _factory.Services.GetRequiredService<MetadataProviderRegistry>().All.Select(p => p.Id).ToArray());
        Assert.Single(_factory.Services.GetServices<IMetadataProvider>());
        Assert.Contains(_factory.Services.GetServices<IMetadataRecordRemovedHandler>(), h => h is MetadataImageStore);
    }
}
