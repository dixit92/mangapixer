namespace com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>How a comics work or a comics record is published: single issues, or collected books (TPB, graphic novel, album).</summary>
public enum ComicsShape
{
    Unknown = 0,

    /// <summary>Single issues: short stapled books (US floppies), numbered <c>#1</c>, <c>#2</c> ...</summary>
    Issues = 1,

    /// <summary>Collected books: trade paperbacks, hardcovers, graphic novels, BD albums.</summary>
    Collected = 2,
}

/// <summary>
/// The local evidence a comics record is scored against (1.32.0, lane B). Read from local data only - the work's name, its
/// stored ComicInfo and page counts, and the admin's preferred language - nothing here is ever sent, except the start year:
/// the Grand Comics Database search may carry <see cref="StartYear"/> (a <c>(YYYY)</c> in the work's own name, the comics
/// taggers' convention), as AGENTS.md and the consent text say.
/// </summary>
/// <param name="StartYear">The <c>(YYYY)</c> year of the work's own name, or null.</param>
/// <param name="Language">ISO 639-1 code: the ComicInfo <c>LanguageISO</c> most of the work's files agree on, else the preferred language.</param>
/// <param name="Shape">Issues or collected books, from the files' page counts and format words.</param>
/// <param name="Publisher">The ComicInfo <c>Publisher</c> most of the work's files agree on, or null.</param>
public sealed record ComicsEvidence(int? StartYear, string? Language, ComicsShape Shape, string? Publisher)
{
    public static ComicsEvidence None { get; } = new(null, null, ComicsShape.Unknown, null);
}

/// <summary>Pure helpers for <see cref="ComicsEvidence"/> and the comics side of a provider record.</summary>
public static partial class ComicsEvidenceRules
{
    /// <summary>The comics provider's id (the Grand Comics Database, <c>metadata_records.Provider</c>).</summary>
    public const string ComicsProviderId = "gcd";

    /// <summary>Whether a candidate comes from the comics provider (the comics evidence is read for these only).</summary>
    public static bool IsComicsProvider(string? provider) => string.Equals(provider, ComicsProviderId, StringComparison.Ordinal);

    /// <summary>A median of at most this many pages per file reads as single issues (US floppies run 24-40 pages).</summary>
    public const int IssuePagesMax = 48;

    /// <summary>A median of at least this many pages reads as collected books (ComicTagger's TPB rule uses &gt; 100).</summary>
    public const int CollectedPagesMin = 100;

    [GeneratedRegex(
        @"(?<![\p{L}\p{N}])(?:TPB|HC|OGN|GN|Graphic\s+Novel|Omnibus|Absolute|Compendium|Library\s+Edition|Int[eé]grale|Gesamtausgabe|Integraal|Trade\s+Paperback)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CollectedWord();

    /// <summary>
    /// The shape of a work: a collected-format word in most file names, or the median page count (at least
    /// <see cref="CollectedPagesMin"/> collected, at most <see cref="IssuePagesMax"/> issues). Unknown in between or without data.
    /// Manga chapters are short too: the shape is scoring evidence for comics records only, never routing.
    /// </summary>
    public static ComicsShape ShapeOf(IReadOnlyList<string> archiveNames, int? medianPageCount)
    {
        ArgumentNullException.ThrowIfNull(archiveNames);
        if (archiveNames.Count > 0 && archiveNames.Count(n => CollectedWord().IsMatch(n ?? string.Empty)) * 2 > archiveNames.Count)
            return ComicsShape.Collected;
        return medianPageCount switch
        {
            >= CollectedPagesMin => ComicsShape.Collected,
            > 0 and <= IssuePagesMax => ComicsShape.Issues,
            _ => ComicsShape.Unknown,
        };
    }

    /// <summary>The median of a list of page counts (null when empty).</summary>
    public static int? Median(IReadOnlyList<int> pageCounts)
    {
        ArgumentNullException.ThrowIfNull(pageCounts);
        var sorted = pageCounts.Where(p => p > 0).Order().ToList();
        return sorted.Count == 0 ? null : sorted[(sorted.Count - 1) / 2];
    }

    /// <summary>An ISO 639-1 language code (two ASCII letters, lowercase) from a ComicInfo / settings value, else null.</summary>
    public static string? LanguageCode(string? value)
    {
        var s = value?.Trim();
        if (string.IsNullOrEmpty(s))
            return null;
        // "en", "en-US", "en_GB" -> "en".
        var head = s.Split('-', '_')[0];
        return head.Length == 2 && head.All(char.IsAsciiLetter) ? head.ToLowerInvariant() : null;
    }

    /// <summary>Publisher names compared loosely: case, accents, punctuation and a trailing "Comics" / "Publishing" / "Inc" are ignored.</summary>
    public static bool PublishersEqual(string? a, string? b)
    {
        var x = PublisherKey(a);
        var y = PublisherKey(b);
        return x.Length > 0 && x == y;
    }

    private static readonly string[] s_publisherSuffixes = ["comics", "comic", "publishing", "publications", "books", "inc", "ltd", "llc", "entertainment", "editions", "press"];

    internal static string PublisherKey(string? name)
    {
        var form = TitleNormalizer.ScoringForm(name);
        var words = form.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && s_publisherSuffixes.Contains(words[^1], StringComparer.Ordinal))
            words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }

    /// <summary>
    /// The distinct issue numbers of a list of issue descriptors (<c>1 [First Printing]</c>, <c>1 - Title</c>, <c>2</c>,
    /// <c>[nn]</c>, empty): a printing or variant of an issue is the same issue; an unnumbered book counts once.
    /// </summary>
    public static IReadOnlyList<decimal> DistinctIssueNumbers(IEnumerable<string?> descriptors, out int unnumbered)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var numbers = new SortedSet<decimal>();
        unnumbered = 0;
        foreach (var raw in descriptors)
        {
            var match = LeadingIssueNumber().Match(raw?.Trim() ?? string.Empty);
            if (match.Success && decimal.TryParse(match.Groups["n"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n))
                numbers.Add(n);
            else
                unnumbered++;
        }
        unnumbered = Math.Min(unnumbered, 1);
        return numbers.ToList();
    }

    [GeneratedRegex(@"^\[?(?<n>\d{1,5}(?:\.\d{1,2})?)\]?(?:$|[\s\[\-/])", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingIssueNumber();
}
