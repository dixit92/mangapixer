namespace com.lifepixer.mangaplex.Server.Features.Auth;

using Microsoft.AspNetCore.Http;

/// <summary>
/// Reads the per-request Incognito mode from the <c>X-Incognito</c> request
/// header (1.4.0). Incognito is pure client session state — not server-
/// persisted. When active, listing/discovery surfaces exclude the user's
/// Private libraries; direct reader URLs remain accessible.
/// <para>
/// The header is truthy when set to <c>"1"</c> or <c>"true"</c> (case-
/// insensitive). Absence or any other value means Incognito is off.
/// </para>
/// </summary>
public sealed class IncognitoAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public IncognitoAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Whether Incognito mode is active for the current request.
    /// Returns <c>false</c> outside an HTTP request context (e.g. in
    /// background services or direct service tests).
    /// </summary>
    public bool IsIncognito
    {
        get
        {
            var context = _httpContextAccessor.HttpContext;
            if (context is null)
                return false;

            if (!context.Request.Headers.TryGetValue("X-Incognito", out var value))
                return false;

            var s = value.ToString();
            return string.Equals(s, "1", StringComparison.Ordinal)
                || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
