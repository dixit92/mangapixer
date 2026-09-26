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
/// whitespace collapsed, edge punctuation trimmed. A bracket character left
/// without its partner (<c>Title (unclosed</c>) is dropped, its text kept.
///
/// Stage 2 (auto-match) adds helpers that never change <see cref="NormalizedTitle.Primary"/>:
/// derived retrieval variants (<see cref="DerivedVariants"/>, also exposed as
/// <see cref="NormalizedTitle.Derived"/>), sequel / part numbers
/// (<see cref="NumberTokens"/>) and the base title of an archive name
/// (<see cref="ArchiveBaseTitle"/>, <see cref="ArchiveTitle"/>).
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

    [GeneratedRegex(@"[\[\](){}]", RegexOptions.CultureInvariant)]
    private static partial Regex StrayBracket();

    // Subtitle separator: " - ", " – ", " — ", " ~ " or ": " (NFKC folds the full-width colon).
    [GeneratedRegex(@"\s+[-\u2013\u2014~]\s+|:\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SubtitleSeparator();

    // A sequel / part number: "Part 3", "Season 2", "Book II", "Arc 4", "Phase 2", "Stage 3", or a
    // bare number / roman numeral II-X standing alone before the end or a subtitle separator.
    // Four-digit numbers are years, never sequel numbers.
    [GeneratedRegex(@"(?<![\p{L}\p{N}/])(?:(?<unit>part|season|book|arc|phase|stage)\s*\.?\s*(?<num>\d{1,3}|[ivx]{1,4})|(?<num>\d{1,3}(?:\.\d)?|ii|iii|iv|v|vi|vii|viii|ix|x))(?=\s*$|\s+[-\u2013\u2014~]\s+|:\s+|\s*[-\u2013\u2014~:]\s*$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SequelNumber();

    // A leading unit number of an archive name: "001 - Title", "01. Title", "12) Title".
    [GeneratedRegex(@"^\d+(?:\.\d+)?\s*(?:[-.:)\u2013\u2014]\s*|$)", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingUnitNumber();

    // Trailing number run of an archive base title: " 01", " 1.5", " 01-03", " - 012", "_07".
    [GeneratedRegex(@"(?:[\s\-_.#\u2013\u2014]+\d+(?:\.\d+)?(?:\s*-\s*\d+(?:\.\d+)?)?)+$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingNumbers();

    [GeneratedRegex(@"^[\p{P}\p{S}\p{N}\s]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NoLetters();

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

        s = RemoveBracketGroups(s);

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

        var derived = new List<DerivedTitle>();
        foreach (var variant in variants)
        {
            foreach (var d in DerivedVariants(variant))
            {
                if (!variants.Contains(d.Text, StringComparer.OrdinalIgnoreCase)
                    && !derived.Any(x => string.Equals(x.Text, d.Text, StringComparison.OrdinalIgnoreCase)))
                    derived.Add(d);
            }
        }

        return new NormalizedTitle(
            variants.Count > 0 ? variants[0] : string.Empty,
            variants,
            yearHint,
            editionHints.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            Derived = derived,
        };
    }

    /// <summary>
    /// Retrieval-only variants derived from a clean title (stage 2): the text before a
    /// subtitle separator when at least two words precede it (<c>Title Words - Subtitle</c>
    /// -> <c>Title Words</c>), and the title without its sequel / part number
    /// (<c>Title 2 - Sub</c>, <c>Title Part 3</c>, <c>Title II</c> -> <c>Title</c>). A
    /// derived variant finds the franchise record; scoring still penalizes the number it
    /// dropped (<see cref="NumberTokens"/>). Empty when nothing can be derived.
    /// </summary>
    public static IReadOnlyList<DerivedTitle> DerivedVariants(string? title)
    {
        var result = new List<DerivedTitle>();
        if (string.IsNullOrWhiteSpace(title))
            return result;
        var t = Whitespace().Replace(title.Normalize(NormalizationForm.FormKC), " ").Trim();

        var sep = SubtitleSeparator().Match(t);
        if (sep.Success && sep.Index > 0)
        {
            var head = TrimEdges(t[..sep.Index]);
            var tail = TrimEdges(t[(sep.Index + sep.Length)..]);
            if (CountWords(head) >= 2 && tail.Length > 0)
                result.Add(new DerivedTitle(head, DerivedTitleKind.SubtitleSplit));
        }

        var number = SequelNumber().Match(t);
        if (number.Success && number.Index > 0)
        {
            var head = TrimEdges(t[..number.Index]);
            if (head.Length >= 3 && head.Any(char.IsLetter)
                && !result.Any(r => string.Equals(r.Text, head, StringComparison.OrdinalIgnoreCase)))
                result.Add(new DerivedTitle(head, DerivedTitleKind.SequelNumberSplit));
        }

        return result;
    }

    /// <summary>
    /// The sequel / part numbers of a title, normalized and sorted (<c>Title 2</c> -> <c>["2"]</c>,
    /// <c>Title Part III: Sub</c> -> <c>["3"]</c>, <c>Title</c> -> <c>[]</c>). Two titles whose
    /// number tokens differ name different works of one franchise (<c>Part 3</c> vs <c>Part 4</c>,
    /// <c>Title 2</c> vs <c>Title</c>). Leading numbers (<c>20th Century Boys</c>), numbers inside a
    /// name (<c>Ranma 1/2</c>) and years are not sequel numbers.
    /// </summary>
    public static IReadOnlyList<string> NumberTokens(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return [];
        var t = Whitespace().Replace(title.Normalize(NormalizationForm.FormKC), " ").Trim();
        var result = new List<string>();
        foreach (Match m in SequelNumber().Matches(t))
        {
            if (m.Index == 0 && !m.Groups["unit"].Success)
                continue; // a leading bare number is part of the name
            if (NormalizeNumber(m.Groups["num"].Value) is { } n)
                result.Add(n);
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// The base title of an archive file name (stage 2 work detection): bracket groups
    /// removed, cut before the first volume / chapter / <c>#</c> token, trailing numbers
    /// stripped (<c>Title 03</c> -> <c>Title</c>, so a numbered mini-series shares one base).
    /// Empty for a unit-named archive (<c>001 [chapter title]</c>, <c>Vol 01</c>,
    /// <c>01 - Subtitle</c>) - an archive that names only its position in a work.
    /// </summary>
    public static string ArchiveBaseTitle(string? archiveName)
    {
        if (string.IsNullOrWhiteSpace(archiveName))
            return string.Empty;

        var s = archiveName.Normalize(NormalizationForm.FormKC).Trim();
        s = StripArchiveExtension(s);
        if (!s.Contains(' ', StringComparison.Ordinal))
            s = s.Replace('_', ' ').Replace('.', ' ');
        s = Whitespace().Replace(RemoveBracketGroups(s), " ").Trim();

        if (LeadingUnitNumber().IsMatch(s))
            return string.Empty;

        var cut = s.Length;
        foreach (var token in new[] { VolumeToken().Match(s), ChapterToken().Match(s), HashNumber().Match(s) })
        {
            if (token.Success && token.Index < cut)
                cut = token.Index;
        }
        s = s[..cut];

        s = CleanTitle(s, []);
        s = TrimEdges(TrailingNumbers().Replace(s, string.Empty));
        return NoLetters().IsMatch(s) ? string.Empty : s;
    }

    /// <summary>
    /// The dominant archive-derived title of a folder: the base title
    /// (<see cref="ArchiveBaseTitle"/>) that at least half of the archives share
    /// (compared by <see cref="ScoringForm"/>), or null. Deterministic: the ordinal-first
    /// spelling of the winning base is returned.
    /// </summary>
    public static string? ArchiveTitle(IEnumerable<string> archiveNames)
    {
        ArgumentNullException.ThrowIfNull(archiveNames);
        var total = 0;
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var name in archiveNames)
        {
            total++;
            var baseTitle = ArchiveBaseTitle(name);
            var key = ScoringForm(baseTitle);
            if (key.Length == 0) continue;
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = [];
            list.Add(baseTitle);
        }
        if (total == 0)
            return null;

        var best = groups
            .OrderByDescending(g => g.Value.Count)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        return best.Value is { } spellings && spellings.Count * 2 >= total
            ? spellings.Order(StringComparer.Ordinal).First()
            : null;
    }

    /// <summary>
    /// The comparison form used only for scoring: casefold, strip diacritics
    /// (o-macron -> o), collapse long vowels (ou / oo -> o, uu -> u), unify the
    /// multiplication sign with <c>x</c> and <c>&amp;</c> with <c>and</c>, drop punctuation,
    /// collapse whitespace. Apostrophes are removed, not turned into spaces
    /// (<c>Ren'ai</c> -> <c>renai</c>, <c>Hell's</c> -> <c>hells</c>). Both sides of a comparison
    /// go through it, so the long-vowel collapse is symmetric.
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
            if (cat == UnicodeCategory.NonSpacingMark || IsApostrophe(ch))
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

    private static string RemoveBracketGroups(string s)
    {
        // Every bracket group (nested groups: repeat until stable, bounded), then any
        // bracket character left without its partner.
        for (var i = 0; i < 4; i++)
        {
            var next = AnyBracketGroup().Replace(s, " ");
            if (next == s) break;
            s = next;
        }
        return StrayBracket().Replace(s, " ");
    }

    private static bool IsApostrophe(char ch) =>
        ch is '\'' or '\u2018' or '\u2019' or '\u02BC' or '`' or '\u00B4';

    private static string TrimEdges(string s) =>
        s.Trim().Trim(' ', '-', '_', '.', ',', ':', ';', '~', '!', '|', '/', '+', '=', '\'', '"', '\u2013', '\u2014').Trim();

    private static readonly string[] s_romanNumerals = ["i", "ii", "iii", "iv", "v", "vi", "vii", "viii", "ix", "x"];

    private static string? NormalizeNumber(string raw)
    {
        var lower = raw.ToLowerInvariant();
        var roman = Array.IndexOf(s_romanNumerals, lower);
        if (roman >= 0)
            return (roman + 1).ToString(CultureInfo.InvariantCulture);
        return decimal.TryParse(lower, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)
            ? d.ToString("0.##", CultureInfo.InvariantCulture)
            : null;
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
    IReadOnlyList<string> EditionHints)
{
    /// <summary>
    /// Retrieval-only variants derived from <see cref="Variants"/> (stage 2), flagged by kind
    /// so an identify prefill can offer them as suggestions rather than as the name.
    /// </summary>
    public IReadOnlyList<DerivedTitle> Derived { get; init; } = [];
}

/// <summary>How a derived variant was made from a clean title.</summary>
public enum DerivedTitleKind
{
    /// <summary>The text before a subtitle separator (<c>Title Words - Subtitle</c>).</summary>
    SubtitleSplit = 0,

    /// <summary>The title without its sequel / part number (<c>Title 2</c>, <c>Title Part 3</c>).</summary>
    SequelNumberSplit = 1,
}

/// <summary>A derived (retrieval-only) title variant.</summary>
public sealed record DerivedTitle(string Text, DerivedTitleKind Kind);
