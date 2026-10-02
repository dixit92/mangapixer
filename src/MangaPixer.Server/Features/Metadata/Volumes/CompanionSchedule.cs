namespace com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence.Entities;

/// <summary>
/// When companion data (the MangaDex record, its volume list and cover list, the AniList totals) is looked at again
/// (1.29.0): on the linked series record's refresh cadence - since 1.32.0 the admin's choice and the series' publishing
/// pace, stored per record by <see cref="MetadataRefreshService"/> (<c>RefreshCadenceDays</c>; before it is computed,
/// 30 days while ongoing, 90 once finished) - and a failed attempt after a day.
/// </summary>
public static class CompanionSchedule
{
    public static readonly TimeSpan FailedRetry = TimeSpan.FromDays(1);

    /// <summary>The next regular check of companion data for the series record <paramref name="series"/>.</summary>
    public static DateTimeOffset NextCheck(MetadataRecordEntity series, DateTimeOffset now) =>
        now + RefreshCadence.AgeFor(series.RefreshCadenceDays, series.OriginStatus);

    public static DateTimeOffset AfterFailure(DateTimeOffset now) => now + FailedRetry;
}
