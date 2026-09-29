namespace com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

using System.Globalization;

/// <summary>What one unit kind of the count rule says about a record.</summary>
public enum CountSignal
{
    /// <summary>Nothing to compare: no local number, no published total for that unit, or a mixed folder.</summary>
    None = 0,

    /// <summary>The local number fits the published total.</summary>
    Agree = 1,

    /// <summary>The local number is far above the published total (<see cref="CountEvidence.Factor"/>, <see cref="CountEvidence.Slack"/>).</summary>
    Conflict = 2,
}

/// <summary>
/// The local side of the count rule: how many archives name volumes / chapters and the unit NUMBERS they state (the
/// highest, and the lowest for wording). Numbers, never archive counts: twelve archives that are volumes 1-6 and six
/// <c>x.5</c> extras are volume 6. <see cref="IsMixed"/>: volumes next to chapters give no signal at all.
/// </summary>
public sealed record LocalUnitCounts(
    int VolumeArchives,
    int ChapterArchives,
    int? LowestVolume,
    int? HighestVolume,
    int? LowestChapter,
    int? HighestChapter,
    // 1.29.0 (owner): the highest volume that CHAPTER archive names state ("Title v09 c060") - a chapter folder also says how
    // many volumes the run has reached, compared with volume totals only (never makes the folder "mixed").
    int? HighestNamedVolume = null)
{
    public static LocalUnitCounts Empty { get; } = new(0, 0, null, null, null, null);

    public bool IsMixed => VolumeArchives > 0 && ChapterArchives > 0;
}

/// <summary>
/// The published side (public provider data): the record's volume total (status in the country of origin), the
/// chapter total its status line states, the English publisher's totals, and the latest tracked chapter.
/// </summary>
public sealed record PublishedUnitCounts(
    int? Volumes,
    int? EnglishVolumes,
    int? StatusChapters,
    int? EnglishChapters,
    int? LatestChapter)
{
    public static PublishedUnitCounts Of(MatchCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return new(candidate.Volumes, candidate.EnglishVolumes, candidate.TotalChapters, candidate.EnglishChapters, candidate.LatestChapter);
    }
}

/// <summary>The rule's answer per unit, with the published number each signal was compared with.</summary>
public sealed record CountComparison(
    CountSignal Volumes,
    CountSignal Chapters,
    int? PublishedVolumes,
    int? PublishedChapters)
{
    public static CountComparison None { get; } = new(CountSignal.None, CountSignal.None, null, null);

    public bool IsConflict => Volumes == CountSignal.Conflict || Chapters == CountSignal.Conflict;
}

/// <summary>
/// The count rule (extracted from the matcher's scorer in 1.29.0 so Identify uses the same rule). Pure.
/// <list type="bullet">
/// <item>Volumes are compared only with volume totals, chapters only with chapter totals - never one with the other. A
/// folder that mixes volume and chapter archives gives no signal.</item>
/// <item>The local side is the highest unit NUMBER the archive names state, in the folder and in its unit subfolders
/// (<see cref="LocalOf"/>) - never an archive count.</item>
/// <item>The published side is the largest number any source states: volumes = origin or English volumes; chapters = the
/// status line's total, the English chapters, or the latest tracked chapter (it restarts per season on renumbered webtoons,
/// so the larger one counts).</item>
/// <item>The latest tracked chapter follows scanlation releases, not the run: when the record counts its run in volumes and
/// states no chapter total, it may lag far behind (scanlations stop at a licence) and never makes a conflict - a chapter
/// folder of a record with 10 volumes has nothing to be compared with.</item>
/// <item>Conflict: local &gt; <see cref="Factor"/> x published + <see cref="Slack"/>; otherwise agreement.</item>
/// </list>
/// </summary>
public static class CountEvidence
{
    /// <summary>A count conflict: the local unit number &gt; <c>Factor x published + Slack</c>.</summary>
    public const double Factor = 1.5;

    public const int Slack = 2;

    /// <summary>Unit subfolders whose archives are not the numbered run (they never add a number).</summary>
    private static readonly string[] s_sideWords = ["extra", "special", "side", "one", "oneshot", "bonus", "omake", "raw", "color", "colour"];

    /// <summary>
    /// True for a unit subfolder that holds side material, not the numbered run: Extras, Specials, Side Stories, Oneshots,
    /// Bonus, Omake, Raws, Colored.
    /// </summary>
    public static bool IsSideFolderName(string? name) =>
        AutoMatchText.IsUnitFolderName(name)
        && name!.TrimStart('[', '(', ' ') is var s && s_sideWords.Any(w => s.StartsWith(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The local side of a folder: its own archive names, plus its unit subfolders (<see cref="AutoMatchText.IsUnitFolderName"/>;
    /// not side folders). A <c>Volumes</c> / <c>Vol 1-5</c> subfolder counts its archives as volumes and a <c>Chapters</c> one as
    /// chapters; other unit subfolders (<c>Season 2</c>, <c>Part 3</c>, <c>12</c>) are read by their archive names. Numbers come from
    /// <see cref="ChildFolderShape.ArchiveNames"/>; a subfolder without them adds only the range its own name states
    /// (<c>Volumes 3-5</c>), never its archive count.
    /// </summary>
    public static LocalUnitCounts LocalOf(IReadOnlyList<string> archiveNames, IEnumerable<ChildFolderShape>? subfolders)
    {
        ArgumentNullException.ThrowIfNull(archiveNames);
        var acc = new Accumulator();
        foreach (var name in archiveNames)
            acc.AddLoose(name);
        foreach (var sub in subfolders ?? [])
        {
            if (sub.DescendantArchiveCount <= 0 || !AutoMatchText.IsUnitFolderName(sub.DisplayName) || IsSideFolderName(sub.DisplayName))
                continue;
            var names = sub.ArchiveNames ?? [];
            if (AutoMatchText.IsVolumeFolderName(sub.DisplayName))
            {
                acc.VolumeArchives += sub.DescendantArchiveCount;
                foreach (var n in names)
                    acc.Volume(AutoMatchText.VolumeNumberOf(n) ?? AutoMatchText.BareNumberOf(n), AutoMatchText.UnitsOf(n) is { IsExtra: false } u ? u.Volume ?? u.Chapter : null);
                if (names.Count == 0)
                    acc.Volume(FolderRangeEnd(sub.DisplayName), AutoMatchText.UnitsOf(sub.DisplayName).Volume);
            }
            else if (AutoMatchText.IsChapterFolderName(sub.DisplayName))
            {
                acc.ChapterArchives += sub.DescendantArchiveCount;
                foreach (var n in names)
                {
                    acc.Chapter(AutoMatchText.ChapterNumberOf(n) ?? AutoMatchText.BareNumberOf(n), AutoMatchText.UnitsOf(n) is { IsExtra: false } u ? u.Chapter : null);
                    acc.NamedVolume(n);
                }
                if (names.Count == 0)
                    acc.Chapter(FolderRangeEnd(sub.DisplayName), AutoMatchText.UnitsOf(sub.DisplayName).Chapter);
            }
            else
            {
                foreach (var n in names)
                    acc.AddLoose(n);
            }
        }
        return acc.Result();
    }

    /// <summary>
    /// The local side a context carries: <see cref="MatchContext.Units"/> when the planner set it, otherwise the older fields
    /// (the highest numbers, or the archive counts when no number is known).
    /// </summary>
    public static LocalUnitCounts FromContext(MatchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Units is { } units)
            return units;
        var volumes = context.VolumeLikeCount > 0 ? context.LocalVolumes ?? context.VolumeLikeCount : (int?)null;
        var chapters = context.ChapterLikeCount > 0 ? context.LocalChapters ?? context.ChapterLikeCount : (int?)null;
        return new LocalUnitCounts(context.VolumeLikeCount, context.ChapterLikeCount, null, volumes, null, chapters);
    }

    /// <summary>Compares the local unit numbers with a record's published totals (see the type's rules).</summary>
    public static CountComparison Compare(LocalUnitCounts local, PublishedUnitCounts published)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(published);
        if (local.IsMixed)
            return CountComparison.None;

        var volumeTotal = Math.Max(published.Volumes ?? 0, published.EnglishVolumes ?? 0);
        var chapterTotal = Math.Max(published.StatusChapters ?? 0, published.EnglishChapters ?? 0);
        var latest = published.LatestChapter ?? 0;
        var chapterBound = Math.Max(chapterTotal, latest);
        // Only the latest tracked chapter, and the record counts its run in volumes: agreement at most.
        var latestOnly = chapterTotal == 0 && volumeTotal > 0;

        var volumes = CountSignal.None;
        if (local.VolumeArchives > 0 && local.HighestVolume is > 0 and var lv && volumeTotal > 0)
            volumes = Exceeds(lv, volumeTotal) ? CountSignal.Conflict : CountSignal.Agree;
        // A chapter folder whose names state their volume ("v09 c060", 1.29.0): the volume number is compared with the volume
        // totals, like volume archives are.
        else if (local.VolumeArchives == 0 && local.HighestNamedVolume is > 0 and var nv && volumeTotal > 0)
            volumes = Exceeds(nv, volumeTotal) ? CountSignal.Conflict : CountSignal.Agree;
        var chapters = CountSignal.None;
        if (local.ChapterArchives > 0 && local.HighestChapter is > 0 and var lc && chapterBound > 0)
            chapters = !Exceeds(lc, chapterBound) ? CountSignal.Agree : latestOnly ? CountSignal.None : CountSignal.Conflict;
        return new CountComparison(
            volumes,
            chapters,
            volumeTotal > 0 ? volumeTotal : null,
            chapterBound > 0 ? chapterBound : null);
    }

    /// <summary>True when the local number is far above the published one.</summary>
    public static bool Exceeds(int local, int published) => local > Factor * published + Slack;

    /// <summary>
    /// "volumes 1-43", "volume 7", "chapters 12-80" - the local numbers of one unit for a sentence, or null when unknown.
    /// </summary>
    public static string? Describe(LocalUnitCounts local, bool volumes)
    {
        ArgumentNullException.ThrowIfNull(local);
        var (low, high, word) = volumes ? (local.LowestVolume, local.HighestVolume, "volume") : (local.LowestChapter, local.HighestChapter, "chapter");
        // A chapter folder whose names state their volume (1.29.0): "chapters up to volume 9".
        if (high is null && volumes && local.HighestNamedVolume is { } named)
            return string.Create(CultureInfo.InvariantCulture, $"chapters up to volume {named}");
        if (high is not { } h)
            return null;
        return low is { } l && l < h
            ? string.Create(CultureInfo.InvariantCulture, $"{word}s {l}-{h}")
            : string.Create(CultureInfo.InvariantCulture, $"{word} {h}");
    }

    private static int? FolderRangeEnd(string name)
    {
        var u = AutoMatchText.UnitsOf(name);
        var end = u.VolumeEnd ?? u.Volume ?? u.ChapterEnd ?? u.Chapter;
        return end is { } e ? (int)decimal.Floor(e) : null;
    }

    private sealed class Accumulator
    {
        public int VolumeArchives;
        public int ChapterArchives;
        private int? _lowVolume, _highVolume, _lowChapter, _highChapter, _highNamedVolume;

        public void AddLoose(string name)
        {
            if (AutoMatchText.IsVolumeLike(name))
            {
                VolumeArchives++;
                Volume(AutoMatchText.VolumeNumberOf(name), AutoMatchText.UnitsOf(name) is { IsExtra: false } u ? u.Volume : null);
            }
            else if (AutoMatchText.IsChapterLike(name))
            {
                ChapterArchives++;
                Chapter(AutoMatchText.ChapterNumberOf(name), AutoMatchText.UnitsOf(name) is { IsExtra: false } u ? u.Chapter : null);
                NamedVolume(name);
            }
        }

        /// <summary>The volume a chapter archive's name states (<c>Title v09 c060</c> -> 9), extras and ranges' top included.</summary>
        public void NamedVolume(string name)
        {
            var units = AutoMatchText.UnitsOf(name);
            if ((units.VolumeEnd ?? units.Volume) is { } v && v >= 1)
                _highNamedVolume = Math.Max(_highNamedVolume ?? 0, (int)decimal.Floor(v));
        }

        // high: the matcher's integer (unchanged 1.27.0 helpers); low: the start a name states, for wording only.
        public void Volume(int? high, decimal? low) => Add(ref _lowVolume, ref _highVolume, high, low);

        public void Chapter(int? high, decimal? low) => Add(ref _lowChapter, ref _highChapter, high, low);

        private static void Add(ref int? lowest, ref int? highest, int? high, decimal? low)
        {
            if (high is not { } h)
                return;
            highest = Math.Max(highest ?? h, h);
            var l = low is { } d ? Math.Min((int)decimal.Floor(d), h) : h;
            lowest = Math.Min(lowest ?? l, l);
        }

        public LocalUnitCounts Result() => new(VolumeArchives, ChapterArchives, _lowVolume, _highVolume, _lowChapter, _highChapter, _highNamedVolume);
    }
}
