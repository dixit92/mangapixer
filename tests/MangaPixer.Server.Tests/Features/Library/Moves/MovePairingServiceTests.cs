namespace com.lifepixer.mangapixer.Tests.Server.Features.Library.Moves;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Library.Moves;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests for moves recognised after the fact (1.31.0, "destination library scanned first"): the new copies
/// already exist when the source scan tombstones the old ones; the pairing pass copies the old state onto them, records
/// conflicts where both differ, and the admin resolves them.
/// </summary>
public sealed class MovePairingServiceTests : IDisposable
{
    private readonly MoveTestHarness _h = new();
    private long _src;
    private long _dst;
    private long _user;

    public void Dispose() => _h.Dispose();

    /// <summary>Two libraries; "Series" (three volumes) and "Other" in the source, analysed.</summary>
    private async Task SeedAsync()
    {
        await _h.InitAsync();
        _src = await _h.AddLibraryAsync("ongoing");
        _dst = await _h.AddLibraryAsync("concluded");
        _user = await _h.AddUserAsync("reader");
        _h.WriteArchive(_src, "Series/v01.cbz", 1);
        _h.WriteArchive(_src, "Series/v02.cbz", 2);
        _h.WriteArchive(_src, "Series/v03.cbz", 3);
        _h.WriteArchive(_src, "Other/x.cbz", 4);
        await _h.ScanAsync(_src);
        await _h.ScanAsync(_dst);
        await _h.AnalyseLibraryAsync(_src);
    }

    /// <summary>Moves "Series" to the destination and scans the DESTINATION first, then the source.</summary>
    private async Task MoveDestinationFirstAsync(bool analyseNew = true, Func<Task>? betweenScans = null)
    {
        _h.Move(_src, "Series", _dst, "Series");
        var dstScan = await _h.ScanAsync(_dst);
        Assert.Equal(0, dstScan.NodesMoved);
        if (analyseNew)
            await _h.AnalyseLibraryAsync(_dst);
        if (betweenScans is not null)
            await betweenScans();
        var srcScan = await _h.ScanAsync(_src);
        Assert.Equal(4, srcScan.NodesTombstoned);
    }

    [Fact]
    public async Task DestinationFirst_CopiesTheOldStateOntoTheNewCopies()
    {
        await SeedAsync();
        var v1 = await _h.NodeAsync(_src, "Series/v01.cbz");
        var v2 = await _h.NodeAsync(_src, "Series/v02.cbz");
        await _h.SetProgressAsync(_user, v1.Id, 3, state: 2);
        await _h.MarkReadAsync(_user, v1.Id);
        await _h.SetProgressAsync(_user, v2.Id, 1);
        await _h.FavoriteAsync(_user, v2.Id);
        await using (var db = _h.NewContext())
        {
            db.Bookmarks.Add(new BookmarkEntity { UserId = _user, ItemId = v2.Id, ContentVersion = 1, EntryKey = new PageEntryKey(2).ToOpaque(), Ordinal = 2, CreatedAt = DateTimeOffset.UtcNow });
            db.ItemReaderOverrides.Add(new ItemReaderOverridesEntity { UserId = _user, ItemId = v2.Id, ReaderMode = 1 });
            await db.SaveChangesAsync();
        }

        await MoveDestinationFirstAsync();
        var n1 = await _h.NodeAsync(_dst, "Series/v01.cbz");
        var n2 = await _h.NodeAsync(_dst, "Series/v02.cbz");
        Assert.NotEqual(v1.Id, n1.Id);
        Assert.True(n1.CreatedAt > v1.CreatedAt);

        var pass = await _h.PairAsync();

        Assert.Equal(3, pass.Paired);
        Assert.Equal(0, pass.Conflicts);
        var p1 = await _h.ProgressAsync(_user, n1.Id);
        Assert.NotNull(p1);
        Assert.Equal(2, p1!.State);
        Assert.Equal(3, p1.Ordinal);
        Assert.Equal(1, (await _h.ProgressAsync(_user, n2.Id))!.Ordinal);
        await using (var db = _h.NewContext())
        {
            Assert.True(await db.ReadMarks.AnyAsync(m => m.UserId == _user && m.ItemId == n1.Id));
            Assert.True(await db.Favorites.AnyAsync(f => f.UserId == _user && f.CatalogNodeId == n2.Id));
            Assert.True(await db.Bookmarks.AnyAsync(b => b.UserId == _user && b.ItemId == n2.Id && b.Ordinal == 2));
            Assert.Equal(1, (await db.ItemReaderOverrides.SingleAsync(o => o.UserId == _user && o.ItemId == n2.Id)).ReaderMode);
            Assert.Equal(3, await db.NodeMoves.CountAsync(m => m.Kind == 1));
            Assert.Equal(0, await db.MoveConflicts.CountAsync());
            // The moved copies keep their original Added date (a move is not new content).
            Assert.Equal(v1.CreatedAt, (await db.CatalogNodes.SingleAsync(n => n.Id == n1.Id)).CreatedAt);
            // The old copies stay tombstones.
            Assert.Equal(MoveTestHarness.Tombstoned, (await db.CatalogNodes.SingleAsync(n => n.Id == v1.Id)).Availability);
        }

        // Idempotent: a second pass finds nothing.
        Assert.Equal(0, (await _h.PairAsync()).Paired);
    }

    [Fact]
    public async Task DestinationFirst_FolderRows_Follow_AndAnAutoLinkToTheConfirmedRecordBecomesConfirmed()
    {
        await SeedAsync();
        var folder = await _h.NodeAsync(_src, "Series");
        var record = await _h.AddRecordAsync("100", "Synthetic Saga");
        await _h.LinkAsync(folder.Id, SeriesLinkState.Confirmed, record);
        await _h.FavoriteAsync(_user, folder.Id);
        await using (var db = _h.NewContext())
        {
            db.FolderViewSettings.Add(new FolderViewSettingsEntity { NodeId = folder.Id, VirtualVolumes = 1 });
            db.NodeCoverChoices.Add(new NodeCoverChoiceEntity { NodeId = folder.Id, Mode = 1, Version = 1, SetAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await MoveDestinationFirstAsync(betweenScans: async () =>
        {
            // The destination's automatic matching linked the new folder to the same record meanwhile.
            var newFolder = await _h.NodeAsync(_dst, "Series");
            await _h.LinkAsync(newFolder.Id, SeriesLinkState.Auto, record);
        });
        var nf = await _h.NodeAsync(_dst, "Series");

        var pass = await _h.PairAsync();

        Assert.Equal(3, pass.Paired);
        Assert.Equal(1, pass.FoldersCarried);
        var link = await _h.LinkOfAsync(nf.Id);
        Assert.Equal((int)SeriesLinkState.Confirmed, link!.State);
        Assert.Equal(record, link.RecordId);
        Assert.Null(await _h.LinkOfAsync(folder.Id));
        await using var check = _h.NewContext();
        Assert.True(await check.Favorites.AnyAsync(f => f.UserId == _user && f.CatalogNodeId == nf.Id));
        Assert.True(await check.FolderViewSettings.AnyAsync(v => v.NodeId == nf.Id));
        Assert.True(await check.NodeCoverChoices.AnyAsync(c => c.NodeId == nf.Id));
        Assert.Equal(0, await check.MoveConflicts.CountAsync());
        Assert.True(await check.MetadataRecords.AnyAsync(r => r.Id == record));
    }

    [Fact]
    public async Task ADifferentLinkOnTheNewFolder_IsAConflict_OverwriteUsesTheOldOne()
    {
        await SeedAsync();
        var folder = await _h.NodeAsync(_src, "Series");
        var right = await _h.AddRecordAsync("200", "Right Saga");
        var wrong = await _h.AddRecordAsync("201", "Wrong Saga");
        await _h.LinkAsync(folder.Id, SeriesLinkState.Confirmed, right);
        await MoveDestinationFirstAsync(betweenScans: async () =>
            await _h.LinkAsync((await _h.NodeAsync(_dst, "Series")).Id, SeriesLinkState.Auto, wrong));
        var nf = await _h.NodeAsync(_dst, "Series");

        await _h.PairAsync();

        Assert.Equal(wrong, (await _h.LinkOfAsync(nf.Id))!.RecordId); // the new copy keeps its link until the admin chooses
        await using (var db = _h.NewContext())
        {
            var conflict = await db.MoveConflicts.Include(c => c.Move).SingleAsync();
            Assert.Equal((int)MoveConflictKind.SeriesLink, conflict.Kind);
            Assert.Null(conflict.UserId);
            Assert.Equal(folder.Id, conflict.Move!.FromNodeId);
            // Held from the trash while open; not listed under Missing folders meanwhile.
            Assert.Contains(folder.Id, await new MoveTombstoneHolds(db).HeldNodeIds().ToListAsync());
            Assert.DoesNotContain(folder.Id, await MoveTestHarness.CarryOver(db).StrandedFolderIds(_src).ToListAsync());

            var page = await MoveTestHarness.Conflicts(db).ListAsync(resolved: false, cursor: null, limit: 50);
            var dto = Assert.Single(page.Items);
            Assert.Equal("Right Saga", dto.Old.RecordTitle);
            Assert.Equal("Wrong Saga", dto.New.RecordTitle);
            Assert.True(dto.IsFolder);
            Assert.Equal("ongoing", dto.FromLibraryName);
            Assert.Equal(nf.PublicId, dto.NodeId);

            var (error, result) = await MoveTestHarness.Conflicts(db).ResolveAsync(
                new MoveConflictResolveRequest { Ids = [dto.Id], Resolution = MoveConflictResolution.Overwrite }, null, "admin");
            Assert.Null(error);
            Assert.Equal(1, result!.Resolved);
        }
        var resolved = await _h.LinkOfAsync(nf.Id);
        Assert.Equal(right, resolved!.RecordId);
        Assert.Equal((int)SeriesLinkState.Confirmed, resolved.State);
        await using (var db = _h.NewContext())
        {
            Assert.False(await db.MetadataRecords.AnyAsync(r => r.Id == wrong)); // orphaned record removed
            Assert.Empty(await new MoveTombstoneHolds(db).HeldNodeIds().ToListAsync());
            Assert.Equal((int)MoveConflictState.Overwritten, (await db.MoveConflicts.SingleAsync()).State);
            // 1.37.0: the metadata export shows the moved link as carried from the old node, not as a removal + an addition.
            var carry = await db.ExportCarries.AsNoTracking().SingleAsync();
            Assert.Equal((nf.Id, folder.Id, folder.PublicId), (carry.NewNodeId, carry.OldNodeId, carry.OldNodePublicId));
        }
    }

    [Fact]
    public async Task ProgressOnBothCopies_IsAConflict_KeepLeavesTheNew_OverwriteUsesTheOld()
    {
        await SeedAsync();
        var other = await _h.AddUserAsync("other");
        var v1 = await _h.NodeAsync(_src, "Series/v01.cbz");
        var v2 = await _h.NodeAsync(_src, "Series/v02.cbz");
        await _h.SetProgressAsync(_user, v1.Id, 3);
        await _h.SetProgressAsync(other, v2.Id, 2);
        await using (var db = _h.NewContext())
        {
            db.ItemReaderOverrides.Add(new ItemReaderOverridesEntity { UserId = _user, ItemId = v2.Id, ReaderMode = 3 });
            await db.SaveChangesAsync();
        }

        await MoveDestinationFirstAsync(betweenScans: async () =>
        {
            // Both users read the new copies before the source was scanned; "other" at the same page (equal, no conflict).
            var n1x = await _h.NodeAsync(_dst, "Series/v01.cbz");
            var n2x = await _h.NodeAsync(_dst, "Series/v02.cbz");
            await _h.SetProgressAsync(_user, n1x.Id, 1);
            await _h.SetProgressAsync(other, n2x.Id, 2);
            await using var db = _h.NewContext();
            db.ItemReaderOverrides.Add(new ItemReaderOverridesEntity { UserId = _user, ItemId = n2x.Id, ReaderMode = 0 });
            await db.SaveChangesAsync();
        });
        var n1 = await _h.NodeAsync(_dst, "Series/v01.cbz");

        var pass = await _h.PairAsync();

        Assert.Equal(2, pass.Conflicts); // user's progress on v01 + user's reader settings on v02
        Assert.Equal(1, (await _h.ProgressAsync(_user, n1.Id))!.Ordinal); // new state kept until resolved
        await using (var db = _h.NewContext())
        {
            var service = MoveTestHarness.Conflicts(db);
            var page = await service.ListAsync(false, null, 50);
            Assert.Equal(2, page.OpenCount);
            var progress = page.Items.Single(i => i.Kind == MoveConflictKind.Progress);
            Assert.Equal("ureader", progress.UserId);
            Assert.Equal(4, progress.Old.Page);
            Assert.Equal(2, progress.New.Page);
            var settings = page.Items.Single(i => i.Kind == MoveConflictKind.ReaderSettings);
            Assert.Equal(com.lifepixer.mangapixer.Core.Reading.ReaderMode.VerticalWebtoon, settings.Old.ReaderMode);

            var keep = await service.ResolveAsync(new MoveConflictResolveRequest { Ids = [settings.Id], Resolution = MoveConflictResolution.Keep }, null, "admin");
            Assert.Equal(1, keep.Result!.Resolved);
            var overwrite = await service.ResolveAsync(new MoveConflictResolveRequest { All = true, Resolution = MoveConflictResolution.Overwrite }, null, "admin");
            Assert.Equal(1, overwrite.Result!.Resolved);
            Assert.Equal(0, await service.CountOpenAsync());
        }
        var after = await _h.ProgressAsync(_user, n1.Id);
        Assert.Equal(3, after!.Ordinal);
        Assert.True(after.Revision > 1);
        await using (var db = _h.NewContext())
        {
            var n2 = await db.CatalogNodes.SingleAsync(n => n.LibraryId == _dst && n.PathKey == "Series/v02.cbz");
            Assert.Equal(0, (await db.ItemReaderOverrides.SingleAsync(o => o.UserId == _user && o.ItemId == n2.Id)).ReaderMode);
            Assert.Equal(2, await db.MoveConflicts.CountAsync(c => c.State != (int)MoveConflictState.Open));
        }
    }

    [Fact]
    public async Task ANewCopyNotYetAnalysed_Waits_AndHoldsTheTombstone_ThenPairs()
    {
        await SeedAsync();
        var v1 = await _h.NodeAsync(_src, "Series/v01.cbz");
        await _h.MarkReadAsync(_user, v1.Id);
        await MoveDestinationFirstAsync(analyseNew: false);

        var pass = await _h.PairAsync();
        Assert.Equal(0, pass.Paired);
        Assert.Equal(3, pass.Waiting);
        await using (var db = _h.NewContext())
            Assert.Contains(v1.Id, await new MoveTombstoneHolds(db).HeldNodeIds().ToListAsync());

        await _h.AnalyseLibraryAsync(_dst);
        Assert.Equal(3, (await _h.PairAsync()).Paired);
        var n1 = await _h.NodeAsync(_dst, "Series/v01.cbz");
        await using (var db = _h.NewContext())
        {
            Assert.True(await db.ReadMarks.AnyAsync(m => m.ItemId == n1.Id));
            Assert.Empty(await new MoveTombstoneHolds(db).HeldNodeIds().ToListAsync());
        }
    }

    [Fact]
    public async Task IdenticalCopiesSeenSideBySide_StaySeparate()
    {
        await SeedAsync();
        var v1 = await _h.NodeAsync(_src, "Series/v01.cbz");
        await _h.MarkReadAsync(_user, v1.Id);
        // A copy in the destination, scanned while the original is still present.
        _h.Copy(_src, "Series/v01.cbz", _dst, "copy.cbz");
        await _h.ScanAsync(_dst);
        await _h.AnalyseLibraryAsync(_dst);
        await _h.ScanAsync(_src); // the original was seen present after the copy appeared
        File.Delete(_h.PathOf(_src, "Series/v01.cbz"));
        await _h.ScanAsync(_src);

        var pass = await _h.PairAsync();

        Assert.Equal(0, pass.Paired);
        var copy = await _h.NodeAsync(_dst, "copy.cbz");
        await using var db = _h.NewContext();
        Assert.False(await db.ReadMarks.AnyAsync(m => m.ItemId == copy.Id));
    }

    [Fact]
    public async Task OutsideTheWindow_NothingIsPaired()
    {
        await SeedAsync();
        await MoveDestinationFirstAsync();
        await _h.SetRetentionAsync(TrashRetention.Daily);
        await using (var db = _h.NewContext())
            await db.CatalogNodes.Where(n => n.LibraryId == _src && n.Availability == 5)
                .ExecuteUpdateAsync(u => u.SetProperty(n => n.TombstonedAt, DateTimeOffset.UtcNow.AddDays(-2)));

        Assert.Equal(0, (await _h.PairAsync()).Paired);
    }

    [Fact]
    public async Task PagesThatDisagree_AreNotPaired_AndAnotherPageOrder_MapsByEntry()
    {
        await SeedAsync();
        var v1 = await _h.NodeAsync(_src, "Series/v01.cbz");
        var v2 = await _h.NodeAsync(_src, "Series/v02.cbz");
        await _h.SetProgressAsync(_user, v2.Id, 0);
        await MoveDestinationFirstAsync();
        var n1 = await _h.NodeAsync(_dst, "Series/v01.cbz");
        var n2 = await _h.NodeAsync(_dst, "Series/v02.cbz");
        await _h.AnalyseAsync(n1.Id, pages: 5);                 // another page list: not the same archive by its pages
        await _h.AnalyseAsync(n2.Id, reversedOrder: true);      // the same entries in another order

        var pass = await _h.PairAsync();

        Assert.Equal(1, pass.ManifestMismatch);
        Assert.Equal(2, pass.Paired);
        await using var db = _h.NewContext();
        Assert.False(await db.NodeMoves.AnyAsync(m => m.FromNodeId == v1.Id));
        // Old page 1 held entry p00; in the new copy p00 is the last page.
        Assert.Equal(3, (await db.ReadingProgress.SingleAsync(p => p.UserId == _user && p.ItemId == n2.Id)).Ordinal);
    }

    [Fact]
    public async Task AUserWithoutAccessToTheDestination_StillGetsTheirState()
    {
        await SeedAsync();
        var outsider = await _h.AddUserAsync("outsider"); // no grant on either library (non-admin)
        var v1 = await _h.NodeAsync(_src, "Series/v01.cbz");
        await _h.SetProgressAsync(outsider, v1.Id, 2);
        await MoveDestinationFirstAsync();

        await _h.PairAsync();

        var n1 = await _h.NodeAsync(_dst, "Series/v01.cbz");
        Assert.Equal(2, (await _h.ProgressAsync(outsider, n1.Id))!.Ordinal);
    }

    /// <summary>
    /// The owner's live shape (1.30.1, synthetic stand-in): four finished series were moved from one library to another;
    /// the source scan tombstoned them, the destination scan added them again (analysed, re-matched Auto to the SAME records
    /// the old folders were Confirmed to); one series' read marks and progress stayed on the tombstones. The first pairing
    /// pass after the upgrade repairs it; a second pass does nothing.
    /// </summary>
    [Fact]
    public async Task TheOwnersLiveShape_IsRepairedByTheFirstPass()
    {
        await _h.InitAsync();
        _src = await _h.AddLibraryAsync("ongoing");
        _dst = await _h.AddLibraryAsync("concluded");
        _user = await _h.AddUserAsync("owner", admin: true);
        var sizes = new[] { 14, 3, 4, 5 };
        for (var s = 0; s < sizes.Length; s++)
            for (var v = 1; v <= sizes[s]; v++)
                _h.WriteArchive(_src, $"Series {s}/Vol {v:00}.cbz", s * 100 + v);
        _h.WriteArchive(_src, "Staying/ch1.cbz", 999);
        await _h.ScanAsync(_src);
        await _h.ScanAsync(_dst);
        await _h.AnalyseLibraryAsync(_src);
        var records = new long[sizes.Length];
        for (var s = 0; s < sizes.Length; s++)
        {
            records[s] = await _h.AddRecordAsync($"{500 + s}", $"Finished Series {s}");
            await _h.LinkAsync((await _h.NodeAsync(_src, $"Series {s}")).Id, SeriesLinkState.Confirmed, records[s]);
        }
        for (var v = 1; v <= 14; v++)
        {
            var node = await _h.NodeAsync(_src, $"Series 0/Vol {v:00}.cbz");
            await _h.MarkReadAsync(_user, node.Id);
            await _h.SetProgressAsync(_user, node.Id, 3, state: 2);
        }
        // Moved on disk; the source was scanned first (tombstoned) - by 1.30.1, which never paired them...
        for (var s = 0; s < sizes.Length; s++)
            _h.Move(_src, $"Series {s}", _dst, $"Series {s}");
        await _h.ScanAsync(_src);
        // ...then the destination added them as new nodes (1.30.1 had no cross-library move pool).
        await ScanDestinationAsIn1301Async();
        await _h.AnalyseLibraryAsync(_dst);
        for (var s = 0; s < sizes.Length; s++)
            await _h.LinkAsync((await _h.NodeAsync(_dst, $"Series {s}")).Id, SeriesLinkState.Auto, records[s]);

        var pass = await _h.PairAsync();

        Assert.Equal(26, pass.Paired);
        Assert.Equal(0, pass.Conflicts);
        Assert.Equal(4, pass.FoldersCarried);
        await using (var db = _h.NewContext())
        {
            var newVolumes = await db.CatalogNodes.Where(n => n.LibraryId == _dst && n.Kind == 1 && n.PathKey.StartsWith("Series 0/")).Select(n => n.Id).ToListAsync();
            Assert.Equal(14, await db.ReadMarks.CountAsync(m => m.UserId == _user && newVolumes.Contains(m.ItemId)));
            Assert.Equal(14, await db.ReadingProgress.CountAsync(p => p.UserId == _user && newVolumes.Contains(p.ItemId) && p.State == 2));
            for (var s = 0; s < sizes.Length; s++)
            {
                var nf = await db.CatalogNodes.SingleAsync(n => n.LibraryId == _dst && n.PathKey == $"Series {s}");
                var link = await db.NodeSeriesLinks.SingleAsync(l => l.NodeId == nf.Id);
                Assert.Equal((int)SeriesLinkState.Confirmed, link.State);
                Assert.Equal(records[s], link.RecordId);
            }
            Assert.Equal(0, await db.MoveConflicts.CountAsync());
            Assert.Empty(await MoveTestHarness.CarryOver(db).StrandedFolderIds(_src).ToListAsync());
        }
        var again = await _h.PairAsync();
        Assert.Equal(0, again.Paired);
        Assert.Equal(0, again.FoldersCarried);
    }

    /// <summary>
    /// The destination scan as 1.30.1 ran it: the new files become new nodes. The source's tombstones are hidden from the
    /// scan's move pool for that one scan by back-dating them past the window, then restored.
    /// </summary>
    private async Task ScanDestinationAsIn1301Async()
    {
        Dictionary<long, DateTimeOffset?> stamps;
        await using (var db = _h.NewContext())
            stamps = await db.CatalogNodes.Where(n => n.LibraryId == _src && n.Availability == 5).ToDictionaryAsync(n => n.Id, n => n.TombstonedAt);
        await using (var db = _h.NewContext())
            await db.CatalogNodes.Where(n => n.LibraryId == _src && n.Availability == 5)
                .ExecuteUpdateAsync(u => u.SetProperty(n => n.TombstonedAt, DateTimeOffset.UtcNow.AddYears(-2)));
        Assert.Equal(0, (await _h.ScanAsync(_dst)).NodesMoved);
        foreach (var (id, at) in stamps)
            await _h.SetTombstonedAtAsync(id, at!.Value);
    }
}
