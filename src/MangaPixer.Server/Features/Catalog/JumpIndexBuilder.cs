using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

namespace com.lifepixer.mangapixer.Server.Features.Catalog;

/// <summary>
/// One listed row of a level, as the jump index reads it (in the listing's ascending SortKey order). The last three fields are read only
/// by the date rail (<see cref="JumpIndexBuilder.BuildByDate"/>): the row's internal id, its <c>CreatedAt</c> and a folder's
/// <c>LatestDescendantAddedAt</c> - the parts of browse's date cursors.
/// </summary>
public sealed record JumpRow(
    string Id,
    CatalogNodeKind Kind,
    string Name,
    string SortKey,
    long InternalId = 0,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? LatestDescendantAddedAt = null);

/// <summary>
/// One chip of the rail: a contiguous run of the level's rows. <see cref="FirstCursor"/> is the browse name-sort cursor that lands on
/// <see cref="FirstNodeId"/> (null when the run starts the listing); <see cref="Range"/> is the numeric range (<c>50-99</c>) or null.
/// </summary>
public sealed record JumpBucket(string Label, int Count, string? FirstCursor, string FirstNodeId, string? Range, string? Group = null);

/// <summary>
/// The rail of one level: <see cref="JumpIndexBuilder.Letters"/>, <see cref="JumpIndexBuilder.Numbers"/> or <see cref="JumpIndexBuilder.Dates"/>,
/// buckets in display order.
/// </summary>
public sealed record JumpRail(string Mode, IReadOnlyList<JumpBucket> Buckets);

/// <summary>
/// The smart jump rail (1.41.0): a pure function from the rows of ONE listed level (a library root or a folder) to its chips.
/// <para>
/// Invariant: every bucket is one contiguous run of the rows in <c>SortKey</c> order, so its cursor (the key of the row just before
/// its first row in display order) lands on its first row and the next <c>Count</c> rows are exactly its rows - ascending and
/// descending alike.
/// </para>
/// <para>
/// Folders sort before archives (the <c>SortKey</c> kind prefix), so each kind is its own segment. In a level with both, the kind with
/// more rows (a tie: the archives) gets the smart rail and the other kind one chip, <c>Folders</c> / <c>Files</c>, at its position.
/// Per segment: a prefix that at least two thirds of the names share is ignored (<c>Ch. </c>, <c>Vol. </c>, <c>Series - </c>,
/// <c>[Group] Series v</c>, cut back to a token boundary). When two thirds of the rows then carry a number (a chapter when the names
/// state chapters, else a volume, else the stripped name's leading number), the rail is numeric with round ranges; otherwise it is
/// letters on the stripped names.
/// </para>
/// </summary>
public static partial class JumpIndexBuilder
{
    /// <summary>The rail lists letters / scripts.</summary>
    public const string Letters = "letters";

    /// <summary>The rail lists round number ranges.</summary>
    public const string Numbers = "numbers";

    /// <summary>The rail lists time chips (1.49.0: Today, This week, This month, This year, then years) under the date sorts.</summary>
    public const string Dates = "dates";

    /// <summary>Bucket group of the folders' chips at a level whose both kinds carry their own rail (1.49.0).</summary>
    public const string FoldersGroup = "folders";

    /// <summary>Bucket group of the archives' chips at a level whose both kinds carry their own rail (1.49.0).</summary>
    public const string FilesGroup = "files";

    /// <summary>
    /// At a level with both kinds, the smaller kind gets its own letters / numbers once it has at least this many rows (1.49.0, owner
    /// decision 14); below it keeps the single <see cref="FoldersLabel"/> / <see cref="FilesLabel"/> chip.
    /// </summary>
    internal const int OwnRailMinRows = 20;

    /// <summary>Label of the single chip of the folders of a level whose archives carry the rail.</summary>
    public const string FoldersLabel = "Folders";

    /// <summary>Label of the single chip of the archives of a level whose folders carry the rail.</summary>
    public const string FilesLabel = "Files";

    /// <summary>Label of a run of unnumbered rows (extras) between or around the ranges of a numeric rail, and of unknown scripts.</summary>
    public const string OtherLabel = "Other";

    /// <summary>At most this many number ranges.</summary>
    internal const int MaxNumberChips = 12;

    /// <summary>A numeric rail with more extras chips than this falls back to letters.</summary>
    internal const int MaxExtraChips = 3;

    /// <summary>One range per this many rows at most (a 33-row folder gets at most 8 ranges), never fewer than 2.</summary>
    internal const int RowsPerNumberChip = 4;

    private static readonly long[] s_stepBases = [1, 2, 5];

    /// <summary>A span of a level's rows (ascending indices) that forms one chip.</summary>
    private readonly record struct Span(int Start, int Count, string Label, string? Range, string? Group = null);

    /// <summary>
    /// Builds the rail of one level. <paramref name="rows"/> must be in the listing's ascending <c>SortKey</c> order (as the database
    /// orders the browse name sort); <paramref name="containerName"/> is the listed folder's own name (its <c>No. N</c> is a title, not
    /// an issue), null at a root.
    /// </summary>
    public static JumpRail Build(IReadOnlyList<JumpRow> rows, bool descending, string? containerName = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
            return new JumpRail(Letters, []);

        var spans = new List<Span>();
        var mode = Letters;
        var kindRuns = KindRuns(rows);
        if (kindRuns.Count == 1 || kindRuns.Count > 2)
        {
            // One kind (or, defensively, kinds that do not form two segments): the whole level is one segment.
            mode = Segment(rows, 0, rows.Count, containerName, spans);
        }
        else
        {
            var (first, second) = (kindRuns[0], kindRuns[1]);
            var main = second.Count >= first.Count ? second : first;
            var minor = ReferenceEquals(main, second) ? first : second;
            if (minor.Count >= OwnRailMinRows)
            {
                // 1.49.0 (owner decision 14): the smaller kind has enough rows for its own chips - each kind is its own segment (folders
                // first, as listed), and the chips say which kind they belong to. The level's mode is the bigger kind's.
                var folderFirst = rows[first.Start].Kind == CatalogNodeKind.Folder;
                var firstMode = Segment(rows, first.Start, first.Count, containerName, spans, folderFirst ? FoldersGroup : FilesGroup);
                var secondMode = Segment(rows, second.Start, second.Count, containerName, spans, folderFirst ? FilesGroup : FoldersGroup);
                mode = ReferenceEquals(main, first) ? firstMode : secondMode;
            }
            else
            {
                var minorSpan = new Span(minor.Start, minor.Count, rows[minor.Start].Kind == CatalogNodeKind.Folder ? FoldersLabel : FilesLabel, null);
                if (minor.Start < main.Start)
                    spans.Add(minorSpan);
                mode = Segment(rows, main.Start, main.Count, containerName, spans);
                if (minor.Start > main.Start)
                    spans.Add(minorSpan);
            }
        }

        var buckets = new List<JumpBucket>(spans.Count);
        foreach (var span in spans)
        {
            var end = span.Start + span.Count; // exclusive
            buckets.Add(descending
                ? new JumpBucket(span.Label, span.Count, end < rows.Count ? rows[end].SortKey : null, rows[end - 1].Id, span.Range, span.Group)
                : new JumpBucket(span.Label, span.Count, span.Start > 0 ? rows[span.Start - 1].SortKey : null, rows[span.Start].Id, span.Range, span.Group));
        }
        if (descending)
            buckets.Reverse();
        return new JumpRail(mode, buckets);
    }

    // ---- dates (1.49.0, owner decision 14 b) ----------------------------------------------------------------------------------------

    /// <summary>The date sort whose rows are ranked by their own <c>CreatedAt</c>: folders first, then archives, newest first.</summary>
    public const string RecentlyAddedSort = "recentlyAdded";

    /// <summary>The date sort whose rows interleave by effective recency (a folder's newest descendant, an archive's <c>CreatedAt</c>).</summary>
    public const string RecentlyUpdatedSort = "recentlyUpdated";

    internal const string TodayLabel = "Today";
    internal const string ThisWeekLabel = "This week";
    internal const string ThisMonthLabel = "This month";
    internal const string ThisYearLabel = "This year";

    /// <summary>
    /// The time-chip rail of a level listed under a date sort: <c>Today</c>, <c>This week</c> (since Monday), <c>This month</c>,
    /// <c>This year</c>, then one chip per older year, newest first - each chip one contiguous run of the listing, so its cursor is browse's
    /// own date cursor (<c>a:</c> for recentlyAdded, <c>u:</c> for recentlyUpdated) of the row just before its first row. No new cursor shape.
    /// <paramref name="rowsInDisplayOrder"/> must be in the browse order of <paramref name="sort"/> (recentlyAdded: folders first, each kind
    /// newest first; recentlyUpdated: interleaved by effective recency, folders without a descendant last), carrying the internal id and
    /// the dates. Where both kinds list under recentlyAdded each kind has its own chips (<c>Group</c> says which). Rows without a date
    /// (a folder with no descendant archive, under recentlyUpdated) form a trailing <see cref="OtherLabel"/> chip. The rail is empty when every
    /// chip would carry the same label - nothing to jump between. <paramref name="now"/> and <paramref name="utcOffset"/> place the
    /// boundaries in the viewer's day.
    /// </summary>
    public static JumpRail BuildByDate(IReadOnlyList<JumpRow> rowsInDisplayOrder, string sort, DateTimeOffset now, TimeSpan utcOffset)
    {
        ArgumentNullException.ThrowIfNull(rowsInDisplayOrder);
        var rows = rowsInDisplayOrder;
        if (rows.Count == 0 || sort is not (RecentlyAddedSort or RecentlyUpdatedSort))
            return new JumpRail(Dates, []);

        var local = now.ToOffset(utcOffset);
        var today = local.Date;
        var week = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // Monday
        var month = new DateTime(today.Year, today.Month, 1);
        var year = new DateTime(today.Year, 1, 1);

        string LabelOf(JumpRow row)
        {
            var at = sort == RecentlyAddedSort || row.Kind != CatalogNodeKind.Folder ? row.CreatedAt : row.LatestDescendantAddedAt;
            if (at is null)
                return OtherLabel;
            var day = at.Value.ToOffset(utcOffset).Date;
            if (day >= today) return TodayLabel;
            if (day >= week) return ThisWeekLabel;
            if (day >= month) return ThisMonthLabel;
            if (day >= year) return ThisYearLabel;
            return day.Year.ToString(CultureInfo.InvariantCulture);
        }

        // recentlyAdded lists the kinds one after the other (folders first), so each kind is a segment; recentlyUpdated interleaves.
        var segments = new List<(int Start, int Count)>();
        if (sort == RecentlyAddedSort)
            segments.AddRange(KindRuns(rows).Select(k => (k.Start, k.Count)));
        else
            segments.Add((0, rows.Count));
        var grouped = segments.Count == 2;

        var buckets = new List<JumpBucket>();
        foreach (var (start, count) in segments)
        {
            var group = grouped ? (rows[start].Kind == CatalogNodeKind.Folder ? FoldersGroup : FilesGroup) : null;
            void Close(int from, int to, string label) =>
                buckets.Add(new JumpBucket(label, to - from, from > 0 ? DateCursorOf(sort, rows[from - 1]) : null, rows[from].Id, null, group));

            var runStart = start;
            var runLabel = LabelOf(rows[start]);
            for (var i = start + 1; i < start + count; i++)
            {
                var label = LabelOf(rows[i]);
                if (label == runLabel)
                    continue;
                Close(runStart, i, runLabel);
                runStart = i;
                runLabel = label;
            }
            Close(runStart, start + count, runLabel);
        }

        // Every chip alike (all rows in one period, or one chip per kind of the same period): nothing to jump between.
        if (buckets.Select(b => b.Label).Distinct(StringComparer.Ordinal).Count() < 2)
            return new JumpRail(Dates, []);
        return new JumpRail(Dates, buckets);
    }

    /// <summary>
    /// Browse's date cursor of a row (<c>CatalogBrowseService.EncodeCursor</c>): <c>a:{kind}:{createdAtBinary}:{id}</c> under recentlyAdded,
    /// <c>u:{hasValue}:{effectiveBinary}:{id}:{sortKey}</c> under recentlyUpdated. Pinned against browse by the landing tests.
    /// </summary>
    internal static string DateCursorOf(string sort, JumpRow row)
    {
        var created = row.CreatedAt ?? DateTimeOffset.UnixEpoch;
        if (sort == RecentlyAddedSort)
            return string.Create(CultureInfo.InvariantCulture, $"a:{(int)row.Kind}:{created.UtcDateTime.ToBinary()}:{row.InternalId}");
        var effective = row.Kind == CatalogNodeKind.Folder ? row.LatestDescendantAddedAt : created;
        return effective is null
            ? string.Create(CultureInfo.InvariantCulture, $"u:0:0:{row.InternalId}:{row.SortKey}")
            : string.Create(CultureInfo.InvariantCulture, $"u:1:{effective.Value.UtcDateTime.ToBinary()}:{row.InternalId}:{row.SortKey}");
    }

    private sealed record KindRun(int Start, int Count);

    private static List<KindRun> KindRuns(IReadOnlyList<JumpRow> rows)
    {
        var runs = new List<KindRun>();
        var start = 0;
        for (var i = 1; i <= rows.Count; i++)
        {
            if (i == rows.Count || rows[i].Kind != rows[start].Kind)
            {
                runs.Add(new KindRun(start, i - start));
                start = i;
            }
        }
        return runs;
    }

    /// <summary>Adds the chips of one segment (<paramref name="count"/> rows from <paramref name="offset"/>) and returns its mode.</summary>
    private static string Segment(IReadOnlyList<JumpRow> rows, int offset, int count, string? containerName, List<Span> spans, string? group = null)
    {
        var names = new string[count];
        for (var i = 0; i < count; i++)
            names[i] = rows[offset + i].Name ?? string.Empty;

        var prefix = CommonPrefix(names);
        var stripped = new string[count];
        for (var i = 0; i < count; i++)
            stripped[i] = prefix.Length > 0 && names[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? names[i][prefix.Length..]
                : names[i];

        var numeric = NumberSpans(names, stripped, containerName);
        if (numeric is not null)
        {
            foreach (var s in numeric)
                spans.Add(s with { Start = s.Start + offset, Group = group });
            return Numbers;
        }
        // A common prefix that is only the opening bracket of a doujin level ("[Circle] Title" names) is not cut for the letters: the
        // bracket labels (J2, "[A" ... "[Z") need it, and they say what the letters are.
        var letterNames = prefix is "[" or "(" ? names : stripped;
        foreach (var s in LetterSpans(LetterLabels(letterNames)))
            spans.Add(s with { Start = s.Start + offset, Group = group });
        return Letters;
    }

    // ---- common prefix ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The longest prefix (case-insensitive, as the names are spelled in the first name that has it) that at least two thirds of the
    /// names share and that ends on a token boundary - never inside a run of letters, a run of digits or a decimal number - and is
    /// shorter than every name that has it. Empty when there is none or fewer than two names.
    /// </summary>
    internal static string CommonPrefix(IReadOnlyList<string> names)
    {
        if (names.Count < 2)
            return string.Empty;
        var need = TwoThirds(names.Count);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var spelled = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var folded = Fold(name);
            for (var p = 1; p < folded.Length; p++)
            {
                if (!IsBoundary(folded, p))
                    continue;
                var key = folded[..p];
                counts[key] = counts.GetValueOrDefault(key) + 1;
                spelled.TryAdd(key, name[..p]);
            }
        }
        var best = string.Empty;
        foreach (var (key, n) in counts)
        {
            if (n >= need && key.Length > best.Length)
                best = key;
        }
        return best.Length == 0 ? string.Empty : spelled[best];
    }

    private static string Fold(string name) =>
        string.Create(name.Length, name, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++) span[i] = char.ToLowerInvariant(source[i]);
        });

    /// <summary>Whether a prefix may end before index <paramref name="p"/> of <paramref name="s"/>.</summary>
    private static bool IsBoundary(string s, int p)
    {
        char a = s[p - 1], b = s[p];
        if (char.IsLetter(a) && char.IsLetter(b))
            return false;
        if (char.IsDigit(a) && char.IsDigit(b))
            return false;
        // Inside a decimal number: "10|.5" and "10.|5".
        if (char.IsDigit(a) && b == '.' && p + 1 < s.Length && char.IsDigit(s[p + 1]))
            return false;
        if (a == '.' && char.IsDigit(b) && p >= 2 && char.IsDigit(s[p - 2]))
            return false;
        return true;
    }

    private static int TwoThirds(int n) => (2 * n + 2) / 3;

    // ---- numbers --------------------------------------------------------------------------------------------------------------------

    [GeneratedRegex(@"^\s*(?<n>\d{1,9}(?:\.\d{1,2})?)(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingNumber();

    /// <summary>The number a stripped name starts with (<c>0012.cbz</c> -> 12, <c>10.5 - Title</c> -> 10.5), or null.</summary>
    internal static decimal? LeadingNumberOf(string stripped)
    {
        var m = LeadingNumber().Match(stripped);
        return m.Success && decimal.TryParse(m.Groups["n"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }

    /// <summary>
    /// The numeric spans of a segment (relative indices), or null when the segment is not numeric: fewer than two thirds of the rows
    /// carry a number in the level's meaning (after the rows that break the order are set aside), or the extras need more than
    /// <see cref="MaxExtraChips"/> chips.
    /// </summary>
    private static List<Span>? NumberSpans(string[] names, string[] stripped, string? containerName)
    {
        var n = names.Length;
        var need = TwoThirds(n);
        var chapter = new decimal?[n];
        var volume = new decimal?[n];
        var bare = new decimal?[n];
        int chapters = 0, volumes = 0, bares = 0;
        for (var i = 0; i < n; i++)
        {
            var units = AutoMatchText.UnitsOf(Mask(stripped[i], containerName));
            if (units.IsEmpty)
                units = AutoMatchText.UnitsOf(Mask(names[i], containerName));
            bare[i] = LeadingNumberOf(stripped[i]);
            chapter[i] = units.Chapter ?? (units.Volume is null ? bare[i] : null);
            volume[i] = units.Volume ?? (units.Chapter is null ? bare[i] : null);
            if (units.Chapter is not null) chapters++;
            if (units.Volume is not null) volumes++;
            if (bare[i] is not null) bares++;
        }

        // The meaning of the level: chapters when the names state chapters, else volumes, else the bare leading number.
        var values = chapters >= need ? chapter : volumes >= need ? volume : bares >= need ? bare : null;
        if (values is null)
            return null;

        var wholes = new long?[n];
        for (var i = 0; i < n; i++)
            wholes[i] = values[i] is { } v ? (long)decimal.Floor(v) : null;
        KeepLongestNonDecreasing(wholes);
        var numbered = wholes.Count(w => w is not null);
        if (numbered < need)
            return null;

        var step = StepFor(wholes, Math.Min(MaxNumberChips, Math.Max(2, n / RowsPerNumberChip)));

        // Runs of one range (or of unnumbered rows), then unnumbered runs inside one range join it.
        var runs = new List<(long? Key, int Start, int Count)>();
        for (var i = 0; i < n; i++)
        {
            long? key = wholes[i] is { } w ? w / step : null;
            if (runs.Count > 0 && runs[^1].Key == key)
                runs[^1] = (key, runs[^1].Start, runs[^1].Count + 1);
            else
                runs.Add((key, i, 1));
        }
        var merged = new List<(long? Key, int Start, int Count)>();
        for (var r = 0; r < runs.Count; r++)
        {
            var run = runs[r];
            if (run.Key is null && merged.Count > 0 && r + 1 < runs.Count && merged[^1].Key == runs[r + 1].Key)
            {
                var next = runs[r + 1];
                merged[^1] = (merged[^1].Key, merged[^1].Start, merged[^1].Count + run.Count + next.Count);
                r++;
                continue;
            }
            merged.Add(run);
        }
        if (merged.Count(m => m.Key is null) > MaxExtraChips)
            return null;

        return merged.Select(m =>
        {
            if (m.Key is not { } k)
                return new Span(m.Start, m.Count, OtherLabel, null);
            var low = k * step;
            var high = low + step - 1;
            var label = low.ToString(CultureInfo.InvariantCulture);
            return new Span(m.Start, m.Count, label, step == 1 ? label : $"{label}-{high.ToString(CultureInfo.InvariantCulture)}");
        }).ToList();
    }

    private static string Mask(string name, string? containerName) =>
        containerName is null ? name : AutoMatchText.MaskFolderTitleNumber(name, containerName);

    /// <summary>
    /// Keeps the longest non-decreasing subsequence of the numbered rows (in listing order) and clears every other number: a row whose
    /// number does not follow the listing (an extra sorted among the numbers, another shape) is treated as unnumbered, so one outlier
    /// cannot push every later row out of order.
    /// </summary>
    internal static void KeepLongestNonDecreasing(long?[] values)
    {
        var idx = new List<int>();       // idx[k]: index of the smallest tail of a kept subsequence of length k + 1
        var prev = new int[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } v)
                continue;
            // First tail strictly greater than v (upper bound): non-decreasing.
            int lo = 0, hi = idx.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (values[idx[mid]]!.Value <= v) lo = mid + 1;
                else hi = mid;
            }
            prev[i] = lo > 0 ? idx[lo - 1] : -1;
            if (lo == idx.Count) idx.Add(i);
            else idx[lo] = i;
        }
        var keep = new bool[values.Length];
        for (var i = idx.Count > 0 ? idx[^1] : -1; i >= 0; i = prev[i])
            keep[i] = true;
        for (var i = 0; i < values.Length; i++)
        {
            if (!keep[i])
                values[i] = null;
        }
    }

    /// <summary>The smallest round step (1, 2, 5 x 10^n) that gives at most <paramref name="maxChips"/> non-empty ranges.</summary>
    internal static long StepFor(IReadOnlyList<long?> wholes, int maxChips)
    {
        for (long scale = 1; scale <= 1_000_000_000_000L; scale *= 10)
        {
            foreach (var b in s_stepBases)
            {
                var step = b * scale;
                var ranges = 0;
                long? last = null;
                foreach (var w in wholes)
                {
                    if (w is not { } value)
                        continue;
                    var key = value / step;
                    if (key != last)
                    {
                        ranges++;
                        last = key;
                    }
                }
                if (ranges <= maxChips)
                    return step;
            }
        }
        return 1_000_000_000_000L;
    }

    // ---- letters --------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The letter spans of a segment (relative indices): runs of one label; a label found in more than one run keeps its biggest run as
    /// the chip, and its other runs join the chip before them (the first chip when none precedes), so every chip stays contiguous.
    /// </summary>
    private static List<Span> LetterSpans(string[] labels)
    {
        var runs = new List<(string Label, int Start, int Count)>();
        for (var i = 0; i < labels.Length; i++)
        {
            var label = labels[i];
            if (runs.Count > 0 && runs[^1].Label == label)
                runs[^1] = (label, runs[^1].Start, runs[^1].Count + 1);
            else
                runs.Add((label, i, 1));
        }
        var keeper = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var r = 0; r < runs.Count; r++)
        {
            if (!keeper.TryGetValue(runs[r].Label, out var k) || runs[r].Count > runs[k].Count)
                keeper[runs[r].Label] = r;
        }
        var spans = new List<Span>();
        var pending = 0;
        for (var r = 0; r < runs.Count; r++)
        {
            var run = runs[r];
            if (keeper[run.Label] == r)
            {
                spans.Add(new Span(run.Start - pending, run.Count + pending, run.Label, null));
                pending = 0;
            }
            else if (spans.Count > 0)
            {
                spans[^1] = spans[^1] with { Count = spans[^1].Count + run.Count };
            }
            else
            {
                pending += run.Count;
            }
        }
        return spans;
    }

    /// <summary>
    /// The letter labels of a segment's (prefix-stripped) names. Doujin levels (1.49.0, owner decision 11): when at least two thirds of the
    /// names open with <c>[</c> or <c>(</c> - which sort before every letter, so every name would be <c>#</c> - each name is labelled by the
    /// first letter INSIDE its leading tag with the bracket kept (<c>[A</c> ... <c>[Z</c>, <c>[#</c>, <c>(A</c> ...). The names sort by that
    /// tag's text, so each label is still one contiguous run.
    /// </summary>
    internal static string[] LetterLabels(string[] stripped)
    {
        var tagged = stripped.Count(n => n.Length > 0 && n[0] is ('[' or '('));
        var labels = new string[stripped.Length];
        var byTag = stripped.Length > 0 && tagged >= TwoThirds(stripped.Length);
        for (var i = 0; i < stripped.Length; i++)
            labels[i] = byTag ? TagLetterLabelFor(stripped[i]) : LetterLabelFor(stripped[i]);
        return labels;
    }

    /// <summary>
    /// The label of a name in a doujin level: the opening bracket plus the label of the first character inside the leading tag
    /// (<c>[Circle] Title</c> -> <c>[C</c>; <c>[2nd] x</c> -> <c>[#</c>); a name without an opening bracket keeps its plain letter label.
    /// </summary>
    internal static string TagLetterLabelFor(string name)
    {
        if (name.Length == 0 || name[0] is not ('[' or '('))
            return LetterLabelFor(name);
        var inner = name[1..].TrimStart();
        return name[0] + (inner.Length == 0 ? "#" : LetterLabelFor(inner));
    }

    /// <summary>
    /// The letter label of a name by its first character AS IT SORTS (1.41.0: leading punctuation is no longer skipped - a skipped
    /// character put the name in a letter bucket far from where the listing shows it): an ASCII letter -> <c>A</c>-<c>Z</c>; an ASCII
    /// digit, space or punctuation -> <c>#</c>; a recognised script -> its group (<c>Kana</c>, <c>Hangul</c>, <c>CJK</c>,
    /// <c>Cyrillic</c>, <c>Greek</c>, <c>Arabic</c>, <c>Hebrew</c>, <c>Thai</c>); anything else (including <c>{|}~</c>, which sort
    /// after the letters, and accented Latin letters) -> <c>Other</c>.
    /// </summary>
    public static string LetterLabelFor(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return OtherLabel;
        var c = name[0];
        if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            return char.ToUpperInvariant(c).ToString();
        if (c < '{')
            return "#";
        if (c < 0x80)
            return OtherLabel;
        return Rune.TryGetRuneAt(name, 0, out var rune) ? ScriptBucket(rune.Value) : OtherLabel;
    }

    private static string ScriptBucket(int c)
    {
        // Hiragana / Katakana (+ half-width Katakana).
        if ((c >= 0x3040 && c <= 0x30FF) || (c >= 0xFF65 && c <= 0xFF9F))
            return "Kana";
        // Hangul Jamo / Syllables / Compatibility Jamo.
        if ((c >= 0x1100 && c <= 0x11FF) || (c >= 0xAC00 && c <= 0xD7AF) || (c >= 0x3130 && c <= 0x318F))
            return "Hangul";
        // CJK Unified Ideographs + extensions + radicals.
        if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) ||
            (c >= 0x20000 && c <= 0x2FFFF) || (c >= 0x2E80 && c <= 0x2EFF) || (c >= 0x2F00 && c <= 0x2FDF))
            return "CJK";
        if (c >= 0x0400 && c <= 0x052F)
            return "Cyrillic";
        if ((c >= 0x0370 && c <= 0x03FF) || (c >= 0x1F00 && c <= 0x1FFF))
            return "Greek";
        if ((c >= 0x0600 && c <= 0x06FF) || (c >= 0x0750 && c <= 0x077F))
            return "Arabic";
        if (c >= 0x0590 && c <= 0x05FF)
            return "Hebrew";
        if (c >= 0x0E00 && c <= 0x0E7F)
            return "Thai";
        return OtherLabel;
    }
}
