namespace com.lifepixer.mangapixer.Server.Features.Admin;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Read/write access to the administrative audit trail (1.18.0).
///
/// The store (<see cref="AuditEventEntity"/> / the <c>audit_events</c> table)
/// has existed since the InitialCreate migration but was effectively
/// write-only — only the DB restore apply path recorded to it and nothing read
/// it back. This service centralises the write (resolving the acting admin's
/// user id from their name) and adds the paged, admin-only read path that backs
/// the audit-trail UI.
///
/// Privacy invariant: audit rows carry action/result verbs, numeric ids,
/// timestamps and an optional correlation id ONLY — never absolute paths,
/// secrets, tokens, or DB contents. The read path resolves an actor's id to
/// their user name (already admin-visible), nothing more.
/// </summary>
public sealed class AuditService
{
    private readonly MangaPixerDbContext _db;

    public AuditService(MangaPixerDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Records one audit event. Resolves the acting admin's user id from their
    /// (normalized) user name when supplied; a null/unknown actor is stored as a
    /// null <see cref="AuditEventEntity.ActorUserId"/> (e.g. a system action).
    /// Saves immediately so the row lands even if the surrounding request's own
    /// state was persisted in a separate SaveChanges.
    /// </summary>
    public async Task RecordAsync(
        string action,
        string result,
        string? actorUserName,
        long? targetUserId = null,
        string? correlationId = null,
        CancellationToken ct = default)
    {
        long? actorId = null;
        if (!string.IsNullOrWhiteSpace(actorUserName))
        {
            var normalized = actorUserName.ToUpperInvariant();
            actorId = await _db.Users
                .Where(u => u.NormalizedUserName == normalized)
                .Select(u => (long?)u.Id)
                .FirstOrDefaultAsync(ct);
        }

        _db.AuditEvents.Add(new AuditEventEntity
        {
            Action = action,
            Result = result,
            ActorUserId = actorId,
            TargetUserId = targetUserId,
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = correlationId,
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Returns one page of audit events, newest first. <paramref name="page"/>
    /// is 1-based; <paramref name="pageSize"/> is clamped to [1, 200]. Actor ids
    /// are resolved to user names in a single follow-up query (no per-row N+1).
    /// </summary>
    public async Task<AuditTrailPageDto> GetPageAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize < 1 ? 50 : (pageSize > 200 ? 200 : pageSize);

        var total = await _db.AuditEvents.CountAsync(ct);

        var rows = await _db.AuditEvents
            .OrderByDescending(a => a.Timestamp)
            .ThenByDescending(a => a.Id)
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .Select(a => new
            {
                a.Id,
                a.Action,
                a.Result,
                a.ActorUserId,
                a.TargetUserId,
                a.Timestamp,
                a.CorrelationId,
            })
            .ToListAsync(ct);

        var actorIds = rows
            .Where(r => r.ActorUserId != null)
            .Select(r => r.ActorUserId!.Value)
            .Distinct()
            .ToList();

        var names = actorIds.Count == 0
            ? new Dictionary<long, string>()
            : await _db.Users
                .Where(u => actorIds.Contains(u.Id))
                .Select(u => new { u.Id, u.UserName })
                .ToDictionaryAsync(u => u.Id, u => u.UserName, ct);

        var items = rows.Select(r => new AuditEventDto
        {
            Id = r.Id,
            Action = r.Action,
            Result = r.Result,
            ActorUserId = r.ActorUserId,
            ActorUserName = r.ActorUserId is { } id && names.TryGetValue(id, out var n) ? n : null,
            TargetUserId = r.TargetUserId,
            Timestamp = r.Timestamp,
            CorrelationId = r.CorrelationId,
        }).ToList();

        return new AuditTrailPageDto
        {
            Items = items,
            TotalCount = total,
            Page = safePage,
            PageSize = safeSize,
        };
    }
}

/// <summary>
/// A single administrative audit event, safe for admin API exposure: action /
/// result verbs, resolved actor user name, numeric ids, timestamp and an
/// optional correlation id — never paths, secrets, or DB contents.
/// </summary>
public sealed record AuditEventDto
{
    public required long Id { get; init; }
    public required string Action { get; init; }
    public required string Result { get; init; }
    public required long? ActorUserId { get; init; }
    public required string? ActorUserName { get; init; }
    public required long? TargetUserId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string? CorrelationId { get; init; }
}

/// <summary>One page of the audit trail (newest first), with total count.</summary>
public sealed record AuditTrailPageDto
{
    public required IReadOnlyList<AuditEventDto> Items { get; init; }
    public required int TotalCount { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
}

/// <summary>
/// Canonical audit action verbs (kept short — the column is 64 chars). Using a
/// shared constant set keeps the write sites and any future filter/query in
/// lock-step and avoids typos drifting the vocabulary.
/// </summary>
public static class AuditActions
{
    public const string UserDeleted = "user.delete";
    public const string UserActivationReissued = "user.activation.reissue";
    public const string UserPasswordReset = "user.password.reset";
    public const string BackupRestoreStaged = "backup.restore.stage";
    public const string LoggingChanged = "logging.change";
    public const string BackupSettingsChanged = "backup.settings.change";
    public const string BackupLocationChanged = "backup.location.change";
    public const string BackupLocationUnavailable = "backup.location.unavailable";
    public const string BackupLocationAvailable = "backup.location.available";
    public const string BackupSnapshotsMoved = "backup.snapshots.move";
}

/// <summary>Canonical audit result verbs (kept short — the column is 32 chars).</summary>
public static class AuditResults
{
    public const string Success = "success";
    public const string Failure = "failure";
}
