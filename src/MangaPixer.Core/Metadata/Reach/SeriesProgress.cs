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
/// 1.39.0: <see cref="VolumeOverride"/>, <see cref="Edition"/> and <see cref="TrackingOff"/> are what an admin declared on the series folder
/// itself (<see cref="DeclaredEditionFacts"/>): the edition's volume count replaces the regular edition's volumes in every VOLUME answer
/// (chapter answers are unchanged); tracking off gives no Completion / missing / upgrade answer at all.
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
    double? ChaptersPerVolume = null,
    int? VolumeOverride = null,
    DeclaredEdition? Edition = null,
    bool TrackingOff = false)
{
    /// <summary>The highest whole chapter the released list names, or null.</summary>
    public int? ReleasedChapter => ReleasedChapters is { Count: > 0 } r ? (int)decimal.Floor(r.Max()) : null;

    /// <summary>Something is known about what is released in the language ("up to date" can be said).</summary>
    public bool ReleaseKnown => OfficialVolumes is not null || OfficialChapters is not null || ReleasedChapters is not null || LatestChapter is not null
        || VolumeOverride is not null;

    /// <summary>The origin run has ended (complete, or cancelled there).</summary>
    public bool OriginEnded => OriginStatus is MetadataOriginStatus.Complete or MetadataOriginStatus.Cancelled;

    /// <summary>The preferred language is English (the MangaUpdates facts apply).</summary>
    public bool IsEnglish => string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>1.32.0: the one answer of the Completion tab (<see cref="SeriesAnswers"/>).</summary>
    public SeriesAnswer Answer { get; init; }
    public SeriesAnswerReason AnswerReason { get; init; }

    public int MissingChapterCount => ChapterHoles.Count + ChaptersBehind;

    /// <summary>Something released in the preferred language (or a hole below the highest unit here) is not in the folder.</summary>
    public bool AnythingMissing => MissingVolumes.Count > 0 || MissingChapterCount > 0;
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
        {
            return facts.TrackingOff
                ? NotTracked(new ProgressResult { Facts = facts, Reach = ReachResult.Empty, Restarts = true })
                : SeriesAnswers.Apply(new ProgressResult { Facts = facts, Reach = ReachResult.Empty, Restarts = true }, 0);
        }

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
        // 1.39.0: tracking off - what the folder holds still shows; nothing is compared.
        if (facts.TrackingOff)
            return NotTracked(new ProgressResult { Facts = facts, Reach = reach });
        if (!reach.HasNumbers)
            return SeriesAnswers.Apply(new ProgressResult { Facts = facts, Reach = reach }, archives.Count);

        var grouping = VolumeGrouping.Group(archives, map, markMissingVolumes: true);
        IReadOnlyList<int> missingVolumes;
        int volumesBehind;
        if (facts.VolumeOverride is { } edition)
        {
            // 1.39.0: the declared edition's volumes 1..N, by volume FILES only - a chapter cannot be placed in an edition volume without
            // that edition's list, so a folder without volume files has no volume answer to give.
            var files = reach.VolumeFiles.Where(v => v >= 1).ToList();
            var highestFile = files.Count > 0 ? files.Max() : 0;
            missingVolumes = files.Count == 0
                ? []
                : Enumerable.Range(1, Math.Min(edition, VolumeGrouping.MaxMissingVolumes)).Where(v => !reach.VolumeFiles.Contains(v)).ToList();
            volumesBehind = missingVolumes.Count(v => v > highestFile);
        }
        else
        {
            var present = reach.TouchedVolumes.ToList();
            var highestVolume = present.Count > 0 ? present.Max() : 0;
            missingVolumes = grouping.MissingVolumeNumbers;
            volumesBehind = missingVolumes.Count(v => v > highestVolume);
        }

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
                // The regular list's missing volumes (chapter answers never follow an edition override).
                var missingWhole = grouping.MissingVolumeNumbers.ToHashSet();
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
        // 1.39.0: none with an edition override (the official volumes are the regular edition's).
        var upgrades = facts.VolumeOverride is null && facts.OfficialVolumes is { } n1 && n1 > 0
            ? reach.HeldAsChapters.Concat(reach.PartialVolumes).Where(v => v >= 1 && v <= n1 && !reach.VolumeFiles.Contains(v)).Distinct().Order().ToList()
            : [];

        var (volumeTotal, volumeSource) = VolumeTotal(facts);
        return SeriesAnswers.Apply(Complete(new ProgressResult
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
        }, map), archives.Count);
    }

    private static double? Ratio(ProgressFacts facts) => facts.ChaptersPerVolume is { } r && r >= 1 ? r : null;

    /// <summary>
    /// 1.39.0, "Track completion: off": the reach and the trackers stay (what the folder holds, what is out), and no answer is given -
    /// nothing missing, no upgrade, no completion; the answer is Can't tell with the reason <see cref="SeriesAnswerReason.NotTracked"/>.
    /// </summary>
    private static ProgressResult NotTracked(ProgressResult r) =>
        r with { Answer = SeriesAnswer.CantTell, AnswerReason = SeriesAnswerReason.NotTracked };

    // The released volume total: the official volumes, else the official chapters converted by a chapters-per-volume ratio (an estimate).
    private static (int? Total, MissingTotalSource? Source) VolumeTotal(ProgressFacts facts)
    {
        if (facts.VolumeOverride is { } declared)
            return (declared, MissingTotalSource.Declared);
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
    /// folder holds whole - the finished official edition (volumes 1..N), every chapter released in the language (to the last), the
    /// ended origin run (its volumes, else its chapters) - is a Complete collection; a series finished IN THE PREFERRED LANGUAGE
    /// (bases 1-2) that the folder does not hold whole is a prompt. A complete run of chapters counts as holding its volume.
    /// <para>
    /// 1.32.0 (Completion tab, owner-approved): every basis needs the ORIGIN RUN to have ended (complete or cancelled) - a finished
    /// English edition of a running series is not the end; every chapter is out when MangaUpdates says so (English) or, for another
    /// language, when the released list reaches the last chapter anything knows; a folder of volume FILES with a known volume edition in
    /// the language is never prompted by the chapter release (the volume edition decides); a chapter release covered by an official
    /// chapter-by-chapter publisher is named <see cref="CompletionBasis.OfficialChapters"/>.
    /// </para>
    /// </summary>
    private static ProgressResult Complete(ProgressResult r, VolumeMapInput map)
    {
        var f = r.Facts;
        var reach = r.Reach;
        // A volume held as chapters counts only by an exact list (1.30.1, owner live check: a 4-volume series with chapters 1-36 was
        // "complete" from a chapters-per-volume estimate while the provider knew chapters past 100).
        var wholeVolumes = reach.VolumeFiles.Concat(reach.HeldAsChapters.Except(reach.HeldByEstimate)).ToHashSet();
        int HeldVolumes(int n) => Enumerable.Range(1, Math.Min(n, MissingUnits.MaxNumber)).Count(wholeVolumes.Contains);
        int HeldChapters(int n) => Enumerable.Range(1, Math.Min(n, MissingUnits.MaxNumber)).Count(reach.Covered.Contains);

        // The last chapter anything knows. A volume list built from translations names only the translated chapters of the last
        // volume (1.30.1, owner live check: volume 8 listed as chapters 36-38 while the origin run has 40), so volumes held as
        // chapters are whole only when the chapters here reach it. Volume FILES are whole by themselves.
        var knownLast = new[] { f.OriginChapters, f.LatestChapter, f.ReleasedChapter, f.OfficialChapters, LastListedChapter(map) }.Max();
        bool ReachesKnownLast() => knownLast is not { } k || k <= 0 || reach.ReachChapter is { } h && h >= k;
        bool WholeByVolumes(int n) => Enumerable.Range(1, Math.Min(n, MissingUnits.MaxNumber)).All(reach.VolumeFiles.Contains) || ReachesKnownLast();

        var candidates = new List<(CompletionBasis Basis, int Target, int Held, bool Chapters, bool InLanguage, bool Whole)>();
        // 1.39.0: an edition override replaces the regular edition's volume bases (official volumes, the origin run by volumes) with the
        // declared edition's volumes 1..N held as volume FILES; like every basis it needs the origin run to have ended.
        var edition = f.VolumeOverride;
        if (f.OriginEnded && edition is { } declared && declared > 0)
        {
            var held = Enumerable.Range(1, Math.Min(declared, MissingUnits.MaxNumber)).Count(reach.VolumeFiles.Contains);
            candidates.Add((CompletionBasis.Edition, declared, held, false, true, held >= declared));
        }
        if (edition is null && f.OriginEnded && f.OfficialVolumes is { } n && n > 0
            && (f.OfficialStatus == MetadataOriginStatus.Complete
                || (f.OfficialStatus is null && f.OriginStatus == MetadataOriginStatus.Complete && f.OriginVolumes is { } originTotal && n >= originTotal)))
        {
            candidates.Add((CompletionBasis.OfficialVolumes, n, HeldVolumes(n), false, true, WholeByVolumes(n)));
        }
        // The last chapter is the HIGHEST extent anything knows (1.30.0 soak test: the latest release said 51 while chapters to 55
        // were listed as released - the series read "Complete collection" next to "4 chapters missing").
        if (f.OriginEnded && ChaptersFinished(f, map)
            && new[] { f.LatestChapter, f.OriginChapters, f.ReleasedChapter, LastListedChapter(map) }.Max() is { } last && last > 0)
        {
            // A folder of volume files with a known volume edition in the language is judged by that edition, never prompted by the
            // chapter release (1.32.0 rule V: an English edition still coming is "everything released so far", not "missing some").
            var inLanguage = !(SeriesAnswers.CollectsVolumes(reach) && (f.OfficialVolumes is > 0 || edition is not null));
            var basis = f.OfficialChapters is { } oc && oc >= last
                || (f.OfficialVolumes is null && f.OfficialChapters is not null && f.OfficialStatus == MetadataOriginStatus.Complete)
                ? CompletionBasis.OfficialChapters
                : CompletionBasis.AllChapters;
            candidates.Add((basis, last, HeldChapters(last), true, inLanguage, true));
        }
        if (f.OriginEnded)
        {
            if (edition is null && f.OriginVolumes is { } ov && ov > 0)
                candidates.Add((CompletionBasis.OriginRun, ov, HeldVolumes(ov), false, false, WholeByVolumes(ov)));
            else if (f.OriginChapters is { } oc && oc > 0 && Math.Max(oc, knownLast ?? 0) is var lastOrigin)
                candidates.Add((CompletionBasis.OriginRun, lastOrigin, HeldChapters(lastOrigin), true, false, true));
        }

        // Complete means nothing is missing - never a Complete collection next to a missing count.
        var nothingMissing = r.MissingVolumes.Count == 0 && r.VolumesBehind == 0 && r.MissingChapterCount == 0;
        if (nothingMissing && candidates.FirstOrDefault(c => c.Held >= c.Target && c.Whole) is { Target: > 0 } done)
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

    // Every chapter is out in the language: MangaUpdates' "completely released in English" for English; for another language the
    // released list reaching the last chapter the origin or the volume list knows (1.32.0; no hard-coded English).
    private static bool ChaptersFinished(ProgressFacts f, VolumeMapInput map)
    {
        if (f.ScanlationComplete == true)
            return true;
        if (f.IsEnglish)
            return false;
        var end = new[] { f.OriginChapters, LastListedChapter(map) }.Max();
        return end is { } e && e > 0 && f.ReleasedChapter is { } released && released >= e;
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
        // 1.39.0: tracking off - no verdict (the report leaves such a series out; its own line says it is not tracked).
        if (r.Facts.TrackingOff)
            return new MissingUnitsResult(MissingVerdict.NoUnits, null, null, 0);
        if (r.Restarts)
            return new MissingUnitsResult(MissingVerdict.Restarts, null, null, 0);
        var reach = r.Reach;
        if (!reach.HasNumbers)
            return new MissingUnitsResult(MissingVerdict.NoUnits, null, null, 0);

        var origin = r.Facts.OriginVolumes;
        MissingUnitGap? volumes = null;
        // 1.39.0: with an edition override only volume files count (chapters say nothing about the edition's volumes).
        var touched = (r.Facts.VolumeOverride is null ? reach.TouchedVolumes : reach.VolumeFiles.Where(v => v >= 1)).Order().ToList();
        if (reach.VolumeFiles.Count > 0 && touched.Count > 0)
        {
            var have = touched[^1];
            var holes = r.MissingVolumes.Where(v => v < have).ToList();
            volumes = new MissingUnitGap(
                MissingUnitKind.Volume, volumeArchives, touched.Count, touched[0], have, r.VolumeTotal, r.VolumeTotalSource,
                ConfidenceOf(r.VolumeTotalSource),
                r.VolumeTotalSource == MissingTotalSource.Converted && r.VolumeTotal is { } estimate ? Math.Max(0, estimate - have) : r.VolumesBehind,
                holes.Take(MissingUnits.MaxListed).ToList(), holes.Count, origin);
        }

        MissingUnitGap? chapters = null;
        // A prologue (chapter 0) counts as a file here, as in the 1.29.0 report, once a real chapter is present.
        var files = reach.ChapterFiles.Where(n => n >= 0).Order().ToList();
        if (files.Any(n => n > 0))
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
        MissingTotalSource.English or MissingTotalSource.Declared => MissingConfidence.High,
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
            Answer = r.Answer,
            AnswerReason = r.AnswerReason,
            VolumeTotalOverride = f.VolumeOverride,
            Edition = f.Edition,
            TrackingOff = f.TrackingOff,
        };
    }
}
