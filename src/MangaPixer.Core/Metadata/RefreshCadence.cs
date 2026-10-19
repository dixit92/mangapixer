namespace com.lifepixer.mangapixer.Core.Metadata;

/// <summary>
/// How often stored web information about a linked series is looked at again (1.32.0): ONE cadence for the id-only refresh,
/// the companion schedule (MangaDex / AniList / Wikipedia data) and the cover decision re-check. The admin chooses it
/// (<see cref="RefreshCadencePolicy"/>): a fixed rhythm for ongoing series, or (1.35.0) "Follow their pace", where <see cref="For"/>
/// checks each ongoing series by how quickly it publishes - new chapters (the latest chapter MangaUpdates states, which follows
/// scanlation releases), new volumes (the original edition), or whichever is faster (<see cref="RefreshPaceSource"/>) - from what the
/// stored record and its observations say, never from a new request. Before 1.35.0 the ongoing rhythm also capped the pace.
/// The result is stored per record (<c>metadata_records.RefreshCadenceDays</c>); <see cref="AgeFor(int?)"/> is the fallback for
/// a record not computed yet: every 30 days while ongoing (or unknown / on hiatus), every 90 days once finished.
/// </summary>
public static class RefreshCadence
{
    public static readonly TimeSpan OngoingAge = TimeSpan.FromDays(30);
    public static readonly TimeSpan FinishedAge = TimeSpan.FromDays(90);

    /// <summary>Never more often than this from the volume pace (and, before 1.45.0, from any pace).</summary>
    public const int FastestDays = 7;

    /// <summary>
    /// 1.45.0 (MangaList's new-chapter detection, recommendation 4): a series paced by its chapters is checked at about half its chapter
    /// interval - a check at or above the interval found a monthly chapter half a month late on average - but never more often than this.
    /// </summary>
    public const int ChapterPaceFastestDays = 3;

    /// <summary>
    /// 1.45.0 (recommendation 3, the reading fast lane): a series someone is reading - a read mark in the last <see cref="ReadingWindowDays"/>
    /// days, or a tracker entry with status Reading - is checked this often, below <see cref="FastestDays"/> for these series only.
    /// </summary>
    public const int ReadingDays = 2;

    /// <summary>A read mark this recent makes a series "being read" (<see cref="ReadingDays"/>).</summary>
    public const int ReadingWindowDays = 14;

    /// <summary>Nothing new for this long (with a history at least this long) = paused: checked like a finished series.</summary>
    public const int QuietDays = 180;

    /// <summary>A volume interval below this is checked weekly; below <see cref="EveryTwoWeeksBelowDays"/> every 2 weeks.</summary>
    public const double WeeklyBelowDays = 60;

    public const double EveryTwoWeeksBelowDays = 120;

    /// <summary>The observed interval needs at least two volume increases this far apart.</summary>
    public const double MinObservedSpanDays = 120;

    /// <summary>The observed chapter interval needs at least two chapter increases this far apart (1.35.0).</summary>
    public const double MinObservedChapterSpanDays = 28;

    /// <summary>Chapters taken as one volume for a series without volumes, when the volume map gives no average.</summary>
    public const double ChaptersPerVolumeFallback = 10;

    /// <summary>The <see cref="MetadataOriginStatus"/> values (as stored) that count as finished.</summary>
    public static IReadOnlyList<int?> FinishedStatuses { get; } =
        [(int)MetadataOriginStatus.Complete, (int)MetadataOriginStatus.Cancelled];

    public static bool IsFinished(int? originStatus) =>
        originStatus is (int)MetadataOriginStatus.Complete or (int)MetadataOriginStatus.Cancelled;

    /// <summary>How long stored information of a record with this origin status stays current, before its cadence is computed.</summary>
    public static TimeSpan AgeFor(int? originStatus) => IsFinished(originStatus) ? FinishedAge : OngoingAge;

    /// <summary>The record's stored cadence (<c>RefreshCadenceDays</c>) when computed, else <see cref="AgeFor(int?)"/>.</summary>
    public static TimeSpan AgeFor(int? storedDays, int? originStatus) =>
        storedDays is > 0 ? TimeSpan.FromDays(storedDays.Value) : AgeFor(originStatus);

    /// <summary>The cadence of one series under the admin's policy at <paramref name="now"/>.</summary>
    public static RefreshCadenceResult For(RefreshCadencePolicy policy, RefreshEvidence evidence, DateTimeOffset now)
    {
        // 1.45.0: the reading fast lane wins over every other rule - except a finished series whose translation is complete too (nothing
        // new can come).
        if (evidence.Reading && !(IsFinished(evidence.OriginStatus) && evidence.TranslationComplete == true))
            return new(ReadingDays, RefreshCadenceReason.Reading, null);
        if (IsFinished(evidence.OriginStatus))
            return new(policy.FinishedDays, RefreshCadenceReason.Finished, null);
        if (!policy.FollowPace)
            return new(policy.OngoingDays, RefreshCadenceReason.Choice, null);
        if (evidence.OriginStatus == (int)MetadataOriginStatus.Hiatus || IsQuiet(evidence.History, now))
            return new(policy.FinishedDays, RefreshCadenceReason.Paused, null);

        var volumes = policy.PaceSource == RefreshPaceSource.Chapters ? null : VolumeIntervalDays(evidence, now);
        var observed = policy.PaceSource == RefreshPaceSource.Volumes ? null : ObservedChapterInterval(evidence.History);
        var chapters = observed ?? (policy.PaceSource == RefreshPaceSource.Volumes ? null : LifetimeChapterInterval(evidence, now));
        int? volumeDays = volumes is { } v ? (v < WeeklyBelowDays ? FastestDays : v < EveryTwoWeeksBelowDays ? 14 : 30) : null;
        int? chapterDays = chapters is { } c ? ChapterPaceDays(c) : null;
        if (volumeDays is null && chapterDays is null)
            return new(RefreshCadencePolicy.DefaultOngoingDays, RefreshCadenceReason.PaceUnknown, null);
        var days = Math.Min(volumeDays ?? int.MaxValue, chapterDays ?? int.MaxValue);
        // Which pace set the days: the chapters on a tie (they are what changes between two refreshes).
        var from = chapterDays is { } cd && cd <= (volumeDays ?? int.MaxValue) ? RefreshPaceSource.Chapters : RefreshPaceSource.Volumes;
        return new(days, RefreshCadenceReason.Pace, volumes, chapters)
        {
            PaceFrom = from,
            ChapterIntervalEstimated = chapters is not null && observed is null,
        };
    }

    /// <summary>
    /// 1.45.0: the check interval for a chapter interval - about half of it, at least <see cref="ChapterPaceFastestDays"/> and at most the
    /// default ongoing rhythm (<see cref="RefreshCadencePolicy.DefaultOngoingDays"/>). Before 1.45.0 the buckets below 14 / 30 days gave 7 /
    /// 14 / 30, at or above the interval.
    /// </summary>
    public static int ChapterPaceDays(double chapterIntervalDays) =>
        double.IsFinite(chapterIntervalDays)
            ? (int)Math.Clamp(Math.Floor(chapterIntervalDays / 2), ChapterPaceFastestDays, RefreshCadencePolicy.DefaultOngoingDays)
            : RefreshCadencePolicy.DefaultOngoingDays;

    /// <summary>
    /// The average days between two new chapters since the series began (1.35.1): the stored latest chapter - which follows scanlation
    /// releases - over the days since 1 January of the start year. The chapter pace until two rises are observed
    /// (<see cref="ObservedChapterInterval"/>): it can only make a series look slower than it is (translations that started late, a
    /// pause), never faster. Null without a start year before this one or at least two chapters.
    /// </summary>
    public static double? LifetimeChapterInterval(RefreshEvidence evidence, DateTimeOffset now)
    {
        if (evidence.StartYear is not { } year || year >= now.UtcDateTime.Year || year < 1900 || evidence.LatestChapter is not (>= 2 and var chapters))
            return null;
        return (now - new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalDays / chapters;
    }

    /// <summary>
    /// The observed days between two new chapters (1.35.0): from two or more increases of the stored latest chapter at least
    /// <see cref="MinObservedChapterSpanDays"/> apart - the chapters gained after the first increase over the time between the first
    /// and the last. Null when the observations do not give it yet; then <see cref="LifetimeChapterInterval"/> stands in (1.35.1).
    /// </summary>
    public static double? ObservedChapterInterval(IReadOnlyList<RefreshObservation> history)
    {
        var increases = new List<(DateTimeOffset At, double Gained)>();
        for (var i = 1; i < history.Count; i++)
        {
            if (history[i].LatestChapter is { } now && history[i - 1].LatestChapter is { } before && now > before)
                increases.Add((history[i].At, now - before));
        }
        if (increases.Count < 2)
            return null;
        var span = (increases[^1].At - increases[0].At).TotalDays;
        if (span < MinObservedChapterSpanDays)
            return null;
        var gained = increases.Skip(1).Sum(x => x.Gained);
        return gained > 0 ? span / gained : null;
    }

    /// <summary>
    /// The estimated days between two volumes: observed (two or more volume increases at least
    /// <see cref="MinObservedSpanDays"/> apart), else the lifetime average since the start year, else - without volumes - the
    /// lifetime chapter rate times the chapters of a volume. Null when nothing gives it.
    /// </summary>
    public static double? VolumeIntervalDays(RefreshEvidence evidence, DateTimeOffset now)
    {
        if (ObservedVolumeInterval(evidence.History) is { } observed)
            return observed;
        if (evidence.StartYear is not { } year || year >= now.UtcDateTime.Year || year < 1900)
            return null;
        var lifetime = (now - new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalDays;
        if (evidence.OriginVolumes is >= 2)
            return lifetime / evidence.OriginVolumes.Value;
        if (evidence.LatestChapter is >= 2 and var chapters)
        {
            var perVolume = evidence.ChaptersPerVolume is > 0 ? evidence.ChaptersPerVolume.Value : ChaptersPerVolumeFallback;
            return lifetime / chapters * perVolume;
        }
        return null;
    }

    private static double? ObservedVolumeInterval(IReadOnlyList<RefreshObservation> history)
    {
        var increases = new List<(DateTimeOffset At, int Gained)>();
        for (var i = 1; i < history.Count; i++)
        {
            if (history[i].OriginVolumes is { } now && history[i - 1].OriginVolumes is { } before && now > before)
                increases.Add((history[i].At, now - before));
        }
        if (increases.Count < 2)
            return null;
        var span = (increases[^1].At - increases[0].At).TotalDays;
        if (span < MinObservedSpanDays)
            return null;
        var gained = increases.Skip(1).Sum(x => x.Gained);
        return span / gained;
    }

    /// <summary>True when the history covers <see cref="QuietDays"/> and no chapter or volume increase was seen in that time.</summary>
    public static bool IsQuiet(IReadOnlyList<RefreshObservation> history, DateTimeOffset now)
    {
        if (history.Count == 0 || (now - history[0].At).TotalDays < QuietDays)
            return false;
        var since = now - TimeSpan.FromDays(QuietDays);
        for (var i = 1; i < history.Count; i++)
        {
            if (history[i].At < since)
                continue;
            if (Greater(history[i].LatestChapter, history[i - 1].LatestChapter) || Greater(history[i].OriginVolumes, history[i - 1].OriginVolumes))
                return false;
        }
        return true;
    }

    private static bool Greater(double? now, double? before) => now is { } a && (before is not { } b || a > b);

    private static bool Greater(int? now, int? before) => now is { } a && (before is not { } b || a > b);
}

/// <summary>
/// The admin's refresh cadence (1.32.0): days for ongoing and finished (or paused) series, and whether to follow each series' pace
/// instead of the ongoing days (1.35.0: "Follow their pace" is its own choice; <see cref="OngoingDays"/> is not read while it is on),
/// and what the pace follows.
/// </summary>
public sealed record RefreshCadencePolicy(int OngoingDays, int FinishedDays, bool FollowPace, RefreshPaceSource PaceSource = RefreshPaceSource.Faster)
{
    public const int DefaultOngoingDays = 30;
    public const int DefaultFinishedDays = 90;

    /// <summary>The default of "Follow each series' publishing pace" (1.32.0 provisional decision Q4: on).</summary>
    public const bool DefaultFollowPace = true;

    public static IReadOnlyList<int> AllowedOngoingDays { get; } = [7, 14, 30];
    public static IReadOnlyList<int> AllowedFinishedDays { get; } = [30, 90, 180];

    public static RefreshCadencePolicy Default { get; } = new(DefaultOngoingDays, DefaultFinishedDays, DefaultFollowPace);

    /// <summary>The policy from the stored columns (null or an unknown value = the default).</summary>
    public static RefreshCadencePolicy FromStored(int? ongoingDays, int? finishedDays, bool followPace, int? paceSource = null) => new(
        ongoingDays is { } o && AllowedOngoingDays.Contains(o) ? o : DefaultOngoingDays,
        finishedDays is { } f && AllowedFinishedDays.Contains(f) ? f : DefaultFinishedDays,
        followPace,
        paceSource is { } p && Enum.IsDefined((RefreshPaceSource)p) ? (RefreshPaceSource)p : RefreshPaceSource.Faster);
}

/// <summary>What "Follow their pace" follows (1.35.0). Stored as its number; null = <see cref="Faster"/>.</summary>
public enum RefreshPaceSource
{
    /// <summary>Whichever of the chapter and the volume pace checks more often (the default).</summary>
    Faster = 0,

    /// <summary>New chapters: the stored latest chapter, which follows scanlation releases.</summary>
    Chapters = 1,

    /// <summary>New volumes of the original edition.</summary>
    Volumes = 2,
}

/// <summary>Why a series has its cadence.</summary>
public enum RefreshCadenceReason
{
    /// <summary>Complete or cancelled: the finished choice.</summary>
    Finished = 0,

    /// <summary>The ongoing choice (pace off, or no pace known).</summary>
    Choice = 1,

    /// <summary>From the publishing pace (<see cref="RefreshCadenceResult.VolumeIntervalDays"/>).</summary>
    Pace = 2,

    /// <summary>On hiatus, or nothing new for <see cref="RefreshCadence.QuietDays"/>: the finished choice.</summary>
    Paused = 3,

    /// <summary>Following the pace, but none is known yet (1.35.0): every <see cref="RefreshCadencePolicy.DefaultOngoingDays"/> days.</summary>
    PaceUnknown = 4,

    /// <summary>1.45.0: someone is reading the series (the reading fast lane): every <see cref="RefreshCadence.ReadingDays"/> days.</summary>
    Reading = 5,
}

/// <summary>
/// A series' cadence, why, and the pace it came from (days between volumes / chapters; null when not used or unknown). 1.35.1:
/// <see cref="PaceFrom"/> - which pace set the days (null unless <see cref="RefreshCadenceReason.Pace"/>); <see cref="ChapterIntervalEstimated"/>
/// - the chapter interval is the average since the series began, not yet observed.
/// </summary>
public readonly record struct RefreshCadenceResult(int Days, RefreshCadenceReason Reason, double? VolumeIntervalDays, double? ChapterIntervalDays = null)
{
    public RefreshPaceSource? PaceFrom { get; init; }
    public bool ChapterIntervalEstimated { get; init; }
}

/// <summary>
/// What the stored record says about a series' pace, plus its observations (oldest first). 1.45.0: <see cref="Reading"/> - someone is
/// reading it (a read mark in the last <see cref="RefreshCadence.ReadingWindowDays"/> days, or a tracker entry with status Reading);
/// <see cref="TranslationComplete"/> - the record says the translation is complete.
/// </summary>
public sealed record RefreshEvidence(
    int? OriginStatus,
    int? StartYear,
    int? OriginVolumes,
    double? LatestChapter,
    double? ChaptersPerVolume,
    IReadOnlyList<RefreshObservation> History,
    bool Reading = false,
    bool? TranslationComplete = null);

/// <summary>One stored observation of a record (written when a refresh left different values).</summary>
public readonly record struct RefreshObservation(DateTimeOffset At, double? LatestChapter, int? OriginVolumes);
