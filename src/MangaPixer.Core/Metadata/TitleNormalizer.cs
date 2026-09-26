namespace com.lifepixer.mangapixer.Core.Metadata;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Turns a folder or file display name into clean title query variants plus
/// hints (1.24.0 metadata stage 1). Pure and deterministic, shared by the
/// identify prefill (lane B2) and the similarity scoring
/// (<see cref="TitleSimilarity"/>). Never touches the filesystem: it only ever
/// sees a display name.
///
/// Pipeline for <see cref="Normalize"/>:
/// NFKC (full-width to ASCII) -> strip a known archive extension -> <c>_</c> and
/// <c>.</c> become spaces when the name has no spaces -> bracketed tags
/// <c>[...]</c>, <c>(...)</c>, <c>{...}</c> are removed, EXCEPT a non-leading,
/// trailing <c>[English Title]</c> of at least two words (no further tag after it) (a second query variant, the
/// Manga-list convention) and a <c>(19xx|20xx)</c> year (a year hint) -> volume
/// and chapter tokens are removed, edition words are removed but kept as hints ->
/// whitespace collapsed, edge punctuation trimmed.
/// </summary>
public static partial class TitleNormalizer
{
    private static readonly string[] s_archiveExtensions =
        [".cbz", ".zip", ".cbr", ".rar", ".cb7", ".7z", ".cbt", ".tar", ".pdf", ".epub"];

    private static readonly string[] s_editionWords = ["Omnibus", "Deluxe", "Complete", "Digital"];

    [GeneratedRegex(@"\[([^\[\]]*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex SquareGroup();

    [GeneratedRegex(@"\[[^\[\]]*\]|\([^()]*\)|\{[^{}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex AnyBracketGroup();

    [GeneratedRegex(@"\((19\d{2}|20\d{2})\)", RegexOptions.CultureInvariant)]
    private static partial Regex YearGroup();

    // v01, v.1, vol 3, Vol. 3, Volume 1-5, volumes 2 - 4
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:v|vol|vols|volume|volumes)\.?\s*\d+(?:\.\d+)?(?:\s*-\s*\d+(?:\.\d+)?)?(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VolumeToken();

    // Ch 12, ch.12, chap 3, Chapter 10.5, chapters 1-20, c003 (bare c only when glued to digits)
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:(?:ch|chap|chapter|chapters)\.?\s*\d+(?:\.\d+)?(?:\s*-\s*\d+(?:\.\d+)?)?|c\d+(?:\.\d+)?(?:-\d+(?:\.\d+)?)?)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChapterToken();

    [GeneratedRegex(@"#\s*\d+(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex HashNumber();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:chapter|chapters|volume|volumes)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareUnitWord();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>
    /// Normalizes a display name into query variants and hints. Never returns
    /// null; an empty or all-tag input yields an empty <see cref="NormalizedTitle.Primary"/>.
    /// </summary>
    public static NormalizedTitle Normalize(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return new NormalizedTitle(string.Empty, [], null, []);

        var s = displayName.Normalize(NormalizationForm.FormKC).Trim();
        s = StripArchiveExtension(s);

        if (!s.Contains(' ', StringComparison.Ordinal))
            s = s.Replace('_', ' ').Replace('.', ' ');

        // (a) a non-leading, TRAILING [English Title] of >= 2 words becomes a second
        // variant. Only the last bracket group qualifies (a year group after it is
        // allowed): a [Group] tag followed by further tags ("[Scan Team] [OneShot]")
        // or nested inside another group ("[Vol. 7 Ch. 5 - Title [Scan Team]]") is a
        // scanlation group, never a title.
        string? englishVariant = null;
        var lastSquare = SquareGroup().Matches(s).LastOrDefault();
        if (lastSquare is not null && lastSquare.Index > 0) // a leading [Group] tag is never a title
        {
            var after = s[(lastSquare.Index + lastSquare.Length)..];
            var inner = lastSquare.Groups[1].Value.Trim();
            var tagsAfter = YearGroup().Replace(after, " ").IndexOfAny(['[', ']', '(', ')', '{', '}']) >= 0;
            if (!tagsAfter && CountWords(inner) >= 2 && inner.Any(char.IsLetter))
                englishVariant = inner;
        }

        // (b) a (19xx|20xx) year becomes a year hint.
        int? yearHint = null;
        var yearMatch = YearGroup().Match(s);
        if (yearMatch.Success)
            yearHint = int.Parse(yearMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        // Remove every bracket group (nested groups: repeat until stable, bounded).
        for (var i = 0; i < 4; i++)
        {
            var next = AnyBracketGroup().Replace(s, " ");
            if (next == s) break;
            s = next;
        }

        var editionHints = new List<string>();
        var primary = CleanTitle(s, editionHints);
        var variants = new List<string>();
        if (primary.Length > 0)
            variants.Add(primary);
        if (englishVariant is not null)
        {
            var english = CleanTitle(englishVariant, editionHints);
            if (english.Length > 0 && !variants.Contains(english, StringComparer.OrdinalIgnoreCase))
                variants.Add(english);
        }

        return new NormalizedTitle(
            variants.Count > 0 ? variants[0] : string.Empty,
            variants,
            yearHint,
            editionHints.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// The comparison form used only for scoring: casefold, strip diacritics
    /// (o-macron -> o), collapse long vowels (ou / oo -> o, uu -> u), unify the
    /// multiplication sign with <c>x</c> and <c>&amp;</c> with <c>and</c>, drop punctuation,
    /// collapse whitespace. Both sides of a comparison go through it, so the
    /// long-vowel collapse is symmetric.
    /// </summary>
    public static string ScoringForm(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var s = text.Normalize(NormalizationForm.FormKC)
            .Replace('×', 'x')
            .Replace("&", " and ", StringComparison.Ordinal)
            .ToLowerInvariant();

        // Strip diacritics: decompose, drop non-spacing marks.
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }

        s = sb.ToString().Normalize(NormalizationForm.FormC);
        s = s.Replace("ou", "o", StringComparison.Ordinal)
            .Replace("oo", "o", StringComparison.Ordinal)
            .Replace("uu", "u", StringComparison.Ordinal);
        return Whitespace().Replace(s, " ").Trim();
    }

    private static string CleanTitle(string text, List<string> editionHints)
    {
        var s = VolumeToken().Replace(text, " ");
        s = ChapterToken().Replace(s, " ");
        s = HashNumber().Replace(s, " ");
        s = BareUnitWord().Replace(s, " ");

        foreach (var word in s_editionWords)
        {
            var pattern = $@"(?<![\p{{L}}\p{{N}}]){word}(?![\p{{L}}\p{{N}}])";
            if (Regex.IsMatch(s, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                editionHints.Add(word);
                s = Regex.Replace(s, pattern, " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }

        s = Whitespace().Replace(s, " ").Trim();
        return s.Trim(' ', '-', '_', '.', ',', ':', ';', '~', '!', '|', '/', '+', '=', '\'', '"');
    }

    private static string StripArchiveExtension(string s)
    {
        foreach (var ext in s_archiveExtensions)
        {
            if (s.Length > ext.Length && s.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return s[..^ext.Length];
        }
        return s;
    }

    private static int CountWords(string s) =>
        s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
}

/// <summary>
/// Normalized title: the primary query, all query variants (primary first, then a
/// bracketed English title when present), an optional year hint and the edition
/// words that were removed (Omnibus, Deluxe, Complete, Digital).
/// </summary>
public sealed record NormalizedTitle(
    string Primary,
    IReadOnlyList<string> Variants,
    int? YearHint,
    IReadOnlyList<string> EditionHints);
