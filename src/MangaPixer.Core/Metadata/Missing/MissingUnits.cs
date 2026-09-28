namespace com.lifepixer.mangapixer.Core.Metadata.Missing;

using System.Globalization;
using System.Text.RegularExpressions;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

// The missing volumes / chapters report (1.28.0): compares the unit NUMBERS on disk
// of a linked series with the totals its stored record states. Pure; it reads names
// and stored numbers only, never a path, and makes no request. Values of the enums
// are append-only (they travel over the API as names).

/// <summary>Volumes and chapters are compared separately, never with each other.</summary>
public enum MissingUnitKind
{
    Volume = 0,
    Chapter = 1,
}

/// <summary>Where the published total came from.</summary>
public enum MissingTotalSource
{
    /// <summary>The English publisher's notes ("10 Volumes / 60 Chapters; Ongoing").</summary>
    English = 0,

    /// <summary>The status in the country of origin ("14 Volumes (Complete)", "195 Chapters (Hiatus)").</summary>
    Origin = 1,

    /// <summary>The provider's latest chapter (scanlation releases; chapters only).</summary>
    LatestChapter = 2,
}

/// <summary>How far the total can be trusted as "what you could own".</summary>
public enum MissingConfidence
{
    Low = 0,
    Medium = 1,
    High = 2,
}

/// <summary>The series-level outcome, worst first.</summary>
public enum MissingVerdict
{
    /// <summary>The published total is higher than the highest number on disk.</summary>
    Behind = 0,

    /// <summary>Not behind, but numbers below the highest are missing.</summary>
    Holes = 1,

    /// <summary>Every number up to the published total is on disk.</summary>
    UpToDate = 2,

    /// <summary>Numbered units on disk, but the record states no total for them.</summary>
    NoTotal = 3,

    /// <summary>The archives mix volumes and chapters in one folder: no verdict.</summary>
    Mixed = 4,

    /// <summary>No archive name states a volume or chapter number.</summary>
    NoUnits = 5,
}

/// <summary>The totals a stored record states (any may be null).</summary>
public sealed record PublishedTotals(
    int? EnglishVolumes = null,
    int? EnglishChapters = null,
    int? OriginVolumes = null,
    int? OriginChapters = null,
    double? LatestChapter = null);

/// <summary>One unit kind of one series.</summary>
public sealed record MissingUnitGap(
    MissingUnitKind Kind,
    int ArchiveCount,
    int Lowest,
    int Have,
    int? Available,
    MissingTotalSource? Source,
    MissingConfidence? Confidence,
    int BehindBy,
    IReadOnlyList<int> Missing,
    int MissingCount);

public sealed record MissingUnitsResult(
    MissingVerdict Verdict,
    MissingUnitGap? Volumes,
    MissingUnitGap? Chapters,
    int MixedFolders);

public static partial class MissingUnits
{
    /// <summary>At most this many missing numbers are listed; <see cref="MissingUnitGap.MissingCount"/> has the rest.</summary>
    public const int MaxListed = 50;

    // Only a hole count below this is listed number by number: a unit number of 5000 is a typo, not 4999 holes.
    private const int MaxNumber = 3000;

    // The low end of a range archive ("Vol. 01-05", "c010-012"); the helpers give the high end.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:v|vol|vols|volume|volumes|ch|chap|chapter|chapters|c)?\.?\s*(?<n>\d{1,4})(?:\.\d+)?\s*-\s*(?:v|vol|ch|c)?\.?\s*(?<m>\d{1,4})(?:\.\d+)?(?![\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Range();

    /// <summary>
    /// The report for one series. <paramref name="folders"/> holds the archive names of each folder that belongs to
    /// it (the linked folder itself and its Volumes / Chapters subfolders), one list per folder: a folder whose
    /// archives are both volume-like and chapter-like is mixed and gives no numbers.
    /// </summary>
    public static MissingUnitsResult Evaluate(IEnumerable<IReadOnlyList<string>> folders, PublishedTotals totals)
    {
        var volumes = new SortedSet<int>();
        var chapters = new SortedSet<int>();
        int volumeArchives = 0, chapterArchives = 0, mixed = 0;
        foreach (var names in folders)
        {
            var v = names.Where(AutoMatchText.IsVolumeLike).ToList();
            var c = names.Where(n => !AutoMatchText.IsVolumeLike(n) && AutoMatchText.IsChapterLike(n)).ToList();
            if (v.Count > 0 && c.Count > 0)
            {
                mixed++;
                continue;
            }
            foreach (var name in v)
                if (AutoMatchText.VolumeNumberOf(name) is { } n)
                {
                    volumeArchives++;
                    AddUnits(volumes, name, n);
                }
            foreach (var name in c)
                if (AutoMatchText.ChapterNumberOf(name) is { } n)
                {
                    chapterArchives++;
                    AddUnits(chapters, name, n);
                }
        }

        var volumeGap = volumes.Count == 0 ? null
            : Gap(MissingUnitKind.Volume, volumeArchives, volumes, firstExpected: 1, VolumeTotals(totals));
        // Chapters next to volumes continue after the last volume: holes count from the lowest chapter on disk.
        var chapterGap = chapters.Count == 0 ? null
            : Gap(MissingUnitKind.Chapter, chapterArchives, chapters, firstExpected: volumes.Count > 0 ? chapters.Min : 1, ChapterTotals(totals));

        var gaps = new[] { volumeGap, chapterGap }.OfType<MissingUnitGap>().ToList();
        MissingVerdict verdict;
        if (gaps.Count == 0)
            verdict = mixed > 0 ? MissingVerdict.Mixed : MissingVerdict.NoUnits;
        else if (gaps.Any(g => g.BehindBy > 0))
            verdict = MissingVerdict.Behind;
        else if (gaps.Any(g => g.MissingCount > 0))
            verdict = MissingVerdict.Holes;
        else if (gaps.All(g => g.Available is null))
            verdict = MissingVerdict.NoTotal;
        else
            verdict = MissingVerdict.UpToDate;
        return new MissingUnitsResult(verdict, volumeGap, chapterGap, mixed);
    }

    private static void AddUnits(SortedSet<int> set, string name, int high)
    {
        var low = high;
        foreach (Match m in Range().Matches(name))
        {
            if (int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) != high)
                continue;
            var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            if (n < high && high - n <= MaxNumber)
                low = n;
        }
        for (var i = Math.Max(0, low); i <= Math.Min(high, MaxNumber); i++)
            set.Add(i);
        if (high > MaxNumber)
            set.Add(high);
    }

    private static IEnumerable<(int Total, MissingTotalSource Source, MissingConfidence Confidence)> VolumeTotals(PublishedTotals t)
    {
        if (t.EnglishVolumes is { } e && e > 0)
            yield return (e, MissingTotalSource.English, MissingConfidence.High);
        if (t.OriginVolumes is { } o && o > 0)
            yield return (o, MissingTotalSource.Origin, MissingConfidence.Medium);
    }

    private static IEnumerable<(int Total, MissingTotalSource Source, MissingConfidence Confidence)> ChapterTotals(PublishedTotals t)
    {
        if (t.EnglishChapters is { } e && e > 0)
            yield return (e, MissingTotalSource.English, MissingConfidence.High);
        if (t.OriginChapters is { } o && o > 0)
            yield return (o, MissingTotalSource.Origin, MissingConfidence.Medium);
        if (t.LatestChapter is { } l && l >= 1)
            yield return ((int)Math.Floor(l), MissingTotalSource.LatestChapter, MissingConfidence.Low);
    }

    private static MissingUnitGap Gap(
        MissingUnitKind kind, int archives, SortedSet<int> numbers, int firstExpected,
        IEnumerable<(int Total, MissingTotalSource Source, MissingConfidence Confidence)> totals)
    {
        var have = numbers.Max;
        // English first; a source whose total is below what is on disk (copies numbered like another edition,
        // an English release still catching up) gives way to the next one that covers it. None does: the first
        // source is kept and the series counts as up to date against it.
        var list = totals.ToList();
        (int Total, MissingTotalSource Source, MissingConfidence Confidence)? pick =
            list.Count == 0 ? null : list.FirstOrDefault(x => x.Total >= have) is { Total: > 0 } covering ? covering : list[0];

        var missing = new List<int>();
        var missingCount = 0;
        for (var i = Math.Max(1, firstExpected); i < have && i <= MaxNumber; i++)
        {
            if (numbers.Contains(i))
                continue;
            missingCount++;
            if (missing.Count < MaxListed)
                missing.Add(i);
        }
        return new MissingUnitGap(
            kind,
            archives,
            numbers.Min,
            have,
            pick?.Total,
            pick?.Source,
            pick?.Confidence,
            pick is { } p ? Math.Max(0, p.Total - have) : 0,
            missing,
            missingCount);
    }
}
