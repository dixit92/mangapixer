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
/// in its order, adjacent). Punctuation separates words and is otherwise ignored. 1.50.0: the <see cref="Negated"/> terms (<c>-word</c>,
/// <c>-"two words"</c>) a listed name must not contain.
/// </summary>
public sealed record SearchQuery(IReadOnlyList<SearchWord> Words, IReadOnlyList<IReadOnlyList<SearchWord>> Phrases)
{
    /// <summary>Every word in the order typed (phrase words included; never a negated one).</summary>
    public IReadOnlyList<SearchWord> AllWords { get; } = Words.Concat(Phrases.SelectMany(p => p)).ToList();

    /// <summary>
    /// 1.50.0: the terms after a leading <c>-</c>, each as its words in order (<c>-re:zero</c> = "re zero", <c>-"one piece"</c> = "one
    /// piece"). A name holding one of them is left out (<see cref="SearchText.IsExcluded"/>). Never looked up in the index.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<SearchWord>> Negated { get; init; } = [];

    /// <summary>No word to look for (a search of only <c>-word</c> terms is empty too: it lists nothing - lane S rule, 1.50.0).</summary>
    public bool IsEmpty => AllWords.Count == 0;

    /// <summary>A search of nothing but <c>-word</c> terms (1.50.0): it lists nothing.</summary>
    public bool HasOnlyExclusions => IsEmpty && Negated.Count > 0;

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
    /// on any character that is not a letter, digit or combining mark. 1.50.0: a <c>-</c> at the start of the text or after white space,
    /// right before a letter, digit or quote, makes the next term (up to the next white space or quote, or the quoted phrase) one that a
    /// name must not hold (<see cref="SearchQuery.Negated"/>). A <c>-</c> anywhere else - inside a word (<c>Spider-Man</c>) or before a
    /// space - separates words as before.
    /// </summary>
    public static SearchQuery Parse(string? input)
    {
        var words = new List<SearchWord>();
        var phrases = new List<IReadOnlyList<SearchWord>>();
        var negated = new List<IReadOnlyList<SearchWord>>();
        if (string.IsNullOrWhiteSpace(input))
            return new SearchQuery(words, phrases);

        var text = input.Normalize(NormalizationForm.FormC);
        var plain = 0; // start of the plain text not split yet
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                words.AddRange(SplitWords(text.AsSpan(plain, i - plain)));
                var (phrase, next) = Quoted(text, i);
                if (phrase.Count == 1)
                    words.Add(phrase[0]);
                else if (phrase.Count > 1)
                    phrases.Add(phrase);
                i = plain = next;
                continue;
            }
            if (c == '-' && (i == 0 || char.IsWhiteSpace(text[i - 1])) && i + 1 < text.Length
                && (text[i + 1] == '"' || IsWordChar(text[i + 1])))
            {
                words.AddRange(SplitWords(text.AsSpan(plain, i - plain)));
                List<SearchWord> term;
                if (text[i + 1] == '"')
                {
                    (term, i) = Quoted(text, i + 1);
                }
                else
                {
                    var end = i + 1;
                    while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '"')
                        end++;
                    term = SplitWords(text.AsSpan(i + 1, end - i - 1));
                    i = end;
                }
                if (term.Count > 0)
                    negated.Add(term);
                plain = i;
                continue;
            }
            i++;
        }
        words.AddRange(SplitWords(text.AsSpan(plain)));
        return new SearchQuery(words, phrases) { Negated = negated };
    }

    /// <summary>The words of the quoted part opening at <paramref name="quote"/> and the index after it (an unclosed quote runs to the end).</summary>
    private static (List<SearchWord> Words, int Next) Quoted(string text, int quote)
    {
        var close = text.IndexOf('"', quote + 1);
        var end = close < 0 ? text.Length : close;
        return (SplitWords(text.AsSpan(quote + 1, end - quote - 1)), close < 0 ? text.Length : close + 1);
    }

    /// <summary>
    /// Whether a name is left out by the search's <c>-word</c> terms (1.50.0): a single word of 1-2 characters when it starts a word of the
    /// name, any other term when the name contains it (its words in order) - the rules a word or phrase that is looked FOR follows.
    /// <paramref name="foldedName"/> comes from <see cref="FoldName"/> (or <see cref="Fold"/>).
    /// </summary>
    public static bool IsExcluded(SearchQuery query, string foldedName)
    {
        foreach (var term in query.Negated)
        {
            if (term.Count == 1 && term[0].Length < TrigramLength
                ? HasWordStartingWith(foldedName, term[0].Folded)
                : foldedName.Contains(term.Count == 1 ? term[0].Folded : string.Join(' ', term.Select(w => w.Folded)), StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>A search with no word of 3+ characters: names match when every word STARTS a word of the name (no index, no typos).</summary>
    public static bool IsShort(SearchQuery query) => !query.IsEmpty && query.IndexWords.Count == 0;

    /// <summary>
    /// The FTS5 query for exact candidates: every index word as its own quoted string, ANDed - any order. Null for a short or empty search.
    /// </summary>
    public static string? ExactFtsQuery(SearchQuery query) =>
        query.IndexWords.Count == 0 ? null : string.Join(" AND ", query.IndexWords.Select(w => Quote(w.Raw)));

    /// <summary>
    /// SQL <c>LIKE</c> patterns of which a name holding <paramref name="word"/> within its typo distance (or exactly) contains at least one
    /// - a cheap SQL narrowing before the C# distance (1.49.0). Null when the word is not plain ASCII (LIKE folds ASCII case only) or may
    /// carry no typo. The word is cut into pieces with one character between them: one edit (a swap of two neighbours included) touches
    /// two adjacent positions at most, so it can break only one piece - two pieces for a distance of 1, three for 2. A 4-letter word adds
    /// <c>%a__d%</c> for a swap of its two middle letters. Letters and digits only: nothing to escape.
    /// </summary>
    public static IReadOnlyList<string>? TypoPatterns(SearchWord word)
    {
        var w = word.Folded;
        var max = MaxDistance(w.Length);
        if (max == 0 || !w.All(char.IsAsciiLetterOrDigit))
            return null;
        if (w.Length == MinFuzzyLength)
            return ["%" + w[..2] + "%", "%" + w[2..] + "%", "%" + w[0] + "__" + w[3] + "%"];
        var pieces = max + 1;
        var size = (w.Length - max) / pieces;   // pieces of `size` with one character between them; the last takes the rest
        var patterns = new List<string>();
        var at = 0;
        for (var i = 0; i < pieces; i++)
        {
            var end = i == pieces - 1 ? w.Length : at + size;
            patterns.Add("%" + w[at..end] + "%");
            at = end + 1;
        }
        return patterns;
    }

    /// <summary>Whether the search may match with typos at all (decision 5 b): it has an unquoted word of 4+ characters.</summary>
    public static bool AllowsTypos(SearchQuery query) => query.Words.Any(w => w.Length >= MinFuzzyLength);

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
        // A stack buffer for the usual short name (this runs for every visible name in a short or typo search).
        Span<char> buffer = source.Length <= 256 ? stackalloc char[source.Length] : new char[source.Length];
        var n = 0;
        var pendingSpace = false;
        foreach (var c in source)
        {
            if (!ascii && CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
                continue;
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && n > 0)
                    buffer[n++] = ' ';
                pendingSpace = false;
                buffer[n++] = char.ToLowerInvariant(c);
            }
            else
            {
                pendingSpace = true;
            }
        }
        return new string(buffer[..n]);
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
        var shortSearch = IsShort(query);
        foreach (var word in query.Words)
        {
            if ((shortSearch || word.Length < TrigramLength) && !HasWordStartingWith(foldedName, word.Folded))
                return null;
        }
        foreach (var phrase in query.Phrases)
        {
            var text = string.Join(' ', phrase.Select(w => w.Folded));
            if (!(shortSearch ? HasWordStartingWith(foldedName, text) : foldedName.Contains(text, StringComparison.Ordinal)))
                return null;
        }
        return Tier(query, foldedName);
    }

    private static int Tier(SearchQuery query, string foldedName)
    {
        var all = string.Join(' ', query.AllWords.Select(w => w.Folded));
        if (foldedName == all)
            return RankExact;
        if (foldedName.StartsWith(all, StringComparison.Ordinal))
            return RankStartsWith;
        return query.AllWords.All(w => HasWordStartingWith(foldedName, w.Folded)) ? RankWordStarts : RankContains;
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
        var total = 0;
        foreach (var word in query.Words)
        {
            if (word.Length < TrigramLength)
            {
                if (!HasWordStartingWith(foldedName, word.Folded))
                    return null;
                continue;
            }
            if (foldedName.Contains(word.Folded, StringComparison.Ordinal))
                continue;
            var max = MaxDistance(word.Length);
            if (max == 0)
                return null;
            var best = int.MaxValue;
            foreach (var range in foldedName.AsSpan().Split(' '))
            {
                var nameWord = foldedName.AsSpan(range);
                if (nameWord.IsEmpty)
                    continue;
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

    /// <summary>
    /// Whether <paramref name="text"/> (folded) starts a word of <paramref name="foldedName"/> - at the start or after a space; no
    /// allocation (it runs for every visible name in a short or typo search). 1.50.0: text that starts with a digit also starts a number
    /// after its leading zeros (<c>6</c> and <c>06</c> start <c>006</c>; <c>12</c> starts <c>0012</c>) - title-less volume names are
    /// usually zero-padded (<c>Vol. 006</c>). The typed zeros still count (<c>01</c> does not start <c>1</c>).
    /// </summary>
    public static bool HasWordStartingWith(string foldedName, string text)
    {
        if (text.Length == 0)
            return true;
        var digit = char.IsAsciiDigit(text[0]);
        var at = foldedName.IndexOf(text, StringComparison.Ordinal);
        while (at >= 0)
        {
            if (at == 0 || foldedName[at - 1] == ' ')
                return true;
            if (digit && foldedName[at - 1] == '0')
            {
                var start = at - 1;
                while (start > 0 && foldedName[start - 1] == '0')
                    start--;
                if (start == 0 || foldedName[start - 1] == ' ')
                    return true;
            }
            at = foldedName.IndexOf(text, at + 1, StringComparison.Ordinal);
        }
        return false;
    }

    /// <summary>The largest typo distance a word of this (folded) length may carry: none below 4 characters, 1 up to 7, 2 from 8.</summary>
    public static int MaxDistance(int length) => length >= 8 ? 2 : length >= MinFuzzyLength ? 1 : 0;

    private static int BestPrefixDistance(ReadOnlySpan<char> word, ReadOnlySpan<char> nameWord, int max)
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
    public static int Distance(string a, string b, int max = int.MaxValue) => Distance(a.AsSpan(), b.AsSpan(), max);

    /// <inheritdoc cref="Distance(string, string, int)"/>
    public static int Distance(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int max = int.MaxValue)
    {
        if (a.SequenceEqual(b))
            return 0;
        if (Math.Abs(a.Length - b.Length) > max)
            return max == int.MaxValue ? Math.Max(a.Length, b.Length) : max + 1;
        var n = a.Length;
        var m = b.Length;
        if (n == 0) return m;
        if (m == 0) return n;
        // Three rows; on the stack for the short words of names (the scan runs this for every word of every visible name).
        Span<int> rows = m + 1 <= 64 ? stackalloc int[3 * (m + 1)] : new int[3 * (m + 1)];
        var prev2 = rows[..(m + 1)];
        var prev = rows.Slice(m + 1, m + 1);
        var cur = rows.Slice(2 * (m + 1), m + 1);
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
            var spare = prev2;
            prev2 = prev;
            prev = cur;
            cur = spare;
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
