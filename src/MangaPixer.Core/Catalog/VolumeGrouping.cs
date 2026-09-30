namespace com.lifepixer.mangapixer.Core.Catalog;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Metadata.Missing;

// Virtual volume stacks (1.29.0). PURE: rows and a stored volume map in, ordered browse entries out. No database, no
// clock, no path. The server loads the rows and the map, memoises the result and pages it (VolumePaging); everything
// about WHICH chapters share a volume lives here so it is unit-tested without a database.

/// <summary>What a grouping input row is.</summary>
public enum GroupingRowKind
{
    Archive = 0,
    Folder = 1,
}

/// <summary>
/// One child of the browsed folder (or of a unit subfolder merged into it). <see cref="ContainerName"/> is the display name
/// of the unit subfolder the row came from (null = the browsed folder itself): in a <c>Volumes</c> folder a bare
/// <c>01.cbz</c> is volume 1, elsewhere a bare number is a chapter (as the Missing report reads it).
/// </summary>
public sealed record GroupingRow(
    string Id,
    GroupingRowKind Kind,
    string Name,
    string SortKey,
    string? ContainerName = null,
    int? ComicInfoVolume = null,
    string? ComicInfoNumber = null);

/// <summary>One volume of a stored map: its number and the chapters the provider places in it (ascending, extras included).</summary>
public sealed record VolumeMapVolume(decimal Volume, IReadOnlyList<decimal> Chapters);

/// <summary>
/// The stored volume knowledge of one linked series (<c>series_volume_maps</c>): the exact volume -> chapters list, the
/// average chapters per volume (the exact volumes' average, else the AniList ratio), the highest volume the provider knows
/// (caps estimated volumes) and whether the series is still running (the last volume is then open-ended).
/// </summary>
public sealed record VolumeMapInput(
    IReadOnlyList<VolumeMapVolume> Volumes,
    double? ChaptersPerVolume,
    int? KnownVolumeCount,
    bool Ongoing,
    VolumeListSource Source,
    // 1.29.0 owner rule: "missing" = released in the user's PREFERRED language. The chapters the provider lists as released in
    // that language (null = unknown: nothing is marked missing on the provider's word alone) and how many volumes are released in
    // it (today only English has a source - MangaUpdates' English publisher totals; null = unknown: no missing-volume cards).
    IReadOnlySet<decimal>? ReleasedChapters = null,
    int? ReleasedVolumeCount = null,
    string? ReleasedLanguage = null)
{
    public static VolumeMapInput Empty { get; } = new([], null, null, false, VolumeListSource.FileNames);

    /// <summary>True when the map can place a chapter at all (an exact list, or a ratio with a volume total).</summary>
    public bool HasData => Volumes.Count > 0 || (ChaptersPerVolume is >= 1 && KnownVolumeCount is > 0);
}

/// <summary>How a chapter got its volume.</summary>
public enum VolumePlacement
{
    /// <summary>The file name or ComicInfo states the volume.</summary>
    Local = 0,

    /// <summary>The provider's list places the chapter.</summary>
    Exact = 1,

    /// <summary>Between the known volumes on either side, with exactly one volume missing between them.</summary>
    Bounded = 2,

    /// <summary>Split evenly or by the average chapters per volume: an estimate.</summary>
    Estimated = 3,

    /// <summary>
    /// Between two adjacent known volumes (the list places it in neither): the end of the previous volume (owner, 1.29.0 RC).
    /// </summary>
    Adjacent = 4,
}

/// <summary>
/// A member of a volume stack: a real volume archive, one chapter (or a part of a split chapter, or an extra), or a fractional
/// volume file (<see cref="IsBonusVolume"/>: "Volume 2.5" goes at the end of volume 2's stack).
/// </summary>
public sealed record StackMember(
    GroupingRow Row, bool IsVolumeArchive, decimal? Chapter, bool IsExtra, VolumePlacement Placement, bool IsBonusVolume = false);

/// <summary>
/// A virtual volume: its members present and the chapter units it should hold but does not (<see cref="MissingChapters"/>:
/// whole chapters and parts of split chapters, "5.2"). <see cref="ChapterCount"/> counts the volume's chapters (a split chapter
/// once), <see cref="ChaptersPresent"/> the complete ones (every listed part here); both null when no list says.
/// </summary>
public sealed record VolumeStack(
    string Key,
    decimal Volume,
    string Label,
    VolumeStackConfidence Confidence,
    VolumeListSource Source,
    IReadOnlyList<StackMember> Members,
    IReadOnlyList<decimal> MissingChapters,
    int PresentCount,
    int? ChapterCount,
    int ExtraCount,
    bool HasVolumeArchive,
    string? FirstChapter,
    string? LastChapter,
    int? ChaptersPresent = null,
    string? OfficialRelease = null);

public enum VolumeEntryKind
{
    /// <summary>A real subfolder that is not merged into the list.</summary>
    Folder = 0,

    /// <summary>A real archive shown as its own card (a volume archive without chapters, or a loose chapter).</summary>
    Archive = 1,

    /// <summary>A virtual volume stack.</summary>
    Stack = 2,

    /// <summary>
    /// A whole volume with neither a volume file nor any chapter on disk (1.29.0 RC): a gap below the highest volume present,
    /// or a volume released in the preferred language after it. A placeholder, never opened.
    /// </summary>
    MissingVolume = 3,
}

/// <summary>
/// One entry of the Volumes view. Position: (<see cref="Rank"/>, <see cref="VolumeKey"/>, <see cref="SortKey"/>,
/// <see cref="Id"/>) ordered ordinally - Rank 0 = volume entries (a volume archive, a merged volume + chapter stack, a
/// chapter stack) by volume number, 1 = unmerged subfolders, 2 = loose archives ("not in a volume yet").
/// </summary>
public sealed record VolumeEntry
{
    public required VolumeEntryKind Kind { get; init; }
    public required int Rank { get; init; }

    /// <summary>Fixed-width sortable volume ("00003.00"); empty outside Rank 0.</summary>
    public required string VolumeKey { get; init; }

    /// <summary>The entry's first member's SortKey.</summary>
    public required string SortKey { get; init; }

    /// <summary>The entry's first member's id (a stack: <c>vs:&lt;key&gt;</c>), the last tiebreaker of the position.</summary>
    public required string Id { get; init; }

    /// <summary>Kinds Folder and Archive.</summary>
    public GroupingRow? Row { get; init; }

    /// <summary>Kind Stack.</summary>
    public VolumeStack? Stack { get; init; }

    /// <summary>Kind MissingVolume: the missing volume's number.</summary>
    public decimal? Volume { get; init; }
}

/// <summary>
/// The entries plus what is missing: the missing volumes (placeholders) and the number of missing chapter units - the stacks'
/// placeholders plus the gaps between loose chapters below the highest one present. 1.30.0: the missing units themselves
/// (<see cref="MissingChapterUnits"/>, ascending; parts of split chapters included) and the missing volume numbers, so the
/// reach / progress engine and the Missing report read the same answer as the Volumes view.
/// </summary>
public sealed record VolumeGroupingResult(IReadOnlyList<VolumeEntry> Entries, int StackCount, int MissingVolumeCount = 0, int MissingChapterCount = 0)
{
    public IReadOnlyList<decimal> MissingChapterUnits { get; init; } = [];

    public IReadOnlyList<int> MissingVolumeNumbers { get; init; } = [];

    /// <summary>True when at least one stack or missing-volume placeholder formed (the Volumes view differs from the flat list).</summary>
    public bool Grouped => StackCount > 0 || MissingVolumeCount > 0;

    /// <summary>At least one volume entry (a volume file, a stack or a placeholder).</summary>
    public bool HasVolumes => Entries.Any(e => e.Rank == 0);
}

/// <summary>The pure grouping function of the Volumes view.</summary>
public static class VolumeGrouping
{
    /// <summary>A chapter-archive folder groups by file names alone when at least this share of its chapters state their volume.</summary>
    public const double LocalShare = 0.5;

    /// <summary>ComicInfo volumes at or above this are a year, not a volume.</summary>
    private const int YearFloor = 1900;

    /// <summary>The fixed-width sortable form of a volume number (<c>3</c> -> <c>00003.00</c>; four digits and two decimals).</summary>
    public static string SortableKey(decimal volume) => volume.ToString("00000.00", CultureInfo.InvariantCulture);

    /// <summary>The display / route key of a volume (<c>3</c>, <c>2.5</c>).</summary>
    public static string KeyOf(decimal volume) => Canonical(volume);

    /// <summary>The label of a stack (<c>Volume 3</c>, <c>~ Volume 12</c> when estimated).</summary>
    public static string LabelOf(decimal volume, VolumeStackConfidence confidence) =>
        (confidence == VolumeStackConfidence.Estimated ? "~ Volume " : "Volume ") + Canonical(volume);

    /// <summary>A canonical invariant number string: <c>45</c>, <c>45.5</c> (no trailing zeros).</summary>
    public static string Canonical(decimal value) =>
        decimal.Truncate(value) == value
            ? decimal.Truncate(value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// The unit numbers of a row after the local rules (P2.3 rule 1): the file name, then ComicInfo <c>Volume</c> (a year or a
    /// non-positive value is ignored) and, for a name that states no chapter, ComicInfo <c>Number</c> when it differs from the
    /// volume (a volume's own issue number is not a chapter). Public for the stack view and for tests.
    /// </summary>
    public static UnitNumbers UnitsOf(GroupingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var u = AutoMatchText.UnitsOf(row.Name);
        // In a Volumes folder a bare "01.cbz" is volume 1 (as the Missing report reads it).
        if (AutoMatchText.IsVolumeFolderName(row.ContainerName) && u.Volume is null && u.Chapter is not null
            && AutoMatchText.BareNumberOf(row.Name) is not null)
        {
            u = new UnitNumbers(u.Chapter, u.ChapterEnd, null, null, u.IsExtra);
        }
        if (u.Volume is null && row.ComicInfoVolume is { } cv and >= 1 && cv < YearFloor)
        {
            if (u.Chapter is not null)
                return u with { Volume = cv, VolumeEnd = null };
            var number = ParseNumber(row.ComicInfoNumber);
            return number is { } n && n != cv && n >= 0
                ? new UnitNumbers(cv, null, n, null, decimal.Truncate(n) != n)
                : new UnitNumbers(cv, null, null, null, false);
        }
        return u;
    }

    private static decimal? ParseNumber(string? text) =>
        decimal.TryParse(text?.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>At most this many missing-volume placeholders are listed (a corrupt total cannot run away).</summary>
    public const int MaxMissingVolumes = 500;

    /// <summary>
    /// Groups the rows of one folder into volume-ordered entries. <paramref name="rows"/> are the browsed folder's children
    /// (plus the archives of its merged unit subfolders); <paramref name="map"/> is the linked series' stored volume map, or
    /// null when nothing is linked (file names and ComicInfo alone still group). Entries are returned ascending by position
    /// (Rank, VolumeKey, SortKey, Id); Name descending reverses the list, never the members of a stack.
    /// <para>
    /// "Missing" means released in the preferred language (owner, 1.29.0 RC): a chapter the list names BELOW the highest chapter
    /// present exists (a later one does) and is missing; one beyond it is missing only when <see cref="VolumeMapInput.ReleasedChapters"/>
    /// lists it. With <paramref name="markMissingVolumes"/> (a folder with its own link) a whole volume with neither a file nor a
    /// chapter here is a placeholder entry: the gaps below the highest volume present and the volumes after it up to
    /// <see cref="VolumeMapInput.ReleasedVolumeCount"/>.
    /// </para>
    /// </summary>
    public static VolumeGroupingResult Group(IReadOnlyList<GroupingRow> rows, VolumeMapInput? map, bool markMissingVolumes = false)
    {
        ArgumentNullException.ThrowIfNull(rows);
        map ??= VolumeMapInput.Empty;
        var resolver = new VolumeResolver(map);
        var entries = new List<VolumeEntry>();

        var chapters = new List<(GroupingRow Row, UnitNumbers Units)>();
        var volumes = new List<(GroupingRow Row, UnitNumbers Units)>();
        foreach (var row in rows)
        {
            if (row.Kind == GroupingRowKind.Folder)
            {
                entries.Add(new VolumeEntry
                {
                    Kind = VolumeEntryKind.Folder,
                    Rank = 1,
                    VolumeKey = string.Empty,
                    SortKey = row.SortKey,
                    Id = row.Id,
                    Row = row,
                });
                continue;
            }
            var units = UnitsOf(row);
            if (units.Chapter is not null)
                chapters.Add((row, units));
            else if (units.Volume is not null)
                volumes.Add((row, units));
            else
                entries.Add(LooseArchive(row));
        }

        // Local grouping (names / ComicInfo): with a map every stated volume counts; without one it needs at least half of
        // the chapters to state theirs (one "v01" among two hundred bare chapters is not a grouped folder).
        var localStated = chapters.Count(c => c.Units.Volume is not null);
        var localEligible = resolver.HasData || (chapters.Count > 0 && localStated >= chapters.Count * LocalShare);

        // Range volume archives (Vol. 01-05) own every volume in their range.
        var alias = new Dictionary<decimal, decimal>();
        foreach (var (_, u) in volumes.Where(v => v.Units.VolumeEnd is not null))
        {
            for (var v = decimal.Truncate(u.Volume!.Value) + 1; v <= u.VolumeEnd!.Value && v - u.Volume.Value < 200; v++)
                alias.TryAdd(v, u.Volume.Value);
        }
        // A fractional volume (a "2.5" file or list key) belongs to the previous volume (owner, 1.29.0 RC).
        decimal Canon(decimal v) => alias.GetValueOrDefault(decimal.Truncate(v), decimal.Truncate(v));

        var byVolume = new SortedDictionary<decimal, (List<StackMember> Volumes, List<StackMember> Chapters)>();
        (List<StackMember> Volumes, List<StackMember> Chapters) Bucket(decimal v)
        {
            v = Canon(v);
            if (!byVolume.TryGetValue(v, out var b))
                byVolume[v] = b = ([], []);
            return b;
        }

        var bonus = new List<(GroupingRow Row, decimal Volume)>();
        foreach (var (row, u) in volumes)
        {
            if (decimal.Truncate(u.Volume!.Value) != u.Volume.Value)
                bonus.Add((row, u.Volume.Value));
            else
                Bucket(u.Volume.Value).Volumes.Add(new StackMember(row, true, null, false, VolumePlacement.Local));
        }

        foreach (var (row, u) in chapters)
        {
            var chapter = u.Chapter!.Value;
            if (localEligible && u.Volume is { } stated)
            {
                Bucket(stated).Chapters.Add(new StackMember(row, false, chapter, u.IsExtra, VolumePlacement.Local));
                continue;
            }
            if (resolver.HasData && resolver.Resolve(chapter) is { } placed)
            {
                Bucket(placed.Volume).Chapters.Add(new StackMember(row, false, chapter, u.IsExtra, placed.Placement));
                continue;
            }
            entries.Add(LooseArchive(row));
        }

        // A fractional volume file joins the end of the previous volume's stack; alone (that volume has nothing here) it stays
        // its own card in its place.
        var bonusByVolume = new Dictionary<decimal, List<StackMember>>();
        foreach (var (row, v) in bonus)
        {
            var target = Canon(v);
            if (byVolume.ContainsKey(target))
            {
                if (!bonusByVolume.TryGetValue(target, out var list))
                    bonusByVolume[target] = list = [];
                list.Add(new StackMember(row, false, null, true, VolumePlacement.Local, IsBonusVolume: true));
            }
            else
            {
                entries.Add(new VolumeEntry { Kind = VolumeEntryKind.Archive, Rank = 0, VolumeKey = SortableKey(v), SortKey = row.SortKey, Id = row.Id, Row = row });
            }
        }

        // The highest chapter present: a chapter archive's number (a range's end), or the last chapter of a volume whose file is here.
        decimal? high = null;
        void Raise(decimal? value)
        {
            if (value is { } x && (high is null || x > high))
                high = x;
        }
        foreach (var (_, u) in chapters)
            Raise(u.ChapterEnd ?? u.Chapter);
        var fileVolumes = new HashSet<decimal>();
        foreach (var (row, u) in volumes.Where(v => decimal.Truncate(v.Units.Volume!.Value) == v.Units.Volume.Value))
        {
            for (var v = u.Volume!.Value; v <= (u.VolumeEnd ?? u.Volume.Value) && v - u.Volume.Value < 200; v++)
            {
                fileVolumes.Add(v);
                Raise(resolver.LastChapterOf(v));
            }
        }

        // A listed unit is missing only when no file in the folder holds it (1.30.0): a chapter the list places in two volumes
        // (split across the boundary) sits in the first one's stack and counts as present in the second.
        var folderUnits = chapters.Select(c => c.Units).ToList();
        var folderWholes = MissingUnits.FileNumbersOf(folderUnits, MissingUnitKind.Chapter);
        var folderSplits = MissingUnits.SplitsOf(folderUnits).Chapters;
        var folderParts = chapters.Select(c => c.Units.Chapter).OfType<decimal>().Where(c => decimal.Truncate(c) != c).ToHashSet();
        bool InFolder(decimal u) =>
            decimal.Truncate(u) == u
                ? u <= MissingUnits.MaxNumber && (folderWholes.Contains((int)u) || folderSplits.Contains((int)u))
                : folderParts.Contains(u) || folderWholes.Contains((int)decimal.Truncate(u));

        var stackCount = 0;
        var missingChapters = new HashSet<decimal>();
        foreach (var (volume, bucket) in byVolume)
        {
            var extras = bonusByVolume.GetValueOrDefault(volume) ?? [];
            if (bucket.Chapters.Count == 0 && extras.Count == 0)
            {
                // A volume archive with no chapters of its volume is a plain card (two editions: two cards).
                foreach (var m in bucket.Volumes)
                {
                    entries.Add(new VolumeEntry
                    {
                        Kind = VolumeEntryKind.Archive,
                        Rank = 0,
                        VolumeKey = SortableKey(volume),
                        SortKey = m.Row.SortKey,
                        Id = m.Row.Id,
                        Row = m.Row,
                    });
                }
                continue;
            }
            var stack = BuildStack(volume, bucket.Volumes, bucket.Chapters, extras, resolver, map, high, InFolder);
            missingChapters.UnionWith(stack.MissingChapters);
            var first = stack.Members.OrderBy(m => m.Row.SortKey, StringComparer.Ordinal).First().Row.SortKey;
            entries.Add(new VolumeEntry
            {
                Kind = VolumeEntryKind.Stack,
                Rank = 0,
                VolumeKey = SortableKey(volume),
                SortKey = first,
                Id = "vs:" + stack.Key,
                Stack = stack,
            });
            stackCount++;
        }

        // Gaps between loose chapters (no volume would take them) below the highest one present exist: count them as missing.
        var chapterUnits = chapters.Select(c => c.Units).ToList();
        var have = MissingUnits.NumbersOf(chapterUnits, MissingUnitKind.Chapter);
        foreach (var v in fileVolumes)
        {
            foreach (var unit in resolver.RequiredUnits(v) ?? [])
            {
                if (decimal.Truncate(unit) == unit && unit <= MissingUnits.MaxNumber)
                    have.Add((int)unit);
            }
        }
        if (high is { } top && have.Count > 0)
        {
            // With a list every chapter it cannot place is counted from 1; with names alone, from the lowest chapter here (the
            // chapters before it may sit in volume files whose chapters no list names).
            var from = resolver.HasData ? 1
                : Math.Max(1, chapterUnits.Where(u => !u.IsExtra).Select(u => (int?)decimal.Ceiling(u.Chapter!.Value)).Min() ?? 1);
            for (var n = from; n < top && n <= MissingUnits.MaxNumber; n++)
            {
                if (!have.Contains(n) && !missingChapters.Contains(n) && (!resolver.HasData || resolver.Resolve(n) is null))
                    missingChapters.Add(n);
            }
        }

        var missingVolumes = 0;
        var missingVolumeNumbers = new List<int>();
        if (markMissingVolumes)
        {
            var present = byVolume.Keys.Where(v => v >= 1).ToHashSet();
            present.UnionWith(alias.Keys);
            present.UnionWith(fileVolumes);
            if (present.Count > 0)
            {
                var highest = (int)present.Max();
                var limit = Math.Min(Math.Max(highest, map.ReleasedVolumeCount ?? 0), highest + MaxMissingVolumes);
                for (var v = 1; v <= limit && missingVolumes < MaxMissingVolumes; v++)
                {
                    if (present.Contains(v))
                        continue;
                    var key = Canonical(v);
                    entries.Add(new VolumeEntry
                    {
                        Kind = VolumeEntryKind.MissingVolume,
                        Rank = 0,
                        VolumeKey = SortableKey(v),
                        SortKey = string.Empty,
                        Id = "vm:" + key,
                        Volume = v,
                    });
                    missingVolumes++;
                    missingVolumeNumbers.Add(v);
                }
            }
        }

        entries.Sort(Compare);
        return new VolumeGroupingResult(entries, stackCount, missingVolumes, missingChapters.Count)
        {
            MissingChapterUnits = missingChapters.Order().ToList(),
            MissingVolumeNumbers = missingVolumeNumbers,
        };
    }

    private static VolumeEntry LooseArchive(GroupingRow row) =>
        new() { Kind = VolumeEntryKind.Archive, Rank = 2, VolumeKey = string.Empty, SortKey = row.SortKey, Id = row.Id, Row = row };

    /// <summary>The ascending position order of entries.</summary>
    public static int Compare(VolumeEntry a, VolumeEntry b)
    {
        var c = a.Rank.CompareTo(b.Rank);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.VolumeKey, b.VolumeKey);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.SortKey, b.SortKey);
        return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
    }

    private static VolumeStack BuildStack(
        decimal volume, List<StackMember> volumeArchives, List<StackMember> chapterMembers, List<StackMember> bonusMembers,
        VolumeResolver resolver, VolumeMapInput map, decimal? high, Func<decimal, bool> inFolder)
    {
        var hasVolumeArchive = volumeArchives.Count > 0;
        var required = resolver.RequiredUnits(volume);
        var requiredSet = required?.ToHashSet() ?? [];
        var units = chapterMembers.Select(m => UnitsOf(m.Row)).ToList();
        var splits = MissingUnits.SplitsOf(units);
        // A listed part of a split chapter (4.1 when 4 is not listed) is a chapter, not an extra, and so is a part on disk of a
        // listed whole chapter (4.1 + 4.2 when the list says 4 - 1.29.1); without a list a fraction stays an extra.
        chapterMembers = chapterMembers
            .Select(m => m.Chapter is { } c && decimal.Truncate(c) != c
                ? m with { IsExtra = !requiredSet.Contains(c) && !(splits.Parts.Contains(c) && requiredSet.Contains(decimal.Truncate(c))) }
                : m)
            .ToList();
        var members = volumeArchives.OrderBy(m => m.Row.SortKey, StringComparer.Ordinal)
            .Concat(chapterMembers.OrderBy(m => m.Chapter).ThenBy(m => m.Row.SortKey, StringComparer.Ordinal))
            .Concat(bonusMembers.OrderBy(m => m.Row.SortKey, StringComparer.Ordinal))
            .ToList();
        var estimated = resolver.IsEstimated(volume) || members.Any(m => m.Placement == VolumePlacement.Estimated);
        var confidence = estimated ? VolumeStackConfidence.Estimated : VolumeStackConfidence.Exact;

        // Present units: whole chapters (a range covers its range) or their parts on disk, a listed part by itself or by its whole
        // chapter's file.
        var wholes = MissingUnits.FileNumbersOf(units, MissingUnitKind.Chapter);
        var parts = chapterMembers.Where(m => m.Chapter is { } c && decimal.Truncate(c) != c).Select(m => m.Chapter!.Value).ToHashSet();
        bool Present(decimal u) =>
            (decimal.Truncate(u) == u ? u <= MissingUnits.MaxNumber && (wholes.Contains((int)u) || splits.Chapters.Contains((int)u))
                : parts.Contains(u) || wholes.Contains((int)decimal.Truncate(u)))
            || inFolder(u);
        // A part missing between the parts here (4.1 and 4.3: 4.2) of a listed whole chapter; a later part exists, so it is released.
        var missingParts = splits.MissingParts.Where(p => requiredSet.Contains(decimal.Truncate(p))).ToList();

        // A volume archive covers all its chapters: no placeholders, no incomplete mark (7.4). Otherwise a listed unit is missing
        // when a later chapter is here (it exists), or when the list of chapters released in the preferred language names it.
        IReadOnlyList<decimal> missing = [];
        if (!hasVolumeArchive && required is { Count: > 0 })
        {
            missing = required
                .Where(u => !Present(u) && ((high is { } h && u < h) || map.ReleasedChapters?.Contains(u) == true))
                .Concat(missingParts)
                .Order()
                .ToList();
        }
        var chaptersOf = required?.GroupBy(decimal.Truncate).ToList();
        int? chapterCount = chaptersOf?.Count;
        var incomplete = missingParts.Select(decimal.Truncate).ToHashSet();
        int? chaptersPresent = chaptersOf is null ? null
            : hasVolumeArchive ? chapterCount : chaptersOf.Count(g => g.All(Present) && !incomplete.Contains(g.Key));

        var extras = chapterMembers.Count(m => m.IsExtra) + bonusMembers.Count;
        var chapterNumbers = chapterMembers.Where(m => m.Chapter is not null).Select(m => m.Chapter!.Value).Order().ToList();
        var usesMap = required is not null || chapterMembers.Any(m => m.Placement != VolumePlacement.Local);
        var usesLocal = hasVolumeArchive || bonusMembers.Count > 0 || chapterMembers.Any(m => m.Placement == VolumePlacement.Local);
        var source = usesMap && usesLocal ? VolumeListSource.Mixed : usesMap ? map.Source : VolumeListSource.FileNames;
        return new VolumeStack(
            Key: Canonical(volume),
            Volume: volume,
            Label: LabelOf(volume, confidence),
            Confidence: confidence,
            Source: source,
            Members: members,
            MissingChapters: missing,
            PresentCount: members.Count,
            ChapterCount: chapterCount,
            ExtraCount: extras,
            HasVolumeArchive: hasVolumeArchive,
            FirstChapter: chapterNumbers.Count > 0 ? Canonical(chapterNumbers[0]) : null,
            LastChapter: chapterNumbers.Count > 0 ? Canonical(chapterNumbers[^1]) : null,
            ChaptersPresent: chaptersPresent,
            OfficialRelease: OfficialReleaseOf(volume, hasVolumeArchive, map));
    }

    /// <summary>
    /// The language code when a volume held here WITHOUT a volume file is released officially in the preferred language (1.30.0,
    /// "Volume 15 available in English"): a whole volume up to <see cref="VolumeMapInput.ReleasedVolumeCount"/> (the preferred
    /// language's official volume total). Never for a volume whose file is here.
    /// </summary>
    public static string? OfficialReleaseOf(decimal volume, bool hasVolumeArchive, VolumeMapInput map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return !hasVolumeArchive && volume >= 1 && decimal.Truncate(volume) == volume && map.ReleasedVolumeCount is { } n && volume <= n
            && !string.IsNullOrEmpty(map.ReleasedLanguage)
            ? map.ReleasedLanguage
            : null;
    }

    /// <summary>One slot of the stack view: a present member or a missing chapter's placeholder.</summary>
    public sealed record Slot(VolumeSlotKind Kind, string? Chapter, StackMember? Member);

    /// <summary>
    /// The ordered slots of a stack view: a real volume archive first, then the chapters ascending (a part or an extra between
    /// its neighbours: 45, 45.5, 46) with a placeholder where each missing chapter (or part) belongs, then a fractional volume file.
    /// </summary>
    public static IReadOnlyList<Slot> Slots(VolumeStack stack)
    {
        ArgumentNullException.ThrowIfNull(stack);
        var slots = new List<Slot>();
        foreach (var m in stack.Members.Where(m => m.IsVolumeArchive))
            slots.Add(new Slot(VolumeSlotKind.Item, null, m));
        var items = stack.Members.Where(m => !m.IsVolumeArchive && !m.IsBonusVolume)
            .Select(m => (Value: m.Chapter ?? 0m, Slot: new Slot(VolumeSlotKind.Item, m.Chapter is { } c ? Canonical(c) : null, m)));
        var gaps = stack.MissingChapters.Select(n => (Value: n, Slot: new Slot(VolumeSlotKind.Missing, Canonical(n), null)));
        slots.AddRange(items.Concat(gaps).OrderBy(x => x.Value).Select(x => x.Slot));
        foreach (var m in stack.Members.Where(m => m.IsBonusVolume))
            slots.Add(new Slot(VolumeSlotKind.Item, null, m));
        return slots;
    }

    /// <summary>The route keys of the stacks in position order (previous / next volume of the stack view).</summary>
    public static IReadOnlyList<string> StackKeys(IReadOnlyList<VolumeEntry> entries) =>
        entries.Where(e => e.Kind == VolumeEntryKind.Stack).Select(e => e.Stack!.Key).ToList();
}
