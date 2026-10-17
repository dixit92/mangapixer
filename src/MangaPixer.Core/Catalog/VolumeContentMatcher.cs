namespace com.lifepixer.mangapixer.Core.Catalog;

using System.Numerics;

// Volume contents scan (1.44.0). PURE: the perceptual page hashes of a folder's volume files and chapter files in, which chapter files
// each volume file holds out. No database, no clock, no path. The rule was measured on the owner's library (1.44.0 gate 1, counts only:
// 4467 real pairs, 0 false chapters) and on re-encoded copies (1.43.0): see the constants.

/// <summary>One hashed archive: an opaque id (the server's node id), its unit number (null = none) and one hash per page (null = that
/// page could not be hashed).</summary>
public sealed record HashedArchive(string Id, decimal? Number, IReadOnlyList<ulong?> Pages);

/// <summary>What the scan knows about a volume file.</summary>
public enum VolumeContentState
{
    /// <summary>No chapter file of the folder is inside it.</summary>
    NoneFound = 0,

    /// <summary>Some chapter files are inside it, but there is room at its edges or between them for chapters not here.</summary>
    Partial = 1,

    /// <summary>The chapters found leave no room for another chapter: the volume's range is known.</summary>
    Known = 2,
}

/// <summary>
/// A chapter file proven inside a volume file: the volume pages its run covers (<see cref="FirstPage"/> - <see cref="LastPage"/>,
/// zero-based ordinals), how many of its <see cref="Pages"/> matched in order, and whether it continues into the next volume file
/// (<see cref="Split"/>: the first pieces are here, the rest at the start of the next volume - it belongs to this one).
/// </summary>
public sealed record FoundChapter(string Id, decimal? Number, int FirstPage, int LastPage, int Matched, int Pages, bool Split = false);

/// <summary>The scan's answer for one volume file.</summary>
public sealed record VolumeContentResult(
    string VolumeId, decimal? Volume, VolumeContentState State, IReadOnlyList<FoundChapter> Chapters, int NearMisses, int CoveredPages);

/// <summary>
/// Which chapter files a volume file holds (1.44.0): a chapter page matches a volume page when their 64-bit perceptual hashes are at
/// most <see cref="PageMaxDistance"/> bits apart. A chapter is inside a volume only as a RUN: the longest chain of its pages matching
/// volume pages in the same order on both sides, split where it skips more than <see cref="MaxJump"/> pages on either side, the largest
/// piece kept; it needs at least <see cref="MinRunPages"/> pages and at most max(3, 25 %) of the chapter's pages without a partner
/// (credits / recruitment pages, a merged spread). A volume is <see cref="VolumeContentState.Known"/> when the chapters found leave at
/// most <see cref="MaxPagesBefore"/> pages before the first, <see cref="MaxPagesAfter"/> after the last and no gap of
/// <see cref="MinGap"/> pages or more between two; otherwise <see cref="VolumeContentState.Partial"/>.
/// </summary>
public static class VolumeContentMatcher
{
    /// <summary>Page match threshold in bits (owner, 1.44.0 gate 1: T = 16; harsh re-encoded copies p95 18, unrelated pages p50 18-20).</summary>
    public const int PageMaxDistance = 16;

    /// <summary>The fewest pages a chapter's run needs (owner, 1.44.0 gate 1: 5 - the worst real stray run was 4).</summary>
    public const int MinRunPages = 5;

    /// <summary>A run is cut where it skips more than this many pages (a stray blank-page match far away).</summary>
    public const int MaxJump = 5;

    /// <summary>The unmatched pages a chapter may have: max(3, 25 % of its pages).</summary>
    public const int MinAllowedUnmatched = 3;

    public const int MaxPagesBefore = 8;
    public const int MaxPagesAfter = 16;
    public const int MinGap = 8;

    /// <summary>Above this many chapter x volume comparisons a folder is not matched (a guard; real folders are far below it).</summary>
    public const int MaxPairs = 20_000;

    /// <summary>The run of one chapter inside one volume: (chapter page, volume page) pairs, ascending on both sides.</summary>
    public static List<(int Chapter, int Volume)> BestRun(IReadOnlyList<ulong?> chapter, IReadOnlyList<ulong?> volume)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ArgumentNullException.ThrowIfNull(volume);
        var pairs = new List<(int Chapter, int Volume)>();
        for (var i = 0; i < chapter.Count; i++)
        {
            if (chapter[i] is not { } a)
                continue;
            // Descending volume page within one chapter page, so the strictly increasing chain takes at most one partner per page.
            for (var j = volume.Count - 1; j >= 0; j--)
            {
                if (volume[j] is { } b && BitOperations.PopCount(a ^ b) <= PageMaxDistance)
                    pairs.Add((i, j));
            }
        }
        if (pairs.Count == 0)
            return [];

        // Longest strictly increasing chain on the volume page (patience sorting with predecessors).
        var tails = new List<int>();
        var tailIndex = new List<int>();
        var previous = new int[pairs.Count];
        for (var k = 0; k < pairs.Count; k++)
        {
            var j = pairs[k].Volume;
            var lo = 0;
            var hi = tails.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (tails[mid] < j) lo = mid + 1; else hi = mid;
            }
            if (lo == tails.Count)
            {
                tails.Add(j);
                tailIndex.Add(k);
            }
            else
            {
                tails[lo] = j;
                tailIndex[lo] = k;
            }
            previous[k] = lo > 0 ? tailIndex[lo - 1] : -1;
        }
        var chain = new List<(int Chapter, int Volume)>();
        for (var k = tailIndex[^1]; k >= 0; k = previous[k])
            chain.Add(pairs[k]);
        chain.Reverse();

        // Split where it jumps, keep the largest piece (the first of equal ones).
        var best = (Start: 0, Length: 1);
        var start = 0;
        for (var k = 1; k <= chain.Count; k++)
        {
            if (k < chain.Count && chain[k].Chapter - chain[k - 1].Chapter <= MaxJump + 1 && chain[k].Volume - chain[k - 1].Volume <= MaxJump + 1)
                continue;
            if (k - start > best.Length)
                best = (start, k - start);
            start = k;
        }
        return Extend(chain.GetRange(best.Start, best.Length), chapter, volume);
    }

    /// <summary>
    /// Grows a piece by the chapter pages right before and after it that match a volume page close before / after it (the longest chain
    /// may have taken a twin page far away for a tie, which the jump split then cut off).
    /// </summary>
    private static List<(int Chapter, int Volume)> Extend(List<(int Chapter, int Volume)> piece, IReadOnlyList<ulong?> chapter, IReadOnlyList<ulong?> volume)
    {
        bool Matches(int i, int j) =>
            chapter[i] is { } a && volume[j] is { } b && BitOperations.PopCount(a ^ b) <= PageMaxDistance;
        while (true)
        {
            var (ci, vj) = piece[0];
            var grown = false;
            for (var i = ci - 1; i >= Math.Max(0, ci - MaxJump - 1) && !grown; i--)
            {
                for (var j = vj - 1; j >= Math.Max(0, vj - MaxJump - 1); j--)
                {
                    if (Matches(i, j))
                    {
                        piece.Insert(0, (i, j));
                        grown = true;
                        break;
                    }
                }
            }
            if (!grown)
                break;
        }
        while (true)
        {
            var (ci, vj) = piece[^1];
            var grown = false;
            for (var i = ci + 1; i <= Math.Min(chapter.Count - 1, ci + MaxJump + 1) && !grown; i++)
            {
                for (var j = vj + 1; j <= Math.Min(volume.Count - 1, vj + MaxJump + 1); j++)
                {
                    if (Matches(i, j))
                    {
                        piece.Add((i, j));
                        grown = true;
                        break;
                    }
                }
            }
            if (!grown)
                break;
        }
        return piece;
    }

    /// <summary>True when <paramref name="matched"/> in-order pages prove a chapter of <paramref name="pages"/> pages.</summary>
    public static bool Proves(int matched, int pages) =>
        matched >= MinRunPages && pages - matched <= Math.Max(MinAllowedUnmatched, pages / 4.0);

    /// <summary>Half or more of the chapter in a run that does not prove it: counted, never shown, nothing placed.</summary>
    public static bool IsNearMiss(int matched, int pages) => !Proves(matched, pages) && matched >= 3 && matched * 2 >= pages;

    /// <summary>
    /// Matches every chapter file of a folder against every scanned volume file of it. <paramref name="volumes"/> come in volume order
    /// (a chapter split across two volume files is tested on neighbours). A chapter proven inside two volume files keeps the one with
    /// more matched pages. Returns one result per volume, in the given order; empty when the folder is over <see cref="MaxPairs"/>.
    /// </summary>
    public static IReadOnlyList<VolumeContentResult> MatchFolder(IReadOnlyList<HashedArchive> volumes, IReadOnlyList<HashedArchive> chapters)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(chapters);
        if ((long)volumes.Count * chapters.Count > MaxPairs)
            return [];

        var runs = new Dictionary<(int Chapter, int Volume), IReadOnlyList<(int Chapter, int Volume)>>();
        var owner = new Dictionary<int, (int Volume, IReadOnlyList<(int Chapter, int Volume)> Run, bool Split)>();
        var nearMiss = new HashSet<int>();
        for (var c = 0; c < chapters.Count; c++)
        {
            var n = chapters[c].Pages.Count;
            for (var v = 0; v < volumes.Count; v++)
            {
                var run = BestRun(chapters[c].Pages, volumes[v].Pages);
                runs[(c, v)] = run;
                if (Proves(run.Count, n))
                {
                    if (!owner.TryGetValue(c, out var held) || run.Count > held.Run.Count)
                        owner[c] = (v, run, false);
                }
                else if (IsNearMiss(run.Count, n))
                {
                    nearMiss.Add(c);
                }
            }
        }

        // A chapter across a volume boundary: its first pages close volume v, the rest open volume v + 1; together they prove it.
        var explained = new List<(int Volume, int First, int Last)>();
        for (var c = 0; c < chapters.Count; c++)
        {
            if (owner.ContainsKey(c))
                continue;
            var n = chapters[c].Pages.Count;
            for (var v = 0; v + 1 < volumes.Count; v++)
            {
                var head = runs[(c, v)];
                var tail = runs[(c, v + 1)];
                if (head.Count == 0 || tail.Count == 0 || head[^1].Chapter >= tail[0].Chapter)
                    continue;
                if (head[^1].Volume < volumes[v].Pages.Count - 1 - MaxPagesAfter || tail[0].Volume > MaxPagesBefore)
                    continue;
                if (!Proves(head.Count + tail.Count, n))
                    continue;
                owner[c] = (v, head, true);
                explained.Add((v + 1, tail[0].Volume, tail[^1].Volume));
                break;
            }
            if (owner.ContainsKey(c))
                nearMiss.Remove(c);
        }
        foreach (var c in owner.Keys)
            nearMiss.Remove(c);

        var results = new List<VolumeContentResult>(volumes.Count);
        for (var v = 0; v < volumes.Count; v++)
        {
            var found = owner.Where(o => o.Value.Volume == v)
                .Select(o => new FoundChapter(chapters[o.Key].Id, chapters[o.Key].Number, o.Value.Run[0].Volume, o.Value.Run[^1].Volume,
                    o.Value.Run.Count, chapters[o.Key].Pages.Count, o.Value.Split))
                .OrderBy(f => f.FirstPage)
                .ToList();
            var spans = found.Select(f => (f.FirstPage, f.LastPage))
                .Concat(explained.Where(e => e.Volume == v).Select(e => (e.First, e.Last)))
                .OrderBy(s => s.Item1)
                .ToList();
            var near = nearMiss.Count(c => runs.TryGetValue((c, v), out var r) && IsNearMiss(r.Count, chapters[c].Pages.Count));
            results.Add(new VolumeContentResult(volumes[v].Id, volumes[v].Number, StateOf(volumes[v].Pages.Count, found.Count, spans), found,
                near, Covered(spans)));
        }
        return results;
    }

    private static VolumeContentState StateOf(int volumePages, int foundCount, List<(int First, int Last)> spans)
    {
        if (foundCount == 0)
            return VolumeContentState.NoneFound;
        var before = spans[0].First;
        var end = spans.Max(s => s.Last);
        var after = volumePages - 1 - end;
        var reach = spans[0].Last;
        for (var k = 1; k < spans.Count; k++)
        {
            if (spans[k].First - reach - 1 >= MinGap)
                return VolumeContentState.Partial;
            reach = Math.Max(reach, spans[k].Last);
        }
        return before <= MaxPagesBefore && after <= MaxPagesAfter ? VolumeContentState.Known : VolumeContentState.Partial;
    }

    private static int Covered(List<(int First, int Last)> spans)
    {
        var covered = 0;
        var reach = -1;
        foreach (var (first, last) in spans)
        {
            var from = Math.Max(first, reach + 1);
            if (last >= from)
                covered += last - from + 1;
            reach = Math.Max(reach, last);
        }
        return covered;
    }
}
