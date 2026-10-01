namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Features.Reading;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// The 1.29.0 cover layer through the public surface: browse sends the layered, versioned card URL and its source; the
/// node cover endpoint serves the CURRENT resolution (current token: cached a year, <c>private</c>; stale: revalidate + 304);
/// the picker endpoints are admin-only and a choice changes the card; the library's "Show saved web covers" is read at
/// request time; stored web covers are admin-only by id and can be deleted; Continue reading carries the server's cover
/// URL. Synthetic rows and files only (no worker, zero network).
/// </summary>
public sealed class CoverLayerHttpTests
{
    private const string LibPub = "cvlib1";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record Seed(long LibraryId, CatalogNodeEntity Folder, CatalogNodeEntity V1, CatalogNodeEntity V2);

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

    private static async Task PublishAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    /// <summary>A library with a series folder and two ready volumes; v01's page 1 is a spread with an automatic crop.</summary>
    private static async Task<Seed> SeedAsync(MetadataNetworkWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var thumbnails = scope.ServiceProvider.GetRequiredService<ThumbnailStore>();
        var files = scope.ServiceProvider.GetRequiredService<CoverFiles>();

        var lib = new LibraryEntity { PublicId = LibPub, DisplayName = "Cover Lib", RootPath = "/synthetic/covers", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        var folder = Node("cvSeries", lib.Id, null, 0, "Series");
        db.CatalogNodes.Add(folder);
        await db.SaveChangesAsync();
        var v1 = Node("cvVol1", lib.Id, folder.Id, 1, "Series v01");
        var v2 = Node("cvVol2", lib.Id, folder.Id, 1, "Series v02");
        db.CatalogNodes.AddRange(v1, v2);
        await db.SaveChangesAsync();
        foreach (var (node, width) in new[] { (v1, 1400), (v2, 700) })
        {
            db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = node.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 1, ThumbnailState = 1, ThumbnailContentVersion = 1 });
            db.PageEntries.Add(new PageEntryEntity { ItemId = node.Id, ContentVersion = 1, Ordinal = 0, EntryKey = "p0", SourceEntryLocator = "p1.png", MediaType = "image/png", Width = width, Height = 1000 });
            var temp = Path.Combine(factory.DataRoot, Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllTextAsync(temp, "file-" + node.PublicId);
            await thumbnails.PublishAsync(node.Id, 1, temp);
            File.Delete(temp);
        }
        db.NodeAutoCovers.Add(new NodeAutoCoverEntity
        {
            NodeId = v1.Id,
            Source = (int)AutoCoverSource.Crop,
            CropSide = (int)CoverCropSide.Right,
            Reason = (int)AutoCoverReason.Spread,
            InputsKey = "seeded",
            Version = 1,
            DecidedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        await PublishAsync(files.CropPath(v1.Id, 1, CoverCropSide.Right), "crop-right-" + v1.PublicId);
        return new Seed(lib.Id, folder, v1, v2);
    }

    private static async Task<PageResponse<CatalogNodeDto>> BrowseAsync(HttpClient client, string? parentId = null) =>
        (await client.GetFromJsonAsync<PageResponse<CatalogNodeDto>>(
            $"/api/v1/libraries/{LibPub}/browse?sort=name" + (parentId is null ? "" : "&parentId=" + parentId), JsonOptions))!;

    [Fact]
    public async Task Browse_SendsTheLayeredUrl_AndTheNodeCoverServesIt_WithVersionedPrivateCaching()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        var seed = await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        var series = (await BrowseAsync(admin)).Items.Single(i => i.Id == seed.Folder.PublicId);
        Assert.StartsWith($"/api/v1/nodes/{seed.V1.PublicId}/cover?v=", series.CoverUrl, StringComparison.Ordinal);
        Assert.Equal(CardCoverSource.Crop, series.CoverSource);
        var inside = await BrowseAsync(admin, seed.Folder.PublicId);
        Assert.Equal(series.CoverUrl, inside.Items.Single(i => i.Id == seed.V1.PublicId).CoverUrl);
        var v2 = inside.Items.Single(i => i.Id == seed.V2.PublicId);
        Assert.Equal($"/api/v1/items/{seed.V2.PublicId}/cover?v=1", v2.CoverUrl);
        Assert.Equal(CardCoverSource.File, v2.CoverSource);

        var current = await admin.GetAsync(series.CoverUrl);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal("crop-right-" + seed.V1.PublicId, await current.Content.ReadAsStringAsync());
        Assert.True(current.Headers.CacheControl!.Private);
        Assert.Contains("immutable", current.Headers.CacheControl.ToString());

        var stale = await admin.GetAsync($"/api/v1/nodes/{seed.V1.PublicId}/cover?v=old");
        Assert.Equal("crop-right-" + seed.V1.PublicId, await stale.Content.ReadAsStringAsync());
        Assert.True(stale.Headers.CacheControl!.NoCache);
        Assert.True(stale.Headers.CacheControl.Private);
        using var revalidate = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/nodes/{seed.V1.PublicId}/cover?v=old");
        revalidate.Headers.IfNoneMatch.Add(stale.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await admin.SendAsync(revalidate)).StatusCode);

        // The folder's own node URL serves the same resolution.
        Assert.Equal("crop-right-" + seed.V1.PublicId, await admin.GetStringAsync($"/api/v1/nodes/{seed.Folder.PublicId}/cover"));
    }

    [Fact]
    public async Task NodeCover_FollowsLibraryAccess()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        var seed = await SeedAsync(factory);
        var stranger = await factory.CreateReaderClientAsync("stranger", grantLibrary: null);
        var member = await factory.CreateReaderClientAsync("member", grantLibrary: LibPub);

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/nodes/{seed.V1.PublicId}/cover")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/v1/nodes/{seed.V1.PublicId}/cover")).StatusCode);
    }

    [Fact]
    public async Task Picker_IsAdminOnly_AndAChoiceChangesTheCard_AndDeleteGoesBackToAutomatic()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        var seed = await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var member = await factory.CreateReaderClientAsync("member", grantLibrary: LibPub);

        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/v1/nodes/{seed.Folder.PublicId}/cover-options")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"/api/v1/nodes/{seed.Folder.PublicId}/cover-choice",
            new CoverChoiceRequest { Mode = CoverMode.FilePinned }, JsonOptions)).StatusCode);

        var options = (await admin.GetFromJsonAsync<CoverOptionsDto>($"/api/v1/nodes/{seed.Folder.PublicId}/cover-options", JsonOptions))!;
        Assert.Equal(CoverMode.Automatic, options.Current.Mode);
        Assert.Equal(CardCoverSource.Crop, options.Current.AutoSource);
        Assert.Contains(options.Local, o => o.Kind == CoverOptionKind.File && o.ArchiveId == seed.V1.PublicId);
        Assert.Contains(options.Local, o => o.Kind == CoverOptionKind.CropLeft);
        Assert.Contains(options.Local, o => o.Kind == CoverOptionKind.CropRight);
        Assert.Contains(options.Local, o => o.Kind == CoverOptionKind.Archive && o.ArchiveId == seed.V2.PublicId);
        Assert.False(options.WebAvailable);
        Assert.Equal("not_linked", options.WebUnavailableReason);
        var automaticUrl = (await BrowseAsync(admin)).Items.Single(i => i.Id == seed.Folder.PublicId).CoverUrl;

        var put = await admin.PutAsJsonAsync($"/api/v1/nodes/{seed.Folder.PublicId}/cover-choice",
            new CoverChoiceRequest { Mode = CoverMode.Archive, ArchiveId = seed.V2.PublicId }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(CoverMode.Archive, (await put.Content.ReadFromJsonAsync<CoverStateDto>(JsonOptions))!.Mode);

        var card = (await BrowseAsync(admin)).Items.Single(i => i.Id == seed.Folder.PublicId);
        Assert.NotEqual(automaticUrl, card.CoverUrl);
        Assert.Equal(CardCoverSource.Chosen, card.CoverSource);
        Assert.Equal("file-" + seed.V2.PublicId, await admin.GetStringAsync(card.CoverUrl));

        // Pin: the file cover (v01's page 1, not its crop).
        await admin.PutAsJsonAsync($"/api/v1/nodes/{seed.Folder.PublicId}/cover-choice", new CoverChoiceRequest { Mode = CoverMode.FilePinned }, JsonOptions);
        card = (await BrowseAsync(admin)).Items.Single(i => i.Id == seed.Folder.PublicId);
        Assert.Equal($"/api/v1/items/{seed.V1.PublicId}/cover?v=1", card.CoverUrl);

        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/v1/nodes/{seed.Folder.PublicId}/cover-choice")).StatusCode);
        Assert.Equal(automaticUrl, (await BrowseAsync(admin)).Items.Single(i => i.Id == seed.Folder.PublicId).CoverUrl);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        Assert.True(await db.AuditEvents.CountAsync(e => e.Action == "cover.choice.change") >= 3);
    }

    [Fact]
    public async Task CoverChoice_Validation()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        var seed = await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        var foreign = await admin.PutAsJsonAsync($"/api/v1/nodes/{seed.Folder.PublicId}/cover-choice",
            new CoverChoiceRequest { Mode = CoverMode.Archive, ArchiveId = seed.Folder.PublicId }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        var noSide = await admin.PutAsJsonAsync($"/api/v1/nodes/{seed.V1.PublicId}/cover-choice",
            new CoverChoiceRequest { Mode = CoverMode.Crop }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, noSide.StatusCode);
        var notLinked = await admin.PutAsJsonAsync($"/api/v1/nodes/{seed.V1.PublicId}/cover-choice",
            new CoverChoiceRequest { Mode = CoverMode.VolumeCover, VolumeCoverId = "vc000001" }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, notLinked.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/nodes/nope/cover-options")).StatusCode);
    }

    [Fact]
    public async Task SavedWebCovers_ShowByTheLibrarySwitch_AdminOnlyById_AndDeleteFallsBackToTheFile()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        var seed = await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var member = await factory.CreateReaderClientAsync("member", grantLibrary: LibPub);

        string coverPublicId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var files = scope.ServiceProvider.GetRequiredService<CoverFiles>();
            var record = new MetadataRecordEntity { PublicId = "rcv1", Provider = "mangaupdates", ExternalId = "901", Title = "Series", FetchedAt = DateTimeOffset.UtcNow };
            var companion = new MetadataRecordEntity { PublicId = "mdcv1", Provider = "mangadex", ExternalId = "md-901", Title = "Series", FetchedAt = DateTimeOffset.UtcNow };
            db.MetadataRecords.AddRange(record, companion);
            await db.SaveChangesAsync();
            db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity { NodeId = seed.Folder.Id, LibraryId = seed.LibraryId, State = 0, RecordId = record.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            db.MetadataCompanions.Add(new MetadataCompanionEntity { RecordId = record.Id, Provider = "mangadex", CompanionRecordId = companion.Id, State = (int)CompanionState.Auto });
            var cover = new VolumeCoverEntity
            {
                PublicId = "vcab0001",
                ProviderRecordId = companion.Id,
                Kind = (int)VolumeCoverKind.Volume,
                Volume = 2,
                Locale = "en",
                RemoteId = "r1",
                RemoteFile = "f1.jpg",
                State = (int)VolumeCoverState.Stored,
                StoredVersion = 1,
                Hash = 5,
                ListedAt = DateTimeOffset.UtcNow,
            };
            db.VolumeCovers.Add(cover);
            await db.SaveChangesAsync();
            db.NodeAutoCovers.Add(new NodeAutoCoverEntity
            {
                NodeId = seed.V2.Id,
                Source = (int)AutoCoverSource.WebVolume,
                VolumeCoverId = cover.Id,
                Reason = (int)AutoCoverReason.LocalNotCover,
                InputsKey = "seeded",
                Version = 1,
                DecidedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            await PublishAsync(files.VolumeCoverPath(cover.PublicId, 1), "web-volume-2");
            coverPublicId = cover.PublicId;
        }

        var card = (await BrowseAsync(member, seed.Folder.PublicId)).Items.Single(i => i.Id == seed.V2.PublicId);
        Assert.Equal(CardCoverSource.WebVolume, card.CoverSource);
        Assert.Equal("web-volume-2", await member.GetStringAsync(card.CoverUrl));

        // The picker lists it for the admin, with its admin-only image URL.
        var options = (await admin.GetFromJsonAsync<CoverOptionsDto>($"/api/v1/nodes/{seed.V2.PublicId}/cover-options", JsonOptions))!;
        Assert.True(options.WebAvailable);
        var listed = options.Web.Single(g => g.Volume == 2).Covers.Single();
        Assert.True(listed.Stored);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(listed.ImageUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(listed.ImageUrl)).StatusCode);

        // "Show saved web covers" off: read at request time, the file shows.
        var off = await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}", new UpdateMetadataLibraryRequest { ShowWebCovers = false }, JsonOptions);
        Assert.True(off.IsSuccessStatusCode);
        card = (await BrowseAsync(member, seed.Folder.PublicId)).Items.Single(i => i.Id == seed.V2.PublicId);
        Assert.Equal(CardCoverSource.File, card.CoverSource);
        Assert.Equal("file-" + seed.V2.PublicId, await member.GetStringAsync(card.CoverUrl));
        await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}", new UpdateMetadataLibraryRequest { ShowWebCovers = true }, JsonOptions);

        // Delete stored volume covers (the metadata endpoint - one for both lanes since the 1.29.0 merge): admin only; files,
        // rows and the automatic decision that used the cover go; the file shows again.
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync("/api/v1/admin/metadata/volume-covers")).StatusCode);
        var deleted = await admin.DeleteAsync("/api/v1/admin/metadata/volume-covers");
        Assert.True(deleted.IsSuccessStatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            Assert.Equal(0, await db.VolumeCovers.CountAsync());
            Assert.Equal(0, await db.NodeAutoCovers.CountAsync(a => a.VolumeCoverId != null));
        }
        card = (await BrowseAsync(member, seed.Folder.PublicId)).Items.Single(i => i.Id == seed.V2.PublicId);
        Assert.Equal(CardCoverSource.File, card.CoverSource);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/volume-covers/{coverPublicId}/image?v=1")).StatusCode);
    }

    [Fact]
    public async Task ContinueReading_CarriesTheServersCoverUrl()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        var seed = await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var userId = await db.Users.Where(u => u.NormalizedUserName == "ADMIN").Select(u => u.Id).SingleAsync();
            db.ReadingProgress.Add(new ReadingProgressEntity
            {
                UserId = userId,
                ItemId = seed.V1.Id,
                ContentVersion = 1,
                EntryKey = "p0",
                Ordinal = 0,
                State = (int)ReadingState.InProgress,
                LastMutationId = "m1",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var entries = (await admin.GetFromJsonAsync<List<ContinueReadingEntry>>("/api/v1/reading/continue", JsonOptions))!;
        var entry = entries.Single(e => e.ItemId == seed.V1.PublicId);
        Assert.StartsWith($"/api/v1/nodes/{seed.V1.PublicId}/cover?v=", entry.CoverUrl, StringComparison.Ordinal);
        Assert.Equal("crop-right-" + seed.V1.PublicId, await admin.GetStringAsync(entry.CoverUrl));
    }
}
