namespace com.lifepixer.mangapixer.Core.Metadata.Wikipedia;

using System.Text.RegularExpressions;

/// <summary>
/// The two decisions of the Wikipedia companion that need no network (1.32.0). Pure.
/// </summary>
public static partial class WikipediaDiscovery
{
    [GeneratedRegex(@"\s*\([^()]*\)\s*$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TrailingParenthetical();

    [GeneratedRegex(@"^Lists? of ", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ListTitle();

    /// <summary>True for a title that already names a list page (<c>List of X chapters</c>, <c>Lists of X chapters</c>).</summary>
    public static bool IsListTitle(string? title) => title is not null && ListTitle().IsMatch(title);

    /// <summary>
    /// The titles to read for a series whose article is <paramref name="article"/> (a Wikidata sitelink, an admin's choice, or the linked record's
    /// English title): the article itself - the list may live inside it, or a <c>List of ... chapters</c> title may redirect to it - and the
    /// usual list titles <c>List of X chapters</c> / <c>Lists of X chapters</c> (X = the article title without a trailing <c>(manga)</c>). A title
    /// that is already a list page is read alone. <paramref name="includeArticle"/> false (the record's title is a GUESS, not a page Wikidata or
    /// an admin named) reads only the list titles.
    /// </summary>
    public static IReadOnlyList<string> TitlesToRead(string article, bool includeArticle = true)
    {
        ArgumentNullException.ThrowIfNull(article);
        var title = Clean(article);
        if (title.Length == 0)
            return [];
        if (IsListTitle(title))
            return [title];
        var bare = Clean(TrailingParenthetical().Replace(title, string.Empty));
        if (bare.Length == 0)
            bare = title;
        var titles = new List<string>();
        if (includeArticle)
            titles.Add(title);
        titles.Add($"List of {bare} chapters");
        titles.Add($"Lists of {bare} chapters");
        return titles.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string Clean(string text) => string.Join(' ', text.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Whether Wikipedia could add anything to a series' MangaDex data (the privacy contract asks it only where it can): MangaDex has no
    /// usable exact list (no list at all, or only an AniList ratio), or its list leaves chapters unassigned (the "none" bucket, dropped
    /// noise), or its highest volume is below the series' volume total (newest volumes missing).
    /// </summary>
    /// <param name="hasExactList">MangaDex gave a usable exact list (<c>VolumeMapService.HasVolumeList</c>).</param>
    /// <param name="mangaDexVolumes">The stored MangaDex list.</param>
    /// <param name="unassigned">The chapters MangaDex leaves without a volume.</param>
    /// <param name="seriesVolumeTotal">The linked record's volume total, or null.</param>
    public static bool CanAdd(bool hasExactList, IReadOnlyList<VolumeMapEntry> mangaDexVolumes, IReadOnlyList<string> unassigned, int? seriesVolumeTotal)
    {
        ArgumentNullException.ThrowIfNull(mangaDexVolumes);
        ArgumentNullException.ThrowIfNull(unassigned);
        if (!hasExactList)
            return true;
        if (unassigned.Count > 0)
            return true;
        if (seriesVolumeTotal is not { } total)
            return false;
        var highest = mangaDexVolumes
            .Select(v => VolumeMapJson.Parse(v.Volume))
            .OfType<decimal>()
            .DefaultIfEmpty(0m)
            .Max();
        return decimal.Truncate(highest) < total;
    }
}
