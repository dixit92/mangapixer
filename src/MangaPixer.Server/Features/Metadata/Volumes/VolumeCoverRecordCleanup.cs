namespace com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;

using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// After records were removed (a purge, "Delete fetched data", an unlink that left a record unused) - 1.29.0: MangaDex
/// records that no series uses any more are removed too, and stored volume-cover files that no row references (their
/// rows went with a removed record) are deleted, so nothing fetched outlives the data it belonged to. Local only.
/// </summary>
public sealed class VolumeCoverRecordCleanup(MangaPixerDbContext db, VolumeCoverStore store, ILogger<VolumeCoverRecordCleanup> logger)
    : IMetadataRecordRemovedHandler
{
    public async Task OnRecordsRemovedAsync(IReadOnlyList<long> recordIds, CancellationToken ct)
    {
        var unused = await db.MetadataRecords
            .Where(r => r.Provider == MetadataProviderAllowlist.MangaDex && !db.MetadataCompanions.Any(c => c.CompanionRecordId == r.Id))
            .Select(r => r.Id)
            .ToListAsync(ct);
        if (unused.Count > 0)
            await db.MetadataRecords.Where(r => unused.Contains(r.Id)).ExecuteDeleteAsync(ct);

        var kept = (await db.VolumeCovers.AsNoTracking()
                .Where(c => c.StoredVersion > 0)
                .Select(c => new { c.PublicId, c.StoredVersion })
                .ToListAsync(ct))
            .Select(c => c.PublicId + "-" + c.StoredVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.Ordinal);
        var files = store.DeleteUnreferenced(kept);
        if (unused.Count > 0 || files > 0)
            logger.LogInformation(LogEvents.Metadata.VolumeCoversDeleted, "Volume covers of removed records deleted: {Records} records, {Files} files",
                unused.Count, files);
    }
}
