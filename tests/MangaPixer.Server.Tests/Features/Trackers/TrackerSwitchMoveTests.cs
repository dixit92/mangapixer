namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Library.Moves;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// A user's tracker sync switch (owner Q3, <c>tracker_node_overrides</c>) follows a moved folder or archive like a favorite (1.43.0):
/// a switch left behind on the tombstone would silently resume syncing a series the user turned off. Service-with-DB, synthetic files.
/// </summary>
public sealed class TrackerSwitchMoveTests : IDisposable
{
    private readonly MoveTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private async Task SwitchAsync(long userId, long nodeId, bool enabled)
    {
        await using var db = _h.NewContext();
        db.TrackerNodeOverrides.Add(new TrackerNodeOverrideEntity { UserId = userId, NodeId = nodeId, Enabled = enabled, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task<bool?> SwitchOfAsync(long userId, long nodeId)
    {
        await using var db = _h.NewContext();
        return await db.TrackerNodeOverrides.Where(o => o.UserId == userId && o.NodeId == nodeId).Select(o => (bool?)o.Enabled).FirstOrDefaultAsync();
    }

    [Fact]
    public async Task AFolderRename_CarriesEachUsersSwitch_AndAUsersOwnSwitchOnTheNewFolderWins()
    {
        await _h.InitAsync();
        var library = await _h.AddLibraryAsync("library");
        var reader = await _h.AddUserAsync("reader");
        var other = await _h.AddUserAsync("other");
        _h.WriteArchive(library, "Series/v01.cbz", 1);
        _h.WriteArchive(library, "Series/v02.cbz", 2);
        await _h.ScanAsync(library);
        await _h.AnalyseLibraryAsync(library);
        var folder = await _h.NodeAsync(library, "Series");
        await SwitchAsync(reader, folder.Id, enabled: false);

        _h.Move(library, "Series", library, "Series (complete)");
        var scan = await _h.ScanAsync(library);
        await using (var db = _h.NewContext())
            Assert.Equal(1, await MoveTestHarness.CarryOver(db).CarryAsync(library, scan.Moves));

        var renamed = await _h.NodeAsync(library, "Series (complete)");
        Assert.False(await SwitchOfAsync(reader, renamed.Id));
        Assert.Null(await SwitchOfAsync(reader, folder.Id));
        Assert.Null(await SwitchOfAsync(other, renamed.Id));
    }

    [Fact]
    public async Task AnArchivePairedAcrossLibraries_CarriesTheSwitch_UnlessTheUserSetOneOnTheNewCopy()
    {
        await _h.InitAsync();
        var src = await _h.AddLibraryAsync("ongoing");
        var dst = await _h.AddLibraryAsync("concluded");
        var reader = await _h.AddUserAsync("reader");
        var other = await _h.AddUserAsync("other");
        _h.WriteArchive(src, "Series/v01.cbz", 1);
        _h.WriteArchive(src, "Series/v02.cbz", 2);
        _h.WriteArchive(src, "Series/v03.cbz", 3);
        _h.WriteArchive(src, "Other/x.cbz", 4);
        await _h.ScanAsync(src);
        await _h.ScanAsync(dst);
        await _h.AnalyseLibraryAsync(src);
        var v1 = await _h.NodeAsync(src, "Series/v01.cbz");
        await SwitchAsync(reader, v1.Id, enabled: true);
        await SwitchAsync(other, v1.Id, enabled: true);

        // Destination scanned first: the new copies exist when the source scan tombstones the old ones; the pairing pass copies state.
        _h.Move(src, "Series", dst, "Series");
        await _h.ScanAsync(dst);
        await _h.AnalyseLibraryAsync(dst);
        var n1 = await _h.NodeAsync(dst, "Series/v01.cbz");
        await SwitchAsync(other, n1.Id, enabled: false);
        await _h.ScanAsync(src);
        Assert.True((await _h.PairAsync()).Paired > 0);

        Assert.True(await SwitchOfAsync(reader, n1.Id));
        Assert.False(await SwitchOfAsync(other, n1.Id));
        Assert.True(await SwitchOfAsync(other, v1.Id));
    }
}
