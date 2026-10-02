namespace com.lifepixer.mangapixer.Server.Features.Metadata.Flags;

using System.Globalization;
using System.Security.Cryptography;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Metadata.Review;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// "Wrong series?" flags (metadata stage 2, owner decision 12). A user with access
/// to a node may flag the web series information it shows; the flag sits on the
/// series ANCHOR (the node holding the link). Limits: one open flag per user and
/// anchor; per UTC day max(20, 2% of the anchors the user can see). The note is
/// user content: plain text, at most 500 characters, shown to admins only and
/// never logged (logs carry flag id, node id and the reason code). Admins resolve a
/// flag by unlinking, Don't match, re-identifying (recorded as Relinked) or
/// dismissing; the resolution applies to every open flag on that anchor.
/// </summary>
public sealed class MetadataFlagService
{
    public const int MaxNoteLength = 500;
    public const int MinDailyCap = 20;
    public const double DailyCapShare = 0.02;

    private readonly MangaPixerDbContext _db;
    private readonly LibraryAuthorizationService _libraryAuth;
    private readonly SeriesInfoResolver _resolver;
    private readonly MetadataLinkService _links;
    private readonly AuditService _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<MetadataFlagService> _logger;

    public MetadataFlagService(
        MangaPixerDbContext db,
        LibraryAuthorizationService libraryAuth,
        SeriesInfoResolver resolver,
        MetadataLinkService links,
        AuditService audit,
        TimeProvider time,
        ILogger<MetadataFlagService> logger)
    {
        _db = db;
        _libraryAuth = libraryAuth;
        _resolver = resolver;
        _links = links;
        _audit = audit;
        _time = time;
        _logger = logger;
    }

    private sealed record Anchor(CatalogNodeEntity Node, long AnchorNodeId, string AnchorPublicId, string? Provider, string? ExternalId);

    /// <summary>The node (when the user may see it) and the anchor of the web data it shows (null when it shows none).</summary>
    private async Task<(CatalogNodeEntity? Node, Anchor? Anchor)> ResolveAsync(long userId, string nodePublicId, CancellationToken ct)
    {
        var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == nodePublicId, ct);
        if (node is null || node.Availability == (int)CatalogNodeAvailability.Tombstoned
            || !await _libraryAuth.CanAccessLibraryAsync(userId, node.LibraryId, ct))
            return (null, null);

        var info = await _resolver.ResolveAsync(node, includeItems: false, ct);
        if (info.Web is null || info.Link is null)
            return (node, null);
        var anchorId = await _db.CatalogNodes.AsNoTracking().Where(n => n.PublicId == info.Link.NodeId).Select(n => n.Id).FirstOrDefaultAsync(ct);
        var record = await _resolver.ResolveWebRecordAsync(node, ct);
        return (node, new Anchor(node, anchorId, info.Link.NodeId, record?.Provider, record?.ExternalId));
    }

    // --- User ---

    public async Task<(string? Error, MetadataMyFlagDto? Flag)> CreateAsync(long userId, string nodePublicId, CreateMetadataFlagRequest request, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(request.Reason))
            return ("invalid_reason", null);
        var note = CleanNote(request.Note);
        if (note is { Length: > MaxNoteLength })
            return ("note_too_long", null);

        var (node, anchor) = await ResolveAsync(userId, nodePublicId, ct);
        if (node is null)
            return ("not_found", null);
        if (anchor is null)
            return ("no_web_data", null);

        if (await _db.MetadataFlags.AnyAsync(f => f.ReporterUserId == userId && f.NodeId == anchor.AnchorNodeId && f.State == (int)MetadataFlagState.Open, ct))
            return ("flag_exists", null);

        var now = _time.GetUtcNow();
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var today = await _db.MetadataFlags.CountAsync(f => f.ReporterUserId == userId && f.CreatedAt >= dayStart, ct);
        if (today >= await DailyCapAsync(userId, ct))
            return ("flag_limit", null);

        var flag = new MetadataFlagEntity
        {
            PublicId = "fl" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
            NodeId = anchor.AnchorNodeId,
            LibraryId = node.LibraryId,
            ReporterUserId = userId,
            Provider = anchor.Provider,
            ExternalId = anchor.ExternalId,
            Reason = (int)request.Reason,
            Note = note,
            State = (int)MetadataFlagState.Open,
            CreatedAt = now,
        };
        _db.MetadataFlags.Add(flag);
        try
        {
            // The scope keeps EF's own error line for the duplicate below out of the log (see ExpectedRaceScope).
            using (ExpectedRaceScope.Begin())
                await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The partial unique index caught a concurrent duplicate.
            return ("flag_exists", null);
        }
        _logger.LogInformation(LogEvents.Metadata.FlagCreated, "Metadata flag {FlagId} created on node {NodeId}: {Reason}", flag.Id, flag.NodeId, request.Reason);
        return (null, ToMine(flag, anchor.AnchorPublicId));
    }

    /// <summary>max(20, 2% of the anchors - nodes with a live Confirmed / Auto link - in libraries the user can access).</summary>
    public async Task<int> DailyCapAsync(long userId, CancellationToken ct = default)
    {
        var libraries = await _libraryAuth.GetAccessibleLibraryIdsAsync(userId, ct);
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var anchors = await _db.NodeSeriesLinks
            .Where(l => libraries.Contains(l.LibraryId)
                && (l.State == (int)SeriesLinkState.Confirmed || l.State == (int)SeriesLinkState.Auto)
                && _db.CatalogNodes.Any(n => n.Id == l.NodeId && n.Availability != tombstoned))
            .CountAsync(ct);
        return Math.Max(MinDailyCap, (int)Math.Ceiling(anchors * DailyCapShare));
    }

    public async Task<MetadataMyFlagStateDto?> MineAsync(long userId, string nodePublicId, CancellationToken ct = default)
    {
        var (node, anchor) = await ResolveAsync(userId, nodePublicId, ct);
        if (node is null)
            return null;
        var anchorId = anchor?.AnchorNodeId ?? node.Id;
        var latest = await _db.MetadataFlags.AsNoTracking()
            .Where(f => f.ReporterUserId == userId && f.NodeId == anchorId)
            .OrderByDescending(f => f.Id)
            .FirstOrDefaultAsync(ct);
        var anchorPublicId = anchor?.AnchorPublicId ?? node.PublicId;
        return new MetadataMyFlagStateDto
        {
            CanFlag = anchor is not null && latest is not { State: (int)MetadataFlagState.Open },
            Flag = latest is null ? null : ToMine(latest, anchorPublicId),
        };
    }

    private static MetadataMyFlagDto ToMine(MetadataFlagEntity f, string anchorPublicId) => new()
    {
        FlagId = f.PublicId,
        AnchorNodeId = anchorPublicId,
        Reason = (MetadataFlagReason)f.Reason,
        State = (MetadataFlagState)f.State,
        CreatedAt = f.CreatedAt,
        ResolvedAt = f.ResolvedAt,
    };

    /// <summary>Plain text: control characters (except newlines) removed, trimmed; null when empty.</summary>
    internal static string? CleanNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
            return null;
        var chars = note.Where(c => c == '\n' || !char.IsControl(c)).ToArray();
        var text = new string(chars).Trim();
        return text.Length == 0 ? null : text;
    }

    // --- Admin ---

    public async Task<(string? Error, MetadataFlagPageDto? Page)> ListAsync(string? state, string? libraryPublicId, string? cursor, int limit, CancellationToken ct = default)
    {
        var query = _db.MetadataFlags.AsNoTracking();
        switch (state?.ToLowerInvariant())
        {
            case null or "" or "open":
                query = query.Where(f => f.State == (int)MetadataFlagState.Open);
                break;
            case "resolved":
                query = query.Where(f => f.State != (int)MetadataFlagState.Open);
                break;
            case "all":
                break;
            default:
                return ("invalid_state", null);
        }
        if (!string.IsNullOrEmpty(libraryPublicId))
        {
            var libraryId = await _db.Libraries.Where(l => l.PublicId == libraryPublicId).Select(l => (long?)l.Id).FirstOrDefaultAsync(ct);
            if (libraryId is null)
                return ("library_not_found", null);
            query = query.Where(f => f.LibraryId == libraryId);
        }
        limit = Math.Clamp(limit, 1, 100);
        var total = await query.CountAsync(ct);
        if (long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var before))
            query = query.Where(f => f.Id < before);
        var rows = await query.OrderByDescending(f => f.Id).Take(limit + 1).ToListAsync(ct);
        var hasMore = rows.Count > limit;
        rows = rows.Take(limit).ToList();
        var items = await ToAdminDtosAsync(_db, rows, ct);
        // Flags on automatic links first (the design's triage order), newest first within each group.
        items = items.OrderByDescending(i => i.CurrentLink?.State == SeriesLinkState.Auto).ToList();
        return (null, new MetadataFlagPageDto
        {
            Items = items,
            Total = total,
            HasMore = hasMore,
            NextCursor = hasMore ? rows[^1].Id.ToString(CultureInfo.InvariantCulture) : null,
        });
    }

    internal static async Task<List<MetadataFlagDto>> ToAdminDtosAsync(MangaPixerDbContext db, IReadOnlyList<MetadataFlagEntity> flags, CancellationToken ct)
    {
        if (flags.Count == 0)
            return [];
        var nodeIds = flags.Select(f => f.NodeId).Distinct().ToList();
        var nodes = await db.CatalogNodes.AsNoTracking().Where(n => nodeIds.Contains(n.Id))
            .Select(n => new { n.Id, n.PublicId, n.Kind, n.DisplayName, n.LibraryId }).ToDictionaryAsync(n => n.Id, ct);
        var libraryIds = nodes.Values.Select(n => n.LibraryId).Distinct().ToList();
        var libraries = await db.Libraries.AsNoTracking().Where(l => libraryIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.PublicId, ct);
        var userIds = flags.Select(f => f.ReporterUserId).Concat(flags.Where(f => f.ResolvedByUserId != null).Select(f => f.ResolvedByUserId!.Value)).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.UserName, ct);
        var links = await db.NodeSeriesLinks.AsNoTracking().Where(l => nodeIds.Contains(l.NodeId)).ToDictionaryAsync(l => l.NodeId, ct);
        var recordIds = links.Values.Where(l => l.RecordId != null).Select(l => l.RecordId!.Value).ToList();
        var records = await db.MetadataRecords.AsNoTracking().Where(r => recordIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);

        return flags.Where(f => nodes.ContainsKey(f.NodeId)).Select(f =>
        {
            var node = nodes[f.NodeId];
            links.TryGetValue(f.NodeId, out var link);
            return new MetadataFlagDto
            {
                FlagId = f.PublicId,
                NodeId = node.PublicId,
                NodeKind = (CatalogNodeKind)node.Kind,
                NodeDisplayName = node.DisplayName,
                LibraryId = libraries.GetValueOrDefault(node.LibraryId) ?? string.Empty,
                Reason = (MetadataFlagReason)f.Reason,
                Note = f.Note,
                State = (MetadataFlagState)f.State,
                ReporterDisplayName = users.GetValueOrDefault(f.ReporterUserId) ?? string.Empty,
                ResolvedByDisplayName = f.ResolvedByUserId is { } r ? users.GetValueOrDefault(r) : null,
                CreatedAt = f.CreatedAt,
                ResolvedAt = f.ResolvedAt,
                Provider = f.Provider,
                ExternalId = f.ExternalId,
                CurrentLink = link is null ? null
                    : MetadataReviewService.ToLinkDto(link, node.PublicId, link.RecordId is { } rid ? records.GetValueOrDefault(rid) : null),
            };
        }).ToList();
    }

    public async Task<(string? Error, MetadataFlagDto? Flag)> ResolveAsync(string flagPublicId, MetadataFlagState outcome, string? actor, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(outcome) || outcome == MetadataFlagState.Open)
            return ("invalid_outcome", null);
        var flag = await _db.MetadataFlags.FirstOrDefaultAsync(f => f.PublicId == flagPublicId, ct);
        if (flag is null)
            return ("not_found", null);
        if (flag.State != (int)MetadataFlagState.Open)
            return ("already_resolved", null);

        var nodePublicId = await _db.CatalogNodes.AsNoTracking().Where(n => n.Id == flag.NodeId).Select(n => n.PublicId).FirstAsync(ct);
        switch (outcome)
        {
            case MetadataFlagState.Unlinked:
                await _links.RemoveAsync(nodePublicId, onlyDontMatch: false, actor, ct);
                break;
            case MetadataFlagState.DontMatch:
                await _links.SetDontMatchAsync(nodePublicId, actor, ct);
                break;
        }

        var actorId = string.IsNullOrEmpty(actor) ? null
            : await _db.Users.AsNoTracking().Where(u => u.UserName == actor).Select(u => (long?)u.Id).FirstOrDefaultAsync(ct);
        var now = _time.GetUtcNow();
        await _db.MetadataFlags
            .Where(f => f.NodeId == flag.NodeId && f.State == (int)MetadataFlagState.Open)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.State, (int)outcome)
                .SetProperty(f => f.ResolvedAt, now)
                .SetProperty(f => f.ResolvedByUserId, actorId), ct);

        await _audit.RecordAsync(AuditActions.MetadataFlagResolve, outcome.ToString(), actor, ct: ct, targetLibraryId: flag.LibraryId, targetItemId: flag.NodeId);
        _logger.LogInformation(LogEvents.Metadata.FlagResolved, "Metadata flag {FlagId} on node {NodeId} resolved: {Outcome}", flag.Id, flag.NodeId, outcome);
        var fresh = await _db.MetadataFlags.AsNoTracking().FirstAsync(f => f.Id == flag.Id, ct);
        return (null, (await ToAdminDtosAsync(_db, [fresh], ct)).FirstOrDefault());
    }
}
