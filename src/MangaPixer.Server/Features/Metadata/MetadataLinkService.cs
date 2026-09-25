namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Called after metadata records were deleted (last link gone, or purge). Lane B2
/// registers its image store here to delete the records' stored posters; B1 has no
/// images, so nothing is registered by default.
/// </summary>
public interface IMetadataRecordRemovedHandler
{
    Task OnRecordsRemovedAsync(IReadOnlyList<long> recordIds, CancellationToken ct);
}

/// <summary>Outcome code of a link-service operation.</summary>
public enum MetadataLinkResultCode
{
    Ok = 0,
    NodeNotFound = 1,
    LibraryNotFound = 2,
    InvalidRequest = 3,
    RecordNotFound = 4,
    NotAFolder = 5,
}

/// <summary>
/// Admin-only writes of series links and source precedence (1.24.0). NO network:
/// a link can only point at a record that already exists in <c>metadata_records</c>
/// (lane B2 fetches and stores records first). Enforces the link invariants
/// (RecordId set iff Confirmed; one row per node), deletes a record when its last
/// link goes, and audits every change with ids only.
/// </summary>
public sealed class MetadataLinkService
{
    private readonly MangaPixerDbContext _db;
    private readonly AuditService _audit;
    private readonly IEnumerable<IMetadataRecordRemovedHandler> _removedHandlers;
    private readonly TimeProvider _time;
    private readonly ILogger<MetadataLinkService> _logger;

    public MetadataLinkService(
        MangaPixerDbContext db,
        AuditService audit,
        IEnumerable<IMetadataRecordRemovedHandler> removedHandlers,
        TimeProvider time,
        ILogger<MetadataLinkService> logger)
    {
        _db = db;
        _audit = audit;
        _removedHandlers = removedHandlers;
        _time = time;
        _logger = logger;
    }

    /// <summary>Links a node (folder or archive) to an existing record, replacing any row it had.</summary>
    public async Task<(MetadataLinkResultCode Code, NodeSeriesLinkChangeDto? Change)> LinkAsync(
        string nodePublicId, LinkSeriesRequest request, string? actor, CancellationToken ct = default)
    {
        if (!MetadataIdentifiers.IsValidProvider(request.Provider) || !MetadataIdentifiers.IsValidExternalId(request.ExternalId))
            return (MetadataLinkResultCode.InvalidRequest, null);

        var node = await _db.CatalogNodes.FirstOrDefaultAsync(n => n.PublicId == nodePublicId, ct);
        if (node is null)
            return (MetadataLinkResultCode.NodeNotFound, null);

        var record = await _db.MetadataRecords
            .FirstOrDefaultAsync(r => r.Provider == request.Provider && r.ExternalId == request.ExternalId, ct);
        if (record is null)
            return (MetadataLinkResultCode.RecordNotFound, null);

        var existing = await _db.NodeSeriesLinks.FirstOrDefaultAsync(l => l.NodeId == node.Id, ct);
        var previous = existing is null ? null : await ToDtoAsync(existing, node.PublicId, ct);
        var now = _time.GetUtcNow();
        var previousRecordId = existing?.RecordId;
        var action = existing is { State: (int)SeriesLinkState.Confirmed } && existing.RecordId != record.Id
            ? AuditActions.MetadataRelink
            : AuditActions.MetadataLink;

        if (existing is null)
        {
            existing = new NodeSeriesLinkEntity { NodeId = node.Id, LibraryId = node.LibraryId, CreatedAt = now };
            _db.NodeSeriesLinks.Add(existing);
        }
        existing.State = (int)SeriesLinkState.Confirmed;
        existing.RecordId = record.Id;
        existing.MatchMethod = (int)(request.MatchMethod ?? MetadataMatchMethod.Reference);
        existing.MatchScore = request.MatchScore is { } score ? Math.Clamp(score, 0, 1) : null;
        existing.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        if (previousRecordId is { } oldRecord && oldRecord != record.Id)
            await DeleteOrphanRecordsAsync([oldRecord], ct);

        await _audit.RecordAsync(action, AuditResults.Success, actor, ct: ct, targetLibraryId: node.LibraryId, targetItemId: node.Id);
        _logger.LogInformation(LogEvents.Metadata.SeriesLinkChanged, "Series link set on node {NodeId} (record {RecordId})", node.Id, record.Id);

        return (MetadataLinkResultCode.Ok, new NodeSeriesLinkChangeDto
        {
            NodeId = node.PublicId,
            Link = await ToDtoAsync(existing, node.PublicId, ct),
            Previous = previous,
        });
    }

    /// <summary>Marks a node "Don't match" (stops inheritance), replacing any link it had.</summary>
    public async Task<(MetadataLinkResultCode Code, NodeSeriesLinkChangeDto? Change)> SetDontMatchAsync(
        string nodePublicId, string? actor, CancellationToken ct = default)
    {
        var node = await _db.CatalogNodes.FirstOrDefaultAsync(n => n.PublicId == nodePublicId, ct);
        if (node is null)
            return (MetadataLinkResultCode.NodeNotFound, null);

        var existing = await _db.NodeSeriesLinks.FirstOrDefaultAsync(l => l.NodeId == node.Id, ct);
        var previous = existing is null ? null : await ToDtoAsync(existing, node.PublicId, ct);
        var previousRecordId = existing?.RecordId;
        var now = _time.GetUtcNow();

        if (existing is null)
        {
            existing = new NodeSeriesLinkEntity { NodeId = node.Id, LibraryId = node.LibraryId, CreatedAt = now };
            _db.NodeSeriesLinks.Add(existing);
        }
        existing.State = (int)SeriesLinkState.DontMatch;
        existing.RecordId = null;
        existing.MatchMethod = null;
        existing.MatchScore = null;
        existing.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        if (previousRecordId is { } oldRecord)
            await DeleteOrphanRecordsAsync([oldRecord], ct);

        await _audit.RecordAsync(AuditActions.MetadataDontMatch, AuditResults.Success, actor, ct: ct, targetLibraryId: node.LibraryId, targetItemId: node.Id);
        _logger.LogInformation(LogEvents.Metadata.SeriesLinkChanged, "Series link set to dont_match on node {NodeId}", node.Id);

        return (MetadataLinkResultCode.Ok, new NodeSeriesLinkChangeDto
        {
            NodeId = node.PublicId,
            Link = await ToDtoAsync(existing, node.PublicId, ct),
            Previous = previous,
        });
    }

    /// <summary>
    /// Removes the node's own link row. With <paramref name="onlyDontMatch"/> only a
    /// "Don't match" row is removed (a confirmed link is left alone). Inheritance
    /// from ancestors resumes. Idempotent.
    /// </summary>
    public async Task<(MetadataLinkResultCode Code, NodeSeriesLinkChangeDto? Change)> RemoveAsync(
        string nodePublicId, bool onlyDontMatch, string? actor, CancellationToken ct = default)
    {
        var node = await _db.CatalogNodes.FirstOrDefaultAsync(n => n.PublicId == nodePublicId, ct);
        if (node is null)
            return (MetadataLinkResultCode.NodeNotFound, null);

        var existing = await _db.NodeSeriesLinks.FirstOrDefaultAsync(l => l.NodeId == node.Id, ct);
        if (existing is null || (onlyDontMatch && existing.State != (int)SeriesLinkState.DontMatch))
            return (MetadataLinkResultCode.Ok, new NodeSeriesLinkChangeDto { NodeId = node.PublicId });

        var previous = await ToDtoAsync(existing, node.PublicId, ct);
        var recordId = existing.RecordId;
        _db.NodeSeriesLinks.Remove(existing);
        await _db.SaveChangesAsync(ct);
        if (recordId is { } id)
            await DeleteOrphanRecordsAsync([id], ct);

        await _audit.RecordAsync(AuditActions.MetadataUnlink, AuditResults.Success, actor, ct: ct, targetLibraryId: node.LibraryId, targetItemId: node.Id);
        _logger.LogInformation(LogEvents.Metadata.SeriesLinkChanged, "Series link removed from node {NodeId}", node.Id);
        return (MetadataLinkResultCode.Ok, new NodeSeriesLinkChangeDto { NodeId = node.PublicId, Previous = previous });
    }

    /// <summary>Sets a folder's precedence override (folders only).</summary>
    public async Task<MetadataLinkResultCode> SetFolderPrecedenceAsync(
        string nodePublicId, MetadataPrecedence precedence, string? actor, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(precedence))
            return MetadataLinkResultCode.InvalidRequest;
        var node = await _db.CatalogNodes.FirstOrDefaultAsync(n => n.PublicId == nodePublicId, ct);
        if (node is null)
            return MetadataLinkResultCode.NodeNotFound;
        if (node.Kind != (int)CatalogNodeKind.Folder)
            return MetadataLinkResultCode.NotAFolder;

        var existing = await _db.FolderMetadataPrecedences.FirstOrDefaultAsync(f => f.NodeId == node.Id, ct);
        if (existing is null)
            _db.FolderMetadataPrecedences.Add(new FolderMetadataPrecedenceEntity { NodeId = node.Id, Precedence = (int)precedence });
        else
            existing.Precedence = (int)precedence;
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync(AuditActions.MetadataPrecedenceSet, AuditResults.Success, actor, ct: ct, targetLibraryId: node.LibraryId, targetItemId: node.Id);
        _logger.LogInformation(LogEvents.Metadata.PrecedenceChanged, "Metadata precedence set on folder {NodeId}", node.Id);
        return MetadataLinkResultCode.Ok;
    }

    /// <summary>Clears a folder's precedence override (idempotent).</summary>
    public async Task<MetadataLinkResultCode> ClearFolderPrecedenceAsync(string nodePublicId, string? actor, CancellationToken ct = default)
    {
        var node = await _db.CatalogNodes.FirstOrDefaultAsync(n => n.PublicId == nodePublicId, ct);
        if (node is null)
            return MetadataLinkResultCode.NodeNotFound;

        var existing = await _db.FolderMetadataPrecedences.FirstOrDefaultAsync(f => f.NodeId == node.Id, ct);
        if (existing is not null)
        {
            _db.FolderMetadataPrecedences.Remove(existing);
            await _db.SaveChangesAsync(ct);
            await _audit.RecordAsync(AuditActions.MetadataPrecedenceClear, AuditResults.Success, actor, ct: ct, targetLibraryId: node.LibraryId, targetItemId: node.Id);
            _logger.LogInformation(LogEvents.Metadata.PrecedenceChanged, "Metadata precedence cleared on folder {NodeId}", node.Id);
        }
        return MetadataLinkResultCode.Ok;
    }

    /// <summary>Sets (or with null clears) a library's precedence override.</summary>
    public async Task<MetadataLinkResultCode> SetLibraryPrecedenceAsync(
        string libraryPublicId, MetadataPrecedence? precedence, string? actor, CancellationToken ct = default)
    {
        if (precedence is { } p && !Enum.IsDefined(p))
            return MetadataLinkResultCode.InvalidRequest;
        var library = await _db.Libraries.FirstOrDefaultAsync(l => l.PublicId == libraryPublicId, ct);
        if (library is null)
            return MetadataLinkResultCode.LibraryNotFound;

        var value = (int?)precedence;
        if (library.MetadataPrecedence != value)
        {
            library.MetadataPrecedence = value;
            await _db.SaveChangesAsync(ct);
            await _audit.RecordAsync(value is null ? AuditActions.MetadataPrecedenceClear : AuditActions.MetadataPrecedenceSet,
                AuditResults.Success, actor, ct: ct, targetLibraryId: library.Id);
            _logger.LogInformation(LogEvents.Metadata.PrecedenceChanged, "Metadata precedence changed on library {LibraryId}", library.Id);
        }
        return MetadataLinkResultCode.Ok;
    }

    /// <summary>
    /// "Delete fetched web data": removes every web link (confirmed / auto /
    /// needs_review; "Don't match" rows are kept - they are admin decisions, not
    /// fetched data) globally or for one library, then every record no link
    /// references any more. ComicInfo data is local and untouched.
    /// </summary>
    public async Task<(MetadataLinkResultCode Code, MetadataPurgeResultDto? Result)> PurgeAsync(
        string? libraryPublicId, string? actor, CancellationToken ct = default)
    {
        long? libraryId = null;
        if (!string.IsNullOrEmpty(libraryPublicId))
        {
            libraryId = await _db.Libraries.Where(l => l.PublicId == libraryPublicId).Select(l => (long?)l.Id).FirstOrDefaultAsync(ct);
            if (libraryId is null)
                return (MetadataLinkResultCode.LibraryNotFound, null);
        }

        var links = _db.NodeSeriesLinks.Where(l => l.State != (int)SeriesLinkState.DontMatch);
        if (libraryId is { } lib)
            links = links.Where(l => l.LibraryId == lib);

        var candidateRecordIds = await links.Where(l => l.RecordId != null).Select(l => l.RecordId!.Value).Distinct().ToListAsync(ct);
        var linksRemoved = await links.ExecuteDeleteAsync(ct);

        // A global purge also sweeps records that were previewed but never linked.
        var recordIds = libraryId is null
            ? await _db.MetadataRecords.Select(r => r.Id).ToListAsync(ct)
            : candidateRecordIds;
        var recordsRemoved = await DeleteOrphanRecordsAsync(recordIds, ct);

        await _audit.RecordAsync(AuditActions.MetadataPurge, AuditResults.Success, actor, ct: ct, targetLibraryId: libraryId);
        _logger.LogInformation(LogEvents.Metadata.Purged, "Metadata purge: {Links} links and {Records} records removed (library {LibraryId})",
            linksRemoved, recordsRemoved, libraryId);
        return (MetadataLinkResultCode.Ok, new MetadataPurgeResultDto { LinksRemoved = linksRemoved, RecordsRemoved = recordsRemoved });
    }

    /// <summary>Deletes the given records that no link references any more; returns how many went.</summary>
    private async Task<int> DeleteOrphanRecordsAsync(IReadOnlyCollection<long> recordIds, CancellationToken ct)
    {
        if (recordIds.Count == 0)
            return 0;

        var orphanIds = await _db.MetadataRecords
            .Where(r => recordIds.Contains(r.Id) && !_db.NodeSeriesLinks.Any(l => l.RecordId == r.Id))
            .Select(r => r.Id)
            .ToListAsync(ct);
        if (orphanIds.Count == 0)
            return 0;

        await _db.MetadataRecords.Where(r => orphanIds.Contains(r.Id)).ExecuteDeleteAsync(ct);
        foreach (var handler in _removedHandlers)
        {
            try { await handler.OnRecordsRemovedAsync(orphanIds, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.Metadata.Purged, "Record removal handler failed: {Error}", ex.GetType().Name);
            }
        }
        return orphanIds.Count;
    }

    private async Task<NodeSeriesLinkDto> ToDtoAsync(NodeSeriesLinkEntity link, string nodePublicId, CancellationToken ct)
    {
        MetadataRecordEntity? record = null;
        if (link.RecordId is { } id)
            record = await _db.MetadataRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        return new NodeSeriesLinkDto
        {
            NodeId = nodePublicId,
            State = (SeriesLinkState)link.State,
            Provider = record?.Provider,
            ExternalId = record?.ExternalId,
            RecordId = record?.PublicId,
            MatchMethod = (MetadataMatchMethod?)link.MatchMethod,
            UpdatedAt = link.UpdatedAt,
        };
    }
}
