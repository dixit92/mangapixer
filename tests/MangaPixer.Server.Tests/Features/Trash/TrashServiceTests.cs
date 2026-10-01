namespace com.lifepixer.mangapixer.Tests.Server.Features.Trash;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests for Empty trash (1.31.0): the retention window, move-recognition holds, the library holds (root
/// unavailable, burst, running scan) and their release, leaf-first deletion, everything a purged node owns (the FK-less per-user
/// rows, cascaded rows, search rows, thumbnail and crops) - and what it never touches (metadata records, web covers, posters,
/// live nodes).
/// </summary>
public sealed class TrashServiceTests : IDisposable
{
    private readonly TrashTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private async Task<EmptyTrashResultDto> EmptyAsync(string? library = null, bool release = false, bool automatic = false)
    {
        await using var db = _kit.NewContext();
        var outcome = await _kit.Service(db).EmptyAsync(library, release, automatic, actor: null, default);
        Assert.Null(outcome.Error);
        return outcome.Result!;
    }

    private async Task<TrashOverviewDto> OverviewAsync()
    {
        await using var db = _kit.NewContext();
        return await _kit.Service(db).GetOverviewAsync(default);
    }

    [Fact]
    public async Task OnlyTombstonesPastTheWindow_Go_AndTheWindowFollowsTheSetting()
    {
        await _kit.InitAsync();
        var lib = await _kit.AddLibraryAsync("lib1");
        // Enough present nodes that no burst hold applies.
        for (var i = 0; i < 6; i++)
            await _kit.AddNodeAsync(lib, null, 1, null);
        var old = await _kit.AddNodeAsync(lib, null, 1, 40);
        var recent = await _kit.AddNodeAsync(lib, null, 1, 10);
        var fresh = await _kit.AddNodeAsync(lib, null, 1, 0.5);

        var monthly = await EmptyAsync();
        Assert.Equal(1, monthly.Removed.Nodes);
        Assert.False(await _kit.ExistsAsync(old));
        Assert.True(await _kit.ExistsAsync(recent));

        await _kit.SetRetentionAsync(7);
        Assert.Equal(1, (await EmptyAsync()).Removed.Nodes);
        Assert.False(await _kit.ExistsAsync(recent));

        // Daily is the shortest window: a tombstone from this pass (half a day old) always stays.
        await _kit.SetRetentionAsync(1);
        Assert.Equal(0, (await EmptyAsync()).Removed.Nodes);
        Assert.True(await _kit.ExistsAsync(fresh));
    }

    [Fact]
    public async Task ATombstoneHeldByMoveRecognition_StaysWhateverItsAge_EvenWhenTheAdminReleasesALibraryHold()
    {
        await _kit.InitAsync();
        var lib = await _kit.AddLibraryAsync("lib1");
        var held = await _kit.AddNodeAsync(lib, null, 1, 400);
        var free = await _kit.AddNodeAsync(lib, null, 1, 400);

        await using (var db = _kit.NewContext())
        {
            var outcome = await _kit.Service(db, new FixedTombstoneHolds(db, held)).EmptyAsync("lib1", releaseHold: true, automatic: false, null, default);
            Assert.Equal(1, outcome.Result!.Removed.Nodes);
        }
        Assert.True(await _kit.ExistsAsync(held));
        Assert.False(await _kit.ExistsAsync(free));
    }

    [Fact]
    public async Task EveryAncestorFolderOfAHeldTombstone_Stays_WhileItsOtherEligibleChildrenGo()
    {
        // Move recognition holds tombstoned ARCHIVES (lane A); the catalog's parent key is Restrict, so the folders above a held
        // archive must stay even though they are past the window themselves.
        await _kit.InitAsync();
        var lib = await _kit.AddLibraryAsync("lib1");
        for (var i = 0; i < 8; i++)
            await _kit.AddNodeAsync(lib, null, 1, null);
        var top = await _kit.AddNodeAsync(lib, null, 0, 90);
        var sub = await _kit.AddNodeAsync(lib, top, 0, 90);
        var held = await _kit.AddNodeAsync(lib, sub, 1, 90);
        var sibling = await _kit.AddNodeAsync(lib, sub, 1, 90);
        var otherFolder = await _kit.AddNodeAsync(lib, top, 0, 90);
        var otherItem = await _kit.AddNodeAsync(lib, otherFolder, 1, 90);

        await using (var db = _kit.NewContext())
        {
            var service = _kit.Service(db, new FixedTombstoneHolds(db, held));
            var row = Assert.Single((await service.GetOverviewAsync(default)).Libraries);
            Assert.Equal((3, 2, 1, 3), (row.Eligible.Nodes, row.Eligible.Archives, row.Eligible.Folders, row.Waiting));
            var outcome = await service.EmptyAsync(null, releaseHold: false, automatic: false, null, default);
            Assert.Equal((3, 2, 1), (outcome.Result!.Removed.Nodes, outcome.Result.Removed.Archives, outcome.Result.Removed.Folders));
        }
        Assert.True(await _kit.ExistsAsync(held));
        Assert.True(await _kit.ExistsAsync(sub));
        Assert.True(await _kit.ExistsAsync(top));
        Assert.False(await _kit.ExistsAsync(sibling));
        Assert.False(await _kit.ExistsAsync(otherFolder));
        Assert.False(await _kit.ExistsAsync(otherItem));
    }

    [Fact]
    public async Task APurge_RemovesEverythingTheNodeOwns_ButKeepsRecordsWebCoversAndPosters()
    {
        await _kit.InitAsync();
        var lib = await _kit.AddLibraryAsync("lib1");
        for (var i = 0; i < 4; i++)
            await _kit.AddNodeAsync(lib, null, 1, null);
        var series = await _kit.AddNodeAsync(lib, null, 0, 45);
        var item = await _kit.AddNodeAsync(lib, series, 1, 45);
        await _kit.AddUserStateAsync(item);
        var files = _kit.WriteArchiveFiles(item);

        long recordId;
        string poster;
        string webCover;
        await using (var db = _kit.NewContext())
        {
            var record = new MetadataRecordEntity { PublicId = "r1", Provider = "mangaupdates", ExternalId = "1", Title = "Synthetic", ImageVersion = 1, ImageState = 1, FetchedAt = TrashTestKit.Now };
            db.MetadataRecords.Add(record);
            await db.SaveChangesAsync();
            recordId = record.Id;
            db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity { NodeId = series, LibraryId = lib, State = 1, RecordId = record.Id, CreatedAt = TrashTestKit.Now, UpdatedAt = TrashTestKit.Now });
            var cover = new VolumeCoverEntity
            {
                PublicId = "vc00112233445566aa",
                ProviderRecordId = record.Id,
                Kind = 0,
                Volume = 1,
                Locale = "en",
                RemoteId = "x",
                RemoteFile = "x.jpg",
                State = 1,
                StoredVersion = 1,
                ListedAt = TrashTestKit.Now,
            };
            db.VolumeCovers.Add(cover);
            await db.SaveChangesAsync();
            db.NodeCoverChoices.Add(new NodeCoverChoiceEntity { NodeId = series, Mode = 3, VolumeCoverId = cover.Id, Version = 1, SetAt = TrashTestKit.Now });
            db.Jobs.Add(new JobEntity { JobType = "analyze", LibraryId = lib, ItemId = item, Status = 2, QueuedAt = TrashTestKit.Now });
            await db.SaveChangesAsync();
            poster = TrashTestKit.WriteFile(Path.Combine(_kit.Images.Root, $"{record.Id}-1.jpg"));
            webCover = TrashTestKit.WriteFile(_kit.VolumeCovers.PathFor(cover.PublicId, 1));
        }

        var overview = await OverviewAsync();
        var row = Assert.Single(overview.Libraries);
        Assert.Equal((2, 1, 1, 5, 3, 200L), (row.Eligible.Nodes, row.Eligible.Archives, row.Eligible.Folders, row.Eligible.UserStateRows,
            row.Eligible.Files, row.Eligible.Bytes));
        Assert.Null(row.Hold);
        Assert.Equal(row.Eligible, overview.Total);

        var result = await EmptyAsync();
        Assert.Equal(row.Eligible, result.Removed);

        await using (var db = _kit.NewContext())
        {
            Assert.False(await db.CatalogNodes.AnyAsync(n => n.Id == item || n.Id == series));
            Assert.False(await db.ArchiveItems.AnyAsync(a => a.NodeId == item));
            Assert.False(await db.PageEntries.AnyAsync(p => p.ItemId == item));
            Assert.Equal(0, await db.ReadingProgress.CountAsync());
            Assert.Equal(0, await db.ReadMarks.CountAsync());
            Assert.Equal(0, await db.Bookmarks.CountAsync());
            Assert.Equal(0, await db.ItemReaderOverrides.CountAsync());
            Assert.Equal(0, await db.Favorites.CountAsync());
            Assert.Equal(0, await db.NodeSeriesLinks.CountAsync());
            Assert.Equal(0, await db.NodeCoverChoices.CountAsync());
            Assert.Equal(0, await db.Jobs.CountAsync());
            var search = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM catalog_search WHERE node_id IN ({item}, {series})").FirstAsync();
            Assert.Equal(0, search);

            // The record, its web cover and its poster belong to the record.
            Assert.True(await db.MetadataRecords.AnyAsync(r => r.Id == recordId));
            Assert.Equal(1, await db.VolumeCovers.CountAsync());
            // Audit: one event, counts only.
            var audit = await db.AuditEvents.SingleAsync();
            Assert.Equal((AuditActions.TrashEmpty, "nodes_2_held_0"), (audit.Action, audit.Result));
            var settings = await db.AppSettings.SingleAsync();
            Assert.Equal((2, 200L, false), (settings.TrashLastEmptiedNodes, settings.TrashLastEmptiedBytes, settings.TrashLastEmptiedAutomatic));
        }
        Assert.False(File.Exists(files.Thumbnail));
        Assert.False(File.Exists(files.Left));
        Assert.False(File.Exists(files.Right));
        Assert.True(File.Exists(poster));
        Assert.True(File.Exists(webCover));
    }

    [Fact]
    public async Task AFolderWithAChildThatStays_Stays_AndTheEligibleChildGoesLeafFirst()
    {
        await _kit.InitAsync();
        var lib = await _kit.AddLibraryAsync("lib1");
        for (var i = 0; i < 6; i++)
            await _kit.AddNodeAsync(lib, null, 1, null);
        var top = await _kit.AddNodeAsync(lib, null, 0, 60);
        var sub = await _kit.AddNodeAsync(lib, top, 0, 60);
        var gone = await _kit.AddNodeAsync(lib, sub, 1, 60);
        var recent = await _kit.AddNodeAsync(lib, sub, 1, 3); // inside the window: keeps sub and top
        var emptyFolder = await _kit.AddNodeAsync(lib, top, 0, 60);

        var result = await EmptyAsync();
        Assert.Equal((2, 1, 1), (result.Removed.Nodes, result.Removed.Archives, result.Removed.Folders));
        Assert.False(await _kit.ExistsAsync(gone));
        Assert.False(await _kit.ExistsAsync(emptyFolder));
        Assert.True(await _kit.ExistsAsync(sub));
        Assert.True(await _kit.ExistsAsync(top));
        Assert.True(await _kit.ExistsAsync(recent));

        // A month later the last child is past the window too: the whole branch goes, deepest first.
        _kit.Clock.Advance(TimeSpan.FromDays(31));
        var later = await EmptyAsync();
        Assert.Equal((3, 1, 2), (later.Removed.Nodes, later.Removed.Archives, later.Removed.Folders));
        Assert.False(await _kit.ExistsAsync(top));
    }

    [Fact]
    public async Task ABurst_IsHeld_UntilTheAdminEmptiesThatLibrary()
    {
        await _kit.InitAsync();
        var lib = await _kit.AddLibraryAsync("lib1");
        await _kit.AddNodeAsync(lib, null, 1, null);
        var a = await _kit.AddNodeAsync(lib, null, 1, 40);
        await _kit.AddNodeAsync(lib, null, 1, 40);

        var row = Assert.Single((await OverviewAsync()).Libraries);
        Assert.Equal(("burst", true, 2), (row.Hold, row.HoldReleasable, row.Eligible.Nodes));

        // The automatic run and "Empty trash now" for all libraries keep it.
        var all = await EmptyAsync(automatic: true);
        Assert.Equal(0, all.Removed.Nodes);
        Assert.Equal("burst", Assert.Single(all.Held).Hold);
        Assert.True(await _kit.ExistsAsync(a));

        await using (var db = _kit.NewContext())
        {
            var refused = await _kit.Service(db).EmptyAsync("lib1", releaseHold: false, automatic: false, null, default);
            Assert.Equal(("trash_held", "burst"), (refused.Error, refused.Hold));
        }

        Assert.Equal(2, (await EmptyAsync("lib1", release: true)).Removed.Nodes);
        Assert.False(await _kit.ExistsAsync(a));
    }

    [Fact]
    public async Task AnUnreachableRoot_HoldsTheLibrary_AndARunningScanSkipsIt()
    {
        await _kit.InitAsync();
        var offline = await _kit.AddLibraryAsync("off");
        var scanning = await _kit.AddLibraryAsync("scan");
        var fine = await _kit.AddLibraryAsync("fine");
        foreach (var lib in new[] { offline, scanning, fine })
        {
            for (var i = 0; i < 4; i++)
                await _kit.AddNodeAsync(lib, null, 1, null);
            await _kit.AddNodeAsync(lib, null, 1, 40);
        }
        await _kit.AddScanRunAsync(offline, 2, null);
        await _kit.AddScanRunAsync(offline, 3, "root_unavailable");
        await _kit.AddScanRunAsync(fine, 3, "root_unavailable");
        await _kit.AddScanRunAsync(fine, 2, null); // a later successful scan clears it
        await _kit.AddScanRunAsync(scanning, 1, null);

        var holds = (await OverviewAsync()).Libraries.ToDictionary(l => l.LibraryId, l => (l.Hold, l.HoldReleasable));
        Assert.Equal(("root_unavailable", true), holds["off"]);
        Assert.Equal(("scan_running", false), holds["scan"]);
        Assert.Equal((null, false), holds["fine"]);

        var result = await EmptyAsync();
        Assert.Equal(1, result.Removed.Nodes);
        Assert.Equal(["off", "scan"], result.Held.Select(h => h.LibraryId).Order());

        await using (var db = _kit.NewContext())
        {
            var service = _kit.Service(db);
            Assert.Equal("scan_in_progress", (await service.EmptyAsync("scan", releaseHold: true, false, null, default)).Error);
            Assert.Equal("not_found", (await service.EmptyAsync("nope", false, false, null, default)).Error);
        }
        Assert.Equal(1, (await EmptyAsync("off", release: true)).Removed.Nodes);
    }

    [Fact]
    public async Task Settings_ValidateTheRetention_AndAuditTheAutomaticSwitch()
    {
        await _kit.InitAsync();
        await using var db = _kit.NewContext();
        var service = _kit.Service(db);

        var (initial, _) = await service.UpdateSettingsAsync(new UpdateTrashSettingsRequest(), null, default);
        Assert.Equal((false, 30), (initial!.AutomaticCleaning, initial.RetentionDays));
        Assert.Equal([1, 7, 30, 90, 365], initial.AllowedRetentionDays);

        foreach (var bad in new[] { 0, 2, 14, 31, -1, 366 })
            Assert.Equal("invalid_retention", (await service.UpdateSettingsAsync(new UpdateTrashSettingsRequest { RetentionDays = bad }, null, default)).Error);

        var (changed, error) = await service.UpdateSettingsAsync(new UpdateTrashSettingsRequest { RetentionDays = 90, AutomaticCleaning = true }, null, default);
        Assert.Null(error);
        Assert.Equal((true, 90), (changed!.AutomaticCleaning, changed.RetentionDays));

        var row = await db.AppSettings.AsNoTracking().SingleAsync();
        Assert.Equal((90, true, TrashTestKit.Now), (row.TrashRetentionDays, row.TrashAutoCleanEnabled, row.TrashAutoCleanEnabledAt));
        Assert.Equal([AuditActions.TrashAutoEnable, AuditActions.TrashRetentionChange],
            (await db.AuditEvents.AsNoTracking().Select(a => a.Action).ToListAsync()).Order());
    }
}
