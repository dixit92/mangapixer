namespace com.lifepixer.mangapixer.Core.Metadata.Missing;

using System.Globalization;
using System.Text.RegularExpressions;
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
/// <para>
/// 1.39.0: files that state the same unit are copies only when their names differ by tags alone (<see cref="OtherNumbers"/>, the
/// rule MangaList's duplicates finder uses): <c>Title 009 Vol 01 Name</c> and <c>Title 001 Vol 01 Name</c> both read as volume 1, and
/// <c>Title Season 1 v01</c> / <c>Title Season 2 v01</c> both as volume 1, but each pair differs by another number, so neither is a
/// duplicate. A different group, year, edition or copy marker in brackets, or different words, still make copies.
/// </para>
/// </summary>
public static partial class DuplicateUnits
{
    /// <summary>
    /// The duplicates among ONE set of archive rows (a virtual volume's members), with no split by container folder; volumes first,
    /// each ascending. Folder rows are ignored.
    /// </summary>
    public static IReadOnlyList<DuplicateUnit> Find(IEnumerable<GroupingRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return Sorted(GroupsOf(rows.Where(r => r.Kind == GroupingRowKind.Archive))).Select(g => g.Unit).ToList();
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
            groups.AddRange(GroupsOf(container));
        return Sorted(groups);
    }

    private static List<DuplicateGroup> Sorted(IEnumerable<DuplicateGroup> groups) =>
        groups.OrderBy(g => g.Unit.Kind).ThenBy(g => g.Unit.Number).ToList();

    /// <summary>The duplicates among one set of archive rows: the same unit, and the same <see cref="OtherNumbers"/>.</summary>
    private static List<DuplicateGroup> GroupsOf(IEnumerable<GroupingRow> rows)
    {
        var byUnit = new Dictionary<(MissingUnitKind Kind, decimal Number), List<GroupingRow>>();
        foreach (var row in rows)
        {
            if (KeyOf(VolumeGrouping.UnitsOf(row)) is not { } key)
                continue;
            if (!byUnit.TryGetValue(key, out var stating))
                byUnit[key] = stating = [];
            stating.Add(row);
        }
        var groups = new List<DuplicateGroup>();
        foreach (var (key, stating) in byUnit.Where(kv => kv.Value.Count > 1).OrderBy(kv => kv.Key.Kind).ThenBy(kv => kv.Key.Number))
        {
            foreach (var alike in stating.GroupBy(r => OtherNumbers(r.Name, key.Number), StringComparer.Ordinal).Where(g => g.Count() > 1))
                groups.Add(new DuplicateGroup(new DuplicateUnit(key.Kind, key.Number, alike.Count()), alike.ToList()));
        }
        return groups;
    }

    /// <summary>
    /// The numbers a file name states outside tags (<c>[...]</c>, <c>(...)</c>, <c>{...}</c>) and outside its volume / chapter labels,
    /// leading zeros dropped, comma-joined: <c>9</c> for <c>Title 009 Vol 01 Name</c>, <c>1</c> for <c>Title Season 1 v01</c>. Every
    /// label goes whatever its number (<c>v03 c012</c> and <c>v04 c012</c> stay copies of chapter 12, one of them mislabelled). Not
    /// counted either: a download index in front of a bracketed unit (<c>0002 [Vol. 0001 Ch. 1]</c>, FMD2's form) and, in a name
    /// with no label at all, the bare unit number itself (<c>01 (2nd copy)</c>). Same rule as MangaList's <c>other_numbers</c>.
    /// </summary>
    internal static string OtherNumbers(string name, decimal number)
    {
        var stem = ArchiveExtension().Replace(name, string.Empty);
        string text = stem, before;
        do
        {
            before = text;
            text = Tag().Replace(text, " ");
        }
        while (text != before); // nested tags: "[Vol. 1 Ch. 2 - Title [group]]"
        text = UnitLabel().Replace(text, " ");
        if (DownloadIndex().Match(stem) is { Success: true } index)
            text = new Regex(@"(?<![0-9.])0*" + Plain(index.Groups["n"].Value) + @"(?![0-9.])").Replace(text, " ", 1);
        if (!AnyLabel().IsMatch(stem))
            text = new Regex(@"(?<![0-9.])" + NumberPattern(number) + "(?![0-9])").Replace(text, " ", 1); // a bare number: the unit itself
        return string.Join(',', Digits().Matches(text).Select(m => Plain(m.Value)));
    }

    /// <summary>A pattern for <paramref name="number"/> as names write it: <c>01</c>, <c>1</c>, <c>12.5</c>, <c>012.50</c>.</summary>
    private static string NumberPattern(decimal number)
    {
        var canonical = VolumeGrouping.Canonical(number);
        var dot = canonical.IndexOf('.', StringComparison.Ordinal);
        return dot < 0
            ? "0*" + Regex.Escape(canonical) + @"(?:\.0+)?"
            : "0*" + Regex.Escape(canonical[..dot]) + @"\." + Regex.Escape(canonical[(dot + 1)..]) + "0*";
    }

    private static string Plain(string digits)
    {
        var dot = digits.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
            return digits.TrimStart('0') is { Length: > 0 } whole ? whole : "0";
        return decimal.Parse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture).ToString("0.############################", CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"\.(?:cbz|cbr|cb7|cbt|zip|rar|7z|tar|pdf|epub)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArchiveExtension();

    [GeneratedRegex(@"\[[^\[\]]*\]|\([^()]*\)|\{[^{}]*\}")]
    private static partial Regex Tag();

    [GeneratedRegex(@"(?<![a-z0-9])(?:v|vol|volume|c|ch|chap|chapter|ep|episode|#)\.?\s*[0-9]+(?:\.[0-9]+)?(?:\s*-\s*[0-9]+(?:\.[0-9]+)?)?(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnitLabel();

    [GeneratedRegex(@"(?<![a-z0-9])(?:v|vol|volume|c|ch|chap|chapter)\.?\s*[0-9]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnyLabel();

    [GeneratedRegex(@"^\s*(?<n>[0-9]+)\s+\[")]
    private static partial Regex DownloadIndex();

    [GeneratedRegex(@"[0-9]+(?:\.[0-9]+)?")]
    private static partial Regex Digits();

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
