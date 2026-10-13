namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Admin-only tracker work that is not the gate (1.43.0): the per-series tracker id an admin enters for everyone (owner Q6 - no per-user
/// paste this cycle), and the tracker rows of a deleted user.
/// </summary>
public sealed class TrackerAdminService(
    MangaPixerDbContext db,
    TrackerSeriesResolver resolver,
    TrackerAccountService accounts,
    AuditService audit,
    TimeProvider time,
    ILogger<TrackerAdminService> logger)
{
    /// <summary>The tracker ids of the series <paramref name="nodePublicId"/> belongs to (its nearest Confirmed / Auto link); null when no node.</summary>
    public async Task<TrackerSeriesIdsDto?> SeriesIdsAsync(string nodePublicId, CancellationToken ct)
    {
        var record = await RecordOfNodeAsync(nodePublicId, ct);
        if (record is null)
            return null;
        if (record.Value.RecordId is not { } recordId)
            return new TrackerSeriesIdsDto { SeriesId = null, Ids = [] };
        var overrides = await db.TrackerIdOverrides.AsNoTracking().Where(o => o.RecordId == recordId)
            .ToDictionaryAsync(o => o.Tracker, o => o.RemoteId, ct);
        var ids = new List<TrackerSeriesIdDto>();
        foreach (var tracker in TrackerSlugs.All)
        {
            var effective = await resolver.RemoteIdAsync(recordId, tracker, ct);
            ids.Add(new TrackerSeriesIdDto
            {
                Tracker = tracker,
                RemoteId = effective?.Id,
                Source = effective?.Source,
                OverrideId = overrides.GetValueOrDefault(tracker),
            });
        }
        return new TrackerSeriesIdsDto { SeriesId = record.Value.PublicId, Ids = ids };
    }

    /// <summary>Sets (or with null / empty removes) the admin's id of <paramref name="tracker"/> for the node's series.</summary>
    public async Task<(TrackerSeriesIdsDto? Ids, TrackerProblem? Problem)> SetSeriesIdAsync(string nodePublicId, string tracker,
        UpdateTrackerSeriesIdRequest request, string? actor, CancellationToken ct)
    {
        if (!TrackerFacts.IsKnown(tracker))
            return (null, TrackerProblem.UnknownTracker);
        var remoteId = request.RemoteId?.Trim();
        if (!string.IsNullOrEmpty(remoteId) && !TrackerFacts.IsValidRemoteId(remoteId))
            return (null, TrackerProblem.Bad("invalid_remote_id", "Enter the tracker's series number (digits only)."));
        var record = await RecordOfNodeAsync(nodePublicId, ct);
        if (record is null)
            return (null, TrackerProblem.NotFound("unknown_node", "There is no such folder."));
        if (record.Value.RecordId is not { } recordId)
            return (null, TrackerProblem.Conflict("not_linked", "Link the folder to a series first."));

        var row = await db.TrackerIdOverrides.FirstOrDefaultAsync(o => o.RecordId == recordId && o.Tracker == tracker, ct);
        if (string.IsNullOrEmpty(remoteId))
        {
            if (row is not null)
                db.TrackerIdOverrides.Remove(row);
        }
        else
        {
            if (row is null)
                db.TrackerIdOverrides.Add(row = new TrackerIdOverrideEntity { RecordId = recordId, Tracker = tracker });
            row.RemoteId = remoteId;
            row.SetAt = time.GetUtcNow();
        }
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(TrackerAuditActions.Settings, string.IsNullOrEmpty(remoteId) ? "series_id_clear" : "series_id_set", actor,
            correlationId: tracker, ct: ct);
        return (await SeriesIdsAsync(nodePublicId, ct), null);
    }

    /// <summary>
    /// The node's series record: (public id, record id) of its nearest Confirmed / Auto link; (null, null) when it has none; null when
    /// the node does not exist.
    /// </summary>
    private async Task<(string? PublicId, long? RecordId)?> RecordOfNodeAsync(string nodePublicId, CancellationToken ct)
    {
        var nodeId = await db.CatalogNodes.AsNoTracking().Where(n => n.PublicId == nodePublicId).Select(n => (long?)n.Id).FirstOrDefaultAsync(ct);
        if (nodeId is null)
            return null;
        var links = await CoverLinks.NearestAsync(db, [nodeId.Value], ct);
        if (!links.TryGetValue(nodeId.Value, out var link) || !link.IsLinked)
            return (null, null);
        var publicId = await db.MetadataRecords.AsNoTracking().Where(r => r.Id == link.RecordId).Select(r => r.PublicId).FirstAsync(ct);
        return (publicId, link.RecordId);
    }

    /// <summary>
    /// Deletes every tracker row of a user inside the caller's transaction (<c>AdminController.DeleteUser</c>) and returns the stored
    /// sign-ins to revoke AFTER the commit (<see cref="RevokeAfterDeleteAsync"/>).
    /// </summary>
    public async Task<IReadOnlyList<(string Tracker, TrackerTokens Tokens)>> DeleteUserRowsAsync(long userId, CancellationToken ct)
    {
        var tokens = new List<(string, TrackerTokens)>();
        foreach (var account in await db.TrackerAccounts.AsNoTracking().Where(a => a.UserId == userId).ToListAsync(ct))
        {
            if (accounts.ReadTokens(account) is { } t)
                tokens.Add((account.Tracker, t));
        }
        var rows = 0;
        rows += await db.TrackerRetries.Where(r => r.UserId == userId).ExecuteDeleteAsync(ct);
        rows += await db.TrackerEntries.Where(e => e.UserId == userId).ExecuteDeleteAsync(ct);
        rows += await db.TrackerNodeOverrides.Where(o => o.UserId == userId).ExecuteDeleteAsync(ct);
        rows += await db.TrackerOAuthStates.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
        rows += await db.TrackerAccounts.Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);
        if (rows > 0)
            logger.LogInformation(LogEvents.Trackers.UserRowsDeleted, "Tracker rows deleted with user {UserId}: {Rows}", userId, rows);
        return tokens;
    }

    /// <summary>Best-effort revoke of a deleted user's sign-ins (after the commit; failures are logged, never surfaced).</summary>
    public async Task RevokeAfterDeleteAsync(long userId, IReadOnlyList<(string Tracker, TrackerTokens Tokens)> tokens, CancellationToken ct)
    {
        foreach (var (tracker, t) in tokens)
            await accounts.RevokeAsync(userId, tracker, t, ct);
    }
}
