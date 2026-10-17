namespace com.lifepixer.mangapixer.Tests.Server.Features.VolumeScan;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Features.Metadata.Reach;
using com.lifepixer.mangapixer.Server.Features.VolumeScan;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-with-DB tests for the volume contents scan (1.44.0): a volume file whose pages hold chapter files turns the Volumes view's
/// "~ Volume 8" into "Volume 8" with exactly those chapters; the scope exclusions (owner: "Don't match" folders and everything inside,
/// a folder whose Content is "Doujinshi &amp; adult one-shots", a doujinshi series record, chapter mode, volumes a list maps); resuming
/// after a stop; the switch; nothing re-hashed when nothing changed. The worker is a fake that answers synthetic page hashes.
/// </summary>
public sealed class VolumeContentScannerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-volscan-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly DbContextOptions<MangaPixerDbContext> _options;
    private readonly FakeHasher _hasher = new();
    private readonly Random _random = new(99);

    public VolumeContentScannerTests()
    {
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "volscan.db")))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private sealed class FakeHasher : IVolumePageHasher
    {
        public Dictionary<string, ulong?[]> Pages { get; } = new(StringComparer.Ordinal);
        public List<(string Path, int From)> Calls { get; } = [];
        public Func<PageHashRequest, string?>? FailWith { get; set; }

        public Task<PageHashOutcome> HashAsync(PageHashRequest request, CancellationToken ct)
        {
            Calls.Add((request.ArchivePath, request.FromOrdinal));
            if (FailWith?.Invoke(request) is { } code)
                return Task.FromResult(PageHashOutcome.Failed(code));
            var pages = Pages[request.ArchivePath];
            var to = request.Count == 0 ? pages.Length : Math.Min(pages.Length, request.FromOrdinal + request.Count);
            return Task.FromResult(PageHashOutcome.Ok(new PageHashResult
            {
                JobId = request.JobId,
                PageCount = pages.Length,
                Pages = [.. Enumerable.Range(request.FromOrdinal, to - request.FromOrdinal).Select(i => new PageHashEntry { Ordinal = i, Hash = pages[i] })],
                ObservedLastWriteTicks = request.ExpectedLastWriteTicks,
                ObservedByteLength = request.ExpectedByteLength,
            }));
        }
    }

    private async Task<(MangaPixerDbContext Db, LibraryEntity Library)> SetupAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        return (db, await VolumeTestData.AddLibraryAsync(db));
    }

    private VolumeContentScanner Scanner(MangaPixerDbContext db) =>
        new(db, new VolumeEntryService(db), _hasher, TimeProvider.System, NullLogger<VolumeContentScanner>.Instance);

    private ulong?[] Pages(int count) => [.. Enumerable.Range(0, count).Select(_ => (ulong?)(ulong)_random.NextInt64(long.MinValue, long.MaxValue))];

    /// <summary>Sets an archive's pages (its page count follows) for the fake worker.</summary>
    private async Task PagesOfAsync(MangaPixerDbContext db, LibraryEntity lib, CatalogNodeEntity node, ulong?[] pages)
    {
        _hasher.Pages[Path.Combine(lib.RootPath, node.RelativePath)] = pages;
        var item = await db.ArchiveItems.SingleAsync(a => a.NodeId == node.Id);
        item.PageCount = pages.Length;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A linked series: the list maps volumes 1-6 (8 chapters each, 1-48, a 12-volume series); the folder holds volume 8's file and the
    /// chapter files 49-64. Volume 8's file holds chapters 57-64 (2 cover pages, 6 extra pages at the end).
    /// </summary>
    private async Task<(CatalogNodeEntity Series, CatalogNodeEntity Volume8, Dictionary<int, CatalogNodeEntity> Chapters, MetadataRecordEntity Record)>
        SeedLinkedAsync(MangaPixerDbContext db, LibraryEntity lib)
    {
        var series = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Synthetic Series");
        var volume8 = await VolumeTestData.AddArchiveAsync(db, lib.Id, series.Id, "Synthetic Series v08");
        var chapters = new Dictionary<int, CatalogNodeEntity>();
        var chapterPages = new Dictionary<int, ulong?[]>();
        for (var c = 49; c <= 64; c++)
        {
            chapters[c] = await VolumeTestData.AddArchiveAsync(db, lib.Id, series.Id, $"Synthetic Series - Chapter {c:000}");
            chapterPages[c] = Pages(18);
            await PagesOfAsync(db, lib, chapters[c], chapterPages[c]);
        }
        await PagesOfAsync(db, lib, volume8, [.. Pages(2), .. Enumerable.Range(57, 8).SelectMany(c => chapterPages[c]), .. Pages(6)]);
        var record = await VolumeTestData.AddRecordAsync(db, originVolumes: 12, status: MetadataOriginStatus.Ongoing);
        await VolumeTestData.LinkAsync(db, series, record.Id);
        await VolumeTestData.AddMapAsync(db, record.Id, 1, 12, 8, VolumeMapState.Ok, VolumeMapSource.MangaDexAggregate,
            (1, 1, 8), (2, 9, 16), (3, 17, 24), (4, 25, 32), (5, 33, 40), (6, 41, 48));
        return (series, volume8, chapters, record);
    }

    private static VolumeStack StackOf(FolderVolumeEntries view, string key) =>
        view.Entries.Single(e => e.Kind == VolumeEntryKind.Stack && e.Stack!.Key == key).Stack!;

    [Fact]
    public async Task A_VolumeFileHoldingChapters57to64_TurnsKnown_WithExactlyThoseChapters_AndBoundsVolume7()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var (series, volume8, chapters, _) = await SeedLinkedAsync(db, lib);
        var before = (await new VolumeEntryService(db).GetEntriesAsync(series.Id, default))!;
        Assert.Equal("~ Volume 8", StackOf(before, "8").Label);

        var outcome = await Scanner(db).ScanFolderAsync(series.Id, () => true, default);

        Assert.Equal(FolderScanOutcome.Scanned, outcome);
        var row = await db.VolumeContents.SingleAsync();
        Assert.Equal((volume8.Id, (int)VolumeContentRowState.Known), (row.VolumeNodeId, row.State));
        Assert.Equal(Enumerable.Range(57, 8).Select(c => chapters[c].Id), VolumeContentReader.ParseChapters(row.ChaptersJson).Select(c => c.NodeId));

        var after = (await new VolumeEntryService(db).GetEntriesAsync(series.Id, default))!;
        var stack8 = StackOf(after, "8");
        Assert.Equal("Volume 8", stack8.Label);
        Assert.Equal(Enumerable.Range(57, 8).Select(c => (decimal)c), stack8.InVolumeFile);
        Assert.Equal(new InVolumeFileDto { From = "57", To = "64", Count = 8 }, VolumeContentLine.Of(stack8));
        // Volume 7 lies between the listed 6 and the scanned 8: no longer an estimate.
        Assert.Equal("Volume 7", StackOf(after, "7").Label);
        Assert.Equal(8, StackOf(after, "7").PresentCount);

        // Progress, Missing and the export read the same answer (SeriesProgressLoader).
        var progress = (await new SeriesProgressLoader(db).LoadAsync([new SeriesProgressTarget(series.Id, (await db.MetadataRecords.SingleAsync()).Id)], default))[series.Id];
        var scanned = Assert.Single(progress.Result.ScannedVolumes);
        Assert.True(scanned.Known);
        Assert.Equal(Enumerable.Range(57, 8).Select(c => (decimal)c), scanned.Chapters.Select(c => c.Chapter));
        Assert.DoesNotContain(progress.Result.EstimatedVolumes, e => e.Volume == 8m);
    }

    [Fact]
    public async Task A_SecondRun_WithNothingChanged_IsUpToDate_AndHashesNothing()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var (series, _, _, _) = await SeedLinkedAsync(db, lib);
        await Scanner(db).ScanFolderAsync(series.Id, () => true, default);
        var calls = _hasher.Calls.Count;

        Assert.Equal(FolderScanOutcome.UpToDate, await Scanner(db).ScanFolderAsync(series.Id, () => true, default));
        Assert.Equal(calls, _hasher.Calls.Count);
    }

    [Fact]
    public async Task A_ChangedChapterFile_IsMatchedAgain_FromTheStoredHashes_OfTheUnchangedVolume()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var (series, volume8, chapters, _) = await SeedLinkedAsync(db, lib);
        await Scanner(db).ScanFolderAsync(series.Id, () => true, default);
        var changed = await db.ArchiveItems.SingleAsync(a => a.NodeId == chapters[60].Id);
        changed.ContentVersion = 2;
        await db.SaveChangesAsync();
        _hasher.Calls.Clear();

        Assert.Equal(FolderScanOutcome.Scanned, await Scanner(db).ScanFolderAsync(series.Id, () => true, default));
        Assert.Equal([Path.Combine(lib.RootPath, chapters[60].RelativePath)], _hasher.Calls.Select(c => c.Path).Distinct());
        Assert.DoesNotContain(_hasher.Calls, c => c.Path.EndsWith(volume8.RelativePath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_StoppedScan_ResumesAtTheNextChunk()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var (series, volume8, _, _) = await SeedLinkedAsync(db, lib);
        var volumePath = Path.Combine(lib.RootPath, volume8.RelativePath);
        // The volume has 152 pages: the second chunk is refused (the worker busy).
        _hasher.FailWith = r => r.ArchivePath == volumePath && r.FromOrdinal == PageHashLimits.ChunkPages ? "busy" : null;

        Assert.Equal(FolderScanOutcome.Stopped, await Scanner(db).ScanFolderAsync(series.Id, () => true, default));
        var partial = await db.ArchivePageHashes.AsNoTracking().SingleAsync(h => h.NodeId == volume8.Id);
        Assert.Equal(PageHashLimits.ChunkPages, partial.HashedPages);
        Assert.Empty(await db.VolumeContents.ToListAsync());

        _hasher.FailWith = null;
        _hasher.Calls.Clear();
        Assert.Equal(FolderScanOutcome.Scanned, await Scanner(db).ScanFolderAsync(series.Id, () => true, default));
        Assert.Equal(PageHashLimits.ChunkPages, _hasher.Calls.First(c => c.Path == volumePath).From);
    }

    [Fact]
    public async Task Switched_Off_NoRequestIsSent()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var (series, _, _, _) = await SeedLinkedAsync(db, lib);

        Assert.Equal(FolderScanOutcome.Stopped, await Scanner(db).ScanFolderAsync(series.Id, () => false, default));
        Assert.Empty(_hasher.Calls);
    }

    [Fact]
    public async Task A_DontMatchFolder_AndEverythingInsideIt_IsNeverScanned()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var parent = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Collection");
        await VolumeTestData.LinkAsync(db, parent, null, SeriesLinkState.DontMatch);
        var inside = await SeedUnlinkedPairAsync(db, lib, parent.Id);
        var marked = await SeedUnlinkedPairAsync(db, lib, null);
        await VolumeTestData.LinkAsync(db, marked, null, SeriesLinkState.DontMatch);

        Assert.Equal(FolderScanOutcome.OutOfScope, await Scanner(db).ScanFolderAsync(inside.Id, () => true, default));
        Assert.Equal(FolderScanOutcome.OutOfScope, await Scanner(db).ScanFolderAsync(marked.Id, () => true, default));
        Assert.Empty(_hasher.Calls);
    }

    [Fact]
    public async Task A_FolderWhoseContentIsDoujinshi_IsNeverScanned()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var parent = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Doujinshi");
        db.FolderMetadataContents.Add(new FolderMetadataContentEntity
        {
            NodeId = parent.Id,
            Content = (int)MetadataFolderContent.DoujinshiAndAdultOneShots,
        });
        await db.SaveChangesAsync();
        var folder = await SeedUnlinkedPairAsync(db, lib, parent.Id);

        Assert.Equal(FolderScanOutcome.OutOfScope, await Scanner(db).ScanFolderAsync(folder.Id, () => true, default));
        Assert.Empty(_hasher.Calls);
    }

    [Fact]
    public async Task A_SeriesWhoseRecordIsADoujinshi_IsNeverScanned()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var folder = await SeedUnlinkedPairAsync(db, lib, null);
        var record = await VolumeTestData.AddRecordAsync(db);
        record.Format = (int)MetadataFormat.Doujinshi;
        await db.SaveChangesAsync();
        await VolumeTestData.LinkAsync(db, folder, record.Id);

        Assert.Equal(FolderScanOutcome.OutOfScope, await Scanner(db).ScanFolderAsync(folder.Id, () => true, default));
        Assert.Empty(_hasher.Calls);
    }

    [Fact]
    public async Task A_WebtoonInChapterMode_IsNeverScanned()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var folder = await SeedUnlinkedPairAsync(db, lib, null);
        var record = await VolumeTestData.AddRecordAsync(db);
        record.Origin = (int)MetadataOrigin.Korea;
        record.Webtoon = true;
        await db.SaveChangesAsync();
        await VolumeTestData.LinkAsync(db, folder, record.Id);
        await VolumeTestData.AddNearEmptyMapAsync(db, record.Id, 11);

        var view = (await new VolumeEntryService(db).GetEntriesAsync(folder.Id, default))!;
        Assert.True(view.ChaptersOnly);
        Assert.Equal(FolderScanOutcome.OutOfScope, await Scanner(db).ScanFolderAsync(folder.Id, () => true, default));
        Assert.Empty(_hasher.Calls);
    }

    [Fact]
    public async Task A_VolumeTheListMaps_IsNeverScanned()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var folder = await SeedUnlinkedPairAsync(db, lib, null);
        var record = await VolumeTestData.AddRecordAsync(db);
        await VolumeTestData.LinkAsync(db, folder, record.Id);
        // The list maps volume 2 (chapters 5-8): its file is never scanned, even though chapters 9-11 are after the list (candidates).
        await VolumeTestData.AddMapAsync(db, record.Id, 1, null, null, VolumeMapState.Ok, VolumeMapSource.MangaDexAggregate, (1, 1, 4), (2, 5, 8));

        Assert.Equal(FolderScanOutcome.OutOfScope, await Scanner(db).ScanFolderAsync(folder.Id, () => true, default));
        Assert.Empty(_hasher.Calls);
    }

    [Fact]
    public async Task An_UnlinkedFolder_IsScanned_AndItsVolumeFileStacksTheChaptersFoundInside()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var folder = await SeedUnlinkedPairAsync(db, lib, null);

        Assert.Equal(FolderScanOutcome.Scanned, await Scanner(db).ScanFolderAsync(folder.Id, () => true, default));
        var view = (await new VolumeEntryService(db).GetEntriesAsync(folder.Id, default))!;
        var stack = StackOf(view, "2");
        Assert.Equal("Volume 2", stack.Label);
        Assert.Equal([9m, 10m], stack.InVolumeFile);
        Assert.True(stack.HasVolumeArchive);
    }

    /// <summary>An unlinked folder: <c>v02</c> holds chapters 9 and 10 (20 pages each), chapter 11 is not in it.</summary>
    private async Task<CatalogNodeEntity> SeedUnlinkedPairAsync(MangaPixerDbContext db, LibraryEntity lib, long? parentId)
    {
        var folder = await VolumeTestData.AddFolderAsync(db, lib.Id, parentId, "Pair " + Guid.NewGuid().ToString("N")[..6]);
        var volume = await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, "Pair v02");
        var c9 = Pages(20);
        var c10 = Pages(20);
        await PagesOfAsync(db, lib, await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, "Pair - Chapter 009"), c9);
        await PagesOfAsync(db, lib, await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, "Pair - Chapter 010"), c10);
        await PagesOfAsync(db, lib, await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, "Pair - Chapter 011"), Pages(20));
        await PagesOfAsync(db, lib, volume, [.. Pages(3), .. c9, .. c10, .. Pages(4)]);
        return folder;
    }

    [Fact]
    public async Task TheBrowseCompletionFilter_ReadsTheSameAnswer_AsTheScanAwareProgress()
    {
        // 1.44.0 merge seam (lanes V + C): the browse filter answers a level with the same SeriesProgressLoader that now carries the scan's
        // results. By design the scan never changes the answer itself (SeriesAnswers reads held vs released numbers; the scan only places
        // chapters and makes a volume's range known) - this proves the two paths agree on a scanned series and the filter keeps it.
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var (series, _, _, record) = await SeedLinkedAsync(db, lib);
        await Scanner(db).ScanFolderAsync(series.Id, () => true, default);
        var progress = (await new SeriesProgressLoader(db).LoadAsync([new SeriesProgressTarget(series.Id, record.Id)], default))[series.Id];
        Assert.True(Assert.Single(progress.Result.ScannedVolumes).Known);
        var answer = progress.Result.Answer;
        var user = await VolumeTestData.AddUserAsync(db);

        var browse = new CatalogBrowseService(db, new Server.Features.Auth.LibraryAuthorizationService(db));
        var page = await browse.BrowseAsync(user.Id, lib.Id, null, null, completion: answer);
        Assert.Equal(1, page.LinkedSeries);
        Assert.Equal(series.PublicId, Assert.Single(page.Items).Id);
        var counts = page.CompletionCounts!;
        Assert.Equal(1, counts.HaveItAll + counts.FinishedMissing + counts.UpToDate + counts.MissingSome + counts.CantTell);
        var other = Enum.GetValues<SeriesAnswer>().First(a => a != answer);
        Assert.Empty((await browse.BrowseAsync(user.Id, lib.Id, null, null, completion: other)).Items);
    }
}
