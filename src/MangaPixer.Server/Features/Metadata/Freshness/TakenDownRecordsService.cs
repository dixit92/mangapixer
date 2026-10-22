namespace com.lifepixer.mangapixer.Server.Features.Metadata.Freshness;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// 1.47.0 (owner decision 10a, 2026-10-22): what the MangaDex latest-upload sweep already knows about records MangaDex left out of its
/// answer (<c>MissingSince</c> / <c>MissingCount</c>, written by <see cref="LatestUploadSweep"/>), for the Metadata Manager's Runs tab.
/// Stored rows only - no request of any kind, and no lookup elsewhere for a replacement (that stays gated). A record counts only while a
/// Confirmed / Auto linked series in a metadata-enabled library still uses it as its MangaDex companion (a stale row of an unlinked series
/// is not shown). Admin-only through the controller; logs ids and counts.
/// </summary>
public sealed class TakenDownRecordsService(MangaPixerDbContext db, ILogger<TakenDownRecordsService> logger)
{
    /// <summary>The most records returned.</summary>
    public const int Limit = 200;

    /// <summary>The most series named per record.</summary>
    public const int SeriesPerRecord = 3;

    public async Task<TakenDownRecordsDto> ListAsync(CancellationToken ct = default)
    {
        var rows = await (
            from u in db.MangaDexLatestUploads.AsNoTracking()
            where u.MissingSince != null
            join md in db.MetadataRecords.AsNoTracking() on u.RecordId equals md.Id
            select new { u.RecordId, md.PublicId, md.Title, u.MissingSince, u.MissingCount, u.CheckedAt }).ToListAsync(ct);
        if (rows.Count == 0)
            return new TakenDownRecordsDto { Total = 0, Limit = Limit, Items = [] };

        var confirmed = (int)SeriesLinkState.Confirmed;
        var auto = (int)SeriesLinkState.Auto;
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var found = new[] { (int)CompanionState.Auto, (int)CompanionState.Confirmed };
        var mangaDexId = MetadataProviderAllowlist.MangaDex;
        var ids = rows.Select(r => r.RecordId).ToList();
        var links = await (
            from c in db.MetadataCompanions.AsNoTracking()
            where c.Provider == mangaDexId && found.Contains(c.State) && c.CompanionRecordId != null && ids.Contains(c.CompanionRecordId.Value)
            join l in db.NodeSeriesLinks.AsNoTracking() on c.RecordId equals l.RecordId
            where l.State == confirmed || l.State == auto
            join n in db.CatalogNodes.AsNoTracking() on l.NodeId equals n.Id
            where n.Availability != tombstoned
            join lib in db.Libraries.AsNoTracking() on n.LibraryId equals lib.Id
            where lib.MetadataEnabled
            select new { Record = c.CompanionRecordId!.Value, NodeId = n.PublicId, LibraryId = lib.PublicId, n.DisplayName }).ToListAsync(ct);
        var byRecord = links.GroupBy(l => l.Record).ToDictionary(g => g.Key,
            g => g.DistinctBy(l => l.NodeId).OrderBy(l => l.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.NodeId, StringComparer.Ordinal).ToList());

        var current = rows.Where(r => byRecord.ContainsKey(r.RecordId))
            .OrderBy(r => r.MissingSince!.Value.UtcTicks).ThenBy(r => r.RecordId)
            .ToList();
        var items = current.Take(Limit).Select(r =>
        {
            var series = byRecord[r.RecordId];
            return new TakenDownRecordDto
            {
                RecordId = r.PublicId,
                Title = r.Title,
                Since = r.MissingSince!.Value,
                LastSeen = r.CheckedAt,
                Sweeps = r.MissingCount,
                Series = series.Take(SeriesPerRecord).Select(s => new TakenDownSeriesDto { NodeId = s.NodeId, LibraryId = s.LibraryId, DisplayName = s.DisplayName }).ToList(),
                MoreSeries = Math.Max(0, series.Count - SeriesPerRecord),
            };
        }).ToList();
        logger.LogInformation(LogEvents.Metadata.TakenDownListed, "Taken-down MangaDex records listed: {Total} records, {Shown} shown", current.Count, items.Count);
        return new TakenDownRecordsDto { Total = current.Count, Limit = Limit, Items = items };
    }
}
