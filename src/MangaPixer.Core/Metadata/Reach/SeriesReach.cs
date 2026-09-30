namespace com.lifepixer.mangapixer.Core.Metadata.Reach;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Metadata.Missing;

// A series folder's REACH (1.30.0): its volume files and chapter files merged into one covered range through the stored volume
// -> chapter list (volumes 1-10 = chapters 1-90 + chapter files 85-120 -> reach 120, 85-90 counted once). PURE: rows and a map
// in, numbers out; no database, no clock, no path.

/// <summary>What <see cref="SeriesReach.Of"/> found.</summary>
public sealed record ReachResult
{
    public static ReachResult Empty { get; } = new();

    /// <summary>Whole volumes held as volume files (a range file covers its range; a fractional volume file is an extra).</summary>
    public IReadOnlySet<int> VolumeFiles { get; init; } = new HashSet<int>();

    /// <summary>Whole chapters held as chapter files (ranges, split chapters whose parts are here).</summary>
    public IReadOnlySet<int> ChapterFiles { get; init; } = new HashSet<int>();

    /// <summary>Whole chapters held at all: chapter files plus the chapters of volume files (the list, an estimate, or local names).</summary>
    public IReadOnlySet<int> Covered { get; init; } = new HashSet<int>();

    /// <summary>Chapter-file chapters that a volume file here already holds (exact list, bounded, or the file name's own volume).</summary>
    public IReadOnlySet<int> Overlap { get; init; } = new HashSet<int>();

    /// <summary>Volumes without a volume file whose every listed chapter is here as chapter files.</summary>
    public IReadOnlySet<int> HeldAsChapters { get; init; } = new HashSet<int>();

    /// <summary>Volumes without a volume file that some chapter file here belongs to, but not all of their chapters.</summary>
    public IReadOnlySet<int> PartialVolumes { get; init; } = new HashSet<int>();

    /// <summary>The volume key of a volume FILE here that already holds a chapter archive, by the archive's row id.</summary>
    public IReadOnlyDictionary<string, string> AlsoInVolume { get; init; } = new Dictionary<string, string>();

    public ReachResolution Resolution { get; init; } = ReachResolution.FileNames;

    /// <summary>The highest whole chapter held, or null.</summary>
    public int? ReachChapter => Covered.Count > 0 && Covered.Max() > 0 ? Covered.Max() : null;

    /// <summary>The highest volume held whole (a file, or all its listed chapters), or null.</summary>
    public int? ReachVolume
    {
        get
        {
            var whole = VolumeFiles.Concat(HeldAsChapters).Where(v => v >= 1).ToList();
            return whole.Count > 0 ? whole.Max() : null;
        }
    }

    /// <summary>Every volume something here belongs to (files, whole or partial chapter runs).</summary>
    public IEnumerable<int> TouchedVolumes => VolumeFiles.Concat(HeldAsChapters).Concat(PartialVolumes).Where(v => v >= 1).Distinct();

    /// <summary>True when any archive states a number (a lone chapter / volume 0, a prologue, is not one).</summary>
    public bool HasNumbers => ReachChapter is not null || ReachVolume is not null || TouchedVolumes.Any();
}

/// <summary>The reach of one series folder.</summary>
public static class SeriesReach
{
    /// <summary>At most this many chapter spans are listed.</summary>
    public const int MaxSpans = 20;

    /// <summary>
    /// The reach of a series from its archive rows (the linked folder and its unit subfolders; folder rows are ignored) and its
    /// stored volume map (null / empty: file names alone). A volume file holds the chapters the list gives it (the exact list or a
    /// bounded range: "overlap" is claimed; an estimated range counts in <see cref="ReachResult.Covered"/> only). Without a list a
    /// chapter file that states its volume (<c>v10 c085</c>) next to that volume's file is an overlap.
    /// </summary>
    public static ReachResult Of(IReadOnlyList<GroupingRow> rows, VolumeMapInput? map)
    {
        ArgumentNullException.ThrowIfNull(rows);
        map ??= VolumeMapInput.Empty;
        var resolver = new VolumeResolver(map);

        var volumeFiles = new HashSet<int>();
        var chapterRows = new List<(GroupingRow Row, UnitNumbers Units)>();
        foreach (var row in rows.Where(r => r.Kind == GroupingRowKind.Archive))
        {
            var u = VolumeGrouping.UnitsOf(row);
            if (u.Chapter is not null)
            {
                chapterRows.Add((row, u));
            }
            else if (u.Volume is { } v && decimal.Truncate(v) == v && v >= 1)
            {
                var end = u.VolumeEnd is { } e && e >= v && e - v < 200 ? (int)decimal.Floor(e) : (int)v;
                for (var i = (int)v; i <= end && i <= MissingUnits.MaxNumber; i++)
                    volumeFiles.Add(i);
            }
        }

        var chapterUnits = chapterRows.Select(c => c.Units).ToList();
        var chapterFiles = MissingUnits.NumbersOf(chapterUnits, MissingUnitKind.Chapter);
        var parts = chapterUnits.Where(u => !u.IsExtra && u.Chapter is { } c && decimal.Truncate(c) != c && u.ChapterEnd is null)
            .Select(u => u.Chapter!.Value).ToHashSet();

        // The chapters of each volume file: from the list (claimable unless estimated), else from chapter names stating it.
        var covered = new HashSet<int>(chapterFiles.Where(n => n >= 0));
        var claimable = new HashSet<int>();
        var resolution = ReachResolution.FileNames;
        foreach (var v in volumeFiles)
        {
            if (resolver.RequiredUnits(v) is { Count: > 0 } required)
            {
                var estimated = resolver.IsEstimated(v);
                resolution = estimated ? ReachResolution.Estimated : resolution == ReachResolution.Estimated ? resolution : ReachResolution.VolumeList;
                foreach (var unit in required)
                {
                    var whole = (int)decimal.Truncate(unit);
                    covered.Add(whole);
                    if (!estimated)
                        claimable.Add(whole);
                }
            }
        }

        // Which volume file holds each chapter archive ("Also in Volume N"): the name's own volume when its file is here, else the
        // list's exact / bounded placement.
        var alsoIn = new Dictionary<string, string>(StringComparer.Ordinal);
        var overlap = new HashSet<int>();
        foreach (var (row, u) in chapterRows)
        {
            var chapter = u.Chapter!.Value;
            int? holder = null;
            if (u.Volume is { } stated && decimal.Truncate(stated) == stated && volumeFiles.Contains((int)stated))
            {
                holder = (int)stated;
                foreach (var n in NumbersOfRow(u))
                    covered.Add(n);
            }
            else if (resolver.HasData && resolver.Resolve(chapter) is { } placed
                && placed.Placement is VolumePlacement.Exact or VolumePlacement.Bounded
                && decimal.Truncate(placed.Volume) == placed.Volume && volumeFiles.Contains((int)placed.Volume)
                && !resolver.IsEstimated(placed.Volume))
            {
                holder = (int)placed.Volume;
            }
            if (holder is not { } h)
                continue;
            alsoIn[row.Id] = VolumeGrouping.KeyOf(h);
            foreach (var n in NumbersOfRow(u))
                overlap.Add(n);
        }
        overlap.IntersectWith(chapterFiles);
        claimable.UnionWith(overlap);

        // Volumes held as chapters (every listed unit here) or in part.
        var held = new HashSet<int>();
        var partial = new HashSet<int>();
        var touched = new HashSet<int>();
        foreach (var (_, u) in chapterRows)
        {
            if (u.IsExtra)
                continue;
            if (u.Volume is { } stated && decimal.Truncate(stated) == stated && stated >= 1)
                touched.Add((int)stated);
            else if (resolver.HasData && resolver.Resolve(u.Chapter!.Value) is { } placed && decimal.Truncate(placed.Volume) == placed.Volume && placed.Volume >= 1)
                touched.Add((int)placed.Volume);
        }
        bool Present(decimal unit) =>
            decimal.Truncate(unit) == unit
                ? unit <= MissingUnits.MaxNumber && chapterFiles.Contains((int)unit)
                : parts.Contains(unit) || chapterFiles.Contains((int)decimal.Truncate(unit));
        foreach (var v in touched.Where(v => !volumeFiles.Contains(v)))
        {
            if (resolver.RequiredUnits(v) is { Count: > 0 } required && required.All(Present))
                held.Add(v);
            else
                partial.Add(v);
        }

        return new ReachResult
        {
            VolumeFiles = volumeFiles,
            ChapterFiles = chapterFiles,
            Covered = covered,
            Overlap = overlap,
            HeldAsChapters = held,
            PartialVolumes = partial,
            AlsoInVolume = alsoIn,
            Resolution = resolution,
        };
    }

    /// <summary>The chapter-file chapters no volume file here claims (what "+ chapters 91-120" names), ascending.</summary>
    public static IReadOnlyList<int> ChaptersOutsideVolumes(ReachResult reach)
    {
        ArgumentNullException.ThrowIfNull(reach);
        return reach.ChapterFiles.Where(n => n >= 1 && !reach.Overlap.Contains(n)).Order().ToList();
    }

    /// <summary><c>[1, 2, 3, 7, 9, 10]</c> -> <c>1-3, 7, 9-10</c> as spans (at most <paramref name="max"/>).</summary>
    public static IReadOnlyList<UnitSpanDto> Spans(IEnumerable<int> numbers, int max = MaxSpans)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        var sorted = numbers.Distinct().Order().ToList();
        var spans = new List<UnitSpanDto>();
        var i = 0;
        while (i < sorted.Count && spans.Count < max)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1)
                j++;
            spans.Add(new UnitSpanDto { From = sorted[i], To = sorted[j] });
            i = j + 1;
        }
        return spans;
    }

    /// <summary>The DTO of a reach, or null when nothing states a number.</summary>
    public static SeriesReachDto? ToDto(ReachResult reach)
    {
        ArgumentNullException.ThrowIfNull(reach);
        if (!reach.HasNumbers)
            return null;
        return new SeriesReachDto
        {
            VolumeFiles = Spans(reach.VolumeFiles),
            Chapters = Spans(ChaptersOutsideVolumes(reach)),
            ReachChapter = reach.ReachChapter,
            ReachVolume = reach.ReachVolume,
            OverlapChapters = reach.Overlap.Count,
            Resolution = reach.Resolution,
        };
    }

    // The whole chapters one chapter archive covers (a range covers its range; an extra covers none).
    private static IEnumerable<int> NumbersOfRow(UnitNumbers u) => MissingUnits.FileNumbersOf([u], MissingUnitKind.Chapter);
}
