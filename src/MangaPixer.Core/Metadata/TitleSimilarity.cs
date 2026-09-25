namespace com.lifepixer.mangapixer.Core.Metadata;

/// <summary>
/// In-house title similarity for ranking identify candidates (1.24.0 stage 1:
/// display only, never auto-applies). No package: Levenshtein, token sort/set
/// ratios and trigram Dice are small enough to own.
///
/// <c>score = 0.45 * tokenSortRatio + 0.35 * tokenSetRatio' + 0.20 * trigramDice</c>,
/// where <c>tokenSetRatio'</c> is the classic token-set ratio multiplied by
/// <c>(1 - 0.5 * unmatchedTokenMass)</c>. The penalty is what keeps "Berserk" from
/// scoring as a perfect match for "Berserk of Gluttony" (a pure token-set ratio
/// treats a subset as identical). Inputs go through
/// <see cref="TitleNormalizer.ScoringForm"/> first.
/// </summary>
public static class TitleSimilarity
{
    /// <summary>Scores at or above this are labelled Strong.</summary>
    public const double StrongThreshold = 0.85;

    /// <summary>Scores at or above this (and below Strong) are labelled Possible.</summary>
    public const double PossibleThreshold = 0.60;

    /// <summary>Longer inputs are truncated before the quadratic Levenshtein step.</summary>
    public const int MaxCompareLength = 256;

    /// <summary>Similarity in [0, 1] between two titles (1 = identical after scoring normalization).</summary>
    public static double Score(string? a, string? b)
    {
        var x = Truncate(TitleNormalizer.ScoringForm(a));
        var y = Truncate(TitleNormalizer.ScoringForm(b));
        if (x.Length == 0 || y.Length == 0)
            return 0;
        if (x == y)
            return 1;

        var tokensX = Tokens(x);
        var tokensY = Tokens(y);
        var score = 0.45 * TokenSortRatio(tokensX, tokensY)
            + 0.35 * PenalizedTokenSetRatio(tokensX, tokensY)
            + 0.20 * TrigramDice(x, y);
        return Math.Clamp(score, 0, 1);
    }

    /// <summary>The best score over every (query, candidate) pair; 0 when either side is empty.</summary>
    public static double Best(IEnumerable<string> queries, IEnumerable<string> candidates)
    {
        var candidateList = candidates.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        var best = 0.0;
        foreach (var q in queries)
        {
            if (string.IsNullOrWhiteSpace(q)) continue;
            foreach (var c in candidateList)
                best = Math.Max(best, Score(q, c));
        }
        return best;
    }

    /// <summary>Strong / Possible / Weak label for a score.</summary>
    public static MatchStrength Label(double score) =>
        score >= StrongThreshold ? MatchStrength.Strong
        : score >= PossibleThreshold ? MatchStrength.Possible
        : MatchStrength.Weak;

    /// <summary>Levenshtein distance (two-row DP, O(n*m) time, O(m) memory).</summary>
    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    /// <summary>1 - distance / max length, in [0, 1].</summary>
    public static double Ratio(string a, string b)
    {
        var max = Math.Max(a.Length, b.Length);
        if (max == 0) return 1;
        return 1.0 - (double)Levenshtein(a, b) / max;
    }

    /// <summary>Ratio of the two token lists after sorting them (word order ignored).</summary>
    public static double TokenSortRatio(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        Ratio(string.Join(' ', a.Order(StringComparer.Ordinal)), string.Join(' ', b.Order(StringComparer.Ordinal)));

    /// <summary>
    /// Token-set ratio (intersection vs intersection + remainders), multiplied by
    /// <c>1 - 0.5 * unmatchedTokenMass</c>: the share of characters that sit in
    /// tokens found on only one side.
    /// </summary>
    public static double PenalizedTokenSetRatio(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var setA = a.ToHashSet(StringComparer.Ordinal);
        var setB = b.ToHashSet(StringComparer.Ordinal);
        var common = setA.Intersect(setB, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var onlyA = setA.Except(setB, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var onlyB = setB.Except(setA, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        var t0 = string.Join(' ', common);
        var t1 = string.Join(' ', common.Concat(onlyA));
        var t2 = string.Join(' ', common.Concat(onlyB));
        var raw = Math.Max(Ratio(t0, t1), Math.Max(Ratio(t0, t2), Ratio(t1, t2)));
        if (common.Count == 0)
            raw = Ratio(t1, t2);

        var total = setA.Sum(t => t.Length) + setB.Sum(t => t.Length);
        var unmatched = onlyA.Sum(t => t.Length) + onlyB.Sum(t => t.Length);
        var mass = total == 0 ? 0 : (double)unmatched / total;
        return raw * (1 - 0.5 * mass);
    }

    /// <summary>Dice coefficient over the character trigram sets of the padded strings.</summary>
    public static double TrigramDice(string a, string b)
    {
        var ta = Trigrams(a);
        var tb = Trigrams(b);
        if (ta.Count == 0 && tb.Count == 0) return 1;
        if (ta.Count == 0 || tb.Count == 0) return 0;
        var common = ta.Count(tb.Contains);
        return 2.0 * common / (ta.Count + tb.Count);
    }

    private static HashSet<string> Trigrams(string s)
    {
        var padded = "  " + s + " ";
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 3 <= padded.Length; i++)
            set.Add(padded.Substring(i, 3));
        return set;
    }

    private static List<string> Tokens(string s) =>
        s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string Truncate(string s) => s.Length > MaxCompareLength ? s[..MaxCompareLength] : s;
}

/// <summary>Display label of an identify candidate's score.</summary>
public enum MatchStrength
{
    Weak = 0,
    Possible = 1,
    Strong = 2,
}
