namespace com.lifepixer.mangapixer.Tests.Server.Features.Library.Moves;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests for a move between libraries recognised while the destination is scanned (1.31.0, "source library
/// scanned first"): the destination scan re-points the tombstoned node, so its id, analysis and every per-user row stay.
/// </summary>
public sealed class CrossLibraryScanMoveTests : IDisposable
{
    private readonly MoveTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private async Task<(long Src, long Dst, long User)> SeedAsync()
    {
        await _h.InitAsync();
        var src = await _h.AddLibraryAsync("ongoing");
        var dst = await _h.AddLibraryAsync("concluded");
        var user = await _h.AddUserAsync("reader");
        _h.WriteArchive(src, "Series/v01.cbz", 1);
        _h.WriteArchive(src, "Series/v02.cbz", 2);
        _h.WriteArchive(src, "Other/x.cbz", 3);
        await _h.ScanAsync(src);
        await _h.ScanAsync(dst);
        await _h.AnalyseLibraryAsync(src);
        return (src, dst, user);
    }

    [Fact]
    public async Task SourceFirst_MovesTheNodeIntoTheDestination_KeepingIdAndState()
    {
        var (src, dst, user) = await SeedAsync();
        var v1 = await _h.NodeAsync(src, "Series/v01.cbz");
        var folder = await _h.NodeAsync(src, "Series");
        await _h.SetProgressAsync(user, v1.Id, 2);
        await _h.MarkReadAsync(user, v1.Id);
        await _h.FavoriteAsync(user, v1.Id);
        long srcRevision;
        await using (var db = _h.NewContext())
        {
            db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity { NodeId = v1.Id, LibraryId = src, State = 0, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            srcRevision = (await db.Libraries.SingleAsync(l => l.Id == src)).CatalogRevision;
        }

        _h.Move(src, "Series", dst, "Series");
        var srcScan = await _h.ScanAsync(src);
        Assert.Equal(3, srcScan.NodesTombstoned); // folder + two archives
        var dstScan = await _h.ScanAsync(dst);

        Assert.Equal(2, dstScan.NodesMoved);
        Assert.Equal(2, dstScan.NodesMovedFromOtherLibraries);
        Assert.Equal(1, dstScan.NodesAdded); // only the new folder
        var moved = await _h.NodeAsync(dst, "Series/v01.cbz");
        Assert.Equal(v1.Id, moved.Id);
        Assert.Equal(v1.PublicId, moved.PublicId);
        Assert.Equal(0, moved.Availability);
        Assert.Null(moved.TombstonedAt);
        Assert.Equal(v1.CreatedAt, moved.CreatedAt);
        Assert.Contains(dstScan.Moves, m => m.ArchiveNodeId == v1.Id && m.OldParentId == folder.Id);

        await using var check = _h.NewContext();
        Assert.Equal(1, await check.ReadingProgress.CountAsync(p => p.UserId == user && p.ItemId == v1.Id));
        Assert.Equal(1, await check.ReadMarks.CountAsync(p => p.UserId == user && p.ItemId == v1.Id));
        Assert.Equal(1, await check.Favorites.CountAsync(p => p.UserId == user && p.CatalogNodeId == v1.Id));
        Assert.Equal(4, await check.PageEntries.CountAsync(p => p.ItemId == v1.Id));
        Assert.Equal(0, (await check.ArchiveItems.SingleAsync(a => a.NodeId == v1.Id)).AnalysisState);
        // Denormalised library follows; the source library's revision moved on and its old folder no longer counts the archives.
        Assert.Equal(dst, (await check.NodeSeriesLinks.SingleAsync(l => l.NodeId == v1.Id)).LibraryId);
        Assert.True((await check.Libraries.SingleAsync(l => l.Id == src)).CatalogRevision >= srcRevision + 2);
        Assert.Null((await check.CatalogNodes.SingleAsync(n => n.Id == folder.Id)).LatestDescendantAddedAt);
        var newFolder = await check.CatalogNodes.SingleAsync(n => n.LibraryId == dst && n.PathKey == "Series");
        Assert.Equal(newFolder.Id, moved.ParentId);
        Assert.NotNull(newFolder.LatestDescendantAddedAt);
    }

    [Fact]
    public async Task SourceFirst_OutsideTheWindow_IsANewNode()
    {
        var (src, dst, _) = await SeedAsync();
        var v1 = await _h.NodeAsync(src, "Series/v01.cbz");
        _h.Move(src, "Series/v01.cbz", dst, "v01.cbz");
        await _h.ScanAsync(src);
        await _h.SetRetentionAsync(TrashRetention.Weekly);
        await _h.SetTombstonedAtAsync(v1.Id, DateTimeOffset.UtcNow.AddDays(-8));

        var dstScan = await _h.ScanAsync(dst);

        Assert.Equal(0, dstScan.NodesMoved);
        Assert.NotEqual(v1.Id, (await _h.NodeAsync(dst, "v01.cbz")).Id);
        Assert.Equal(MoveTestHarness.Tombstoned, (await _h.NodeByIdAsync(v1.Id)).Availability);
    }

    [Fact]
    public async Task SourceFirst_TwoTombstonesWithOneSignature_AreAmbiguous()
    {
        await _h.InitAsync();
        var a = await _h.AddLibraryAsync("a");
        var b = await _h.AddLibraryAsync("b");
        var c = await _h.AddLibraryAsync("c");
        _h.WriteArchive(a, "same.cbz", 7);
        _h.WriteArchive(b, "same.cbz", 7); // byte-identical copy in another library
        _h.WriteArchive(a, "keep.cbz", 70); // a library that loses everything is never tombstoned (suspicious loss)
        _h.WriteArchive(b, "keep.cbz", 71);
        await _h.ScanAsync(a);
        await _h.ScanAsync(b);
        await _h.ScanAsync(c);
        await _h.AnalyseLibraryAsync(a);
        await _h.AnalyseLibraryAsync(b);
        _h.Move(a, "same.cbz", c, "same.cbz");
        File.Delete(_h.PathOf(b, "same.cbz"));
        await _h.ScanAsync(a);
        await _h.ScanAsync(b);
        Assert.Equal(MoveTestHarness.Tombstoned, (await _h.NodeAsync(a, "same.cbz")).Availability);
        Assert.Equal(MoveTestHarness.Tombstoned, (await _h.NodeAsync(b, "same.cbz")).Availability);

        var scan = await _h.ScanAsync(c);

        Assert.Equal(0, scan.NodesMoved);
        Assert.Equal(1, scan.NodesAdded);
    }

    [Fact]
    public async Task SourceFirst_ALiveCopyAppearedElsewhere_IsAmbiguous()
    {
        await _h.InitAsync();
        var a = await _h.AddLibraryAsync("a");
        var b = await _h.AddLibraryAsync("b");
        var c = await _h.AddLibraryAsync("c");
        _h.WriteArchive(a, "v.cbz", 9);
        _h.WriteArchive(a, "keep.cbz", 90);
        await _h.ScanAsync(a);
        await _h.ScanAsync(b);
        await _h.ScanAsync(c);
        await _h.AnalyseLibraryAsync(a);
        var old = await _h.NodeAsync(a, "v.cbz");
        // Copied to b, then moved to c: two new copies of one old archive. b is scanned (and analysed) before a tombstones it.
        _h.Copy(a, "v.cbz", b, "v.cbz");
        _h.Move(a, "v.cbz", c, "v.cbz");
        await _h.ScanAsync(b);
        await _h.AnalyseLibraryAsync(b);
        await _h.ScanAsync(a);
        Assert.Equal(MoveTestHarness.Tombstoned, (await _h.NodeByIdAsync(old.Id)).Availability);

        var scan = await _h.ScanAsync(c);

        Assert.Equal(0, scan.NodesMoved);
        Assert.Equal(MoveTestHarness.Tombstoned, (await _h.NodeByIdAsync(old.Id)).Availability);
    }

    [Fact]
    public async Task SourceFirst_WhileTheSourceLibraryIsScanning_LeavesItsTombstonesAlone()
    {
        var (src, dst, _) = await SeedAsync();
        var v1 = await _h.NodeAsync(src, "Series/v01.cbz");
        _h.Move(src, "Series/v01.cbz", dst, "v01.cbz");
        await _h.ScanAsync(src);
        await using (var db = _h.NewContext())
        {
            db.ScanRuns.Add(new ScanRunEntity { LibraryId = src, ScanRevision = 99, Status = 1, StartedAt = DateTimeOffset.UtcNow, LeaseExpiry = DateTimeOffset.UtcNow.AddMinutes(30) });
            await db.SaveChangesAsync();
        }

        var scan = await _h.ScanAsync(dst);

        Assert.Equal(0, scan.NodesMoved);
        Assert.Equal(src, (await _h.NodeByIdAsync(v1.Id)).LibraryId);
    }

    [Fact]
    public async Task InLibrary_ATombstoneReturningAtANewPath_IsRecognised()
    {
        var (src, _, user) = await SeedAsync();
        var x = await _h.NodeAsync(src, "Other/x.cbz");
        await _h.MarkReadAsync(user, x.Id);
        var bytes = File.ReadAllBytes(_h.PathOf(src, "Other/x.cbz"));
        File.Delete(_h.PathOf(src, "Other/x.cbz"));
        await _h.ScanAsync(src);
        Assert.Equal(MoveTestHarness.Tombstoned, (await _h.NodeByIdAsync(x.Id)).Availability);

        File.WriteAllBytes(_h.PathOf(src, "Series/x-renamed.cbz"), bytes);
        var scan = await _h.ScanAsync(src);

        Assert.Equal(1, scan.NodesMoved);
        Assert.Equal(0, scan.NodesMovedFromOtherLibraries);
        Assert.Equal(x.Id, (await _h.NodeAsync(src, "Series/x-renamed.cbz")).Id);
    }

    [Fact]
    public async Task SourceFirst_AnArchiveAlreadyHandledAfterTheFact_IsNotMovedAgain()
    {
        var (src, dst, _) = await SeedAsync();
        var v1 = await _h.NodeAsync(src, "Series/v01.cbz");
        var other = await _h.NodeAsync(src, "Other/x.cbz");
        _h.Move(src, "Series/v01.cbz", dst, "v01.cbz");
        await _h.ScanAsync(src);
        await using (var db = _h.NewContext())
        {
            db.NodeMoves.Add(new NodeMoveEntity { FromNodeId = v1.Id, ToNodeId = other.Id, Kind = 1, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var scan = await _h.ScanAsync(dst);

        Assert.Equal(0, scan.NodesMoved);
    }
}
