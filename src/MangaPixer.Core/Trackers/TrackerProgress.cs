namespace com.lifepixer.mangapixer.Core.Trackers;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

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
    /// it; a hole on disk does not hold it back (not "contiguous").</item>
    /// <item>A marked VOLUME archive counts its chapters only through an EXACT list (<paramref name="map"/> from MangaDex / Wikipedia), and
    /// raises volumes read only then - never from an estimate or the AniList ratio.</item>
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

        // Series heads are read over every archive of the scope (read or not), as the Volumes view does.
        var withHeads = VolumeGrouping.WithSeriesHeads(rows.Select(r => r.Row).ToList());
        int? autoChapters = null, autoVolumes = null, allChapters = null, allVolumes = null;
        for (var i = 0; i < rows.Count; i++)
        {
            if (!rows[i].Read)
                continue;
            var (chapter, volume) = NumbersOf(VolumeGrouping.UnitsOf(withHeads[i]), map, exact, volumesAllowed);
            allChapters = Max(allChapters, chapter);
            allVolumes = Max(allVolumes, volume);
            if (!rows[i].Held)
            {
                autoChapters = Max(autoChapters, chapter);
                autoVolumes = Max(autoVolumes, volume);
            }
        }
        return new TrackerProgressResult(new TrackerNumbers(autoChapters, autoVolumes), new TrackerNumbers(allChapters, allVolumes));
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

    private static int? ToCount(decimal value) => value is >= 1 and <= 100_000 ? (int)value : null;

    private static int? Max(int? a, int? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
