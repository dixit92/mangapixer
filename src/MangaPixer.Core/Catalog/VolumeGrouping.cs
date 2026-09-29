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
    VolumeListSource Source)
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
}

/// <summary>A member of a volume stack: a real volume archive or one chapter (or extra).</summary>
public sealed record StackMember(GroupingRow Row, bool IsVolumeArchive, decimal? Chapter, bool IsExtra, VolumePlacement Placement);

/// <summary>A virtual volume: its members present and the whole chapters it should hold but does not.</summary>
public sealed record VolumeStack(
    string Key,
    decimal Volume,
    string Label,
    VolumeStackConfidence Confidence,
    VolumeListSource Source,
    IReadOnlyList<StackMember> Members,
    IReadOnlyList<int> MissingChapters,
    int PresentCount,
    int? ChapterCount,
    int ExtraCount,
    bool HasVolumeArchive,
    string? FirstChapter,
    string? LastChapter);

public enum VolumeEntryKind
{
    /// <summary>A real subfolder that is not merged into the list.</summary>
    Folder = 0,

    /// <summary>A real archive shown as its own card (a volume archive without chapters, or a loose chapter).</summary>
    Archive = 1,

    /// <summary>A virtual volume stack.</summary>
    Stack = 2,
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
}

public sealed record VolumeGroupingResult(IReadOnlyList<VolumeEntry> Entries, int StackCount)
{
    /// <summary>True when at least one stack formed (the Volumes view differs from the flat list).</summary>
    public bool Grouped => StackCount > 0;
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

    /// <summary>The label of a stack (<c>Vol. 3</c>, <c>~ Vol. 12</c> when estimated).</summary>
    public static string LabelOf(decimal volume, VolumeStackConfidence confidence) =>
        (confidence == VolumeStackConfidence.Estimated ? "~ Vol. " : "Vol. ") + Canonical(volume);

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

    /// <summary>
    /// Groups the rows of one folder into volume-ordered entries. <paramref name="rows"/> are the browsed folder's children
    /// (plus the archives of its merged unit subfolders); <paramref name="map"/> is the linked series' stored volume map, or
    /// null when nothing is linked (file names and ComicInfo alone still group). Entries are returned ascending by position
    /// (Rank, VolumeKey, SortKey, Id); Name descending reverses the list, never the members of a stack.
    /// </summary>
    public static VolumeGroupingResult Group(IReadOnlyList<GroupingRow> rows, VolumeMapInput? map)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var resolver = new VolumeResolver(map ?? VolumeMapInput.Empty);
        var entries = new List<VolumeEntry>();

        var chapters = new List<(GroupingRow Row, UnitNumbers Units)>();
        var volumes = new List<(GroupingRow Row, UnitNumbers Units)>();
        foreach (var row in rows)
        {
            if (row.Kind == GroupingRowKind.Folder)
            {
                entries.Add(new VolumeEntry { Kind = VolumeEntryKind.Folder, Rank = 1, VolumeKey = string.Empty, SortKey = row.SortKey, Id = row.Id, Row = row });
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
        decimal Canon(decimal v) => alias.GetValueOrDefault(v, v);

        var byVolume = new SortedDictionary<decimal, (List<StackMember> Volumes, List<StackMember> Chapters)>();
        (List<StackMember> Volumes, List<StackMember> Chapters) Bucket(decimal v)
        {
            v = Canon(v);
            if (!byVolume.TryGetValue(v, out var b))
                byVolume[v] = b = ([], []);
            return b;
        }

        foreach (var (row, u) in volumes)
            Bucket(u.Volume!.Value).Volumes.Add(new StackMember(row, true, null, false, VolumePlacement.Local));

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

        var stackCount = 0;
        foreach (var (volume, bucket) in byVolume)
        {
            if (bucket.Chapters.Count == 0)
            {
                // A volume archive with no chapters of its volume is a plain card (two editions: two cards).
                foreach (var m in bucket.Volumes)
                {
                    entries.Add(new VolumeEntry
                    {
                        Kind = VolumeEntryKind.Archive, Rank = 0, VolumeKey = SortableKey(volume), SortKey = m.Row.SortKey, Id = m.Row.Id, Row = m.Row,
                    });
                }
                continue;
            }
            var stack = BuildStack(volume, bucket.Volumes, bucket.Chapters, resolver, map ?? VolumeMapInput.Empty);
            var first = stack.Members.OrderBy(m => m.Row.SortKey, StringComparer.Ordinal).First().Row.SortKey;
            entries.Add(new VolumeEntry
            {
                Kind = VolumeEntryKind.Stack, Rank = 0, VolumeKey = SortableKey(volume), SortKey = first, Id = "vs:" + stack.Key, Stack = stack,
            });
            stackCount++;
        }

        entries.Sort(Compare);
        return new VolumeGroupingResult(entries, stackCount);
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
        decimal volume, List<StackMember> volumeArchives, List<StackMember> chapterMembers, VolumeResolver resolver, VolumeMapInput map)
    {
        var members = volumeArchives.OrderBy(m => m.Row.SortKey, StringComparer.Ordinal)
            .Concat(chapterMembers.OrderBy(m => m.Chapter).ThenBy(m => m.Row.SortKey, StringComparer.Ordinal))
            .ToList();
        var hasVolumeArchive = volumeArchives.Count > 0;
        var expected = resolver.ExpectedChapters(volume);
        var estimated = resolver.IsEstimated(volume) || members.Any(m => m.Placement == VolumePlacement.Estimated);
        var confidence = estimated ? VolumeStackConfidence.Estimated : VolumeStackConfidence.Exact;

        // A volume archive covers all its chapters: no placeholders, no incomplete mark (7.4).
        IReadOnlyList<int> missing = [];
        if (!hasVolumeArchive && expected is { Count: > 0 })
        {
            var units = chapterMembers.Select(m => UnitsOf(m.Row)).ToList();
            var holes = MissingUnits.Holes(units, MissingUnitKind.Chapter, expected.Select(e => (decimal)e));
            // The last volume of an ongoing (or estimated) series: chapters after the last one present may not exist yet.
            var present = MissingUnits.NumbersOf(units, MissingUnitKind.Chapter);
            if (resolver.IsLastVolume(volume) && (map.Ongoing || estimated) && present.Count > 0)
                holes = holes.Where(h => h < present.Max).ToList();
            missing = holes;
        }

        var extras = chapterMembers.Count(m => m.IsExtra);
        var chapterNumbers = chapterMembers.Where(m => m.Chapter is not null).Select(m => m.Chapter!.Value).Order().ToList();
        var usesMap = expected is not null || chapterMembers.Any(m => m.Placement != VolumePlacement.Local);
        var usesLocal = hasVolumeArchive || chapterMembers.Any(m => m.Placement == VolumePlacement.Local);
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
            ChapterCount: expected?.Count,
            ExtraCount: extras,
            HasVolumeArchive: hasVolumeArchive,
            FirstChapter: chapterNumbers.Count > 0 ? Canonical(chapterNumbers[0]) : null,
            LastChapter: chapterNumbers.Count > 0 ? Canonical(chapterNumbers[^1]) : null);
    }

    /// <summary>One slot of the stack view: a present member or a missing chapter's placeholder.</summary>
    public sealed record Slot(VolumeSlotKind Kind, string? Chapter, StackMember? Member);

    /// <summary>
    /// The ordered slots of a stack view: a real volume archive first, then the chapters ascending (an extra between its
    /// neighbours: 45, 45.5, 46) with a placeholder where each missing whole chapter belongs.
    /// </summary>
    public static IReadOnlyList<Slot> Slots(VolumeStack stack)
    {
        ArgumentNullException.ThrowIfNull(stack);
        var slots = new List<Slot>();
        foreach (var m in stack.Members.Where(m => m.IsVolumeArchive))
            slots.Add(new Slot(VolumeSlotKind.Item, null, m));
        var items = stack.Members.Where(m => !m.IsVolumeArchive).Select(m => (Value: m.Chapter ?? 0m, Slot: new Slot(VolumeSlotKind.Item, m.Chapter is { } c ? Canonical(c) : null, m)));
        var gaps = stack.MissingChapters.Select(n => (Value: (decimal)n, Slot: new Slot(VolumeSlotKind.Missing, n.ToString(CultureInfo.InvariantCulture), null)));
        // A missing whole chapter sorts before a present number of the same value (there is none: it is missing).
        slots.AddRange(items.Concat(gaps).OrderBy(x => x.Value).Select(x => x.Slot));
        return slots;
    }

    /// <summary>The route keys of the stacks in position order (previous / next volume of the stack view).</summary>
    public static IReadOnlyList<string> StackKeys(IReadOnlyList<VolumeEntry> entries) =>
        entries.Where(e => e.Kind == VolumeEntryKind.Stack).Select(e => e.Stack!.Key).ToList();
}
