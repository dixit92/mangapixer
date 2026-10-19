namespace com.lifepixer.mangapixer.Server.Features.Metadata.Freshness;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// What follows a refresh of a linked record - the background pass and (1.45.0, MangaList's recommendation 6) an admin's Refresh alike:
/// the observation (<see cref="RefreshObservations"/>, so the chapter pace sees the change when it happened), the record's cadence, the
/// end of a "due now" mark, and (recommendation 5) a rise of the latest chapter makes the series' MangaDex volume list due now. No request.
/// </summary>
public static class RefreshFollowUp
{
    /// <summary>The admin's cadence choice (stored columns; the default without a row).</summary>
    public static async Task<RefreshCadencePolicy> PolicyAsync(MangaPixerDbContext db, CancellationToken ct)
    {
        var row = await db.AppSettings.AsNoTracking()
            .Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => new { s.MetadataRefreshOngoingDays, s.MetadataRefreshFinishedDays, s.MetadataRefreshFollowPace, s.MetadataRefreshPaceSource })
            .FirstOrDefaultAsync(ct);
        return row is null
            ? RefreshCadencePolicy.Default
            : RefreshCadencePolicy.FromStored(row.MetadataRefreshOngoingDays, row.MetadataRefreshFinishedDays, row.MetadataRefreshFollowPace,
                row.MetadataRefreshPaceSource);
    }

    /// <summary>What the cadence of one record is computed from (its observations, oldest first, the volume map's chapters a volume, reading).</summary>
    public static async Task<RefreshEvidence> EvidenceAsync(MangaPixerDbContext db, MetadataRecordEntity record, DateTimeOffset now, CancellationToken ct)
    {
        var history = await db.MetadataRecordObservations.AsNoTracking()
            .Where(o => o.RecordId == record.Id)
            .OrderBy(o => o.ObservedAt).ThenBy(o => o.Id)
            .ToListAsync(ct);
        var perVolume = await db.SeriesVolumeMaps.AsNoTracking()
            .Where(m => m.RecordId == record.Id && m.ChaptersPerVolume != null)
            .OrderBy(m => m.Source)
            .Select(m => m.ChaptersPerVolume)
            .FirstOrDefaultAsync(ct);
        var reading = (await ReadingSeries.RecordIdsAsync(db, now, [record.Id], ct)).Contains(record.Id);
        return new RefreshEvidence(record.OriginStatus, record.StartYear, record.OriginVolumes, record.LatestChapter, perVolume,
            history.Select(RefreshObservations.ToCore).ToList(), reading, record.TranslationComplete);
    }

    /// <summary>
    /// After <paramref name="record"/> was fetched at <paramref name="at"/> (it is saved; <paramref name="latestBefore"/> = its latest chapter
    /// before): the observation, the cadence, the end of its nudge, and the cross-trigger. Returns the record's cadence in days.
    /// </summary>
    public static async Task<int> AfterRefreshAsync(MangaPixerDbContext db, MetadataRecordEntity record, double? latestBefore, DateTimeOffset at,
        ILogger logger, CancellationToken ct)
    {
        await RefreshObservations.NoteAsync(db, record, at, ct);
        var days = RefreshCadence.For(await PolicyAsync(db, ct), await EvidenceAsync(db, record, at, ct), at).Days;
        if (record.RefreshCadenceDays != days)
        {
            record.RefreshCadenceDays = days;
            await db.SaveChangesAsync(ct);
        }
        await ClearNudgeAsync(db, record.Id, ct);
        if (latestBefore is { } before && record.LatestChapter is { } after && after > before)
        {
            var aggregate = (int)VolumeMapSource.MangaDexAggregate;
            var made = await db.SeriesVolumeMaps
                .Where(m => m.RecordId == record.Id && m.Source == aggregate && m.NextCheckAt != null && m.NextCheckAt > at)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.NextCheckAt, at), ct);
            if (made > 0)
                logger.LogInformation(LogEvents.Metadata.CrossTrigger, "Record {RecordId}: latest chapter rose, its MangaDex volume list is due now", record.Id);
        }
        return days;
    }

    /// <summary>A "due now" mark ends with the record's refresh (or when it failed - the regular cadence takes over).</summary>
    public static Task ClearNudgeAsync(MangaPixerDbContext db, long recordId, CancellationToken ct) =>
        db.MetadataRefreshNudges.Where(n => n.RecordId == recordId).ExecuteDeleteAsync(ct);
}
