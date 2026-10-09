namespace com.lifepixer.mangapixer.Server.Features.Metadata.Declared;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>Outcome of a declared-facts call: a result code, the value, and a machine error code for 400s.</summary>
public sealed record DeclaredFactsResult<T>(MetadataLinkResultCode Code, T? Value = default, string? Error = null)
{
    public static DeclaredFactsResult<T> Ok(T value) => new(MetadataLinkResultCode.Ok, value);
}

/// <summary>
/// Declared facts (1.28.0; owner decisions 2026-09-27): an ADMIN declares the type / format and the creators
/// of the works below a folder or a whole library; everything below inherits them and the nearest declaration
/// wins per key. Explicit settings only - nothing is ever inferred from library or folder names, and a
/// declaration never changes a linked record: when the two disagree the Info panel shows both, with a
/// conflict indication. Local only, no network. Logs carry ids and counts, never names.
/// </summary>
public sealed class DeclaredFactsService
{
    private readonly MangaPixerDbContext _db;
    private readonly AuditService _audit;
    private readonly MetadataSettingsService _settings;
    private readonly SeriesInfoResolver _resolver;
    private readonly Providers.MetadataProviderRegistry _providers;
    private readonly ILogger<DeclaredFactsService> _logger;
    private readonly TimeProvider _time;

    public DeclaredFactsService(
        MangaPixerDbContext db,
        AuditService audit,
        MetadataSettingsService settings,
        SeriesInfoResolver resolver,
        Providers.MetadataProviderRegistry providers,
        ILogger<DeclaredFactsService> logger,
        TimeProvider time)
    {
        _db = db;
        _audit = audit;
        _settings = settings;
        _resolver = resolver;
        _providers = providers;
        _logger = logger;
        _time = time;
    }

    // --- Admin: one scope ---

    public async Task<DeclaredFactsResult<DeclaredFactsScopeDto>> GetFolderAsync(string nodePublicId, CancellationToken ct = default)
    {
        var (code, node) = await FolderAsync(nodePublicId, ct);
        return node is null ? new(code) : DeclaredFactsResult<DeclaredFactsScopeDto>.Ok(await FolderScopeDtoAsync(node, ct));
    }

    public async Task<DeclaredFactsResult<DeclaredFactsScopeDto>> GetLibraryAsync(string libraryPublicId, CancellationToken ct = default)
    {
        var library = await LibraryAsync(libraryPublicId, ct);
        return library is null
            ? new(MetadataLinkResultCode.LibraryNotFound)
            : DeclaredFactsResult<DeclaredFactsScopeDto>.Ok(await LibraryScopeDtoAsync(library, ct));
    }

    public async Task<DeclaredFactsResult<DeclaredFactsScopeDto>> SetFolderAsync(
        string nodePublicId, SetDeclaredFactsRequest request, string? actor, CancellationToken ct = default)
    {
        var (values, error) = Validate(request);
        if (values is null)
            return new(MetadataLinkResultCode.InvalidRequest, Error: error);
        var (code, node) = await FolderAsync(nodePublicId, ct);
        if (node is null)
            return new(code);
        await ReplaceAsync(node.LibraryId, node.Id, values, actor, ct);
        return DeclaredFactsResult<DeclaredFactsScopeDto>.Ok(await FolderScopeDtoAsync(node, ct));
    }

    public async Task<DeclaredFactsResult<DeclaredFactsScopeDto>> SetLibraryAsync(
        string libraryPublicId, SetDeclaredFactsRequest request, string? actor, CancellationToken ct = default)
    {
        var (values, error) = Validate(request);
        if (values is null)
            return new(MetadataLinkResultCode.InvalidRequest, Error: error);
        var library = await LibraryAsync(libraryPublicId, ct);
        if (library is null)
            return new(MetadataLinkResultCode.LibraryNotFound);
        await ReplaceAsync(library.Id, null, values, actor, ct);
        return DeclaredFactsResult<DeclaredFactsScopeDto>.Ok(await LibraryScopeDtoAsync(library, ct));
    }

    /// <summary>
    /// 1.39.0: replaces the folder's OWN edition facts (volumes in this edition, edition label, track completion) - folders only, never
    /// inherited; its type and creators are left alone (and a type / creator save leaves these alone).
    /// </summary>
    public async Task<DeclaredFactsResult<DeclaredFactsScopeDto>> SetFolderEditionAsync(
        string nodePublicId, SetDeclaredEditionRequest request, string? actor, CancellationToken ct = default)
    {
        var (values, error) = ValidateEdition(request);
        if (values is null)
            return new(MetadataLinkResultCode.InvalidRequest, Error: error);
        var (code, node) = await FolderAsync(nodePublicId, ct);
        if (node is null)
            return new(code);
        await ReplaceEditionAsync(node.LibraryId, node.Id, values, actor, ct);
        return DeclaredFactsResult<DeclaredFactsScopeDto>.Ok(await FolderScopeDtoAsync(node, ct));
    }

    /// <summary>Clears everything declared on the folder itself: type, creators and (1.39.0) its edition facts.</summary>
    public async Task<DeclaredFactsResult<DeclaredFactsScopeDto>> ClearFolderAsync(string nodePublicId, string? actor, CancellationToken ct = default)
    {
        var (code, node) = await FolderAsync(nodePublicId, ct);
        if (node is null)
            return new(code);
        await ReplaceAsync(node.LibraryId, node.Id, DeclaredFacts.Empty, actor, ct);
        await ReplaceEditionAsync(node.LibraryId, node.Id, DeclaredEditionFacts.Empty, actor, ct);
        return DeclaredFactsResult<DeclaredFactsScopeDto>.Ok(await FolderScopeDtoAsync(node, ct));
    }

    public Task<DeclaredFactsResult<DeclaredFactsScopeDto>> ClearLibraryAsync(string libraryPublicId, string? actor, CancellationToken ct = default) =>
        SetLibraryAsync(libraryPublicId, new SetDeclaredFactsRequest(), actor, ct);

    // --- Reader: the Info panel view of a node ---

    /// <summary>
    /// The effective declared facts at <paramref name="node"/> (a folder, or an archive inheriting from its
    /// folders) plus, when a linked web record applies and disagrees, what it disagrees with. The caller has
    /// already checked the node's access. Empty when "Show series information" is off for the library.
    /// </summary>
    public async Task<NodeDeclaredFactsDto> ForNodeAsync(CatalogNodeEntity node, CancellationToken ct = default)
    {
        if (await _settings.IsSeriesInfoHiddenAsync(node.LibraryId, ct))
            return new NodeDeclaredFactsDto { NodeId = node.PublicId, Effective = new EffectiveDeclaredFactsDto() };

        var chain = await ChainAsync(node, ct);
        var library = await _db.Libraries.AsNoTracking().FirstAsync(l => l.Id == node.LibraryId, ct);
        var (effective, from) = await EffectiveAsync(library, chain, ct);
        var dto = ToEffectiveDto(effective, from);
        return new NodeDeclaredFactsDto
        {
            NodeId = node.PublicId,
            Effective = dto,
            Conflict = effective.IsEmpty ? null : await ConflictAsync(node, effective, ct),
            Edition = node.Kind == (int)CatalogNodeKind.Folder ? await OwnEditionDtoAsync(node.Id, ct) : null,
        };
    }

    private async Task<DeclaredFactsConflictDto?> ConflictAsync(CatalogNodeEntity node, DeclaredFacts declared, CancellationToken ct)
    {
        var record = await _resolver.ResolveWebRecordAsync(node, ct);
        if (record is null)
            return null;
        var typeConflict = declared.TypeValue is { } type
            && DeclaredFactsComparer.TypeConflicts(type, (MetadataOrigin?)record.Origin, (MetadataFormat?)record.Format);
        var recordCreators = MetadataJson.ReadList<MetadataJson.Creator>(record.CreatorsJson)
            .Select(c => c.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var creatorConflict = DeclaredFactsComparer.CreatorsConflict(declared.Creators.Select(c => c.Name).ToList(), recordCreators);
        if (!typeConflict && !creatorConflict)
            return null;
        return new DeclaredFactsConflictDto
        {
            ProviderName = _providers.DisplayNameFor(record.Provider),
            Type = typeConflict,
            RecordType = typeConflict ? RecordTypeText(record) : null,
            Creators = creatorConflict,
            RecordCreators = creatorConflict ? recordCreators.Take(10).ToList() : [],
        };
    }

    /// <summary>The record's type as its provider says it (e.g. <c>Manhwa</c>), else its origin / format name.</summary>
    private static string? RecordTypeText(MetadataRecordEntity record)
    {
        if (!string.IsNullOrWhiteSpace(record.ProviderType))
            return record.ProviderType.Trim();
        if (record.Format == (int)MetadataFormat.Novel)
            return nameof(MetadataFormat.Novel);
        return record.Origin is { } origin ? ((MetadataOrigin)origin).ToString() : null;
    }

    // --- Validation and writes ---

    /// <summary>Cleaned values of a request, or a machine error code.</summary>
    internal static (DeclaredFacts? Values, string? Error) Validate(SetDeclaredFactsRequest request)
    {
        if (request.Type is { } t && !Enum.IsDefined(t))
            return (null, "type_invalid");
        var input = request.Creators ?? [];
        if (input.Count > DeclaredFactKeys.MaxCreators)
            return (null, "creators_too_many");
        var creators = new List<DeclaredCreator>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in input)
        {
            var name = DeclaredFactKeys.CleanName(c?.Name);
            if (name is null)
                return (null, "creator_name_invalid");
            string? role = null;
            if (!string.IsNullOrWhiteSpace(c!.Role))
            {
                role = c.Role.Trim().ToLowerInvariant();
                if (!DeclaredFactKeys.CreatorRoles.Contains(role))
                    return (null, "creator_role_invalid");
            }
            // The same person with the same role once; spelling differences in case or accents are the same entry.
            if (seen.Add(DeclaredFactsComparer.NameKey(name) + "\u0001" + role))
                creators.Add(new DeclaredCreator(name, role));
        }
        var type = request.Type is { } declared ? DeclaredFactKeys.TypeSlug(declared) : null;
        return (new DeclaredFacts(type, creators), null);
    }

    /// <summary>1.39.0: cleaned edition facts of a request, or a machine error code (<c>volumes_invalid</c>, <c>edition_invalid</c>).</summary>
    internal static (DeclaredEditionFacts? Values, string? Error) ValidateEdition(SetDeclaredEditionRequest request)
    {
        if (request.VolumeTotal is { } n && (n < 1 || n > DeclaredFactKeys.MaxVolumeTotal))
            return (null, "volumes_invalid");
        if (request.Edition is { } e && !Enum.IsDefined(e))
            return (null, "edition_invalid");
        return (new DeclaredEditionFacts(request.VolumeTotal, request.Edition, !request.Tracking), null);
    }

    /// <summary>
    /// 1.39.0: replaces the edition keys (volumes, edition, tracking) of one folder; rows of other keys (type, creator) are left alone.
    /// One row per key, written only when set (tracking only when off).
    /// </summary>
    private async Task ReplaceEditionAsync(long libraryId, long nodeId, DeclaredEditionFacts values, string? actor, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var existing = await _db.DeclaredFacts
            .Where(f => f.LibraryId == libraryId && f.NodeId == nodeId
                && (f.Key == DeclaredFactKeys.Volumes || f.Key == DeclaredFactKeys.Edition || f.Key == DeclaredFactKeys.Tracking))
            .ToListAsync(ct);
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        if (values.VolumeTotal is { } volumes)
            wanted[DeclaredFactKeys.Volumes] = volumes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (values.Edition is { } edition)
            wanted[DeclaredFactKeys.Edition] = DeclaredFactKeys.EditionSlug(edition);
        if (values.TrackingOff)
            wanted[DeclaredFactKeys.Tracking] = DeclaredFactKeys.TrackingOff;

        var changed = false;
        var keep = new HashSet<DeclaredFactEntity>();
        foreach (var (key, value) in wanted)
        {
            var row = existing.FirstOrDefault(f => !keep.Contains(f) && f.Key == key);
            if (row is null)
            {
                row = new DeclaredFactEntity { LibraryId = libraryId, NodeId = nodeId, Key = key, Value = value, CreatedAt = now, UpdatedAt = now };
                _db.DeclaredFacts.Add(row);
                changed = true;
            }
            else if (!string.Equals(row.Value, value, StringComparison.Ordinal) || row.Role is not null)
            {
                row.Value = value;
                row.Role = null;
                row.UpdatedAt = now;
                changed = true;
            }
            keep.Add(row);
        }
        var removed = existing.Where(f => !keep.Contains(f)).ToList();
        if (!changed && removed.Count == 0)
            return;
        _db.DeclaredFacts.RemoveRange(removed);
        await _db.SaveChangesAsync(ct);

        var cleared = wanted.Count == 0;
        await _audit.RecordAsync(cleared ? AuditActions.DeclaredFactsClear : AuditActions.DeclaredFactsSet, AuditResults.Success, actor,
            ct: ct, targetLibraryId: libraryId, targetItemId: nodeId);
        _logger.LogInformation(LogEvents.Metadata.DeclaredEditionChanged,
            "Declared edition {Change} on folder {NodeId}: volumes {HasVolumes}, edition {HasEdition}, tracking off {TrackingOff}",
            cleared ? "cleared" : "set", nodeId, values.VolumeTotal is not null, values.Edition is not null, values.TrackingOff);
    }

    /// <summary>Replaces the v1 keys (type, creator) of one scope; rows of other keys are left alone.</summary>
    private async Task ReplaceAsync(long libraryId, long? nodeId, DeclaredFacts values, string? actor, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var existing = await _db.DeclaredFacts
            .Where(f => f.LibraryId == libraryId && f.NodeId == nodeId
                && (f.Key == DeclaredFactKeys.Type || f.Key == DeclaredFactKeys.Creator))
            .ToListAsync(ct);

        var wanted = new List<(string Key, string Value, string? Role)>();
        if (values.Type is { } type)
            wanted.Add((DeclaredFactKeys.Type, type, null));
        wanted.AddRange(values.Creators.Select(c => (DeclaredFactKeys.Creator, c.Name, c.Role)));

        // Keep a row that still applies (its CreatedAt stays); add the new ones; remove the rest.
        var changed = false;
        var keep = new HashSet<DeclaredFactEntity>();
        for (var i = 0; i < wanted.Count; i++)
        {
            var (key, value, role) = wanted[i];
            var row = existing.FirstOrDefault(f => !keep.Contains(f) && f.Key == key
                && string.Equals(f.Value, value, StringComparison.Ordinal) && string.Equals(f.Role, role, StringComparison.Ordinal));
            if (row is null)
            {
                row = new DeclaredFactEntity { LibraryId = libraryId, NodeId = nodeId, Key = key, Value = value, Role = role, CreatedAt = now };
                _db.DeclaredFacts.Add(row);
                changed = true;
            }
            else if (row.Position != i)
            {
                changed = true;
            }
            if (row.Id == 0 || row.Position != i)
            {
                row.Position = i;
                row.UpdatedAt = now;
            }
            keep.Add(row);
        }
        var removed = existing.Where(f => !keep.Contains(f)).ToList();
        if (!changed && removed.Count == 0)
            return;
        _db.DeclaredFacts.RemoveRange(removed);
        await _db.SaveChangesAsync(ct);

        var cleared = wanted.Count == 0;
        await _audit.RecordAsync(cleared ? AuditActions.DeclaredFactsClear : AuditActions.DeclaredFactsSet, AuditResults.Success, actor,
            ct: ct, targetLibraryId: libraryId, targetItemId: nodeId);
        _logger.LogInformation(LogEvents.Metadata.DeclaredFactsChanged,
            "Declared facts {Change} on {Scope} {ScopeId}: type {HasType}, {CreatorCount} creators",
            cleared ? "cleared" : "set", nodeId is null ? "library" : "folder", nodeId ?? libraryId,
            values.Type is not null, values.Creators.Count);
    }

    // --- Views ---

    private async Task<DeclaredFactsScopeDto> FolderScopeDtoAsync(CatalogNodeEntity node, CancellationToken ct)
    {
        var chain = await ChainAsync(node, ct);
        var library = await _db.Libraries.AsNoTracking().FirstAsync(l => l.Id == node.LibraryId, ct);
        // Inherited = what applies from the parent upwards (what the folder falls back to when cleared).
        var (inherited, from) = await EffectiveAsync(library, chain.Skip(1).ToList(), ct);
        var ownRows = await DeclaredFactsResolution.LoadRowsAsync(_db, f => f.NodeId == node.Id, ct);
        return new DeclaredFactsScopeDto
        {
            NodeId = node.PublicId,
            LibraryId = library.PublicId,
            DisplayName = node.DisplayName,
            Own = ToValuesDto(DeclaredFactsResolution.OwnFacts(ownRows)) with { Edition = await OwnEditionDtoAsync(node.Id, ct) },
            Inherited = ToEffectiveDto(Downward(inherited), from),
        };
    }

    private async Task<DeclaredFactsScopeDto> LibraryScopeDtoAsync(LibraryEntity library, CancellationToken ct)
    {
        var rows = await DeclaredFactsResolution.LoadRowsAsync(_db, f => f.LibraryId == library.Id && f.NodeId == null, ct);
        return new DeclaredFactsScopeDto
        {
            NodeId = null,
            LibraryId = library.PublicId,
            DisplayName = library.DisplayName,
            Own = ToValuesDto(DeclaredFactsResolution.OwnFacts(rows)),
            Inherited = new EffectiveDeclaredFactsDto(),
        };
    }

    /// <summary>
    /// Effective facts at the first node of <paramref name="chain"/> (self first, then ancestors; an empty chain
    /// = the library scope alone), and the display names of the scopes each key comes from.
    /// </summary>
    private async Task<(DeclaredFacts Facts, (string? Type, string? Creators) From)> EffectiveAsync(
        LibraryEntity library, IReadOnlyList<ChainEntry> chain, CancellationToken ct)
    {
        var ids = chain.Select(c => c.Id).ToList();
        var rows = await DeclaredFactsResolution.LoadRowsAsync(_db,
            f => f.LibraryId == library.Id && (f.NodeId == null || ids.Contains(f.NodeId.Value)), ct);
        var own = DeclaredFactsResolution.GroupByScope(rows, out var libraryFacts);

        // Nearest scope per key, walking from the node up to the library.
        string? typeFrom = libraryFacts.Type is null ? null : library.DisplayName;
        string? creatorsFrom = libraryFacts.Creators.Count == 0 ? null : library.DisplayName;
        var facts = DeclaredFactsResolution.AsSource(libraryFacts, DeclaredFactSource.Library);
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var mine = own.GetValueOrDefault(chain[i].Id);
            if (mine?.Type is not null)
                typeFrom = chain[i].Name;
            if (mine is { Creators.Count: > 0 })
                creatorsFrom = chain[i].Name;
            facts = DeclaredFactsResolution.Combine(mine, facts);
        }
        return (facts, (typeFrom, creatorsFrom));
    }

    /// <summary>1.39.0: the edition facts declared on the folder itself, or null when none are set.</summary>
    private async Task<DeclaredEditionDto?> OwnEditionDtoAsync(long nodeId, CancellationToken ct)
    {
        var facts = (await DeclaredFactsResolution.LoadEditionAsync(_db, [nodeId], ct)).GetValueOrDefault(nodeId);
        return facts is null || facts.IsEmpty
            ? null
            : new DeclaredEditionDto { VolumeTotal = facts.VolumeTotal, Edition = facts.Edition, Tracking = !facts.TrackingOff };
    }

    /// <summary>Facts seen from a child: Own becomes Inherited (Library stays Library).</summary>
    private static DeclaredFacts Downward(DeclaredFacts facts) => DeclaredFactsResolution.Combine(null, facts);

    private static DeclaredFactValuesDto ToValuesDto(DeclaredFacts facts) => new()
    {
        Type = facts.TypeValue,
        Creators = facts.Creators.Select(c => new DeclaredCreatorDto { Name = c.Name, Role = c.Role }).ToList(),
    };

    private static EffectiveDeclaredFactsDto ToEffectiveDto(DeclaredFacts facts, (string? Type, string? Creators) from) => new()
    {
        Type = facts.TypeValue,
        TypeSource = facts.TypeValue is null ? null : facts.TypeSource,
        TypeFrom = facts.TypeValue is null ? null : from.Type,
        Creators = facts.Creators.Select(c => new DeclaredCreatorDto { Name = c.Name, Role = c.Role }).ToList(),
        CreatorsSource = facts.CreatorsSource,
        CreatorsFrom = facts.Creators.Count == 0 ? null : from.Creators,
    };

    // --- Lookups ---

    private sealed record ChainEntry(long Id, string Name);

    /// <summary>The node, then its ancestors (nearest first), bounded like the series-info walk.</summary>
    private async Task<List<ChainEntry>> ChainAsync(CatalogNodeEntity node, CancellationToken ct)
    {
        var chain = new List<ChainEntry> { new(node.Id, node.DisplayName) };
        var parentId = node.ParentId;
        for (var i = 0; parentId is { } id && i < SeriesInfoResolver.MaxWalkDepth; i++)
        {
            var parent = await _db.CatalogNodes.AsNoTracking()
                .Where(n => n.Id == id)
                .Select(n => new { n.Id, n.ParentId, n.DisplayName })
                .FirstOrDefaultAsync(ct);
            if (parent is null)
                break;
            chain.Add(new ChainEntry(parent.Id, parent.DisplayName));
            parentId = parent.ParentId;
        }
        return chain;
    }

    private async Task<(MetadataLinkResultCode Code, CatalogNodeEntity? Node)> FolderAsync(string nodePublicId, CancellationToken ct)
    {
        var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == nodePublicId, ct);
        if (node is null || node.Availability == (int)CatalogNodeAvailability.Tombstoned)
            return (MetadataLinkResultCode.NodeNotFound, null);
        return node.Kind != (int)CatalogNodeKind.Folder ? (MetadataLinkResultCode.NotAFolder, null) : (MetadataLinkResultCode.Ok, node);
    }

    private Task<LibraryEntity?> LibraryAsync(string libraryPublicId, CancellationToken ct) =>
        _db.Libraries.AsNoTracking().FirstOrDefaultAsync(l => l.PublicId == libraryPublicId, ct);
}
