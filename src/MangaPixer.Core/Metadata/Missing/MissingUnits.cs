namespace com.lifepixer.mangapixer.Core.Metadata.Missing;

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

    /// <summary>
    /// 1.28.0: the other unit's total converted with a chapters-per-volume ratio (a finished AniList entry's
    /// chapters / volumes): an estimate.
    /// </summary>
    Converted = 3,

    /// <summary>
    /// 1.29.0 RC: the chapters released in the preferred language (the series' volume list filtered by that language; chapters
    /// only).
    /// </summary>
    Released = 4,
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

    /// <summary>No archive name states a volume or chapter number (a lone chapter / volume 0, a prologue, is not one).</summary>
    NoUnits = 5,

    /// <summary>
    /// 1.29.0: the numbering starts again (or repeats) in another subfolder - <c>Season 1</c> / <c>Season 2</c> both from
    /// chapter 1 - so the numbers on disk cannot be compared with one total: no verdict.
    /// </summary>
    Restarts = 6,
}

/// <summary>
/// The totals a stored record states (any may be null), plus an optional chapters-per-volume ratio that converts a
/// total of one unit into the other when the record states none of the same unit.
/// </summary>
/// <para>
/// 1.29.0 RC ("missing" = released in the PREFERRED language): with <paramref name="Language"/> set, only what is released in
/// that language is compared - the English totals and the latest release only for English, <paramref name="ReleasedChapters"/>
/// (the highest chapter the volume list names as released in that language) for any language; the origin totals never make a
/// series "behind" (an untranslated volume is not missing) and are kept as context (<see cref="MissingUnitGap.OriginTotal"/>).
/// Without a language every total counts (the 1.28.0 behaviour).
/// </para>
public sealed record PublishedTotals(
    int? EnglishVolumes = null,
    int? EnglishChapters = null,
    int? OriginVolumes = null,
    int? OriginChapters = null,
    double? LatestChapter = null,
    double? ChaptersPerVolume = null,
    string? Language = null,
    int? ReleasedChapters = null)
{
    /// <summary>The language rule applies and the language is English (the only one with publisher totals today).</summary>
    internal bool English => Language is null || string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One unit kind of one series.</summary>
public sealed record MissingUnitGap(
    MissingUnitKind Kind,
    int ArchiveCount,
    int UnitCount,
    int Lowest,
    int Have,
    int? Available,
    MissingTotalSource? Source,
    MissingConfidence? Confidence,
    int BehindBy,
    IReadOnlyList<int> Missing,
    int MissingCount,
    int? OriginTotal = null);

public sealed record MissingUnitsResult(
    MissingVerdict Verdict,
    MissingUnitGap? Volumes,
    MissingUnitGap? Chapters,
    int MixedFolders);

/// <summary>
/// The archive names of one folder of a series (1.29.0). <c>Name</c> is the folder's display name when it is a unit
/// subfolder: in a <c>Volumes</c> folder a bare <c>01.cbz</c> is volume 1 (elsewhere a bare number is a chapter).
/// </summary>
public sealed record MissingFolder(string? Name, IReadOnlyList<string> ArchiveNames);

/// <summary>
/// The split chapters on disk (<see cref="MissingUnits.SplitsOf"/>): the part numbers that belong to one (never extras), the
/// chapters they make up, and the parts missing between the parts here (ascending).
/// </summary>
public sealed record SplitChapters(IReadOnlySet<decimal> Parts, IReadOnlySet<int> Chapters, IReadOnlyList<decimal> MissingParts);

public static class MissingUnits
{
    /// <summary>At most this many missing numbers are listed; <see cref="MissingUnitGap.MissingCount"/> has the rest.</summary>
    public const int MaxListed = 50;

    /// <summary>Only numbers up to this are expanded one by one: a unit number of 5000 is a typo, not 4999 holes.</summary>
    public const int MaxNumber = 3000;

    /// <summary>
    /// The report for one series. <paramref name="folders"/> holds the archive names of each folder that belongs to
    /// it (the linked folder itself and its unit subfolders), one list per folder: a folder whose archives are both
    /// volume-like and chapter-like is mixed and gives no numbers.
    /// </summary>
    public static MissingUnitsResult Evaluate(IEnumerable<IReadOnlyList<string>> folders, PublishedTotals totals) =>
        Evaluate(folders.Select(f => new MissingFolder(null, f)), totals);

    /// <summary>
    /// The report for one series, one <see cref="MissingFolder"/> per folder of it. Unit numbers come from
    /// <see cref="AutoMatchText.UnitsOf"/> (1.29.0): a range archive covers its range, an extra (<c>c045.5</c>) is never
    /// missing and never fills a number, a lone chapter / volume 0 (a prologue) is not progress. When numbering restarts
    /// or repeats across folders (<see cref="MissingVerdict.Restarts"/>) that unit gets no numbers and the series no verdict.
    /// </summary>
    public static MissingUnitsResult Evaluate(IEnumerable<MissingFolder> folders, PublishedTotals totals)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(totals);
        var volumeSets = new List<SortedSet<int>>();
        var chapterSets = new List<SortedSet<int>>();
        int volumeArchives = 0, chapterArchives = 0, mixed = 0;
        foreach (var folder in folders)
        {
            var volumeFolder = AutoMatchText.IsVolumeFolderName(folder.Name);
            var units = folder.ArchiveNames.Select(n => UnitsIn(n, volumeFolder)).Where(u => !u.IsEmpty).ToList();
            var v = units.Where(u => u.Chapter is null).ToList();
            var c = units.Where(u => u.Chapter is not null).ToList();
            if (v.Count > 0 && c.Count > 0)
            {
                mixed++;
                continue;
            }
            volumeArchives += v.Count;
            chapterArchives += c.Count;
            if (v.Count > 0)
                volumeSets.Add(NumbersOf(v, MissingUnitKind.Volume));
            if (c.Count > 0)
                chapterSets.Add(NumbersOf(c, MissingUnitKind.Chapter));
        }

        var volumesRestart = Restarts(volumeSets);
        var chaptersRestart = Restarts(chapterSets);
        var volumes = volumesRestart ? new SortedSet<int>() : Union(volumeSets);
        var chapters = chaptersRestart ? new SortedSet<int>() : Union(chapterSets);

        // With the language rule the origin totals are context only (they never make a series "behind").
        var volumeOrigin = totals.Language is null ? null : totals.OriginVolumes;
        var chapterOrigin = totals.Language is null ? null : totals.OriginChapters;
        var volumeGap = !HasProgress(volumes) ? null
            : Gap(MissingUnitKind.Volume, volumeArchives, volumes, firstExpected: 1, VolumeTotals(totals), volumeOrigin);
        // Chapters next to volumes continue after the last volume: holes count from the lowest chapter on disk.
        var chapterGap = !HasProgress(chapters) ? null
            : Gap(MissingUnitKind.Chapter, chapterArchives, chapters, firstExpected: volumeGap is not null ? chapters.Min : 1, ChapterTotals(totals),
                chapterOrigin);

        var gaps = new[] { volumeGap, chapterGap }.OfType<MissingUnitGap>().ToList();
        MissingVerdict verdict;
        if (volumesRestart || chaptersRestart)
            verdict = MissingVerdict.Restarts;
        else if (gaps.Count == 0)
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

    /// <summary>
    /// The whole numbers of one unit kind the archives cover (1.29.0, public for the virtual-volume stacks): a range covers
    /// every number in it (<c>Vol. 01-05</c> -> 1..5), an extra (<see cref="UnitNumbers.IsExtra"/>, <c>c045.5</c>) covers none.
    /// Chapters: the <see cref="UnitNumbers.Chapter"/> of every name that states one, plus each split chapter whose parts are
    /// here (<see cref="SplitsOf"/>, 1.29.1); volumes: the <see cref="UnitNumbers.Volume"/> of names that state no chapter.
    /// </summary>
    public static SortedSet<int> NumbersOf(IEnumerable<UnitNumbers> units, MissingUnitKind kind)
    {
        ArgumentNullException.ThrowIfNull(units);
        var list = units as IReadOnlyCollection<UnitNumbers> ?? units.ToList();
        var set = FileNumbersOf(list, kind);
        // A split chapter's parts on disk (2.1 + 2.2) make up chapter 2 (1.29.1).
        if (kind == MissingUnitKind.Chapter)
            set.UnionWith(SplitsOf(list).Chapters);
        return set;
    }

    /// <summary>
    /// The whole numbers the archives cover by their own number (a range covers its range): <see cref="NumbersOf"/> without
    /// the split chapters. The stacks use it where a listed part is covered only by its whole chapter's file.
    /// </summary>
    public static SortedSet<int> FileNumbersOf(IEnumerable<UnitNumbers> units, MissingUnitKind kind)
    {
        ArgumentNullException.ThrowIfNull(units);
        var set = new SortedSet<int>();
        foreach (var u in units)
        {
            if (u.IsExtra)
                continue;
            var (start, end) = kind == MissingUnitKind.Chapter ? (u.Chapter, u.ChapterEnd)
                : u.Chapter is null ? (u.Volume, u.VolumeEnd) : ((decimal?)null, (decimal?)null);
            if (start is not { } a)
                continue;
            var low = (int)decimal.Ceiling(a);
            var high = end is { } b && b - a <= MaxNumber ? (int)decimal.Floor(b) : low;
            for (var i = Math.Max(0, low); i <= Math.Min(high, MaxNumber); i++)
                set.Add(i);
            if (high > MaxNumber)
                set.Add(high);
        }
        return set;
    }

    /// <summary>
    /// The split chapters among chapter archives (1.29.1, owner soak test): files numbered as parts of chapter N (N.1, N.2, ...)
    /// are chapter N, also where a provider's list names only the plain N. A part is a one-decimal number; N.5 is the usual
    /// number of an extra (10.5), so it is a part only after N.4. The parts must start at the beginning (1.30.0): at N.1, or at N.2
    /// when a file N is here (that file is the first part; a file N.1 next to it stays an extra) - a run that starts later (N.6
    /// next to N and N.5, listed extras) is extras, never a split with its first parts missing. A part missing below the highest
    /// part here is in
    /// <see cref="SplitChapters.MissingParts"/> (4.1 and 4.3 here: 4.2 is missing).
    /// </summary>
    public static SplitChapters SplitsOf(IEnumerable<UnitNumbers> units)
    {
        ArgumentNullException.ThrowIfNull(units);
        var list = units as IReadOnlyCollection<UnitNumbers> ?? units.ToList();
        var wholes = FileNumbersOf(list, MissingUnitKind.Chapter);
        var tenthsByChapter = new SortedDictionary<int, SortedSet<int>>();
        foreach (var u in list)
        {
            if (u.Chapter is not { } c || u.ChapterEnd is not null || c <= 0 || c > MaxNumber)
                continue;
            var tenths = (c - decimal.Truncate(c)) * 10;
            if (tenths == 0 || decimal.Truncate(tenths) != tenths)
                continue; // a whole chapter, or 12.25 / 12.75: never a part
            var n = (int)decimal.Truncate(c);
            if (!tenthsByChapter.TryGetValue(n, out var set))
                tenthsByChapter[n] = set = [];
            set.Add((int)tenths);
        }

        var parts = new HashSet<decimal>();
        var chapters = new SortedSet<int>();
        var missing = new List<decimal>();
        foreach (var (n, tenths) in tenthsByChapter)
        {
            var hasFile = wholes.Contains(n);
            var own = tenths.Where(t => (t != 5 || tenths.Contains(4)) && (!hasFile || t >= 2)).ToList();
            if (own.Count == 0 || own.Min() != (hasFile ? 2 : 1))
                continue;
            chapters.Add(n);
            var present = own.ToHashSet();
            if (hasFile)
                present.Add(1);
            foreach (var t in own)
                parts.Add(n + t / 10m);
            for (var t = 1; t < own.Max(); t++)
            {
                if (!present.Contains(t))
                    missing.Add(n + t / 10m);
            }
        }
        return new SplitChapters(parts, chapters, missing);
    }

    /// <summary>
    /// The whole numbers from <paramref name="from"/> to <paramref name="to"/> (inclusive) that no archive covers (1.29.0,
    /// public so a virtual volume can show its missing chapters: <c>Holes(units, Chapter, 1, 10)</c> -> <c>[8]</c>). Extras
    /// never fill a number and are never missing; at most <see cref="MaxNumber"/> numbers are checked.
    /// </summary>
    public static IReadOnlyList<int> Holes(IEnumerable<UnitNumbers> units, MissingUnitKind kind, int from, int to)
    {
        var have = NumbersOf(units, kind);
        var result = new List<int>();
        for (var i = Math.Max(0, from); i <= to && i <= MaxNumber; i++)
            if (!have.Contains(i))
                result.Add(i);
        return result;
    }

    /// <summary>
    /// The expected numbers no archive covers (1.29.0): <paramref name="expected"/> is a known unit list (a volume's chapters
    /// from a mapping); a fractional expected number is an extra and is never missing.
    /// </summary>
    public static IReadOnlyList<int> Holes(IEnumerable<UnitNumbers> units, MissingUnitKind kind, IEnumerable<decimal> expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var have = NumbersOf(units, kind);
        return expected.Where(e => e >= 0 && decimal.Truncate(e) == e).Select(e => (int)e).Distinct().Order()
            .Where(e => !have.Contains(e)).ToList();
    }

    // In a Volumes folder a bare "01.cbz" is volume 1; elsewhere UnitsOf reads a bare number as a chapter.
    private static UnitNumbers UnitsIn(string name, bool volumeFolder)
    {
        var u = AutoMatchText.UnitsOf(name);
        return volumeFolder && u.Volume is null && u.Chapter is not null && AutoMatchText.BareNumberOf(name) is not null
            ? new UnitNumbers(u.Chapter, u.ChapterEnd, null, null, u.IsExtra)
            : u;
    }

    // A lone 0 (a prologue, "000.cbz") is not progress: never "chapter 0 of 223".
    private static bool HasProgress(SortedSet<int> numbers) => numbers.Count > 0 && numbers.Max > 0;

    private static SortedSet<int> Union(IEnumerable<SortedSet<int>> sets)
    {
        var all = new SortedSet<int>();
        foreach (var set in sets)
            all.UnionWith(set);
        return all;
    }

    /// <summary>
    /// Numbering restarts or repeats across folders: two folders share two or more numbers, or start at the same number
    /// (<c>Season 1</c> and <c>Season 2</c> both from 1). 0 (a prologue) is ignored; one shared number between folders that
    /// start apart (a duplicate at a boundary) is not a restart.
    /// </summary>
    private static bool Restarts(List<SortedSet<int>> sets)
    {
        var numbered = sets.Select(s => s.Where(n => n > 0).ToHashSet()).Where(s => s.Count > 0).ToList();
        for (var i = 0; i < numbered.Count; i++)
        {
            for (var j = i + 1; j < numbered.Count; j++)
            {
                var shared = numbered[i].Count(numbered[j].Contains);
                if (shared >= 2 || (shared >= 1 && numbered[i].Min() == numbered[j].Min()))
                    return true;
            }
        }
        return false;
    }

    // Volumes: English volumes, English chapters converted, origin volumes, origin chapters converted. With the language rule:
    // the English ones only, and only for English.
    private static IEnumerable<(int Total, MissingTotalSource Source, MissingConfidence Confidence)> VolumeTotals(PublishedTotals t)
    {
        var ratio = t.ChaptersPerVolume is { } r && r >= 1 ? r : (double?)null;
        if (!t.English)
            yield break;
        if (t.EnglishVolumes is { } e && e > 0)
            yield return (e, MissingTotalSource.English, MissingConfidence.High);
        if (ratio is { } r1 && t.EnglishChapters is { } ec && (int)Math.Floor(ec / r1) is var cv && cv > 0)
            yield return (cv, MissingTotalSource.Converted, MissingConfidence.Medium);
        if (t.Language is not null)
            yield break;
        if (t.OriginVolumes is { } o && o > 0)
            yield return (o, MissingTotalSource.Origin, MissingConfidence.Medium);
        if (ratio is { } r2 && t.OriginChapters is { } oc && (int)Math.Floor(oc / r2) is var ov && ov > 0)
            yield return (ov, MissingTotalSource.Converted, MissingConfidence.Low);
    }

    // Chapters: English chapters, English volumes converted, origin chapters, the latest chapter. With the language rule: the
    // English ones (English only), the chapters released in the language, the latest release (English only) - never the origin.
    private static IEnumerable<(int Total, MissingTotalSource Source, MissingConfidence Confidence)> ChapterTotals(PublishedTotals t)
    {
        var ratio = t.ChaptersPerVolume is { } r && r >= 1 ? r : (double?)null;
        if (t.English && t.EnglishChapters is { } e && e > 0)
            yield return (e, MissingTotalSource.English, MissingConfidence.High);
        if (t.English && ratio is { } r1 && t.EnglishVolumes is { } ev && (int)Math.Floor(ev * r1) is var ec && ec > 0)
            yield return (ec, MissingTotalSource.Converted, MissingConfidence.Medium);
        if (t.Language is not null)
        {
            if (t.ReleasedChapters is { } rc && rc > 0)
                yield return (rc, MissingTotalSource.Released, MissingConfidence.Medium);
            if (t.English && t.LatestChapter is { } latest && latest >= 1)
                yield return ((int)Math.Floor(latest), MissingTotalSource.LatestChapter, MissingConfidence.Low);
            yield break;
        }
        if (t.OriginChapters is { } o && o > 0)
            yield return (o, MissingTotalSource.Origin, MissingConfidence.Medium);
        if (t.LatestChapter is { } l && l >= 1)
            yield return ((int)Math.Floor(l), MissingTotalSource.LatestChapter, MissingConfidence.Low);
    }

    private static MissingUnitGap Gap(
        MissingUnitKind kind, int archives, SortedSet<int> numbers, int firstExpected,
        IEnumerable<(int Total, MissingTotalSource Source, MissingConfidence Confidence)> totals, int? originTotal)
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
            numbers.Count,
            numbers.Min,
            have,
            pick?.Total,
            pick?.Source,
            pick?.Confidence,
            pick is { } p ? Math.Max(0, p.Total - have) : 0,
            missing,
            missingCount,
            originTotal);
    }
}
