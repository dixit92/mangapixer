namespace com.lifepixer.mangapixer.Server.Features.Metadata.Freshness;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The series someone is reading (1.45.0, the reading fast lane of <see cref="RefreshCadence"/>): a record linked Confirmed / Auto from a
/// node that holds a read mark of the last <see cref="RefreshCadence.ReadingWindowDays"/> days (the node itself, or an archive up to
/// <see cref="MaxDepth"/> folders below it), or a tracker entry whose status is Reading (as the tracker last said, or as MangaPixer last
/// wrote it). Stored rows only; which user read is not kept.
/// </summary>
public static class ReadingSeries
{
    /// <summary>Unit subfolders followed below a linked folder (as the volume-cover pass does).</summary>
    public const int MaxDepth = 4;

    /// <summary>The record ids being read at <paramref name="now"/> (of <paramref name="recordIds"/> when given).</summary>
    public static async Task<HashSet<long>> RecordIdsAsync(MangaPixerDbContext db, DateTimeOffset now, IReadOnlyCollection<long>? recordIds,
        CancellationToken ct)
    {
        var since = now - TimeSpan.FromDays(RefreshCadence.ReadingWindowDays);
        var reading = (int)TrackerEntryStatus.Reading;
        var entries = db.TrackerEntries.AsNoTracking()
            .Where(e => e.RemoteStatus == reading || (e.RemoteStatus == null && e.LastSentStatus == reading));
        if (recordIds is not null)
            entries = entries.Where(e => recordIds.Contains(e.RecordId));
        var result = (await entries.Select(e => e.RecordId).Distinct().ToListAsync(ct)).ToHashSet();

        var marked = await db.ReadMarks.AsNoTracking().Where(m => m.MarkedAt >= since).Select(m => m.ItemId).Distinct().ToListAsync(ct);
        if (marked.Count == 0)
            return result;
        // The read archives and the folders above them (bounded).
        var nodes = new HashSet<long>(marked);
        var frontier = marked;
        for (var depth = 0; depth < MaxDepth && frontier.Count > 0; depth++)
        {
            var batch = frontier;
            var parents = await db.CatalogNodes.AsNoTracking()
                .Where(n => batch.Contains(n.Id) && n.ParentId != null)
                .Select(n => n.ParentId!.Value)
                .Distinct()
                .ToListAsync(ct);
            frontier = parents.Where(nodes.Add).ToList();
        }
        var confirmed = (int)SeriesLinkState.Confirmed;
        var auto = (int)SeriesLinkState.Auto;
        var nodeIds = nodes.ToList();
        var linked = await db.NodeSeriesLinks.AsNoTracking()
            .Where(l => (l.State == confirmed || l.State == auto) && l.RecordId != null && nodeIds.Contains(l.NodeId))
            .Select(l => l.RecordId!.Value)
            .Distinct()
            .ToListAsync(ct);
        result.UnionWith(recordIds is null ? linked : linked.Where(recordIds.Contains));
        return result;
    }
}
