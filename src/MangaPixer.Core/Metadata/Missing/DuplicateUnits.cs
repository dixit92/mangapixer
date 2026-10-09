namespace com.lifepixer.mangapixer.Core.Metadata.Missing;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

/// <summary>
/// One unit number that more than one file in the same folder states (1.31.0): chapter 1 uploaded twice, in two files of
/// 7 and 4 pages. <see cref="Number"/> is exactly what the names state (<c>45</c>, <c>45.5</c>); <see cref="Files"/> is how many files state it.
/// </summary>
public sealed record DuplicateUnit(MissingUnitKind Kind, decimal Number, int Files);

/// <summary>One duplicate with the archive rows that state it, in the rows' order (1.38.0: the export names each file).</summary>
public sealed record DuplicateGroup(DuplicateUnit Unit, IReadOnlyList<GroupingRow> Rows);

/// <summary>
/// Duplicate chapter / volume numbers (1.31.0). PURE, from the unit numbers the names state (<see cref="UnitNumbers"/>); no
/// database, no path.
/// <para>
/// What is NOT a duplicate: the parts of a split chapter (<c>2.1</c> and <c>2.2</c> are two numbers, and so is a file <c>2</c>
/// next to its parts - <see cref="MissingUnits.SplitsOf"/>), a range archive (<c>Ch. 1-5</c> says it covers several numbers; whether
/// it overlaps a single file is not "the same chapter twice" - ranges are ignored here), and a name that states no number. A
/// file that states a chapter is read as that chapter even if it also names a volume (<c>v03 c012</c> is chapter 12 of volume 3).
/// </para>
/// <para>
/// The unit of comparison is ONE folder: <see cref="FindIn"/> compares the files of each container folder on its own, so
/// <c>Season 1</c> and <c>Season 2</c> both holding a chapter 1 are not duplicates (the numbering restarts, which the report
/// already says), but two chapter 1 files in the same folder are.
/// </para>
/// </summary>
public static class DuplicateUnits
{
    /// <summary>The duplicates among the units of ONE folder, volumes first, each ascending.</summary>
    public static IReadOnlyList<DuplicateUnit> Find(IEnumerable<UnitNumbers> units)
    {
        ArgumentNullException.ThrowIfNull(units);
        var counts = new Dictionary<(MissingUnitKind Kind, decimal Number), int>();
        foreach (var u in units)
        {
            if (KeyOf(u) is { } key)
                counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        return counts.Where(kv => kv.Value > 1)
            .OrderBy(kv => kv.Key.Kind).ThenBy(kv => kv.Key.Number)
            .Select(kv => new DuplicateUnit(kv.Key.Kind, kv.Key.Number, kv.Value))
            .ToList();
    }

    /// <summary>
    /// The duplicates among archive rows, each container folder (<see cref="GroupingRow.ContainerName"/>) on its own; the
    /// units are read as the Volumes view and the Missing report read them (<see cref="VolumeGrouping.UnitsOf"/>: ComicInfo,
    /// a bare <c>01.cbz</c> in a <c>Volumes</c> folder is volume 1). Folder rows are ignored. A number duplicated in two
    /// containers is listed once per container.
    /// </summary>
    public static IReadOnlyList<DuplicateUnit> FindIn(IEnumerable<GroupingRow> rows) => GroupsIn(rows).Select(g => g.Unit).ToList();

    /// <summary>
    /// <see cref="FindIn"/> with the archive rows that state each duplicate (1.38.0, the metadata export: each file's node id, so a
    /// client can open one in the reader). Same rules and order as <see cref="FindIn"/>.
    /// </summary>
    public static IReadOnlyList<DuplicateGroup> GroupsIn(IEnumerable<GroupingRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var groups = new List<DuplicateGroup>();
        foreach (var container in rows.Where(r => r.Kind == GroupingRowKind.Archive)
                     .GroupBy(r => r.ContainerName ?? string.Empty, StringComparer.Ordinal))
        {
            var byUnit = new Dictionary<(MissingUnitKind Kind, decimal Number), List<GroupingRow>>();
            foreach (var row in container)
            {
                if (KeyOf(VolumeGrouping.UnitsOf(row)) is not { } key)
                    continue;
                if (!byUnit.TryGetValue(key, out var stating))
                    byUnit[key] = stating = [];
                stating.Add(row);
            }
            groups.AddRange(byUnit.Where(kv => kv.Value.Count > 1)
                .OrderBy(kv => kv.Key.Kind).ThenBy(kv => kv.Key.Number)
                .Select(kv => new DuplicateGroup(new DuplicateUnit(kv.Key.Kind, kv.Key.Number, kv.Value.Count), kv.Value)));
        }
        return groups.OrderBy(g => g.Unit.Kind).ThenBy(g => g.Unit.Number).ToList();
    }

    /// <summary>The unit a file states for duplicate purposes: its chapter, else its volume; null for no number or a range.</summary>
    private static (MissingUnitKind Kind, decimal Number)? KeyOf(UnitNumbers u)
    {
        if (u.IsEmpty)
            return null;
        var (kind, number, end) = u.Chapter is { } c ? (MissingUnitKind.Chapter, c, u.ChapterEnd) : (MissingUnitKind.Volume, u.Volume!.Value, u.VolumeEnd);
        return end is null ? (kind, number) : null; // a range is never a duplicate
    }

    /// <summary>The API form: the number as the names state it (<c>1</c>, <c>45.5</c>).</summary>
    public static DuplicateUnitDto ToDto(DuplicateUnit duplicate)
    {
        ArgumentNullException.ThrowIfNull(duplicate);
        return new DuplicateUnitDto { Kind = duplicate.Kind, Number = VolumeGrouping.Canonical(duplicate.Number), Files = duplicate.Files };
    }
}
