namespace com.lifepixer.mangapixer.Core.Metadata.Reach;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

// Does a folder's reach contradict the record it is AUTOMATICALLY linked to (1.30.0, integrator decision on the reach design)?
// The matcher's Count rule re-applied with data that arrived after linking (the stored volume list, a refreshed record), plus a
// structure check the list makes possible. PURE; the server acts on the answer (ReachCheckService).

/// <summary>What contradicts the link, strongest first; <see cref="None"/> when nothing does.</summary>
public enum ReachConflictKind
{
    None = 0,

    /// <summary>(a) A volume file number far above every volume total known for the record: the link drops to review.</summary>
    VolumeOverrun = 1,

    /// <summary>(b) A chapter file number far above every chapter extent known for the record: the link drops to review.</summary>
    ChapterOverrun = 2,

    /// <summary>
    /// (c) Chapter files that state their volume disagree with the record's volume list by more than two volumes: a chip on the
    /// Auto-linked review list, never an unlink (editions number volumes differently).
    /// </summary>
    StructureClash = 3,
}

/// <summary>
/// What the stored record and its volume list say about the series' extent (any may be null): volume totals (origin, the list's
/// highest known volume, the official edition) and chapter extents (the latest release, the origin total, the list's highest listed
/// chapter, the official chapters). Language-independent: this is about the series, not about what is released in one language.
/// </summary>
public sealed record ReachEvidence(
    int? OriginVolumes = null,
    int? KnownVolumes = null,
    int? OfficialVolumes = null,
    int? LatestChapter = null,
    int? OriginChapters = null,
    int? ListedChapter = null,
    int? OfficialChapters = null)
{
    public int? VolumeExtent => Max(OriginVolumes, KnownVolumes, OfficialVolumes);

    public int? ChapterExtent => Max(LatestChapter, OriginChapters, ListedChapter, OfficialChapters);

    private static int? Max(params int?[] values)
    {
        var known = values.OfType<int>().Where(v => v > 0).ToList();
        return known.Count > 0 ? known.Max() : null;
    }
}

/// <summary>The answer: the kind, and the local and published numbers compared (for the log: counts only).</summary>
public sealed record ReachConflict(ReachConflictKind Kind, int Local = 0, int Published = 0)
{
    public static ReachConflict None { get; } = new(ReachConflictKind.None);

    /// <summary>(a) and (b) move an Auto link back to Needs review; (c) only flags it.</summary>
    public bool Demotes => Kind is ReachConflictKind.VolumeOverrun or ReachConflictKind.ChapterOverrun;
}

public static class ReachContradiction
{
    /// <summary>(c) needs at least this many chapter files that state their volume and that the list places.</summary>
    public const int MinStatedChapters = 3;

    /// <summary>(c) a stated volume this many volumes (or more) away from the list's is a clash.</summary>
    public const int VolumeDistance = 3;

    /// <summary>
    /// Checks one series folder: its archive rows (the series scope) against the record's extent and its volume list. The overrun
    /// rules use <see cref="CountEvidence.Exceeds"/> (local &gt; 1.5 x published + 2) - the matcher's own Count threshold, so a series
    /// that simply grew since the record was fetched is not flagged. Nothing to compare (no numbers, no extent) is no conflict.
    /// </summary>
    public static ReachConflict Check(IReadOnlyList<GroupingRow> rows, VolumeMapInput? map, ReachEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(evidence);
        map ??= VolumeMapInput.Empty;
        var reach = SeriesReach.Of(rows, map);

        if (reach.VolumeFiles.Count > 0 && evidence.VolumeExtent is { } volumes && CountEvidence.Exceeds(reach.VolumeFiles.Max(), volumes))
            return new ReachConflict(ReachConflictKind.VolumeOverrun, reach.VolumeFiles.Max(), volumes);

        var chapterFiles = reach.ChapterFiles.Where(n => n > 0).ToList();
        if (chapterFiles.Count > 0 && evidence.ChapterExtent is { } chapters && CountEvidence.Exceeds(chapterFiles.Max(), chapters))
            return new ReachConflict(ReachConflictKind.ChapterOverrun, chapterFiles.Max(), chapters);

        return StructureClash(rows, map);
    }

    // (c): chapter files naming their volume (v09 c060) against the list's exact placement of the same chapter.
    private static ReachConflict StructureClash(IReadOnlyList<GroupingRow> rows, VolumeMapInput map)
    {
        if (map.Volumes.Count == 0)
            return ReachConflict.None;
        var resolver = new VolumeResolver(map);
        int stated = 0, clashes = 0;
        foreach (var row in rows.Where(r => r.Kind == GroupingRowKind.Archive))
        {
            var u = VolumeGrouping.UnitsOf(row);
            if (u.IsExtra || u.Chapter is not { } chapter || u.Volume is not { } volume || decimal.Truncate(volume) != volume)
                continue;
            if (resolver.Resolve(chapter) is not { Placement: VolumePlacement.Exact } placed)
                continue;
            stated++;
            if (Math.Abs(placed.Volume - volume) >= VolumeDistance)
                clashes++;
        }
        return stated >= MinStatedChapters && clashes >= MinStatedChapters && clashes * 2 >= stated
            ? new ReachConflict(ReachConflictKind.StructureClash, clashes, stated)
            : ReachConflict.None;
    }
}
