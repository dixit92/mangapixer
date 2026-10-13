namespace com.lifepixer.mangapixer.Core.Trackers;

// 1.43.0 tracker sync - the cross-lane seam (integrator, step 0). The engine lane codes against these types with a fake client;
// the adapter lane implements ITrackerClient per tracker. Design record: the vault note "MangaPixer - Tracker Progress Sync".
// Everything here is user data: a token, a remote account and a list entry belong to ONE user. Nothing in this file is logged.

/// <summary>The trackers MangaPixer can sync to (1.43.0: MyAnimeList and MangaUpdates lists). Stored as these slugs.</summary>
public static class TrackerSlugs
{
    public const string MyAnimeList = "mal";
    public const string MangaUpdates = "mangaupdates";

    public static readonly IReadOnlyList<string> All = [MyAnimeList, MangaUpdates];
}

/// <summary>
/// Keys of a metadata record's <c>CrossIdsJson</c> that name a series on a tracker. <see cref="MyAnimeList"/> is filled from the
/// MangaDex record's <c>links.mal</c> (the answer MangaPixer already fetches - no new request), next to the existing "anilist".
/// </summary>
public static class TrackerCrossIdKeys
{
    public const string MyAnimeList = "mal";
}

/// <summary>How a user connects an account: a browser OAuth sign-in on the tracker's site (PKCE), or a username / password.</summary>
public enum TrackerConnectKind
{
    OAuthPkce = 0,
    Password = 1,
}

/// <summary>A list entry's status, mapped to and from each tracker's own vocabulary by its adapter.</summary>
public enum TrackerEntryStatus
{
    Reading = 0,
    Completed = 1,
    OnHold = 2,
    Dropped = 3,
    PlanToRead = 4,
    Rereading = 5,
}

/// <summary>
/// The instance's registration with a tracker, set by an admin (MyAnimeList: the Client ID of the app this server registered, an
/// optional secret, and the exact redirect URL registered with it). MangaPixer ships none. Unused fields are null.
/// </summary>
public sealed record TrackerAppSettings(string? ClientId, string? ClientSecret, Uri? RedirectUri)
{
    public override string ToString() => $"TrackerAppSettings {{ ClientId = {(ClientId is null ? "null" : "set")}, ClientSecret = {(ClientSecret is null ? "null" : "***")}, RedirectUri = {(RedirectUri is null ? "null" : "set")} }}";
}

/// <summary>
/// What the user's browser hands back to connect an account: the OAuth <see cref="Code"/> (and the PKCE verifier the server issued
/// for that sign-in), or a <see cref="UserName"/> / <see cref="Password"/> used ONCE and never stored.
/// </summary>
public sealed record TrackerConnectRequest(string? Code, string? CodeVerifier, string? UserName, string? Password)
{
    public override string ToString() => "TrackerConnectRequest { *** }";
}

/// <summary>A user's credentials for one tracker. Stored only encrypted (Data Protection); never logged, returned or exported.</summary>
public sealed record TrackerTokens(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt)
{
    public override string ToString() => $"TrackerTokens {{ AccessToken = ***, RefreshToken = {(RefreshToken is null ? "null" : "***")}, ExpiresAt = {ExpiresAt:O} }}";
}

/// <summary>The connected account as the tracker names it ("Connected as ..."). Shown to that user only.</summary>
public sealed record TrackerIdentity(string RemoteUserId, string DisplayName);

/// <summary>
/// The user's entry for one series on a tracker, as read before a write (null from <see cref="ITrackerClient.ReadEntryAsync"/> when
/// the series is on none of the user's lists). <see cref="SeriesFinished"/> is the tracker's own publication state: an ongoing
/// series is never set to Completed (owner, 2026-10-13).
/// </summary>
public sealed record TrackerEntry(
    TrackerEntryStatus Status,
    int? ChaptersRead,
    int? VolumesRead,
    int? TotalChapters,
    int? TotalVolumes,
    bool SeriesFinished,
    DateOnly? StartedOn,
    DateOnly? FinishedOn);

/// <summary>
/// One write to the user's entry. Only these fields are ever sent; null leaves a field as the tracker has it. Never a score, notes,
/// tags, a title or anything about files.
/// </summary>
public sealed record TrackerEntryWrite(
    TrackerEntryStatus Status,
    int? ChaptersRead,
    int? VolumesRead,
    DateOnly? StartedOn,
    DateOnly? FinishedOn,
    bool? Private);

/// <summary>Why a tracker call failed, so the engine can decide without parsing messages.</summary>
public enum TrackerFailure
{
    /// <summary>The credentials are no longer accepted (expired, revoked, refresh lapsed): the account turns "needs reconnect".</summary>
    NeedsReconnect = 0,

    /// <summary>The tracker asked to slow down (429, MangaUpdates' 412 five-second delay): retry after <see cref="TrackerException.RetryAfter"/>.</summary>
    RateLimited = 1,

    /// <summary>The tracker refused this request (4xx other than auth / rate): do not retry it unchanged.</summary>
    Rejected = 2,

    /// <summary>The tracker is unreachable or failing (5xx, timeout, network): retry later.</summary>
    Unavailable = 3,
}

/// <summary>A failed tracker call. The message carries status codes only - never a token, an id, a title or a response body.</summary>
public sealed class TrackerException : Exception
{
    public TrackerException(TrackerFailure failure, int? statusCode = null, TimeSpan? retryAfter = null, Exception? inner = null)
        : base($"Tracker call failed: {failure}{(statusCode is { } s ? $" (HTTP {s})" : "")}.", inner)
    {
        Failure = failure;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public TrackerFailure Failure { get; }
    public int? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// One tracker's API, implemented by the adapter lane (MyAnimeList, MangaUpdates). Stateless: the engine owns the stored tokens, the
/// OAuth <c>state</c> and the PKCE verifier, and passes them in. Failures throw <see cref="TrackerException"/>.
/// </summary>
public interface ITrackerClient
{
    /// <summary>One of <see cref="TrackerSlugs"/>.</summary>
    string Slug { get; }

    TrackerConnectKind ConnectKind { get; }

    /// <summary>
    /// The tracker's sign-in URL the user's browser opens (OAuth trackers), carrying <paramref name="state"/> and the PKCE
    /// <paramref name="codeChallenge"/>; null for <see cref="TrackerConnectKind.Password"/>.
    /// </summary>
    Uri? BuildAuthorizeUrl(TrackerAppSettings app, string state, string codeChallenge);

    /// <summary>Exchanges the browser's answer (an OAuth code, or a username / password used once) for tokens.</summary>
    Task<TrackerTokens> ConnectAsync(TrackerAppSettings app, TrackerConnectRequest request, CancellationToken ct);

    /// <summary>New tokens before <see cref="TrackerTokens.ExpiresAt"/>; throws <see cref="TrackerFailure.NeedsReconnect"/> when it cannot.</summary>
    Task<TrackerTokens> RefreshAsync(TrackerAppSettings app, TrackerTokens tokens, CancellationToken ct);

    Task<TrackerIdentity> WhoAmIAsync(TrackerTokens tokens, CancellationToken ct);

    /// <summary>The user's entry for the tracker's series id <paramref name="mediaId"/>, or null when it is on no list.</summary>
    Task<TrackerEntry?> ReadEntryAsync(TrackerTokens tokens, string mediaId, CancellationToken ct);

    /// <summary>Creates or updates the user's entry for <paramref name="mediaId"/>.</summary>
    Task WriteEntryAsync(TrackerTokens tokens, string mediaId, TrackerEntryWrite write, CancellationToken ct);

    /// <summary>Best-effort revoke / sign-out on disconnect (after the local delete); a no-op where the tracker has none.</summary>
    Task RevokeAsync(TrackerAppSettings app, TrackerTokens tokens, CancellationToken ct);
}
