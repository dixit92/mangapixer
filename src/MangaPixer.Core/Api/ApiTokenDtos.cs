namespace com.lifepixer.mangapixer.Core.Api;

/// <summary>
/// A personal access token as the Administration list shows it (1.33.0, <c>GET /api/v1/admin/tokens</c>). Never carries the
/// secret or its hash: only the public id, the admin's label, the short display prefix, times and the owner's user name.
/// </summary>
public sealed record ApiTokenDto
{
    /// <summary>Opaque public id (the revoke route uses it).</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>The first characters of the token, e.g. <c>mpx_Ab3x</c>, so an admin can tell tokens apart.</summary>
    public required string Prefix { get; init; }

    /// <summary>The scopes, today only <c>metadata:read</c>.</summary>
    public required IReadOnlyList<string> Scopes { get; init; }

    /// <summary>The admin who created the token; the token works only while that account is an active admin.</summary>
    public required string OwnerUserName { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Null = never expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>The last accepted request (updated at most about once a minute); null = never used.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary><c>active</c>, <c>expired</c>, <c>revoked</c> or <c>ownerInactive</c> (the owner is no longer an active admin).</summary>
    public required string Status { get; init; }
}

/// <summary><c>POST /api/v1/admin/tokens</c>: create a token for the signed-in admin.</summary>
public sealed record CreateApiTokenRequest
{
    /// <summary>A label (1-64 characters), e.g. <c>MangaList</c>.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// One of <see cref="ApiTokenLimits.AllowedExpiryDays"/> (30, 90, 365), or null for a token that never expires. Required, so
    /// "never" is always an explicit choice.
    /// </summary>
    public required int? ExpiresInDays { get; init; }
}

/// <summary>
/// The answer to <c>POST /api/v1/admin/tokens</c>: the listed token and its secret. The secret is returned this ONE time and is
/// never stored or shown again.
/// </summary>
public sealed record CreateApiTokenResponse
{
    public required ApiTokenDto Token { get; init; }

    /// <summary>The whole token (<c>mpx_...</c>), sent as <c>Authorization: Bearer &lt;secret&gt;</c>.</summary>
    public required string Secret { get; init; }

    public override string ToString() =>
        $"CreateApiTokenResponse {{ Token = {Token?.Id}, Secret = [redacted] }}";
}

/// <summary>Limits of the token API, shared by the server and the client.</summary>
public static class ApiTokenLimits
{
    /// <summary>The expiry choices in days; null (never) is allowed as well.</summary>
    public static readonly IReadOnlyList<int> AllowedExpiryDays = [30, 90, 365];

    public const int MaxNameLength = 64;
}

/// <summary>
/// <c>GET /api/v1/export/ping</c> (1.33.0): MangaList's "test connection". Says which credential was accepted and the server's
/// clock, nothing else.
/// </summary>
public sealed record ExportPingDto
{
    public required bool Ok { get; init; }

    public required DateTimeOffset ServerTime { get; init; }

    /// <summary><c>token</c> for a personal access token, <c>cookie</c> for an admin's browser login.</summary>
    public required string Auth { get; init; }
}
