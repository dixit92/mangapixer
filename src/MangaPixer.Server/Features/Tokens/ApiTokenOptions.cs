namespace com.lifepixer.mangapixer.Server.Features.Tokens;

/// <summary>
/// Limits of personal access tokens, bound from <c>MangaPixer:Security:ApiTokens</c>. The per-token ceiling only stops a runaway
/// client loop (owner decision 3, 1.33.0); the failed-attempt limit slows down guessing from one address.
/// </summary>
public sealed class ApiTokenOptions
{
    public const string SectionName = "MangaPixer:Security:ApiTokens";

    /// <summary>Requests per minute one token may make to the export (fixed window). 0 or less = no ceiling.</summary>
    public int RequestsPerMinute { get; set; } = 600;

    /// <summary>Failed token authentications per client address within <see cref="FailedAttemptsWindow"/> before it is refused.</summary>
    public int FailedAttemptsPerIp { get; set; } = 20;

    public TimeSpan FailedAttemptsWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Turns the failed-attempt limiter off. Follows the login limiter's switch (<c>MangaPixer:Security:RateLimit:Disabled</c>,
    /// the test host sets it); the per-token ceiling is configured on its own.
    /// </summary>
    public bool FailedAttemptsDisabled { get; set; }

    /// <summary>
    /// Minutes between two TOKEN-requested scans of the same library (1.36.0, owner: 1 per library per 5 minutes). A request inside
    /// the window answers 429 with <c>Retry-After</c>; scans an admin or the schedule started do not count. 0 or less = no cooldown
    /// (the 409 while a scan runs and the per-token request ceiling still apply).
    /// </summary>
    public int ScanCooldownMinutes { get; set; } = 5;
}

/// <summary>Claim types of a token principal.</summary>
public static class ApiTokenClaims
{
    /// <summary>The token's public id.</summary>
    public const string TokenId = "mpx_token_id";

    /// <summary>One claim per scope.</summary>
    public const string Scope = "scope";

    /// <summary>
    /// The owner's numeric user id (1.36.0), for the audit row of a scan request. Deliberately not
    /// <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/> (the cookie's "signed-in user") and not a role.
    /// </summary>
    public const string OwnerUserId = "mpx_token_owner";
}
