namespace com.lifepixer.mangapixer.Core.Trackers;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Metadata.Missing;

// 1.43.0 tracker sync (lane E): what a user has read of one linked series, from read marks only. PURE - rows and a stored volume map
// in, numbers out. The archive -> units reading is VolumeGrouping.UnitsOf, the one the Volumes view and the Missing report use.

/// <summary>Chapters and volumes read as a tracker counts them; null = nothing to say (never sent as a number).</summary>
public readonly record struct TrackerNumbers(int? Chapters, int? Volumes)
{
    public static TrackerNumbers None => default;

    public bool IsEmpty => Chapters is null && Volumes is null;

    /// <summary>True when either number is above <paramref name="other"/>'s (a missing number counts as 0).</summary>
    public bool IsAheadOf(TrackerNumbers other) => (Chapters ?? 0) > (other.Chapters ?? 0) || (Volumes ?? 0) > (other.Volumes ?? 0);
}

/// <summary>
/// One archive of the series scope: its grouping row (name, unit folder, ComicInfo), whether the user has a read mark on it, and
/// whether that mark syncs on its own (<c>completion</c>, <c>manual</c>, <c>upgrade</c>) or waits for the user's "Send" (<c>bulk</c>
/// folder mark-read, <c>import</c>).
/// </summary>
public sealed record TrackerProgressRow(GroupingRow Row, bool Read, bool Held = false);

/// <summary>The automatic numbers (marks that sync on their own) and the numbers with every mark (what "Send" would send).</summary>
public sealed record TrackerProgressResult(TrackerNumbers Automatic, TrackerNumbers All);

/// <summary>The progress rules of tracker sync (D2 section 3 as the owner answered it, 2026-10-13).</summary>
public static class TrackerProgress
{
    /// <summary>
    /// Computes the numbers of one series for one user.
    /// <list type="bullet">
    /// <item>Chapters read = the highest WHOLE chapter whose archive (or range end) is marked; an extra or a <c>.5</c> chapter never raises
    /// it; a hole on disk does not hold it back (not "contiguous"). 1.44.0: a split chapter counts once every part here is marked (the split
    /// rule below).</item>
    /// <item>A marked VOLUME archive counts its chapters only through an EXACT list (<paramref name="map"/> from MangaDex / Wikipedia), and
    /// raises volumes read only then - never from an estimate or the AniList ratio.</item>
    /// <item>1.43.0 (owner E1): a volume also counts as read when EVERY whole chapter the exact list gives it is marked read in chapter
    /// files (a range covers its range; a split chapter counts when all its parts here are marked). Extras, <c>.5</c> and chapter 0 are
    /// never required and never enough; a volume the list gives no whole chapter never counts this way; a hole holds only that volume
    /// back (the highest complete volume counts, as for chapters).</item>
    /// <item>1.43.0: a one-shot (the only archive of the scope, its name stating no number - <see cref="TrackerUnits"/>) is chapter 1 and
    /// volume 1 (no volume for a chapters-only series or a declared edition).</item>
    /// <item>A chapters-only series (webtoon / manhwa without a real list) sends no volumes; a declared edition (omnibus, "Volumes in this
    /// edition") never sends volume numbers and never maps its volumes to chapters - chapters only where its file names state them.</item>
    /// </list>
    /// </summary>
    public static TrackerProgressResult Compute(IReadOnlyList<TrackerProgressRow> rows, VolumeMapInput? map, bool declaredEdition)
    {
        ArgumentNullException.ThrowIfNull(rows);
        map ??= VolumeMapInput.Empty;
        var exact = !map.ChaptersOnly && !declaredEdition && map.Volumes.Count > 0
            && map.Source is VolumeListSource.MangaDex or VolumeListSource.Wikipedia;
        var volumesAllowed = exact;

        // Series heads are read over every archive of the scope (read or not), as the Volumes view does; a one-shot counts as chapter 1 /
        // volume 1 (TrackerUnits - the one place that decides it).
        var trackerUnits = TrackerUnits.Of(rows.Select(r => r.Row).ToList());
        var units = trackerUnits.Select(u => u.OneShot ? default : u.Units).ToList();
        int? autoChapters = null, autoVolumes = null, allChapters = null, allVolumes = null;
        for (var i = 0; i < rows.Count; i++)
        {
            if (!rows[i].Read)
                continue;
            var (chapter, volume) = trackerUnits[i].OneShot
                ? (1, map.ChaptersOnly || declaredEdition ? null : (int?)1)
                : NumbersOf(units[i], map, exact, volumesAllowed);
            allChapters = Max(allChapters, chapter);
            allVolumes = Max(allVolumes, volume);
            if (!rows[i].Held)
            {
                autoChapters = Max(autoChapters, chapter);
                autoVolumes = Max(autoVolumes, volume);
            }
        }
        // 1.44.0 (owner, 2026-10-17): a split chapter whose parts here are all marked counts as read - a part alone holds no whole chapter,
        // so a folder whose newest chapters are split files counted only up to its last whole file.
        var markedAuto = ChaptersMarked(rows, units, includeHeld: false);
        var markedAll = ChaptersMarked(rows, units, includeHeld: true);
        autoChapters = Max(autoChapters, Highest(markedAuto));
        allChapters = Max(allChapters, Highest(markedAll));
        if (volumesAllowed)
        {
            // E1: volumes whose every listed whole chapter is marked in chapter files.
            autoVolumes = Max(autoVolumes, CompleteVolume(map, markedAuto));
            allVolumes = Max(allVolumes, CompleteVolume(map, markedAll));
        }
        return new TrackerProgressResult(new TrackerNumbers(autoChapters, autoVolumes), new TrackerNumbers(allChapters, allVolumes));
    }

    /// <summary>
    /// The whole chapters marked read in chapter files (<paramref name="includeHeld"/>: bulk / import marks too): every whole number a
    /// marked chapter archive covers (a range covers its range; extras none), plus each split chapter (<see cref="MissingUnits.SplitsOf"/>
    /// over the whole scope) whose parts here are ALL marked.
    /// </summary>
    private static HashSet<int> ChaptersMarked(IReadOnlyList<TrackerProgressRow> rows, IReadOnlyList<UnitNumbers> units, bool includeHeld)
    {
        bool Counts(int i) => rows[i].Read && (includeHeld || !rows[i].Held);
        var chapterUnits = Enumerable.Range(0, rows.Count).Where(i => units[i].Chapter is not null).ToList();
        var marked = MissingUnits.FileNumbersOf(chapterUnits.Where(Counts).Select(i => units[i]), MissingUnitKind.Chapter).ToHashSet();
        var splits = MissingUnits.SplitsOf(chapterUnits.Select(i => units[i]).ToList());
        foreach (var chapter in splits.Chapters)
        {
            if (marked.Contains(chapter) || splits.MissingParts.Any(p => decimal.Truncate(p) == chapter))
                continue;
            var parts = chapterUnits.Where(i => units[i].ChapterEnd is null && units[i].Chapter is { } c && decimal.Truncate(c) == chapter
                && (c == chapter || splits.Parts.Contains(c))).ToList();
            if (parts.Count > 0 && parts.All(Counts))
                marked.Add(chapter);
        }
        return marked;
    }

    /// <summary>
    /// The highest whole volume of the exact list whose every whole chapter (above 0) is in <paramref name="marked"/>, or null. A volume
    /// that lists no whole chapter never counts.
    /// </summary>
    private static int? CompleteVolume(VolumeMapInput map, IReadOnlySet<int> marked)
    {
        if (marked.Count == 0)
            return null;
        int? highest = null;
        foreach (var v in map.Volumes)
        {
            if (decimal.Truncate(v.Volume) != v.Volume || ToCount(v.Volume) is not { } volume)
                continue;
            var wholes = v.Chapters.Where(c => decimal.Truncate(c) == c && c >= 1).ToList();
            if (wholes.Count > 0 && wholes.All(c => c <= int.MaxValue && marked.Contains((int)c)))
                highest = Max(highest, volume);
        }
        return highest;
    }

    private static (int? Chapter, int? Volume) NumbersOf(UnitNumbers u, VolumeMapInput map, bool exact, bool volumesAllowed)
    {
        if (u.Chapter is { } start)
        {
            // A chapter archive (it may also state its volume - the chapter is what it holds). Extras never count.
            return (u.IsExtra ? null : HighestWhole(start, u.ChapterEnd ?? start), null);
        }
        if (u.Volume is not { } volume)
            return (null, null);
        var end = u.VolumeEnd ?? volume;
        if (!exact)
            return (null, null);
        int? chapter = null;
        foreach (var v in map.Volumes)
        {
            if (v.Volume < volume || v.Volume > end)
                continue;
            foreach (var c in v.Chapters)
            {
                if (decimal.Truncate(c) == c)
                    chapter = Max(chapter, ToCount(c));
            }
        }
        int? volumes = volumesAllowed && decimal.Truncate(end) == end ? ToCount(end) : null;
        return (chapter, volumes);
    }

    /// <summary>The highest whole number in [<paramref name="start"/>, <paramref name="end"/>], or null (<c>12.5</c> alone holds none).</summary>
    private static int? HighestWhole(decimal start, decimal end)
    {
        if (end < start)
            (start, end) = (end, start);
        var whole = decimal.Floor(end);
        return whole >= start ? ToCount(whole) : null;
    }

    /// <summary>The highest countable chapter of <paramref name="marked"/> (chapter 0 never counts), or null.</summary>
    private static int? Highest(IReadOnlySet<int> marked)
    {
        int? highest = null;
        foreach (var c in marked)
            highest = Max(highest, ToCount(c));
        return highest;
    }

    private static int? ToCount(decimal value) => value is >= 1 and <= 100_000 ? (int)value : null;

    private static int? Max(int? a, int? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
