namespace com.lifepixer.mangapixer.Server.Features.Library.Moves;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
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
/// How one volume of a new archive is covered (1.41.0): by the replaced files of that same volume (<see cref="VolumeFiles"/>, a volume
/// replacing a volume) or, without such files, by the listed chapters (<see cref="Units"/> + <see cref="Coverage"/>, 1.40.0).
/// </summary>
public sealed record UpgradeVolumePlan(
    decimal Volume, IReadOnlyList<long> VolumeFiles, UpgradeVolumeUnits? Units, UpgradeCarryOver.Coverage? Coverage)
{
    /// <summary>The replaced files that are evidence for this volume.</summary>
    public IEnumerable<long> Evidence => Coverage is { } c ? VolumeFiles.Concat(c.Evidence) : VolumeFiles;
}

/// <summary>One user's state on one replaced volume file: read (a read mark or completed progress), else the in-progress page.</summary>
public readonly record struct UpgradeVolumeFileState(long NodeId, bool Read, int Ordinal, DateTimeOffset At);

/// <summary>One user's outcome on a new archive, with the page to resume at (0 = the first page) for an in-progress one.</summary>
public readonly record struct UpgradeCarry(UpgradeOutcome Outcome, int Ordinal, long? FromNode)
{
    public static UpgradeCarry Nothing { get; } = new(UpgradeOutcome.Nothing, 0, null);
}

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

    /// <summary>A range file covers at most this many volumes (a corrupt name cannot run away).</summary>
    public const int MaxRangeVolumes = 100;

    /// <summary>
    /// The volumes a new archive holds (1.41.0): one whole volume (<see cref="VolumeOf"/>), or every whole volume of a stated RANGE of whole
    /// volumes and no chapter (<c>v01-03</c>, <c>Vol. 1-3</c>, <c>Volume 01-05</c> -> 1..5; at most <see cref="MaxRangeVolumes"/>). Empty
    /// otherwise (a chapter, a fractional volume or range end, no volume).
    /// </summary>
    public static IReadOnlyList<decimal> VolumesOf(UnitNumbers units)
    {
        if (VolumeOf(units) is { } single)
            return [single];
        if (units is { Chapter: null, ChapterEnd: null, Volume: { } from, VolumeEnd: { } to } && !units.IsExtra && from >= 1 && to > from
            && decimal.Truncate(from) == from && decimal.Truncate(to) == to && to - from < MaxRangeVolumes)
        {
            var volumes = new List<decimal>();
            for (var v = from; v <= to; v++)
                volumes.Add(v);
            return volumes;
        }
        return [];
    }

    /// <summary>Two new archives of one folder conflict when they hold a volume in common (<c>v01-03</c> next to <c>v03</c> or <c>v03-05</c>).</summary>
    public static bool Overlap(IReadOnlyList<decimal> a, IReadOnlyList<decimal> b) => a.Intersect(b).Any();

    /// <summary>
    /// The edition guard of a volume replacing a volume (1.41.0): the work a file name names, for comparison only. The title part is
    /// <see cref="TitleNormalizer.Normalize"/>'s primary title in <see cref="TitleNormalizer.ScoringForm"/> - extension, every bracket /
    /// parenthesis / brace group (scan groups, resolution, bracketed years, an English title), volume / chapter / <c>#</c> tokens removed -
    /// without the tokens <see cref="ArchiveNameAnatomy.IsReleaseTag"/> calls release tags (bare years, <c>1080p</c>, <c>hq</c>, <c>raw</c>,
    /// <c>colored</c>, ...; a bare number stays: <c>Title 2</c> is a sequel); a unit-only name (<c>Vol. 03</c>) takes its folder's title part.
    /// Then the edition words the title held (Omnibus, Deluxe, Complete, Kanzenban, "Master Edition", ...; not Digital, a source, not a cut):
    /// a kanzenban <c>v03</c> holds other chapters than a regular one.
    /// </summary>
    public static string EditionKey(string name, string? folderName)
    {
        var title = TitlePart(name, out var editions);
        if (title.Length == 0 && folderName is not null)
            title = TitlePart(folderName, out _);
        var cut = editions
            .Select(TitleNormalizer.ScoringForm)
            .Where(e => e.Length > 0 && e != "digital")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return title + "|" + string.Join(",", cut);
    }

    /// <summary>Two names of one folder name the same work and edition (<see cref="EditionKey"/>).</summary>
    public static bool SameWork(string a, string b, string? folderName) =>
        string.Equals(EditionKey(a, folderName), EditionKey(b, folderName), StringComparison.Ordinal);

    private static string TitlePart(string name, out IReadOnlyList<string> editions)
    {
        var normalized = TitleNormalizer.Normalize(name);
        editions = normalized.EditionHints;
        var tokens = TitleNormalizer.ScoringForm(normalized.Primary)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !IsYear(t) && !(t.Any(char.IsLetter) && ArchiveNameAnatomy.IsReleaseTag(t)));
        return string.Join(' ', tokens);
    }

    private static bool IsYear(string token) =>
        token.Length == 4 && token.All(char.IsAsciiDigit) && (token.StartsWith("19", StringComparison.Ordinal) || token.StartsWith("20", StringComparison.Ordinal));

    /// <summary>
    /// How each volume of a new archive is covered by the replaced files of its folder (1.41.0). A volume with replaced files of that same
    /// single volume (<paramref name="isVolumeEvidence"/>: the edition guard and "not byte-identical", checked by the caller) uses them;
    /// otherwise its listed chapters by the stored list's exact entry (1.40.0). Null - the archive gives nothing - when a volume has neither
    /// such files nor an exact list entry (no link, no list, chapter mode, an estimate): a range is never partly guessed (owner, 2026-10-11).
    /// </summary>
    public static IReadOnlyList<UpgradeVolumePlan>? Plan(
        IReadOnlyList<decimal> volumes, IReadOnlyDictionary<decimal, IReadOnlyList<decimal>>? list, IReadOnlyList<UpgradeChapterFile> files,
        Func<long, bool> isVolumeEvidence)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(isVolumeEvidence);
        var plans = new List<UpgradeVolumePlan>(volumes.Count);
        foreach (var volume in volumes)
        {
            var same = files.Where(f => VolumeOf(f.Units) == volume && isVolumeEvidence(f.NodeId)).Select(f => f.NodeId).ToList();
            if (same.Count > 0)
            {
                plans.Add(new UpgradeVolumePlan(volume, same, null, null));
                continue;
            }
            if (list is null || !list.TryGetValue(volume, out var listed))
                return null;
            var units = UnitsOf(volume, listed);
            plans.Add(new UpgradeVolumePlan(volume, [], units, Cover(units, files)));
        }
        return plans.Count == 0 ? null : plans;
    }

    /// <summary>
    /// One user's outcome on a replaced volume's files (1.41.0): the MOST RECENTLY READ file decides (the latest of its read mark / progress
    /// times; a file opened at page 1 and nothing more says nothing) - read -> READ; in progress past page 1 -> IN PROGRESS at that page.
    /// </summary>
    public static UpgradeCarry DecideVolume(IReadOnlyList<UpgradeVolumeFileState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        var latest = states.Where(s => s.Read || s.Ordinal > 0).OrderByDescending(s => s.At).ThenByDescending(s => s.NodeId).ToList();
        if (latest.Count == 0)
            return UpgradeCarry.Nothing;
        var top = latest[0];
        return new UpgradeCarry(top.Read ? UpgradeOutcome.Read : UpgradeOutcome.InProgress, top.Read ? 0 : top.Ordinal, top.NodeId);
    }

    /// <summary>
    /// One user's outcome on a new archive from its volume plans. One volume: that volume's outcome (an in-progress volume replacing a
    /// volume keeps its page). A range: READ when every volume is read; IN PROGRESS at page 1 when any volume is read or in progress.
    /// </summary>
    public static UpgradeCarry DecideTarget(
        IReadOnlyList<UpgradeVolumePlan> plans, IReadOnlySet<long> readFiles, IReadOnlyList<UpgradeVolumeFileState> volumeFileStates)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(readFiles);
        ArgumentNullException.ThrowIfNull(volumeFileStates);
        var outcomes = plans.Select(p => p.Coverage is { } coverage
                ? new UpgradeCarry(Decide(p.Units!, coverage, readFiles), 0, null)
                : DecideVolume(volumeFileStates.Where(s => p.VolumeFiles.Contains(s.NodeId)).ToList()))
            .ToList();
        if (outcomes.Count == 1)
            return outcomes[0];
        if (outcomes.Count > 0 && outcomes.All(o => o.Outcome == UpgradeOutcome.Read))
            return new UpgradeCarry(UpgradeOutcome.Read, 0, null);
        return outcomes.Any(o => o.Outcome != UpgradeOutcome.Nothing) ? new UpgradeCarry(UpgradeOutcome.InProgress, 0, null) : UpgradeCarry.Nothing;
    }

    /// <summary>
    /// The page an in-progress volume replacing a volume resumes at: the old page when both files have the same page count, else the first
    /// page (owner decision 2 of 1.40.0: no estimate).
    /// </summary>
    public static int PageFor(int oldOrdinal, int? oldPageCount, int? newPageCount) =>
        oldOrdinal > 0 && oldPageCount is { } a && newPageCount is { } b && a == b && oldOrdinal < b ? oldOrdinal : 0;
}
