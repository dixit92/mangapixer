namespace com.lifepixer.mangapixer.Server.Features.Covers;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>What image a resolved cover is.</summary>
public enum CoverImageKind
{
    /// <summary>An archive's durable file thumbnail (page 1).</summary>
    File = 0,

    /// <summary>One half of an archive's page 1 (a jacket spread).</summary>
    Crop = 1,

    /// <summary>A stored web cover (<c>volume_covers</c>).</summary>
    VolumeCover = 2,

    /// <summary>The linked record's stored poster.</summary>
    Poster = 3,
}

/// <summary>
/// One node's resolved cover: which image, where it comes from and its URL token. <see cref="Layered"/> covers are served
/// by <c>GET /nodes/{UrlNodePublicId}/cover?v={Version}</c>; plain file covers by <c>GET /items/{ArchivePublicId}/cover?v={ContentVersion}</c>.
/// </summary>
public sealed record CoverResolution
{
    public required long NodeId { get; init; }
    public required CardCoverSource Source { get; init; }
    public required CoverImageKind Image { get; init; }

    /// <summary>True when a layer (admin choice or automatic decision) picked the image.</summary>
    public required bool Layered { get; init; }

    /// <summary>
    /// True when the node's OWN layer picked the image (its admin choice or its automatic decision - for a series folder its
    /// local volume 1 or a web cover), false for the file default (a folder: its first archive by name). Home's Continue
    /// reading shows a linked series folder's cover only when it is its own (1.30.0).
    /// </summary>
    public bool OwnLayer { get; init; }

    /// <summary>The node the layered URL names (the node itself, or a folder's cover archive).</summary>
    public required string UrlNodePublicId { get; init; }

    /// <summary>File / Crop: the archive whose page 1 it is (also the fallback when a layered file is missing).</summary>
    public long ArchiveNodeId { get; init; }

    public string ArchivePublicId { get; init; } = string.Empty;
    public long ContentVersion { get; init; }
    public CoverCropSide? CropSide { get; init; }
    public long? VolumeCoverId { get; init; }
    public string? VolumeCoverPublicId { get; init; }
    public int StoredVersion { get; init; }
    public long? RecordId { get; init; }
    public int ImageVersion { get; init; }

    /// <summary>The URL version (<c>v</c>): changes whenever the layer, its target or the target's version changes.</summary>
    public required string Version { get; init; }

    public string Url => Layered
        ? $"/api/v1/nodes/{UrlNodePublicId}/cover?v={Version}"
        : $"/api/v1/items/{ArchivePublicId}/cover?v={Version}";
}

/// <summary>
/// The cover layer at read time (1.29.0, design 2.9 / 6.1): per node, the FIRST that applies of
/// <list type="number">
/// <item>the admin's choice (another archive's file cover, a stored web cover, a half of page 1);</item>
/// <item>the admin's "use the file's cover" pin (stops: the file);</item>
/// <item>the automatic decision (<c>node_auto_covers</c>) while its layer is allowed - a crop needs "Crop jacket spreads", a web
/// source (volume / main cover, poster) needs "Volume covers from the web" AND the library's "Show saved web covers"; a series
/// folder's "local volume 1" (1.30.0) resolves that archive through its own steps 1-4;</item>
/// <item>the file default: an archive's own page 1; a folder's cover archive (first live descendant archive by SortKey,
/// <see cref="FolderCovers"/>), which goes through steps 1-3 itself - so a volume 1 crop reaches its series card for free.</item>
/// </list>
/// A fixed number of queries per call (no per-node walk). The file thumbnail stays the truth: every layered image falls
/// back to it when its own file is missing.
/// </summary>
public sealed class CoverResolutionService
{
    private readonly MangaPixerDbContext _db;

    public CoverResolutionService(MangaPixerDbContext db) => _db = db;

    private sealed record LayerSwitches(bool CropEnabled, bool VolumeCoversEnabled);

    private sealed class Batch
    {
        public required LayerSwitches Switches { get; init; }
        public Dictionary<long, NodeCoverChoiceEntity> Choices { get; } = [];
        public Dictionary<long, NodeAutoCoverEntity> Autos { get; } = [];
        public Dictionary<long, (long LibraryId, string PublicId, int Kind, int Availability)> Nodes { get; } = [];
        public HashSet<long> WebHiddenLibraries { get; } = [];
        public Dictionary<long, VolumeCoverEntity> VolumeCovers { get; } = [];
        public Dictionary<long, long> ContentVersions { get; } = [];
        public Dictionary<long, (long Id, string PublicId)> FolderCoverArchives { get; } = [];
        public Dictionary<long, MetadataRecordEntity?> PosterRecords { get; } = [];
    }

    /// <summary>Resolves the cover of each target; targets without any cover (an empty folder) are omitted.</summary>
    public async Task<IReadOnlyDictionary<long, CoverResolution>> ResolveAsync(IReadOnlyCollection<CoverTarget> targets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var result = new Dictionary<long, CoverResolution>(targets.Count);
        if (targets.Count == 0)
            return result;

        var row = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => new { s.CoverSpreadCropEnabled, s.MetadataVolumeCoversEnabled })
            .FirstOrDefaultAsync(ct);
        var batch = new Batch { Switches = new LayerSwitches(row?.CoverSpreadCropEnabled ?? true, row?.MetadataVolumeCoversEnabled ?? true) };

        var ids = targets.Select(t => t.NodeId).Distinct().ToList();
        await LoadLayersAsync(batch, ids, ct);

        // A series folder that shows its local volume 1 (1.30.0): that archive's own layer.
        var volume1Archives = batch.Autos.Values
            .Where(a => a.Source == (int)AutoCoverSource.LocalVolume1 && a.ArchiveNodeId is not null)
            .Select(a => a.ArchiveNodeId!.Value).Where(id => !batch.Nodes.ContainsKey(id)).Distinct().ToList();
        await LoadLayersAsync(batch, volume1Archives, ct);

        // Folders with no own image-picking layer, and folder choices that need the cover archive (pin, crop).
        var needArchive = new List<long>();
        foreach (var t in targets)
            if (t.IsFolder)
                needArchive.Add(t.NodeId);
        if (needArchive.Count > 0)
        {
            foreach (var (folderId, archive) in await FolderCovers.ResolveArchivesAsync(_db, needArchive.Distinct().ToList(), ct))
                batch.FolderCoverArchives[folderId] = archive;
            var archiveIds = batch.FolderCoverArchives.Values.Select(a => a.Id).Where(id => !batch.Nodes.ContainsKey(id)).Distinct().ToList();
            await LoadLayersAsync(batch, archiveIds, ct);
        }

        await LoadTargetsAsync(batch, ct);

        foreach (var t in targets)
        {
            if (result.ContainsKey(t.NodeId))
                continue;
            var resolved = Resolve(batch, t.NodeId, t.PublicId, t.IsFolder);
            if (resolved is not null)
                result[t.NodeId] = resolved;
        }
        return result;
    }

    /// <summary>Resolves one node (the cover endpoint, the picker).</summary>
    public async Task<CoverResolution?> ResolveOneAsync(CatalogNodeEntity node, CancellationToken ct)
    {
        var all = await ResolveAsync([new CoverTarget(node.Id, node.PublicId, node.Kind == (int)CatalogNodeKind.Folder)], ct);
        return all.TryGetValue(node.Id, out var r) ? r : null;
    }

    private async Task LoadLayersAsync(Batch batch, List<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return;
        foreach (var c in await _db.NodeCoverChoices.AsNoTracking().Where(c => ids.Contains(c.NodeId)).ToListAsync(ct))
            batch.Choices[c.NodeId] = c;
        foreach (var a in await _db.NodeAutoCovers.AsNoTracking().Where(a => ids.Contains(a.NodeId)).ToListAsync(ct))
            batch.Autos[a.NodeId] = a;
        var nodes = await _db.CatalogNodes.AsNoTracking().Where(n => ids.Contains(n.Id))
            .Select(n => new { n.Id, n.LibraryId, n.PublicId, n.Kind, n.Availability })
            .ToListAsync(ct);
        foreach (var n in nodes)
            batch.Nodes[n.Id] = (n.LibraryId, n.PublicId, n.Kind, n.Availability);
    }

    /// <summary>Loads everything the layers point at: archives (content versions), web covers, posters, library switches.</summary>
    private async Task LoadTargetsAsync(Batch batch, CancellationToken ct)
    {
        var chosenArchives = batch.Choices.Values.Where(c => c.Mode == (int)CoverChoiceMode.Archive && c.ArchiveNodeId is not null)
            .Select(c => c.ArchiveNodeId!.Value).Where(id => !batch.Nodes.ContainsKey(id)).Distinct().ToList();
        if (chosenArchives.Count > 0)
        {
            var nodes = await _db.CatalogNodes.AsNoTracking().Where(n => chosenArchives.Contains(n.Id))
                .Select(n => new { n.Id, n.LibraryId, n.PublicId, n.Kind, n.Availability }).ToListAsync(ct);
            foreach (var n in nodes)
                batch.Nodes[n.Id] = (n.LibraryId, n.PublicId, n.Kind, n.Availability);
        }

        var archiveIds = batch.Nodes.Where(kv => kv.Value.Kind == (int)CatalogNodeKind.Archive).Select(kv => kv.Key).ToList();
        if (archiveIds.Count > 0)
        {
            foreach (var a in await _db.ArchiveItems.AsNoTracking().Where(a => archiveIds.Contains(a.NodeId))
                         .Select(a => new { a.NodeId, a.ContentVersion }).ToListAsync(ct))
                batch.ContentVersions[a.NodeId] = a.ContentVersion;
        }

        var coverIds = batch.Choices.Values.Where(c => c.Mode == (int)CoverChoiceMode.VolumeCover).Select(c => c.VolumeCoverId)
            .Concat(batch.Autos.Values.Select(a => a.VolumeCoverId))
            .Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        if (coverIds.Count > 0)
            foreach (var vc in await _db.VolumeCovers.AsNoTracking().Where(v => coverIds.Contains(v.Id)).ToListAsync(ct))
                batch.VolumeCovers[vc.Id] = vc;

        var libraryIds = batch.Nodes.Values.Select(n => n.LibraryId).Distinct().ToList();
        foreach (var id in await _db.Libraries.AsNoTracking().Where(l => libraryIds.Contains(l.Id) && l.WebCoversHidden).Select(l => l.Id).ToListAsync(ct))
            batch.WebHiddenLibraries.Add(id);

        var posterNodes = batch.Autos.Values.Where(a => a.Source == (int)AutoCoverSource.Poster).Select(a => a.NodeId).ToList();
        if (posterNodes.Count > 0)
        {
            var links = await CoverLinks.NearestAsync(_db, posterNodes, ct);
            var recordIds = links.Values.Where(l => l.IsLinked).Select(l => l.RecordId!.Value).Distinct().ToList();
            var records = await _db.MetadataRecords.AsNoTracking().Where(r => recordIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
            foreach (var nodeId in posterNodes)
                batch.PosterRecords[nodeId] = links.TryGetValue(nodeId, out var l) && l.IsLinked && records.TryGetValue(l.RecordId!.Value, out var r)
                    ? r : null;
        }
    }

    private CoverResolution? Resolve(Batch batch, long nodeId, string publicId, bool isFolder)
    {
        if (!batch.Nodes.ContainsKey(nodeId))
            return null;
        if (OwnLayer(batch, nodeId, publicId, isFolder) is { } own)
            return own with { OwnLayer = true };

        // 4. The file default (a folder: its cover archive, through the archive's own layer).
        if (!isFolder)
            return FileOf(batch, nodeId, isFolder: false);
        if (!batch.FolderCoverArchives.TryGetValue(nodeId, out var coverArchive))
            return null;
        var inner = Resolve(batch, coverArchive.Id, coverArchive.PublicId, isFolder: false);
        return inner is null ? null : inner with { NodeId = nodeId, OwnLayer = false };
    }

    /// <summary>Steps 1-3: the node's own layer (admin choice, pin, automatic decision), or null for the file default.</summary>
    private CoverResolution? OwnLayer(Batch batch, long nodeId, string publicId, bool isFolder)
    {
        var node = batch.Nodes[nodeId];
        var webAllowed = batch.Switches.VolumeCoversEnabled && !batch.WebHiddenLibraries.Contains(node.LibraryId);

        // 1-2. The admin's choice.
        if (batch.Choices.TryGetValue(nodeId, out var choice))
        {
            switch ((CoverChoiceMode)choice.Mode)
            {
                case CoverChoiceMode.FilePinned:
                    return FileOf(batch, nodeId, isFolder);
                case CoverChoiceMode.Archive when choice.ArchiveNodeId is { } archiveId
                    && batch.Nodes.TryGetValue(archiveId, out var chosen) && IsLiveArchive(chosen)
                    && batch.ContentVersions.TryGetValue(archiveId, out var chosenVersion):
                    return Layer(nodeId, publicId, CardCoverSource.Chosen, CoverImageKind.File, "c", choice.Version) with
                    {
                        ArchiveNodeId = archiveId,
                        ArchivePublicId = chosen.PublicId,
                        ContentVersion = chosenVersion,
                        Version = Token("c", choice.Version, "file", archiveId, chosenVersion),
                    };
                case CoverChoiceMode.VolumeCover when webAllowed && choice.VolumeCoverId is { } coverId
                    && batch.VolumeCovers.TryGetValue(coverId, out var cover) && IsServable(cover):
                    return WebLayer(batch, nodeId, publicId, isFolder, CardCoverSource.Chosen, "c", choice.Version, cover);
                case CoverChoiceMode.Crop when choice.CropSide is { } side && OwnArchive(batch, nodeId, isFolder) is { } own:
                    return Layer(nodeId, publicId, CardCoverSource.Chosen, CoverImageKind.Crop, "c", choice.Version) with
                    {
                        ArchiveNodeId = own.Id,
                        ArchivePublicId = own.PublicId,
                        ContentVersion = own.ContentVersion,
                        CropSide = (CoverCropSide)side,
                        Version = Token("c", choice.Version, "crop", own.Id, own.ContentVersion, side),
                    };
            }
            // A choice whose target is gone (or hidden) falls back to the automatic layer.
        }

        // 3. The automatic decision.
        if (batch.Autos.TryGetValue(nodeId, out var auto))
        {
            switch ((AutoCoverSource)auto.Source)
            {
                case AutoCoverSource.Crop when batch.Switches.CropEnabled && auto.CropSide is { } side && !isFolder
                    && OwnArchive(batch, nodeId, isFolder) is { } own:
                    return Layer(nodeId, publicId, CardCoverSource.Crop, CoverImageKind.Crop, "a", auto.Version) with
                    {
                        ArchiveNodeId = own.Id,
                        ArchivePublicId = own.PublicId,
                        ContentVersion = own.ContentVersion,
                        CropSide = (CoverCropSide)side,
                        Version = Token("a", auto.Version, "crop", own.Id, own.ContentVersion, side),
                    };
                case AutoCoverSource.WebVolume or AutoCoverSource.WebMain when webAllowed && auto.VolumeCoverId is { } coverId
                    && batch.VolumeCovers.TryGetValue(coverId, out var cover) && IsServable(cover):
                    return WebLayer(batch, nodeId, publicId, isFolder,
                        auto.Source == (int)AutoCoverSource.WebMain ? CardCoverSource.WebMain : CardCoverSource.WebVolume, "a", auto.Version, cover);
                case AutoCoverSource.Poster when webAllowed && batch.PosterRecords.TryGetValue(nodeId, out var record)
                    && record is { ImageState: 1 }:
                    return WithFallbackArchive(batch, Layer(nodeId, publicId, CardCoverSource.Poster, CoverImageKind.Poster, "a", auto.Version) with
                    {
                        RecordId = record.Id,
                        ImageVersion = record.ImageVersion,
                        Version = Token("a", auto.Version, "poster", record.Id, record.ImageVersion),
                    }, nodeId, isFolder);
                case AutoCoverSource.LocalVolume1 when isFolder && auto.ArchiveNodeId is { } volume1Id
                    && batch.Nodes.TryGetValue(volume1Id, out var volume1) && IsLiveArchive(volume1):
                    // Exactly what volume 1's own card shows (its choice, crop, web cover or file); its URL and version.
                    return Resolve(batch, volume1Id, volume1.PublicId, isFolder: false) is { } shown ? shown with { NodeId = nodeId } : null;
            }
        }
        return null;
    }

    /// <summary>The node's file cover: an archive's own page 1, a folder's cover archive's page 1 (no layer).</summary>
    private static CoverResolution? FileOf(Batch batch, long nodeId, bool isFolder)
    {
        var own = OwnArchive(batch, nodeId, isFolder);
        if (own is null)
            return null;
        return new CoverResolution
        {
            NodeId = nodeId,
            Source = CardCoverSource.File,
            Image = CoverImageKind.File,
            Layered = false,
            UrlNodePublicId = own.Value.PublicId,
            ArchiveNodeId = own.Value.Id,
            ArchivePublicId = own.Value.PublicId,
            ContentVersion = own.Value.ContentVersion,
            Version = own.Value.ContentVersion.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>The archive whose page 1 a node shows by default: itself, or a folder's cover archive.</summary>
    private static (long Id, string PublicId, long ContentVersion)? OwnArchive(Batch batch, long nodeId, bool isFolder)
    {
        long archiveId;
        string archivePublicId;
        if (isFolder)
        {
            if (!batch.FolderCoverArchives.TryGetValue(nodeId, out var a))
                return null;
            (archiveId, archivePublicId) = a;
        }
        else
        {
            if (!batch.Nodes.TryGetValue(nodeId, out var n))
                return null;
            (archiveId, archivePublicId) = (nodeId, n.PublicId);
        }
        // Not analysed yet: version 0 - the endpoint answers 202 pending as before.
        batch.ContentVersions.TryGetValue(archiveId, out var version);
        return (archiveId, archivePublicId, version);
    }

    private static CoverResolution WebLayer(Batch batch, long nodeId, string publicId, bool isFolder, CardCoverSource source,
        string layer, int layerVersion, VolumeCoverEntity cover)
    {
        var r = Layer(nodeId, publicId, source, CoverImageKind.VolumeCover, layer, layerVersion) with
        {
            VolumeCoverId = cover.Id,
            VolumeCoverPublicId = cover.PublicId,
            StoredVersion = cover.StoredVersion,
            Version = Token(layer, layerVersion, "vc", cover.Id, cover.StoredVersion),
        };
        return WithFallbackArchive(batch, r, nodeId, isFolder);
    }

    /// <summary>A web image falls back to the node's file cover when its file is missing: remember which archive.</summary>
    private static CoverResolution WithFallbackArchive(Batch batch, CoverResolution r, long nodeId, bool isFolder) =>
        OwnArchive(batch, nodeId, isFolder) is { } own
            ? r with { ArchiveNodeId = own.Id, ArchivePublicId = own.PublicId, ContentVersion = own.ContentVersion }
            : r;

    private static CoverResolution Layer(long nodeId, string publicId, CardCoverSource source, CoverImageKind image, string layer, int version) => new()
    {
        NodeId = nodeId,
        Source = source,
        Image = image,
        Layered = true,
        UrlNodePublicId = publicId,
        Version = Token(layer, version),
    };

    private static bool IsLiveArchive((long LibraryId, string PublicId, int Kind, int Availability) n) =>
        n.Kind == (int)CatalogNodeKind.Archive && n.Availability != (int)CatalogNodeAvailability.Tombstoned;

    /// <summary>A web cover with a stored file (Stored, or Gone with its file kept for a choice).</summary>
    private static bool IsServable(VolumeCoverEntity cover) =>
        cover.StoredVersion > 0 && cover.State is (int)VolumeCoverState.Stored or (int)VolumeCoverState.Gone;

    /// <summary>
    /// A short opaque token over the layer and its target (base36 of 48 bits of SHA-256): any change of the layer version,
    /// the target or the target's own version gives a new URL.
    /// </summary>
    internal static string Token(params object[] parts)
    {
        var text = string.Join('|', parts.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture)));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        long value = 0;
        for (var i = 0; i < 6; i++)
            value = (value << 8) | hash[i];
        return OpaqueId.Encode(value);
    }
}
