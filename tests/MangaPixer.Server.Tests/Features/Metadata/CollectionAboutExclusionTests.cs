namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Export;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;
using Xunit;

/// <summary>
/// 1.34.0: a "Collection about" folder is never "the series" - with the SAME record also linked to the real series folder, the
/// collection is absent from the Missing report and the refresh cadences, and the export carries it as <c>CollectionAbout</c> with the
/// record as a label only (no companions, volumes, completion or refresh). Synthetic names only.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class CollectionAboutExclusionTests
{
    [Fact]
    public async Task Export_CarriesTheState_AndTheRecordAsALabelOnly()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        var db = kit.Db.Db;
        var record = await kit.Db.AddRecordAsync("4242", "Starlight Academy");
        var series = await kit.Db.AddFolderAsync(null, "Starlight Academy");
        await kit.Db.AddArchiveAsync(series, "Starlight Academy v01.cbz");
        await kit.Db.AddLinkAsync(series, record);
        var collection = await kit.Db.AddFolderAsync(null, "Starlight Academy Fan Works");
        await kit.Db.AddArchiveAsync(collection, "[Circle One] Summer Lesson.cbz");
        await kit.Db.AddLinkAsync(collection, record, SeriesLinkState.CollectionAbout);
        var mangadex = new MetadataRecordEntity
        {
            PublicId = "mdx",
            Provider = "mangadex",
            ExternalId = "0f0f0f0f-0000-4000-8000-000000000001",
            Title = "Starlight Academy",
            FetchedAt = ExportTestKit.Start,
        };
        db.MetadataRecords.Add(mangadex);
        await db.SaveChangesAsync();
        db.MetadataCompanions.Add(new MetadataCompanionEntity { RecordId = record.Id, Provider = "mangadex", CompanionRecordId = mangadex.Id, State = 0 });
        await db.SaveChangesAsync();

        await kit.RebuildAsync();
        var items = await kit.ItemsAsync();

        var seriesItem = items[series.PublicId];
        Assert.Equal("Confirmed", seriesItem.Link.State);
        Assert.NotNull(seriesItem.Companions.Mangadex);
        Assert.NotNull(seriesItem.Refresh);

        var item = items[collection.PublicId];
        Assert.Equal("CollectionAbout", item.Link.State);
        Assert.Equal("Starlight Academy", item.Record!.Title);
        Assert.Null(item.Companions.Mangadex);
        Assert.Null(item.Companions.Anilist);
        Assert.Empty(item.OfficialLinks);
        Assert.Null(item.Volumes);
        Assert.Null(item.Completion);
        Assert.Null(item.Refresh);
    }

    [Fact]
    public async Task MissingReport_AndRefreshCadences_LeaveTheCollectionOut()
    {
        await using var db = await MetadataTestDb.CreateAsync();
        using var h = new AutoMatchHarness(db);
        await h.Net.EnableAsync();
        var shared = await db.AddRecordAsync("4242", "Starlight Academy");
        var labelOnly = await db.AddRecordAsync("4343", "Moonlit Academy");
        var series = await db.AddFolderAsync(null, "Starlight Academy");
        await db.AddArchiveAsync(series, "Starlight Academy v01.cbz");
        await db.AddLinkAsync(series, shared);
        var collection = await db.AddFolderAsync(null, "Starlight Fan Works");
        await db.AddArchiveAsync(collection, "[Circle One] Summer Lesson.cbz");
        await db.AddLinkAsync(collection, shared, SeriesLinkState.CollectionAbout);
        var other = await db.AddFolderAsync(null, "Moonlit Fan Works");
        await db.AddArchiveAsync(other, "[Circle Two] Rainy Day.cbz");
        await db.AddLinkAsync(other, labelOnly, SeriesLinkState.CollectionAbout);

        var (error, page) = await h.Net.Report().ListAsync(null, onlyMissing: false, cursor: null, limit: 50);
        Assert.Null(error);
        Assert.Equal([series.PublicId], page!.Items.Select(i => i.NodeId).ToArray());
        Assert.Null(await h.Net.Report().ForNodeAsync(collection.PublicId));

        var cadences = await h.Refresh().RecomputeCadencesAsync();
        Assert.Contains(shared.Id, cadences.Keys);
        Assert.DoesNotContain(labelOnly.Id, cadences.Keys); // held only by a collection: never refreshed automatically
    }
}
