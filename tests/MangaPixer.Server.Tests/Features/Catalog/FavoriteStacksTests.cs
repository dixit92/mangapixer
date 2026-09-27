namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using Xunit;

/// <summary>
/// Unit tests for the pure favorites-stacking grouping (1.27.0): threshold, direct
/// parent, folders never stacking, newest-member placement, id tie-break, and the
/// cursor walk.
/// </summary>
public sealed class FavoriteStacksTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const int Folder = (int)CatalogNodeKind.Folder;
    private const int Archive = (int)CatalogNodeKind.Archive;

    private static FavoriteStacks.Key K(long favId, int minute, long nodeId, long? parentId, int kind = Archive)
        => new(favId, T0.AddMinutes(minute), nodeId, parentId, kind);

    [Fact]
    public void TwoArchivesInOneFolder_FormOneStack_AtTheNewestMember()
    {
        var entries = FavoriteStacks.Group([K(1, 0, 10, 100), K(2, 1, 20, null), K(3, 2, 11, 100)]);

        Assert.Equal(
            new[] { (100L, (int?)2, 3L), (20L, null, 2L) },
            entries.Select(e => (e.NodeId, e.StackCount, e.RepresentativeId)).ToArray());
    }

    [Fact]
    public void BelowThreshold_RootArchives_AndFolders_StaySingles()
    {
        var entries = FavoriteStacks.Group([
            K(1, 0, 10, 100),               // alone in folder 100
            K(2, 1, 20, null),              // library root archive
            K(3, 2, 30, null),              // another root archive: no parent, no stack
            K(4, 3, 40, 200, Folder),       // two starred folders under 200 ...
            K(5, 4, 41, 200, Folder),       // ... never stack
        ]);

        Assert.All(entries, e => Assert.Null(e.StackCount));
        Assert.Equal(new long[] { 41, 40, 30, 20, 10 }, entries.Select(e => e.NodeId).ToArray());
    }

    [Fact]
    public void SameTimestamp_OrdersByFavoriteIdDescending()
    {
        var entries = FavoriteStacks.Group([K(7, 0, 10, null), K(9, 0, 20, null), K(8, 0, 30, null)]);

        Assert.Equal(new long[] { 20, 30, 10 }, entries.Select(e => e.NodeId).ToArray());
    }

    [Fact]
    public void After_ResumesStrictlyPastTheCursorEntry()
    {
        var entries = FavoriteStacks.Group([K(1, 0, 10, 100), K(2, 1, 20, null), K(3, 2, 11, 100), K(4, 3, 30, null)]);
        var first = entries[0];

        var rest = FavoriteStacks.After(entries, first.RepresentativeTicks, first.RepresentativeId).ToList();

        Assert.Equal(new long[] { 100, 20 }, rest.Select(e => e.NodeId).ToArray());
    }
}
