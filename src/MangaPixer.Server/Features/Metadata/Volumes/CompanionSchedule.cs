namespace com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence.Entities;

/// <summary>
/// When companion data (the MangaDex record, its volume list and cover list, the AniList totals) is looked at again
/// (1.29.0): on the linked series record's refresh cadence - every 30 days while it is ongoing, every 90 days once it
/// is complete (<see cref="MetadataRefreshService"/>) - and a failed attempt after a day. The one place a later
/// dynamic refresh cadence plugs in.
/// </summary>
public static class CompanionSchedule
{
    public static readonly TimeSpan FailedRetry = TimeSpan.FromDays(1);

    /// <summary>The next regular check of companion data for the series record <paramref name="series"/>.</summary>
    public static DateTimeOffset NextCheck(MetadataRecordEntity series, DateTimeOffset now) =>
        now + (series.OriginStatus is (int)MetadataOriginStatus.Complete or (int)MetadataOriginStatus.Cancelled
            ? MetadataRefreshService.CompleteAge
            : MetadataRefreshService.OngoingAge);

    public static DateTimeOffset AfterFailure(DateTimeOffset now) => now + FailedRetry;
}
