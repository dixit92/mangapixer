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
/// subfolders, the stored English totals (1.29.0 RC: the origin totals are context only; "behind" follows what is released in the
/// preferred language), the filters, paging, and the per-node row.
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
    public async Task Report_ComparesWithTheStoredEnglishTotal_AndNeverWithTheOrigin()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var english = await SeriesAsync(t, "Alpha Series", "Alpha v01", "Alpha v02", "Alpha v03");
        await t.AddLinkAsync(english, await RecordAsync(t, "1", 14,
            "[{\"name\":\"Origin House\",\"kind\":\"original\"},{\"name\":\"Print English\",\"kind\":\"english\",\"volumes\":10,\"chapters\":60}]"));
        // A record fetched before 1.28.0: an English publisher without totals -> no volume verdict (1.29.0 RC: an origin volume
        // that is not translated is not missing), the origin total as context, flagged.
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
        Assert.Equal(((int?)null, (MissingTotalSource?)null, 0, (int?)5), (beta.Volumes!.Available, beta.Volumes.Source, beta.Volumes.BehindBy, beta.Volumes.OriginTotal));
        Assert.Equal(MissingVerdict.NoTotal, beta.Verdict);
        Assert.True(beta.EnglishTotalUnknown);
        Assert.Equal("en", beta.Language);
        Assert.Equal(SeriesLinkState.Auto, beta.LinkState);
        Assert.Equal(new[] { "Alpha Series", "Beta Series" }, page.Items.Select(i => i.DisplayName)); // behind before no total
    }

    [Fact]
    public async Task Report_FollowsThePreferredLanguage_AndItsReleasedChapters()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var volumes = await SeriesAsync(t, "Eta Series", "Eta v01", "Eta v02");
        await t.AddLinkAsync(volumes, await RecordAsync(t, "h1", 14, "[{\"name\":\"Print English\",\"kind\":\"english\",\"volumes\":10}]"));
        var chapters = await SeriesAsync(t, "Theta Series", "Theta - Chapter 001", "Theta - Chapter 002");
        var record = await RecordAsync(t, "h2", null, statusText: "40 Chapters (Ongoing)", latestChapter: 30);
        await t.AddLinkAsync(chapters, record);
        t.Db.SeriesVolumeMaps.Add(new SeriesVolumeMapEntity
        {
            RecordId = record.Id, Source = (int)VolumeMapSource.MangaDexAggregate, State = (int)VolumeMapState.Ok, VolumesJson = "[]",
            ReleasedLanguage = "fr", ReleasedChaptersJson = "[\"1\",\"2\",\"3\",\"4\",\"4.5\",\"5\"]", ContentHash = "h", Version = 1,
            FetchedAt = DateTimeOffset.UtcNow,
        });
        t.Db.AppSettings.Add(new AppSettingsEntity { MetadataCoverLanguage = "fr" });
        await t.Db.SaveChangesAsync();

        var (_, page) = await Service(t).ListAsync(null, onlyMissing: false, cursor: null, limit: 50);

        // French: the English volume total says nothing about French volumes - no volume verdict, the origin total as context.
        var eta = page!.Items.Single(i => i.DisplayName == "Eta Series");
        Assert.Equal((MissingVerdict.NoTotal, (int?)null, (int?)14, "fr"), (eta.Verdict, eta.Volumes!.Available, eta.Volumes.OriginTotal, eta.Language));
        Assert.False(eta.EnglishTotalUnknown);
        // Chapters: the list read for French names 1-5 released (an English "latest release" does not count).
        var theta = page.Items.Single(i => i.DisplayName == "Theta Series");
        Assert.Equal((MissingVerdict.Behind, 5, MissingTotalSource.Released, 3), (theta.Verdict, theta.Chapters!.Available, theta.Chapters.Source, theta.Chapters.BehindBy));
        Assert.Equal(40, theta.Chapters.OriginTotal);
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
            NodeId = review.Id,
            LibraryId = review.LibraryId,
            State = (int)SeriesLinkState.NeedsReview,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
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
        // No English totals: the origin volume total is context only; chapters compare with the latest (English) release.
        Assert.Equal((2, (int?)null, 0, (int?)3), (row!.Volumes!.Have, row.Volumes.Available, row.Volumes.BehindBy, row.Volumes.OriginTotal));
        Assert.Equal((16, 18, MissingTotalSource.LatestChapter), (row.Chapters!.Have, row.Chapters.Available, row.Chapters.Source));
        Assert.Empty(row.Chapters.Missing); // chapters after volumes count from the lowest on disk
        Assert.Equal(0, row.MixedFolders);
        Assert.Null(await Service(t).ForNodeAsync(volumes.PublicId)); // no own link
        Assert.Null(await Service(t).ForNodeAsync("no-such-node"));
    }

    [Fact]
    public async Task Report_ReadsSeasonAndPartSubfolders_ButNotSideLinkedOrDontMatchOnes()
    {
        // 1.29.0: the live finding's shape - a loose prologue next to Season subfolders - plus the folders that stay out.
        await using var t = await MetadataTestDb.CreateAsync();
        var series = await t.AddFolderAsync(null, "Delta Series");
        await t.AddArchiveAsync(series, "000.cbz");
        var one = await t.AddFolderAsync(series, "Season 1");
        for (var i = 1; i <= 3; i++)
            await t.AddArchiveAsync(one, $"Delta - Chapter {i:D3}");
        var two = await t.AddFolderAsync(series, "Season 2");
        await t.AddArchiveAsync(two, "Delta - Chapter 004");
        await t.AddArchiveAsync(two, "Delta - Chapter 004.5"); // an extra: never fills 5
        await t.AddArchiveAsync(two, "Delta - Chapter 006");
        var nested = await t.AddFolderAsync(two, "Volumes"); // one level inside another
        await t.AddArchiveAsync(nested, "01.cbz");
        await t.AddArchiveAsync(nested, "02.cbz");
        var extras = await t.AddFolderAsync(series, "Extras");
        await t.AddArchiveAsync(extras, "Delta - Chapter 900");
        var own = await t.AddFolderAsync(series, "Part 3"); // linked to a record of its own
        await t.AddArchiveAsync(own, "Delta - Chapter 950");
        await t.AddLinkAsync(own, await RecordAsync(t, "d3", 1));
        var refused = await t.AddFolderAsync(series, "Part 4"); // Don't match
        await t.AddArchiveAsync(refused, "Delta - Chapter 960");
        await t.AddLinkAsync(refused, null, SeriesLinkState.DontMatch);
        await t.AddLinkAsync(series, await RecordAsync(t, "d", 5, statusText: "5 Volumes (Ongoing)\n10 Chapters"));

        var row = await Service(t).ForNodeAsync(series.PublicId);

        // Only origin totals: nothing counts as behind (1.29.0 RC), but the gap below the highest chapter here is missing.
        Assert.Equal(MissingVerdict.Holes, row!.Verdict);
        Assert.Equal((0, 6, (int?)null, 0, (int?)10), (row.Chapters!.Lowest, row.Chapters.Have, row.Chapters.Available, row.Chapters.BehindBy, row.Chapters.OriginTotal));
        Assert.Equal([5], row.Chapters.Missing);
        Assert.Equal((2, (int?)null, (int?)5), (row.Volumes!.Have, row.Volumes.Available, row.Volumes.OriginTotal));
    }

    [Fact]
    public async Task Report_NumberingThatRestarts_OrALonePrologue_GivesNoVerdict()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var restart = await t.AddFolderAsync(null, "Epsilon Series");
        var one = await t.AddFolderAsync(restart, "Season 1");
        var two = await t.AddFolderAsync(restart, "Season 2");
        foreach (var (folder, n) in new[] { (one, 1), (one, 2), (one, 3), (two, 1), (two, 2) })
            await t.AddArchiveAsync(folder, $"Epsilon - Chapter {n:D3}");
        await t.AddLinkAsync(restart, await RecordAsync(t, "e1", null, statusText: "223 Chapters (Ongoing)"));
        var prologue = await SeriesAsync(t, "Zeta Series", "000.cbz");
        await t.AddLinkAsync(prologue, await RecordAsync(t, "z1", null, statusText: "223 Chapters (Ongoing)"));

        var (_, page) = await Service(t).ListAsync(null, onlyMissing: false, cursor: null, limit: 50);

        var r = page!.Items.Single(i => i.DisplayName == "Epsilon Series");
        Assert.Equal(MissingVerdict.Restarts, r.Verdict);
        Assert.Null(r.Chapters);
        var p = page.Items.Single(i => i.DisplayName == "Zeta Series");
        Assert.Equal(MissingVerdict.NoUnits, p.Verdict);
        Assert.Null(p.Chapters); // never "chapter 0 of 223"
        Assert.Equal((2, 0, 2), (page.Summary.Series, page.Summary.Behind, page.Summary.NoVerdict));
        var (_, onlyMissing) = await Service(t).ListAsync(null, onlyMissing: true, cursor: null, limit: 50);
        Assert.Empty(onlyMissing!.Items);
    }

    [Fact]
    public async Task Report_FiltersByLibraryAndMissing_AndPages()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var other = await t.AddLibraryAsync("otherlib", "Other Lib");
        static string English(int volumes) => $"[{{\"name\":\"Print English\",\"kind\":\"english\",\"volumes\":{volumes}}}]";
        for (var i = 0; i < 3; i++)
        {
            var behind = await SeriesAsync(t, $"Behind {i}", $"Behind {i} v01");
            await t.AddLinkAsync(behind, await RecordAsync(t, "b" + i, 5, English(5)));
        }
        var complete = await SeriesAsync(t, "Complete", "Complete v01", "Complete v02");
        await t.AddLinkAsync(complete, await RecordAsync(t, "c", 2, English(2)));
        var elsewhere = await t.AddFolderAsync(null, "Elsewhere", other.Id);
        await t.AddArchiveAsync(elsewhere, "Elsewhere v01");
        await t.AddLinkAsync(elsewhere, await RecordAsync(t, "e", 9, English(9)));

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
