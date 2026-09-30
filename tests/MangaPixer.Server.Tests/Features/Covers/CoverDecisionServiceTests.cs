namespace com.lifepixer.mangapixer.Tests.Server.Features.Covers;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests of the automatic cover decisions (1.29.0): the P2.2 source matrix end to end on a migrated
/// database - the spread crop (direction from the admin's reader defaults), a volume's web cover only when clearly
/// different, one-shots, series folders, webtoons, Don't match - and the inputs key: an unchanged series spends no
/// worker call, a new stored cover re-decides and bumps the URL version. Stored hashes only (<see cref="CoverLayerTestKit"/>).
/// </summary>
public sealed class CoverDecisionServiceTests
{
    private const ulong Local = 0x0F0F_3C3C_5A5A_A5A5UL;

    private static ulong Away(int bits) => Local ^ ((1UL << bits) - 1);

    [Fact]
    public async Task UnlinkedSpreadVolume_GetsItsFrontHalf_ByTheLibraryReadingDirection()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var folder = await kit.Db.AddFolderAsync(null, "Series");
        var ltr = await kit.AddBookAsync(folder, "Series v01", 1400, 1000, Local);

        Assert.Equal(CoverDecisionOutcome.Decided, await kit.Decisions().DecideAsync(ltr.Id, default));
        var auto = await kit.AutoAsync(ltr.Id);
        Assert.Equal((int)AutoCoverSource.Crop, auto!.Source);
        Assert.Equal((int)CoverCropSide.Right, auto.CropSide); // left-to-right: back / spine / front
        Assert.Equal((int)AutoCoverReason.Spread, auto.Reason);

        var library = await kit.Db.Db.Libraries.SingleAsync(l => l.Id == kit.Db.LibraryId);
        library.DefaultReaderMode = (int)ReaderMode.PagedRtl;
        await kit.Db.Db.SaveChangesAsync();
        var rtl = await kit.AddBookAsync(folder, "Series v02", 1400, 1000, Local);
        await kit.Decisions().DecideAsync(rtl.Id, default);
        Assert.Equal((int)CoverCropSide.Left, (await kit.AutoAsync(rtl.Id))!.CropSide);

        // No web cover to compare with: nothing is hashed; each crop is rendered once, right after its decision, into the
        // data root's crop store.
        Assert.Equal(0, kit.Hasher.Calls);
        Assert.Equal(2, kit.Renderer.Requests.Count);
        Assert.True(File.Exists(kit.Files.CropPath(ltr.Id, 1, CoverCropSide.Right)));
        Assert.True(File.Exists(kit.Files.CropPath(rtl.Id, 1, CoverCropSide.Left)));
        // Same inputs: not decided again, nothing rendered.
        Assert.Equal(CoverDecisionOutcome.Unchanged, await kit.Decisions().DecideAsync(rtl.Id, default));
        Assert.Equal(2, kit.Renderer.Requests.Count);

        // The direction is an input: v01, decided left-to-right, now takes the left half (the library reads right-to-left).
        Assert.Equal(CoverDecisionOutcome.Decided, await kit.Decisions().DecideAsync(ltr.Id, default));
        var redecided = await kit.AutoAsync(ltr.Id);
        Assert.Equal((int)CoverCropSide.Left, redecided!.CropSide);
        Assert.Equal(2, redecided.Version);
        Assert.True(File.Exists(kit.Files.CropPath(ltr.Id, 1, CoverCropSide.Left)));
    }

    [Fact]
    public async Task UnlinkedChapterSpread_AndSinglePageVolumes_GetNoDecision()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var folder = await kit.Db.AddFolderAsync(null, "Series");
        var chapter = await kit.AddBookAsync(folder, "Series c012", 1400, 1000, Local);
        var single = await kit.AddBookAsync(folder, "Series v03", 700, 1000, Local);
        var noUnit = await kit.AddBookAsync(folder, "Artbook", 1400, 1000, Local);

        foreach (var node in new[] { chapter, single, noUnit })
            await kit.Decisions().DecideAsync(node.Id, default);
        Assert.Empty(await kit.Db.Db.NodeAutoCovers.ToListAsync());
    }

    [Fact]
    public async Task LinkedVolume_WebOnlyWhenClearlyDifferent_AndReDecidesOnlyWhenTheInputsChange()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var folder = await kit.Db.AddFolderAsync(null, "Series");
        var record = await kit.Db.AddRecordAsync("101", "Series");
        await kit.Db.AddLinkAsync(folder, record);
        var companion = await kit.AddCompanionAsync(record);
        var v2 = await kit.AddBookAsync(folder, "Series v02", 700, 1000, Local);
        var web = await kit.AddStoredCoverAsync(companion, 2, "ja", Away(30));

        Assert.Equal(CoverDecisionOutcome.Decided, await kit.Decisions().DecideAsync(v2.Id, default));
        var auto = await kit.AutoAsync(v2.Id);
        // 1.30.0 (owner soak test): only the original-language (Japanese) cover exists - a cover in another language is no
        // evidence that page 1 is not the cover, so the file is kept; nothing web is used, so nothing is re-checked.
        Assert.Equal((int)AutoCoverSource.File, auto!.Source);
        Assert.Null(auto.VolumeCoverId);
        Assert.Equal((int)AutoCoverReason.OtherLanguageKept, auto.Reason);
        Assert.Null(auto.RecheckAt);
        Assert.Equal(unchecked((long)Local), auto.LocalHash);
        var calls = kit.Hasher.Calls;

        // Same inputs: nothing is hashed again.
        Assert.Equal(CoverDecisionOutcome.Unchanged, await kit.Decisions().DecideAsync(v2.Id, default));
        Assert.Equal(calls, kit.Hasher.Calls);

        // The preferred-language cover arrives and page 1 is clearly not it (a credit page): the web cover is used.
        var english = await kit.AddStoredCoverAsync(companion, 2, "en", Away(35));
        Assert.Equal(CoverDecisionOutcome.Decided, await kit.Decisions().DecideAsync(v2.Id, default));
        auto = await kit.AutoAsync(v2.Id);
        Assert.Equal((int)AutoCoverSource.WebVolume, auto!.Source);
        Assert.Equal(english.Id, auto.VolumeCoverId);
        Assert.Equal((int)AutoCoverReason.LocalNotCover, auto.Reason);
        Assert.Null(auto.RecheckAt);
        Assert.Equal(2, auto.Version);
        Assert.NotEqual(web.Id, auto.VolumeCoverId);
    }

    [Fact]
    public async Task LinkedVolume_UncertainKeepsTheFile_AndNoWebCoverNeedsNoRow()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var folder = await kit.Db.AddFolderAsync(null, "Series");
        var record = await kit.Db.AddRecordAsync("102", "Series");
        await kit.Db.AddLinkAsync(folder, record);
        var companion = await kit.AddCompanionAsync(record);
        var v1 = await kit.AddBookAsync(folder, "Series v01", 700, 1000, Local);
        var v5 = await kit.AddBookAsync(folder, "Series v05", 700, 1000, Local);
        await kit.AddStoredCoverAsync(companion, 1, "en", Away(15));
        await kit.SetPosterAsync(record, Away(40));

        await kit.Decisions().DecideAsync(v1.Id, default);
        Assert.Equal((int)AutoCoverReason.UncertainKept, (await kit.AutoAsync(v1.Id))!.Reason);
        // Volume 5 has no web cover: the file, never the poster - and no row.
        await kit.Decisions().DecideAsync(v5.Id, default);
        Assert.Null(await kit.AutoAsync(v5.Id));
    }

    [Fact]
    public async Task LinkedSpreadVolume_OtherHalfMatchingTheWebCover_Wins()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var folder = await kit.Db.AddFolderAsync(null, "Series");
        var record = await kit.Db.AddRecordAsync("103", "Series");
        record.Origin = (int)MetadataOrigin.Korea; // left-to-right by origin: the right half is guessed first
        await kit.Db.Db.SaveChangesAsync();
        await kit.Db.AddLinkAsync(folder, record);
        var companion = await kit.AddCompanionAsync(record, MetadataOrigin.Korea);
        var v3 = await kit.AddBookAsync(folder, "Series v03", 1400, 1000, Local);
        await kit.AddStoredCoverAsync(companion, 3, "en", kit.Renderer.HashBySide[CoverCropSides.Left] ^ 0b11);

        await kit.Decisions().DecideAsync(v3.Id, default);
        var auto = await kit.AutoAsync(v3.Id);
        Assert.Equal((int)AutoCoverSource.Crop, auto!.Source);
        Assert.Equal((int)CoverCropSide.Left, auto.CropSide);
        Assert.Equal((int)AutoCoverReason.SpreadOtherSide, auto.Reason);
        // Both halves were rendered into the data root's crop store (never a source path).
        Assert.Equal(2, kit.Renderer.Requests.Count);
        Assert.All(kit.Renderer.Requests, r => Assert.StartsWith(kit.Files.CropsRoot, r.OutputPath, StringComparison.Ordinal));
        Assert.True(File.Exists(kit.Files.CropPath(v3.Id, 1, CoverCropSide.Left)));
    }

    [Fact]
    public async Task DontMatch_NothingAutomatic_NotEvenTheCrop()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var folder = await kit.Db.AddFolderAsync(null, "Series");
        var v1 = await kit.AddBookAsync(folder, "Series v01", 1400, 1000, Local);
        await kit.Decisions().DecideAsync(v1.Id, default);
        Assert.NotNull(await kit.AutoAsync(v1.Id));

        await kit.Db.AddLinkAsync(folder, null, SeriesLinkState.DontMatch);
        Assert.Equal(CoverDecisionOutcome.Cleared, await kit.Decisions().DecideAsync(v1.Id, default));
        Assert.Null(await kit.AutoAsync(v1.Id));
    }

    [Fact]
    public async Task OneShot_ShowsThePosterByDefault_TheFileOnlyWhenPageOneIsAlreadyIt_AndNeverCrops()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var folder = await kit.Db.AddFolderAsync(null, "One Shot");
        var record = await kit.Db.AddRecordAsync("104", "One Shot");
        await kit.Db.AddLinkAsync(folder, record);
        var book = await kit.AddBookAsync(folder, "One Shot", 1400, 1000, Local);
        await kit.SetPosterAsync(record, Away(40));

        await kit.Decisions().DecideAsync(book.Id, default);
        var auto = await kit.AutoAsync(book.Id);
        Assert.Equal((int)AutoCoverSource.Poster, auto!.Source);
        Assert.Equal((int)AutoCoverReason.OneShotDefault, auto.Reason);
        Assert.Empty(kit.Renderer.Requests);
        // The folder of one archive is not decided itself: it shows its one-shot.
        await kit.Decisions().DecideAsync(folder.Id, default);
        Assert.Null(await kit.AutoAsync(folder.Id));

        await kit.SetPosterAsync(record, Away(5));
        await kit.Decisions().DecideAsync(book.Id, default);
        Assert.Equal((int)AutoCoverSource.File, (await kit.AutoAsync(book.Id))!.Source);
    }

    [Fact]
    public async Task SeriesFolder_WithoutLocalVolume1_ShowsTheWebVolume1_WithAMatchingVolume1_KeepsTheFile()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var chapters = await kit.Db.AddFolderAsync(null, "Chapters Series");
        var record = await kit.Db.AddRecordAsync("105", "Chapters Series");
        await kit.Db.AddLinkAsync(chapters, record);
        var companion = await kit.AddCompanionAsync(record);
        await kit.AddBookAsync(chapters, "Chapters Series c001", 700, 1000, Local);
        await kit.AddBookAsync(chapters, "Chapters Series c002", 700, 1000, Local);
        var w1 = await kit.AddStoredCoverAsync(companion, 1, "en", Away(40));

        await kit.Decisions().DecideSubtreeAsync(chapters.Id, default);
        var auto = await kit.AutoAsync(chapters.Id);
        Assert.Equal((int)AutoCoverSource.WebVolume, auto!.Source);
        Assert.Equal(w1.Id, auto.VolumeCoverId);
        Assert.Equal((int)AutoCoverReason.ChapterFolderDefault, auto.Reason);

        var volumes = await kit.Db.AddFolderAsync(null, "Volume Series");
        var record2 = await kit.Db.AddRecordAsync("106", "Volume Series");
        await kit.Db.AddLinkAsync(volumes, record2);
        var companion2 = await kit.AddCompanionAsync(record2);
        await kit.AddBookAsync(volumes, "Volume Series v01", 700, 1000, Local);
        await kit.AddBookAsync(volumes, "Volume Series v02", 700, 1000, Local);
        await kit.AddStoredCoverAsync(companion2, 1, "en", Away(2));
        Assert.True(await kit.Decisions().DecideSubtreeAsync(volumes.Id, default) >= 2); // volume 1 and the folder
        auto = await kit.AutoAsync(volumes.Id);
        Assert.Equal((int)AutoCoverSource.File, auto!.Source);
        Assert.Equal((int)AutoCoverReason.FileMatchesWeb, auto.Reason);
    }

    [Fact]
    public async Task Webtoon_ShowsTheMainCoverThenThePoster()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var folder = await kit.Db.AddFolderAsync(null, "Strip");
        var record = await kit.Db.AddRecordAsync("107", "Strip");
        record.Webtoon = true;
        await kit.Db.Db.SaveChangesAsync();
        await kit.Db.AddLinkAsync(folder, record);
        await kit.AddBookAsync(folder, "Strip c001", 700, 5000, Local);
        await kit.AddBookAsync(folder, "Strip c002", 700, 5000, Local);
        await kit.SetPosterAsync(record, Away(40));

        await kit.Decisions().DecideAsync(folder.Id, default);
        Assert.Equal((int)AutoCoverSource.Poster, (await kit.AutoAsync(folder.Id))!.Source);

        var companion = await kit.AddCompanionAsync(record, MetadataOrigin.Korea);
        var main = await kit.AddStoredCoverAsync(companion, null, "ko", Away(30), VolumeCoverKind.Main);
        await kit.Decisions().DecideAsync(folder.Id, default);
        var auto = await kit.AutoAsync(folder.Id);
        Assert.Equal((int)AutoCoverSource.WebMain, auto!.Source);
        Assert.Equal(main.Id, auto.VolumeCoverId);
        Assert.Equal((int)AutoCoverReason.WebtoonDefault, auto.Reason);
    }

    [Fact]
    public async Task SeasonSubfolder_ShowsTheVolumeItsFirstChapterMapsTo()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var series = await kit.Db.AddFolderAsync(null, "Series");
        var record = await kit.Db.AddRecordAsync("108", "Series");
        await kit.Db.AddLinkAsync(series, record);
        var companion = await kit.AddCompanionAsync(record);
        var part2 = await kit.Db.AddFolderAsync(series, "Part 2");
        await kit.AddBookAsync(part2, "Series c031", 700, 1000, Local);
        kit.Db.Db.SeriesVolumeMaps.Add(new SeriesVolumeMapEntity
        {
            RecordId = record.Id,
            Source = (int)VolumeMapSource.MangaDexAggregate,
            State = (int)VolumeMapState.Ok,
            VolumesJson = """[{"v":"3","c":["21","22","30"]},{"v":"4","c":["31","32","32.5"]}]""",
            ContentHash = "x",
            Version = 1,
            FetchedAt = DateTimeOffset.UtcNow,
        });
        await kit.Db.Db.SaveChangesAsync();
        var w4 = await kit.AddStoredCoverAsync(companion, 4, "ja", Away(40));

        await kit.Decisions().DecideAsync(part2.Id, default);
        var auto = await kit.AutoAsync(part2.Id);
        Assert.Equal(w4.Id, auto!.VolumeCoverId);
        Assert.Equal((int)AutoCoverReason.SubfolderFirstVolume, auto.Reason);
    }

    [Theory]
    [InlineData("""[{"v":"3","c":["21","22"]},{"v":"4","c":["31","31.5"]}]""", "31.5", 4)]
    [InlineData("""[{"v":"3","c":["21","22"]}]""", "22", 3)]
    [InlineData("""[{"v":"3","c":["21"]}]""", "99", null)]
    [InlineData("""{"not":"a list"}""", "1", null)]
    [InlineData("not json", "1", null)]
    public void VolumeOfChapter_ReadsTheStoredList(string json, string chapter, int? volume) =>
        Assert.Equal(volume, CoverDecisionService.VolumeOfChapter(json, decimal.Parse(chapter, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public async Task Direction_FolderDefault_ThenLibrary_ThenComicInfo_ThenOrigin()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var folder = await kit.Db.AddFolderAsync(null, "Series");
        var book = await kit.AddBookAsync(folder, "Series v01", 1400, 1000, Local);
        var resolver = new CoverDirectionResolver(kit.Db.Db);

        Assert.Equal(CoverDirection.LeftToRight, await resolver.ResolveAsync(book.Id, null, default));
        Assert.Equal(CoverDirection.RightToLeft, await resolver.ResolveAsync(book.Id, MetadataOrigin.Japan, default));

        await kit.Db.AddComicInfoAsync(book, "Series");
        var row = await kit.Db.Db.EmbeddedMetadata.SingleAsync(e => e.NodeId == book.Id);
        row.MangaDirection = 0; // ComicInfo Manga = No
        await kit.Db.Db.SaveChangesAsync();
        Assert.Equal(CoverDirection.LeftToRight, await resolver.ResolveAsync(book.Id, MetadataOrigin.Japan, default));

        var library = await kit.Db.Db.Libraries.SingleAsync(l => l.Id == kit.Db.LibraryId);
        library.DefaultReaderMode = (int)ReaderMode.PagedRtl;
        await kit.Db.Db.SaveChangesAsync();
        Assert.Equal(CoverDirection.RightToLeft, await resolver.ResolveAsync(book.Id, null, default));

        kit.Db.Db.FolderReaderDefaults.Add(new FolderReaderDefaultEntity { NodeId = folder.Id, ReaderMode = (int)ReaderMode.PagedLtr });
        await kit.Db.Db.SaveChangesAsync();
        Assert.Equal(CoverDirection.LeftToRight, await resolver.ResolveAsync(book.Id, null, default));
    }

    [Fact]
    public async Task Sweep_FindsChangedSeries_AndUndecidedSpreads()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var folder = await kit.Db.AddFolderAsync(null, "Series");
        var record = await kit.Db.AddRecordAsync("109", "Series");
        await kit.Db.AddLinkAsync(folder, record);
        var spread = await kit.AddBookAsync(folder, "Series v01", 1400, 1000, Local);
        await kit.AddBookAsync(folder, "Series v02", 700, 1000, Local);

        Assert.Equal([folder.Id], await CoverDecisionHostedService.ChangedSeriesAsync(kit.Db.Db, DateTimeOffset.MinValue, default));
        Assert.Empty(await CoverDecisionHostedService.ChangedSeriesAsync(kit.Db.Db, DateTimeOffset.UtcNow.AddMinutes(1), default));
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);
        var companion = await kit.AddCompanionAsync(record);
        Assert.Equal([folder.Id], await CoverDecisionHostedService.ChangedSeriesAsync(kit.Db.Db, since, default));

        var spreads = await CoverDecisionHostedService.UndecidedSpreadsAsync(kit.Db.Db, default);
        Assert.Equal([spread.Id], spreads.Select(s => s.NodeId));
        await kit.Decisions().DecideAsync(spread.Id, default);
        Assert.Empty(await CoverDecisionHostedService.UndecidedSpreadsAsync(kit.Db.Db, default));
        Assert.NotNull(companion);
    }
}
