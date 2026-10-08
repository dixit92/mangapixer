namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Artists;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) for "Artist folder" (1.37.0): <c>PUT / DELETE admin/metadata/nodes/{id}/artist-folder</c>,
/// <c>POST admin/metadata/review/{id}/accept-artist</c> and the <c>MarkArtistFolder</c> bulk action - admin only (403 for a reader),
/// folders only, audited; the declared artist through <c>GET folders/{id}/declared</c>; the folder's series information and the stop
/// below it through <c>GET nodes/{id}/series-info</c>; the review summary and the Collections tab; and, with automatic matching on (the
/// production matcher from DI, no request may leave), the works inside queued at once. Synthetic names.
/// </summary>
[Trait("Category", "Http")]
public sealed class ArtistFolderHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const string LibPubId = "arlib1";
    private readonly MangaPixerWebApplicationFactory _factory;

    public ArtistFolderHttpTests(MangaPixerWebApplicationFactory factory) => _factory = factory;

    // Node public ids: arFolder (artist folder, 3 archives arArc1-3), arWaiting (a Needs-review folder, arWaitArc1-2),
    // arOther (a folder for the bulk action), arOtherArc1.
    private static async Task SeedAsync(IServiceProvider services, string libPubId, bool metadataEnabled = false)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        if (await db.Libraries.AnyAsync(l => l.PublicId == libPubId))
            return;

        var lib = new LibraryEntity
        {
            PublicId = libPubId,
            DisplayName = "Artists Http",
            RootPath = "/synthetic/" + libPubId,
            CreatedAt = DateTimeOffset.UtcNow,
            MetadataEnabled = metadataEnabled,
        };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        var folder = Node("arFolder", lib.Id, null, CatalogNodeKind.Folder, "Beta Painter");
        var waiting = Node("arWaiting", lib.Id, null, CatalogNodeKind.Folder, "Gamma Inker");
        var other = Node("arOther", lib.Id, null, CatalogNodeKind.Folder, "Delta Penciller");
        db.CatalogNodes.AddRange(folder, waiting, other);
        await db.SaveChangesAsync();
        db.CatalogNodes.AddRange(
            Node("arArc1", lib.Id, folder.Id, CatalogNodeKind.Archive, "Qzv Harbor Tale.cbz"),
            Node("arArc2", lib.Id, folder.Id, CatalogNodeKind.Archive, "Qzv Lantern Road.cbz"),
            Node("arArc3", lib.Id, folder.Id, CatalogNodeKind.Archive, "Qzv Quiet Orchard.cbz"),
            Node("arWaitArc1", lib.Id, waiting.Id, CatalogNodeKind.Archive, "Qzv Silver Gate.cbz"),
            Node("arWaitArc2", lib.Id, waiting.Id, CatalogNodeKind.Archive, "Qzv Copper Field.cbz"),
            Node("arOtherArc1", lib.Id, other.Id, CatalogNodeKind.Archive, "Qzv Iron Bridge.cbz"));
        var now = DateTimeOffset.UtcNow;
        db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
        {
            NodeId = waiting.Id,
            LibraryId = lib.Id,
            State = (int)SeriesLinkState.NeedsReview,
            MatchMethod = (int)MetadataMatchMethod.Auto,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.MetadataMatchQueue.Add(new MetadataMatchQueueEntity
        {
            NodeId = waiting.Id,
            LibraryId = lib.Id,
            State = QueueState.Done,
            Reason = QueueReason.NewFolder,
            Level = (int)MatchLevel.ReviewOnly,
            WorkClass = (int)WorkClass.Ambiguous,
            Outcome = (int)MatchBand.NeedsReview,
            EnqueuedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private static CatalogNodeEntity Node(string pub, long libId, long? parentId, CatalogNodeKind kind, string name) => new()
    {
        PublicId = pub,
        LibraryId = libId,
        ParentId = parentId,
        Kind = (int)kind,
        DisplayName = name,
        RelativePath = pub,
        PathKey = pub,
        SortKey = (kind == CatalogNodeKind.Folder ? "0" : "1") + name,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<HttpClient> AdminAsync()
    {
        await SeedAsync(_factory.Services, LibPubId);
        return await _factory.LoginAsAdminWithChangedPasswordAsync();
    }

    private async Task<HttpClient> ReaderAsync()
    {
        const string username = "arreader";
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

    private async Task<List<string>> AuditActionsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().AuditEvents.Select(a => a.Action).ToListAsync();
    }

    [Fact]
    public void Service_IsRegistered()
    {
        using var scope = _factory.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ArtistFolderService>());
    }

    [Fact]
    public async Task Reader_IsForbidden()
    {
        var reader = await ReaderAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync("/api/v1/admin/metadata/nodes/arFolder/artist-folder", new SetArtistFolderRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync("/api/v1/admin/metadata/nodes/arFolder/artist-folder")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/admin/metadata/review/arWaiting/accept-artist", new SetArtistFolderRequest())).StatusCode);
    }

    [Fact]
    public async Task Admin_MarksShowsAndRemovesAnArtistFolder()
    {
        var admin = await AdminAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsync("/api/v1/admin/metadata/nodes/nope/artist-folder", null)).StatusCode);
        var onArchive = await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/arArc1/artist-folder", new SetArtistFolderRequest());
        Assert.Equal(HttpStatusCode.BadRequest, onArchive.StatusCode);
        Assert.Equal("not_a_folder", (await onArchive.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        var badRole = await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/arFolder/artist-folder", new SetArtistFolderRequest { Role = "colorist" });
        Assert.Equal("creator_role_invalid", (await badRole.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);

        // No body: the folder's own name, "Story & art".
        var set = await admin.PutAsync("/api/v1/admin/metadata/nodes/arFolder/artist-folder", null);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var result = (await set.Content.ReadFromJsonAsync<ArtistFolderResultDto>(TestJson.Web))!;
        Assert.Equal(SeriesLinkState.ArtistFolder, result.Change.Link!.State);
        Assert.Null(result.Change.Link.RecordId);
        Assert.Equal(("Beta Painter", "author"), (result.Artist.Name, result.Artist.Role));
        Assert.Equal(0, result.Queued); // automatic matching is off
        Assert.Contains(AuditActions.MetadataArtistFolder, await AuditActionsAsync());

        var declared = (await admin.GetFromJsonAsync<DeclaredFactsScopeDto>("/api/v1/admin/metadata/folders/arFolder/declared", TestJson.Web))!;
        Assert.Equal("Beta Painter", declared.Own.Creators.Single().Name);

        var info = (await admin.GetFromJsonAsync<SeriesInfoDto>("/api/v1/nodes/arFolder/series-info", TestJson.Web))!;
        Assert.Equal((SeriesInfoState.ArtistFolder, "Beta Painter"), (info.State, info.Title));
        Assert.Equal(SeriesLinkState.ArtistFolder, info.Link!.State);
        var below = (await admin.GetFromJsonAsync<SeriesInfoDto>("/api/v1/nodes/arArc1/series-info", TestJson.Web))!;
        Assert.Equal(SeriesInfoState.None, below.State);
        Assert.True(below.Link!.Inherited);

        var summary = (await admin.GetFromJsonAsync<MetadataReviewSummaryDto>($"/api/v1/admin/metadata/review/summary?library={LibPubId}", TestJson.Web))!;
        Assert.True(summary.ArtistFolders >= 1);
        var tab = (await admin.GetFromJsonAsync<MetadataReviewPageDto>($"/api/v1/admin/metadata/review?tab=Collections&library={LibPubId}", TestJson.Web))!;
        Assert.Contains(tab.Items, i => i.NodeId == "arFolder" && i.Link!.State == SeriesLinkState.ArtistFolder && i.Artist!.Name == "Beta Painter");

        var clear = await admin.DeleteAsync("/api/v1/admin/metadata/nodes/arFolder/artist-folder");
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Contains(AuditActions.MetadataArtistFolderClear, await AuditActionsAsync());
        var after = (await admin.GetFromJsonAsync<SeriesInfoDto>("/api/v1/nodes/arFolder/series-info", TestJson.Web))!;
        Assert.NotEqual(SeriesInfoState.ArtistFolder, after.State);
        var kept = (await admin.GetFromJsonAsync<DeclaredFactsScopeDto>("/api/v1/admin/metadata/folders/arFolder/declared", TestJson.Web))!;
        Assert.Equal("Beta Painter", kept.Own.Creators.Single().Name); // the declared artist stays
    }

    [Fact]
    public async Task Review_BulkMarkArtistFolder_WorksThroughTheBulkRoute()
    {
        var admin = await AdminAsync();
        var response = await admin.PostAsJsonAsync("/api/v1/admin/metadata/review/bulk", new MetadataReviewBulkRequest
        {
            Action = MetadataReviewBulkAction.MarkArtistFolder,
            NodeIds = ["arOther", "arOtherArc1"],
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<MetadataReviewBulkResultDto>(TestJson.Web))!;
        Assert.Equal("ok", result.Results.Single(r => r.NodeId == "arOther").Code);
        Assert.Equal("not_a_folder", result.Results.Single(r => r.NodeId == "arOtherArc1").Code);
        var info = (await admin.GetFromJsonAsync<SeriesInfoDto>("/api/v1/nodes/arOther/series-info", TestJson.Web))!;
        Assert.Equal(SeriesInfoState.ArtistFolder, info.State);
    }

    [Fact]
    public async Task Review_AcceptArtist_WithAutomaticMatchingOn_ClearsTheRow_AndQueuesTheWorksAtOnce_WithoutARequest()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        await SeedAsync(factory.Services, "arlib2", metadataEnabled: true);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings", new UpdateMetadataSettingsRequest
        {
            FetchEnabled = true,
            AcceptedConsentVersion = MetadataConsent.CurrentVersion,
            AutoMatchEnabled = true,
            AcceptedAutoConsentVersion = MetadataAutoConsent.CurrentVersion,
        })).EnsureSuccessStatusCode();

        var accept = await admin.PostAsJsonAsync("/api/v1/admin/metadata/review/arWaiting/accept-artist", new SetArtistFolderRequest { Name = "Gamma Inker", Role = "artist" });

        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        var result = (await accept.Content.ReadFromJsonAsync<ArtistFolderResultDto>(TestJson.Web))!;
        Assert.Equal(SeriesLinkState.NeedsReview, result.Change.Previous!.State);
        Assert.Equal(SeriesLinkState.ArtistFolder, result.Change.Link!.State);
        Assert.Equal(("Gamma Inker", "artist"), (result.Artist.Name, result.Artist.Role));
        Assert.Equal(2, result.Queued);

        var waiting = (await admin.GetFromJsonAsync<MetadataReviewPageDto>("/api/v1/admin/metadata/review?tab=NeedsReview&library=arlib2", TestJson.Web))!;
        Assert.DoesNotContain(waiting.Items, i => i.NodeId == "arWaiting");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var pending = await db.MetadataMatchQueue.AsNoTracking()
                .Where(q => q.State == QueueState.Pending)
                .Join(db.CatalogNodes, q => q.NodeId, n => n.Id, (q, n) => n.PublicId)
                .OrderBy(p => p).ToListAsync();
            Assert.Equal(["arWaitArc1", "arWaitArc2"], pending);
        }
        Assert.Equal(0, factory.Handler.CallCount); // marking sends nothing; the queued works wait for the worker
    }
}
