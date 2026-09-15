namespace com.lifepixer.mangaplex.Server.Features.SystemInfo;

using System.Reflection;
using com.lifepixer.mangaplex.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Read-only system information endpoint.
/// GET /api/v1/system/info — returns the product version from the assembly
/// <see cref="AssemblyInformationalVersionAttribute"/> (sourced from Version.props
/// at build time). Contains no private data; served unauthenticated so the app
/// footer can display it before login. This is distinct from /health (a static
/// health-check string) and from the admin Diagnostics card.
/// </summary>
[ApiController]
[Route("api/v1/system")]
[AllowAnonymous]
public sealed class SystemController : ControllerBase
{
    private static readonly string Version = ResolveVersion();

    [HttpGet("info")]
    public IActionResult GetInfo()
    {
        return Ok(new SystemInfoDto { Version = Version });
    }

    /// <summary>
    /// Resolves the product version from the assembly InformationalVersion attribute
    /// (set by Directory.Build.props from Version.props). Falls back to the numeric
    /// assembly version, then "unknown", so the endpoint never throws.
    /// </summary>
    private static string ResolveVersion()
    {
        var assembly = typeof(Program).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational;
        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
