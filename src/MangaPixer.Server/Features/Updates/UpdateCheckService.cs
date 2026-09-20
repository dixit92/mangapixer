namespace com.lifepixer.mangapixer.Server.Features.Updates;

using System.Reflection;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The opt-in Update Checker (1.21.0). This is the ONE sanctioned outbound
/// third-party call in MangaPixer: when an admin has enabled it, and at most once
/// per <see cref="CheckInterval"/>, it GETs the latest GitHub release for
/// <c>dixit92/mangapixer</c> and compares the tag against the running version.
///
/// Privacy: the request sends only a generic <c>User-Agent</c>
/// (<see cref="UserAgent"/>, required by the GitHub API) and carries no instance
/// identifier, user data, path, or telemetry. Only version strings and a
/// timestamp are ever stored or returned.
///
/// Resilience: any failure (offline, DNS, rate limit, non-200, malformed body)
/// degrades gracefully — the last known result is retained and the call NEVER
/// throws to the caller, so it cannot block a page. The last-checked timestamp is
/// advanced even on failure so a persistently failing endpoint is not hammered on
/// every admin page load (the "Check now" force path bypasses the cadence gate).
/// </summary>
public sealed class UpdateCheckService
{
    /// <summary>Named <see cref="System.Net.Http.HttpClient"/> for the GitHub Releases call.</summary>
    public const string HttpClientName = "GitHubReleases";

    /// <summary>Generic, non-identifying User-Agent (GitHub rejects requests without one).</summary>
    public const string UserAgent = "MangaPixer-UpdateCheck";

    /// <summary>The one URL this service is allowed to contact.</summary>
    public const string ReleasesUrl = "https://api.github.com/repos/dixit92/mangapixer/releases/latest";

    /// <summary>Minimum gap between automatic checks.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly MangaPixerDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly TimeProvider _clock;
    private readonly ILogger<UpdateCheckService>? _logger;
    private readonly string _currentVersion;

    public UpdateCheckService(
        MangaPixerDbContext db,
        IHttpClientFactory httpFactory,
        TimeProvider clock,
        ILogger<UpdateCheckService>? logger = null,
        string? currentVersionOverride = null)
    {
        _db = db;
        _httpFactory = httpFactory;
        _clock = clock;
        _logger = logger;
        _currentVersion = currentVersionOverride ?? ResolveCurrentVersion();
    }

    /// <summary>The running server version (no leading "v", build metadata stripped).</summary>
    public string CurrentVersion => _currentVersion;

    /// <summary>
    /// Returns the current update-check status. When enabled and either forced or
    /// past the <see cref="CheckInterval"/> since the last attempt, performs the
    /// GitHub call (best-effort) and caches the result before returning.
    /// </summary>
    public async Task<UpdateCheckStatusDto> GetStatusAsync(bool force, CancellationToken ct = default)
    {
        var settings = await LoadOrCreateAsync(ct);

        if (settings.UpdateCheckEnabled && ShouldCheck(settings, force))
            await PerformCheckAsync(settings, ct);

        return BuildDto(settings);
    }

    /// <summary>
    /// Sets the opt-in flag and returns the resulting status. Turning the checker
    /// ON triggers an immediate first check; turning it OFF just persists the flag
    /// (the cached last-known version is retained but no longer surfaced as an
    /// available update).
    /// </summary>
    public async Task<UpdateCheckStatusDto> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        var settings = await LoadOrCreateAsync(ct);
        var wasEnabled = settings.UpdateCheckEnabled;
        settings.UpdateCheckEnabled = enabled;
        await _db.SaveChangesAsync(ct);

        // Enabling for the first time (or re-enabling) should show a result
        // promptly rather than waiting up to 24h for the next page load.
        if (enabled && !wasEnabled)
            await PerformCheckAsync(settings, ct);

        return BuildDto(settings);
    }

    private bool ShouldCheck(AppSettingsEntity settings, bool force)
    {
        if (force)
            return true;
        if (settings.UpdateLastCheckedAt is not { } last)
            return true;
        return _clock.GetUtcNow() - last >= CheckInterval;
    }

    /// <summary>
    /// Best-effort GitHub call. Always advances the last-checked timestamp;
    /// updates the cached latest version only on a successful parse. Swallows all
    /// failures (logged as a sanitized warning) so callers are never blocked.
    /// </summary>
    private async Task PerformCheckAsync(AppSettingsEntity settings, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        try
        {
            var client = _httpFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(ReleasesUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Update check returned status {Status}", (int)response.StatusCode);
            }
            else
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var tag = ExtractTagName(await JsonDocument.ParseAsync(stream, cancellationToken: ct));
                var parsed = UpdateVersion.TryParse(tag);
                if (parsed is { } p)
                    settings.UpdateLastKnownLatestVersion = FormatVersion(p);
                else
                    _logger?.LogWarning("Update check received an unparseable release tag");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            // Offline, DNS failure, timeout, rate limit reset, malformed body —
            // all non-fatal. No paths or response bodies are logged.
            _logger?.LogWarning("Update check failed: {Reason}", ex.GetType().Name);
        }
        finally
        {
            settings.UpdateLastCheckedAt = now;
            await _db.SaveChangesAsync(ct);
        }
    }

    private static string? ExtractTagName(JsonDocument doc)
    {
        using (doc)
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("tag_name", out var tag) &&
                tag.ValueKind == JsonValueKind.String)
            {
                return tag.GetString();
            }
        }

        return null;
    }

    private UpdateCheckStatusDto BuildDto(AppSettingsEntity settings)
    {
        var latest = settings.UpdateLastKnownLatestVersion;
        return new UpdateCheckStatusDto
        {
            Enabled = settings.UpdateCheckEnabled,
            CurrentVersion = _currentVersion,
            LatestVersion = latest,
            UpdateAvailable = UpdateVersion.IsNewer(latest, _currentVersion),
            LastChecked = settings.UpdateLastCheckedAt,
        };
    }

    private async Task<AppSettingsEntity> LoadOrCreateAsync(CancellationToken ct)
    {
        var settings = await _db.AppSettings
            .FirstOrDefaultAsync(x => x.Id == AppSettingsEntity.SingletonId, ct);
        if (settings is not null)
            return settings;

        settings = new AppSettingsEntity { Id = AppSettingsEntity.SingletonId };
        _db.AppSettings.Add(settings);
        await _db.SaveChangesAsync(ct);
        return settings;
    }

    private static string FormatVersion(UpdateVersion.Parsed p)
    {
        var core = $"{p.Major}.{p.Minor}.{p.Patch}";
        return p.PreRelease is null ? core : $"{core}-{p.PreRelease}";
    }

    /// <summary>
    /// Resolves the running version from the assembly informational version
    /// (sourced from Version.props), normalized to drop any leading "v" and build
    /// metadata so it compares cleanly against a GitHub tag.
    /// </summary>
    private static string ResolveCurrentVersion()
    {
        var raw = typeof(UpdateCheckService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        var parsed = UpdateVersion.TryParse(raw);
        if (parsed is { } p)
            return FormatVersion(p);

        // Fall back to the numeric assembly version, then a sentinel, so the
        // service never throws during construction.
        return typeof(UpdateCheckService).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
