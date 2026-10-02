namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The stored observations of linked series records (1.32.0, <c>metadata_record_observations</c>): what the refresh saw of the
/// latest chapter, the volume count and the status, written only when they differ from the record's LAST observation (so a
/// change made by Identify or an admin's refresh is still caught by the next background refresh), at most
/// <see cref="MaxPerRecord"/> per record. The publishing pace of <see cref="RefreshCadence"/> comes from them - no extra request.
/// </summary>
public static class RefreshObservations
{
    public const int MaxPerRecord = 24;

    /// <summary>True when the record's values differ from <paramref name="last"/> (or there is none).</summary>
    public static bool Differs(MetadataRecordObservationEntity? last, double? latestChapter, int? originVolumes, int? originStatus) =>
        last is null || last.LatestChapter != latestChapter || last.OriginVolumes != originVolumes || last.OriginStatus != originStatus;

    /// <summary>Writes an observation of <paramref name="record"/> at <paramref name="at"/> when it differs from the last one; prunes the oldest.</summary>
    public static async Task<bool> NoteAsync(MangaPixerDbContext db, MetadataRecordEntity record, DateTimeOffset at, CancellationToken ct)
    {
        var rows = await db.MetadataRecordObservations
            .Where(o => o.RecordId == record.Id)
            .OrderBy(o => o.ObservedAt).ThenBy(o => o.Id)
            .ToListAsync(ct);
        if (!Differs(rows.LastOrDefault(), record.LatestChapter, record.OriginVolumes, record.OriginStatus))
            return false;
        db.MetadataRecordObservations.Add(new MetadataRecordObservationEntity
        {
            RecordId = record.Id,
            ObservedAt = at,
            LatestChapter = record.LatestChapter,
            OriginVolumes = record.OriginVolumes,
            OriginStatus = record.OriginStatus,
        });
        var excess = rows.Count + 1 - MaxPerRecord;
        if (excess > 0)
            db.MetadataRecordObservations.RemoveRange(rows.Take(excess));
        await db.SaveChangesAsync(ct);
        return true;
    }

    public static RefreshObservation ToCore(MetadataRecordObservationEntity row) => new(row.ObservedAt, row.LatestChapter, row.OriginVolumes);
}
