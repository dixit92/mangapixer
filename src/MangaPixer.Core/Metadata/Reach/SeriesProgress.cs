namespace com.lifepixer.mangapixer.Core.Metadata.Reach;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.Missing;

// A linked series' progress (1.30.0): the reach of its folder compared, per kind, with what the stored record says is released in
// the preferred language - official volumes, released chapters - plus the upgrades (official volumes held only as chapters) and the
// completion mark. PURE: the Volumes view, the Missing report and the Official releases tab all call it, so they agree.

/// <summary>
/// The stored facts of one linked record the comparison reads (any may be null). <see cref="Language"/> is the preferred
/// language; the English-only facts (<see cref="LatestChapter"/>, <see cref="ScanlationComplete"/>, <see cref="Licensed"/> and the
/// official publisher totals, which come from MangaUpdates' English publishers) are set by the caller only for English.
/// </summary>
public sealed record ProgressFacts(
    string Language,
    MetadataOrigin? Origin = null,
    MetadataOriginStatus? OriginStatus = null,
    int? OriginVolumes = null,
    int? OriginChapters = null,
    string? OfficialPublisher = null,
    int? OfficialVolumes = null,
    int? OfficialChapters = null,
    MetadataOriginStatus? OfficialStatus = null,
    bool? Licensed = null,
    int? LatestChapter = null,
    bool? ScanlationComplete = null,
    IReadOnlySet<decimal>? ReleasedChapters = null,
    double? ChaptersPerVolume = null)
{
    /// <summary>The highest whole chapter the released list names, or null.</summary>
    public int? ReleasedChapter => ReleasedChapters is { Count: > 0 } r ? (int)decimal.Floor(r.Max()) : null;

    /// <summary>Something is known about what is released in the language ("up to date" can be said).</summary>
    public bool ReleaseKnown => OfficialVolumes is not null || OfficialChapters is not null || ReleasedChapters is not null || LatestChapter is not null;

    /// <summary>The origin run has ended (complete, or cancelled there).</summary>
    public bool OriginEnded => OriginStatus is MetadataOriginStatus.Complete or MetadataOriginStatus.Cancelled;
}

/// <summary>What <see cref="SeriesProgress.Evaluate"/> found.</summary>
public sealed record ProgressResult
{
    public required ProgressFacts Facts { get; init; }

    /// <summary>The reach; <see cref="ReachResult.Empty"/> when numbering restarts.</summary>
    public required ReachResult Reach { get; init; }

    /// <summary>Numbering restarts or repeats across subfolders: no reach, no verdict.</summary>
    public bool Restarts { get; init; }

    public IReadOnlyList<int> MissingVolumes { get; init; } = [];

    /// <summary>Missing volumes above the highest volume here (released after it).</summary>
    public int VolumesBehind { get; init; }

    /// <summary>Missing chapter units below the highest chapter here (holes; parts of split chapters included).</summary>
    public IReadOnlyList<decimal> ChapterHoles { get; init; } = [];

    /// <summary>Whole chapters released in the language after the highest chapter here.</summary>
    public int ChaptersBehind { get; init; }

    /// <summary>The released chapter total the chapters were compared with, and where it came from.</summary>
    public int? ChapterTotal { get; init; }
    public MissingTotalSource? ChapterTotalSource { get; init; }

    /// <summary>The released volume total (official volumes, else official chapters converted by a ratio), and its source.</summary>
    public int? VolumeTotal { get; init; }
    public MissingTotalSource? VolumeTotalSource { get; init; }

    public IReadOnlyList<int> UpgradeVolumes { get; init; } = [];

    public SeriesCompletion Completion { get; init; }
    public CompletionBasis? CompletionBasis { get; init; }
    public int? CompletionTarget { get; init; }
    public int? CompletionHeld { get; init; }
    public bool CompletionInChapters { get; init; }

    public int MissingChapterCount => ChapterHoles.Count + ChaptersBehind;
}

public static class SeriesProgress
{
    /// <summary>At most this many upgrade volumes are listed (<see cref="SeriesProgressDto.UpgradeCount"/> has the rest).</summary>
    public const int MaxListed = 50;

    /// <summary>
    /// The map the Volumes view and the comparison group with: the stored one, plus (1.30.0) the chapters of the official volumes
    /// <c>1..N</c> as released in the language - an official volume means its chapters are out in that language.
    /// </summary>
    public static VolumeMapInput WithOfficialChapters(VolumeMapInput map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.ReleasedVolumeCount is not { } n || n < 1 || !map.HasData)
            return map;
        var resolver = new VolumeResolver(map);
        var released = new HashSet<decimal>(map.ReleasedChapters ?? new HashSet<decimal>());
        var added = false;
        for (var v = 1; v <= n && v <= VolumeGrouping.MaxMissingVolumes; v++)
        {
            foreach (var unit in resolver.RequiredUnits(v) ?? [])
                added |= released.Add(unit);
        }
        return added ? map with { ReleasedChapters = released } : map;
    }

    /// <summary>
    /// Compares the series' archive rows (its linked folder and unit subfolders) with the stored map and facts. With
    /// <paramref name="restarts"/> (numbering restarts across subfolders) there is no reach and nothing is missing; the trackers stay.
    /// <para>
    /// Rules (owner, 1.29.0 / 1.30.0): "missing" means released in the preferred language; an official volume held only as chapters
    /// is an UPGRADE, never missing; chapters are compared only when the folder holds chapter files (a volumes-only folder is never
    /// "behind" a scanlation); origin totals are context only.
    /// </para>
    /// </summary>
    public static ProgressResult Evaluate(IReadOnlyList<GroupingRow> rows, VolumeMapInput? map, ProgressFacts facts, bool restarts = false)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(facts);
        if (restarts)
            return new ProgressResult { Facts = facts, Reach = ReachResult.Empty, Restarts = true };

        map ??= VolumeMapInput.Empty;
        map = map with
        {
            ReleasedVolumeCount = facts.OfficialVolumes,
            ReleasedChapters = facts.ReleasedChapters ?? map.ReleasedChapters,
            ReleasedLanguage = facts.Language,
        };
        map = WithOfficialChapters(map);
        var archives = rows.Where(r => r.Kind == GroupingRowKind.Archive).ToList();
        var reach = SeriesReach.Of(archives, map);
        if (!reach.HasNumbers)
            return new ProgressResult { Facts = facts, Reach = reach };

        var grouping = VolumeGrouping.Group(archives, map, markMissingVolumes: true);
        var present = reach.TouchedVolumes.ToList();
        var highestVolume = present.Count > 0 ? present.Max() : 0;
        var missingVolumes = grouping.MissingVolumeNumbers;
        var volumesBehind = missingVolumes.Count(v => v > highestVolume);

        // Chapters: only when chapter files are here.
        IReadOnlyList<decimal> holes = [];
        var behind = 0;
        int? total = null;
        MissingTotalSource? source = null;
        var hasChapterFiles = reach.ChapterFiles.Any(n => n > 0);
        if (hasChapterFiles)
        {
            var top = reach.ReachChapter ?? 0;
            holes = grouping.MissingChapterUnits.Where(u => u < top).ToList();
            var alreadyMissing = grouping.MissingChapterUnits.Where(u => decimal.Truncate(u) == u).Select(u => (int)u).ToHashSet();
            (total, source) = ChapterTotal(facts, top);
            if (total is { } t && t > top)
            {
                // Released after the highest chapter here: the ones the grouping did not already count (a stack's released placeholder),
                // and not inside a volume that is missing whole (that volume is the missing unit).
                var resolver = new VolumeResolver(map);
                var missingWhole = missingVolumes.ToHashSet();
                for (var n = top + 1; n <= t && n <= MissingUnits.MaxNumber; n++)
                {
                    if (alreadyMissing.Contains(n))
                        continue;
                    if (resolver.HasData && resolver.Resolve(n) is { } placed && decimal.Truncate(placed.Volume) == placed.Volume
                        && missingWhole.Contains((int)placed.Volume))
                    {
                        continue;
                    }
                    behind++;
                }
                // The grouping's own released placeholders above the top count as behind too.
                behind += grouping.MissingChapterUnits.Count(u => u > top);
            }
            else
            {
                behind = grouping.MissingChapterUnits.Count(u => u > top);
            }
        }

        // Upgrades: official volumes 1..N without a volume file that the folder holds as chapters (whole or in part).
        var upgrades = facts.OfficialVolumes is { } n1 && n1 > 0
            ? reach.HeldAsChapters.Concat(reach.PartialVolumes).Where(v => v >= 1 && v <= n1 && !reach.VolumeFiles.Contains(v)).Distinct().Order().ToList()
            : [];

        var (volumeTotal, volumeSource) = VolumeTotal(facts);
        return Complete(new ProgressResult
        {
            VolumeTotal = volumeTotal,
            VolumeTotalSource = volumeSource,
            Facts = facts,
            Reach = reach,
            MissingVolumes = missingVolumes,
            VolumesBehind = volumesBehind,
            ChapterHoles = holes,
            ChaptersBehind = behind,
            ChapterTotal = total,
            ChapterTotalSource = source,
            UpgradeVolumes = upgrades,
        }, map);
    }

    private static double? Ratio(ProgressFacts facts) => facts.ChaptersPerVolume is { } r && r >= 1 ? r : null;

    // The released volume total: the official volumes, else the official chapters converted by a chapters-per-volume ratio (an estimate).
    private static (int? Total, MissingTotalSource? Source) VolumeTotal(ProgressFacts facts)
    {
        if (facts.OfficialVolumes is { } n && n > 0)
            return (n, MissingTotalSource.English);
        if (Ratio(facts) is { } r && facts.OfficialChapters is { } oc && (int)Math.Floor(oc / r) is var cv && cv > 0)
            return (cv, MissingTotalSource.Converted);
        return (null, null);
    }

    // The released chapter total: an official chapter total (English), the official volumes converted, the released list, the latest
    // release (English) - the first that covers what is here, else the first (as the Missing report picks its total).
    private static (int? Total, MissingTotalSource? Source) ChapterTotal(ProgressFacts facts, int have)
    {
        var list = new List<(int Total, MissingTotalSource Source)>();
        if (facts.OfficialChapters is { } oc && oc > 0)
            list.Add((oc, MissingTotalSource.English));
        if (Ratio(facts) is { } r && facts.OfficialVolumes is { } ov && (int)Math.Floor(ov * r) is var ec && ec > 0)
            list.Add((ec, MissingTotalSource.Converted));
        if (facts.ReleasedChapter is { } rc && rc > 0)
            list.Add((rc, MissingTotalSource.Released));
        if (facts.LatestChapter is { } lc && lc > 0)
            list.Add((lc, MissingTotalSource.LatestChapter));
        if (list.Count == 0)
            return (null, null);
        var pick = list.FirstOrDefault(x => x.Total >= have);
        return pick.Total > 0 ? (pick.Total, pick.Source) : (list[0].Total, list[0].Source);
    }

    /// <summary>
    /// The completion mark (owner, 1.30.0, like Manga-list's Completed column cross-checked with the library): the first basis the
    /// folder holds whole - the finished official edition (volumes 1..N), a finished English scanlation (every chapter to the last),
    /// the ended origin run (its volumes, else its chapters) - is a Complete collection; a series finished IN THE PREFERRED LANGUAGE
    /// (bases 1-2) that the folder does not hold whole is a prompt. A complete run of chapters counts as holding its volume.
    /// </summary>
    private static ProgressResult Complete(ProgressResult r, VolumeMapInput map)
    {
        var f = r.Facts;
        var reach = r.Reach;
        var wholeVolumes = reach.VolumeFiles.Concat(reach.HeldAsChapters).ToHashSet();
        int HeldVolumes(int n) => Enumerable.Range(1, Math.Min(n, MissingUnits.MaxNumber)).Count(wholeVolumes.Contains);
        int HeldChapters(int n) => Enumerable.Range(1, Math.Min(n, MissingUnits.MaxNumber)).Count(reach.Covered.Contains);

        var candidates = new List<(CompletionBasis Basis, int Target, int Held, bool Chapters, bool InLanguage)>();
        if (f.OfficialVolumes is { } n && n > 0
            && (f.OfficialStatus == MetadataOriginStatus.Complete
                || (f.OfficialStatus is null && f.OriginStatus == MetadataOriginStatus.Complete && f.OriginVolumes is { } originTotal && n >= originTotal)))
        {
            candidates.Add((CompletionBasis.OfficialVolumes, n, HeldVolumes(n), false, true));
        }
        if (f.ScanlationComplete == true && f.OriginStatus == MetadataOriginStatus.Complete
            && (f.LatestChapter ?? f.OriginChapters ?? LastListedChapter(map)) is { } last && last > 0)
        {
            candidates.Add((CompletionBasis.AllChapters, last, HeldChapters(last), true, true));
        }
        if (f.OriginEnded)
        {
            if (f.OriginVolumes is { } ov && ov > 0)
                candidates.Add((CompletionBasis.OriginRun, ov, HeldVolumes(ov), false, false));
            else if (f.OriginChapters is { } oc && oc > 0)
                candidates.Add((CompletionBasis.OriginRun, oc, HeldChapters(oc), true, false));
        }

        if (candidates.FirstOrDefault(c => c.Held >= c.Target) is { Target: > 0 } done)
        {
            return r with
            {
                Completion = SeriesCompletion.CompleteCollection,
                CompletionBasis = done.Basis,
                CompletionTarget = done.Target,
                CompletionHeld = done.Held,
                CompletionInChapters = done.Chapters,
            };
        }
        if (candidates.FirstOrDefault(c => c.InLanguage) is { Target: > 0 } open)
        {
            return r with
            {
                Completion = SeriesCompletion.FinishedNotHeld,
                CompletionBasis = open.Basis,
                CompletionTarget = open.Target,
                CompletionHeld = open.Held,
                CompletionInChapters = open.Chapters,
            };
        }
        return r;
    }

    private static int? LastListedChapter(VolumeMapInput map)
    {
        var whole = map.Volumes.SelectMany(v => v.Chapters).Where(c => decimal.Truncate(c) == c && c > 0).ToList();
        return whole.Count > 0 ? (int)whole.Max() : null;
    }

    /// <summary>
    /// The Missing report's verdict and per-kind gaps of a result (1.30.0: the report reads the same engine as the Volumes view).
    /// Volumes: the highest volume here (files, whole or partial chapter runs) against the released volume total; chapters (only
    /// with chapter files): the reach against the released chapter total. An upgrade is never behind nor a gap. Verdict
    /// <see cref="MissingVerdict.Mixed"/> is no longer produced.
    /// </summary>
    public static MissingUnitsResult ToMissing(ProgressResult r, int volumeArchives, int chapterArchives)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Restarts)
            return new MissingUnitsResult(MissingVerdict.Restarts, null, null, 0);
        var reach = r.Reach;
        if (!reach.HasNumbers)
            return new MissingUnitsResult(MissingVerdict.NoUnits, null, null, 0);

        var origin = r.Facts.OriginVolumes;
        MissingUnitGap? volumes = null;
        if (reach.VolumeFiles.Count > 0)
        {
            var touched = reach.TouchedVolumes.Order().ToList();
            var have = touched[^1];
            var holes = r.MissingVolumes.Where(v => v < have).ToList();
            volumes = new MissingUnitGap(
                MissingUnitKind.Volume, volumeArchives, touched.Count, touched[0], have, r.VolumeTotal, r.VolumeTotalSource,
                ConfidenceOf(r.VolumeTotalSource),
                r.VolumeTotalSource == MissingTotalSource.Converted && r.VolumeTotal is { } estimate ? Math.Max(0, estimate - have) : r.VolumesBehind,
                holes.Take(MissingUnits.MaxListed).ToList(), holes.Count, origin);
        }

        MissingUnitGap? chapters = null;
        var files = reach.ChapterFiles.Where(n => n > 0).Order().ToList();
        if (files.Count > 0)
        {
            var whole = r.ChapterHoles.Where(u => decimal.Truncate(u) == u).Select(u => (int)u).ToList();
            chapters = new MissingUnitGap(
                MissingUnitKind.Chapter, chapterArchives, files.Count, files[0], reach.ReachChapter ?? files[^1], r.ChapterTotal, r.ChapterTotalSource,
                ConfidenceOf(r.ChapterTotalSource), r.ChaptersBehind, whole.Take(MissingUnits.MaxListed).ToList(), r.ChapterHoles.Count,
                r.Facts.OriginChapters);
        }

        var gaps = new[] { volumes, chapters }.OfType<MissingUnitGap>().ToList();
        MissingVerdict verdict;
        if (gaps.Count == 0)
            verdict = MissingVerdict.NoUnits;
        else if (gaps.Any(g => g.BehindBy > 0))
            verdict = MissingVerdict.Behind;
        else if (gaps.Any(g => g.MissingCount > 0))
            verdict = MissingVerdict.Holes;
        else if (gaps.All(g => g.Available is null) && !r.Facts.ReleaseKnown)
            verdict = MissingVerdict.NoTotal;
        else
            verdict = MissingVerdict.UpToDate;
        return new MissingUnitsResult(verdict, volumes, chapters, 0);
    }

    private static MissingConfidence? ConfidenceOf(MissingTotalSource? source) => source switch
    {
        MissingTotalSource.English => MissingConfidence.High,
        MissingTotalSource.Converted or MissingTotalSource.Released or MissingTotalSource.Origin => MissingConfidence.Medium,
        MissingTotalSource.LatestChapter => MissingConfidence.Low,
        _ => null,
    };

    /// <summary>The DTO of a result.</summary>
    public static SeriesProgressDto ToDto(ProgressResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var f = r.Facts;
        return new SeriesProgressDto
        {
            Trackers = new SeriesTrackersDto
            {
                Language = f.Language,
                Origin = f.Origin,
                OriginStatus = f.OriginStatus is MetadataOriginStatus.Unknown ? null : f.OriginStatus,
                OriginVolumes = f.OriginVolumes,
                OriginChapters = f.OriginChapters,
                OfficialPublisher = f.OfficialPublisher,
                OfficialVolumes = f.OfficialVolumes,
                OfficialChapters = f.OfficialChapters,
                OfficialStatus = f.OfficialStatus,
                Licensed = f.Licensed,
                LatestChapter = f.LatestChapter,
                ScanlationComplete = f.ScanlationComplete,
                ReleasedChapter = f.ReleasedChapter,
            },
            Reach = r.Restarts ? null : SeriesReach.ToDto(r.Reach),
            MissingVolumes = r.MissingVolumes.Count,
            MissingChapters = r.MissingChapterCount,
            ReleaseKnown = f.ReleaseKnown,
            UpgradeVolumes = r.UpgradeVolumes.Take(MaxListed).ToList(),
            UpgradeCount = r.UpgradeVolumes.Count,
            Completion = r.Completion,
            CompletionBasis = r.CompletionBasis,
            CompletionTarget = r.CompletionTarget,
            CompletionHeld = r.CompletionHeld,
            CompletionInChapters = r.CompletionInChapters,
        };
    }
}
