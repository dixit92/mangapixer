namespace com.lifepixer.mangapixer.Tests.Core.Metadata.Reach;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.Reach;
using Xunit;

/// <summary>
/// Unit tests for the reach contradiction rule (1.30.0): (a) a volume file far past every volume total, (b) a chapter file far past
/// every chapter extent - both with the matcher's Count threshold (1.5 x + 2), so a grown series is not flagged - and (c) chapter
/// files whose stated volumes disagree with the volume list. Synthetic names.
/// </summary>
public sealed class ReachContradictionTests
{
    private static GroupingRow Archive(string name) => new("id:" + name, GroupingRowKind.Archive, name, name.ToLowerInvariant());

    private static List<GroupingRow> Volumes(int from, int to) => Enumerable.Range(from, to - from + 1).Select(v => Archive($"Series v{v:00}")).ToList();

    private static List<GroupingRow> Chapters(int from, int to) => Enumerable.Range(from, to - from + 1).Select(c => Archive($"Series c{c:000}")).ToList();

    private static VolumeMapInput EvenMap(int volumes, int per) =>
        new(Enumerable.Range(1, volumes).Select(k => new VolumeMapVolume(k, Enumerable.Range(per * (k - 1) + 1, per).Select(c => (decimal)c).ToList())).ToList(),
            per, volumes, true, VolumeListSource.MangaDex);

    [Fact]
    public void VolumeOverrun_WhenAVolumeFileIsFarPastEveryVolumeTotal()
    {
        var conflict = ReachContradiction.Check(Volumes(1, 30), null, new ReachEvidence(OriginVolumes: 5, OfficialVolumes: 4));
        Assert.Equal(new ReachConflict(ReachConflictKind.VolumeOverrun, 30, 5), conflict);
        Assert.True(conflict.Demotes);
    }

    [Fact]
    public void ChapterOverrun_WhenAChapterFileIsFarPastEveryChapterExtent()
    {
        var conflict = ReachContradiction.Check(Chapters(1, 200), EvenMap(5, 10), new ReachEvidence(LatestChapter: 40, OriginChapters: 60));
        Assert.Equal(new ReachConflict(ReachConflictKind.ChapterOverrun, 200, 60), conflict);
    }

    [Fact]
    public void AGrownSeries_OrNoExtent_IsNoConflict()
    {
        // 14 volumes against a record that knew 10: within 1.5 x 10 + 2.
        Assert.Equal(ReachConflict.None, ReachContradiction.Check(Volumes(1, 14), null, new ReachEvidence(OriginVolumes: 10)));
        Assert.Equal(ReachConflict.None, ReachContradiction.Check(Chapters(1, 80), null, new ReachEvidence(LatestChapter: 60)));
        // Nothing known about the record's extent: nothing to contradict.
        Assert.Equal(ReachConflict.None, ReachContradiction.Check(Volumes(1, 99), null, new ReachEvidence()));
        // The largest known total counts: the volume list knows 30 volumes.
        Assert.Equal(ReachConflict.None, ReachContradiction.Check(Volumes(1, 30), null, new ReachEvidence(OriginVolumes: 5, KnownVolumes: 30)));
    }

    [Fact]
    public void StructureClash_WhenStatedVolumesDisagreeWithTheList_OnlyFlags()
    {
        // The list puts chapters 1-10 in volume 1 ... chapters 91-100 in volume 10; the files say chapters 91-94 are in volume 3.
        var rows = Enumerable.Range(91, 4).Select(c => Archive($"Series v03 c{c:000}")).ToList();
        var conflict = ReachContradiction.Check(rows, EvenMap(10, 10), new ReachEvidence(OriginVolumes: 10, LatestChapter: 100));
        Assert.Equal(new ReachConflict(ReachConflictKind.StructureClash, 4, 4), conflict);
        Assert.False(conflict.Demotes);
    }

    [Fact]
    public void StructureClash_NeedsEnoughStatedChapters_AndAClearMajority()
    {
        var map = EvenMap(10, 10);
        var evidence = new ReachEvidence(OriginVolumes: 10, LatestChapter: 100);
        // Two clashing files are not enough.
        Assert.Equal(ReachConflict.None, ReachContradiction.Check([Archive("Series v03 c091"), Archive("Series v03 c092")], map, evidence));
        // Most stated volumes agree (a neighbour volume is fine: 2 away is not a clash).
        var agree = new List<GroupingRow> { Archive("Series v01 c005"), Archive("Series v02 c015"), Archive("Series v05 c030"), Archive("Series v03 c091") };
        Assert.Equal(ReachConflict.None, ReachContradiction.Check(agree, map, evidence));
        // Without an exact list there is nothing to compare with.
        Assert.Equal(ReachConflict.None, ReachContradiction.Check(Enumerable.Range(91, 4).Select(c => Archive($"Series v03 c{c:000}")).ToList(), null, evidence));
    }
}
