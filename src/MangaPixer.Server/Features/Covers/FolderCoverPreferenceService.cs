namespace com.lifepixer.mangapixer.Server.Features.Covers;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>What a folder cover preference call came to.</summary>
public enum FolderCoverPreferenceResult
{
    Ok = 0,
    NodeNotFound = 1,
    NotAFolder = 2,
    InvalidPreference = 3,
}

/// <summary>
/// The per-folder cover preference (1.32.0), cloned from the folder reader defaults: one row per folder, the nearest folder with a
/// row (self first, then ancestors) wins, no row = inherit, and the library's "Show web covers" switch is the root. "File covers"
/// hides web / poster / volume covers below the folder and stops the background work from fetching them; "Web covers when
/// available" shows them even where the library switch is off. An admin's explicit cover choice on a card wins over both. A change
/// asks the cover layer to decide the subtree again (the web decisions below are dropped or made again). No network.
/// </summary>
public sealed class FolderCoverPreferenceService(MangaPixerDbContext db, AuditService audit, CoverDecisionQueue decisions,
    ILogger<FolderCoverPreferenceService> logger)
{
    public async Task<(FolderCoverPreferenceResult Code, FolderCoverPreferenceDto? Dto)> GetAsync(string nodePublicId, CancellationToken ct = default)
    {
        var node = await FolderAsync(nodePublicId, ct);
        if (node.Code != FolderCoverPreferenceResult.Ok)
            return (node.Code, null);
        return (FolderCoverPreferenceResult.Ok, await ToDtoAsync(node.Node!, ct));
    }

    public async Task<(FolderCoverPreferenceResult Code, FolderCoverPreferenceDto? Dto)> SetAsync(
        string nodePublicId, FolderCoverPreference preference, string? actor, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(preference))
            return (FolderCoverPreferenceResult.InvalidPreference, null);
        var node = await FolderAsync(nodePublicId, ct);
        if (node.Code != FolderCoverPreferenceResult.Ok)
            return (node.Code, null);

        var row = await db.FolderCoverPreferences.FirstOrDefaultAsync(f => f.NodeId == node.Node!.Id, ct);
        var changed = row is null || row.Preference != (int)preference;
        if (row is null)
            db.FolderCoverPreferences.Add(new FolderCoverPreferenceEntity { NodeId = node.Node!.Id, Preference = (int)preference });
        else
            row.Preference = (int)preference;
        await db.SaveChangesAsync(ct);
        if (changed)
        {
            await audit.RecordAsync(AuditActions.FolderCoverPreferenceSet, preference.ToString(), actor, ct: ct,
                targetLibraryId: node.Node!.LibraryId, targetItemId: node.Node.Id);
            logger.LogInformation(LogEvents.Metadata.SettingsChanged, "Cover preference set on folder {NodeId}", node.Node.Id);
            decisions.EnqueueSubtree(node.Node.Id);
        }
        return (FolderCoverPreferenceResult.Ok, await ToDtoAsync(node.Node!, ct));
    }

    public async Task<(FolderCoverPreferenceResult Code, FolderCoverPreferenceDto? Dto)> ClearAsync(string nodePublicId, string? actor, CancellationToken ct = default)
    {
        var node = await FolderAsync(nodePublicId, ct);
        if (node.Code != FolderCoverPreferenceResult.Ok)
            return (node.Code, null);
        var removed = await db.FolderCoverPreferences.Where(f => f.NodeId == node.Node!.Id).ExecuteDeleteAsync(ct);
        if (removed > 0)
        {
            await audit.RecordAsync(AuditActions.FolderCoverPreferenceClear, AuditResults.Success, actor, ct: ct,
                targetLibraryId: node.Node!.LibraryId, targetItemId: node.Node.Id);
            logger.LogInformation(LogEvents.Metadata.SettingsChanged, "Cover preference cleared on folder {NodeId}", node.Node.Id);
            decisions.EnqueueSubtree(node.Node.Id);
        }
        return (FolderCoverPreferenceResult.Ok, await ToDtoAsync(node.Node!, ct));
    }

    private async Task<(FolderCoverPreferenceResult Code, CatalogNodeEntity? Node)> FolderAsync(string nodePublicId, CancellationToken ct)
    {
        var node = await db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == nodePublicId, ct);
        if (node is null || node.Availability == (int)CatalogNodeAvailability.Tombstoned)
            return (FolderCoverPreferenceResult.NodeNotFound, null);
        return node.Kind != (int)CatalogNodeKind.Folder ? (FolderCoverPreferenceResult.NotAFolder, null) : (FolderCoverPreferenceResult.Ok, node);
    }

    private async Task<FolderCoverPreferenceDto> ToDtoAsync(CatalogNodeEntity node, CancellationToken ct)
    {
        // What "Inherit" gives: the nearest row above the folder, else the library's switch.
        NearestCoverPreference? inherited = node.ParentId is { } parentId ? await FolderCoverPreferences.OfAsync(db, parentId, ct) : null;
        string? sourcePublicId = null, sourceName = null;
        FolderCoverPreference inheritedValue;
        if (inherited is { } above)
        {
            inheritedValue = above.Preference;
            var source = await db.CatalogNodes.AsNoTracking().Where(n => n.Id == above.SourceNodeId)
                .Select(n => new { n.PublicId, n.DisplayName }).FirstOrDefaultAsync(ct);
            (sourcePublicId, sourceName) = (source?.PublicId, source?.DisplayName);
        }
        else
        {
            var libraryHidden = await db.Libraries.AsNoTracking().Where(l => l.Id == node.LibraryId).Select(l => l.WebCoversHidden).FirstOrDefaultAsync(ct);
            inheritedValue = libraryHidden ? FolderCoverPreference.File : FolderCoverPreference.Web;
        }

        var own = await db.FolderCoverPreferences.AsNoTracking().Where(f => f.NodeId == node.Id).Select(f => (int?)f.Preference).FirstOrDefaultAsync(ct);
        return new FolderCoverPreferenceDto
        {
            NodeId = node.PublicId,
            Preference = own is { } o ? (FolderCoverPreference)o : null,
            Effective = own is { } e ? (FolderCoverPreference)e : inheritedValue,
            Inherited = inheritedValue,
            InheritedSourceNodeId = sourcePublicId,
            InheritedSourceName = sourceName,
        };
    }
}
