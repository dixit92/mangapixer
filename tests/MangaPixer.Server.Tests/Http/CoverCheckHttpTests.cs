namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Catalog;
using com.lifepixer.mangapixer.Tests.Server.Features.Covers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) of the cover check after linking (1.31.0) through the REAL DI graph: the automatic-matching
/// hosted service's volume-cover tick runs the check (also while the pass itself waits); an Auto link whose volume covers are
/// clearly different pictures shows in Needs review with the <c>cover_differs</c> reason; "Compare covers" off (the settings API)
/// keeps it Auto-linked. Every provider request fails the test - the check sends none. Covers are stored HASHES (the cover-layer
/// kit's fake hasher reads them back); no picture.
/// </summary>
[Trait("Category", "Http")]
public sealed class CoverCheckHttpTests
{
    private const string LibPub = "cclib1";
    private const string FolderPub = "ccFolder";
    private const ulong Web1 = 0x0000_0000_0000_0000;
    private const ulong Web2 = 0xFFFF_FFFF_0000_0000;
    private const ulong Local1 = 0x0000_0000_FFFF_FFFF;
    private const ulong Local2 = 0x0000_FFFF_FFFF_0000;

    private static MetadataNetworkWebApplicationFactory Factory() => new(failOnAnyRequest: true,
        configureServices: s => s.AddSingleton<ICoverHasher>(new CoverLayerTestKit.FakeHasher()));

    // An Auto-linked folder of two volumes whose page-1 thumbnails are clearly different from the record's stored volume covers.
    private static async Task SeedAsync(MetadataNetworkWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var thumbnails = scope.ServiceProvider.GetRequiredService<ThumbnailStore>();
        var lib = await VolumeTestData.AddLibraryAsync(db, LibPub);
        var folder = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "CC Saga", FolderPub);
        foreach (var (name, hash) in new[] { ("CC Saga v01", Local1), ("CC Saga v02", Local2) })
        {
            var volume = await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, name);
            var temp = Path.Combine(factory.DataRoot, Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllTextAsync(temp, CoverLayerTestKit.HashFile(hash));
            await thumbnails.PublishAsync(volume.Id, 1, temp);
            File.Delete(temp);
        }
        var record = await VolumeTestData.AddRecordAsync(db, "CC Same Title");
        await VolumeTestData.LinkAsync(db, folder, record.Id, SeriesLinkState.Auto);
        db.MetadataMatchQueue.Add(new MetadataMatchQueueEntity
        {
            NodeId = folder.Id,
            LibraryId = lib.Id,
            State = 2,
            Outcome = (int)MatchBand.Auto,
            EnqueuedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
        });
        var companion = new MetadataRecordEntity { PublicId = "mdcc1", Provider = "mangadex", ExternalId = "md-cc-1", Title = "CC", FetchedAt = DateTimeOffset.UtcNow };
        db.MetadataRecords.Add(companion);
        await db.SaveChangesAsync();
        db.MetadataCompanions.Add(new MetadataCompanionEntity { RecordId = record.Id, Provider = "mangadex", CompanionRecordId = companion.Id, State = (int)CompanionState.Auto });
        foreach (var (volume, locale, hash) in new[] { (1, "en", Web1), (1, "ja", Web1 ^ 0x7), (2, "en", Web2) })
        {
            db.VolumeCovers.Add(new VolumeCoverEntity
            {
                PublicId = $"vc00000000000cc{volume}{locale}",
                ProviderRecordId = companion.Id,
                Kind = (int)VolumeCoverKind.Volume,
                Volume = volume,
                Locale = locale,
                RemoteId = $"r{volume}{locale}",
                RemoteFile = $"f{volume}{locale}.jpg",
                State = (int)VolumeCoverState.Stored,
                StoredVersion = 1,
                Hash = unchecked((long)hash),
                ListedAt = DateTimeOffset.UtcNow,
                StoredAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }

    private static MetadataAutoMatchHostedService Worker(MetadataNetworkWebApplicationFactory factory) =>
        factory.Services.GetServices<IHostedService>().OfType<MetadataAutoMatchHostedService>().Single();

    private static async Task<T> OkAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Web))!;
    }

    [Fact]
    public async Task TheVolumeCoverTick_MovesAnAutoLinkWithDifferentVolumeCovers_ToNeedsReview_WithCoverDiffers()
    {
        using var factory = Factory();
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var autoTab = await OkAsync<MetadataReviewPageDto>(await admin.GetAsync("/api/v1/admin/metadata/review?tab=AutoLinked"));
        Assert.Contains(autoTab.Items, i => i.NodeId == FolderPub);

        await Worker(factory).RunVolumeCoversAsync(CancellationToken.None);

        var page = await OkAsync<MetadataReviewPageDto>(await admin.GetAsync("/api/v1/admin/metadata/review?tab=NeedsReview"));
        var item = Assert.Single(page.Items, i => i.NodeId == FolderPub);
        Assert.Contains("cover_differs", item.Reasons);
        var candidate = Assert.Single(item.Candidates);
        Assert.Equal(["cover_differs"], candidate.Reasons);
        Assert.Empty(factory.Handler.Seen); // stored covers only - not one request
    }

    [Fact]
    public async Task CompareCoversOff_KeepsTheAutoLink()
    {
        using var factory = Factory();
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings", new UpdateMetadataSettingsRequest { CompareCoversEnabled = false }))
            .EnsureSuccessStatusCode();

        await Worker(factory).RunVolumeCoversAsync(CancellationToken.None);

        var autoTab = await OkAsync<MetadataReviewPageDto>(await admin.GetAsync("/api/v1/admin/metadata/review?tab=AutoLinked"));
        Assert.Contains(autoTab.Items, i => i.NodeId == FolderPub);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        Assert.Equal((int)SeriesLinkState.Auto, (await db.NodeSeriesLinks.SingleAsync(l => l.Node!.PublicId == FolderPub)).State);
    }
}
