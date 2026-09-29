namespace com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaDex;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;

/// <summary>
/// The cross-link rule (1.29.0, design section 1.3; measured 39 of 41 golden records linked, 0 false accepts): a
/// MangaDex record is the companion of a MangaUpdates record R ONLY when MangaDex's own <c>links.mu</c> names R. Pure -
/// no I/O; the service sends the queries this class builds (R's own title, or one associated title of R: never a folder
/// or file name).
/// <list type="number">
/// <item>Query 1 = R's title minus a trailing parenthetical disambiguator (<c>"Chainsaw Man (FUJIMOTO Tatsuki)"</c>).</item>
/// <item>Candidates = results whose <c>links.mu</c> decodes (base36) to R's id; an all-digit value only when it has at
/// least 8 digits and equals R's decimal id (a shorter one is a legacy id that never maps to a current one).</item>
/// <item>Query 2 (only when query 1 has no candidate) = R's first associated title that is ASCII, has at least two
/// words and differs from query 1 after normalisation. Nothing after query 2.</item>
/// <item>Tie-break between candidates: (a) carries <c>links.al</c> (equal to the stored AniList id when one is stored),
/// (b) not tagged "Fan Colored", (c) earliest <c>createdAt</c>, (d) relevance order.</item>
/// </list>
/// </summary>
public static partial class MangaDexCrossLink
{
    [GeneratedRegex(@"\s*\([^()]*\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingParenthetical();

    /// <summary>Query 1: the record's title without a trailing parenthetical (kept when nothing else would remain).</summary>
    public static string Query1(string recordTitle)
    {
        var text = MetadataGateway.NormalizeQuery(recordTitle);
        var stripped = MetadataGateway.NormalizeQuery(TrailingParenthetical().Replace(text, string.Empty));
        return stripped.Length > 0 ? stripped : text;
    }

    /// <summary>Query 2: the first ASCII, multi-word associated title that differs from query 1; null when there is none.</summary>
    public static string? Query2(IEnumerable<string> associatedTitles, string query1)
    {
        var first = Key(query1);
        foreach (var raw in associatedTitles)
        {
            var title = MetadataGateway.NormalizeQuery(raw);
            if (title.Length is 0 or > MetadataGateway.MaxQueryLength || title.Any(c => c > 127))
                continue;
            if (title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2)
                continue;
            if (Key(title) == first)
                continue;
            return title;
        }
        return null;
    }

    /// <summary>True when a <c>links.mu</c> value names the MangaUpdates record with the decimal id <paramref name="recordExternalId"/>.</summary>
    public static bool LinksTo(string? muLink, string recordExternalId)
    {
        if (string.IsNullOrWhiteSpace(muLink) || !long.TryParse(recordExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            return false;
        var value = muLink.Trim();
        if (value.All(char.IsAsciiDigit))
            return value.Length >= 8 && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) && numeric == id;
        return MangaUpdatesReference.TryDecodeBase36(value, out var decoded) && decoded == id;
    }

    /// <summary>The companion among <paramref name="results"/> (relevance order), or null when no result links to the record.</summary>
    public static MangaDexManga? Pick(IReadOnlyList<MangaDexManga> results, string recordExternalId, string? storedAniListId = null)
    {
        var candidates = results
            .Select((m, rank) => (Manga: m, Rank: rank))
            .Where(x => LinksTo(x.Manga.MangaUpdatesLink, recordExternalId))
            .ToList();
        if (candidates.Count == 0)
            return null;
        return candidates
            .OrderBy(x => storedAniListId is not null
                ? (x.Manga.AniListId == storedAniListId ? 0 : x.Manga.AniListId is not null ? 1 : 2)
                : (x.Manga.AniListId is not null ? 0 : 1))
            .ThenBy(x => x.Manga.FanColored ? 1 : 0)
            .ThenBy(x => x.Manga.CreatedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(x => x.Rank)
            .First().Manga;
    }

    private static string Key(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKD))
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
