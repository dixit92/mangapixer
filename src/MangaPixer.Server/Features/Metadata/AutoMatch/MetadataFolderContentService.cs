namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The folder-level Content setting (owner decision 4b), cloned from the folder
/// source-precedence pattern: one row per folder, the nearest folder with a row
/// (self first, then ancestors) wins, default Auto. "Doujinshi &amp; adult
/// one-shots" lifts the Doujinshi exclusion of automatic searches below it. The
/// work detector only SUGGESTS a value (from archive-name signals); it is never
/// applied automatically. No network.
/// </summary>
public sealed class MetadataFolderContentService
{
    private readonly MangaPixerDbContext _db;
    private readonly AuditService _audit;
    private readonly ILogger<MetadataFolderContentService> _logger;
    private readonly IWorkDetector? _detector;

    public MetadataFolderContentService(
        MangaPixerDbContext db,
        AuditService audit,
        ILogger<MetadataFolderContentService> logger,
        IEnumerable<IWorkDetector> detectors)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
        _detector = detectors.LastOrDefault();
    }

    /// <summary>The effective Content of a folder and the folder it comes from (null source = default Auto).</summary>
    public static async Task<(MetadataFolderContent Content, long? SourceNodeId)> ResolveAsync(MangaPixerDbContext db, long folderId, CancellationToken ct)
    {
        var chain = new List<long>();
        long? current = folderId;
        for (var i = 0; current is { } id && i <= SeriesInfoResolver.MaxWalkDepth; i++)
        {
            chain.Add(id);
            current = await db.CatalogNodes.AsNoTracking().Where(n => n.Id == id).Select(n => n.ParentId).FirstOrDefaultAsync(ct);
        }
        var rows = await db.FolderMetadataContents.AsNoTracking()
            .Where(c => chain.Contains(c.NodeId))
            .ToDictionaryAsync(c => c.NodeId, c => c.Content, ct);
        foreach (var id in chain)
            if (rows.TryGetValue(id, out var content))
                return ((MetadataFolderContent)content, id);
        return (MetadataFolderContent.Auto, null);
    }

    public async Task<(MetadataLinkResultCode Code, FolderMetadataContentDto? Dto)> GetAsync(string nodePublicId, CancellationToken ct = default)
    {
        var node = await FolderAsync(nodePublicId, ct);
        if (node.Code != MetadataLinkResultCode.Ok)
            return (node.Code, null);
        return (MetadataLinkResultCode.Ok, await ToDtoAsync(node.Node!, ct));
    }

    public async Task<(MetadataLinkResultCode Code, FolderMetadataContentDto? Dto)> SetAsync(
        string nodePublicId, MetadataFolderContent content, string? actor, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(content))
            return (MetadataLinkResultCode.InvalidRequest, null);
        var node = await FolderAsync(nodePublicId, ct);
        if (node.Code != MetadataLinkResultCode.Ok)
            return (node.Code, null);

        var row = await _db.FolderMetadataContents.FirstOrDefaultAsync(c => c.NodeId == node.Node!.Id, ct);
        if (row is null)
            _db.FolderMetadataContents.Add(new FolderMetadataContentEntity { NodeId = node.Node!.Id, Content = (int)content });
        else
            row.Content = (int)content;
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync(AuditActions.MetadataContentSet, content.ToString(), actor, ct: ct,
            targetLibraryId: node.Node!.LibraryId, targetItemId: node.Node.Id);
        _logger.LogInformation(LogEvents.Metadata.SettingsChanged, "Metadata Content set on folder {NodeId}", node.Node.Id);
        return (MetadataLinkResultCode.Ok, await ToDtoAsync(node.Node, ct));
    }

    public async Task<(MetadataLinkResultCode Code, FolderMetadataContentDto? Dto)> ClearAsync(string nodePublicId, string? actor, CancellationToken ct = default)
    {
        var node = await FolderAsync(nodePublicId, ct);
        if (node.Code != MetadataLinkResultCode.Ok)
            return (node.Code, null);
        var removed = await _db.FolderMetadataContents.Where(c => c.NodeId == node.Node!.Id).ExecuteDeleteAsync(ct);
        if (removed > 0)
        {
            await _audit.RecordAsync(AuditActions.MetadataContentClear, AuditResults.Success, actor, ct: ct,
                targetLibraryId: node.Node!.LibraryId, targetItemId: node.Node.Id);
            _logger.LogInformation(LogEvents.Metadata.SettingsChanged, "Metadata Content cleared on folder {NodeId}", node.Node.Id);
        }
        return (MetadataLinkResultCode.Ok, await ToDtoAsync(node.Node!, ct));
    }

    private async Task<(MetadataLinkResultCode Code, CatalogNodeEntity? Node)> FolderAsync(string nodePublicId, CancellationToken ct)
    {
        var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == nodePublicId, ct);
        if (node is null || node.Availability == (int)CatalogNodeAvailability.Tombstoned)
            return (MetadataLinkResultCode.NodeNotFound, null);
        return node.Kind != (int)CatalogNodeKind.Folder ? (MetadataLinkResultCode.NotAFolder, null) : (MetadataLinkResultCode.Ok, node);
    }

    private async Task<FolderMetadataContentDto> ToDtoAsync(CatalogNodeEntity node, CancellationToken ct)
    {
        var (effective, source) = await ResolveAsync(_db, node.Id, ct);
        var own = source == node.Id ? effective : (MetadataFolderContent?)null;
        string? sourcePublicId = source is { } s
            ? await _db.CatalogNodes.AsNoTracking().Where(n => n.Id == s).Select(n => n.PublicId).FirstOrDefaultAsync(ct)
            : null;
        return new FolderMetadataContentDto
        {
            NodeId = node.PublicId,
            Content = own,
            Effective = effective,
            SourceNodeId = sourcePublicId,
            Suggested = await SuggestAsync(node, ct),
        };
    }

    /// <summary>The detector's suggestion for this folder (display only).</summary>
    private async Task<MetadataFolderContent?> SuggestAsync(CatalogNodeEntity node, CancellationToken ct)
    {
        if (_detector is null)
            return null;
        var tree = await LibraryTreeSnapshot.LoadAsync(_db, node.LibraryId, ct);
        if (tree.Find(node.Id) is null)
            return null;
        return _detector.Classify(tree.ShapeOf(node.Id)).ContentSuggestion == ContentSuggestion.DoujinshiAndAdultOneShots
            ? MetadataFolderContent.DoujinshiAndAdultOneShots
            : null;
    }
}
