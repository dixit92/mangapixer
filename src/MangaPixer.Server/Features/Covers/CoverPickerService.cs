namespace com.lifepixer.mangapixer.Server.Features.Covers;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>Outcome of a cover choice change.</summary>
public enum CoverChoiceResult
{
    Ok = 0,
    NotFound = 1,
    Invalid = 2,

    /// <summary>The chosen web cover is known but not downloaded (choosing it would need a request).</summary>
    NotStored = 3,

    /// <summary>The node has no archive whose page 1 could be cropped / shown.</summary>
    NoArchive = 4,
}

/// <summary>
/// The admin "Choose cover..." picker (1.29.0, design 6.7): the options of one node - its file cover, the two halves of
/// page 1, other archives' covers, the stored web covers of its linked series - and the choice itself (the TOP of the
/// cover layer; no row = automatic). Works everywhere, also under Don't match, in unlinked libraries and with series
/// information hidden (the web part is then empty with a reason). Local data only: nothing here sends a request.
/// </summary>
public sealed class CoverPickerService
{
    /// <summary>At most this many "another item's cover" options (the folder's archives by SortKey).</summary>
    public const int MaxArchiveOptions = 60;

    private readonly MangaPixerDbContext _db;
    private readonly CoverResolutionService _resolutions;
    private readonly CoverCropService _crops;
    private readonly AuditService _audit;
    private readonly ILogger<CoverPickerService>? _logger;

    public CoverPickerService(MangaPixerDbContext db, CoverResolutionService resolutions, CoverCropService crops, AuditService audit,
        ILogger<CoverPickerService>? logger = null)
    {
        _db = db;
        _resolutions = resolutions;
        _crops = crops;
        _audit = audit;
        _logger = logger;
    }

    public async Task<CoverOptionsDto?> GetOptionsAsync(string nodePublicId, CancellationToken ct)
    {
        var node = await LiveNodeAsync(nodePublicId, ct);
        if (node is null)
            return null;
        var isFolder = node.Kind == (int)CatalogNodeKind.Folder;

        var local = new List<CoverOptionDto>();
        var own = await OwnArchiveAsync(node, ct);
        if (own is { } o)
        {
            local.Add(new CoverOptionDto { Kind = CoverOptionKind.File, ArchiveId = o.PublicId, ImageUrl = ItemCoverUrl(o.PublicId, o.ContentVersion), Label = "This file's cover" });
            if (o.Ready)
            {
                local.Add(new CoverOptionDto { Kind = CoverOptionKind.CropLeft, ArchiveId = o.PublicId, ImageUrl = CropPreviewUrl(o.PublicId, "left", o.ContentVersion), Label = "Left half of page 1" });
                local.Add(new CoverOptionDto { Kind = CoverOptionKind.CropRight, ArchiveId = o.PublicId, ImageUrl = CropPreviewUrl(o.PublicId, "right", o.ContentVersion), Label = "Right half of page 1" });
            }
        }
        foreach (var a in await OtherArchivesAsync(node, isFolder, own?.Id, ct))
            local.Add(new CoverOptionDto { Kind = CoverOptionKind.Archive, ArchiveId = a.PublicId, ImageUrl = ItemCoverUrl(a.PublicId, a.ContentVersion), Label = a.DisplayName });

        var (web, available, reason) = await WebOptionsAsync(node, ct);
        return new CoverOptionsDto
        {
            NodeId = node.PublicId,
            Current = await StateAsync(node, ct),
            Local = local,
            Web = web,
            WebAvailable = available,
            WebUnavailableReason = reason,
        };
    }

    public async Task<(CoverChoiceResult Result, CoverStateDto? State)> SetChoiceAsync(string nodePublicId, CoverChoiceRequest request,
        string? actor, long? actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var node = await LiveNodeAsync(nodePublicId, ct);
        if (node is null)
            return (CoverChoiceResult.NotFound, null);
        if (request.Mode == CoverMode.Automatic)
            return await ClearChoiceAsync(nodePublicId, actor, ct);

        long? archiveNodeId = null, volumeCoverId = null;
        int? cropSide = null;
        CoverChoiceMode mode;
        switch (request.Mode)
        {
            case CoverMode.FilePinned:
                mode = CoverChoiceMode.FilePinned;
                break;
            case CoverMode.Archive:
                mode = CoverChoiceMode.Archive;
                var archive = string.IsNullOrEmpty(request.ArchiveId) ? null : await _db.CatalogNodes.AsNoTracking()
                    .FirstOrDefaultAsync(n => n.PublicId == request.ArchiveId, ct);
                if (archive is null || archive.Kind != (int)CatalogNodeKind.Archive || archive.LibraryId != node.LibraryId
                    || archive.Availability == (int)CatalogNodeAvailability.Tombstoned)
                    return (CoverChoiceResult.Invalid, null);
                archiveNodeId = archive.Id;
                break;
            case CoverMode.VolumeCover:
                mode = CoverChoiceMode.VolumeCover;
                var companion = await CompanionRecordIdAsync(node, ct);
                var cover = companion is null || string.IsNullOrEmpty(request.VolumeCoverId) ? null : await _db.VolumeCovers.AsNoTracking()
                    .FirstOrDefaultAsync(v => v.PublicId == request.VolumeCoverId && v.ProviderRecordId == companion, ct);
                if (cover is null)
                    return (CoverChoiceResult.Invalid, null);
                if (cover.State != (int)VolumeCoverState.Stored || cover.StoredVersion <= 0)
                    return (CoverChoiceResult.NotStored, null);
                volumeCoverId = cover.Id;
                break;
            case CoverMode.Crop:
                mode = CoverChoiceMode.Crop;
                if (request.CropSide is not { } side || !Enum.IsDefined(side))
                    return (CoverChoiceResult.Invalid, null);
                if (await OwnArchiveAsync(node, ct) is not { Ready: true } target)
                    return (CoverChoiceResult.NoArchive, null);
                cropSide = (int)side;
                // Render now so the card changes at once; the cover endpoint renders on demand if this fails.
                await _crops.EnsureAsync(target.Id, side, rerender: false, ct);
                break;
            default:
                return (CoverChoiceResult.Invalid, null);
        }

        var row = await _db.NodeCoverChoices.FirstOrDefaultAsync(c => c.NodeId == node.Id, ct);
        if (row is null)
        {
            row = new NodeCoverChoiceEntity { NodeId = node.Id };
            _db.NodeCoverChoices.Add(row);
        }
        row.Mode = (int)mode;
        row.ArchiveNodeId = archiveNodeId;
        row.VolumeCoverId = volumeCoverId;
        row.CropSide = cropSide;
        row.Version++;
        row.SetByUserId = actorUserId;
        row.SetAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync(AuditActions.CoverChoiceChange, AuditResults.Success, actor, ct: ct, targetLibraryId: node.LibraryId, targetItemId: node.Id);
        _logger?.LogInformation(LogEvents.Metadata.CoverChoiceChanged, "Cover choice set (node {NodeId}, mode {Mode})", node.Id, mode);
        return (CoverChoiceResult.Ok, await StateAsync(node, ct));
    }

    public async Task<(CoverChoiceResult Result, CoverStateDto? State)> ClearChoiceAsync(string nodePublicId, string? actor, CancellationToken ct)
    {
        var node = await LiveNodeAsync(nodePublicId, ct);
        if (node is null)
            return (CoverChoiceResult.NotFound, null);
        var row = await _db.NodeCoverChoices.FirstOrDefaultAsync(c => c.NodeId == node.Id, ct);
        if (row is not null)
        {
            _db.NodeCoverChoices.Remove(row);
            await _db.SaveChangesAsync(ct);
            await _audit.RecordAsync(AuditActions.CoverChoiceChange, AuditResults.Success, actor, ct: ct, targetLibraryId: node.LibraryId, targetItemId: node.Id);
            _logger?.LogInformation(LogEvents.Metadata.CoverChoiceChanged, "Cover choice cleared (node {NodeId})", node.Id);
        }
        return (CoverChoiceResult.Ok, await StateAsync(node, ct));
    }

    /// <summary>The node's current cover: the choice (or Automatic), what the automatic layer decided and why, the image URL.</summary>
    public async Task<CoverStateDto> StateAsync(CatalogNodeEntity node, CancellationToken ct)
    {
        var choice = await _db.NodeCoverChoices.AsNoTracking().FirstOrDefaultAsync(c => c.NodeId == node.Id, ct);
        var auto = await _db.NodeAutoCovers.AsNoTracking().FirstOrDefaultAsync(a => a.NodeId == node.Id, ct);
        if ((auto is null || auto.Source == (int)AutoCoverSource.File) && node.Kind == (int)CatalogNodeKind.Folder
            && (await FolderCovers.ResolveArchivesAsync(_db, [node.Id], ct)).TryGetValue(node.Id, out var coverArchive))
        {
            // A folder without its own automatic cover shows its cover archive's: report that one.
            auto = await _db.NodeAutoCovers.AsNoTracking().FirstOrDefaultAsync(a => a.NodeId == coverArchive.Id, ct) ?? auto;
        }
        // A series folder that shows its local volume 1 (1.30.0): what that archive shows, for the reason "your volume 1".
        var shown = auto;
        if (auto is { Source: (int)AutoCoverSource.LocalVolume1, ArchiveNodeId: { } volume1Id })
            shown = await _db.NodeAutoCovers.AsNoTracking().FirstOrDefaultAsync(a => a.NodeId == volume1Id, ct);
        var resolved = await _resolutions.ResolveOneAsync(node, ct);
        return new CoverStateDto
        {
            Mode = choice is null ? CoverMode.Automatic : (CoverChoiceMode)choice.Mode switch
            {
                CoverChoiceMode.FilePinned => CoverMode.FilePinned,
                CoverChoiceMode.Archive => CoverMode.Archive,
                CoverChoiceMode.VolumeCover => CoverMode.VolumeCover,
                CoverChoiceMode.Crop => CoverMode.Crop,
                _ => CoverMode.Automatic,
            },
            AutoSource = auto is null ? null : (AutoCoverSource)(shown?.Source ?? (int)AutoCoverSource.File) switch
            {
                AutoCoverSource.Crop => CardCoverSource.Crop,
                AutoCoverSource.WebVolume => CardCoverSource.WebVolume,
                AutoCoverSource.WebMain => CardCoverSource.WebMain,
                AutoCoverSource.Poster => CardCoverSource.Poster,
                _ => CardCoverSource.File,
            },
            Reason = auto is null || auto.Reason == (int)AutoCoverReason.None ? null : ((AutoCoverReason)auto.Reason).ToString(),
            ImageUrl = resolved?.Url,
            RecheckAt = auto?.RecheckAt,
        };
    }

    private async Task<CatalogNodeEntity?> LiveNodeAsync(string publicId, CancellationToken ct)
    {
        var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == publicId, ct);
        return node is null || node.Availability == (int)CatalogNodeAvailability.Tombstoned ? null : node;
    }

    private readonly record struct OwnArchive(long Id, string PublicId, long ContentVersion, bool Ready);

    /// <summary>The archive whose page 1 is the node's file cover: itself, or a folder's cover archive.</summary>
    private async Task<OwnArchive?> OwnArchiveAsync(CatalogNodeEntity node, CancellationToken ct)
    {
        long id;
        string publicId;
        if (node.Kind == (int)CatalogNodeKind.Folder)
        {
            if (!(await FolderCovers.ResolveArchivesAsync(_db, [node.Id], ct)).TryGetValue(node.Id, out var a))
                return null;
            (id, publicId) = a;
        }
        else
        {
            (id, publicId) = (node.Id, node.PublicId);
        }
        var item = await _db.ArchiveItems.AsNoTracking().Where(i => i.NodeId == id)
            .Select(i => new { i.ContentVersion, i.AnalysisState }).FirstOrDefaultAsync(ct);
        return new OwnArchive(id, publicId, item?.ContentVersion ?? 0, item is { AnalysisState: 0 });
    }

    private sealed record ArchiveRow(long Id, string PublicId, string DisplayName, long ContentVersion);

    /// <summary>A folder's live archives (any depth, SortKey order); for an archive, its siblings.</summary>
    private async Task<List<ArchiveRow>> OtherArchivesAsync(CatalogNodeEntity node, bool isFolder, long? ownId, CancellationToken ct)
    {
        List<long> ids;
        if (isFolder)
        {
            ids = await DescendantArchivesAsync(node.Id, ct);
        }
        else
        {
            if (node.ParentId is null)
                return [];
            ids = await _db.CatalogNodes.AsNoTracking()
                .Where(n => n.ParentId == node.ParentId && n.Kind == (int)CatalogNodeKind.Archive && n.Availability != (int)CatalogNodeAvailability.Tombstoned)
                .OrderBy(n => n.SortKey).ThenBy(n => n.Id)
                .Select(n => n.Id).Take(MaxArchiveOptions + 1).ToListAsync(ct);
        }
        ids = ids.Where(id => id != ownId).Take(MaxArchiveOptions).ToList();
        var rows = await _db.CatalogNodes.AsNoTracking().Where(n => ids.Contains(n.Id))
            .Select(n => new { n.Id, n.PublicId, n.DisplayName, n.SortKey, Version = n.ArchiveItem != null ? n.ArchiveItem.ContentVersion : 0 })
            .ToListAsync(ct);
        return rows.OrderBy(r => r.SortKey, StringComparer.Ordinal).ThenBy(r => r.Id)
            .Select(r => new ArchiveRow(r.Id, r.PublicId, r.DisplayName, r.Version)).ToList();
    }

    private async Task<List<long>> DescendantArchivesAsync(long folderId, CancellationToken ct)
    {
        var result = new List<long>();
        var connection = _db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                WITH RECURSIVE d(NodeId, Kind, SortKey, Availability) AS (
                    SELECT cn.Id, cn.Kind, cn.SortKey, cn.Availability FROM catalog_nodes cn WHERE cn.ParentId = {folderId.ToString(CultureInfo.InvariantCulture)}
                    UNION ALL
                    SELECT cn.Id, cn.Kind, cn.SortKey, cn.Availability FROM d JOIN catalog_nodes cn ON cn.ParentId = d.NodeId
                )
                SELECT NodeId FROM d WHERE Kind = 1 AND Availability != 5 ORDER BY SortKey, NodeId LIMIT {MaxArchiveOptions + 1};
                """;
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(reader.GetInt64(0));
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
        return result;
    }

    /// <summary>The MangaDex companion record of the node's linked series, or null (not linked, Don't match, none found).</summary>
    private async Task<long?> CompanionRecordIdAsync(CatalogNodeEntity node, CancellationToken ct)
    {
        var links = await CoverLinks.NearestAsync(_db, [node.Id], ct);
        if (!links.TryGetValue(node.Id, out var link) || !link.IsLinked)
            return null;
        return await CoverSeries.CompanionRecordIdAsync(_db, link.RecordId!.Value, ct);
    }

    private async Task<(IReadOnlyList<WebCoverGroupDto> Groups, bool Available, string? Reason)> WebOptionsAsync(CatalogNodeEntity node, CancellationToken ct)
    {
        var links = await CoverLinks.NearestAsync(_db, [node.Id], ct);
        if (!links.TryGetValue(node.Id, out var link) || link.IsDontMatch)
            return ([], false, link.IsDontMatch ? "dont_match" : "not_linked");
        if (link.IsOwnCollection)
            return ([], false, "collection"); // 1.34.0: its series' poster is automatic; there are no volume covers to choose from.
        if (!link.IsLinked)
            return ([], false, "not_linked");

        var settings = await _db.AppSettings.AsNoTracking().Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => new { s.MetadataVolumeCoversEnabled }).FirstOrDefaultAsync(ct);
        if (settings is { MetadataVolumeCoversEnabled: false })
            return ([], false, "volume_covers_off");
        // 1.32.0: an admin's explicit choice wins over an inherited folder "File covers"; only the library switch (unless the nearest
        // folder says "Web covers when available") still hides the choice.
        var libraryHidden = await _db.Libraries.AsNoTracking().Where(l => l.Id == node.LibraryId).Select(l => l.WebCoversHidden).FirstOrDefaultAsync(ct);
        if (!FolderCoverRules.ChosenWebShown((await FolderCoverPreferences.OfAsync(_db, node.Id, ct))?.Preference, libraryHidden))
            return ([], false, "web_covers_hidden");

        var companion = await CoverSeries.CompanionRecordIdAsync(_db, link.RecordId!.Value, ct);
        if (companion is null)
            return ([], false, "no_companion");

        var covers = await _db.VolumeCovers.AsNoTracking()
            .Where(v => v.ProviderRecordId == companion && v.State != (int)VolumeCoverState.Failed)
            .ToListAsync(ct);
        var groups = covers
            .GroupBy(v => v.Kind == (int)VolumeCoverKind.Main ? null : v.Volume)
            .OrderBy(g => g.Key is null ? int.MaxValue : g.Key.Value)
            .Select(g => new WebCoverGroupDto
            {
                Volume = g.Key,
                Covers = g.OrderBy(v => v.Kind).ThenBy(v => v.Variant).ThenBy(v => v.Locale, StringComparer.Ordinal)
                    .Select(v => new WebCoverDto
                    {
                        Id = v.PublicId,
                        Kind = (VolumeCoverKind)v.Kind,
                        Volume = v.Volume,
                        Variant = v.Variant,
                        Locale = v.Locale,
                        Stored = v.State == (int)VolumeCoverState.Stored && v.StoredVersion > 0,
                        ImageUrl = v.State == (int)VolumeCoverState.Stored && v.StoredVersion > 0 ? VolumeCoverImageUrl(v.PublicId, v.StoredVersion) : null,
                    }).ToList(),
            }).ToList();
        return (groups, true, null);
    }

    public static string ItemCoverUrl(string archivePublicId, long contentVersion) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/v1/items/{Uri.EscapeDataString(archivePublicId)}/cover?v={contentVersion}");

    public static string CropPreviewUrl(string archivePublicId, string side, long contentVersion) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/v1/nodes/{Uri.EscapeDataString(archivePublicId)}/cover-crops/{side}?v={contentVersion}");

    public static string VolumeCoverImageUrl(string publicId, int storedVersion) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/v1/volume-covers/{Uri.EscapeDataString(publicId)}/image?v={storedVersion}");
}
