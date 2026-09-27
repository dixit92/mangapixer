namespace com.lifepixer.mangapixer.Server.Features.Metadata.Review;

using System.Globalization;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Flags;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The admin review dashboard (metadata stage 2). Tabs are keyset-paged lists
/// (cursor = the last row id) with an optional library filter:
/// - Needs review / Auto-linked / Confirmed / Don't match: own link rows of that
///   state on live nodes (Auto-linked lists work anchors only, not archive-group
///   members, newest first);
/// - Unmatched: decided works with no confident match (and works that failed);
/// - Flags: anchors with open flags, automatic links first;
/// - Missing folders: removed folders still carrying admin rows.
/// Reading never contacts a provider. Accept links a STORED candidate (one gated GET
/// when its record is not stored yet); bulk actions reuse the stage-1 link service.
/// </summary>
public sealed class MetadataReviewService
{
    public const int MaxBulk = 200;
    private const int TrailDepth = 3;

    private readonly MangaPixerDbContext _db;
    private readonly MetadataLinkService _links;
    private readonly MetadataIdentifyService _identify;
    private readonly MetadataAutoMatchService _autoMatch;
    private readonly MetadataCarryOverService _carryOver;
    private readonly AuditService _audit;
    private readonly ILogger<MetadataReviewService> _logger;

    public MetadataReviewService(
        MangaPixerDbContext db,
        MetadataLinkService links,
        MetadataIdentifyService identify,
        MetadataAutoMatchService autoMatch,
        MetadataCarryOverService carryOver,
        AuditService audit,
        ILogger<MetadataReviewService> logger)
    {
        _db = db;
        _links = links;
        _identify = identify;
        _autoMatch = autoMatch;
        _carryOver = carryOver;
        _audit = audit;
        _logger = logger;
    }

    // --- Summary ---

    public async Task<MetadataReviewSummaryDto?> SummaryAsync(string? libraryPublicId, CancellationToken ct = default)
    {
        var (ok, libraryId) = await LibraryFilterAsync(libraryPublicId, ct);
        if (!ok)
            return null;
        return new MetadataReviewSummaryDto
        {
            NeedsReview = await LinksOf(SeriesLinkState.NeedsReview, libraryId).CountAsync(ct),
            AutoLinked = await AutoAnchors(libraryId).CountAsync(ct),
            Unmatched = await UnmatchedRows(libraryId).CountAsync(ct),
            OpenFlags = await OpenFlags(libraryId).CountAsync(ct),
            DontMatch = await LinksOf(SeriesLinkState.DontMatch, libraryId).CountAsync(ct),
            Confirmed = await LinksOf(SeriesLinkState.Confirmed, libraryId).CountAsync(ct),
            MissingFolders = await _carryOver.StrandedFolderIds(libraryId).CountAsync(ct),
            Pending = await _db.MetadataMatchQueue
                .Where(q => (q.State == QueueState.Pending || q.State == QueueState.Leased) && (libraryId == null || q.LibraryId == libraryId))
                .CountAsync(ct),
        };
    }

    // --- Tab queries (ids + keyset key) ---

    private IQueryable<NodeSeriesLinkEntity> LinksOf(SeriesLinkState state, long? libraryId)
    {
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var s = (int)state;
        return _db.NodeSeriesLinks.AsNoTracking()
            .Where(l => l.State == s && (libraryId == null || l.LibraryId == libraryId)
                && _db.CatalogNodes.Any(n => n.Id == l.NodeId && n.Availability != tombstoned));
    }

    /// <summary>Automatic links on work anchors (archive-group members have no queue row of their own).</summary>
    private IQueryable<NodeSeriesLinkEntity> AutoAnchors(long? libraryId) =>
        LinksOf(SeriesLinkState.Auto, libraryId).Where(l => _db.MetadataMatchQueue.Any(q => q.NodeId == l.NodeId));

    private IQueryable<MetadataMatchQueueEntity> UnmatchedRows(long? libraryId)
    {
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var unmatched = (int)MatchBand.Unmatched;
        return _db.MetadataMatchQueue.AsNoTracking()
            .Where(q => (libraryId == null || q.LibraryId == libraryId)
                && ((q.Outcome == unmatched && (q.State == QueueState.Done || q.State == QueueState.Pending)) || q.State == QueueState.Failed)
                && !_db.NodeSeriesLinks.Any(l => l.NodeId == q.NodeId)
                && _db.CatalogNodes.Any(n => n.Id == q.NodeId && n.Availability != tombstoned));
    }

    private IQueryable<MetadataFlagEntity> OpenFlags(long? libraryId) =>
        _db.MetadataFlags.AsNoTracking().Where(f => f.State == (int)MetadataFlagState.Open && (libraryId == null || f.LibraryId == libraryId));

    // --- List ---

    public async Task<(string? Error, MetadataReviewPageDto? Page)> ListAsync(
        MetadataReviewTab tab, string? libraryPublicId, string? cursor, int limit, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(tab))
            return ("invalid_tab", null);
        var (ok, libraryId) = await LibraryFilterAsync(libraryPublicId, ct);
        if (!ok)
            return ("library_not_found", null);
        limit = Math.Clamp(limit, 1, 100);
        long? after = long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : null;

        List<(long Key, long NodeId)> rows;
        int total;
        switch (tab)
        {
            case MetadataReviewTab.NeedsReview:
            case MetadataReviewTab.AutoLinked:
            case MetadataReviewTab.Confirmed:
            case MetadataReviewTab.DontMatch:
                {
                    var q = tab switch
                    {
                        MetadataReviewTab.NeedsReview => LinksOf(SeriesLinkState.NeedsReview, libraryId),
                        MetadataReviewTab.AutoLinked => AutoAnchors(libraryId),
                        MetadataReviewTab.Confirmed => LinksOf(SeriesLinkState.Confirmed, libraryId),
                        _ => LinksOf(SeriesLinkState.DontMatch, libraryId),
                    };
                    total = await q.CountAsync(ct);
                    if (after is { } a)
                        q = q.Where(l => l.Id < a);
                    rows = (await q.OrderByDescending(l => l.Id).Take(limit + 1).Select(l => new { l.Id, l.NodeId }).ToListAsync(ct))
                        .Select(x => (x.Id, x.NodeId)).ToList();
                    break;
                }
            case MetadataReviewTab.Unmatched:
                {
                    var q = UnmatchedRows(libraryId);
                    total = await q.CountAsync(ct);
                    if (after is { } a)
                        q = q.Where(r => r.Id < a);
                    rows = (await q.OrderByDescending(r => r.Id).Take(limit + 1).Select(r => new { r.Id, r.NodeId }).ToListAsync(ct))
                        .Select(x => (x.Id, x.NodeId)).ToList();
                    break;
                }
            case MetadataReviewTab.Flags:
                {
                    // One row per anchor, keyed by its newest open flag; automatic links first within a page.
                    var q = OpenFlags(libraryId).GroupBy(f => f.NodeId).Select(g => new { NodeId = g.Key, Key = g.Max(f => f.Id) });
                    total = await q.CountAsync(ct);
                    if (after is { } a)
                        q = q.Where(x => x.Key < a);
                    rows = (await q.OrderByDescending(x => x.Key).Take(limit + 1).ToListAsync(ct)).Select(x => (x.Key, x.NodeId)).ToList();
                    break;
                }
            default:
                {
                    var q = _carryOver.StrandedFolderIds(libraryId);
                    total = await q.CountAsync(ct);
                    if (after is { } a)
                        q = q.Where(id => id < a);
                    rows = (await q.OrderByDescending(id => id).Take(limit + 1).ToListAsync(ct)).Select(id => (id, id)).ToList();
                    break;
                }
        }

        var hasMore = rows.Count > limit;
        rows = rows.Take(limit).ToList();
        var items = await BuildItemsAsync(tab, rows.Select(r => r.NodeId).ToList(), ct);
        if (tab == MetadataReviewTab.Flags)
            items = items.OrderByDescending(i => i.Link?.State == SeriesLinkState.Auto).ToList();
        return (null, new MetadataReviewPageDto
        {
            Tab = tab,
            Items = items,
            Total = total,
            HasMore = hasMore,
            NextCursor = hasMore ? rows[^1].Key.ToString(CultureInfo.InvariantCulture) : null,
        });
    }

    private async Task<(bool Ok, long? LibraryId)> LibraryFilterAsync(string? libraryPublicId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(libraryPublicId))
            return (true, null);
        var id = await _db.Libraries.Where(l => l.PublicId == libraryPublicId).Select(l => (long?)l.Id).FirstOrDefaultAsync(ct);
        return (id is not null, id);
    }

    private sealed record NodeRow(long Id, string PublicId, long LibraryId, long? ParentId, int Kind, string DisplayName, int Availability);

    /// <summary>Builds the rows of a page with a fixed number of queries (no per-row query).</summary>
    private async Task<List<MetadataReviewItemDto>> BuildItemsAsync(MetadataReviewTab tab, IReadOnlyList<long> nodeIds, CancellationToken ct)
    {
        if (nodeIds.Count == 0)
            return [];
        var nodes = await _db.CatalogNodes.AsNoTracking()
            .Where(n => nodeIds.Contains(n.Id))
            .Select(n => new NodeRow(n.Id, n.PublicId, n.LibraryId, n.ParentId, n.Kind, n.DisplayName, n.Availability))
            .ToDictionaryAsync(n => n.Id, ct);
        var libraries = await _db.Libraries.AsNoTracking()
            .Where(l => nodes.Values.Select(n => n.LibraryId).Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => (l.PublicId, l.DisplayName), ct);
        var links = await _db.NodeSeriesLinks.AsNoTracking().Where(l => nodeIds.Contains(l.NodeId)).ToDictionaryAsync(l => l.NodeId, ct);
        var recordIds = links.Values.Where(l => l.RecordId != null).Select(l => l.RecordId!.Value).ToList();
        var records = await _db.MetadataRecords.AsNoTracking().Where(r => recordIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var queue = await _db.MetadataMatchQueue.AsNoTracking().Where(q => nodeIds.Contains(q.NodeId)).ToDictionaryAsync(q => q.NodeId, ct);
        var runIds = queue.Values.Where(q => q.RunId != null).Select(q => q.RunId!.Value).Distinct().ToList();
        var runs = await _db.MetadataMatchRuns.AsNoTracking().Where(r => runIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.PublicId, ct);
        var candidates = (await _db.MetadataMatchCandidates.AsNoTracking().Where(c => nodeIds.Contains(c.NodeId)).OrderBy(c => c.Rank).ToListAsync(ct))
            .GroupBy(c => c.NodeId).ToDictionary(g => g.Key, g => g.ToList());
        var flagCounts = await OpenFlags(null).Where(f => nodeIds.Contains(f.NodeId)).GroupBy(f => f.NodeId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var flags = tab == MetadataReviewTab.Flags
            ? (await MetadataFlagService.ToAdminDtosAsync(_db, await OpenFlags(null).Where(f => nodeIds.Contains(f.NodeId)).OrderByDescending(f => f.Id).ToListAsync(ct), ct))
                .GroupBy(f => f.NodeId).ToDictionary(g => g.Key, g => g.ToList())
            : [];
        var trails = await TrailsAsync(nodes.Values.ToList(), ct);
        var archiveCounts = await ArchiveCountsAsync(nodes.Values.Where(n => n.Kind == (int)CatalogNodeKind.Folder).Select(n => n.Id).ToList(), ct);
        var folderCovers = await Catalog.FolderCovers.ResolveAsync(_db,
            nodes.Values.Where(n => n.Kind == (int)CatalogNodeKind.Folder).Select(n => n.Id).ToList(), ct);

        var memberIds = queue.Values.SelectMany(q => Members(q)).Distinct().ToList();
        var memberPublic = await _db.CatalogNodes.AsNoTracking().Where(n => memberIds.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, n => n.PublicId, ct);

        var items = new List<MetadataReviewItemDto>();
        foreach (var id in nodeIds)
        {
            if (!nodes.TryGetValue(id, out var node))
                continue;
            links.TryGetValue(id, out var link);
            queue.TryGetValue(id, out var q);
            var members = q is null ? [] : Members(q);
            var library = libraries.GetValueOrDefault(node.LibraryId);
            var coverUrl = node.Kind == (int)CatalogNodeKind.Folder
                ? folderCovers.TryGetValue(node.Id, out var coverId) ? Catalog.FolderCovers.ArchiveCoverUrl(coverId) : null
                : node.Availability != (int)CatalogNodeAvailability.Tombstoned ? Catalog.FolderCovers.ArchiveCoverUrl(node.PublicId) : null;
            items.Add(new MetadataReviewItemDto
            {
                NodeId = node.PublicId,
                CoverUrl = coverUrl,
                NodeKind = (CatalogNodeKind)node.Kind,
                DisplayName = node.DisplayName,
                LibraryId = library.PublicId ?? string.Empty,
                LibraryName = library.DisplayName ?? string.Empty,
                Trail = trails.GetValueOrDefault(id) ?? [],
                Missing = node.Availability == (int)CatalogNodeAvailability.Tombstoned,
                WorkClass = q?.WorkClass is { } wc ? (WorkClass)wc : null,
                MatchLevel = q is null ? null : (MatchLevel)q.Level,
                ItemCount = node.Kind == (int)CatalogNodeKind.Folder ? archiveCounts.GetValueOrDefault(id) : 1 + members.Count,
                MemberNodeIds = members.Select(m => memberPublic.GetValueOrDefault(m)).OfType<string>().ToList(),
                Link = link is null ? null : ToLinkDto(link, node.PublicId, link.RecordId is { } rid ? records.GetValueOrDefault(rid) : null),
                Candidates = candidates.TryGetValue(id, out var list) ? list.Select(c => ToCandidateDto(c, node.LibraryId)).ToList() : [],
                Reasons = q is null ? [] : MatchReasonCodes.Of(q.OutcomeReasons),
                MatchedAt = q?.CompletedAt,
                NextRetryAt = q is { Outcome: (int)MatchBand.Unmatched } ? q.NotBefore : null,
                RunId = q?.RunId is { } run ? runs.GetValueOrDefault(run) : null,
                OpenFlagCount = flagCounts.GetValueOrDefault(id),
                Flags = flags.TryGetValue(node.PublicId, out var nodeFlags) ? nodeFlags : [],
            });
        }
        return items;
    }

    private static List<long> Members(MetadataMatchQueueEntity q) =>
        q.MemberNodeIdsJson is { } json ? JsonSerializer.Deserialize<List<long>>(json) ?? [] : [];

    internal static MetadataReviewLinkDto ToLinkDto(NodeSeriesLinkEntity link, string nodePublicId, MetadataRecordEntity? record) => new()
    {
        State = (SeriesLinkState)link.State,
        Provider = record?.Provider,
        ExternalId = record?.ExternalId,
        RecordId = record?.PublicId,
        Title = record?.Title,
        MatchMethod = (MetadataMatchMethod?)link.MatchMethod,
        MatchScore = link.MatchScore,
        ImageUrl = record is { ImageState: 1 } ? SeriesInfoResolver.ImageUrlFor(nodePublicId, record) : null,
        UpdatedAt = link.UpdatedAt,
    };

    private MetadataReviewCandidateDto ToCandidateDto(MetadataMatchCandidateEntity c, long libraryId) => new()
    {
        Rank = c.Rank,
        Provider = c.Provider,
        ExternalId = c.ExternalId,
        Title = c.Title,
        ProviderType = c.ProviderType,
        Format = (MetadataFormat?)c.Format,
        Origin = (MetadataOrigin?)c.Origin,
        Year = c.Year,
        Volumes = c.Volumes,
        TitleScore = c.TitleScore,
        AdjustedScore = c.AdjustedScore,
        Reasons = MatchReasonCodes.Of(c.Reasons),
        ImageToken = c.ImageRemoteUrl is { } url ? _identify.IssueImageToken(c.Provider, libraryId, url) : null,
    };

    /// <summary>Up to 3 ancestor names per node, outermost first (3 batched queries).</summary>
    private async Task<Dictionary<long, List<string>>> TrailsAsync(IReadOnlyList<NodeRow> nodes, CancellationToken ct)
    {
        var parentOf = nodes.ToDictionary(n => n.Id, n => n.ParentId);
        var names = new Dictionary<long, (string Name, long? ParentId)>();
        var frontier = nodes.Where(n => n.ParentId != null).Select(n => n.ParentId!.Value).Distinct().ToList();
        for (var level = 0; level < TrailDepth && frontier.Count > 0; level++)
        {
            var batch = await _db.CatalogNodes.AsNoTracking().Where(n => frontier.Contains(n.Id))
                .Select(n => new { n.Id, n.DisplayName, n.ParentId }).ToListAsync(ct);
            foreach (var b in batch)
                names[b.Id] = (b.DisplayName, b.ParentId);
            frontier = batch.Where(b => b.ParentId != null && !names.ContainsKey(b.ParentId.Value)).Select(b => b.ParentId!.Value).Distinct().ToList();
        }
        var result = new Dictionary<long, List<string>>();
        foreach (var n in nodes)
        {
            var trail = new List<string>();
            var current = n.ParentId;
            while (current is { } p && trail.Count < TrailDepth && names.TryGetValue(p, out var entry))
            {
                trail.Insert(0, entry.Name);
                current = entry.ParentId;
            }
            result[n.Id] = trail;
        }
        return result;
    }

    /// <summary>Live archives below each folder (one recursive query for the page).</summary>
    private async Task<Dictionary<long, int>> ArchiveCountsAsync(IReadOnlyList<long> folderIds, CancellationToken ct)
    {
        if (folderIds.Count == 0)
            return [];
        var ids = string.Join(',', folderIds.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        // Ids are longs from our own query (no user input); the IN list is formatted, not interpolated from a request.
#pragma warning disable EF1002
        var rows = await _db.Database.SqlQueryRaw<ArchiveCountRow>($"""
            WITH RECURSIVE sub(root, id, kind) AS (
                SELECT n."Id", n."Id", n."Kind" FROM catalog_nodes n WHERE n."Id" IN ({ids})
                UNION ALL
                SELECT sub.root, c."Id", c."Kind" FROM catalog_nodes c JOIN sub ON c."ParentId" = sub.id WHERE c."Availability" != 5
            )
            SELECT root AS "Root", COUNT(*) AS "Count" FROM sub WHERE kind = 1 GROUP BY root
            """).ToListAsync(ct);
#pragma warning restore EF1002
        return rows.ToDictionary(r => r.Root, r => r.Count);
    }

    private sealed record ArchiveCountRow(long Root, int Count);

    // --- Accept ---

    /// <summary>Links a review row to one of its stored candidates (Confirmed). Group members follow the anchor.</summary>
    public async Task<(string? Error, NodeSeriesLinkChangeDto? Change)> AcceptAsync(string nodePublicId, int rank, string? actor, CancellationToken ct = default)
    {
        var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == nodePublicId, ct);
        if (node is null || node.Availability == (int)CatalogNodeAvailability.Tombstoned)
            return ("not_found", null);
        var candidate = await _db.MetadataMatchCandidates.AsNoTracking().FirstOrDefaultAsync(c => c.NodeId == node.Id && c.Rank == rank, ct);
        if (candidate is null)
            return ("no_candidate", null);

        var (code, change) = await _identify.LinkAsync(nodePublicId, new LinkSeriesRequest
        {
            Provider = candidate.Provider,
            ExternalId = candidate.ExternalId,
            MatchMethod = MetadataMatchMethod.Auto,
            MatchScore = candidate.TitleScore,
        }, actor, ct);
        if (code != MetadataLinkResultCode.Ok)
            return (code == MetadataLinkResultCode.NodeNotFound ? "not_found" : "record_not_found", null);

        await LinkMembersAsync(node.Id, candidate.Provider, candidate.ExternalId, actor, ct);
        await _audit.RecordAsync(AuditActions.MetadataReviewAccept, rank == 1 ? "top" : "other", actor, ct: ct,
            targetLibraryId: node.LibraryId, targetItemId: node.Id);
        return (null, change);
    }

    /// <summary>An archive group's other archives get the same confirmed link as the anchor (unless they have their own row).</summary>
    private async Task LinkMembersAsync(long anchorId, string provider, string externalId, string? actor, CancellationToken ct)
    {
        var json = await _db.MetadataMatchQueue.AsNoTracking().Where(q => q.NodeId == anchorId).Select(q => q.MemberNodeIdsJson).FirstOrDefaultAsync(ct);
        if (json is null)
            return;
        var members = JsonSerializer.Deserialize<List<long>>(json) ?? [];
        var linked = await _db.NodeSeriesLinks.AsNoTracking()
            .Where(l => members.Contains(l.NodeId) && l.State != (int)SeriesLinkState.Auto && l.State != (int)SeriesLinkState.NeedsReview)
            .Select(l => l.NodeId).ToListAsync(ct);
        var publicIds = await _db.CatalogNodes.AsNoTracking()
            .Where(n => members.Contains(n.Id) && !linked.Contains(n.Id) && n.Availability != (int)CatalogNodeAvailability.Tombstoned)
            .Select(n => n.PublicId).ToListAsync(ct);
        foreach (var memberId in publicIds)
            await _links.LinkAsync(memberId, new LinkSeriesRequest { Provider = provider, ExternalId = externalId, MatchMethod = MetadataMatchMethod.Auto }, actor, ct);
    }

    // --- Bulk ---

    public async Task<(string? Error, MetadataReviewBulkResultDto? Result)> BulkAsync(MetadataReviewBulkRequest request, string? actor, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(request.Action))
            return ("invalid_action", null);
        var ids = (request.NodeIds ?? []).Where(i => !string.IsNullOrEmpty(i)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0 || ids.Count > MaxBulk)
            return ("invalid_node_ids", null);

        var results = new List<MetadataReviewBulkItemResultDto>();
        if (request.Action == MetadataReviewBulkAction.RerunMatching)
        {
            var codes = await _autoMatch.RerunAsync(ids, actor, ct);
            results.AddRange(ids.Select(id => new MetadataReviewBulkItemResultDto { NodeId = id, Code = codes.GetValueOrDefault(id, "not_found") }));
        }
        else
        {
            foreach (var id in ids)
            {
                string code;
                try
                {
                    code = await ApplyOneAsync(request.Action, id, actor, ct);
                }
                catch (MetadataGatewayException ex)
                {
                    code = ex.Code;
                }
                results.Add(new MetadataReviewBulkItemResultDto { NodeId = id, Code = code });
            }
        }
        var succeeded = results.Count(r => r.Code == "ok");
        _logger.LogInformation(LogEvents.Metadata.ReviewAction, "Review bulk {Action}: {Succeeded} ok, {Failed} failed",
            request.Action, succeeded, results.Count - succeeded);
        return (null, new MetadataReviewBulkResultDto
        {
            Action = request.Action,
            Succeeded = succeeded,
            Failed = results.Count - succeeded,
            Results = results,
        });
    }

    private async Task<string> ApplyOneAsync(MetadataReviewBulkAction action, string nodePublicId, string? actor, CancellationToken ct)
    {
        switch (action)
        {
            case MetadataReviewBulkAction.AcceptTop:
                return (await AcceptAsync(nodePublicId, 1, actor, ct)).Error ?? "ok";
            case MetadataReviewBulkAction.DontMatch:
                return Code((await _links.SetDontMatchAsync(nodePublicId, actor, ct)).Code);
            case MetadataReviewBulkAction.Unlink:
                return Code((await _links.RemoveAsync(nodePublicId, onlyDontMatch: false, actor, ct)).Code);
            case MetadataReviewBulkAction.Confirm:
                return await ConfirmAsync(nodePublicId, actor, ct);
            default:
                return "invalid_action";
        }
    }

    /// <summary>Auto -> Confirmed on the anchor and its group members (no network, same record).</summary>
    private async Task<string> ConfirmAsync(string nodePublicId, string? actor, CancellationToken ct)
    {
        var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == nodePublicId, ct);
        if (node is null)
            return "not_found";
        var json = await _db.MetadataMatchQueue.AsNoTracking().Where(q => q.NodeId == node.Id).Select(q => q.MemberNodeIdsJson).FirstOrDefaultAsync(ct);
        var ids = (json is null ? [] : JsonSerializer.Deserialize<List<long>>(json) ?? []).Prepend(node.Id).ToList();
        var updated = await _db.NodeSeriesLinks
            .Where(l => ids.Contains(l.NodeId) && l.State == (int)SeriesLinkState.Auto)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.State, (int)SeriesLinkState.Confirmed), ct);
        if (updated == 0)
            return "not_auto";
        await _audit.RecordAsync(AuditActions.MetadataReviewConfirm, AuditResults.Success, actor, ct: ct, targetLibraryId: node.LibraryId, targetItemId: node.Id);
        return "ok";
    }

    private static string Code(MetadataLinkResultCode code) => code switch
    {
        MetadataLinkResultCode.Ok => "ok",
        MetadataLinkResultCode.NodeNotFound => "not_found",
        MetadataLinkResultCode.RecordNotFound => "record_not_found",
        _ => "invalid_request",
    };
}
