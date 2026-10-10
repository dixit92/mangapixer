namespace com.lifepixer.mangapixer.Server.Features.Library.Moves;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Metadata.Missing;

/// <summary>What a chapter-to-volume upgrade gives one user on the new volume archive (1.40.0, owner decisions of 2026-10-10).</summary>
public enum UpgradeOutcome
{
    /// <summary>No listed chapter of the volume was read in the replaced files.</summary>
    Nothing = 0,

    /// <summary>Some listed chapters were read, not every required one: in progress at page 1 (no page estimate).</summary>
    InProgress = 1,

    /// <summary>Every required chapter of the volume was read in the replaced files (extras optional).</summary>
    Read = 2,
}

/// <summary>
/// The units of one volume by the stored list: <see cref="Required"/> - whole chapters and the parts of a split chapter whose whole number
/// is not listed (4.1 + 4.2) - and <see cref="Extras"/> - a fraction next to its listed whole (9.5), never required.
/// </summary>
public sealed record UpgradeVolumeUnits(decimal Volume, IReadOnlyList<decimal> Required, IReadOnlyList<decimal> Extras)
{
    public bool Lists(decimal unit) => Required.Contains(unit) || Extras.Contains(unit);
}

/// <summary>A replaced (tombstoned) archive of the folder, with the unit numbers its name states.</summary>
public sealed record UpgradeChapterFile(long NodeId, UnitNumbers Units);

/// <summary>
/// The pure rules of read state that follows a chapter-to-volume upgrade (1.40.0): a volume archive that replaced its chapter archives in
/// the same folder (the scan tombstoned the chapters, then saw the volume). Read state is carried by the stored volume list only - never by
/// names alone, sizes or timestamps. Owner decisions (2026-10-10): the volume is READ for a user when every chapter the list gives for it was
/// read in the replaced files (.5-style extras optional - the 1.29.1 Missing-report rule); IN PROGRESS at page 1 when only some were read.
/// No database, no clock.
/// </summary>
public static class UpgradeCarryOver
{
    /// <summary>
    /// The new archive's volume number when its name (and ComicInfo) states exactly one whole volume and no chapter - a volume file, not a
    /// range (<c>v01-03</c>) or a fractional volume (<c>v02.5</c>). Null otherwise.
    /// </summary>
    public static decimal? VolumeOf(UnitNumbers units) =>
        units is { Chapter: null, ChapterEnd: null, VolumeEnd: null, Volume: { } v } && v >= 1 && decimal.Truncate(v) == v && !units.IsExtra
            ? v
            : null;

    /// <summary>
    /// The units of a listed volume (the rule of the Volumes view's resolver, <c>VolumeResolver.RequiredUnits</c>): every whole number, and a
    /// fraction only when its whole number is NOT listed (then it is a part of a split chapter, required); a fraction next to its listed whole
    /// is an extra.
    /// </summary>
    public static UpgradeVolumeUnits UnitsOf(decimal volume, IReadOnlyList<decimal> listedChapters)
    {
        ArgumentNullException.ThrowIfNull(listedChapters);
        var listed = listedChapters.Where(c => c >= 0 && c <= MissingUnits.MaxNumber).ToHashSet();
        var required = listed.Where(c => decimal.Truncate(c) == c || !listed.Contains(decimal.Truncate(c))).Order().ToList();
        var extras = listed.Where(c => decimal.Truncate(c) != c && listed.Contains(decimal.Truncate(c))).Order().ToList();
        return new UpgradeVolumeUnits(volume, required, extras);
    }

    /// <summary>
    /// How the replaced files cover the volume's listed units. <see cref="Direct"/>: per listed unit, the files that hold it by their own
    /// number - the chapter itself, a range over it, a whole chapter's file for a listed part of it. <see cref="Split"/>: per listed whole
    /// chapter held only as parts on disk (4.1 + 4.2 for a listed 4, <see cref="MissingUnits.SplitsOf"/>), the files of each part.
    /// <see cref="Evidence"/>: the files that cover at least one listed unit (the others are not evidence).
    /// </summary>
    public sealed record Coverage(
        IReadOnlyDictionary<decimal, IReadOnlyList<long>> Direct,
        IReadOnlyDictionary<decimal, IReadOnlyDictionary<decimal, IReadOnlyList<long>>> Split,
        IReadOnlySet<long> Evidence);

    /// <summary>
    /// Maps the replaced files of one folder onto a volume's units. A file is a candidate only when it states a chapter and states no other
    /// volume than this one; chapter numbers outside the list cover nothing.
    /// </summary>
    public static Coverage Cover(UpgradeVolumeUnits volume, IReadOnlyList<UpgradeChapterFile> files)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(files);
        var candidates = files
            .Where(f => f.Units.Chapter is not null && (f.Units.Volume is null || (f.Units.VolumeEnd is null && f.Units.Volume == volume.Volume)))
            .ToList();
        var listed = volume.Required.Concat(volume.Extras).ToList();
        var direct = new Dictionary<decimal, List<long>>();
        var evidence = new HashSet<long>();
        void Add(decimal unit, long node)
        {
            if (!direct.TryGetValue(unit, out var list))
                direct[unit] = list = [];
            if (!list.Contains(node))
                list.Add(node);
            evidence.Add(node);
        }

        foreach (var f in candidates)
        {
            var c = f.Units.Chapter!.Value;
            if (f.Units.ChapterEnd is { } end)
            {
                // A range covers the listed whole numbers in it and the listed parts of those (never an extra).
                foreach (var u in listed.Where(u => decimal.Truncate(u) >= decimal.Ceiling(c) && decimal.Truncate(u) <= decimal.Floor(end)))
                {
                    if (decimal.Truncate(u) == u || volume.Required.Contains(u))
                        Add(u, f.NodeId);
                }
                continue;
            }
            if (listed.Contains(c))
                Add(c, f.NodeId);
            // A whole chapter's file holds the listed parts of its chapter (4 on disk, 4.1 + 4.2 listed).
            if (decimal.Truncate(c) == c && !f.Units.IsExtra)
            {
                foreach (var part in volume.Required.Where(u => decimal.Truncate(u) != u && decimal.Truncate(u) == c))
                    Add(part, f.NodeId);
            }
        }

        // Parts on disk of a LISTED whole chapter (4.1 + 4.2 for a listed 4) make that chapter together (1.29.1).
        var splits = MissingUnits.SplitsOf(candidates.Select(f => f.Units).ToList());
        var split = new Dictionary<decimal, IReadOnlyDictionary<decimal, IReadOnlyList<long>>>();
        foreach (var whole in splits.Chapters.Select(n => (decimal)n).Where(n => volume.Required.Contains(n)))
        {
            var parts = new Dictionary<decimal, IReadOnlyList<long>>();
            foreach (var part in splits.Parts.Where(p => decimal.Truncate(p) == whole))
            {
                var holders = candidates.Where(f => f.Units.ChapterEnd is null && f.Units.Chapter == part).Select(f => f.NodeId).ToList();
                parts[part] = holders;
                evidence.UnionWith(holders);
            }
            if (parts.Count > 0)
                split[whole] = parts;
        }
        return new Coverage(direct.ToDictionary(k => k.Key, k => (IReadOnlyList<long>)k.Value), split, evidence);
    }

    /// <summary>
    /// One user's outcome: <paramref name="readFiles"/> are the evidence files the user read (a read mark or completed progress). A unit is
    /// read when ANY file holding it directly was read (two files of one chapter: either), or - a chapter held only as parts - when every part
    /// present has a read file. READ needs every required unit; IN PROGRESS at least one listed unit (an extra counts) without that.
    /// </summary>
    public static UpgradeOutcome Decide(UpgradeVolumeUnits volume, Coverage coverage, IReadOnlySet<long> readFiles)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(readFiles);
        bool IsRead(decimal unit) =>
            (coverage.Direct.TryGetValue(unit, out var files) && files.Any(readFiles.Contains))
            || (coverage.Split.TryGetValue(unit, out var parts) && parts.Count > 0 && parts.Values.All(p => p.Any(readFiles.Contains)));

        if (volume.Required.Count > 0 && volume.Required.All(IsRead))
            return UpgradeOutcome.Read;
        return volume.Required.Concat(volume.Extras).Any(IsRead) ? UpgradeOutcome.InProgress : UpgradeOutcome.Nothing;
    }

    /// <summary>The unit numbers of an archive as the Volumes view reads them (its name in its folder, then its ComicInfo).</summary>
    public static UnitNumbers UnitsOfArchive(string name, string? folderName, int? comicInfoVolume, string? comicInfoNumber) =>
        VolumeGrouping.UnitsOf(new GroupingRow(string.Empty, GroupingRowKind.Archive, name, string.Empty, folderName, comicInfoVolume, comicInfoNumber));
}
