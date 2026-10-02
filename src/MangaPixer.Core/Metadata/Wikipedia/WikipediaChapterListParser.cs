namespace com.lifepixer.mangapixer.Core.Metadata.Wikipedia;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>One volume row of an English "List of ... chapters" table: the volume, its chapters, the English release date and ISBN.</summary>
/// <param name="Volume">The canonical volume number (<c>"3"</c>, <c>"0"</c>).</param>
/// <param name="Chapters">The chapters the page places in this volume: canonical unit numbers, ascending, distinct.</param>
/// <param name="EnglishDate">The earliest English release date, a partial ISO text (<c>2026-12-08</c>, <c>2002-02</c>), or null. May be a FUTURE date.</param>
/// <param name="EnglishIsbn">The first valid English ISBN as digits, or null.</param>
public sealed record WikipediaVolumeRow(string Volume, IReadOnlyList<string> Chapters, string? EnglishDate, string? EnglishIsbn);

/// <summary>
/// One <c>{{Graphic novel list/header}}</c> ... table of a page: its heading, its volume rows and the chapters its page lists as
/// "not yet in a volume". <see cref="ImplicitChapters"/> counts chapters whose number the page does not state (an unnumbered list that
/// was numbered by counting on from the previous one); <see cref="Specials"/> counts extras ("Bonus", "Short Mission: 19") that are
/// never placed.
/// </summary>
public sealed record WikipediaTable(
    string? Heading, IReadOnlyList<WikipediaVolumeRow> Volumes, IReadOnlyList<string> NotInVolume, int ImplicitChapters, int Specials);

/// <summary>A parsed page: its tables, and - for a hub page that only links to range pages - the titles it links to.</summary>
public sealed record WikipediaPageParse(IReadOnlyList<WikipediaTable> Tables, IReadOnlyList<string> SubPageTitles);

/// <summary>
/// Reads the wikitext of an English Wikipedia "List of ... chapters" page into volume -> chapters tables (1.32.0, design 6.1). Pure:
/// text in, rows out - no clock, no network, no storage.
/// <para>
/// Only NUMBERS, DATES and ISBNs leave this class: chapter titles, summaries and every other text of the page are read for structure
/// and dropped (the privacy contract stores nothing else).
/// </para>
/// Chapter-list forms handled (all seen live in 2026-09): <c>{{Numbered list|start=N|...}}</c> and <c>{{#invoke:list|ordered|start=N|...}}</c>;
/// <c>* 001. {{Nihongo|...}}</c>, <c>* 1.</c>, <c>* Chapter 001: ...</c>; ranges (<c>* Chapters 1-8</c>, <c>*Mission: 128-140.1</c>); a raw
/// <c>&lt;ol start=N&gt;&lt;li&gt;</c> block; unnumbered <c>*</c> / <c>#</c> lists (counted on from the previous chapter); specials; and the
/// <c>Yotsuba&amp;!</c> form whose chapter list sits in raw table rows after the template.
/// </summary>
public static class WikipediaChapterListParser
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(2);

    private static Regex Make(string pattern, RegexOptions extra = RegexOptions.None) =>
        new(pattern, RegexOptions.CultureInvariant | extra, s_timeout);

    private const string Num = @"(\d+(?:\.\d+)?)";
    private const string Dash = @"\s*[–—\-~]\s*";

    private static readonly Regex s_heading = Make(@"^(=+)\s*(.*?)\s*\1\s*$", RegexOptions.Multiline);
    private static readonly Regex s_notYet = Make(@"not yet|not collected|not released|uncollected|yet to be|not in (?:a )?volume", RegexOptions.IgnoreCase);
    private static readonly Regex s_header = Make(@"\{\{\s*Graphic novel list/header", RegexOptions.IgnoreCase);
    private static readonly Regex s_footer = Make(@"\{\{\s*Graphic novel list/footer", RegexOptions.IgnoreCase);
    private static readonly Regex s_line = Make(@"^\s*([*#:]+)\s*(.*)$");
    private static readonly Regex s_startParameter = Make(@"^\s*start\s*=\s*(\d+)\s*\z");
    private static readonly Regex s_listKind = Make(@"^\s*(ordered|bulleted|unordered)\s*\z", RegexOptions.IgnoreCase);
    private static readonly Regex s_otherNamed = Make(@"^\s*[a-z_]+\s*=");
    private static readonly Regex s_olTags = Make(@"<ol([^>]*)>|<li([^>]*)>", RegexOptions.IgnoreCase);
    private static readonly Regex s_olBlock = Make(@"<ol.*?</ol>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex s_start = Make(@"start\s*=\s*""?(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex s_value = Make(@"value\s*=\s*""?(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex s_range = Make(
        @"^(?:Chapters?|Ch\.|Missions?|Episodes?|Nights?|Acts?|Stages?|Rounds?|Files?|Stories)?\s*:?\s*" + Num + Dash + Num + @"\b", RegexOptions.IgnoreCase);
    private static readonly Regex s_numberThenTemplate = Make(@"^\d+\s*[.:]\s*\{\{");
    private static readonly Regex s_special = Make(
        @"^(Short Mission|Extra Mission|Bonus|Extra|Special|Omake|Side Story|Intermission)\b", RegexOptions.IgnoreCase);
    private static readonly Regex s_specialStart = Make(
        @"^(bonus|extra|special|omake|side story|short mission|interlude|prologue|epilogue|afterword|one-shot)", RegexOptions.IgnoreCase);
    private static readonly Regex s_numbered = Make(
        @"^(?:Chapter|Ch\.|Episode|Mission|Night|Act|Stage|Round|File|Part|#)?\s*:?\s*" + Num + @"\s*(?:[.:)]|\s-\s|\s–\s)", RegexOptions.IgnoreCase);
    private static readonly Regex s_single = Make(@"^(?:Chapters?|Missions?)\s*:?\s*" + Num + @"\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex s_subPage = Make(
        @"\[\[\s*(Lists? of [^\]|#\[]{1,120}? chapters[^\]|#\[]{0,40}?)\s*(?:\||\]\])", RegexOptions.IgnoreCase);

    /// <summary>Hard bounds: a hostile or broken page cannot make the parser build an unbounded structure.</summary>
    private const int MaxRangeLength = 2000;
    private const int MaxRows = 2000;
    private const int MaxSubPages = 12;

    private enum Kind
    {
        Number,
        Implicit,
        Special,
    }

    private readonly record struct Item(Kind Kind, decimal Value);

    /// <summary>Parses one page's wikitext.</summary>
    public static WikipediaPageParse Parse(string wikitext)
    {
        ArgumentNullException.ThrowIfNull(wikitext);
        var tables = new List<WikipediaTable>();
        var headings = s_heading.Matches(wikitext).Select(m => (m.Index, m.Length, Text: m.Groups[2].Value)).ToList();
        var headers = s_header.Matches(wikitext).Select(m => m.Index).ToList();
        var footers = s_footer.Matches(wikitext).Select(m => m.Index).ToList();

        var regions = new List<(int Start, int End)>();
        if (headers.Count == 0)
        {
            regions.Add((0, wikitext.Length));
        }
        else
        {
            for (var i = 0; i < headers.Count; i++)
            {
                var nextHeader = i + 1 < headers.Count ? headers[i + 1] : wikitext.Length;
                var footer = footers.FirstOrDefault(f => f > headers[i]);
                var end = footer != 0 && footer < nextHeader ? footer : nextHeader;
                regions.Add((headers[i], end));
            }
        }

        var allRows = Wikitext.FindTemplates(wikitext, @"Graphic novel list");
        var notYet = NotInVolumeChapters(wikitext, headings, out var notYetImplicit);
        var first = true;
        foreach (var (start, end) in regions)
        {
            var rows = allRows.Where(t => t.Start >= start && t.Start < end).Take(MaxRows).ToList();
            if (rows.Count == 0)
                continue;
            var heading = headings.LastOrDefault(h => h.Index < start).Text;
            tables.Add(BuildTable(wikitext, rows, end, heading, first ? notYet : [], first ? notYetImplicit : 0));
            first = false;
        }
        return new WikipediaPageParse(tables, SubPages(wikitext));
    }

    private static List<string> SubPages(string wikitext) =>
        s_subPage.Matches(wikitext)
            .Select(m => m.Groups[1].Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSubPages)
            .ToList();

    private static WikipediaTable BuildTable(
        string text, List<Wikitext.Template> rows, int regionEnd, string? heading, IReadOnlyList<string> notYet, int notYetImplicit)
    {
        var volumes = new List<WikipediaVolumeRow>();
        var last = 0m;
        var implicitCount = 0;
        var specials = 0;
        for (var idx = 0; idx < rows.Count; idx++)
        {
            var row = rows[idx];
            var parameters = Wikitext.NamedParameters(Wikitext.SplitParameters(row.Body));
            var volumeText = Wikitext.StripRefs(parameters.GetValueOrDefault("VolumeNumber", string.Empty));
            if (VolumeMapJson.Parse(volumeText) is not { } volume)
                continue; // "Special edition", a blank number: not a numbered volume

            var chapterText = string.Join('\n', new[] { "ChapterList", "ChapterListCol1", "ChapterListCol2" }
                .Select(k => parameters.GetValueOrDefault(k, string.Empty)));
            // The raw-table form (Yotsuba&!): the chapter list follows the template in table rows.
            var after = text[row.End..Math.Min(idx + 1 < rows.Count ? rows[idx + 1].Start : regionEnd, text.Length)];
            if (after.Contains("Chapter list", StringComparison.OrdinalIgnoreCase))
            {
                var block = after[after.IndexOf("Chapter list", StringComparison.OrdinalIgnoreCase)..];
                block = Regex.Split(block, @"\n\|\s*colspan|\n\|-", RegexOptions.CultureInvariant, s_timeout)[0];
                chapterText += "\n" + block;
            }

            var chapters = new SortedSet<decimal>();
            foreach (var item in ParseItems(chapterText))
            {
                switch (item.Kind)
                {
                    case Kind.Number:
                        chapters.Add(item.Value);
                        last = Math.Max(last, item.Value);
                        break;
                    case Kind.Implicit:
                        implicitCount++;
                        last = decimal.Truncate(last) + 1;
                        chapters.Add(last);
                        break;
                    default:
                        specials++;
                        break;
                }
            }

            volumes.Add(new WikipediaVolumeRow(
                VolumeMapJson.Canonical(volume),
                chapters.Select(VolumeMapJson.Canonical).ToList(),
                WikipediaFacts.EarliestDate(parameters.GetValueOrDefault("LicensedRelDate")),
                WikipediaFacts.FirstIsbn(parameters.GetValueOrDefault("LicensedISBN"))));
        }
        return new WikipediaTable(heading, volumes, notYet, implicitCount + notYetImplicit, specials);
    }

    /// <summary>The chapters of "not yet in a volume" sections (heading wording varies): numbered ones only.</summary>
    private static IReadOnlyList<string> NotInVolumeChapters(
        string text, List<(int Index, int Length, string Text)> headings, out int implicitCount)
    {
        var numbers = new SortedSet<decimal>();
        implicitCount = 0;
        for (var i = 0; i < headings.Count; i++)
        {
            if (!s_notYet.IsMatch(headings[i].Text))
                continue;
            var from = headings[i].Index + headings[i].Length;
            var to = i + 1 < headings.Count ? headings[i + 1].Index : text.Length;
            foreach (var item in ParseItems(text[from..to]))
            {
                if (item.Kind == Kind.Number)
                    numbers.Add(item.Value);
                else if (item.Kind == Kind.Implicit)
                    implicitCount++;
            }
        }
        return numbers.Select(VolumeMapJson.Canonical).ToList();
    }

    /// <summary>Items of one chapter-list blob, in order: numbered, implicit (no number stated) or special.</summary>
    private static List<Item> ParseItems(string blob)
    {
        var items = new List<Item>();
        var text = Wikitext.StripRefs(blob);

        // {{Numbered list|start=N|a|b}} / {{#invoke:list|ordered|start=N|a|b}}
        var consumed = new List<(int Start, int End)>();
        foreach (var template in Wikitext.FindTemplates(text, @"Numbered list|#invoke:\s*list"))
        {
            var parts = Wikitext.SplitParameters(template.Body);
            var start = 1;
            var positional = 0;
            foreach (var part in parts.Skip(1))
            {
                if (s_startParameter.Match(part) is { Success: true } m)
                    start = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                else if (s_listKind.IsMatch(part) || s_otherNamed.IsMatch(part) || string.IsNullOrWhiteSpace(part))
                    continue;
                else
                    positional++;
            }
            for (var k = 0; k < positional && k < MaxRangeLength; k++)
                items.Add(new Item(Kind.Number, start + k));
            consumed.Add((template.Start, template.End));
        }
        var rest = text;
        foreach (var (s, e) in Enumerable.Reverse(consumed))
            rest = rest.Remove(s, e - s);

        // <ol start=N> ... <li value=N> ...
        if (rest.Contains("<li", StringComparison.OrdinalIgnoreCase))
        {
            var n = 1;
            foreach (Match m in s_olTags.Matches(rest))
            {
                if (m.Value.StartsWith("<ol", StringComparison.OrdinalIgnoreCase))
                {
                    n = s_start.Match(m.Groups[1].Value) is { Success: true } st ? int.Parse(st.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
                    continue;
                }
                if (s_value.Match(m.Groups[2].Value) is { Success: true } vv)
                    n = int.Parse(vv.Groups[1].Value, CultureInfo.InvariantCulture);
                items.Add(new Item(Kind.Number, n));
                n++;
            }
            rest = s_olBlock.Replace(rest, string.Empty);
        }

        foreach (var line in rest.Split('\n'))
        {
            if (s_line.Match(line) is not { Success: true } lm)
                continue;
            var marker = lm.Groups[1].Value;
            var body = lm.Groups[2].Value.Trim();
            if (body.Length == 0)
                continue;

            if (s_range.Match(body) is { Success: true } range && !s_numberThenTemplate.IsMatch(body))
            {
                foreach (var x in ExpandRange(range.Groups[1].Value, range.Groups[2].Value))
                    items.Add(new Item(Kind.Number, x));
                continue;
            }
            if (s_special.IsMatch(body))
            {
                items.Add(new Item(Kind.Special, 0));
                continue;
            }
            if (s_numbered.Match(body) is { Success: true } numbered && VolumeMapJson.Parse(numbered.Groups[1].Value) is { } number)
            {
                items.Add(new Item(Kind.Number, number));
                continue;
            }
            if (s_single.Match(body) is { Success: true } single && VolumeMapJson.Parse(single.Groups[1].Value) is { } only)
            {
                items.Add(new Item(Kind.Number, only));
                continue;
            }
            if (marker.StartsWith('*') && !body.StartsWith("{{", StringComparison.Ordinal) && s_specialStart.IsMatch(body))
            {
                items.Add(new Item(Kind.Special, 0));
                continue;
            }
            items.Add(new Item(Kind.Implicit, 0));
        }
        return items;
    }

    /// <summary><c>1</c>-<c>8</c> -> 1..8; <c>128</c>-<c>140.1</c> -> 128..140 and 140.1. Bounded.</summary>
    private static IEnumerable<decimal> ExpandRange(string from, string to)
    {
        if (VolumeMapJson.Parse(from) is not { } a || VolumeMapJson.Parse(to) is not { } b || b < a)
            yield break;
        var count = 0;
        for (var x = decimal.Truncate(a); x <= decimal.Truncate(b) && count < MaxRangeLength; x++, count++)
            yield return x;
        if (b != decimal.Truncate(b))
            yield return b;
    }
}
