namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Web volume covers on virtual volume stacks through the public surface (1.29.0): the browse card and the stack view of a
/// chapter-only stack carry the versioned <c>/nodes/{folder}/volumes/{key}/cover</c> URL; a READER of the library gets the
/// image (never the admin-only stored-cover URL), a user without access gets 404; a stale version revalidates; with "Show
/// saved web covers" off the card shows its first chapter again and the old URL redirects there. Synthetic rows and files.
/// </summary>
public sealed class StackCoverHttpTests
{
    private const string LibPub = "scLib1";
    private const string SeriesPub = "scSeries";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    // SC Series (linked, a MangaDex companion): volume 1 = chapters 1-2, volume 2 = chapters 3-4; a stored web cover of volume 1 only.
    private static async Task SeedAsync(MetadataNetworkWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var thumbnails = scope.ServiceProvider.GetRequiredService<ThumbnailStore>();
        var files = scope.ServiceProvider.GetRequiredService<CoverFiles>();

        var lib = await VolumeTestData.AddLibraryAsync(db, LibPub);
        var series = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "SC Series", SeriesPub);
        foreach (var n in new[] { 1, 2, 3, 4 })
        {
            var chapter = await VolumeTestData.AddArchiveAsync(db, lib.Id, series.Id, $"SC Series - Chapter {n:000}", $"scc{n}");
            var temp = Path.Combine(factory.DataRoot, Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllTextAsync(temp, "file-" + chapter.PublicId);
            await thumbnails.PublishAsync(chapter.Id, 1, temp);
            File.Delete(temp);
        }
        var record = await VolumeTestData.AddRecordAsync(db, status: MetadataOriginStatus.Ongoing);
        await VolumeTestData.LinkAsync(db, series, record.Id);
        await VolumeTestData.AddMapAsync(db, record.Id, 1, null, null, VolumeMapState.Ok, VolumeMapSource.MangaDexAggregate, (1, 1, 2), (2, 3, 4));

        var companion = new MetadataRecordEntity { PublicId = "mdsc1", Provider = "mangadex", ExternalId = "md-sc-1", Title = "SC", FetchedAt = DateTimeOffset.UtcNow };
        db.MetadataRecords.Add(companion);
        await db.SaveChangesAsync();
        db.MetadataCompanions.Add(new MetadataCompanionEntity { RecordId = record.Id, Provider = "mangadex", CompanionRecordId = companion.Id, State = (int)CompanionState.Auto });
        var cover = new VolumeCoverEntity
        {
            PublicId = "vc000000000000a001",
            ProviderRecordId = companion.Id,
            Kind = (int)VolumeCoverKind.Volume,
            Volume = 1,
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
        var path = files.VolumeCoverPath(cover.PublicId, 1);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "web-volume-1");
    }

    private static async Task<List<CatalogNodeDto>> StacksAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<PageResponse<CatalogNodeDto>>($"/api/v1/libraries/{LibPub}/browse?parentId={SeriesPub}", JsonOptions))!
            .Items.Where(i => i.Kind == CatalogNodeKind.VolumeStack).ToList();

    [Fact]
    public async Task AChapterOnlyStack_ShowsItsVolumesWebCover_ToReadersOfTheLibrary()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        await SeedAsync(factory);
        var reader = await factory.CreateReaderClientAsync("member", grantLibrary: LibPub);
        var stranger = await factory.CreateReaderClientAsync("stranger", grantLibrary: null);

        var stacks = await StacksAsync(reader);
        var vol1 = stacks.Single(s => s.VolumeStack!.Key == "1");
        var vol2 = stacks.Single(s => s.VolumeStack!.Key == "2");
        Assert.StartsWith($"/api/v1/nodes/{SeriesPub}/volumes/1/cover?v=", vol1.CoverUrl, StringComparison.Ordinal);
        Assert.Equal(CardCoverSource.WebVolume, vol1.CoverSource);
        Assert.Equal("/api/v1/items/scc3/cover?v=1", vol2.CoverUrl); // no stored cover of volume 2: its first chapter

        var image = await reader.GetAsync(vol1.CoverUrl);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("web-volume-1", await image.Content.ReadAsStringAsync());
        Assert.True(image.Headers.CacheControl!.Private);
        Assert.Contains("immutable", image.Headers.CacheControl.ToString());

        var stale = await reader.GetAsync($"/api/v1/nodes/{SeriesPub}/volumes/1/cover?v=old");
        Assert.Equal("web-volume-1", await stale.Content.ReadAsStringAsync());
        Assert.True(stale.Headers.CacheControl!.NoCache);
        using var revalidate = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/nodes/{SeriesPub}/volumes/1/cover?v=old");
        revalidate.Headers.IfNoneMatch.Add(stale.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await reader.SendAsync(revalidate)).StatusCode);

        // The stack view header carries the same cover.
        var view = await reader.GetFromJsonAsync<VolumeStackDto>($"/api/v1/nodes/{SeriesPub}/volumes/1", JsonOptions);
        Assert.Equal(vol1.CoverUrl, view!.CoverUrl);

        // No access to the library: 404, like every node cover; an unknown stack key: 404.
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(vol1.CoverUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/v1/nodes/{SeriesPub}/volumes/9/cover")).StatusCode);
        // The stored cover itself stays admin-only.
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/volume-covers/vc000000000000a001/image?v=1")).StatusCode);
    }

    [Fact]
    public async Task WithSavedWebCoversOff_TheStackShowsItsFirstChapter_AndTheOldUrlRedirectsThere()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var webUrl = (await StacksAsync(admin)).Single(s => s.VolumeStack!.Key == "1").CoverUrl!;

        var off = await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}", new UpdateMetadataLibraryRequest { ShowWebCovers = false }, JsonOptions);
        Assert.True(off.IsSuccessStatusCode);

        var vol1 = (await StacksAsync(admin)).Single(s => s.VolumeStack!.Key == "1");
        Assert.Equal("/api/v1/items/scc1/cover?v=1", vol1.CoverUrl);
        Assert.NotEqual(CardCoverSource.WebVolume, vol1.CoverSource);

        // The URL built while the covers were shown now leads (redirect, uncached) to the stack's first chapter.
        var followed = await admin.GetAsync(webUrl);
        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);
        Assert.Equal("file-scc1", await followed.Content.ReadAsStringAsync());
    }
}
