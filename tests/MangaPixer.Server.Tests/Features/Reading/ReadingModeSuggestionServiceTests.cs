namespace com.lifepixer.mangapixer.Tests.Server.Features.Reading;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Reading;
using com.lifepixer.mangapixer.Server.Features.Reading.Suggestions;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-with-DB tests of the reading-mode suggestions (1.41.0): which folders are suggested (tall strips, ComicInfo right-to-left,
/// the linked record as a hint), the roll-up of a linked series' subfolders, the admin's folder defaults (never suggested again), the
/// shared tall-strip sample, and Apply's validation and effect. Synthetic names only.
/// </summary>
public sealed class ReadingModeSuggestionServiceTests : IAsyncLifetime
{
    private MetadataTestDb _t = null!;

    public async Task InitializeAsync() => _t = await MetadataTestDb.CreateAsync();

    public async Task DisposeAsync() => await _t.DisposeAsync();

    private ReadingModeSuggestionService Service() =>
        new(_t.Db, new AuditService(_t.Db), NullLogger<ReadingModeSuggestionService>.Instance);

    private async Task<ReadingModeSuggestionsDto> ListAsync()
    {
        _t.Db.ChangeTracker.Clear();
        return await Service().ListAsync(CancellationToken.None);
    }

    /// <summary>An archive in <paramref name="folder"/> with <paramref name="count"/> measured pages of the given size.</summary>
    private async Task<CatalogNodeEntity> ArchiveAsync(CatalogNodeEntity folder, string name, int count, int width, int height)
    {
        var archive = await _t.AddArchiveAsync(folder, name);
        for (var i = 0; i < count; i++)
            _t.Db.PageEntries.Add(new PageEntryEntity
            {
                ItemId = archive.Id,
                ContentVersion = 1,
                Ordinal = i,
                EntryKey = new PageEntryKey(i).ToOpaque(),
                SourceEntryLocator = $"p{i}.png",
                MediaType = "image/png",
                Width = width,
                Height = height,
                ByteSize = 100,
            });
        await _t.Db.SaveChangesAsync();
        return archive;
    }

    private Task<CatalogNodeEntity> TallAsync(CatalogNodeEntity folder, string name, int count = 15) => ArchiveAsync(folder, name, count, 100, 400);

    private Task<CatalogNodeEntity> PagedAsync(CatalogNodeEntity folder, string name, int count = 15) => ArchiveAsync(folder, name, count, 700, 1000);

    private async Task MangaAsync(CatalogNodeEntity archive, int direction)
    {
        await _t.AddComicInfoAsync(archive, "Synthetic Series");
        var row = await _t.Db.EmbeddedMetadata.SingleAsync(e => e.NodeId == archive.Id);
        row.MangaDirection = direction;
        await _t.Db.SaveChangesAsync();
    }

    private async Task LibraryDefaultAsync(ReaderMode? mode)
    {
        var lib = await _t.Db.Libraries.SingleAsync(l => l.Id == _t.LibraryId);
        lib.DefaultReaderMode = (int?)mode;
        await _t.Db.SaveChangesAsync();
    }

    private async Task FolderDefaultAsync(CatalogNodeEntity folder, ReaderMode mode)
    {
        _t.Db.FolderReaderDefaults.Add(new FolderReaderDefaultEntity { NodeId = folder.Id, ReaderMode = (int)mode });
        await _t.Db.SaveChangesAsync();
    }

    private async Task<MetadataRecordEntity> RecordAsync(string id, MetadataOrigin origin, bool? webtoon)
    {
        var record = await _t.AddRecordAsync(id, "Synthetic " + id);
        record.Origin = (int)origin;
        record.Webtoon = webtoon;
        await _t.Db.SaveChangesAsync();
        return record;
    }

    [Fact]
    public async Task TallStripFolder_IsSuggestedVertical_OverTheLibraryDefault()
    {
        await LibraryDefaultAsync(ReaderMode.PagedRtl);
        var webtoon = await _t.AddFolderAsync(null, "Long Strip Story");
        await TallAsync(webtoon, "c001");
        await TallAsync(webtoon, "c002");
        var manga = await _t.AddFolderAsync(null, "Paged Story");
        await PagedAsync(manga, "v01");
        await PagedAsync(manga, "v02");

        var list = await ListAsync();

        var row = Assert.Single(list.Items);
        Assert.Equal(webtoon.PublicId, row.NodeId);
        Assert.Equal("Long Strip Story", row.FolderName);
        Assert.Equal(ReaderMode.VerticalWebtoon, row.SuggestedMode);
        Assert.Equal(ReaderMode.PagedRtl, row.CurrentMode);
        Assert.Equal(ReaderModeSource.Library, row.CurrentSource);
        Assert.Equal(ReadingModeConfidence.High, row.Confidence);
        Assert.Equal(ReadingModeSignal.TallPages, row.Signal);
        Assert.Equal((30, 30), (row.MeasuredPages, row.TallPages));
        Assert.Equal(_t.LibraryPublicId, row.LibraryId);
        Assert.Equal(2, list.FoldersChecked);
        Assert.Equal(1, list.Total);
        Assert.Empty(list.SetByAdmin);
    }

    [Fact]
    public async Task TallStripFolder_AlreadyVerticalByTheLibrary_IsNotSuggested()
    {
        await LibraryDefaultAsync(ReaderMode.VerticalWebtoon);
        var webtoon = await _t.AddFolderAsync(null, "Long Strip Story");
        await TallAsync(webtoon, "c001");
        await TallAsync(webtoon, "c002");

        Assert.Empty((await ListAsync()).Items);
    }

    [Fact]
    public async Task NothingSet_SuggestionShowsNoCurrentMode()
    {
        var webtoon = await _t.AddFolderAsync(null, "Long Strip Story");
        await TallAsync(webtoon, "c001", 25);

        var row = Assert.Single((await ListAsync()).Items);
        Assert.Null(row.CurrentMode);
        Assert.Equal(ReaderModeSource.None, row.CurrentSource);
    }

    [Fact]
    public async Task ComicInfoRightToLeftMajority_IsSuggestedRightToLeft()
    {
        await LibraryDefaultAsync(ReaderMode.PagedLtr);
        var folder = await _t.AddFolderAsync(null, "Tagged Story");
        var a = await PagedAsync(folder, "v01");
        var b = await PagedAsync(folder, "v02");
        var c = await PagedAsync(folder, "v03");
        await MangaAsync(a, 2);
        await MangaAsync(b, 2);
        await MangaAsync(c, 1);
        var minority = await _t.AddFolderAsync(null, "Mixed Tags");
        var d = await PagedAsync(minority, "v01");
        var e = await PagedAsync(minority, "v02");
        await MangaAsync(d, 2);
        await MangaAsync(e, 0);

        var row = Assert.Single((await ListAsync()).Items);
        Assert.Equal(folder.PublicId, row.NodeId);
        Assert.Equal(ReaderMode.PagedRtl, row.SuggestedMode);
        Assert.Equal(ReadingModeSignal.ComicInfo, row.Signal);
        Assert.Equal(ReadingModeConfidence.Medium, row.Confidence);
        Assert.Equal((2, 3), (row.ComicInfoRightToLeft, row.ComicInfoStated));
        Assert.Equal(ReaderMode.PagedLtr, row.CurrentMode);
    }

    [Fact]
    public async Task RightToLeft_IsNotSuggestedOverDoublePage()
    {
        await LibraryDefaultAsync(ReaderMode.DoubleSpread);
        var folder = await _t.AddFolderAsync(null, "Tagged Story");
        await MangaAsync(await PagedAsync(folder, "v01"), 2);

        Assert.Empty((await ListAsync()).Items);
    }

    [Fact]
    public async Task AFolderDefault_OnTheFolderOrAbove_IsNeverSuggested_ButListedWhenItDisagrees()
    {
        await LibraryDefaultAsync(ReaderMode.PagedRtl);
        var category = await _t.AddFolderAsync(null, "Category");
        var below = await _t.AddFolderAsync(category, "Strip Below A Default");
        await TallAsync(below, "c001", 25);
        await FolderDefaultAsync(category, ReaderMode.PagedRtl);
        var own = await _t.AddFolderAsync(null, "Strip With Own Default");
        await TallAsync(own, "c001", 25);
        await FolderDefaultAsync(own, ReaderMode.PagedLtr);
        var agreed = await _t.AddFolderAsync(null, "Strip Already Vertical");
        await TallAsync(agreed, "c001", 25);
        await FolderDefaultAsync(agreed, ReaderMode.VerticalWebtoon);

        var list = await ListAsync();

        Assert.Empty(list.Items);
        Assert.Equal(2, list.SetByAdmin.Count);
        var byName = list.SetByAdmin.ToDictionary(r => r.FolderName);
        Assert.Equal(ReaderModeSource.FolderAbove, byName["Strip Below A Default"].CurrentSource);
        Assert.Equal(ReaderMode.PagedRtl, byName["Strip Below A Default"].CurrentMode);
        Assert.Equal(ReaderModeSource.ThisFolder, byName["Strip With Own Default"].CurrentSource);
        Assert.Equal(ReaderMode.VerticalWebtoon, byName["Strip With Own Default"].SuggestedMode);
    }

    [Fact]
    public async Task ALinkedSeries_WithVolumeSubfolders_IsOneRow()
    {
        await LibraryDefaultAsync(ReaderMode.PagedLtr);
        var series = await _t.AddFolderAsync(null, "Linked Strip Series");
        var s1 = await _t.AddFolderAsync(series, "Season 1");
        var s2 = await _t.AddFolderAsync(series, "Season 2");
        await TallAsync(s1, "c001");
        await TallAsync(s2, "c001");
        await _t.AddLinkAsync(series, await RecordAsync("101", MetadataOrigin.Korea, null), SeriesLinkState.Auto);
        // An unlinked series with subfolders: one row per subfolder.
        var plain = await _t.AddFolderAsync(null, "Unlinked Series");
        await TallAsync(await _t.AddFolderAsync(plain, "Part 1"), "c001", 25);
        await TallAsync(await _t.AddFolderAsync(plain, "Part 2"), "c001", 25);

        var list = await ListAsync();

        var linked = Assert.Single(list.Items, r => r.NodeId == series.PublicId);
        Assert.Equal(2, linked.Subfolders);
        Assert.Equal((30, 30), (linked.MeasuredPages, linked.TallPages));
        Assert.Equal(MetadataOrigin.Korea, linked.LinkedOrigin);
        Assert.Equal(ReadingModeConfidence.High, linked.Confidence);
        Assert.DoesNotContain(list.Items, r => r.NodeId == s1.PublicId || r.NodeId == s2.PublicId);
        Assert.Equal(new[] { "Part 1", "Part 2" }, list.Items.Where(r => r.ParentName == "Unlinked Series").Select(r => r.FolderName).Order().ToArray());
        Assert.Equal(3, list.Items.Count);
    }

    [Fact]
    public async Task TheLinkedRecord_IsOnlyAHint()
    {
        await LibraryDefaultAsync(ReaderMode.PagedLtr);
        var hinted = await _t.AddFolderAsync(null, "Few Pages Manhwa");
        await PagedAsync(hinted, "c001", 5); // too few measured pages to say anything
        await _t.AddLinkAsync(hinted, await RecordAsync("201", MetadataOrigin.Korea, null));
        var contradicted = await _t.AddFolderAsync(null, "Printed Manhwa");
        await PagedAsync(contradicted, "v01", 40);
        await _t.AddLinkAsync(contradicted, await RecordAsync("202", MetadataOrigin.Korea, null));
        var review = await _t.AddFolderAsync(null, "Undecided");
        await PagedAsync(review, "c001", 5);
        await _t.AddLinkAsync(review, await RecordAsync("203", MetadataOrigin.Japan, null), SeriesLinkState.NeedsReview);

        var row = Assert.Single((await ListAsync()).Items);
        Assert.Equal(hinted.PublicId, row.NodeId);
        Assert.Equal(ReaderMode.VerticalWebtoon, row.SuggestedMode);
        Assert.Equal(ReadingModeSignal.LinkedRecord, row.Signal);
        Assert.Equal(ReadingModeConfidence.Low, row.Confidence);
    }

    [Fact]
    public async Task TheSample_IsTheSharedRule_FirstFourHundredMeasuredPages()
    {
        var folder = await _t.AddFolderAsync(null, "Paged Then Strips");
        var first = await PagedAsync(folder, "a", 400);
        var second = await TallAsync(folder, "b", 400);

        var list = await ListAsync();

        // Over all 800 pages half are tall; the shared sample is the first 400 (archive id, page order): none tall.
        Assert.Empty(list.Items);
        Assert.False(await TallStripSampler.TallStripsAsync(_t.Db, [first.Id, second.Id], CancellationToken.None));
    }

    [Fact]
    public async Task TheSample_CutsTheLastArchive_AtItsFirstPages()
    {
        // 190 paged pages, then an archive of 30 paged + 370 tall pages: the sample takes its FIRST 210 (30 paged + 180 tall) -> 180 of
        // 400 tall, not tall strips. Counting the cut archive's tall pages anywhere in it (210) would say tall.
        var folder = await _t.AddFolderAsync(null, "Cut Sample");
        var first = await PagedAsync(folder, "a", 190);
        var second = await _t.AddArchiveAsync(folder, "b");
        for (var i = 0; i < 400; i++)
            _t.Db.PageEntries.Add(new PageEntryEntity
            {
                ItemId = second.Id,
                ContentVersion = 1,
                Ordinal = i,
                EntryKey = new PageEntryKey(i).ToOpaque(),
                SourceEntryLocator = $"p{i}.png",
                MediaType = "image/png",
                Width = 100,
                Height = i < 30 ? 120 : 400,
                ByteSize = 100,
            });
        await _t.Db.SaveChangesAsync();
        // A second folder where the cut keeps the verdict tall: 190 paged, then 400 tall (210 of them in the sample).
        var other = await _t.AddFolderAsync(null, "Cut Sample Tall");
        await PagedAsync(other, "a", 190);
        await TallAsync(other, "b", 400);

        var list = await ListAsync();

        Assert.False(await TallStripSampler.TallStripsAsync(_t.Db, [first.Id, second.Id], CancellationToken.None));
        Assert.DoesNotContain(list.Items, r => r.NodeId == folder.PublicId);
        var row = Assert.Single(list.Items);
        Assert.Equal(other.PublicId, row.NodeId);
        Assert.Equal((400, 210), (row.MeasuredPages, row.TallPages));
        Assert.Equal(ReadingModeConfidence.Medium, row.Confidence);
    }

    [Fact]
    public async Task TombstonedArchivesAndFolders_AreIgnored()
    {
        var folder = await _t.AddFolderAsync(null, "Gone Strips");
        var archive = await TallAsync(folder, "c001", 25);
        await _t.Db.CatalogNodes.Where(n => n.Id == archive.Id).ExecuteUpdateAsync(u => u.SetProperty(n => n.Availability, 5));
        var goneFolder = await _t.AddFolderAsync(null, "Gone Folder");
        await TallAsync(goneFolder, "c001", 25);
        await _t.Db.CatalogNodes.Where(n => n.Id == goneFolder.Id).ExecuteUpdateAsync(u => u.SetProperty(n => n.Availability, 5));

        var list = await ListAsync();
        Assert.Empty(list.Items);
        Assert.Equal(0, list.FoldersChecked);
    }

    [Fact]
    public async Task Apply_WritesTheFolderDefaults_SkipsWhatIsNotAFolder_AndAuditsOnce()
    {
        var a = await _t.AddFolderAsync(null, "Strip A");
        var b = await _t.AddFolderAsync(null, "Strip B");
        await FolderDefaultAsync(b, ReaderMode.PagedLtr);
        var archive = await TallAsync(a, "c001", 25);
        await _t.AddUserAsync("boss", isAdmin: true);

        var (error, result) = await Service().ApplyAsync(new ReadingModeApplyRequest
        {
            Items =
            [
                new() { NodeId = a.PublicId, ReaderMode = ReaderMode.VerticalWebtoon },
                new() { NodeId = b.PublicId, ReaderMode = ReaderMode.VerticalWebtoon },
                new() { NodeId = archive.PublicId, ReaderMode = ReaderMode.VerticalWebtoon },
                new() { NodeId = "nope", ReaderMode = ReaderMode.PagedRtl },
            ],
        }, "boss", CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(2, result!.Applied);
        Assert.Equal(new[] { (archive.PublicId, "not_a_folder"), ("nope", "not_found") },
            result.Skipped.Select(s => (s.NodeId, s.Reason)).ToArray());
        _t.Db.ChangeTracker.Clear();
        var defaults = await _t.Db.FolderReaderDefaults.ToDictionaryAsync(f => f.NodeId, f => f.ReaderMode);
        Assert.Equal((int)ReaderMode.VerticalWebtoon, defaults[a.Id]);
        Assert.Equal((int)ReaderMode.VerticalWebtoon, defaults[b.Id]);
        Assert.Equal(2, defaults.Count);
        var audit = Assert.Single(await _t.Db.AuditEvents.Where(e => e.Action == AuditActions.ReadingModeSuggestionsApply).ToListAsync());
        Assert.Equal("applied_2", audit.Result);
        Assert.Equal(_t.LibraryId, audit.TargetLibraryId);
        Assert.NotNull(audit.ActorUserId);
        // Applied: the folder resolves to the mode now, so it is no longer suggested.
        Assert.Empty((await ListAsync()).Items);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("duplicate")]
    [InlineData("mode")]
    [InlineData("many")]
    public async Task Apply_RefusesABadRequest_AndWritesNothing(string kind)
    {
        var a = await _t.AddFolderAsync(null, "Strip A");
        List<ReadingModeApplyItem> items = kind switch
        {
            "none" => [],
            "duplicate" => [new() { NodeId = a.PublicId, ReaderMode = ReaderMode.PagedRtl }, new() { NodeId = a.PublicId, ReaderMode = ReaderMode.VerticalWebtoon }],
            "mode" => [new() { NodeId = a.PublicId, ReaderMode = (ReaderMode)7 }],
            _ => Enumerable.Range(0, ReadingModeSuggestionService.MaxApplyItems + 1)
                .Select(i => new ReadingModeApplyItem { NodeId = "n" + i, ReaderMode = ReaderMode.PagedRtl }).ToList(),
        };

        var (error, result) = await Service().ApplyAsync(new ReadingModeApplyRequest { Items = items }, null, CancellationToken.None);

        Assert.Equal(kind switch { "none" => "no_items", "duplicate" => "duplicate_items", "mode" => "invalid_mode", _ => "too_many_items" }, error);
        Assert.Null(result);
        Assert.False(await _t.Db.FolderReaderDefaults.AnyAsync());
        Assert.False(await _t.Db.AuditEvents.AnyAsync(e => e.Action == AuditActions.ReadingModeSuggestionsApply));
    }
}
