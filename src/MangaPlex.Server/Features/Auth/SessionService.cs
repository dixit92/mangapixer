namespace com.lifepixer.mangaplex.Server.Features.Auth;

using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

/// <summary>
/// Manages revocable authentication sessions. Each login creates a session record
/// with the user's current security stamp. If the user's security stamp changes
/// (password change, account disable, admin reset), all sessions are invalidated.
/// </summary>
public sealed class SessionService
{
    private readonly MangaPlexDbContext _db;
    private readonly SessionOptions _options;

    public SessionService(MangaPlexDbContext db, SessionOptions? options = null)
    {
        _db = db;
        _options = options ?? new SessionOptions();
    }

    /// <summary>
    /// Creates a new session for a user.
    /// </summary>
    public async Task<SessionEntity> CreateSessionAsync(UserEntity user, CancellationToken ct = default)
    {
        var session = new SessionEntity
        {
            TicketId = GenerateTicketId(),
            UserId = user.Id,
            SecurityStamp = user.SecurityStamp,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(_options.SessionLifetime),
            IsRevoked = false,
        };

        _db.Sessions.Add(session);
        await _db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>
    /// Validates a session ticket. Returns the user if valid, null if invalid/expired/revoked.
    /// </summary>
    public async Task<UserEntity?> ValidateSessionAsync(string ticketId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ticketId))
            return null;

        var session = await _db.Sessions
            .FirstOrDefaultAsync(s => s.TicketId == ticketId, ct);

        if (session is null || session.IsRevoked || session.ExpiresAt < DateTimeOffset.UtcNow)
            return null;

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == session.UserId, ct);
        if (user is null || !user.IsActive)
            return null;

        // Security stamp check: if the user's stamp changed, the session is invalid
        if (user.SecurityStamp != session.SecurityStamp)
            return null;

        return user;
    }

    /// <summary>
    /// Revokes a specific session.
    /// </summary>
    public async Task RevokeSessionAsync(string ticketId, CancellationToken ct = default)
    {
        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.TicketId == ticketId, ct);
        if (session is not null)
        {
            session.IsRevoked = true;
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Revokes all sessions for a user. Called when password changes, account is disabled, etc.
    /// </summary>
    public async Task RevokeAllSessionsAsync(long userId, CancellationToken ct = default)
    {
        var sessions = await _db.Sessions.Where(s => s.UserId == userId && !s.IsRevoked).ToListAsync(ct);
        foreach (var session in sessions)
            session.IsRevoked = true;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Cleans up expired sessions. Called periodically.
    /// </summary>
    public async Task CleanupExpiredSessionsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await _db.Sessions
            .Where(s => s.ExpiresAt < now || s.IsRevoked)
            .ToListAsync(ct);
        _db.Sessions.RemoveRange(expired);
        await _db.SaveChangesAsync(ct);
    }

    private static string GenerateTicketId()
    {
        // 256-bit random ticket ID, base64url encoded
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

/// <summary>
/// Session configuration options.
/// </summary>
public sealed class SessionOptions
{
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(7);
    public string CookieName { get; set; } = ".MangaPlex.Auth";
    public string CsrfCookieName { get; set; } = ".MangaPlex.Csrf";
    public string CsrfHeaderName { get; set; } = "X-MangaPlex-Csrf";
}
