namespace com.lifepixer.mangapixer.Tests.Core.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using Xunit;

/// <summary>
/// Unit tests for the volume contents scan (1.44.0): the run matcher (T = 16 bits, a run of at least 5 pages, at most max(3, 25 %) of a
/// chapter's pages without a partner, the jump split), the Known / Partial edge rule, chapters across a volume boundary, and how the
/// answer reaches the Volumes view (<see cref="VolumeContents.Apply"/> + <see cref="VolumeGrouping.Group"/>). Synthetic hashes only.
/// </summary>
public sealed class VolumeContentMatcherTests
{
    private static readonly Random s_seed = new(1234);

    /// <summary>Distinct random page hashes (unrelated pages sit ~32 bits apart).</summary>
    private static List<ulong?> Pages(int count, Random? random = null)
    {
        random ??= s_seed;
        return Enumerable.Range(0, count).Select(_ => (ulong?)(ulong)random.NextInt64(long.MinValue, long.MaxValue)).ToList();
    }

    /// <summary>The same pages from another release: <paramref name="bits"/> bits flipped per page.</summary>
    private static List<ulong?> Copy(IEnumerable<ulong?> pages, int bits, int seed = 7)
    {
        var random = new Random(seed);
        return pages.Select(p =>
        {
            var h = p!.Value;
            var flipped = new HashSet<int>();
            while (flipped.Count < bits)
                flipped.Add(random.Next(64));
            foreach (var b in flipped)
                h ^= 1UL << b;
            return (ulong?)h;
        }).ToList();
    }

    private static HashedArchive Archive(string id, decimal? number, IReadOnlyList<ulong?> pages) => new(id, number, pages);

    /// <summary>A volume: <paramref name="front"/> foreign pages, the chapters' pages in order, <paramref name="back"/> foreign pages.</summary>
    private static List<ulong?> Volume(int front, int back, params List<ulong?>[] chapters) =>
        [.. Pages(front), .. chapters.SelectMany(c => c), .. Pages(back)];

    [Fact]
    public void Chapters_FromAnotherRelease_AreFoundInsideTheVolume_InOrder_AndTheVolumeIsKnown()
    {
        var a = Pages(20);
        var b = Pages(18);
        var c = Pages(22);
        var volume = Volume(6, 12, a, b, c);
        // Another release: 12 bits off per page, a credits page added at the end of each chapter.
        var chapters = new[] { (a, 51m), (b, 52m), (c, 53m) }
            .Select((x, i) => Archive("ch" + x.Item2, x.Item2, [.. Copy(x.Item1, 12, i), .. Pages(1)]))
            .ToList();

        var result = VolumeContentMatcher.MatchFolder([Archive("v8", 8m, volume)], chapters).Single();

        Assert.Equal(VolumeContentState.Known, result.State);
        Assert.Equal(["ch51", "ch52", "ch53"], result.Chapters.Select(f => f.Id));
        Assert.Equal((6, 25), (result.Chapters[0].FirstPage, result.Chapters[0].LastPage));
        Assert.All(result.Chapters, f => Assert.Equal(f.Pages - 1, f.Matched));
        Assert.Equal(60, result.CoveredPages);
    }

    [Fact]
    public void A_ChapterMissingHere_LeavesAGap_AndTheVolumeIsOnlyPartial()
    {
        var a = Pages(20);
        var b = Pages(18);
        var c = Pages(22);
        var volume = Volume(4, 4, a, b, c);

        var result = VolumeContentMatcher.MatchFolder([Archive("v8", 8m, volume)],
            [Archive("ch51", 51m, Copy(a, 8)), Archive("ch53", 53m, Copy(c, 8))]).Single();

        Assert.Equal(VolumeContentState.Partial, result.State);
        Assert.Equal(["ch51", "ch53"], result.Chapters.Select(f => f.Id));
    }

    [Fact]
    public void Room_AtTheEdges_MakesTheVolumePartial()
    {
        var a = Pages(20);
        var front = VolumeContentMatcher.MatchFolder([Archive("v", 8m, Volume(VolumeContentMatcher.MaxPagesBefore + 1, 0, a))], [Archive("c", 1m, a)]).Single();
        var back = VolumeContentMatcher.MatchFolder([Archive("v", 8m, Volume(0, VolumeContentMatcher.MaxPagesAfter + 1, a))], [Archive("c", 1m, a)]).Single();
        var fits = VolumeContentMatcher.MatchFolder([Archive("v", 8m, Volume(VolumeContentMatcher.MaxPagesBefore, VolumeContentMatcher.MaxPagesAfter, a))], [Archive("c", 1m, a)]).Single();

        Assert.Equal(VolumeContentState.Partial, front.State);
        Assert.Equal(VolumeContentState.Partial, back.State);
        Assert.Equal(VolumeContentState.Known, fits.State);
    }

    [Fact]
    public void A_ChapterNotInTheVolume_IsNotFound()
    {
        var result = VolumeContentMatcher.MatchFolder([Archive("v", 8m, Volume(4, 4, Pages(60)))], [Archive("c", 70m, Pages(20))]).Single();

        Assert.Equal(VolumeContentState.NoneFound, result.State);
        Assert.Empty(result.Chapters);
        Assert.Equal(0, result.NearMisses);
    }

    [Fact]
    public void A_PageMatches_Within16Bits_NotBeyond()
    {
        var a = Pages(10);
        var within = VolumeContentMatcher.BestRun(Copy(a, 16), a);
        var beyond = VolumeContentMatcher.BestRun(Copy(a, 17), a);

        Assert.Equal(10, within.Count);
        Assert.Empty(beyond);
    }

    [Fact]
    public void A_RunOfFourPages_NeverProvesAChapter_EvenAShortOne()
    {
        // Owner (1.44.0 gate 1): at least 5 pages - the worst stray run between unrelated real files was 4 pages.
        var a = Pages(4);
        var shortChapter = VolumeContentMatcher.MatchFolder([Archive("v", 1m, Volume(2, 2, a))], [Archive("c", 1m, a)]).Single();
        var b = Pages(7);
        var strayRun = VolumeContentMatcher.MatchFolder([Archive("v", 1m, Volume(2, 2, b[..4]))], [Archive("c", 2m, b)]).Single();

        Assert.Empty(shortChapter.Chapters);
        Assert.Empty(strayRun.Chapters);
        Assert.True(VolumeContentMatcher.Proves(5, 8));
        Assert.False(VolumeContentMatcher.Proves(4, 7));
    }

    [Fact]
    public void Too_ManyPagesWithoutAPartner_IsANearMiss_NotAChapter()
    {
        var a = Pages(20);
        // 11 of 20 pages in the volume: more than max(3, 25 %) without a partner, but half or more - a near-miss.
        var result = VolumeContentMatcher.MatchFolder([Archive("v", 8m, Volume(4, 4, a[..11]))], [Archive("c", 1m, a)]).Single();

        Assert.Empty(result.Chapters);
        Assert.Equal(1, result.NearMisses);
        Assert.Equal(VolumeContentState.NoneFound, result.State);
    }

    [Fact]
    public void A_StrayMatchFarAway_IsCutOff_ByTheJumpSplit()
    {
        var a = Pages(20);
        var volume = Volume(30, 0, a);
        volume[2] = a[0]; // a blank-page twin of the chapter's first page far before the chapter
        var run = VolumeContentMatcher.BestRun(a, volume);

        Assert.Equal(20, run.Count);
        Assert.Equal(30, run[0].Volume);
    }

    [Fact]
    public void A_ChapterAcrossTwoVolumeFiles_BelongsToTheFirst_AndTheSecondCountsItsPagesAsExplained()
    {
        var a = Pages(20);
        var split = Pages(20);
        var b = Pages(20);
        var v1 = Volume(3, 0, a, split[..10]);
        var v2 = Volume(0, 3, split[10..], b);

        var results = VolumeContentMatcher.MatchFolder(
            [Archive("v1", 1m, v1), Archive("v2", 2m, v2)],
            [Archive("a", 1m, a), Archive("s", 2m, split), Archive("b", 3m, b)]);

        Assert.Equal(["a", "s"], results[0].Chapters.Select(f => f.Id));
        Assert.True(results[0].Chapters[1].Split);
        Assert.Equal(["b"], results[1].Chapters.Select(f => f.Id));
        Assert.Equal(VolumeContentState.Known, results[0].State);
        Assert.Equal(VolumeContentState.Known, results[1].State);
    }

    [Fact]
    public void A_ChapterInTwoVolumeFiles_StaysWithTheLongerRun()
    {
        var a = Pages(20);
        var results = VolumeContentMatcher.MatchFolder(
            [Archive("v1", 1m, Volume(2, 2, a[..16])), Archive("v2", 2m, Volume(2, 2, a))],
            [Archive("a", 1m, a)]);

        Assert.Empty(results[0].Chapters);
        Assert.Equal(["a"], results[1].Chapters.Select(f => f.Id));
    }

    [Fact]
    public void UnhashablePages_CountAsPagesWithoutAPartner()
    {
        var a = Pages(20);
        var chapter = a.Select((p, i) => i < 4 ? null : p).ToList();
        var tooMany = a.Select((p, i) => i < 6 ? null : p).ToList();

        Assert.Single(VolumeContentMatcher.MatchFolder([Archive("v", 1m, Volume(2, 2, a))], [Archive("c", 1m, chapter)]).Single().Chapters);
        Assert.Empty(VolumeContentMatcher.MatchFolder([Archive("v", 1m, Volume(2, 2, a))], [Archive("c", 1m, tooMany)]).Single().Chapters);
    }

    [Fact]
    public void A_FolderOverThePairGuard_IsNotMatched()
    {
        var volumes = Enumerable.Range(0, 201).Select(i => Archive("v" + i, i, [])).ToList();
        var chapters = Enumerable.Range(0, 100).Select(i => Archive("c" + i, i, [])).ToList();

        Assert.Empty(VolumeContentMatcher.MatchFolder(volumes, chapters));
    }

    // --- VolumeContents.Apply + VolumeGrouping.Group -------------------------------------------------------------------

    private static GroupingRow Row(string name) => new("id:" + name, GroupingRowKind.Archive, name, name.ToLowerInvariant());

    private static VolumeMapInput SixListedVolumes() =>
        new(Enumerable.Range(1, 6).Select(v => new VolumeMapVolume(v, Enumerable.Range((v - 1) * 10 + 1, 10).Select(c => (decimal)c).ToList())).ToList(),
            10, 10, true, VolumeListSource.MangaDex);

    private static ScannedVolume Scan(string volumeRow, decimal volume, VolumeContentState state, int from, int to) =>
        new(volumeRow, volume, state, Enumerable.Range(from, to - from + 1).Select(c => new ScannedChapter($"id:Series - Chapter {c:000}", c)).ToList());

    private static List<GroupingRow> LinkedFolderRows() =>
        [Row("Series v08"), .. Enumerable.Range(61, 20).Select(c => Row($"Series - Chapter {c:000}"))];

    [Fact]
    public void Without_AScan_TheVolumeAfterTheListIsEstimated()
    {
        var r = VolumeGrouping.Group(LinkedFolderRows(), SixListedVolumes());

        var stacks = r.Entries.Where(e => e.Kind == VolumeEntryKind.Stack).Select(e => e.Stack!).ToList();
        Assert.Contains(stacks, s => s.Label == "~ Volume 8");
        Assert.All(stacks, s => Assert.Empty(s.InVolumeFile));
    }

    [Fact]
    public void A_KnownVolume_IsNoLongerEstimated_HoldsItsProvenChapters_AndBoundsItsNeighbour()
    {
        var (rows, map) = VolumeContents.Apply(LinkedFolderRows(), SixListedVolumes(),
            [Scan("id:Series v08", 8m, VolumeContentState.Known, 71, 78)]);
        var r = VolumeGrouping.Group(rows, map);

        var stacks = r.Entries.Where(e => e.Kind == VolumeEntryKind.Stack).Select(e => e.Stack!).ToDictionary(s => s.Key);
        Assert.Equal("Volume 8", stacks["8"].Label);
        Assert.Equal(Enumerable.Range(71, 8).Select(c => (decimal)c), stacks["8"].InVolumeFile);
        Assert.All(stacks["8"].Members.Where(m => !m.IsVolumeArchive), m => Assert.Equal(VolumePlacement.Scanned, m.Placement));
        Assert.True(stacks["8"].HasVolumeArchive);
        // Volume 7 sits between the listed 6 and the scanned 8: bounded, not estimated.
        Assert.Equal("Volume 7", stacks["7"].Label);
        Assert.Equal(VolumeStackConfidence.Exact, stacks["7"].Confidence);
        Assert.Equal(10, stacks["7"].PresentCount);
    }

    [Fact]
    public void A_PartialVolume_HoldsItsProvenChapters_ButStaysEstimated()
    {
        var (rows, map) = VolumeContents.Apply(LinkedFolderRows(), SixListedVolumes(),
            [Scan("id:Series v08", 8m, VolumeContentState.Partial, 73, 76)]);
        var r = VolumeGrouping.Group(rows, map);

        var stack8 = r.Entries.Single(e => e.Kind == VolumeEntryKind.Stack && e.Stack!.Key == "8").Stack!;
        Assert.Equal("~ Volume 8", stack8.Label);
        Assert.Equal([73m, 74m, 75m, 76m], stack8.InVolumeFile);
        Assert.Equal(6, map!.Volumes.Count);
    }

    [Fact]
    public void A_VolumeTheListMaps_IsNeverChangedByAScan()
    {
        var (_, map) = VolumeContents.Apply(LinkedFolderRows(), SixListedVolumes(),
            [Scan("id:Series v06", 6m, VolumeContentState.Known, 61, 70)]);

        Assert.Equal(Enumerable.Range(51, 10).Select(c => (decimal)c), map!.Volumes.Single(v => v.Volume == 6m).Chapters);
    }

    [Fact]
    public void An_UnlinkedFolder_GroupsTheProvenChaptersWithTheVolumeFile_WithoutEstimatingAnythingElse()
    {
        var rows = LinkedFolderRows();
        var before = VolumeGrouping.Group(rows, null);
        var (applied, map) = VolumeContents.Apply(rows, null, [Scan("id:Series v08", 8m, VolumeContentState.Known, 71, 78)]);
        var after = VolumeGrouping.Group(applied, map);

        Assert.Equal(0, before.StackCount);
        Assert.Null(map);
        var stack = after.Entries.Single(e => e.Kind == VolumeEntryKind.Stack).Stack!;
        Assert.Equal("Volume 8", stack.Label);
        Assert.Equal(9, stack.Members.Count);
        // Read from the files: the stack's source is the file names, not a list.
        Assert.Equal(VolumeListSource.FileNames, stack.Source);
        // The other chapters stay loose: nothing estimates them without a list.
        Assert.Equal(12, after.Entries.Count(e => e.Kind == VolumeEntryKind.Archive && e.Rank != 0));
    }

    [Fact]
    public void A_WebtoonMapInChapterMode_IsNeverChanged()
    {
        var chaptersOnly = SixListedVolumes() with { ChaptersOnly = true };
        var (_, map) = VolumeContents.Apply(LinkedFolderRows(), chaptersOnly, [Scan("id:Series v08", 8m, VolumeContentState.Known, 71, 78)]);

        Assert.Same(chaptersOnly, map);
    }
}
