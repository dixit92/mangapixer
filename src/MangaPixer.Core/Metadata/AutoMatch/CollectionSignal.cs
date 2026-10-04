namespace com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

/// <summary>A stored review candidate as the collection signal sees it.</summary>
public sealed record CollectionCandidate(int Rank, MetadataFormat? Format, double TitleScore);

/// <summary>
/// The "Looks like a collection about &lt;Series&gt;" hint of Needs review (1.34.0, owner decision 4: the matcher may SUGGEST it, never
/// link it). A folder waiting in review whose archives are doujin-shaped works by several circles, and whose name matches a series
/// record (not a doujinshi record) closely, is most likely a folder of fan works about that series. Read at list time from the
/// folder's archive names and the stored candidates - nothing is requested, stored or re-scored. Thresholds from the 1.34.0 census
/// (199 waiting folders: 25 hits; 607 linked folders: none passes the shape part) - kept conservative.
/// </summary>
public static class CollectionSignal
{
    /// <summary>The folder must hold at least this many direct archives.</summary>
    public const int MinArchives = 3;

    /// <summary>Share of the archives whose name starts with a circle / artist tag.</summary>
    public const double MinTaggedShare = 0.8;

    /// <summary>Share of the archives carrying a volume / chapter token at or above which the folder is a series (scanlation tags).</summary>
    public const double MaxUnitShare = 0.5;

    /// <summary>Different authors among the tags (a collection of circles, not one group's releases).</summary>
    public const int MinAuthors = 2;

    /// <summary>Title score the series candidate needs (the matcher's "confident" title).</summary>
    public const double MinTitleScore = 0.85;

    /// <summary>
    /// The candidate the folder looks like a collection about (the best-scored non-doujinshi candidate, lowest rank on a tie), or null
    /// when the archive names or the candidates do not give the signal.
    /// </summary>
    public static CollectionCandidate? Suggest(IReadOnlyList<string> archiveNames, IReadOnlyList<CollectionCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(archiveNames);
        ArgumentNullException.ThrowIfNull(candidates);
        if (archiveNames.Count < MinArchives)
            return null;

        var tagged = 0;
        var units = 0;
        var authors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in archiveNames)
        {
            var names = ReviewAuthorNames.FromWorkName(name);
            if (names.Count > 0)
                tagged++;
            foreach (var n in names)
                authors.Add(n.Key);
            if (ArchiveNameAnatomy.Parse(name).HasUnitToken)
                units++;
        }
        var total = (double)archiveNames.Count;
        if (tagged / total < MinTaggedShare || units / total >= MaxUnitShare || authors.Count < MinAuthors)
            return null;

        return candidates
            .Where(c => c.Format != MetadataFormat.Doujinshi && c.TitleScore >= MinTitleScore - 1e-9)
            .OrderByDescending(c => c.TitleScore)
            .ThenBy(c => c.Rank)
            .FirstOrDefault();
    }
}
