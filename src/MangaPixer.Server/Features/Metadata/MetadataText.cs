namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Flattens provider text (Markdown links and emphasis, stray HTML, entities)
/// into bounded plain text for storage (1.24.0). The result is DATA: the web
/// client renders it as text, never as markup, and links inside it are reduced
/// to their label (a provider URL never reaches the browser this way).
/// </summary>
public static partial class MetadataText
{
    [GeneratedRegex(@"\[([^\[\]]*)\]\((?:[^()\s]|\([^()\s]*\))*\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlBreak();

    [GeneratedRegex(@"<[^<>]{0,200}>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTag();

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
        s = s.Replace("**", string.Empty, StringComparison.Ordinal)
             .Replace("__", string.Empty, StringComparison.Ordinal)
             .Replace("*", string.Empty, StringComparison.Ordinal);

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
