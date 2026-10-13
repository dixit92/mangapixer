namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>A refused tracker API call: the HTTP status and a stable error code (never a remote message).</summary>
public sealed record TrackerProblem(int Status, string Code, string Message)
{
    public static TrackerProblem NotFound(string code, string message) => new(StatusCodes.Status404NotFound, code, message);
    public static TrackerProblem Bad(string code, string message) => new(StatusCodes.Status400BadRequest, code, message);
    public static TrackerProblem Conflict(string code, string message) => new(StatusCodes.Status409Conflict, code, message);
    public static TrackerProblem Unavailable(string code, string message) => new(StatusCodes.Status503ServiceUnavailable, code, message);

    public static readonly TrackerProblem UnknownTracker = NotFound("unknown_tracker", "There is no such tracker.");
    public static readonly TrackerProblem NotAllowed = Conflict("tracker_not_allowed", "An admin has not allowed this tracker on this server.");
    public static readonly TrackerProblem NotConnected = NotFound("not_connected", "Connect your account first.");
}

/// <summary>
/// A user's own tracker accounts (1.43.0): connect (OAuth sign-in with a one-use <c>state</c> bound to the user's session and a PKCE
/// verifier, or a MangaUpdates username / password used once), disconnect (local delete first, then a best-effort revoke), settings,
/// and the stored credentials the sync uses - renewed before they expire, "needs reconnect" when the tracker or the key ring refuses
/// them. Tokens never leave this class except to the tracker's own client.
/// </summary>
public sealed class TrackerAccountService(
    MangaPixerDbContext db,
    TrackerSettingsService settings,
    TrackerClients clients,
    TrackerTokenProtector protector,
    AuditService audit,
    TimeProvider time,
    ILogger<TrackerAccountService> logger)
{
    public async Task<TrackersOverviewDto> OverviewAsync(long userId, CancellationToken ct)
    {
        var rows = await db.TrackerSettings.AsNoTracking().ToListAsync(ct);
        var accounts = await db.TrackerAccounts.AsNoTracking().Where(a => a.UserId == userId).ToListAsync(ct);
        var disabled = settings.NetworkDisabledByConfig;
        return new TrackersOverviewDto
        {
            Trackers = TrackerSlugs.All.Select(slug => new TrackerSummaryDto
            {
                Slug = slug,
                Name = TrackerFacts.NameOf(slug),
                Allowed = TrackerSettingsService.IsAllowed(rows.FirstOrDefault(r => r.Tracker == slug), slug, disabled),
                NetworkDisabledByConfig = disabled,
                ConnectKind = TrackerSettingsService.ConnectKindOf(slug),
                CurrentConsentVersion = TrackerConsent.UserVersion(slug),
                SupportsPrivateEntries = TrackerFacts.SupportsPrivateEntries(slug),
                Account = accounts.FirstOrDefault(a => a.Tracker == slug) is { } account ? ToDto(account) : null,
            }).ToList(),
        };
    }

    public TrackerAccountDto ToDto(TrackerAccountEntity a) => new()
    {
        Tracker = a.Tracker,
        State = (TrackerAccountState)a.State,
        DisplayName = a.DisplayName,
        ConnectedAt = a.CreatedAt,
        ExpiresAt = a.ExpiresAt,
        LastSuccessAt = a.LastSuccessAt,
        LastFailureAt = a.LastFailureAt,
        LastFailureCode = a.LastFailureCode,
        ConsentRenewalNeeded = a.ConsentVersion < TrackerConsent.UserVersion(a.Tracker),
        AddStartedSeries = a.AddStartedSeries,
        PrivateEntries = a.PrivateEntries,
        RequestsToday = a.RequestsDay == DayOf(time.GetUtcNow()) ? a.RequestsUsed : 0,
        DailyRequestCap = TrackerFacts.DailyRequestCap,
    };

    /// <summary>Starts an OAuth sign-in: a 256-bit <c>state</c> (stored hashed, bound to the user and session) and a PKCE verifier.</summary>
    public async Task<(TrackerConnectStartResponse? Response, TrackerProblem? Problem)> StartAsync(
        long userId, string sessionKey, string tracker, TrackerConnectStartRequest request, CancellationToken ct)
    {
        if (await GateAsync(tracker, request.AcceptedConsentVersion, ct) is { } problem)
            return (null, problem);
        var client = clients.Get(tracker);
        if (client is null)
            return (null, TrackerProblem.Unavailable("tracker_unavailable", "This tracker cannot be reached from this server."));
        if (client.ConnectKind != TrackerConnectKind.OAuthPkce)
            return (null, TrackerProblem.Bad("password_connect", "This tracker connects with a username and password."));
        var app = await settings.AppSettingsAsync(tracker, ct);
        if (string.IsNullOrEmpty(app.ClientId) || app.RedirectUri is null)
            return (null, TrackerProblem.Conflict("not_configured", "An admin has not entered this server's app registration yet."));

        var now = time.GetUtcNow();
        // Expired sign-ins of anyone, and this user's earlier unfinished ones for the tracker, are dropped.
        await db.TrackerOAuthStates.Where(s => s.ExpiresAt < now || (s.UserId == userId && s.Tracker == tracker)).ExecuteDeleteAsync(ct);

        var state = RandomToken(32);
        // MyAnimeList supports only the "plain" PKCE method (D2 1.2): the challenge is the verifier itself.
        var verifier = RandomToken(48);
        db.TrackerOAuthStates.Add(new TrackerOAuthStateEntity
        {
            UserId = userId,
            Tracker = tracker,
            StateHash = Sha256(state),
            SessionHash = Sha256(sessionKey),
            VerifierProtected = protector.Protect(verifier),
            ConsentVersion = TrackerConsent.UserVersion(tracker),
            CreatedAt = now,
            ExpiresAt = now + TrackerFacts.StateLifetime,
        });
        await db.SaveChangesAsync(ct);
        var url = client.BuildAuthorizeUrl(app, state, verifier);
        if (url is null)
            return (null, TrackerProblem.Unavailable("tracker_unavailable", "This tracker cannot be reached from this server."));
        return (new TrackerConnectStartResponse { AuthorizeUrl = url.AbsoluteUri }, null);
    }

    /// <summary>Finishes an OAuth sign-in with the code and the <c>state</c> the browser brought back.</summary>
    public async Task<(TrackerAccountDto? Account, TrackerProblem? Problem)> FinishAsync(
        long userId, string? actor, string sessionKey, string tracker, TrackerConnectFinishRequest request, CancellationToken ct)
    {
        if (!TrackerFacts.IsKnown(tracker))
            return (null, TrackerProblem.UnknownTracker);
        if (string.IsNullOrEmpty(request.Code) || request.Code.Length > 4096 || string.IsNullOrEmpty(request.State) || request.State.Length > 512)
            return (null, TrackerProblem.Bad("invalid_state", "This sign-in is not valid any more. Start again."));
        var now = time.GetUtcNow();
        var hash = Sha256(request.State);
        var row = await db.TrackerOAuthStates.FirstOrDefaultAsync(s => s.StateHash == hash, ct);
        // Only a state THIS server issued to THIS user in THIS session, for this tracker, unexpired - and only once.
        if (row is null || row.UserId != userId || row.Tracker != tracker || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(row.SessionHash), Encoding.ASCII.GetBytes(Sha256(sessionKey))))
        {
            logger.LogWarning(LogEvents.Trackers.ConnectRefused, "Tracker sign-in refused for user {UserId} on {Tracker}: {Reason}",
                userId, tracker, "state_mismatch");
            return (null, TrackerProblem.Bad("invalid_state", "This sign-in is not valid any more. Start again."));
        }
        db.TrackerOAuthStates.Remove(row);
        await db.SaveChangesAsync(ct);
        if (row.ExpiresAt < now)
            return (null, TrackerProblem.Bad("invalid_state", "This sign-in is not valid any more. Start again."));
        if (!await settings.IsAllowedAsync(tracker, ct))
            return (null, TrackerProblem.NotAllowed);
        var verifier = protector.TryUnprotect(row.VerifierProtected);
        var client = clients.Get(tracker);
        if (verifier is null || client is null)
            return (null, TrackerProblem.Bad("invalid_state", "This sign-in is not valid any more. Start again."));

        var app = await settings.AppSettingsAsync(tracker, ct);
        return await ConnectAsync(userId, actor, tracker, client, app, new TrackerConnectRequest(request.Code, verifier, null, null),
            row.ConsentVersion, ct);
    }

    /// <summary>Connects a password tracker (MangaUpdates): the username and password go to the tracker once and are never kept.</summary>
    public async Task<(TrackerAccountDto? Account, TrackerProblem? Problem)> ConnectWithPasswordAsync(
        long userId, string? actor, string tracker, TrackerConnectPasswordRequest request, CancellationToken ct)
    {
        if (await GateAsync(tracker, request.AcceptedConsentVersion, ct) is { } problem)
            return (null, problem);
        var client = clients.Get(tracker);
        if (client is null)
            return (null, TrackerProblem.Unavailable("tracker_unavailable", "This tracker cannot be reached from this server."));
        if (client.ConnectKind != TrackerConnectKind.Password)
            return (null, TrackerProblem.Bad("oauth_connect", "This tracker connects with a sign-in on its own site."));
        if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 256
            || string.IsNullOrEmpty(request.Password) || request.Password.Length > 1024)
        {
            return (null, TrackerProblem.Bad("invalid_credentials", "Enter your username and password."));
        }
        var app = await settings.AppSettingsAsync(tracker, ct);
        return await ConnectAsync(userId, actor, tracker, client, app,
            new TrackerConnectRequest(null, null, request.Username.Trim(), request.Password), TrackerConsent.UserVersion(tracker), ct);
    }

    private async Task<(TrackerAccountDto?, TrackerProblem?)> ConnectAsync(long userId, string? actor, string tracker, ITrackerClient client,
        TrackerAppSettings app, TrackerConnectRequest connect, int consentVersion, CancellationToken ct)
    {
        TrackerTokens tokens;
        TrackerIdentity identity;
        try
        {
            tokens = await client.ConnectAsync(app, connect, ct);
            identity = await client.WhoAmIAsync(tokens, ct);
        }
        catch (TrackerException ex)
        {
            logger.LogWarning(LogEvents.Trackers.ConnectFailed, "Tracker connect failed for user {UserId} on {Tracker}: {Failure} {Status}",
                userId, tracker, ex.Failure, ex.StatusCode);
            return (null, ex.Failure is TrackerFailure.NeedsReconnect or TrackerFailure.Rejected
                ? TrackerProblem.Bad("connect_rejected", "The tracker did not accept this sign-in.")
                : TrackerProblem.Unavailable("tracker_unavailable", "The tracker could not be reached. Try again later."));
        }

        var now = time.GetUtcNow();
        var account = await db.TrackerAccounts.FirstOrDefaultAsync(a => a.UserId == userId && a.Tracker == tracker, ct);
        if (account is null)
        {
            account = new TrackerAccountEntity { UserId = userId, Tracker = tracker };
            db.TrackerAccounts.Add(account);
        }
        account.RemoteUserId = Bounded(identity.RemoteUserId, 128);
        account.DisplayName = Bounded(identity.DisplayName, 256);
        StoreTokens(account, tokens);
        account.State = (int)TrackerAccountState.Ok;
        account.ConsentVersion = consentVersion;
        account.ConsentAt = now;
        account.CreatedAt = now;
        account.LastSuccessAt = now;
        account.LastFailureAt = null;
        account.LastFailureCode = null;
        CountRequests(account, 2);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(TrackerAuditActions.Connect, AuditResults.Success, actor, targetUserId: userId, correlationId: tracker, ct: ct);
        logger.LogInformation(LogEvents.Trackers.AccountConnected, "Tracker account connected for user {UserId} on {Tracker}", userId, tracker);
        return (ToDto(account), null);
    }

    public async Task<(TrackerAccountDto? Account, TrackerProblem? Problem)> UpdateSettingsAsync(
        long userId, string tracker, UpdateTrackerUserSettingsRequest request, CancellationToken ct)
    {
        if (!TrackerFacts.IsKnown(tracker))
            return (null, TrackerProblem.UnknownTracker);
        var account = await db.TrackerAccounts.FirstOrDefaultAsync(a => a.UserId == userId && a.Tracker == tracker, ct);
        if (account is null)
            return (null, TrackerProblem.NotConnected);
        if (request.AddStartedSeries is { } add)
            account.AddStartedSeries = add;
        if (request.PrivateEntries is { } priv)
            account.PrivateEntries = priv;
        await db.SaveChangesAsync(ct);
        return (ToDto(account), null);
    }

    /// <summary>
    /// The user disconnects: the account, its entries and retries are deleted at once (the tokens with them), then the tracker is asked
    /// to revoke the sign-in, best effort. Answers false when there was no account.
    /// </summary>
    public async Task<bool> DisconnectAsync(long userId, string? actor, string tracker, CancellationToken ct)
    {
        var account = await db.TrackerAccounts.FirstOrDefaultAsync(a => a.UserId == userId && a.Tracker == tracker, ct);
        if (account is null)
            return false;
        var tokens = ReadTokens(account);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.TrackerRetries.Where(r => r.UserId == userId && r.Tracker == tracker).ExecuteDeleteAsync(ct);
            await db.TrackerEntries.Where(e => e.UserId == userId && e.Tracker == tracker).ExecuteDeleteAsync(ct);
            await db.TrackerOAuthStates.Where(s => s.UserId == userId && s.Tracker == tracker).ExecuteDeleteAsync(ct);
            db.TrackerAccounts.Remove(account);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        await audit.RecordAsync(TrackerAuditActions.Disconnect, "user", actor, targetUserId: userId, correlationId: tracker, ct: ct);
        logger.LogInformation(LogEvents.Trackers.AccountDisconnected, "Tracker account disconnected for user {UserId} on {Tracker} by {By}",
            userId, tracker, "user");
        await RevokeAsync(userId, tracker, tokens, ct);
        return true;
    }

    /// <summary>
    /// An admin disconnects a user's account (support, offboarding): the tokens are deleted and the account shows "disconnected by an
    /// admin" to its user, who may connect again; an admin can never connect one. Then a best-effort revoke.
    /// </summary>
    public async Task<bool> AdminDisconnectAsync(long userId, string? actor, string tracker, CancellationToken ct)
    {
        var account = await db.TrackerAccounts.FirstOrDefaultAsync(a => a.UserId == userId && a.Tracker == tracker, ct);
        if (account is null)
            return false;
        var tokens = ReadTokens(account);
        account.AccessTokenProtected = null;
        account.RefreshTokenProtected = null;
        account.ExpiresAt = null;
        account.RemoteUserId = null;
        account.DisplayName = null;
        account.State = (int)TrackerAccountState.DisconnectedByAdmin;
        await db.TrackerRetries.Where(r => r.UserId == userId && r.Tracker == tracker).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(TrackerAuditActions.Disconnect, "admin", actor, targetUserId: userId, correlationId: tracker, ct: ct);
        logger.LogInformation(LogEvents.Trackers.AccountDisconnected, "Tracker account disconnected for user {UserId} on {Tracker} by {By}",
            userId, tracker, "admin");
        await RevokeAsync(userId, tracker, tokens, ct);
        return true;
    }

    public async Task<IReadOnlyList<AdminTrackerAccountDto>> AdminListAsync(CancellationToken ct)
    {
        var rows = await (from a in db.TrackerAccounts.AsNoTracking()
                          join u in db.Users.AsNoTracking() on a.UserId equals u.Id
                          orderby u.UserName, a.Tracker
                          select new { a, u.PublicId, u.UserName }).ToListAsync(ct);
        return rows.Select(r => new AdminTrackerAccountDto
        {
            UserId = r.PublicId,
            Username = r.UserName,
            Tracker = r.a.Tracker,
            State = (TrackerAccountState)r.a.State,
            ConnectedAt = r.a.CreatedAt,
            LastSuccessAt = r.a.LastSuccessAt,
            LastFailureCode = r.a.LastFailureCode,
        }).ToList();
    }

    /// <summary>
    /// The credentials a sync may use for <paramref name="account"/> (tracked by this service's context), renewed when they expire within
    /// <see cref="TrackerFacts.RefreshAhead"/> (the rotated refresh token is stored at once). Null - and the account turned "needs
    /// reconnect" - when they cannot be read (no key ring) or the tracker refused them; null without a state change when the tracker
    /// could not be reached and the old token has expired.
    /// </summary>
    public async Task<TrackerTokens?> TokensForSyncAsync(TrackerAccountEntity account, ITrackerClient client, CancellationToken ct)
    {
        if (account.State != (int)TrackerAccountState.Ok)
            return null;
        var tokens = ReadTokens(account);
        if (tokens is null)
        {
            await MarkNeedsReconnectAsync(account, "key_ring", ct);
            return null;
        }
        var now = time.GetUtcNow();
        if (tokens.ExpiresAt is not { } expires || expires - now > TrackerFacts.RefreshAhead)
            return tokens;
        if (tokens.RefreshToken is null)
        {
            if (expires > now)
                return tokens;
            await MarkNeedsReconnectAsync(account, "expired", ct);
            return null;
        }
        if (!TryCountRequests(account, 1))
            return expires > now ? tokens : null;
        try
        {
            var app = await settings.AppSettingsAsync(account.Tracker, ct);
            var renewed = await client.RefreshAsync(app, tokens, ct);
            StoreTokens(account, renewed);
            account.LastSuccessAt = now;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(LogEvents.Trackers.TokenRefreshed, "Tracker sign-in renewed for user {UserId} on {Tracker}",
                account.UserId, account.Tracker);
            return renewed;
        }
        catch (TrackerException ex) when (ex.Failure == TrackerFailure.NeedsReconnect)
        {
            await MarkNeedsReconnectAsync(account, "needs_reconnect", ct);
            return null;
        }
        catch (TrackerException ex)
        {
            account.LastFailureAt = now;
            account.LastFailureCode = TrackerSyncService.CodeOf(ex.Failure);
            await db.SaveChangesAsync(ct);
            return expires > now ? tokens : null;
        }
    }

    public async Task MarkNeedsReconnectAsync(TrackerAccountEntity account, string code, CancellationToken ct)
    {
        account.State = (int)TrackerAccountState.NeedsReconnect;
        account.LastFailureAt = time.GetUtcNow();
        account.LastFailureCode = code;
        await db.SaveChangesAsync(ct);
        logger.LogInformation(LogEvents.Trackers.AccountNeedsReconnect, "Tracker account needs reconnect for user {UserId} on {Tracker}: {Code}",
            account.UserId, account.Tracker, code);
    }

    /// <summary>Counts <paramref name="count"/> requests against the account's daily cap; false (nothing counted) when it would exceed it.</summary>
    public bool TryCountRequests(TrackerAccountEntity account, int count)
    {
        var today = DayOf(time.GetUtcNow());
        var used = account.RequestsDay == today ? account.RequestsUsed : 0;
        if (used + count > TrackerFacts.DailyRequestCap)
            return false;
        account.RequestsDay = today;
        account.RequestsUsed = used + count;
        return true;
    }

    /// <summary>The requests left today for the account.</summary>
    public int RequestsLeft(TrackerAccountEntity account) =>
        TrackerFacts.DailyRequestCap - (account.RequestsDay == DayOf(time.GetUtcNow()) ? account.RequestsUsed : 0);

    private void CountRequests(TrackerAccountEntity account, int count)
    {
        var today = DayOf(time.GetUtcNow());
        account.RequestsUsed = (account.RequestsDay == today ? account.RequestsUsed : 0) + count;
        account.RequestsDay = today;
    }

    private void StoreTokens(TrackerAccountEntity account, TrackerTokens tokens)
    {
        account.AccessTokenProtected = protector.Protect(tokens.AccessToken);
        account.RefreshTokenProtected = string.IsNullOrEmpty(tokens.RefreshToken) ? null : protector.Protect(tokens.RefreshToken);
        account.ExpiresAt = tokens.ExpiresAt;
    }

    /// <summary>The stored tokens, or null when there are none or this key ring cannot open them.</summary>
    public TrackerTokens? ReadTokens(TrackerAccountEntity account)
    {
        var access = protector.TryUnprotect(account.AccessTokenProtected);
        if (access is null)
            return null;
        string? refresh = null;
        if (account.RefreshTokenProtected is not null && (refresh = protector.TryUnprotect(account.RefreshTokenProtected)) is null)
            return null;
        return new TrackerTokens(access, refresh, account.ExpiresAt);
    }

    /// <summary>Best-effort revoke after the local delete: failures are logged (status only) and never surface.</summary>
    public async Task RevokeAsync(long userId, string tracker, TrackerTokens? tokens, CancellationToken ct)
    {
        if (tokens is null || settings.NetworkDisabledByConfig || clients.Get(tracker) is not { } client)
            return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await client.RevokeAsync(await settings.AppSettingsAsync(tracker, ct), tokens, timeout.Token);
        }
        catch (Exception ex) when (ex is TrackerException or OperationCanceledException or HttpRequestException)
        {
            logger.LogWarning(LogEvents.Trackers.RevokeFailed, "Tracker revoke failed for user {UserId} on {Tracker}: {ErrorType} {Status}",
                userId, tracker, ex.GetType().Name, (ex as TrackerException)?.StatusCode);
        }
    }

    private async Task<TrackerProblem?> GateAsync(string tracker, int? acceptedConsentVersion, CancellationToken ct)
    {
        if (!TrackerFacts.IsKnown(tracker))
            return TrackerProblem.UnknownTracker;
        if (!await settings.IsAllowedAsync(tracker, ct))
            return TrackerProblem.NotAllowed;
        if (acceptedConsentVersion != TrackerConsent.UserVersion(tracker))
            return TrackerProblem.Bad("consent_required", "Accept the current text to connect.");
        return null;
    }

    public static string DayOf(DateTimeOffset now) => now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Bounded(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];

    private static string RandomToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
