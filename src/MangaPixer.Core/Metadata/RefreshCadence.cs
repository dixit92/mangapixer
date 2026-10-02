namespace com.lifepixer.mangapixer.Core.Metadata;

/// <summary>
/// How often stored web information about a linked series is looked at again (1.32.0): ONE cadence for the id-only refresh,
/// the companion schedule (MangaDex / AniList / Wikipedia data) and the cover decision re-check. The admin chooses it
/// (<see cref="RefreshCadencePolicy"/>) and, with "Follow each series' publishing pace" on, <see cref="For"/> makes a series that
/// publishes quickly come round more often - from what the stored record and its observations say, never from a new request.
/// The result is stored per record (<c>metadata_records.RefreshCadenceDays</c>); <see cref="AgeFor(int?)"/> is the fallback for
/// a record not computed yet: every 30 days while ongoing (or unknown / on hiatus), every 90 days once finished.
/// </summary>
public static class RefreshCadence
{
    public static readonly TimeSpan OngoingAge = TimeSpan.FromDays(30);
    public static readonly TimeSpan FinishedAge = TimeSpan.FromDays(90);

    /// <summary>Never more often than this, whatever the pace.</summary>
    public const int FastestDays = 7;

    /// <summary>Nothing new for this long (with a history at least this long) = paused: checked like a finished series.</summary>
    public const int QuietDays = 180;

    /// <summary>A volume interval below this is checked weekly; below <see cref="EveryTwoWeeksBelowDays"/> every 2 weeks.</summary>
    public const double WeeklyBelowDays = 60;

    public const double EveryTwoWeeksBelowDays = 120;

    /// <summary>The observed interval needs at least two volume increases this far apart.</summary>
    public const double MinObservedSpanDays = 120;

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
        if (IsFinished(evidence.OriginStatus))
            return new(policy.FinishedDays, RefreshCadenceReason.Finished, null);
        if (!policy.FollowPace)
            return new(policy.OngoingDays, RefreshCadenceReason.Choice, null);
        if (evidence.OriginStatus == (int)MetadataOriginStatus.Hiatus || IsQuiet(evidence.History, now))
            return new(policy.FinishedDays, RefreshCadenceReason.Paused, null);
        if (VolumeIntervalDays(evidence, now) is not { } interval)
            return new(policy.OngoingDays, RefreshCadenceReason.Choice, null);
        var days = interval < WeeklyBelowDays ? FastestDays : interval < EveryTwoWeeksBelowDays ? 14 : 30;
        return new(Math.Min(days, policy.OngoingDays), RefreshCadenceReason.Pace, interval);
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
            return lifetime / chapters.Value * perVolume;
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

/// <summary>The admin's refresh cadence (1.32.0): days for ongoing and finished series, and whether to follow each series' pace.</summary>
public sealed record RefreshCadencePolicy(int OngoingDays, int FinishedDays, bool FollowPace)
{
    public const int DefaultOngoingDays = 30;
    public const int DefaultFinishedDays = 90;

    /// <summary>The default of "Follow each series' publishing pace" (1.32.0 provisional decision Q4: on).</summary>
    public const bool DefaultFollowPace = true;

    public static IReadOnlyList<int> AllowedOngoingDays { get; } = [7, 14, 30];
    public static IReadOnlyList<int> AllowedFinishedDays { get; } = [30, 90, 180];

    public static RefreshCadencePolicy Default { get; } = new(DefaultOngoingDays, DefaultFinishedDays, DefaultFollowPace);

    /// <summary>The policy from the stored columns (null or an unknown value = the default).</summary>
    public static RefreshCadencePolicy FromStored(int? ongoingDays, int? finishedDays, bool followPace) => new(
        ongoingDays is { } o && AllowedOngoingDays.Contains(o) ? o : DefaultOngoingDays,
        finishedDays is { } f && AllowedFinishedDays.Contains(f) ? f : DefaultFinishedDays,
        followPace);
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
}

public readonly record struct RefreshCadenceResult(int Days, RefreshCadenceReason Reason, double? VolumeIntervalDays);

/// <summary>What the stored record says about a series' pace, plus its observations (oldest first).</summary>
public sealed record RefreshEvidence(
    int? OriginStatus,
    int? StartYear,
    int? OriginVolumes,
    double? LatestChapter,
    double? ChaptersPerVolume,
    IReadOnlyList<RefreshObservation> History);

/// <summary>One stored observation of a record (written when a refresh left different values).</summary>
public readonly record struct RefreshObservation(DateTimeOffset At, double? LatestChapter, int? OriginVolumes);
