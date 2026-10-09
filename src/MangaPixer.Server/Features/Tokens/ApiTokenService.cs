namespace com.lifepixer.mangapixer.Server.Features.Tokens;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Export;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

/// <summary>
/// Creates, lists, revokes and validates personal access tokens (1.33.0). A token is tied to the admin who created it and works
/// only while that account exists, is active, has finished activation, has no forced password change pending, and is still an
/// admin - the same account state a cookie session needs, plus the admin role. Account lockout (failed password logins) does
/// not stop a token, as it does not stop a session either: anyone guessing at the login form could otherwise cut MangaList off.
/// </summary>
public sealed class ApiTokenService
{
    /// <summary><see cref="ApiTokenEntity.LastUsedAt"/> is written at most once per this interval per token.</summary>
    public static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    private readonly MangaPixerDbContext _db;
    private readonly TimeProvider _clock;

    public ApiTokenService(MangaPixerDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Creates a token for <paramref name="userId"/>; the returned secret is never available again. <paramref name="scopes"/> must
    /// come from <see cref="TryNormalizeScopes"/> (null = the read scope only, as before 1.36.0).
    /// </summary>
    public async Task<CreateApiTokenResponse> CreateAsync(
        long userId, string name, int? expiresInDays, IReadOnlyList<string>? scopes = null, CancellationToken ct = default)
    {
        if (!TryNormalizeScopes(scopes, out var granted))
            throw new ArgumentException("Unknown or empty token scopes.", nameof(scopes));
        var now = _clock.GetUtcNow();
        var (secret, prefix, hash) = ApiTokenSecret.Generate();
        var entity = new ApiTokenEntity
        {
            PublicId = NewPublicId(),
            UserId = userId,
            Name = name,
            Prefix = prefix,
            SecretHash = hash,
            Scopes = string.Join(' ', granted),
            CreatedAt = now,
            ExpiresAt = expiresInDays is { } days ? now.AddDays(days) : null,
        };
        _db.ApiTokens.Add(entity);
        await _db.SaveChangesAsync(ct);

        var owner = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        return new CreateApiTokenResponse { Token = ToDto(entity, owner, now), Secret = secret };
    }

    /// <summary>Every token, newest first. Never includes a secret or its hash.</summary>
    public async Task<IReadOnlyList<ApiTokenDto>> ListAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var rows = await _db.ApiTokens.AsNoTracking()
            .Include(t => t.User)
            .OrderByDescending(t => t.Id)
            .ToListAsync(ct);
        return rows.Select(t => ToDto(t, t.User!, now)).ToList();
    }

    /// <summary>Revokes a token by its public id. Null when no such token exists; revoking twice keeps the first time.</summary>
    public async Task<ApiTokenEntity?> RevokeAsync(string publicId, CancellationToken ct = default)
    {
        var entity = await _db.ApiTokens.FirstOrDefaultAsync(t => t.PublicId == publicId, ct);
        if (entity is null)
            return null;
        if (entity.RevokedAt is null)
        {
            entity.RevokedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
        }
        return entity;
    }

    /// <summary>
    /// Removes every revoked or expired token (1.37.0, "Clear revoked"): they can never work again, so the list need not keep them. An
    /// active token, or one paused because its admin is no longer an active admin (it works again when that changes), is kept. The
    /// audit trail keeps each token's creation and revocation rows. Returns the removed tokens (public id and owner).
    /// </summary>
    public async Task<IReadOnlyList<(string PublicId, long UserId)>> ClearRevokedAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var gone = await _db.ApiTokens.Where(t => t.RevokedAt != null || (t.ExpiresAt != null && t.ExpiresAt <= now)).ToListAsync(ct);
        if (gone.Count == 0)
            return [];
        _db.ApiTokens.RemoveRange(gone);
        await _db.SaveChangesAsync(ct);
        return gone.Select(t => (t.PublicId, t.UserId)).ToList();
    }

    /// <summary>
    /// Checks a presented token. On success the token's last-used time is brought up to date (at most once per
    /// <see cref="LastUsedResolution"/>). The presented value is only hashed; it is never logged or stored.
    /// </summary>
    public async Task<ApiTokenValidation> ValidateAsync(string presented, CancellationToken ct = default)
    {
        if (!ApiTokenSecret.IsWellFormed(presented))
            return ApiTokenValidation.Refused(ApiTokenRefusal.Malformed);

        var hash = ApiTokenSecret.Hash(presented);
        // The join to users is an inner join: a token whose owner row is gone is never found.
        var row = await _db.ApiTokens.AsNoTracking()
            .Where(t => t.SecretHash == hash)
            .Select(t => new
            {
                t.Id,
                t.PublicId,
                t.SecretHash,
                t.Scopes,
                t.ExpiresAt,
                t.RevokedAt,
                t.LastUsedAt,
                t.UserId,
                t.User!.IsActive,
                t.User.IsAdmin,
                t.User.IsPendingActivation,
                t.User.ForcePasswordChange,
            })
            .FirstOrDefaultAsync(ct);

        // The index lookup already matched the hash; the constant-time compare is a second, timing-safe guard.
        if (row is null || !ApiTokenSecret.HashesMatch(row.SecretHash, hash))
            return ApiTokenValidation.Refused(ApiTokenRefusal.Unknown);

        var now = _clock.GetUtcNow();
        if (row.RevokedAt is not null)
            return ApiTokenValidation.Refused(ApiTokenRefusal.Revoked, row.PublicId);
        if (row.ExpiresAt is { } expires && expires <= now)
            return ApiTokenValidation.Refused(ApiTokenRefusal.Expired, row.PublicId);
        if (!OwnerMayUseTokens(row.IsActive, row.IsAdmin, row.IsPendingActivation, row.ForcePasswordChange))
            return ApiTokenValidation.Refused(ApiTokenRefusal.OwnerNotAllowed, row.PublicId);

        var scopes = ParseScopes(row.Scopes);
        if (scopes.Count == 0)
            return ApiTokenValidation.Refused(ApiTokenRefusal.NoScope, row.PublicId);

        if (row.LastUsedAt is not { } lastUsed || now - lastUsed >= LastUsedResolution)
        {
            // Conditional, so concurrent requests write at most once per interval.
            var cutoff = now - LastUsedResolution;
            await _db.ApiTokens
                .Where(t => t.Id == row.Id && (t.LastUsedAt == null || t.LastUsedAt <= cutoff))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), ct);
        }

        return ApiTokenValidation.Accepted(row.PublicId, scopes, row.UserId);
    }

    /// <summary>The list status of a token at <paramref name="now"/>.</summary>
    public static string StatusOf(ApiTokenEntity token, UserEntity owner, DateTimeOffset now)
    {
        if (token.RevokedAt is not null) return "revoked";
        if (token.ExpiresAt is { } expires && expires <= now) return "expired";
        if (!OwnerMayUseTokens(owner.IsActive, owner.IsAdmin, owner.IsPendingActivation, owner.ForcePasswordChange)) return "ownerInactive";
        return "active";
    }

    /// <summary>
    /// The scopes a new token is granted (1.36.0). Null (the field left out) = <see cref="ExportApi.Scope"/> only, so a client of
    /// 1.33.0 keeps today's behaviour. Otherwise every entry must be one of <see cref="ExportApi.KnownScopes"/> exactly (ordinal, no
    /// trimming) and at least one is required; repeats collapse. The result is in the canonical order of the known set.
    /// </summary>
    public static bool TryNormalizeScopes(IReadOnlyList<string>? requested, out IReadOnlyList<string> scopes)
    {
        if (requested is null)
        {
            scopes = [ExportApi.Scope];
            return true;
        }
        scopes = [];
        if (requested.Count == 0 || requested.Any(r => r is null || !ExportApi.KnownScopes.Contains(r, StringComparer.Ordinal)))
            return false;
        scopes = ExportApi.KnownScopes.Where(k => requested.Contains(k, StringComparer.Ordinal)).ToList();
        return true;
    }

    /// <summary>The account state a token needs from its owner (see the class remarks).</summary>
    private static bool OwnerMayUseTokens(bool isActive, bool isAdmin, bool isPendingActivation, bool forcePasswordChange) =>
        isActive && isAdmin && !isPendingActivation && !forcePasswordChange;

    private static ApiTokenDto ToDto(ApiTokenEntity t, UserEntity owner, DateTimeOffset now) => new()
    {
        Id = t.PublicId,
        Name = t.Name,
        Prefix = t.Prefix,
        Scopes = ParseScopes(t.Scopes),
        OwnerUserName = owner.UserName,
        CreatedAt = t.CreatedAt,
        ExpiresAt = t.ExpiresAt,
        LastUsedAt = t.LastUsedAt,
        RevokedAt = t.RevokedAt,
        Status = StatusOf(t, owner, now),
    };

    private static IReadOnlyList<string> ParseScopes(string scopes) =>
        scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>A random positive 63-bit id, base36 (the unique index rejects the improbable repeat).</summary>
    private static string NewPublicId()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return OpaqueId.Encode((BitConverter.ToInt64(bytes) & long.MaxValue) | 1);
    }
}

/// <summary>Why a presented token was refused. The code is logged; the token never is.</summary>
public enum ApiTokenRefusal
{
    None,
    Malformed,
    Unknown,
    Revoked,
    Expired,
    OwnerNotAllowed,
    NoScope,
}

/// <summary>The outcome of <see cref="ApiTokenService.ValidateAsync"/>.</summary>
public sealed record ApiTokenValidation(ApiTokenRefusal Refusal, string? TokenId, IReadOnlyList<string> Scopes)
{
    public bool Succeeded => Refusal == ApiTokenRefusal.None;

    /// <summary>The owner's user id of an accepted token (1.36.0, for the scan request's audit row); null when refused.</summary>
    public long? OwnerUserId { get; init; }

    public static ApiTokenValidation Accepted(string tokenId, IReadOnlyList<string> scopes, long ownerUserId) =>
        new(ApiTokenRefusal.None, tokenId, scopes) { OwnerUserId = ownerUserId };

    public static ApiTokenValidation Refused(ApiTokenRefusal refusal, string? tokenId = null) => new(refusal, tokenId, []);
}
