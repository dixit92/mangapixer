namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Flattens provider text (Markdown links, headings, emphasis and quote markers,
/// stray HTML, entities)
/// into bounded plain text for storage (1.24.0). The result is DATA: the web
/// client renders it as text, never as markup, and links inside it are reduced
/// to their label (a provider URL never reaches the browser this way).
/// </summary>
public static partial class MetadataText
{
    [GeneratedRegex(@"\[([^\[\]]*)\]\((?:[^()\s]|\([^()\s]*\))*\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"<\s*a\b[^<>]{0,500}>.*?<\s*/\s*a\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlAnchor();

    [GeneratedRegex(@"\([^()]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Parenthesized();

    [GeneratedRegex(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlBreak();

    [GeneratedRegex(@"<[^<>]{0,200}>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTag();

    // "##### Notes:" -> "Notes:" (ATX heading marker at a line start, up to 3 spaces of indent).
    [GeneratedRegex(@"^[ \t]{0,3}#{1,6}(?:[ \t]+|$)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingMarker();

    // "> quoted" -> "quoted" (one or more quote markers at a line start).
    [GeneratedRegex(@"^[ \t]*(?:>[ \t]?)+", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex QuoteMarker();

    // "_word_" -> "word", only when the underscores sit at word edges (snake_case survives).
    [GeneratedRegex(@"(?<![\p{L}\p{N}_])_(?=[^\s_])([^_\n]*?[^\s_])_(?![\p{L}\p{N}_])", RegexOptions.CultureInvariant)]
    private static partial Regex UnderscoreEmphasis();

    [GeneratedRegex(@"[ \t]+\n", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingSpaces();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex ExtraBlankLines();

    [GeneratedRegex(@"[ \t]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedSpaces();

    /// <summary>Plain text of at most <paramref name="maxLength"/> characters, or null when nothing is left.</summary>
    public static string? Flatten(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var s = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        s = MarkdownLink().Replace(s, "$1");
        s = HtmlBreak().Replace(s, "\n");
        s = HtmlTag().Replace(s, string.Empty);
        s = WebUtility.HtmlDecode(s);
        s = HeadingMarker().Replace(s, string.Empty);
        s = QuoteMarker().Replace(s, string.Empty);
        s = s.Replace("**", string.Empty, StringComparison.Ordinal)
             .Replace("__", string.Empty, StringComparison.Ordinal)
             .Replace("*", string.Empty, StringComparison.Ordinal);
        s = UnderscoreEmphasis().Replace(s, "$1");

        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '\n' || c == '\t' || !char.IsControl(c))
                sb.Append(c);
        }
        s = TrailingSpaces().Replace(sb.ToString(), "\n");
        s = RepeatedSpaces().Replace(s, " ");
        s = ExtraBlankLines().Replace(s, "\n\n").Trim();
        if (s.Length == 0)
            return null;
        return s.Length <= maxLength ? s : s[..maxLength].TrimEnd();
    }

    /// <summary>
    /// A provider description: <see cref="Flatten"/> after dropping the lines that held only links
    /// ("[Original Manga](...) (old link)") and a short "Links:" heading left with nothing under it.
    /// Flattening keeps only a link's label, so such lines would read as links that go nowhere.
    /// </summary>
    public static string? Description(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var drop = new bool[lines.Length];
        for (var i = 0; i < lines.Length; i++)
            drop[i] = IsLinkOnly(lines[i]);

        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (drop[i] || !drop[i + 1] || !IsShortHeading(lines[i]))
                continue;
            // The heading goes only when everything under it, up to a blank line, was a link.
            var j = i + 1;
            while (j < lines.Length && drop[j])
                j++;
            if (j == lines.Length || string.IsNullOrWhiteSpace(lines[j]))
                drop[i] = true;
        }

        var kept = lines.Where((_, i) => !drop[i]);
        return Flatten(string.Join('\n', kept), maxLength);
    }

    // "- **Japanese:** " / "*Digital:* " - a list marker and a short label in front of a list of links.
    [GeneratedRegex(@"^\s*(?:[-*+\u2022]\s+)?(?:[*_]{0,2}[^:\n\[\]]{1,40}:[*_]{0,2})?", RegexOptions.CultureInvariant)]
    private static partial Regex LinkListLabel();

    // "Piccoma ([Main Story](...), [Side Story](...))": a short name followed by links in parentheses.
    [GeneratedRegex(@"^([^()\[\]]{1,40}?)\s*\((.*)\)\s*$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex NamedLinkGroup();

    /// <summary>
    /// A line that is only a list of links: after an optional short label ("- **French:**"), every item
    /// between top-level <c>,</c> <c>;</c> <c>|</c> is a link (optionally followed by notes in parentheses,
    /// "[Site](...) (old link)") or a short name with links in parentheses. A sentence with a link inside
    /// ("see the [site](...)") is not.
    /// </summary>
    private static bool IsLinkOnly(string line)
    {
        var s = HtmlAnchor().Replace(line, m => "[" + HtmlTag().Replace(m.Value, string.Empty) + "](a)");
        if (!MarkdownLink().IsMatch(s))
            return false;
        s = LinkListLabel().Replace(s, string.Empty, 1);
        return TopLevelItems(s).All(IsLinkItem);
    }

    private static bool IsLinkItem(string item)
    {
        var t = WebUtility.HtmlDecode(HtmlTag().Replace(item, " ")).Trim();
        if (t.Length == 0 || !t.Any(char.IsLetter))
            return true;
        if (MarkdownLink().IsMatch(t) && !HasLetters(Parenthesized().Replace(MarkdownLink().Replace(t, " "), " ")))
            return true;
        var named = NamedLinkGroup().Match(t);
        return named.Success
            && named.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4
            && MarkdownLink().IsMatch(named.Groups[2].Value)
            && !HasLetters(MarkdownLink().Replace(named.Groups[2].Value, " "));
    }

    private static bool HasLetters(string s) => s.Any(char.IsLetter);

    /// <summary>Splits on <c>,</c> <c>;</c> <c>|</c> outside brackets and parentheses.</summary>
    private static IEnumerable<string> TopLevelItems(string s)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '(' or '[')
                depth++;
            else if (c is ')' or ']')
                depth = Math.Max(0, depth - 1);
            else if (depth == 0 && c is ',' or ';' or '|')
            {
                yield return s[start..i];
                start = i + 1;
            }
        }
        yield return s[start..];
    }

    private static bool IsShortHeading(string line)
    {
        var t = line.Trim().Trim('#', '*', '_', '>', ' ', '\t');
        return t.Length is > 1 and <= 40 && t.EndsWith(':');
    }

    /// <summary>A single-line bounded value (names, titles): flattened, newlines folded to spaces.</summary>
    public static string? Line(string? value, int maxLength)
    {
        var flat = Flatten(value, int.MaxValue);
        if (flat is null)
            return null;
        flat = RepeatedSpaces().Replace(flat.Replace('\n', ' ').Replace('\t', ' '), " ").Trim();
        if (flat.Length == 0)
            return null;
        return flat.Length <= maxLength ? flat : flat[..maxLength].TrimEnd();
    }
}
