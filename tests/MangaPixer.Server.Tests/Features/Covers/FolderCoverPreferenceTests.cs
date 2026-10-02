namespace com.lifepixer.mangapixer.Tests.Server.Features.Covers;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-with-DB tests (1.32.0) of the per-folder cover preference: the nearest folder row wins in ONE resolution batch with
/// nested folders, an explicit folder value beats the library switch both ways, an admin's explicit choice beats an inherited
/// "File covers", the background decision stops offering web sources under "File covers" (and makes them again when cleared), the
/// stack covers obey it, the set / clear service (validation, audit, "decide the subtree soon") and the carry-over of the row.
/// Synthetic rows and stored hashes only.
/// </summary>
public sealed class FolderCoverPreferenceTests
{
    private sealed record Series(CatalogNodeEntity Folder, CatalogNodeEntity V1, CatalogNodeEntity V2, MetadataRecordEntity Record, MetadataRecordEntity Companion, VolumeCoverEntity Web);

    /// <summary>A linked series folder with volumes 1 (a spread) and 2, a stored web cover of volume 2 and its decision (WebVolume).</summary>
    private static async Task<Series> AddSeriesAsync(CoverLayerTestKit kit, CatalogNodeEntity? parent, string name, string muId)
    {
        var folder = await kit.Db.AddFolderAsync(parent, name);
        var v1 = await kit.AddBookAsync(folder, name + " v01", 1400, 1000, 1UL);
        var v2 = await kit.AddBookAsync(folder, name + " v02", 700, 1000, 2UL);
        var record = await kit.Db.AddRecordAsync(muId, name);
        await kit.Db.AddLinkAsync(folder, record);
        var companion = await kit.AddCompanionAsync(record);
        var web = await kit.AddStoredCoverAsync(companion, 2, "en", ulong.MaxValue);
        await kit.Decisions().DecideAsync(v2.Id, default);
        Assert.Equal(AutoCoverSource.WebVolume, (AutoCoverSource)(await kit.AutoAsync(v2.Id))!.Source);
        return new Series(folder, v1, v2, record, companion, web);
    }

    private static async Task SetPreferenceAsync(CoverLayerTestKit kit, CatalogNodeEntity folder, FolderCoverPreference? preference)
    {
        kit.Db.Db.ChangeTracker.Clear();
        var existing = await kit.Db.Db.FolderCoverPreferences.FirstOrDefaultAsync(f => f.NodeId == folder.Id);
        if (preference is null)
        {
            if (existing is not null)
                kit.Db.Db.FolderCoverPreferences.Remove(existing);
        }
        else if (existing is null)
        {
            kit.Db.Db.FolderCoverPreferences.Add(new FolderCoverPreferenceEntity { NodeId = folder.Id, Preference = (int)preference });
        }
        else
        {
            existing.Preference = (int)preference;
        }
        await kit.Db.Db.SaveChangesAsync();
    }

    private static async Task SetLibraryHiddenAsync(CoverLayerTestKit kit, bool hidden)
    {
        var library = await kit.Db.Db.Libraries.SingleAsync(l => l.Id == kit.Db.LibraryId);
        library.WebCoversHidden = hidden;
        await kit.Db.Db.SaveChangesAsync();
    }

    private static CoverTarget Target(CatalogNodeEntity n) => new(n.Id, n.PublicId, n.Kind == 0);

    private static async Task<IReadOnlyDictionary<long, CoverResolution>> ResolveAsync(CoverLayerTestKit kit, params CatalogNodeEntity[] nodes)
    {
        kit.Db.Db.ChangeTracker.Clear();
        return await kit.Resolutions().ResolveAsync(nodes.Select(Target).ToList(), default);
    }

    [Fact]
    public async Task TheNearestFolderRowWins_ForEveryCardOfOneBatch_AndTheLibrarySwitchIsTheRoot()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var collection = await kit.Db.AddFolderAsync(null, "Collection");
        var a = await AddSeriesAsync(kit, collection, "Alpha", "501");
        var sub = await kit.Db.AddFolderAsync(collection, "Sub");
        var b = await AddSeriesAsync(kit, sub, "Bravo", "502");
        var outside = await kit.Db.AddFolderAsync(null, "Outside");
        var c = await AddSeriesAsync(kit, outside, "Charlie", "503");

        // No row anywhere: the web covers show (the library shows them).
        var all = await ResolveAsync(kit, a.V2, b.V2, c.V2);
        Assert.All([a.V2, b.V2, c.V2], v => Assert.Equal(CardCoverSource.WebVolume, all[v.Id].Source));

        // File covers on the collection: its whole subtree, nested folders included; the sibling subtree is untouched.
        await SetPreferenceAsync(kit, collection, FolderCoverPreference.File);
        all = await ResolveAsync(kit, a.V2, b.V2, c.V2);
        Assert.Equal(CardCoverSource.File, all[a.V2.Id].Source);
        Assert.Equal(CardCoverSource.File, all[b.V2.Id].Source);
        Assert.Equal(CardCoverSource.WebVolume, all[c.V2.Id].Source);
        Assert.Equal($"/api/v1/items/{a.V2.PublicId}/cover?v=1", all[a.V2.Id].Url);

        // A nearer row wins: Web covers on the nested folder only.
        await SetPreferenceAsync(kit, sub, FolderCoverPreference.Web);
        all = await ResolveAsync(kit, a.V2, b.V2, c.V2);
        Assert.Equal(CardCoverSource.File, all[a.V2.Id].Source);
        Assert.Equal(CardCoverSource.WebVolume, all[b.V2.Id].Source);

        // A row on the series folder itself (self first) beats the ones above.
        await SetPreferenceAsync(kit, b.Folder, FolderCoverPreference.File);
        Assert.Equal(CardCoverSource.File, (await ResolveAsync(kit, b.V2))[b.V2.Id].Source);
        await SetPreferenceAsync(kit, b.Folder, null);

        // The library switch is the root: off hides everything without a row; a folder saying Web covers still shows them.
        await SetPreferenceAsync(kit, collection, null);
        await SetLibraryHiddenAsync(kit, true);
        all = await ResolveAsync(kit, a.V2, b.V2, c.V2);
        Assert.Equal(CardCoverSource.File, all[a.V2.Id].Source);
        Assert.Equal(CardCoverSource.WebVolume, all[b.V2.Id].Source); // Sub says Web covers
        Assert.Equal(CardCoverSource.File, all[c.V2.Id].Source);

        // Back to inheriting: nothing is left of the folder's value.
        await SetPreferenceAsync(kit, sub, null);
        await SetLibraryHiddenAsync(kit, false);
        all = await ResolveAsync(kit, a.V2, b.V2, c.V2);
        Assert.All([a.V2, b.V2, c.V2], v => Assert.Equal(CardCoverSource.WebVolume, all[v.Id].Source));
    }

    [Fact]
    public async Task TheSeriesCardAndItsFolderFollowThePreferenceToo()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var shelf = await kit.Db.AddFolderAsync(null, "Shelf");
        // A one-volume-per-series shelf: the series folder shows its cover archive's resolved cover.
        var s = await AddSeriesAsync(kit, shelf, "Delta", "511");
        await kit.Decisions().DecideSubtreeAsync(shelf.Id, default);
        var before = (await ResolveAsync(kit, s.Folder))[s.Folder.Id];

        await SetPreferenceAsync(kit, shelf, FolderCoverPreference.File);
        var after = (await ResolveAsync(kit, s.Folder))[s.Folder.Id];

        // Whatever the folder showed before, no web layer shows now: the file or its crop.
        Assert.DoesNotContain(after.Source, new[] { CardCoverSource.WebVolume, CardCoverSource.WebMain, CardCoverSource.Poster });
        Assert.NotEqual(CardCoverSource.Chosen, after.Source);
        Assert.NotNull(before);
    }

    [Fact]
    public async Task AnExplicitChoice_BeatsAnInheritedFileCovers_ButTheLibrarySwitchStillHidesAChosenWebCover()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var shelf = await kit.Db.AddFolderAsync(null, "Shelf");
        var s = await AddSeriesAsync(kit, shelf, "Echo", "521");
        kit.Db.Db.NodeCoverChoices.Add(new NodeCoverChoiceEntity
        {
            NodeId = s.V2.Id,
            Mode = (int)CoverChoiceMode.VolumeCover,
            VolumeCoverId = s.Web.Id,
            Version = 1,
            SetAt = DateTimeOffset.UtcNow,
        });
        await kit.Db.Db.SaveChangesAsync();

        await SetPreferenceAsync(kit, shelf, FolderCoverPreference.File);
        var chosen = (await ResolveAsync(kit, s.V2))[s.V2.Id];
        Assert.Equal(CardCoverSource.Chosen, chosen.Source);
        Assert.Equal(CoverImageKind.VolumeCover, chosen.Image);

        // The library hiding web covers (no folder saying Web covers) still hides a chosen web cover, as before 1.32.0.
        await SetLibraryHiddenAsync(kit, true);
        Assert.Equal(CardCoverSource.File, (await ResolveAsync(kit, s.V2))[s.V2.Id].Source);
        // ... unless the nearest folder says Web covers.
        await SetPreferenceAsync(kit, shelf, FolderCoverPreference.Web);
        Assert.Equal(CardCoverSource.Chosen, (await ResolveAsync(kit, s.V2))[s.V2.Id].Source);
    }

    [Fact]
    public async Task ThePicker_StillOffersWebCoversUnderFileCovers_AndTheLibrarySwitchStillClosesIt()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var shelf = await kit.Db.AddFolderAsync(null, "Shelf");
        var s = await AddSeriesAsync(kit, shelf, "Foxtrot", "531");
        await SetPreferenceAsync(kit, shelf, FolderCoverPreference.File);
        var picker = new CoverPickerService(kit.Db.Db, kit.Resolutions(), new CoverCropService(kit.Db.Db, kit.Renderer, kit.Files), new AuditService(kit.Db.Db),
            NullLogger<CoverPickerService>.Instance);

        var options = await picker.GetOptionsAsync(s.V2.PublicId, default);
        Assert.True(options!.WebAvailable); // an explicit choice may still pick a web cover

        await SetLibraryHiddenAsync(kit, true);
        options = await picker.GetOptionsAsync(s.V2.PublicId, default);
        Assert.False(options!.WebAvailable);
        Assert.Equal("web_covers_hidden", options.WebUnavailableReason);

        await SetPreferenceAsync(kit, shelf, FolderCoverPreference.Web);
        options = await picker.GetOptionsAsync(s.V2.PublicId, default);
        Assert.True(options!.WebAvailable);
    }

    [Fact]
    public async Task TheDecision_OffersNoWebSourceUnderFileCovers_AndMakesTheWebDecisionAgainWhenCleared()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var shelf = await kit.Db.AddFolderAsync(null, "Shelf");
        var s = await AddSeriesAsync(kit, shelf, "Golf", "541");
        await SetPreferenceAsync(kit, shelf, FolderCoverPreference.File);

        // The web decision made before is replaced: the file decides (no row for a plain volume without a spread).
        await kit.Decisions().DecideAsync(s.V2.Id, default);
        var row = await kit.AutoAsync(s.V2.Id);
        Assert.True(row is null || row.Source != (int)AutoCoverSource.WebVolume);

        // A subtree pass (the sweep, "decide soon") over the shelf: no web decision anywhere below, the jacket crop of volume 1 stays.
        await kit.Decisions().DecideSubtreeAsync(shelf.Id, default);
        Assert.DoesNotContain(await kit.Db.Db.NodeAutoCovers.AsNoTracking().ToListAsync(),
            a => a.Source is (int)AutoCoverSource.WebVolume or (int)AutoCoverSource.WebMain or (int)AutoCoverSource.Poster);
        Assert.Equal((int)AutoCoverSource.Crop, (await kit.AutoAsync(s.V1.Id))!.Source);

        // Cleared: the same pass makes the web decision again (the stored cover never left).
        await SetPreferenceAsync(kit, shelf, null);
        await kit.Decisions().DecideSubtreeAsync(shelf.Id, default);
        Assert.Equal((int)AutoCoverSource.WebVolume, (await kit.AutoAsync(s.V2.Id))!.Source);
        Assert.Equal((CardCoverSource.WebVolume), (await ResolveAsync(kit, s.V2))[s.V2.Id].Source);
    }

    [Fact]
    public async Task TheSeriesFolderDecision_DropsItsWebMainAndPoster_UnderFileCovers()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var shelf = await kit.Db.AddFolderAsync(null, "Shelf");
        // A chapters-only series: its folder shows a web cover (volume 1, else main, else poster) when it may.
        var folder = await kit.Db.AddFolderAsync(shelf, "Hotel");
        await kit.AddBookAsync(folder, "Hotel c001", 700, 1000, 3UL);
        await kit.AddBookAsync(folder, "Hotel c002", 700, 1000, 4UL);
        var record = await kit.Db.AddRecordAsync("551", "Hotel");
        await kit.Db.AddLinkAsync(folder, record);
        var companion = await kit.AddCompanionAsync(record);
        await kit.AddStoredCoverAsync(companion, null, "en", ulong.MaxValue, VolumeCoverKind.Main);
        await kit.SetPosterAsync(record, 0xABCDUL);

        await kit.Decisions().DecideAsync(folder.Id, default);
        Assert.NotEqual((int)AutoCoverSource.File, (await kit.AutoAsync(folder.Id))!.Source); // web (main or poster)

        await SetPreferenceAsync(kit, shelf, FolderCoverPreference.File);
        await kit.Decisions().DecideAsync(folder.Id, default);
        var row = await kit.AutoAsync(folder.Id);
        Assert.True(row is null || row.Source == (int)AutoCoverSource.File);
        Assert.Equal(CardCoverSource.File, (await ResolveAsync(kit, folder))[folder.Id].Source);
    }

    [Fact]
    public async Task StackCovers_ObeyThePreference_LikeTheCoverLayer()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var shelf = await kit.Db.AddFolderAsync(null, "Shelf");
        var folder = await kit.Db.AddFolderAsync(shelf, "India");
        var record = await kit.Db.AddRecordAsync("561", "India");
        await kit.Db.AddLinkAsync(folder, record);
        var companion = await kit.AddCompanionAsync(record);
        await kit.AddStoredCoverAsync(companion, 1, "en", 2UL);
        VolumeStack[] stacks = [new("1", 1m, "Volume 1", VolumeStackConfidence.Exact, VolumeListSource.MangaDex, [], [], 2, 2, 0, false, "1", "2")];
        Task<IReadOnlyDictionary<string, StackCover>> StackCovers() =>
            new StackCoverService(kit.Db.Db).ResolveAsync(folder.Id, folder.PublicId, folder.LibraryId, stacks, default);

        Assert.Single(await StackCovers());
        await SetPreferenceAsync(kit, shelf, FolderCoverPreference.File);
        Assert.Empty(await StackCovers());
        await SetPreferenceAsync(kit, folder, FolderCoverPreference.Web); // nearer
        Assert.Single(await StackCovers());
        await SetPreferenceAsync(kit, shelf, null);
        await SetPreferenceAsync(kit, folder, null);
        await SetLibraryHiddenAsync(kit, true);
        Assert.Empty(await StackCovers());
        await SetPreferenceAsync(kit, shelf, FolderCoverPreference.Web); // beats the hiding library
        Assert.Single(await StackCovers());
    }

    private static FolderCoverPreferenceService Service(CoverLayerTestKit kit, CoverDecisionQueue? queue = null) =>
        new(kit.Db.Db, new AuditService(kit.Db.Db), queue ?? new CoverDecisionQueue(), NullLogger<FolderCoverPreferenceService>.Instance);

    [Fact]
    public async Task TheService_SetsClearsAndValidates_AuditsAndAsksForTheSubtreeDecisionOnlyOnAChange()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var shelf = await kit.Db.AddFolderAsync(null, "Shelf");
        var series = await kit.Db.AddFolderAsync(shelf, "Juliet");
        var book = await kit.AddBookAsync(series, "Juliet v01", 700, 1000, 1UL);
        var queue = new CoverDecisionQueue();
        var service = Service(kit, queue);

        var (code, dto) = await service.GetAsync(shelf.PublicId);
        Assert.Equal(FolderCoverPreferenceResult.Ok, code);
        Assert.Equal((null, FolderCoverPreference.Web, FolderCoverPreference.Web, (string?)null), (dto!.Preference, dto.Effective, dto.Inherited, dto.InheritedSourceName));

        (code, dto) = await service.SetAsync(shelf.PublicId, FolderCoverPreference.File, "admin");
        Assert.Equal(FolderCoverPreferenceResult.Ok, code);
        Assert.Equal((FolderCoverPreference.File, FolderCoverPreference.File, FolderCoverPreference.Web), (dto!.Preference, dto.Effective, dto.Inherited));
        Assert.True(queue.TryDequeueSubtree(out var queued));
        Assert.Equal(shelf.Id, queued);

        // The same value again: no change, no audit, nothing queued.
        await service.SetAsync(shelf.PublicId, FolderCoverPreference.File, "admin");
        Assert.False(queue.TryDequeueSubtree(out _));

        // The series folder inherits it, and says where from; its own value shows the inherited one next to it.
        (_, dto) = await service.GetAsync(series.PublicId);
        Assert.Equal((null, FolderCoverPreference.File, FolderCoverPreference.File, shelf.PublicId, "Shelf"),
            (dto!.Preference, dto.Effective, dto.Inherited, dto.InheritedSourceNodeId, dto.InheritedSourceName));
        (_, dto) = await service.SetAsync(series.PublicId, FolderCoverPreference.Web, "admin");
        Assert.Equal((FolderCoverPreference.Web, FolderCoverPreference.Web, FolderCoverPreference.File, "Shelf"),
            (dto!.Preference, dto.Effective, dto.Inherited, dto.InheritedSourceName));
        Assert.True(queue.TryDequeueSubtree(out queued));
        Assert.Equal(series.Id, queued);

        // The library switch is what a folder without any row above inherits.
        await SetLibraryHiddenAsync(kit, true);
        (_, dto) = await service.GetAsync(shelf.PublicId);
        Assert.Equal((FolderCoverPreference.File, FolderCoverPreference.File), (dto!.Inherited, dto.Effective));
        await service.ClearAsync(shelf.PublicId, "admin");
        Assert.True(queue.TryDequeueSubtree(out _));
        (_, dto) = await service.GetAsync(shelf.PublicId);
        Assert.Equal((null, FolderCoverPreference.File, (string?)null), (dto!.Preference, dto.Inherited, dto.InheritedSourceName)); // the library, no folder

        // Clearing what is not there changes nothing.
        await service.ClearAsync(shelf.PublicId, "admin");
        Assert.False(queue.TryDequeueSubtree(out _));

        // Validation: an archive, an unknown node, an out-of-range value.
        Assert.Equal(FolderCoverPreferenceResult.NotAFolder, (await service.SetAsync(book.PublicId, FolderCoverPreference.File, "admin")).Code);
        Assert.Equal(FolderCoverPreferenceResult.NodeNotFound, (await service.GetAsync("nope")).Code);
        Assert.Equal(FolderCoverPreferenceResult.InvalidPreference, (await service.SetAsync(shelf.PublicId, (FolderCoverPreference)9, "admin")).Code);

        // Audited with ids only (never a name).
        var audit = await kit.Db.Db.AuditEvents.AsNoTracking().Where(e => e.Action.StartsWith("folder.cover-preference")).OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(["folder.cover-preference.set", "folder.cover-preference.set", "folder.cover-preference.clear"], audit.Select(e => e.Action));
        Assert.Equal([shelf.Id, series.Id, shelf.Id], audit.Select(e => e.TargetItemId!.Value));
        Assert.All(audit, e => Assert.Equal(kit.Db.LibraryId, e.TargetLibraryId));
        Assert.Equal(["File", "Web", "success"], audit.Select(e => e.Result));
    }

    [Fact]
    public async Task ARemovedFolder_CarriesItsPreferenceToTheRenamedOne_UnlessTheNewOneHasItsOwn()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var oldFolder = await kit.Db.AddFolderAsync(null, "Old name");
        var newFolder = await kit.Db.AddFolderAsync(null, "New name");
        var other = await kit.Db.AddFolderAsync(null, "Other");
        await SetPreferenceAsync(kit, oldFolder, FolderCoverPreference.File);
        var carry = new MetadataCarryOverService(kit.Db.Db, new AuditService(kit.Db.Db), TimeProvider.System, [], NullLogger<MetadataCarryOverService>.Instance);

        var moved = await carry.MoveRowsAsync(oldFolder.Id, newFolder.Id, default);

        Assert.True(moved.CoverPreference);
        Assert.True(moved.Any);
        var rows = await kit.Db.Db.FolderCoverPreferences.AsNoTracking().ToListAsync();
        Assert.Equal([(newFolder.Id, (int)FolderCoverPreference.File)], rows.Select(r => (r.NodeId, r.Preference)));

        // A row the target already has wins; the old one stays where it was.
        await SetPreferenceAsync(kit, oldFolder, FolderCoverPreference.File);
        await SetPreferenceAsync(kit, other, FolderCoverPreference.Web);
        moved = await carry.MoveRowsAsync(oldFolder.Id, other.Id, default);
        Assert.False(moved.CoverPreference);
        Assert.Equal((int)FolderCoverPreference.Web, (await kit.Db.Db.FolderCoverPreferences.AsNoTracking().SingleAsync(r => r.NodeId == other.Id)).Preference);
        Assert.True(await kit.Db.Db.FolderCoverPreferences.AnyAsync(r => r.NodeId == oldFolder.Id));
    }
}
