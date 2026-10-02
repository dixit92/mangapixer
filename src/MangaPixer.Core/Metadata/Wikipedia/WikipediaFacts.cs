namespace com.lifepixer.mangapixer.Core.Metadata.Wikipedia;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>
/// Release dates and ISBNs as the "List of ... chapters" pages write them (1.32.0). Pure. A date field holds free text:
/// <c>July 3, 2026</c>, <c>28 February 2002</c>, <c>February 2002</c>, <c>2002-02-28</c>, a template (<c>{{dts|2002|Feb|28}}</c>), a
/// <c>&lt;ref&gt;</c>, several publishers in one field (<c>6 June 2005 (ADV) &lt;br /&gt; September 2009 (Yen)</c>), or a "-" placeholder. A date
/// is kept as a partial ISO text (<c>2002-02-28</c>, <c>2002-02</c>, <c>2002</c>) - the precision the page states.
/// </summary>
public static partial class WikipediaFacts
{
    private static readonly string[] s_months =
        ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];

    [GeneratedRegex(@"\b(?<d1>\d{1,2})(?:st|nd|rd|th)?\s+(?<m1>[A-Za-z]{3,9})\.?,?\s+(?<y1>\d{4})\b", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex DayMonthYear();

    [GeneratedRegex(@"\b(?<m2>[A-Za-z]{3,9})\.?\s+(?<d2>\d{1,2})(?:st|nd|rd|th)?,?\s+(?<y2>\d{4})\b", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex MonthDayYear();

    [GeneratedRegex(@"\b(?<m3>[A-Za-z]{3,9})\.?\s+(?<y3>\d{4})\b", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex MonthYear();

    [GeneratedRegex(@"\b(?<y4>\d{4})-(?<m4>\d{2})(?:-(?<d4>\d{2}))?\b", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"\{\{\s*(?:dts|start date|release date|start-date)\s*\|(?<a>[^}]*)\}\}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 2000)]
    private static partial Regex DateTemplate();

    [GeneratedRegex(@"<br\s*/?>|\r?\n|;", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 2000)]
    private static partial Regex EntrySeparator();

    [GeneratedRegex(@"\{\{[^{}]*\}\}|<[^>]+>|\[\[(?:[^\]|]*\|)?([^\]]*)\]\]", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex Markup();

    [GeneratedRegex(@"(?<![\d-])(?:97[89][- ]?)?(?:\d[- ]?){8,9}[\dXx](?![\d-])", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex IsbnShape();

    /// <summary>
    /// The EARLIEST release date a date field states (several publishers: the first English release), as a partial ISO text, or null
    /// for an empty field, a placeholder ("-", "TBA") or text without a date.
    /// </summary>
    public static string? EarliestDate(string? field)
    {
        if (string.IsNullOrWhiteSpace(field))
            return null;
        var text = Wikitext.StripRefs(field);
        var found = new List<(int Year, int Month, int Day)>();
        foreach (Match t in DateTemplate().Matches(text))
        {
            var parts = t.Groups["a"].Value.Split('|').Select(p => p.Trim()).Where(p => p.Length > 0 && !p.Contains('=')).ToList();
            if (parts.Count > 0 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is >= 1900 and <= 2200)
            {
                var m = parts.Count > 1 ? MonthOf(parts[1]) : 0;
                var d = parts.Count > 2 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var dd) ? dd : 0;
                found.Add((y, m, m == 0 ? 0 : d));
            }
        }
        text = DateTemplate().Replace(text, " ");
        foreach (var entry in EntrySeparator().Split(text))
        {
            var plain = Markup().Replace(entry, "$1");
            if (TryEntry(plain, out var date))
                found.Add(date);
        }
        if (found.Count == 0)
            return null;
        // Earliest by (year, month, day) with a missing part sorting first.
        var best = found.OrderBy(f => f.Year).ThenBy(f => f.Month).ThenBy(f => f.Day).First();
        return Format(best);
    }

    private static bool TryEntry(string text, out (int Year, int Month, int Day) date)
    {
        date = default;
        if (IsoDate().Match(text) is { Success: true } iso
            && int.TryParse(iso.Groups["y4"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var iy)
            && int.TryParse(iso.Groups["m4"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var im) && im is >= 1 and <= 12)
        {
            var id = iso.Groups["d4"].Success && int.TryParse(iso.Groups["d4"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var d4) ? d4 : 0;
            return Valid(iy, im, id, out date);
        }
        if (DayMonthYear().Match(text) is { Success: true } a && MonthOf(a.Groups["m1"].Value) is > 0 and var m1
            && int.TryParse(a.Groups["d1"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var d1)
            && int.TryParse(a.Groups["y1"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var y1))
            return Valid(y1, m1, d1, out date);
        if (MonthDayYear().Match(text) is { Success: true } b && MonthOf(b.Groups["m2"].Value) is > 0 and var m2
            && int.TryParse(b.Groups["d2"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var d2)
            && int.TryParse(b.Groups["y2"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var y2))
            return Valid(y2, m2, d2, out date);
        if (MonthYear().Match(text) is { Success: true } c && MonthOf(c.Groups["m3"].Value) is > 0 and var m3
            && int.TryParse(c.Groups["y3"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var y3))
            return Valid(y3, m3, 0, out date);
        return false;
    }

    private static bool Valid(int year, int month, int day, out (int Year, int Month, int Day) date)
    {
        date = (year, month, day);
        if (year is < 1900 or > 2200)
            return false;
        if (day != 0 && (month == 0 || day > DateTime.DaysInMonth(year, month) || day < 1))
            return false;
        return true;
    }

    /// <summary>1-12 for an English month name or its first three letters (<c>Feb</c>, <c>Sept</c>), else 0.</summary>
    private static int MonthOf(string name)
    {
        var n = name.Trim().TrimEnd('.').ToLowerInvariant();
        if (int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return number is >= 1 and <= 12 ? number : 0;
        if (n.Length < 3)
            return 0;
        for (var i = 0; i < s_months.Length; i++)
        {
            if (s_months[i] == n || (n.Length is >= 3 and <= 4 && s_months[i].StartsWith(n, StringComparison.Ordinal)))
                return i + 1;
        }
        return 0;
    }

    private static string Format((int Year, int Month, int Day) d) =>
        d.Month == 0 ? d.Year.ToString("D4", CultureInfo.InvariantCulture)
        : d.Day == 0 ? $"{d.Year:D4}-{d.Month:D2}"
        : $"{d.Year:D4}-{d.Month:D2}-{d.Day:D2}";

    /// <summary>
    /// A stored partial ISO date as the first day it could mean (<c>2026-12</c> -> 2026-12-01), or null when it is not one. Used to tell a
    /// volume that is already out from an announced one.
    /// </summary>
    public static DateOnly? FirstDayOf(string? partialIso)
    {
        if (string.IsNullOrEmpty(partialIso))
            return null;
        var parts = partialIso.Split('-');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var y) || y is < 1900 or > 2200)
            return null;
        var m = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var mm) && mm is >= 1 and <= 12 ? mm : 1;
        var d = parts.Length > 2 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var dd) && dd >= 1 && dd <= DateTime.DaysInMonth(y, m) ? dd : 1;
        return new DateOnly(y, m, d);
    }

    /// <summary>
    /// The first VALID ISBN a field names (an ISBN-13 preferred over an ISBN-10 - several publishers may be listed), as digits only
    /// (<c>9781974725762</c>; a trailing ISBN-10 <c>X</c> kept), or null. A check-digit failure is not an ISBN.
    /// </summary>
    public static string? FirstIsbn(string? field)
    {
        if (string.IsNullOrWhiteSpace(field))
            return null;
        string? ten = null;
        foreach (Match m in IsbnShape().Matches(Wikitext.StripRefs(field)))
        {
            var digits = new string(m.Value.Where(c => char.IsAsciiDigit(c) || c is 'X' or 'x').Select(char.ToUpperInvariant).ToArray());
            if (digits.Length == 13 && Isbn13Valid(digits))
                return digits;
            if (digits.Length == 10 && ten is null && Isbn10Valid(digits))
                ten = digits;
        }
        return ten;
    }

    private static bool Isbn13Valid(string d)
    {
        if (!d.All(char.IsAsciiDigit))
            return false;
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (d[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - (sum % 10)) % 10 == d[12] - '0';
    }

    private static bool Isbn10Valid(string d)
    {
        var sum = 0;
        for (var i = 0; i < 10; i++)
        {
            int v;
            if (d[i] == 'X' && i == 9)
                v = 10;
            else if (char.IsAsciiDigit(d[i]))
                v = d[i] - '0';
            else
                return false;
            sum += v * (10 - i);
        }
        return sum % 11 == 0;
    }
}
