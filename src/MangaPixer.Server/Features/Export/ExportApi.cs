namespace com.lifepixer.mangapixer.Server.Features.Export;

/// <summary>
/// The access contract of the read-only metadata export (1.33.0, for MangaList): every export endpoint lives under
/// <see cref="RoutePrefix"/> and is guarded by <see cref="Policy"/>. A personal access token authenticates with
/// <see cref="TokenScheme"/>, which only this policy names, so a token reaches nothing outside the prefix; the cookie
/// login stays the default scheme for every other endpoint.
/// </summary>
public static class ExportApi
{
    /// <summary>Route prefix of every export endpoint.</summary>
    public const string RoutePrefix = "api/v1/export";

    /// <summary>Authorization policy of every export endpoint.</summary>
    public const string Policy = "ExportRead";

    /// <summary>The one token scope: read the export.</summary>
    public const string Scope = "metadata:read";

    /// <summary>Authentication scheme of personal access tokens (<c>Authorization: Bearer</c>).</summary>
    public const string TokenScheme = "MangaPixerToken";
}
