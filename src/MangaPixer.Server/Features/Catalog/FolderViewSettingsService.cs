namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>Outcome of a folder view-settings call.</summary>
public enum FolderViewSettingsResult
{
    Ok = 0,
    NotFound = 1,
    NotAFolder = 2,
    InvalidRequest = 3,
}

/// <summary>
/// Admin per-folder view overrides (1.29.0): the Volumes view switch of one folder (<c>folder_view_settings</c>; null =
/// inherit the library, then the global default). The resolution chain itself (user switch > folder > library >
/// global) belongs to the Volumes view; this service only stores and reads the folder's own row.
/// </summary>
public sealed class FolderViewSettingsService(MangaPixerDbContext db, AuditService audit)
{
    public async Task<(FolderViewSettingsResult Result, FolderViewSettingsDto? Dto)> GetAsync(string nodePublicId, CancellationToken ct)
    {
        var (result, node) = await FolderAsync(nodePublicId, ct);
        if (result != FolderViewSettingsResult.Ok)
            return (result, null);
        var row = await db.FolderViewSettings.AsNoTracking().FirstOrDefaultAsync(s => s.NodeId == node!.Id, ct);
        return (FolderViewSettingsResult.Ok, ToDto(nodePublicId, row));
    }

    public async Task<(FolderViewSettingsResult Result, FolderViewSettingsDto? Dto)> SetAsync(
        string nodePublicId, UpdateFolderViewSettingsRequest request, string? actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.VirtualVolumes is { } view && !Enum.IsDefined(view))
            return (FolderViewSettingsResult.InvalidRequest, null);
        var (result, node) = await FolderAsync(nodePublicId, ct);
        if (result != FolderViewSettingsResult.Ok)
            return (result, null);

        var row = await db.FolderViewSettings.FirstOrDefaultAsync(s => s.NodeId == node!.Id, ct);
        var wanted = (int?)request.VirtualVolumes;
        var changed = false;
        if (wanted is null)
        {
            // Every override inherits again: the row goes.
            if (row is not null)
            {
                db.FolderViewSettings.Remove(row);
                row = null;
                changed = true;
            }
        }
        else if (row is null)
        {
            row = new FolderViewSettingsEntity { NodeId = node!.Id, VirtualVolumes = wanted };
            db.FolderViewSettings.Add(row);
            changed = true;
        }
        else if (row.VirtualVolumes != wanted)
        {
            row.VirtualVolumes = wanted;
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
            await audit.RecordAsync(AuditActions.FolderViewSettingsChange, AuditResults.Success, actor, ct: ct,
                targetLibraryId: node!.LibraryId, targetItemId: node.Id);
        }
        return (FolderViewSettingsResult.Ok, ToDto(nodePublicId, row));
    }

    private static FolderViewSettingsDto ToDto(string nodePublicId, FolderViewSettingsEntity? row) => new()
    {
        NodeId = nodePublicId,
        VirtualVolumes = (ViewSwitch?)row?.VirtualVolumes,
    };

    private async Task<(FolderViewSettingsResult, CatalogNodeEntity?)> FolderAsync(string nodePublicId, CancellationToken ct)
    {
        var node = await db.CatalogNodes.AsNoTracking()
            .FirstOrDefaultAsync(n => n.PublicId == nodePublicId && n.Availability != (int)CatalogNodeAvailability.Tombstoned, ct);
        if (node is null)
            return (FolderViewSettingsResult.NotFound, null);
        return node.Kind == (int)CatalogNodeKind.Folder ? (FolderViewSettingsResult.Ok, node) : (FolderViewSettingsResult.NotAFolder, null);
    }
}
