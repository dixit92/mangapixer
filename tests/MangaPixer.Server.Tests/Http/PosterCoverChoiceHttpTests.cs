namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 1.39.0 through the public surface: a series folder whose linked record has a STORED poster offers it in the picker (admin only), a
/// <c>Poster</c> choice puts it on the folder's card (layered, versioned, served from the stored file with private caching), a
/// replaced poster changes the card URL, and unlinking the series puts the automatic cover back. No provider request is ever sent
/// (the host fails on any outgoing request). Synthetic rows and bytes only.
/// </summary>
public sealed class PosterCoverChoiceHttpTests
{
    private const string LibPub = "pclib1";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record Seed(CatalogNodeEntity Folder, CatalogNodeEntity Volume, MetadataRecordEntity Record);

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

    private static byte[] PosterBytes(string text) =>
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(Encoding.ASCII.GetBytes(text)).ToArray();

    /// <summary>A library with one linked (Auto) series folder of two ready volumes and, when asked, a stored poster for its record.</summary>
    private static async Task<Seed> SeedAsync(MetadataNetworkWebApplicationFactory factory, bool poster = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var thumbnails = scope.ServiceProvider.GetRequiredService<ThumbnailStore>();
        var images = scope.ServiceProvider.GetRequiredService<MetadataImageStore>();

        var lib = new LibraryEntity { PublicId = LibPub, DisplayName = "Poster Lib", RootPath = "/synthetic/posters", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        var folder = Node("pcSeries", lib.Id, null, 0, "Series");
        db.CatalogNodes.Add(folder);
        await db.SaveChangesAsync();
        var v1 = Node("pcVol1", lib.Id, folder.Id, 1, "Series v01");
        var v2 = Node("pcVol2", lib.Id, folder.Id, 1, "Series v02");
        db.CatalogNodes.AddRange(v1, v2);
        await db.SaveChangesAsync();
        foreach (var node in new[] { v1, v2 })
        {
            db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = node.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 1, ThumbnailState = 1, ThumbnailContentVersion = 1 });
            db.PageEntries.Add(new PageEntryEntity { ItemId = node.Id, ContentVersion = 1, Ordinal = 0, EntryKey = "p0", SourceEntryLocator = "p1.png", MediaType = "image/png", Width = 700, Height = 1000 });
            var temp = Path.Combine(factory.DataRoot, Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllTextAsync(temp, "file-" + node.PublicId);
            await thumbnails.PublishAsync(node.Id, 1, temp);
            File.Delete(temp);
        }

        var record = new MetadataRecordEntity { PublicId = "prec1", Provider = "mangaupdates", ExternalId = "9101", Title = "Series (record)", FetchedAt = DateTimeOffset.UtcNow };
        db.MetadataRecords.Add(record);
        await db.SaveChangesAsync();
        db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity { NodeId = folder.Id, LibraryId = lib.Id, State = (int)SeriesLinkState.Auto, RecordId = record.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        if (poster)
            await StorePosterAsync(db, images, record, "poster-one");
        return new Seed(folder, v1, record);
    }

    private static async Task StorePosterAsync(MangaPixerDbContext db, MetadataImageStore images, MetadataRecordEntity record, string text)
    {
        var tracked = await db.MetadataRecords.SingleAsync(r => r.Id == record.Id);
        tracked.ImageState = 1;
        tracked.ImageVersion++;
        await db.SaveChangesAsync();
        await images.PublishAsync(tracked.Id, tracked.ImageVersion, PosterBytes(text));
    }

    private static async Task<PageResponse<CatalogNodeDto>> BrowseAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<PageResponse<CatalogNodeDto>>($"/api/v1/libraries/{LibPub}/browse?sort=name", JsonOptions))!;

    private static async Task<CatalogNodeDto> CardAsync(HttpClient client, Seed seed) =>
        (await BrowseAsync(client)).Items.Single(i => i.Id == seed.Folder.PublicId);

    private static string TextOf(byte[] bytes) => Encoding.ASCII.GetString(bytes, 8, bytes.Length - 8);

    [Fact]
    public async Task APosterChoice_PutsTheStoredPosterOnTheCard_AdminOnly_WithVersionedPrivateCaching_AndAutomaticBringsTheOldCoverBack()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        var seed = await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var member = await factory.CreateReaderClientAsync("member", grantLibrary: LibPub);
        var automaticCard = await CardAsync(admin, seed);

        // The picker offers the poster; the preview is served from the stored file and is admin only.
        var options = (await admin.GetFromJsonAsync<CoverOptionsDto>($"/api/v1/nodes/{seed.Folder.PublicId}/cover-options", JsonOptions))!;
        Assert.NotNull(options.Poster);
        Assert.Equal(CoverMode.Automatic, options.Current.Mode);
        var preview = await admin.GetAsync(options.Poster.ImageUrl);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("image/png", preview.Content.Headers.ContentType!.MediaType);
        Assert.Equal("poster-one", TextOf(await preview.Content.ReadAsByteArrayAsync()));
        Assert.True(preview.Headers.CacheControl!.Private);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(options.Poster.ImageUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/v1/nodes/{seed.Folder.PublicId}/cover-options")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"/api/v1/nodes/{seed.Folder.PublicId}/cover-choice",
            new CoverChoiceRequest { Mode = CoverMode.Poster }, JsonOptions)).StatusCode);
        // An archive is not offered the poster, and the preview 404s there.
        Assert.Null((await admin.GetFromJsonAsync<CoverOptionsDto>($"/api/v1/nodes/{seed.Volume.PublicId}/cover-options", JsonOptions))!.Poster);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/nodes/{seed.Volume.PublicId}/cover-poster")).StatusCode);

        var put = await admin.PutAsJsonAsync($"/api/v1/nodes/{seed.Folder.PublicId}/cover-choice", new CoverChoiceRequest { Mode = CoverMode.Poster }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var state = (await put.Content.ReadFromJsonAsync<CoverStateDto>(JsonOptions))!;
        Assert.Equal(CoverMode.Poster, state.Mode);

        // The card shows it (layered, "chosen"), the endpoint serves the stored poster, and a reader of the library sees the same.
        var card = await CardAsync(admin, seed);
        Assert.Equal(CardCoverSource.Chosen, card.CoverSource);
        Assert.Equal(state.ImageUrl, card.CoverUrl);
        Assert.NotEqual(automaticCard.CoverUrl, card.CoverUrl);
        Assert.StartsWith($"/api/v1/nodes/{seed.Folder.PublicId}/cover?v=", card.CoverUrl, StringComparison.Ordinal);
        var served = await admin.GetAsync(card.CoverUrl);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("poster-one", TextOf(await served.Content.ReadAsByteArrayAsync()));
        Assert.True(served.Headers.CacheControl!.Private);
        Assert.Contains("immutable", served.Headers.CacheControl.ToString());
        Assert.Equal("poster-one", TextOf(await (await member.GetAsync(card.CoverUrl)).Content.ReadAsByteArrayAsync()));
        Assert.Equal(CoverMode.Poster, (await admin.GetFromJsonAsync<CoverOptionsDto>($"/api/v1/nodes/{seed.Folder.PublicId}/cover-options", JsonOptions))!.Current.Mode);

        // Back to Automatic.
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/v1/nodes/{seed.Folder.PublicId}/cover-choice")).StatusCode);
        Assert.Equal(automaticCard.CoverUrl, (await CardAsync(admin, seed)).CoverUrl);
        Assert.Equal(0, factory.Handler.CallCount);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        Assert.True(await db.AuditEvents.CountAsync(e => e.Action == "cover.choice.change") >= 2);
    }

    [Fact]
    public async Task AReplacedPoster_ChangesTheCardUrl_AndAnUnlinkFallsBackToAutomatic()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        var seed = await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var automaticUrl = (await CardAsync(admin, seed)).CoverUrl;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/nodes/{seed.Folder.PublicId}/cover-choice",
            new CoverChoiceRequest { Mode = CoverMode.Poster }, JsonOptions)).StatusCode);
        var first = (await CardAsync(admin, seed)).CoverUrl;

        // The record gets a new poster (a new image version): another card URL, the new bytes; the old URL still answers the current cover.
        using (var scope = factory.Services.CreateScope())
            await StorePosterAsync(scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>(), scope.ServiceProvider.GetRequiredService<MetadataImageStore>(),
                seed.Record, "poster-two");
        var second = (await CardAsync(admin, seed)).CoverUrl;
        Assert.NotEqual(first, second);
        Assert.Equal("poster-two", TextOf(await admin.GetByteArrayAsync(second)));
        Assert.Equal("poster-two", TextOf(await admin.GetByteArrayAsync(first)));

        // The series is unlinked: the card is automatic again, the picker stops offering the poster and the server refuses it.
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/v1/admin/metadata/nodes/{seed.Folder.PublicId}/link")).StatusCode);
        var card = await CardAsync(admin, seed);
        Assert.NotEqual(CardCoverSource.Chosen, card.CoverSource);
        Assert.Equal(automaticUrl, card.CoverUrl);
        var options = (await admin.GetFromJsonAsync<CoverOptionsDto>($"/api/v1/nodes/{seed.Folder.PublicId}/cover-options", JsonOptions))!;
        Assert.Null(options.Poster);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/nodes/{seed.Folder.PublicId}/cover-poster")).StatusCode);
        var refused = await admin.PutAsJsonAsync($"/api/v1/nodes/{seed.Folder.PublicId}/cover-choice", new CoverChoiceRequest { Mode = CoverMode.Poster }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(0, factory.Handler.CallCount);
    }

    [Fact]
    public async Task ALinkedSeriesWithoutAStoredPoster_OffersNone_AndTheChoiceIsRefusedWithoutAnyRequest()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        var seed = await SeedAsync(factory, poster: false);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        Assert.Null((await admin.GetFromJsonAsync<CoverOptionsDto>($"/api/v1/nodes/{seed.Folder.PublicId}/cover-options", JsonOptions))!.Poster);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/nodes/{seed.Folder.PublicId}/cover-poster")).StatusCode);
        var put = await admin.PutAsJsonAsync($"/api/v1/nodes/{seed.Folder.PublicId}/cover-choice", new CoverChoiceRequest { Mode = CoverMode.Poster }, JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Equal("cover_not_stored", (await put.Content.ReadFromJsonAsync<ApiError>(JsonOptions))!.Error);
        Assert.Equal(0, factory.Handler.CallCount);
    }
}
