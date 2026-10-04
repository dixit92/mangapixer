namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata.Collections;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) for "Collection about" (1.34.0): <c>PUT / DELETE admin/metadata/nodes/{id}/collection</c> and
/// <c>POST admin/metadata/review/{id}/accept-collection</c> - admin only (403 for a reader), folders only, audited; the folder's
/// series information and the stop below it through <c>GET nodes/{id}/series-info</c>; the review summary and the Collections tab;
/// and the DI wiring of the service. Synthetic names; the record is stored, so no provider is contacted.
/// </summary>
public sealed class CollectionAboutHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const string LibPubId = "colib1";
    private readonly MangaPixerWebApplicationFactory _factory;

    public CollectionAboutHttpTests(MangaPixerWebApplicationFactory factory) => _factory = factory;

    // Node public ids: coFolder (doujin folder), coArc1 / coArc2 (its archives), coWaiting (a Needs-review doujin folder),
    // coWaitArc1-3 (its archives).
    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        if (await db.Libraries.AnyAsync(l => l.PublicId == LibPubId))
            return;

        var lib = new LibraryEntity { PublicId = LibPubId, DisplayName = "Collections Http", RootPath = "/synthetic/co1", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        var folder = Node("coFolder", lib.Id, null, 0, "Starlight Academy");
        var waiting = Node("coWaiting", lib.Id, null, 0, "Moonlit Academy");
        db.CatalogNodes.AddRange(folder, waiting);
        await db.SaveChangesAsync();
        db.CatalogNodes.AddRange(
            Node("coArc1", lib.Id, folder.Id, 1, "[Circle One] Summer Lesson.cbz"),
            Node("coArc2", lib.Id, folder.Id, 1, "[Circle Two] Rainy Day.cbz"),
            Node("coWaitArc1", lib.Id, waiting.Id, 1, "[Circle One (Artist A)] Night Walk.cbz"),
            Node("coWaitArc2", lib.Id, waiting.Id, 1, "[Circle Two] Morning Run.cbz"),
            Node("coWaitArc3", lib.Id, waiting.Id, 1, "Circle Three] Long Way.cbz"));
        db.MetadataRecords.AddRange(
            new MetadataRecordEntity
            {
                PublicId = "corec1", Provider = "mangaupdates", ExternalId = "717171", Title = "Starlight Academy",
                Description = "Series description.", OriginVolumes = 9, FetchedAt = DateTimeOffset.UtcNow,
            },
            new MetadataRecordEntity
            {
                PublicId = "corec2", Provider = "mangaupdates", ExternalId = "727272", Title = "Moonlit Academy", FetchedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();
        db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
        {
            NodeId = waiting.Id, LibraryId = lib.Id, State = (int)SeriesLinkState.NeedsReview, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.MetadataMatchCandidates.Add(new MetadataMatchCandidateEntity
        {
            NodeId = waiting.Id, Rank = 1, Provider = "mangaupdates", ExternalId = "727272", Title = "Moonlit Academy",
            Format = (int)MetadataFormat.Comic, TitleScore = 1, AdjustedScore = 1,
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

    private async Task<HttpClient> ReaderAsync()
    {
        const string username = "coreader";
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
                (await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/activate",
                    new ActivateAccountRequest { Token = token, Password = "ReaderPassword123!" })).EnsureSuccessStatusCode();
            }
        }
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = username, Password = "ReaderPassword123!" })).EnsureSuccessStatusCode();
        var csrf = await (await client.GetAsync("/api/v1/auth/csrf")).Content.ReadFromJsonAsync<CsrfTokenDto>();
        client.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);
        return client;
    }

    private static SetCollectionAboutRequest Request() => new() { Provider = "mangaupdates", ExternalId = "717171" };

    private async Task<List<string>> AuditActionsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().AuditEvents.Select(a => a.Action).ToListAsync();
    }

    [Fact]
    public void Service_IsRegistered()
    {
        using var scope = _factory.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CollectionAboutService>());
    }

    [Fact]
    public async Task Reader_IsForbidden()
    {
        var reader = await ReaderAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync("/api/v1/admin/metadata/nodes/coFolder/collection", Request())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync("/api/v1/admin/metadata/nodes/coFolder/collection")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/admin/metadata/review/coWaiting/accept-collection",
            new MetadataReviewAcceptCollectionRequest { Rank = 1 })).StatusCode);
    }

    [Fact]
    public async Task Admin_SetsShowsAndClearsACollection()
    {
        var admin = await AdminAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/nope/collection", Request())).StatusCode);
        var onArchive = await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/coArc1/collection", Request());
        Assert.Equal(HttpStatusCode.BadRequest, onArchive.StatusCode);
        Assert.Equal("not_a_folder", (await onArchive.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);

        var set = await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/coFolder/collection", Request());
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var result = (await set.Content.ReadFromJsonAsync<CollectionAboutResultDto>(TestJson.Web))!;
        Assert.Equal(SeriesLinkState.CollectionAbout, result.Change.Link!.State);
        Assert.True(result.ContentSet);
        Assert.Equal(0, result.Queued); // automatic matching is off
        Assert.Contains(AuditActions.MetadataCollection, await AuditActionsAsync());

        var info = (await (await admin.GetAsync("/api/v1/nodes/coFolder/series-info")).Content.ReadFromJsonAsync<SeriesInfoDto>(TestJson.Web))!;
        Assert.Equal((SeriesInfoState.CollectionAbout, "Starlight Academy"), (info.State, info.Title));
        Assert.Null(info.OriginVolumes);
        var below = (await (await admin.GetAsync("/api/v1/nodes/coArc1/series-info")).Content.ReadFromJsonAsync<SeriesInfoDto>(TestJson.Web))!;
        Assert.Equal(SeriesInfoState.None, below.State);

        var summary = (await admin.GetFromJsonAsync<MetadataReviewSummaryDto>($"/api/v1/admin/metadata/review/summary?library={LibPubId}", TestJson.Web))!;
        Assert.Equal(1, summary.Collections);
        var tab = (await admin.GetFromJsonAsync<MetadataReviewPageDto>($"/api/v1/admin/metadata/review?tab=Collections&library={LibPubId}", TestJson.Web))!;
        Assert.Equal("coFolder", tab.Items.Single().NodeId);

        var clear = await admin.DeleteAsync("/api/v1/admin/metadata/nodes/coFolder/collection");
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Contains(AuditActions.MetadataCollectionClear, await AuditActionsAsync());
        var after = (await (await admin.GetAsync("/api/v1/nodes/coFolder/series-info")).Content.ReadFromJsonAsync<SeriesInfoDto>(TestJson.Web))!;
        Assert.NotEqual(SeriesInfoState.CollectionAbout, after.State);
    }

    [Fact]
    public async Task Review_SuggestsAndAcceptsACollection()
    {
        var admin = await AdminAsync();
        var page = (await admin.GetFromJsonAsync<MetadataReviewPageDto>($"/api/v1/admin/metadata/review?tab=NeedsReview&library={LibPubId}", TestJson.Web))!;
        var row = page.Items.Single(i => i.NodeId == "coWaiting");
        Assert.Equal("Moonlit Academy", row.Collection!.Title);

        var bad = await admin.PostAsJsonAsync("/api/v1/admin/metadata/review/coWaiting/accept-collection", new MetadataReviewAcceptCollectionRequest { Rank = 9 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var accept = await admin.PostAsJsonAsync("/api/v1/admin/metadata/review/coWaiting/accept-collection",
            new MetadataReviewAcceptCollectionRequest { Rank = row.Collection.Rank });
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        var result = (await accept.Content.ReadFromJsonAsync<CollectionAboutResultDto>(TestJson.Web))!;
        Assert.Equal(SeriesLinkState.CollectionAbout, result.Change.Link!.State);
        Assert.Equal(SeriesLinkState.NeedsReview, result.Change.Previous!.State);
    }
}
