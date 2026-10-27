namespace com.lifepixer.mangapixer.Core.Catalog;

using System.Globalization;
using System.Text;

/// <summary>
/// One word of a search (1.49.0): <see cref="Raw"/> as typed (NFC; what the FTS index is asked for - SQLite folds case and accents
/// itself), <see cref="Folded"/> lower-cased with its accents removed (what the C# checks and the ranking compare).
/// </summary>
public sealed record SearchWord(string Raw, string Folded)
{
    /// <summary>The folded length - the 3-character trigram floor and the typo-tolerance bands are measured on it.</summary>
    public int Length => Folded.Length;
}

/// <summary>
/// A parsed plain search (1.49.0, decision 5): the unquoted <see cref="Words"/> (any order) and the quoted <see cref="Phrases"/> (each
/// in its order, adjacent). Punctuation separates words and is otherwise ignored.
/// </summary>
public sealed record SearchQuery(IReadOnlyList<SearchWord> Words, IReadOnlyList<IReadOnlyList<SearchWord>> Phrases)
{
    /// <summary>Every word in the order typed (phrase words included).</summary>
    public IReadOnlyList<SearchWord> AllWords { get; } = Words.Concat(Phrases.SelectMany(p => p)).ToList();

    public bool IsEmpty => AllWords.Count == 0;

    /// <summary>
    /// The words the trigram index can look up (3+ characters), distinct by their folded form. Empty for a short search, which is matched
    /// by word starts instead (<see cref="SearchText.IsShort"/>).
    /// </summary>
    public IReadOnlyList<SearchWord> IndexWords { get; } = Words.Concat(Phrases.SelectMany(p => p))
        .Where(w => w.Length >= SearchText.TrigramLength)
        .DistinctBy(w => w.Folded, StringComparer.Ordinal)
        .ToList();
}

/// <summary>
/// The plain search's text rules (1.49.0, lane S; owner decisions 4-6): parsing words and quoted phrases, the accent- and case-folded form
/// both sides are compared in, the FTS5 queries (exact and typo-tolerant candidates), the rank tiers, and the typo distance. Pure and
/// allocation-light: the server reads the candidate names and ranks them here.
/// </summary>
public static class SearchText
{
    /// <summary>The trigram index needs at least this many characters to look a word up.</summary>
    public const int TrigramLength = 3;

    /// <summary>A word shorter than this never matches with a typo (decision 5: a minimum word length).</summary>
    public const int MinFuzzyLength = 4;

    /// <summary>Rank tiers of an exact hit, best first (decision 6). A typo-tolerant hit always ranks below every exact one.</summary>
    public const int RankExact = 0, RankStartsWith = 1, RankWordStarts = 2, RankContains = 3;

    private static readonly string[] ArchiveExtensions =
        [".cbz", ".cbr", ".cb7", ".cbt", ".zip", ".rar", ".7z", ".tar", ".pdf", ".epub"];

    /// <summary>
    /// Parses the search text: <c>"..."</c> keeps an exact phrase (an unclosed quote runs to the end); everything else is split into words
    /// on any character that is not a letter, digit or combining mark.
    /// </summary>
    public static SearchQuery Parse(string? input)
    {
        var words = new List<SearchWord>();
        var phrases = new List<IReadOnlyList<SearchWord>>();
        if (string.IsNullOrWhiteSpace(input))
            return new SearchQuery(words, phrases);

        var text = input.Normalize(NormalizationForm.FormC);
        var i = 0;
        while (i < text.Length)
        {
            var quote = text.IndexOf('"', i);
            if (quote < 0)
            {
                words.AddRange(SplitWords(text.AsSpan(i)));
                break;
            }
            words.AddRange(SplitWords(text.AsSpan(i, quote - i)));
            var close = text.IndexOf('"', quote + 1);
            var end = close < 0 ? text.Length : close;
            var phrase = SplitWords(text.AsSpan(quote + 1, end - quote - 1));
            if (phrase.Count == 1)
                words.Add(phrase[0]);
            else if (phrase.Count > 1)
                phrases.Add(phrase);
            i = close < 0 ? text.Length : close + 1;
        }
        return new SearchQuery(words, phrases);
    }

    /// <summary>A search with no word of 3+ characters: names match when every word STARTS a word of the name (no index, no typos).</summary>
    public static bool IsShort(SearchQuery query) => !query.IsEmpty && query.IndexWords.Count == 0;

    /// <summary>
    /// The FTS5 query for exact candidates: every index word as its own quoted string, ANDed - any order. Null for a short or empty search.
    /// </summary>
    public static string? ExactFtsQuery(SearchQuery query) =>
        query.IndexWords.Count == 0 ? null : string.Join(" AND ", query.IndexWords.Select(w => Quote(w.Raw)));

    /// <summary>
    /// The FTS5 query for typo-tolerant candidates (decision 5 b): an unquoted word of 4+ characters becomes "the word OR any of its
    /// trigrams"; every other index word stays an exact quoted string; all ANDed. Null when no word may carry a typo.
    /// </summary>
    public static string? FuzzyFtsQuery(SearchQuery query)
    {
        if (!query.Words.Any(w => w.Length >= MinFuzzyLength))
            return null;
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in query.Words.Concat(query.Phrases.SelectMany(p => p)))
        {
            if (word.Length < TrigramLength || !seen.Add(word.Folded))
                continue;
            var fuzzy = word.Length >= MinFuzzyLength && query.Words.Contains(word);
            if (!fuzzy)
            {
                parts.Add(Quote(word.Raw));
                continue;
            }
            var grams = new List<string> { Quote(word.Folded) };
            for (var g = 0; g + TrigramLength <= word.Folded.Length; g++)
                grams.Add(Quote(word.Folded.Substring(g, TrigramLength)));
            parts.Add("(" + string.Join(" OR ", grams.Distinct(StringComparer.Ordinal)) + ")");
        }
        return parts.Count == 0 ? null : string.Join(" AND ", parts);
    }

    /// <summary>
    /// The folded form of a name or text: canonical decomposition, combining marks removed, lower-cased (invariant), every run of
    /// characters that are not letters or digits made one space, trimmed. "Kaguya-sama: Love is War" -> "kaguya sama love is war".
    /// </summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var ascii = true;
        foreach (var c in text)
        {
            if (c > 0x7F) { ascii = false; break; }
        }
        var source = ascii ? text : text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(source.Length);
        var pendingSpace = false;
        foreach (var c in source)
        {
            if (!ascii && CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
                continue;
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && sb.Length > 0)
                    sb.Append(' ');
                pendingSpace = false;
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingSpace = true;
            }
        }
        return sb.ToString();
    }

    /// <summary>The folded name a hit is ranked by: an archive's file extension is left out ("Alpha 001.cbz" ranks as "alpha 001").</summary>
    public static string FoldName(string displayName, bool isArchive)
    {
        if (isArchive)
        {
            foreach (var ext in ArchiveExtensions)
            {
                if (displayName.Length > ext.Length && displayName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return Fold(displayName[..^ext.Length]);
            }
        }
        return Fold(displayName);
    }

    /// <summary>
    /// The rank tier of a name the index already matched (every 3+ character word is in it), or null when a rule the index cannot check
    /// fails: a 1-2 character word must start a word of the name, a phrase must appear in order. <paramref name="foldedName"/> comes from
    /// <see cref="FoldName"/>. For a short search (<see cref="IsShort"/>) every word must start a word of the name.
    /// </summary>
    public static int? Rank(SearchQuery query, string foldedName)
    {
        if (query.IsEmpty)
            return null;
        var padded = " " + foldedName;
        var shortSearch = IsShort(query);
        foreach (var word in query.Words)
        {
            if ((shortSearch || word.Length < TrigramLength) && !padded.Contains(" " + word.Folded, StringComparison.Ordinal))
                return null;
        }
        foreach (var phrase in query.Phrases)
        {
            var text = string.Join(' ', phrase.Select(w => w.Folded));
            if (!(shortSearch ? padded.Contains(" " + text, StringComparison.Ordinal) : foldedName.Contains(text, StringComparison.Ordinal)))
                return null;
        }
        return Tier(query, foldedName, padded);
    }

    private static int Tier(SearchQuery query, string foldedName, string padded)
    {
        var all = string.Join(' ', query.AllWords.Select(w => w.Folded));
        if (foldedName == all)
            return RankExact;
        if (foldedName.StartsWith(all, StringComparison.Ordinal))
            return RankStartsWith;
        return query.AllWords.All(w => padded.Contains(" " + w.Folded, StringComparison.Ordinal)) ? RankWordStarts : RankContains;
    }

    /// <summary>
    /// The typo distance of a name that is NOT an exact hit (decision 5 b), or null when it is not close enough: every unquoted word of
    /// 4+ characters is in the name or within <see cref="MaxDistance"/> of a word of the name (or of that word's start, one character
    /// shorter or longer than the typed word); every other word and phrase must match exactly as in <see cref="Rank"/>. The sum of the
    /// distances, at least 1.
    /// </summary>
    public static int? FuzzyDistance(SearchQuery query, string foldedName)
    {
        if (IsShort(query) || query.IsEmpty)
            return null;
        var padded = " " + foldedName;
        string[]? nameWords = null;
        var total = 0;
        foreach (var word in query.Words)
        {
            if (word.Length < TrigramLength)
            {
                if (!padded.Contains(" " + word.Folded, StringComparison.Ordinal))
                    return null;
                continue;
            }
            if (foldedName.Contains(word.Folded, StringComparison.Ordinal))
                continue;
            var max = MaxDistance(word.Length);
            if (max == 0)
                return null;
            nameWords ??= foldedName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var best = int.MaxValue;
            foreach (var nameWord in nameWords)
            {
                best = Math.Min(best, BestPrefixDistance(word.Folded, nameWord, max));
                if (best == 1)
                    break;
            }
            if (best > max)
                return null;
            total += best;
        }
        foreach (var phrase in query.Phrases)
        {
            if (!foldedName.Contains(string.Join(' ', phrase.Select(w => w.Folded)), StringComparison.Ordinal))
                return null;
        }
        return total > 0 ? total : null;
    }

    /// <summary>The largest typo distance a word of this (folded) length may carry: none below 4 characters, 1 up to 7, 2 from 8.</summary>
    public static int MaxDistance(int length) => length >= 8 ? 2 : length >= MinFuzzyLength ? 1 : 0;

    private static int BestPrefixDistance(string word, string nameWord, int max)
    {
        if (Math.Abs(nameWord.Length - word.Length) <= max)
            return Distance(word, nameWord, max);
        var best = int.MaxValue;
        for (var length = word.Length - 1; length <= word.Length + 1; length++)
        {
            if (length < 1 || length >= nameWord.Length)
                continue;
            best = Math.Min(best, Distance(word, nameWord[..length], max));
        }
        return best;
    }

    /// <summary>
    /// Damerau-Levenshtein distance (optimal string alignment: insert, delete, substitute, swap two neighbours), stopping early once
    /// every path exceeds <paramref name="max"/> (then any value above it is returned).
    /// </summary>
    public static int Distance(string a, string b, int max = int.MaxValue)
    {
        if (a == b)
            return 0;
        if (Math.Abs(a.Length - b.Length) > max)
            return max == int.MaxValue ? Math.Max(a.Length, b.Length) : max + 1;
        var n = a.Length;
        var m = b.Length;
        if (n == 0) return m;
        if (m == 0) return n;
        var prev2 = new int[m + 1];
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (var j = 0; j <= m; j++)
            prev[j] = j;
        for (var i = 1; i <= n; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var v = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    v = Math.Min(v, prev2[j - 2] + 1);
                cur[j] = v;
                rowMin = Math.Min(rowMin, v);
            }
            if (max != int.MaxValue && rowMin > max)
                return max + 1;
            (prev2, prev, cur) = (prev, cur, prev2);
        }
        return prev[m];
    }

    private static List<SearchWord> SplitWords(ReadOnlySpan<char> text)
    {
        var result = new List<SearchWord>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inWord = i < text.Length && IsWordChar(text[i]);
            if (inWord && start < 0)
            {
                start = i;
            }
            else if (!inWord && start >= 0)
            {
                var raw = text[start..i].ToString();
                var folded = Fold(raw);
                if (folded.Length > 0)
                    result.Add(new SearchWord(raw, folded));
                start = -1;
            }
        }
        return result;
    }

    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c)
        || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;

    private static string Quote(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
