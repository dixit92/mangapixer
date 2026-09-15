namespace com.lifepixer.mangaplex.Server.Features.SystemInfo;

using System.Reflection;
using com.lifepixer.mangaplex.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Read-only system information endpoint (reports the server platform since 1.13.0).
/// GET /api/v1/system/info — returns the product version from the assembly
/// <see cref="AssemblyInformationalVersionAttribute"/> (sourced from Version.props
/// at build time) and the server's OS platform. Contains no private data; served
/// unauthenticated so the app footer can display it before login. This is
/// distinct from /health (a static health-check string) and from the admin
/// Diagnostics card.
/// </summary>
[ApiController]
[Route("api/v1/system")]
[AllowAnonymous]
public sealed class SystemController : ControllerBase
{
    private static readonly string Version = ResolveVersion();
    private static readonly string? Platform = ResolvePlatform();

    [HttpGet("info")]
    public IActionResult GetInfo()
    {
        return Ok(new SystemInfoDto { Version = Version, Platform = Platform });
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

    /// <summary>
    /// Derives the server's platform so the admin UI can speak its path idiom
    /// (lane WinDeploy H, 1.13.0). Windows/Linux only for now — MangaPlex ships
    /// on those two; other OSes report null and the client falls back to the
    /// current (container-oriented) wording.
    /// </summary>
    private static string? ResolvePlatform()
    {
        if (OperatingSystem.IsWindows())
            return "windows";
        if (OperatingSystem.IsLinux())
            return "linux";
        return null;
    }
}
