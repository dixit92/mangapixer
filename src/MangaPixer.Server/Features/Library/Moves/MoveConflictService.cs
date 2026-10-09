namespace com.lifepixer.mangapixer.Server.Features.Library.Moves;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The admin side of move conflicts (1.31.0): list them, count the open ones, resolve them one by one or in bulk. Overwrite
/// copies the old state - kept on the removed copy, which is held from the trash while the conflict is open - onto the new
/// copy; Keep leaves the new copy as it is. Local only; ids-and-counts logs; one audit event per request.
/// </summary>
public sealed class MoveConflictService
{
    public const int MaxIds = 500;
    public const int MaxPage = 200;

    private readonly MangaPixerDbContext _db;
    private readonly MetadataCarryOverService _carryOver;
    private readonly AuditService _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<MoveConflictService> _logger;

    public MoveConflictService(MangaPixerDbContext db, MetadataCarryOverService carryOver, AuditService audit, TimeProvider time,
        ILogger<MoveConflictService> logger)
    {
        _db = db;
        _carryOver = carryOver;
        _audit = audit;
        _time = time;
        _logger = logger;
    }

    public Task<int> CountOpenAsync(CancellationToken ct = default) =>
        _db.MoveConflicts.CountAsync(c => c.State == (int)MoveConflictState.Open, ct);

    /// <summary>Open conflicts oldest first, or resolved ones newest first. The cursor is the last id of the previous page.</summary>
    public async Task<MoveConflictPageDto> ListAsync(bool resolved, string? cursor, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, MaxPage);
        var open = (int)MoveConflictState.Open;
        var q = _db.MoveConflicts.AsNoTracking().Where(c => resolved ? c.State != open : c.State == open);
        long? after = TryDecode(cursor);
        if (after is { } a)
            q = resolved ? q.Where(c => c.Id < a) : q.Where(c => c.Id > a);
        q = resolved ? q.OrderByDescending(c => c.Id) : q.OrderBy(c => c.Id);
        var rows = await q.Take(limit + 1)
            .Select(c => new { c.Id, c.Kind, c.State, c.UserId, c.CreatedAt, c.ResolvedAt, c.Move!.FromNodeId, c.Move.ToNodeId })
            .ToListAsync(ct);
        var more = rows.Count > limit;
        if (more)
            rows.RemoveAt(rows.Count - 1);

        var nodeIds = rows.SelectMany(r => new[] { r.FromNodeId, r.ToNodeId }).Distinct().ToList();
        var nodes = await _db.CatalogNodes.AsNoTracking().Where(n => nodeIds.Contains(n.Id))
            .Select(n => new
            {
                n.Id,
                n.PublicId,
                n.DisplayName,
                n.Kind,
                n.LibraryId,
                LibraryPublicId = n.Library!.PublicId,
                LibraryName = n.Library.DisplayName,
                ParentTitle = n.Parent == null ? null : n.Parent.DisplayName,
                PageCount = n.ArchiveItem == null ? null : n.ArchiveItem.PageCount,
            })
            .ToDictionaryAsync(n => n.Id, ct);
        var userIds = rows.Where(r => r.UserId != null).Select(r => r.UserId!.Value).Distinct().ToList();
        var users = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => (u.PublicId, u.UserName), ct);

        var items = new List<MoveConflictDto>(rows.Count);
        foreach (var r in rows)
        {
            if (!nodes.TryGetValue(r.FromNodeId, out var from) || !nodes.TryGetValue(r.ToNodeId, out var to))
                continue;
            var kind = (MoveConflictKind)r.Kind;
            items.Add(new MoveConflictDto
            {
                Id = OpaqueId.Encode(r.Id),
                Kind = kind,
                State = (MoveConflictState)r.State,
                UserId = r.UserId is { } u && users.TryGetValue(u, out var user) ? user.PublicId : null,
                UserName = r.UserId is { } u2 && users.TryGetValue(u2, out var user2) ? user2.UserName : null,
                NodeId = to.PublicId,
                Title = to.DisplayName,
                IsFolder = to.Kind == (int)CatalogNodeKind.Folder,
                ParentTitle = to.ParentTitle,
                LibraryId = to.LibraryPublicId,
                LibraryName = to.LibraryName,
                FromTitle = from.DisplayName,
                FromLibraryName = from.LibraryName,
                Old = await SideAsync(kind, r.UserId, from.Id, from.PageCount, ct),
                New = await SideAsync(kind, r.UserId, to.Id, to.PageCount, ct),
                CreatedAt = r.CreatedAt,
                ResolvedAt = r.ResolvedAt,
            });
        }
        return new MoveConflictPageDto
        {
            Items = items,
            OpenCount = await CountOpenAsync(ct),
            NextCursor = more ? OpaqueId.Encode(rows[^1].Id) : null,
        };
    }

    private async Task<MoveConflictSideDto> SideAsync(MoveConflictKind kind, long? userId, long nodeId, int? pageCount, CancellationToken ct)
    {
        switch (kind)
        {
            case MoveConflictKind.Progress:
                var p = await _db.ReadingProgress.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId && x.ItemId == nodeId, ct);
                return p is null
                    ? new MoveConflictSideDto { Present = false, PageCount = pageCount }
                    : new MoveConflictSideDto
                    {
                        Present = true,
                        Progress = (MoveProgressState)Math.Clamp(p.State, 0, 2),
                        Page = p.Ordinal + 1,
                        PageCount = pageCount,
                        UpdatedAt = p.UpdatedAt,
                    };
            case MoveConflictKind.ReaderSettings:
                var o = await _db.ItemReaderOverrides.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId && x.ItemId == nodeId, ct);
                return o is null
                    ? new MoveConflictSideDto { Present = false }
                    : new MoveConflictSideDto
                    {
                        Present = true,
                        ReaderMode = o.ReaderMode is { } m && Enum.IsDefined((ReaderMode)m) ? (ReaderMode)m : null,
                        OtherReaderSettings = o.Direction != null || o.FitMode != null || o.SpreadOffset != null || o.CoverOffset != null || o.Background != null,
                    };
            default:
                var l = await _db.NodeSeriesLinks.AsNoTracking().Where(x => x.NodeId == nodeId)
                    .Select(x => new { x.State, x.UpdatedAt, Title = x.Record == null ? null : x.Record.Title, Provider = x.Record == null ? null : x.Record.Provider })
                    .FirstOrDefaultAsync(ct);
                return l is null
                    ? new MoveConflictSideDto { Present = false }
                    : new MoveConflictSideDto
                    {
                        Present = true,
                        LinkState = (SeriesLinkState)l.State,
                        RecordTitle = l.Title,
                        Provider = l.Provider,
                        UpdatedAt = l.UpdatedAt,
                    };
        }
    }

    /// <summary>Resolves conflicts. Error codes: <c>too_many_ids</c>, <c>nothing_selected</c>.</summary>
    public async Task<(string? Error, MoveConflictResolveResultDto? Result)> ResolveAsync(
        MoveConflictResolveRequest request, long? actorUserId, string? actor, CancellationToken ct = default)
    {
        var open = (int)MoveConflictState.Open;
        List<long> ids;
        var requested = 0;
        if (request.All)
        {
            var q = _db.MoveConflicts.Where(c => c.State == open);
            if (request.Kind is { } k)
                q = q.Where(c => c.Kind == (int)k);
            ids = await q.OrderBy(c => c.Id).Select(c => c.Id).ToListAsync(ct);
            requested = ids.Count;
        }
        else
        {
            var raw = request.Ids ?? [];
            if (raw.Count == 0)
                return ("nothing_selected", null);
            if (raw.Count > MaxIds)
                return ("too_many_ids", null);
            requested = raw.Count;
            var decoded = raw.Select(TryDecode).Where(v => v is not null).Select(v => v!.Value).Distinct().ToList();
            ids = await _db.MoveConflicts.Where(c => decoded.Contains(c.Id) && c.State == open).Select(c => c.Id).ToListAsync(ct);
        }

        var resolved = 0;
        foreach (var id in ids)
        {
            if (await ResolveOneAsync(id, request.Resolution, actorUserId, ct))
                resolved++;
        }
        var skipped = requested - resolved;
        if (resolved > 0)
        {
            _logger.LogInformation(LogEvents.Scanning.MoveConflictsResolved, "{Count} move conflicts resolved ({Resolution}) by user {UserId}",
                resolved, request.Resolution, actorUserId);
            await _audit.RecordAsync(AuditActions.MoveConflictsResolve, AuditResults.Success, actor, ct: ct);
        }
        return (null, new MoveConflictResolveResultDto { Resolved = resolved, Skipped = Math.Max(0, skipped) });
    }

    private async Task<bool> ResolveOneAsync(long conflictId, MoveConflictResolution resolution, long? actorUserId, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        long? droppedRecord = null;
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            var conflict = await _db.MoveConflicts.Include(c => c.Move)
                .FirstOrDefaultAsync(c => c.Id == conflictId && c.State == (int)MoveConflictState.Open, ct);
            if (conflict?.Move is not { } move)
                return false;
            var kind = (MoveConflictKind)conflict.Kind;
            if (resolution == MoveConflictResolution.Overwrite)
            {
                droppedRecord = kind switch
                {
                    MoveConflictKind.Progress => await OverwriteProgressAsync(conflict.UserId!.Value, move.FromNodeId, move.ToNodeId, ct),
                    MoveConflictKind.ReaderSettings => await OverwriteOverridesAsync(conflict.UserId!.Value, move.FromNodeId, move.ToNodeId, ct),
                    _ => await OverwriteLinkAsync(move.FromNodeId, move.ToNodeId, now, ct),
                };
            }
            else if (kind == MoveConflictKind.SeriesLink)
            {
                // Keep the new link: the old one goes (its record too, when nothing else links it).
                var oldLink = await _db.NodeSeriesLinks.FirstOrDefaultAsync(l => l.NodeId == move.FromNodeId, ct);
                if (oldLink is not null)
                {
                    droppedRecord = oldLink.RecordId;
                    _db.NodeSeriesLinks.Remove(oldLink);
                }
            }
            conflict.State = resolution == MoveConflictResolution.Overwrite ? (int)MoveConflictState.Overwritten : (int)MoveConflictState.Kept;
            conflict.ResolvedAt = now;
            conflict.ResolvedByUserId = actorUserId;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        _db.ChangeTracker.Clear();
        if (droppedRecord is { } recordId)
            await _carryOver.RemoveOrphanRecordsAsync([recordId], ct);
        return true;
    }

    private async Task<long?> OverwriteProgressAsync(long userId, long fromId, long toId, CancellationToken ct)
    {
        var from = await _db.ReadingProgress.FirstOrDefaultAsync(p => p.UserId == userId && p.ItemId == fromId, ct);
        if (from is null)
            return null;
        var fromVersion = await _db.ArchiveItems.Where(a => a.NodeId == fromId).Select(a => a.ContentVersion).FirstAsync(ct);
        var toVersion = await _db.ArchiveItems.Where(a => a.NodeId == toId).Select(a => a.ContentVersion).FirstAsync(ct);
        var oldPages = await PagesAsync(fromId, fromVersion, ct);
        var newPages = await PagesAsync(toId, toVersion, ct);
        if (newPages.Count == 0)
            return null;
        var map = ManifestAgreement.MapByEntry(oldPages, newPages);
        var at = from.ContentVersion == fromVersion && map.TryGetValue(from.EntryKey, out var p) ? p : newPages[0];
        var to = await _db.ReadingProgress.FirstOrDefaultAsync(x => x.UserId == userId && x.ItemId == toId, ct);
        if (to is null)
        {
            from.ItemId = toId;
            from.ContentVersion = toVersion;
            from.Ordinal = at.Ordinal;
            from.EntryKey = at.EntryKey;
            from.Revision++;
        }
        else
        {
            MoveStateRules.CopyProgress(from, to, at);
            to.UpdatedAt = _time.GetUtcNow();
        }
        return null;
    }

    private async Task<long?> OverwriteOverridesAsync(long userId, long fromId, long toId, CancellationToken ct)
    {
        var from = await _db.ItemReaderOverrides.FirstOrDefaultAsync(o => o.UserId == userId && o.ItemId == fromId, ct);
        if (from is null)
            return null;
        var to = await _db.ItemReaderOverrides.FirstOrDefaultAsync(o => o.UserId == userId && o.ItemId == toId, ct);
        if (to is null)
        {
            from.ItemId = toId;
            return null;
        }
        to.ReaderMode = from.ReaderMode;
        to.Direction = from.Direction;
        to.FitMode = from.FitMode;
        to.SpreadOffset = from.SpreadOffset;
        to.CoverOffset = from.CoverOffset;
        to.Background = from.Background;
        return null;
    }

    /// <summary>The old link replaces the new one; returns the new link's record, which may now be orphaned.</summary>
    private async Task<long?> OverwriteLinkAsync(long fromId, long toId, DateTimeOffset now, CancellationToken ct)
    {
        var from = await _db.NodeSeriesLinks.FirstOrDefaultAsync(l => l.NodeId == fromId, ct);
        if (from is null)
            return null;
        var to = await _db.NodeSeriesLinks.FirstOrDefaultAsync(l => l.NodeId == toId, ct);
        long? dropped = null;
        if (to is not null)
        {
            dropped = to.RecordId;
            _db.NodeSeriesLinks.Remove(to);
            await _db.SaveChangesAsync(ct); // NodeId is unique: free it first
        }
        var target = await _db.CatalogNodes.AsNoTracking().Where(n => n.Id == toId).Select(n => new { n.LibraryId, n.Kind }).FirstAsync(ct);
        from.NodeId = toId;
        from.LibraryId = target.LibraryId;
        from.UpdatedAt = now;
        // 1.37.0: as folder carry-over does (1.33.0), the metadata export shows the old node's id as the new item's carriedFrom
        // instead of a removal + an addition.
        var fromPublicId = await _db.CatalogNodes.AsNoTracking().Where(n => n.Id == fromId).Select(n => n.PublicId).FirstAsync(ct);
        _db.ExportCarries.Add(new ExportCarryEntity { NewNodeId = toId, OldNodeId = fromId, OldNodePublicId = fromPublicId, At = now });
        await _db.SaveChangesAsync(ct);
        // As after an admin link: a folder linked or marked Don't match speaks for its subtree.
        // (A "Collection about" row retires nothing: its items are works of their own, 1.34.0.)
        if (target.Kind == (int)CatalogNodeKind.Folder && from.State is (int)SeriesLinkState.Confirmed or (int)SeriesLinkState.DontMatch)
        {
            var tree = await LibraryTreeSnapshot.LoadAsync(_db, target.LibraryId, ct);
            await CoveredWorkRetirement.RetireBelowAsync(_db, tree, toId, ct);
        }
        return dropped == from.RecordId ? null : dropped;
    }

    private static long? TryDecode(string? opaque)
    {
        if (string.IsNullOrEmpty(opaque))
            return null;
        try { return OpaqueId.Decode(opaque); }
        catch (ArgumentException) { return null; }
        catch (OverflowException) { return null; }
    }

    private async Task<List<ManifestPage>> PagesAsync(long itemId, long contentVersion, CancellationToken ct) =>
        await _db.PageEntries.AsNoTracking()
            .Where(p => p.ItemId == itemId && p.ContentVersion == contentVersion)
            .OrderBy(p => p.Ordinal)
            .Select(p => new ManifestPage(p.Ordinal, p.EntryKey, p.SourceEntryLocator, p.ByteSize))
            .ToListAsync(ct);
}
