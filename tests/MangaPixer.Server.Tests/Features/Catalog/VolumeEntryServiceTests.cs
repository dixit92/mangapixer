namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

/// <summary>
/// Service-with-DB tests for the Volumes view's entry list (1.29.0, P2.3 / P2.4): the consolidated series view (generic unit
/// subfolders merged, their cards hidden, <c>Season N</c> kept), restarts, unlinked folders, the stored volume map, ComicInfo,
/// the memoisation and the toggle chain. Synthetic rows only.
/// </summary>
public sealed class VolumeEntryServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public VolumeEntryServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-volentries-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "volentries.db")))
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

    private static string[] Kinds(FolderVolumeEntries view) =>
        view.Entries.Select(e => e.Kind switch
        {
            VolumeEntryKind.Stack => "stack:" + e.Stack!.Key,
            VolumeEntryKind.MissingVolume => "missing:" + VolumeGrouping.KeyOf(e.Volume!.Value),
            VolumeEntryKind.Folder => "folder:" + e.Row!.Name,
            _ => "archive:" + e.Row!.Name,
        }).ToArray();

    // Series/ (linked) holds Volumes/ (v01, v02), Chapters/ (17-28), Season 2/ and Extras/ subfolders; the map places 1-8 in
    // volume 1, 9-16 in 2, 17-24 in 3, 25-32 in 4.
    private static async Task<(CatalogNodeEntity Series, CatalogNodeEntity Volumes, CatalogNodeEntity Chapters)> SeedSeriesAsync(
        MangaPixerDbContext db, LibraryEntity lib, MetadataOriginStatus status = MetadataOriginStatus.Complete)
    {
        var series = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Synthetic Series");
        var volumes = await VolumeTestData.AddFolderAsync(db, lib.Id, series.Id, "Volumes");
        var chapters = await VolumeTestData.AddFolderAsync(db, lib.Id, series.Id, "Chapters");
        await VolumeTestData.AddFolderAsync(db, lib.Id, series.Id, "Season 2");
        await VolumeTestData.AddFolderAsync(db, lib.Id, series.Id, "Extras");
        await VolumeTestData.AddArchiveAsync(db, lib.Id, volumes.Id, "Synthetic Series v01");
        await VolumeTestData.AddArchiveAsync(db, lib.Id, volumes.Id, "Synthetic Series v02");
        await VolumeTestData.AddChaptersAsync(db, lib.Id, chapters.Id, "Synthetic Series - Chapter ", 17, 28);
        var record = await VolumeTestData.AddRecordAsync(db, status: status);
        await VolumeTestData.LinkAsync(db, series, record.Id);
        await VolumeTestData.AddMapAsync(db, record.Id, 1, null, null, VolumeMapState.Ok, VolumeMapSource.MangaDexAggregate,
            (1, 1, 8), (2, 9, 16), (3, 17, 24), (4, 25, 32));
        return (series, volumes, chapters);
    }

    [Fact]
    public async Task LinkedSeries_MergesItsVolumesAndChaptersFolders_IntoOneVolumeOrderedList()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var (series, volumes, _) = await SeedSeriesAsync(db, lib);

        var view = await new VolumeEntryService(db).GetEntriesAsync(series.Id, default);

        Assert.NotNull(view);
        Assert.True(view!.Available);
        Assert.True(view.Consolidated);
        // Volume archives and stacks by volume number; the unmerged subfolders after them; the merged folders' cards are hidden.
        Assert.Equal(
            ["archive:Synthetic Series v01", "archive:Synthetic Series v02", "stack:3", "stack:4", "folder:Extras", "folder:Season 2"],
            Kinds(view));
        Assert.Equal(["Extras", "Season 2"], view.Entries.Where(e => e.Kind == VolumeEntryKind.Folder).Select(e => e.Row!.Name).Order());
        // The rows keep their REAL parent (the reader's neighbours and "open containing folder" follow it).
        var v1 = view.Rows.Values.Single(r => r.DisplayName == "Synthetic Series v01");
        Assert.Equal(volumes.PublicId, v1.ParentId);
        var stack3 = view.Entries.Single(e => e.Kind == VolumeEntryKind.Stack && e.Stack!.Key == "3").Stack!;
        Assert.Equal(8, stack3.PresentCount);
        Assert.Empty(stack3.MissingChapters);
        // Volume 4 lists 25-32, only 25-28 are on disk and nothing later is: without a released list nothing is marked missing.
        var stack4 = view.Entries.Single(e => e.Kind == VolumeEntryKind.Stack && e.Stack!.Key == "4").Stack!;
        Assert.Empty(stack4.MissingChapters);
        Assert.Equal(series.Id, view.SeriesFolderId);
    }

    [Fact]
    public async Task TrailingChapters_AreMissingOnlyWhenReleasedInThePreferredLanguage()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var (series, _, _) = await SeedSeriesAsync(db, lib, MetadataOriginStatus.Ongoing);
        var map = await db.SeriesVolumeMaps.SingleAsync();
        var service = new VolumeEntryService(db);
        IReadOnlyList<decimal> Missing4(FolderVolumeEntries v) => v.Entries.Single(e => e.Kind == VolumeEntryKind.Stack && e.Stack!.Key == "4").Stack!.MissingChapters;

        // Released in English through chapter 30 (the preferred language defaults to English): 29 and 30 are missing, 31-32 not yet.
        await VolumeTestData.SetReleasedAsync(db, map, "en", 30);
        var english = (await service.GetEntriesAsync(series.Id, default))!;
        Assert.Equal([29m, 30m], Missing4(english));
        Assert.Equal((2, true, "en"), (english.Status!.MissingChapters, english.Status.ReleaseKnown, english.Status.Language));

        // Another preferred language: the English list says nothing about it.
        await VolumeTestData.SetPreferredLanguageAsync(db, "fr");
        var french = (await service.GetEntriesAsync(series.Id, default))!;
        Assert.Empty(Missing4(french));
        Assert.False(french.Status!.ReleaseKnown);
        Assert.Equal("fr", french.Status.Language);

        // A list read for French counts.
        await VolumeTestData.SetReleasedAsync(db, map, "fr", 29);
        Assert.Equal([29m], Missing4((await service.GetEntriesAsync(series.Id, default))!));
    }

    [Fact]
    public async Task AVolumesOnlyLinkedFolder_ShowsMissingVolumes_AgainstTheEnglishTotal()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var folder = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Volumes Only");
        foreach (var v in new[] { 1, 2, 4 })
            await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, $"Volumes Only v{v:00}");
        var record = await VolumeTestData.AddRecordAsync(db, status: MetadataOriginStatus.Ongoing, originVolumes: 12, englishVolumes: 6);
        await VolumeTestData.LinkAsync(db, folder, record.Id);
        var service = new VolumeEntryService(db);

        var view = (await service.GetEntriesAsync(folder.Id, default))!;

        Assert.True(view.Available);
        Assert.Equal(["archive:Volumes Only v01", "archive:Volumes Only v02", "missing:3", "archive:Volumes Only v04", "missing:5", "missing:6"], Kinds(view));
        Assert.Equal(new SeriesStatusInfo(MetadataOriginStatus.Ongoing, 3, 0, true, "en"), view.Status);

        // French: no volume total is known for it - only the gap below volume 4, never the untranslated origin volumes.
        await VolumeTestData.SetPreferredLanguageAsync(db, "fr");
        var french = (await service.GetEntriesAsync(folder.Id, default))!;
        Assert.Equal(["archive:Volumes Only v01", "archive:Volumes Only v02", "missing:3", "archive:Volumes Only v04"], Kinds(french));
        Assert.Equal(new SeriesStatusInfo(MetadataOriginStatus.Ongoing, 1, 0, false, "fr"), french.Status);
    }

    [Fact]
    public async Task AnUpToDateVolumesOnlyFolder_StillHasAVolumesView_ForItsStatus()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var folder = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Complete Set");
        foreach (var v in new[] { 1, 2, 3 })
            await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, $"Complete Set v{v:00}");
        var record = await VolumeTestData.AddRecordAsync(db, status: MetadataOriginStatus.Complete, englishVolumes: 3);
        await VolumeTestData.LinkAsync(db, folder, record.Id);

        var view = (await new VolumeEntryService(db).GetEntriesAsync(folder.Id, default))!;

        Assert.True(view.Available);
        Assert.Equal(0, view.StackCount);
        Assert.Equal(new SeriesStatusInfo(MetadataOriginStatus.Complete, 0, 0, true, "en"), view.Status);

        // The same folder without a link: nothing to show beyond the folder list.
        var plain = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Unlinked Set");
        foreach (var v in new[] { 1, 2, 4 })
            await VolumeTestData.AddArchiveAsync(db, lib.Id, plain.Id, $"Unlinked Set v{v:00}");
        var unlinked = (await new VolumeEntryService(db).GetEntriesAsync(plain.Id, default))!;
        Assert.False(unlinked.Available);
        Assert.Null(unlinked.Status);
        Assert.DoesNotContain(unlinked.Entries, e => e.Kind == VolumeEntryKind.MissingVolume);
    }

    [Fact]
    public async Task NumberingThatRestarts_MergesNothing_AndTheFolderCardStays()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var series = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Restarting Series");
        var chapters = await VolumeTestData.AddFolderAsync(db, lib.Id, series.Id, "Chapters");
        await VolumeTestData.AddChaptersAsync(db, lib.Id, series.Id, "Restarting Series - Chapter ", 1, 3);
        await VolumeTestData.AddChaptersAsync(db, lib.Id, chapters.Id, "Restarting Series - Chapter ", 1, 3);
        var record = await VolumeTestData.AddRecordAsync(db);
        await VolumeTestData.LinkAsync(db, series, record.Id);

        var view = await new VolumeEntryService(db).GetEntriesAsync(series.Id, default);

        Assert.False(view!.Consolidated);
        Assert.False(view.Available);
        Assert.Contains(view.Entries, e => e.Kind == VolumeEntryKind.Folder && e.Row!.Id == chapters.PublicId);
    }

    [Fact]
    public async Task ASubfolderWithItsOwnLink_IsASeparateWork_AndNeverMerges()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var (series, volumes, _) = await SeedSeriesAsync(db, lib);
        var other = await VolumeTestData.AddRecordAsync(db, "Other Record");
        await VolumeTestData.LinkAsync(db, volumes, other.Id);

        var view = await new VolumeEntryService(db).GetEntriesAsync(series.Id, default);

        Assert.Contains(view!.Entries, e => e.Kind == VolumeEntryKind.Folder && e.Row!.Id == volumes.PublicId);
        Assert.DoesNotContain(view.Rows.Values, r => r.DisplayName == "Synthetic Series v01");
    }

    [Fact]
    public async Task AGenericFolderHoldingAnotherFolder_KeepsItsCard()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var (series, volumes, _) = await SeedSeriesAsync(db, lib);
        await VolumeTestData.AddFolderAsync(db, lib.Id, volumes.Id, "Bonus Art");

        var view = await new VolumeEntryService(db).GetEntriesAsync(series.Id, default);

        // Merging Volumes/ would hide its subfolder: the card stays, Chapters/ still merges.
        Assert.Contains(view!.Entries, e => e.Kind == VolumeEntryKind.Folder && e.Row!.Id == volumes.PublicId);
        Assert.DoesNotContain(view.Entries, e => e.Kind == VolumeEntryKind.Folder && e.Row!.Name == "Chapters");
        Assert.True(view.Consolidated);
    }

    [Fact]
    public async Task AnUnlinkedFolder_NeverConsolidates_ButItsOwnNamedVolumesGroup()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var folder = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Unlinked Series");
        var volumes = await VolumeTestData.AddFolderAsync(db, lib.Id, folder.Id, "Volumes");
        await VolumeTestData.AddArchiveAsync(db, lib.Id, volumes.Id, "Unlinked v01");
        await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, "Unlinked v02 c005");
        await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, "Unlinked v02 c006");

        var view = await new VolumeEntryService(db).GetEntriesAsync(folder.Id, default);

        Assert.False(view!.Consolidated);
        Assert.True(view.Available);
        Assert.Equal(["stack:2", "folder:Volumes"], Kinds(view));
        Assert.Contains(view.Entries, e => e.Kind == VolumeEntryKind.Folder && e.Row!.Id == volumes.PublicId);
        Assert.Equal(1, view.StackCount);
    }

    [Fact]
    public async Task APlainFolderOfUnnumberedArchives_HasNoVolumesView()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var folder = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Plain");
        await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, "Alpha");
        await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, "Beta");

        var view = await new VolumeEntryService(db).GetEntriesAsync(folder.Id, default);

        Assert.False(view!.Available);
        Assert.Equal(0, view.StackCount);
    }

    [Fact]
    public async Task ANonFolderOrUnknownNode_HasNoEntries()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var archive = await VolumeTestData.AddArchiveAsync(db, lib.Id, null, "Root Archive");
        var service = new VolumeEntryService(db);

        Assert.Null(await service.GetEntriesAsync(archive.Id, default));
        Assert.Null(await service.GetEntriesAsync(999_999, default));
    }

    [Fact]
    public async Task ASeasonSubfolder_GroupsWithTheParentsMap_UnlessTheSeriesRestarts()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var series = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Seasonal Series");
        var season1 = await VolumeTestData.AddFolderAsync(db, lib.Id, series.Id, "Season 1");
        var season2 = await VolumeTestData.AddFolderAsync(db, lib.Id, series.Id, "Season 2");
        await VolumeTestData.AddChaptersAsync(db, lib.Id, season1.Id, "Seasonal - Chapter ", 1, 40);
        await VolumeTestData.AddChaptersAsync(db, lib.Id, season2.Id, "Seasonal - Chapter ", 41, 50);
        var record = await VolumeTestData.AddRecordAsync(db, status: MetadataOriginStatus.Complete);
        await VolumeTestData.LinkAsync(db, series, record.Id);
        await VolumeTestData.AddMapAsync(db, record.Id, 1, null, null, VolumeMapState.Ok, VolumeMapSource.MangaDexAggregate,
            (4, 31, 40), (5, 41, 50));
        var service = new VolumeEntryService(db);

        var view = await service.GetEntriesAsync(season2.Id, default);
        Assert.True(view!.Available);
        var stack = Assert.Single(view.Entries, e => e.Kind == VolumeEntryKind.Stack).Stack!;
        Assert.Equal("5", stack.Key);
        Assert.Equal(10, stack.PresentCount);
        Assert.Equal(series.Id, view.SeriesFolderId);
        // A Season subfolder holds part of the run: no missing-volume placeholders (volumes 1-4 are elsewhere) and no status line.
        Assert.DoesNotContain(view.Entries, e => e.Kind == VolumeEntryKind.MissingVolume);
        Assert.Null(view.Status);
        // The series folder keeps Season 1 / Season 2 as folders (never merged) and groups nothing of its own.
        var parent = await service.GetEntriesAsync(series.Id, default);
        Assert.False(parent!.Consolidated);
        Assert.Equal(["folder:Season 1", "folder:Season 2"], Kinds(parent));

        // Season 1 restarting from chapter 1 again in a third season: no season groups.
        var season3 = await VolumeTestData.AddFolderAsync(db, lib.Id, series.Id, "Season 3");
        await VolumeTestData.AddChaptersAsync(db, lib.Id, season3.Id, "Seasonal - Chapter ", 1, 10);
        db.Libraries.Single(l => l.Id == lib.Id).CatalogRevision++;
        await db.SaveChangesAsync();
        Assert.False((await service.GetEntriesAsync(season2.Id, default))!.Available);
        Assert.False((await service.GetEntriesAsync(season3.Id, default))!.Available);
    }

    [Fact]
    public async Task OnlyAnOkMapCounts_AndARatioAloneEstimatesVolumes()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var folder = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Mapped");
        await VolumeTestData.AddChaptersAsync(db, lib.Id, folder.Id, "Mapped - Chapter ", 1, 25);
        var record = await VolumeTestData.AddRecordAsync(db, status: MetadataOriginStatus.Ongoing, originVolumes: 3);
        await VolumeTestData.LinkAsync(db, folder, record.Id);
        // A foreign-numbered (Empty) MangaDex map is ignored: nothing groups from it.
        await VolumeTestData.AddMapAsync(db, record.Id, 1, 46, null, VolumeMapState.Empty, VolumeMapSource.MangaDexAggregate, (46, 1, 25));
        var service = new VolumeEntryService(db);

        Assert.False((await service.GetEntriesAsync(folder.Id, default))!.Available);

        // The AniList ratio row alone estimates volumes (capped by the record's volume total).
        await VolumeTestData.AddMapAsync(db, record.Id, 1, null, 10, VolumeMapState.Ok, VolumeMapSource.AniListRatio);
        var view = await service.GetEntriesAsync(folder.Id, default);
        Assert.True(view!.Available);
        Assert.Equal([10, 10, 5], view.Entries.Select(e => e.Stack!.PresentCount));
        Assert.All(view.Entries, e => Assert.Equal(com.lifepixer.mangapixer.Core.Api.VolumeStackConfidence.Estimated, e.Stack!.Confidence));
        Assert.Equal(com.lifepixer.mangapixer.Core.Api.VolumeListSource.AniList, view.Entries[0].Stack!.Source);
    }

    [Fact]
    public async Task ComicInfoVolume_GroupsAChapterWhoseNameStatesNone()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var folder = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "ComicInfo Folder");
        foreach (var (name, volume) in new[] { ("Untitled Book A", 1), ("Untitled Book B", 1), ("Untitled Book C", 2) })
        {
            var node = await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, name);
            await VolumeTestData.AddComicInfoAsync(db, node.Id, volume, name[^1] == 'A' ? "5" : name[^1] == 'B' ? "6" : "7");
        }

        var view = await new VolumeEntryService(db).GetEntriesAsync(folder.Id, default);

        Assert.NotNull(view);
        Assert.Equal(["stack:1", "stack:2"], Kinds(view));
        Assert.Equal("5", view.Entries[0].Stack!.FirstChapter);
        Assert.Equal("6", view.Entries[0].Stack!.LastChapter);
    }

    [Fact]
    public async Task TheEntryList_IsMemoisedPerCatalogRevision_AndPerMapVersion()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var (series, _, chapters) = await SeedSeriesAsync(db, lib);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new VolumeEntryService(db, cache);

        var first = await service.GetEntriesAsync(series.Id, default);
        Assert.Same(first, await service.GetEntriesAsync(series.Id, default));

        // A new archive without a new catalog revision: still the cached list (a scan bumps the revision).
        await VolumeTestData.AddArchiveAsync(db, lib.Id, chapters.Id, "Synthetic Series - Chapter 029");
        Assert.Same(first, await service.GetEntriesAsync(series.Id, default));
        db.Libraries.Single(l => l.Id == lib.Id).CatalogRevision++;
        await db.SaveChangesAsync();
        var second = await service.GetEntriesAsync(series.Id, default);
        Assert.NotSame(first, second);
        Assert.Equal(5, second!.Entries.Single(e => e.Kind == VolumeEntryKind.Stack && e.Stack!.Key == "4").Stack!.PresentCount);

        // A new map version rebuilds it too.
        var map = await db.SeriesVolumeMaps.SingleAsync();
        map.Version = 2;
        await db.SaveChangesAsync();
        Assert.NotSame(second, await service.GetEntriesAsync(series.Id, default));
    }

    [Fact]
    public async Task ToggleChain_UserThenFolderThenLibraryThenGlobal()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var (series, _, _) = await SeedSeriesAsync(db, lib);
        var user = await VolumeTestData.AddUserAsync(db);
        var service = new VolumeEntryService(db);
        var view = (await service.GetEntriesAsync(series.Id, default))!;

        // Nothing set: the global default is on.
        Assert.True(await service.IsActiveAsync(user.Id, view, null, default));

        // Global off; library on beats it; folder off beats the library; the user's own switch beats all.
        db.AppSettings.Add(new AppSettingsEntity { VirtualVolumesEnabled = false });
        await db.SaveChangesAsync();
        Assert.False(await service.IsActiveAsync(user.Id, view, null, default));

        db.Libraries.Single(l => l.Id == lib.Id).VirtualVolumes = (int)ViewSwitch.On;
        await db.SaveChangesAsync();
        Assert.True(await service.IsActiveAsync(user.Id, view, null, default));

        db.FolderViewSettings.Add(new FolderViewSettingsEntity { NodeId = series.Id, VirtualVolumes = (int)ViewSwitch.Off });
        await db.SaveChangesAsync();
        Assert.False(await service.IsActiveAsync(user.Id, view, null, default));

        db.ReaderPreferences.Add(new ReaderPreferencesEntity { UserId = user.Id, SeriesViewMode = (int)SeriesViewMode.Volumes });
        await db.SaveChangesAsync();
        Assert.True(await service.IsActiveAsync(user.Id, view, null, default));

        // An explicit request wins over everything.
        Assert.False(await service.IsActiveAsync(user.Id, view, "flat", default));
        var prefs = await db.ReaderPreferences.SingleAsync();
        prefs.SeriesViewMode = (int)SeriesViewMode.Folders;
        await db.SaveChangesAsync();
        Assert.False(await service.IsActiveAsync(user.Id, view, null, default));
        Assert.True(await service.IsActiveAsync(user.Id, view, "volumes", default));
    }

    [Fact]
    public async Task AUnitSubfolders_FolderOverride_FallsBackToItsSeriesFolder()
    {
        var (db, lib) = await SetupAsync();
        using var _ = db;
        var series = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Override Series");
        var season = await VolumeTestData.AddFolderAsync(db, lib.Id, series.Id, "Season 2");
        await VolumeTestData.AddChaptersAsync(db, lib.Id, season.Id, "Override - Chapter ", 41, 50);
        var record = await VolumeTestData.AddRecordAsync(db, status: MetadataOriginStatus.Complete);
        await VolumeTestData.LinkAsync(db, series, record.Id);
        await VolumeTestData.AddMapAsync(db, record.Id, 1, null, null, VolumeMapState.Ok, VolumeMapSource.MangaDexAggregate, (5, 41, 50));
        var user = await VolumeTestData.AddUserAsync(db);
        db.FolderViewSettings.Add(new FolderViewSettingsEntity { NodeId = series.Id, VirtualVolumes = (int)ViewSwitch.Off });
        await db.SaveChangesAsync();
        var service = new VolumeEntryService(db);

        var view = (await service.GetEntriesAsync(season.Id, default))!;

        Assert.False(await service.IsActiveAsync(user.Id, view, null, default));
    }
}
