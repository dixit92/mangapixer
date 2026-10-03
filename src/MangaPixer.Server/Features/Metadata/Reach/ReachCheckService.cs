namespace com.lifepixer.mangapixer.Server.Features.Metadata.Reach;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Metadata.Reach;
using com.lifepixer.mangapixer.Server.Features.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Re-checks the AUTOMATIC links of a record against each folder's reach when new data about the record arrives (1.30.0; the
/// integrator's decision on the reach design): its volume list stored or changed, or the record refreshed. A volume or chapter file
/// far past every total known for the record (<see cref="ReachConflictKind.VolumeOverrun"/> / <see cref="ReachConflictKind.ChapterOverrun"/>)
/// moves the link back to Needs review with the record as its only candidate and the <see cref="MatchReason.ReachConflict"/> chip;
/// chapter files whose stated volumes disagree with the volume list (<see cref="ReachConflictKind.StructureClash"/>) only add the
/// chip to the Auto-linked item. Confirmed links are never touched. Stored rows only - never a request. Logs ids, counts and the
/// conflict kind, never names or titles.
/// </summary>
public sealed class ReachCheckService(MangaPixerDbContext db, TimeProvider time, ILogger<ReachCheckService> logger)
{
    /// <summary>Checks every Auto-linked series folder of a record; returns how many links it changed or flagged.</summary>
    public async Task<int> CheckRecordAsync(long recordId, CancellationToken ct = default)
    {
        var auto = (int)SeriesLinkState.Auto;
        var folder = (int)CatalogNodeKind.Folder;
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var links = await (
            from l in db.NodeSeriesLinks
            where l.RecordId == recordId && l.State == auto
            join n in db.CatalogNodes on l.NodeId equals n.Id
            where n.Kind == folder && n.Availability != tombstoned
            select l).ToListAsync(ct);
        if (links.Count == 0)
            return 0;
        var record = await db.MetadataRecords.FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null)
            return 0;

        var maps = await db.SeriesVolumeMaps.AsNoTracking().Where(m => m.RecordId == recordId).ToListAsync(ct);
        var row = new SeriesProgressLoader.RecordRow(record.Id, record.Origin, record.OriginStatus, record.OriginVolumes, record.StatusText,
            record.LatestChapter, record.PublishersJson, record.LicensedEn, record.TranslationComplete);
        var (map, facts) = SeriesProgressLoader.MapAndFacts(maps, row, ReleasedInLanguage.DefaultLanguage);
        var official = ReleasedInLanguage.OfficialOf(ReleasedInLanguage.DefaultLanguage, record.PublishersJson);
        var listed = map.Volumes.SelectMany(v => v.Chapters).Where(c => decimal.Truncate(c) == c && c > 0).Select(c => (int)c).DefaultIfEmpty().Max();
        var evidence = new ReachEvidence(
            record.OriginVolumes,
            map.KnownVolumeCount,
            official?.Volumes,
            record.LatestChapter is { } latest && latest >= 1 ? (int)Math.Floor(latest) : null,
            facts.OriginChapters,
            listed > 0 ? listed : null,
            official?.Chapters);

        var progress = await new SeriesProgressLoader(db).LoadAsync(links.Select(l => new SeriesProgressTarget(l.NodeId, recordId)).ToList(), ct);
        var now = time.GetUtcNow();
        var changed = 0;
        foreach (var link in links)
        {
            if (!progress.TryGetValue(link.NodeId, out var entry) || entry.Result.Restarts)
                continue;
            var conflict = ReachContradiction.Check(entry.Rows, map, evidence);
            if (conflict.Kind == ReachConflictKind.None)
                continue;
            var queue = await db.MetadataMatchQueue.FirstOrDefaultAsync(q => q.NodeId == link.NodeId, ct);
            if (!conflict.Demotes && queue is not null && ((MatchReason)queue.OutcomeReasons).HasFlag(MatchReason.ReachConflict))
                continue; // already flagged
            if (queue is not null)
                queue.OutcomeReasons |= (int)MatchReason.ReachConflict;
            if (conflict.Demotes)
                Demote(link, record, queue, now);
            else
                link.UpdatedAt = now;
            changed++;
            logger.LogInformation(LogEvents.Metadata.ReachConflict,
                "Reach check: node {NodeId} record {RecordId}: {Kind} ({Local} vs {Published}), {Action}",
                link.NodeId, recordId, conflict.Kind, conflict.Local, conflict.Published, conflict.Demotes ? "moved to review" : "flagged");
        }
        if (changed > 0)
            await db.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>The same shape as the matcher's review outcome: no record, the record kept as the one candidate with the chip.</summary>
    private void Demote(NodeSeriesLinkEntity link, MetadataRecordEntity record, MetadataMatchQueueEntity? queue, DateTimeOffset now)
    {
        var score = Math.Round(Math.Clamp(link.MatchScore ?? 0, 0, 1), 4);
        link.State = (int)SeriesLinkState.NeedsReview;
        link.RecordId = null;
        link.UpdatedAt = now;
        link.LaterAt = null;
        db.MetadataMatchCandidates.RemoveRange(db.MetadataMatchCandidates.Where(c => c.NodeId == link.NodeId));
        db.MetadataMatchCandidates.Add(new MetadataMatchCandidateEntity
        {
            NodeId = link.NodeId,
            Rank = 1,
            Provider = record.Provider,
            ExternalId = record.ExternalId,
            Title = record.Title.Length <= 512 ? record.Title : record.Title[..512],
            ProviderType = record.ProviderType,
            Format = record.Format,
            Origin = record.Origin,
            Year = record.StartYear,
            Volumes = record.OriginVolumes,
            TitleScore = score,
            AdjustedScore = score,
            Reasons = (int)MatchReason.ReachConflict,
            ImageRemoteUrl = record.ImageRemoteUrl,
            CreatedAt = now,
        });
        if (queue is not null)
            queue.Outcome = (int)MatchBand.NeedsReview;
    }

    /// <summary>
    /// The hook of the places that store new data about a record (the volume list store; the record refresh): never lets a failed
    /// check break the store itself.
    /// </summary>
    public async Task TryCheckRecordAsync(long recordId, CancellationToken ct)
    {
        try
        {
            await CheckRecordAsync(recordId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(LogEvents.Metadata.ReachCheckFailed, "Reach check of record {RecordId} failed: {Error}", recordId, ex.GetType().Name);
        }
    }
}
