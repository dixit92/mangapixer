namespace com.lifepixer.mangapixer.Server.Features.Export;

/// <summary>
/// The access contract of the export API (1.33.0, for MangaList): every endpoint a personal access token can reach lives under
/// <see cref="RoutePrefix"/> and is guarded by one of exactly two policies. A token authenticates with <see cref="TokenScheme"/>,
/// which only these two policies name, so a token reaches nothing outside the prefix; the cookie login stays the default scheme
/// for every other endpoint.
/// <list type="bullet">
/// <item><see cref="Policy"/>: the read-only metadata export (GET / HEAD) - an admin's cookie or a token with <see cref="Scope"/>.</item>
/// <item><see cref="ScanPolicy"/> (1.36.0): the single token-writable route, <c>POST /api/v1/export/libraries/{id}/scan</c>, which
/// starts a FULL library scan - a token with <see cref="ScanScope"/> only, never the cookie. Any further token-writable route needs
/// owner approval.</item>
/// </list>
/// </summary>
public static class ExportApi
{
    /// <summary>Route prefix of every export endpoint.</summary>
    public const string RoutePrefix = "api/v1/export";

    /// <summary>Authorization policy of every read-only export endpoint.</summary>
    public const string Policy = "ExportRead";

    /// <summary>The read scope: read the export.</summary>
    public const string Scope = "metadata:read";

    /// <summary>
    /// Authorization policy of the library scan request (1.36.0): the token scheme ONLY (a cookie never authenticates there, which
    /// is why that one action may skip the antiforgery check) and the <see cref="ScanScope"/> claim.
    /// </summary>
    public const string ScanPolicy = "ExportScan";

    /// <summary>The scan scope (1.36.0): request a full scan of a library.</summary>
    public const string ScanScope = "library:scan";

    /// <summary>Every scope a token may be created with; a scope is granted only when ticked at creation.</summary>
    public static readonly IReadOnlyList<string> KnownScopes = [Scope, ScanScope];

    /// <summary>Authentication scheme of personal access tokens (<c>Authorization: Bearer</c>).</summary>
    public const string TokenScheme = "MangaPixerToken";
}
