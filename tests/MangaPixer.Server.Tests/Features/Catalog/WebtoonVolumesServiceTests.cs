namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Features.Metadata.Reach;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests for 1.34.0 lane V (owner report 2026-10-04): a webtoon folder named <c>&lt;index&gt; [&lt;chapter&gt; - &lt;title&gt;]</c>
/// with a near-empty MangaDex list - the Volumes view entries, the series header numbers and the map every reader gets. Synthetic rows.
/// </summary>
public sealed class WebtoonVolumesServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public WebtoonVolumesServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-webtoon-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "webtoon.db")))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPixerDbContext Db, LibraryEntity Library)> SetupAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        return (db, await VolumeTestData.AddLibraryAsync(db));
    }

    /// <summary>A linked series folder: chapters 0, 0.5, 1..<paramref name="last"/> (minus <paramref name="without"/>), a near-empty map, 12 English volumes.</summary>
    private static async Task<(CatalogNodeEntity Folder, MetadataRecordEntity Record)> SeedAsync(
        MangaPixerDbContext db, LibraryEntity lib, MetadataOrigin origin, bool? webtoon, int last, params int[] without)
    {
        var folder = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Synthetic Webtoon");
        await VolumeTestData.AddIndexedChaptersAsync(db, lib.Id, folder.Id, last, without);
        var record = await VolumeTestData.AddRecordAsync(db, status: MetadataOriginStatus.Ongoing, englishVolumes: 12);
        record.Origin = (int)origin;
        record.Webtoon = webtoon;
        record.LatestChapter = last + 2;
        await db.SaveChangesAsync();
        await VolumeTestData.LinkAsync(db, folder, record.Id);
        await VolumeTestData.AddNearEmptyMapAsync(db, record.Id, last);
        return (folder, record);
    }

    [Fact]
    public async Task AWebtoonWithANearEmptyList_ListsItsChaptersInOrder_WithNoVolumePlaceholders()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var (folder, _) = await SeedAsync(db, lib, MetadataOrigin.Korea, webtoon: true, last: 40, without: 17);

        var view = await new VolumeEntryService(db).GetEntriesAsync(folder.Id, default);

        Assert.NotNull(view);
        Assert.True(view.ChaptersOnly);
        Assert.True(view.Available); // the chapter list with the series header
        Assert.Equal(0, view.StackCount);
        Assert.DoesNotContain(view.Entries, e => e.Kind is VolumeEntryKind.MissingVolume or VolumeEntryKind.Stack);
        // Chapter order: 0, 0.5, 1, 2, ... (the bracketed numbers), 41 files.
        Assert.Equal(41, view.Entries.Count);
        Assert.Equal(["0001 [0000].cbz", "0002 [0000.5].cbz", "0003 [0001 - Some Title].cbz"], view.Entries.Take(3).Select(e => e.Row!.Name));
        Assert.Equal("0042 [0040 - Some Title].cbz", view.Entries[^1].Row!.Name);

        // The header: chapters, never "11 volumes missing" - chapter 17 is a hole, 41 and 42 are released after the last one here.
        var status = view.Status!;
        Assert.Equal(0, status.MissingVolumes);
        var progress = status.Progress!;
        Assert.Equal(0, progress.MissingVolumes);
        Assert.Equal(3, progress.MissingChapters);
        Assert.Empty(progress.Reach!.VolumeFiles);
        Assert.Equal(40, progress.Reach.Chapters[^1].To);
        Assert.Empty(progress.UpgradeVolumes);
    }

    [Fact]
    public async Task AMangaWithANearEmptyList_HasNoListEither_ButStaysOutOfChapterMode()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var (folder, record) = await SeedAsync(db, lib, MetadataOrigin.Japan, webtoon: false, last: 30);

        var maps = await db.SeriesVolumeMaps.AsNoTracking().Where(m => m.RecordId == record.Id).ToListAsync();
        // One rule, read at load time: the stored map stops counting without a new request.
        Assert.False(VolumeMapService.IsUsable(maps[0]));
        Assert.False(VolumeMapService.HasVolumeList(maps[0]));
        Assert.Null(VolumeMapService.UsableMangaDexMap(maps));
        Assert.Empty(SeriesProgressLoader.ExactList(maps).Volumes);
        var (map, _) = SeriesProgressLoader.MapAndFacts(maps, null, "en");
        Assert.False(map.ChaptersOnly);
        Assert.Empty(map.Volumes);
        Assert.Null(map.ChaptersPerVolume); // not the degenerate list's 1.0

        var view = await new VolumeEntryService(db).GetEntriesAsync(folder.Id, default);
        Assert.False(view!.ChaptersOnly);
        Assert.Equal(0, view.StackCount);
        Assert.Equal(0, view.Status!.MissingVolumes); // was 11 (volumes 2-12) before 1.34.0
    }

    [Fact]
    public async Task AManhwaWithARealList_KeepsItsVolumeStacks()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var folder = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Synthetic Manhwa");
        await VolumeTestData.AddIndexedChaptersAsync(db, lib.Id, folder.Id, 20);
        var record = await VolumeTestData.AddRecordAsync(db);
        record.Origin = (int)MetadataOrigin.Korea;
        await db.SaveChangesAsync();
        await VolumeTestData.LinkAsync(db, folder, record.Id);
        await VolumeTestData.AddMapAsync(db, record.Id, 1, null, null, VolumeMapState.Ok, VolumeMapSource.MangaDexAggregate, (1, 1, 10), (2, 11, 20));

        var view = await new VolumeEntryService(db).GetEntriesAsync(folder.Id, default);

        Assert.False(view!.ChaptersOnly);
        Assert.Equal(2, view.StackCount);
        Assert.Equal(["1", "2"], view.Entries.Where(e => e.Kind == VolumeEntryKind.Stack).Select(e => e.Stack!.Key));
    }

    [Fact]
    public async Task AWebtoonWithAWikipediaList_KeepsItsVolumeStacks()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var (folder, record) = await SeedAsync(db, lib, MetadataOrigin.Korea, webtoon: true, last: 20);
        await VolumeTestData.AddMapAsync(db, record.Id, 1, null, null, VolumeMapState.Ok, VolumeMapSource.WikipediaList, (1, 1, 10), (2, 11, 20));

        var view = await new VolumeEntryService(db).GetEntriesAsync(folder.Id, default);

        Assert.False(view!.ChaptersOnly);
        Assert.Equal(2, view.StackCount);
    }
}
