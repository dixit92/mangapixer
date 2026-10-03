namespace com.lifepixer.mangapixer.Server.Features.Auth;

using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Export;
using com.lifepixer.mangapixer.Server.Features.Tokens;
using com.lifepixer.mangapixer.Server.Logging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

/// <summary>
/// The personal access token scheme (<see cref="ExportApi.TokenScheme"/>, 1.33.0): reads <c>Authorization: Bearer mpx_...</c>.
/// </summary>
/// <remarks>
/// <para>This scheme is never the default. Only the export policy (<see cref="ExportApi.Policy"/>) names it, so the
/// authorization middleware runs it for export endpoints only; every other endpoint authenticates with the cookie alone and a
/// bearer header there is ignored exactly as if it were absent.</para>
/// <para>Order: no bearer header -> no result (the cookie may still succeed). A client address with too many failed attempts
/// -> refused before any database lookup (the challenge answers 429). A method other than GET / HEAD -> refused (tokens are
/// read-only). Otherwise the token is validated; a refusal counts one failed attempt for the address.</para>
/// <para>The principal carries the token's public id and its scopes and NO role, so no role-based policy ever accepts it.</para>
/// </remarks>
public sealed class ApiTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>Request item holding the wait time when the failed-attempt limiter refused the request.</summary>
    private const string RetryAfterItem = "MangaPixer.ApiToken.RetryAfter";

    /// <summary>Request item set when a bearer token was presented and refused (adds <c>error="invalid_token"</c>).</summary>
    private const string RefusedItem = "MangaPixer.ApiToken.Refused";

    private readonly TokenFailureRateLimiter _failures;

    public ApiTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        TokenFailureRateLimiter failures)
        : base(options, logger, encoder)
    {
        _failures = failures;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (header is null || !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var presented = header[BearerPrefix.Length..].Trim();

        var address = Context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (_failures.RetryAfter(address) is { } wait)
        {
            // Debug only: the Warning was written once, when the address became blocked (no log flood from a blocked client).
            Context.Items[RetryAfterItem] = wait;
            Logger.LogDebug(LogEvents.Auth.ApiTokenFailuresLimited,
                "Bearer request refused: too many failed token attempts from this address; retry in {RetryAfter}s",
                (int)Math.Ceiling(wait.TotalSeconds));
            return AuthenticateResult.Fail("too_many_failures");
        }

        if (!HttpMethods.IsGet(Request.Method) && !HttpMethods.IsHead(Request.Method))
        {
            Context.Items[RefusedItem] = true;
            Logger.LogInformation(LogEvents.Auth.ApiTokenRefused, "API token refused: {Reason}", "method_not_allowed");
            return AuthenticateResult.Fail("method_not_allowed");
        }

        var tokens = Context.RequestServices.GetRequiredService<ApiTokenService>();
        var result = await tokens.ValidateAsync(presented, Context.RequestAborted);
        if (!result.Succeeded)
        {
            if (_failures.RecordFailure(address))
            {
                // Never the address itself (privacy invariant, as for the login limiter).
                Logger.LogWarning(LogEvents.Auth.ApiTokenFailuresLimited,
                    "Too many failed token attempts from one address; its bearer requests are refused for {Window}s",
                    (int)_failures.Window.TotalSeconds);
            }
            Context.Items[RefusedItem] = true;
            // The public id is known only for a stored token (revoked / expired / owner); never the presented value.
            Logger.LogInformation(LogEvents.Auth.ApiTokenRefused, "API token {TokenId} refused: {Reason}",
                result.TokenId ?? "-", result.Refusal);
            return AuthenticateResult.Fail("invalid_token");
        }

        var claims = new List<Claim> { new(ApiTokenClaims.TokenId, result.TokenId!) };
        claims.AddRange(result.Scopes.Select(s => new Claim(ApiTokenClaims.Scope, s)));
        // No name and no role claim: the identity is the token, and no role-based policy may accept it.
        var identity = new ClaimsIdentity(claims, Scheme.Name, nameType: null, roleType: null);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Items.TryGetValue(RetryAfterItem, out var item) && item is TimeSpan wait)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            await Response.WriteAsJsonAsync(new ApiError
            {
                Error = "too_many_attempts",
                Message = "Too many failed token attempts. Try again later.",
            });
            return;
        }

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append(HeaderNames.WWWAuthenticate,
            Context.Items.ContainsKey(RefusedItem) ? "Bearer error=\"invalid_token\"" : "Bearer");
    }
}
