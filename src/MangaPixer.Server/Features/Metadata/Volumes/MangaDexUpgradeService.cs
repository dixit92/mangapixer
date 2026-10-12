namespace com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// "Switch to MangaUpdates..." (1.42.0, owner: suggest, never switch on its own). A series linked to a MangaDex record whose own
/// MangaUpdates link now names a series moves to that MangaUpdates record: every node link of the MangaDex record moves, keeping its state
/// (a confirmation stays a confirmation) and method; the MangaDex record becomes the MangaUpdates record's MangaDex companion (its own link
/// names it), and its volume lists and AniList companion move along - nothing is fetched again. The only request is the MangaUpdates
/// record by its id when it is not stored yet (an admin action, the existing kind). Logs ids only.
/// </summary>
public sealed class MangaDexUpgradeService
{
    private readonly MangaPixerDbContext _db;
    private readonly MetadataIdentifyService _identify;
    private readonly AuditService _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<MangaDexUpgradeService> _logger;

    public MangaDexUpgradeService(
        MangaPixerDbContext db, MetadataIdentifyService identify, AuditService audit, TimeProvider time, ILogger<MangaDexUpgradeService> logger)
    {
        _db = db;
        _identify = identify;
        _audit = audit;
        _time = time;
        _logger = logger;
    }

    /// <summary>The audit result of a switch (action <see cref="AuditActions.MetadataLink"/>).</summary>
    public const string AuditResult = "switch_mangaupdates";

    /// <summary>
    /// Switches the series the node's own link names. Error <c>not_found</c> (no live node, or its own link is not to a MangaDex record)
    /// or <c>not_listed</c> (the MangaDex record names no MangaUpdates series). Gateway refusals propagate.
    /// </summary>
    public async Task<(string? Error, NodeSeriesLinkChangeDto? Change)> SwitchAsync(string nodePublicId, string? actor, CancellationToken ct = default)
    {
        var node = await _db.CatalogNodes.AsNoTracking()
            .FirstOrDefaultAsync(n => n.PublicId == nodePublicId && n.Availability != (int)CatalogNodeAvailability.Tombstoned, ct);
        if (node is null)
            return ("not_found", null);
        var own = await _db.NodeSeriesLinks.AsNoTracking().FirstOrDefaultAsync(l => l.NodeId == node.Id, ct);
        var mangaDex = own?.RecordId is { } recordId ? await _db.MetadataRecords.FirstOrDefaultAsync(r => r.Id == recordId, ct) : null;
        if (own is null || mangaDex is null || !CompanionLinkService.IsMangaDexSeries(mangaDex)
            || own.State is not ((int)SeriesLinkState.Confirmed or (int)SeriesLinkState.Auto))
            return ("not_found", null);
        if (MetadataIdentifyService.MangaUpdatesIdOf(mangaDex) is not { } muId)
            return ("not_listed", null);

        var mu = await _identify.EnsureStoredRecordAsync(MetadataProviderAllowlist.MangaUpdates, muId, node.LibraryId, ct);
        var now = _time.GetUtcNow();
        int moved;
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            // Every node link of the MangaDex record moves (one series), keeping its state and method.
            moved = await _db.NodeSeriesLinks.Where(l => l.RecordId == mangaDex.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.RecordId, mu.Id).SetProperty(l => l.UpdatedAt, now), ct);

            // Its volume lists and AniList companion move along unless the MangaUpdates series has its own.
            foreach (var map in await _db.SeriesVolumeMaps.Where(m => m.RecordId == mangaDex.Id).ToListAsync(ct))
            {
                if (await _db.SeriesVolumeMaps.AnyAsync(m => m.RecordId == mu.Id && m.Source == map.Source, ct))
                    _db.SeriesVolumeMaps.Remove(map);
                else
                    map.RecordId = mu.Id;
            }
            var aniList = await _db.MetadataCompanions.FirstOrDefaultAsync(c => c.RecordId == mangaDex.Id && c.Provider == MetadataProviderAllowlist.AniList, ct);
            if (aniList is not null)
            {
                if (await _db.MetadataCompanions.AnyAsync(c => c.RecordId == mu.Id && c.Provider == MetadataProviderAllowlist.AniList, ct))
                    _db.MetadataCompanions.Remove(aniList);
                else
                    aniList.RecordId = mu.Id;
            }

            // The MangaDex record becomes the MangaUpdates series' companion (its own link names it) - unless that series already has one.
            var self = await _db.MetadataCompanions.FirstOrDefaultAsync(c => c.RecordId == mangaDex.Id && c.Provider == CompanionLinkService.MangaDex, ct);
            var existing = await _db.MetadataCompanions.FirstOrDefaultAsync(c => c.RecordId == mu.Id && c.Provider == CompanionLinkService.MangaDex, ct);
            var takeOver = existing is null;
            if (existing is null)
            {
                existing = self ?? new MetadataCompanionEntity { Provider = CompanionLinkService.MangaDex };
                if (self is null)
                    _db.MetadataCompanions.Add(existing);
                self = null;
                existing.RecordId = mu.Id;
            }
            if (takeOver || existing.CompanionRecordId is null || existing.State is (int)CompanionState.NotFound or (int)CompanionState.Failed)
            {
                existing.CompanionRecordId = mangaDex.Id;
                existing.State = (int)CompanionState.Auto;
                existing.Method = (int)CompanionMethod.CrossLink;
                existing.CheckedAt = now;
                existing.NextCheckAt = null; // Read again by id on the next pass (the cross-link rule checks its link).
            }
            if (self is not null)
                _db.MetadataCompanions.Remove(self);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        if (mu.ImageState != 1)
            await _identify.TryStoreImageAsync(mu, node.LibraryId, ct);
        await _audit.RecordAsync(AuditActions.MetadataLink, AuditResult, actor, ct: ct, targetLibraryId: node.LibraryId, targetItemId: node.Id);
        _logger.LogInformation(LogEvents.Metadata.MangaDexSwitched,
            "MangaDex record {MangaDexRecordId} switched to MangaUpdates record {RecordId}: {Links} links moved", mangaDex.Id, mu.Id, moved);

        return (null, new NodeSeriesLinkChangeDto
        {
            NodeId = node.PublicId,
            Previous = new NodeSeriesLinkDto
            {
                NodeId = node.PublicId,
                State = (SeriesLinkState)own.State,
                Provider = mangaDex.Provider,
                ExternalId = mangaDex.ExternalId,
                RecordId = mangaDex.PublicId,
                MatchMethod = (MetadataMatchMethod?)own.MatchMethod,
                UpdatedAt = own.UpdatedAt,
            },
            Link = new NodeSeriesLinkDto
            {
                NodeId = node.PublicId,
                State = (SeriesLinkState)own.State,
                Provider = mu.Provider,
                ExternalId = mu.ExternalId,
                RecordId = mu.PublicId,
                MatchMethod = (MetadataMatchMethod?)own.MatchMethod,
                UpdatedAt = now,
            },
        });
    }
}
