namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-with-DB tests for the missing volumes / chapters report (1.28.0): which links count (own confirmed /
/// auto links on folders; not Don't match, needs review, archives or removed folders), the Volumes / Chapters
/// subfolders, the stored English totals vs the origin fallback, the filters, paging, and the per-node row.
/// Synthetic names only.
/// </summary>
public sealed class MissingReportServiceTests
{
    private static MissingReportService Service(MetadataTestDb t) => new(t.Db, NullLogger<MissingReportService>.Instance);

    private static async Task<MetadataRecordEntity> RecordAsync(MetadataTestDb t, string id, int? originVolumes, string? publishersJson = null,
        string? statusText = null, double? latestChapter = null)
    {
        var r = await t.AddRecordAsync(id, "Synthetic Record " + id);
        r.OriginVolumes = originVolumes;
        r.PublishersJson = publishersJson;
        r.StatusText = statusText;
        r.LatestChapter = latestChapter;
        await t.Db.SaveChangesAsync();
        return r;
    }

    private static async Task<CatalogNodeEntity> SeriesAsync(MetadataTestDb t, string name, params string[] archives)
    {
        var folder = await t.AddFolderAsync(null, name);
        foreach (var a in archives)
            await t.AddArchiveAsync(folder, a);
        return folder;
    }

    [Fact]
    public async Task Report_ComparesWithTheStoredEnglishTotal_AndFallsBackToTheOrigin()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var english = await SeriesAsync(t, "Alpha Series", "Alpha v01", "Alpha v02", "Alpha v03");
        await t.AddLinkAsync(english, await RecordAsync(t, "1", 14,
            "[{\"name\":\"Origin House\",\"kind\":\"original\"},{\"name\":\"Print English\",\"kind\":\"english\",\"volumes\":10,\"chapters\":60}]"));
        // A record fetched before 1.28.0: an English publisher without totals -> origin total, flagged.
        var legacy = await SeriesAsync(t, "Beta Series", "Beta v01", "Beta v02");
        await t.AddLinkAsync(legacy, await RecordAsync(t, "2", 5, "[{\"name\":\"Print English\",\"kind\":\"english\"}]"), SeriesLinkState.Auto);

        var (error, page) = await Service(t).ListAsync(null, onlyMissing: false, cursor: null, limit: 50);

        Assert.Null(error);
        Assert.Equal(2, page!.Total);
        var alpha = page.Items.Single(i => i.DisplayName == "Alpha Series");
        Assert.Equal(MissingVerdict.Behind, alpha.Verdict);
        Assert.Equal((3, 10, 7, MissingTotalSource.English), (alpha.Volumes!.Have, alpha.Volumes.Available, alpha.Volumes.BehindBy, alpha.Volumes.Source));
        Assert.False(alpha.EnglishTotalUnknown);
        Assert.Equal(SeriesLinkState.Confirmed, alpha.LinkState);
        Assert.Equal("Synthetic Record 1", alpha.RecordTitle);
        Assert.Equal(t.LibraryPublicId, alpha.LibraryId);

        var beta = page.Items.Single(i => i.DisplayName == "Beta Series");
        Assert.Equal((5, MissingTotalSource.Origin, 3), (beta.Volumes!.Available, beta.Volumes.Source, beta.Volumes.BehindBy));
        Assert.True(beta.EnglishTotalUnknown);
        Assert.Equal(SeriesLinkState.Auto, beta.LinkState);
        Assert.Equal(new[] { "Alpha Series", "Beta Series" }, page.Items.Select(i => i.DisplayName)); // behind by 7 before 3
    }

    [Fact]
    public async Task Report_ListsOnlyOwnFolderLinks_WithARecord()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var record = await RecordAsync(t, "3", 4);
        var counted = await SeriesAsync(t, "Counted", "Counted v01");
        await t.AddLinkAsync(counted, record);
        await t.AddLinkAsync(await SeriesAsync(t, "Refused", "Refused v01"), null, SeriesLinkState.DontMatch);
        var review = await SeriesAsync(t, "Undecided", "Undecided v01");
        t.Db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
        {
            NodeId = review.Id, LibraryId = review.LibraryId, State = (int)SeriesLinkState.NeedsReview,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        var shelf = await t.AddFolderAsync(null, "Loose Shelf");
        await t.AddLinkAsync(await t.AddArchiveAsync(shelf, "Loose v01"), record); // archive link: not a series folder
        var removed = await SeriesAsync(t, "Removed", "Removed v01");
        await t.AddLinkAsync(removed, record);
        removed.Availability = (int)Core.Catalog.CatalogNodeAvailability.Tombstoned;
        await t.Db.SaveChangesAsync();

        var (_, page) = await Service(t).ListAsync(null, false, null, 50);

        Assert.Equal("Counted", Assert.Single(page!.Items).DisplayName);
    }

    [Fact]
    public async Task Report_ReadsVolumeAndChapterSubfolders_ButNotOtherSubfoldersOrLinkedOnes()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var series = await t.AddFolderAsync(null, "Gamma Series");
        var volumes = await t.AddFolderAsync(series, "Volumes");
        await t.AddArchiveAsync(volumes, "Gamma v01");
        await t.AddArchiveAsync(volumes, "Gamma v02");
        var chapters = await t.AddFolderAsync(series, "Chapters");
        await t.AddArchiveAsync(chapters, "Gamma - Chapter 015");
        await t.AddArchiveAsync(chapters, "Gamma - Chapter 016");
        var extras = await t.AddFolderAsync(series, "Extras");
        await t.AddArchiveAsync(extras, "Gamma v09"); // not a Volumes / Chapters folder: ignored
        await t.AddLinkAsync(series, await RecordAsync(t, "4", 3, statusText: "3 Volumes (Ongoing)\n20 Chapters", latestChapter: 18));

        var row = await Service(t).ForNodeAsync(series.PublicId);

        Assert.NotNull(row);
        Assert.Equal((2, 3, 1), (row!.Volumes!.Have, row.Volumes.Available, row.Volumes.BehindBy));
        Assert.Equal((16, 20, MissingTotalSource.Origin), (row.Chapters!.Have, row.Chapters.Available, row.Chapters.Source));
        Assert.Empty(row.Chapters.Missing); // chapters after volumes count from the lowest on disk
        Assert.Equal(0, row.MixedFolders);
        Assert.Null(await Service(t).ForNodeAsync(volumes.PublicId)); // no own link
        Assert.Null(await Service(t).ForNodeAsync("no-such-node"));
    }

    [Fact]
    public async Task Report_FiltersByLibraryAndMissing_AndPages()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var other = await t.AddLibraryAsync("otherlib", "Other Lib");
        for (var i = 0; i < 3; i++)
        {
            var behind = await SeriesAsync(t, $"Behind {i}", $"Behind {i} v01");
            await t.AddLinkAsync(behind, await RecordAsync(t, "b" + i, 5));
        }
        var complete = await SeriesAsync(t, "Complete", "Complete v01", "Complete v02");
        await t.AddLinkAsync(complete, await RecordAsync(t, "c", 2));
        var elsewhere = await t.AddFolderAsync(null, "Elsewhere", other.Id);
        await t.AddArchiveAsync(elsewhere, "Elsewhere v01");
        await t.AddLinkAsync(elsewhere, await RecordAsync(t, "e", 9));

        var svc = Service(t);
        var (_, all) = await svc.ListAsync(t.LibraryPublicId, onlyMissing: false, cursor: null, limit: 50);
        Assert.Equal(4, all!.Total);
        Assert.Equal((4, 3, 1), (all.Summary.Series, all.Summary.Behind, all.Summary.UpToDate));

        var (_, first) = await svc.ListAsync(t.LibraryPublicId, onlyMissing: true, cursor: null, limit: 2);
        Assert.Equal(3, first!.Total);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal("2", first.NextCursor);
        var (_, second) = await svc.ListAsync(t.LibraryPublicId, onlyMissing: true, cursor: first.NextCursor, limit: 2);
        Assert.Equal("Behind 2", Assert.Single(second!.Items).DisplayName);
        Assert.Null(second.NextCursor);

        var (error, _) = await svc.ListAsync("nope", false, null, 50);
        Assert.Equal("library_not_found", error);
    }

    [Fact]
    public void EnglishTotals_AreStoredWithThePublisher_JsonAdditive()
    {
        var json = MetadataJson.WriteList(new[]
        {
            new MetadataJson.Publisher("Origin House", "original"),
            new MetadataJson.Publisher("Print English", "english", 10, 60),
        });
        Assert.Equal("[{\"name\":\"Origin House\",\"kind\":\"original\"},{\"name\":\"Print English\",\"kind\":\"english\",\"volumes\":10,\"chapters\":60}]", json);
        Assert.Equal(new MetadataJson.Publisher("Old", "english"), MetadataJson.ReadList<MetadataJson.Publisher>("[{\"name\":\"Old\",\"kind\":\"english\"}]")[0]);
    }
}
