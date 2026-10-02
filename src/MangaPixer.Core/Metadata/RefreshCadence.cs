namespace com.lifepixer.mangapixer.Core.Metadata;

/// <summary>
/// How often stored web information about a linked series is looked at again (1.32.0 step 0): ONE place for the cadence
/// that was duplicated in the id-only refresh, the companion schedule (MangaDex / AniList data) and the cover decision
/// re-check - every 30 days while a series is ongoing (or its status is unknown / on hiatus), every 90 days once it is
/// finished (complete or cancelled). The dynamic, admin-selectable cadence (1.32.0 lane D) changes it here.
/// </summary>
public static class RefreshCadence
{
    public static readonly TimeSpan OngoingAge = TimeSpan.FromDays(30);
    public static readonly TimeSpan FinishedAge = TimeSpan.FromDays(90);

    /// <summary>The <see cref="MetadataOriginStatus"/> values (as stored) that count as finished.</summary>
    public static IReadOnlyList<int?> FinishedStatuses { get; } =
        [(int)MetadataOriginStatus.Complete, (int)MetadataOriginStatus.Cancelled];

    public static bool IsFinished(int? originStatus) =>
        originStatus is (int)MetadataOriginStatus.Complete or (int)MetadataOriginStatus.Cancelled;

    /// <summary>How long stored information of a record with this origin status stays current.</summary>
    public static TimeSpan AgeFor(int? originStatus) => IsFinished(originStatus) ? FinishedAge : OngoingAge;
}
