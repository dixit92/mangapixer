namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>Audit verbs of tracker sync (1.43.0). Rows carry the actor and the target user id - never a token or a remote account.</summary>
public static class TrackerAuditActions
{
    public const string Connect = "tracker.connect";
    public const string Disconnect = "tracker.disconnect";
    public const string Settings = "tracker.settings";
}

/// <summary>
/// The admin gate of tracker sync (1.43.0): per tracker, allowed for this server after the admin consent text, plus the instance's own
/// app registration (MyAnimeList: Client ID, optional secret - write-only, encrypted - and the exact redirect URL). Separate from the
/// metadata fetcher (own switches, own consent): "Fetch from the web" off does not stop sync. <c>Trackers:NetworkDisabled=true</c>
/// disables every tracker request regardless of the UI.
/// </summary>
public sealed class TrackerSettingsService(
    MangaPixerDbContext db,
    TrackerTokenProtector protector,
    AuditService audit,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<TrackerSettingsService> logger)
{
    public const string NetworkDisabledConfigKey = "Trackers:NetworkDisabled";

    /// <summary>True when the operator disabled every tracker request in configuration.</summary>
    public bool NetworkDisabledByConfig => IsNetworkDisabled(configuration);

    public static bool IsNetworkDisabled(IConfiguration configuration) =>
        bool.TryParse(configuration[NetworkDisabledConfigKey], out var disabled) && disabled;

    /// <summary>True when users may connect to and sync with <paramref name="tracker"/> right now.</summary>
    public static bool IsAllowed(TrackerSettingsEntity? row, string tracker, bool networkDisabled) =>
        !networkDisabled && row is { Enabled: true } && row.ConsentVersion == TrackerConsent.AdminVersion(tracker);

    public async Task<bool> IsAllowedAsync(string tracker, CancellationToken ct) =>
        IsAllowed(await RowAsync(tracker, ct), tracker, NetworkDisabledByConfig);

    public Task<TrackerSettingsEntity?> RowAsync(string tracker, CancellationToken ct) =>
        db.TrackerSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Tracker == tracker, ct);

    /// <summary>The instance's registration as the adapters take it (the secret decrypted; null when the key ring cannot open it).</summary>
    public async Task<TrackerAppSettings> AppSettingsAsync(string tracker, CancellationToken ct)
    {
        var row = await RowAsync(tracker, ct);
        Uri? redirect = null;
        if (row?.RedirectUri is { } r && Uri.TryCreate(r, UriKind.Absolute, out var uri))
            redirect = uri;
        return new TrackerAppSettings(row?.ClientId, protector.TryUnprotect(row?.ClientSecretProtected), redirect);
    }

    public async Task<IReadOnlyList<AdminTrackerSettingsDto>> ListAsync(CancellationToken ct)
    {
        var rows = await db.TrackerSettings.AsNoTracking().ToListAsync(ct);
        var counts = await db.TrackerAccounts.AsNoTracking().GroupBy(a => a.Tracker)
            .Select(g => new { Tracker = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Tracker, x => x.Count, ct);
        return TrackerSlugs.All.Select(slug => ToDto(slug, rows.FirstOrDefault(r => r.Tracker == slug), counts.GetValueOrDefault(slug))).ToList();
    }

    public async Task<AdminTrackerSettingsDto> GetAsync(string tracker, CancellationToken ct)
    {
        var row = await RowAsync(tracker, ct);
        var count = await db.TrackerAccounts.CountAsync(a => a.Tracker == tracker, ct);
        return ToDto(tracker, row, count);
    }

    private AdminTrackerSettingsDto ToDto(string tracker, TrackerSettingsEntity? row, int connected) => new()
    {
        Slug = tracker,
        Name = TrackerFacts.NameOf(tracker),
        Enabled = row?.Enabled ?? false,
        AcceptedConsentVersion = row?.ConsentVersion,
        CurrentConsentVersion = TrackerConsent.AdminVersion(tracker),
        ConsentAt = row?.ConsentAt,
        ConsentRenewalNeeded = row is { Enabled: true, ConsentVersion: { } v } && v < TrackerConsent.AdminVersion(tracker),
        NetworkDisabledByConfig = NetworkDisabledByConfig,
        ConnectKind = ConnectKindOf(tracker),
        NeedsAppRegistration = TrackerFacts.NeedsAppRegistration(tracker),
        ClientId = row?.ClientId,
        ClientSecretSet = !string.IsNullOrEmpty(row?.ClientSecretProtected),
        RedirectUri = row?.RedirectUri,
        ConnectedUsers = connected,
        DailyRequestCap = TrackerFacts.DailyRequestCapOf(row?.DailyRequestCap),
    };

    public static TrackerConnectKind ConnectKindOf(string tracker) =>
        tracker == TrackerSlugs.MangaUpdates ? TrackerConnectKind.Password : TrackerConnectKind.OAuthPkce;

    /// <summary>Applies a partial update; returns a validation error code, or null on success.</summary>
    public async Task<string?> UpdateAsync(string tracker, UpdateTrackerSettingsRequest request, string? actor, CancellationToken ct)
    {
        var current = TrackerConsent.AdminVersion(tracker);
        var row = await db.TrackerSettings.FirstOrDefaultAsync(s => s.Tracker == tracker, ct);
        var alreadyConsented = row is { Enabled: true } && row.ConsentVersion == current;
        if (request.Enabled == true && !alreadyConsented && request.AcceptedConsentVersion != current)
            return "consent_required";
        if (request.ClientId is { } clientId && (clientId.Trim().Length > 256 || clientId.Any(char.IsControl)))
            return "invalid_client_id";
        if (request.ClientSecret is { } secret && (secret.Length > 1024 || secret.Any(char.IsControl)))
            return "invalid_client_secret";
        if (request.RedirectUri is { Length: > 0 } redirect && !IsValidRedirect(redirect))
            return "invalid_redirect_uri";
        if (request.DailyRequestCap is { } cap && (cap < TrackerFacts.MinDailyRequestCap || cap > TrackerFacts.MaxDailyRequestCap))
            return "invalid_daily_cap";

        var now = time.GetUtcNow();
        if (row is null)
        {
            row = new TrackerSettingsEntity { Tracker = tracker, UpdatedAt = now };
            db.TrackerSettings.Add(row);
        }

        var changes = 0;
        var enabledChange = (string?)null;
        if (request.Enabled == true && !alreadyConsented)
        {
            row.Enabled = true;
            row.ConsentVersion = current;
            row.ConsentAt = now;
            enabledChange = $"enable_consent_v{current}";
            changes++;
        }
        else if (request.Enabled == false && row.Enabled)
        {
            row.Enabled = false;
            enabledChange = "disable";
            changes++;
        }
        if (request.ClientId is { } id)
        {
            var value = id.Trim() is { Length: > 0 } t ? t : null;
            if (!string.Equals(value, row.ClientId, StringComparison.Ordinal))
            {
                row.ClientId = value;
                changes++;
            }
        }
        if (request.ClientSecret is { } newSecret)
        {
            row.ClientSecretProtected = newSecret.Length > 0 ? protector.Protect(newSecret) : null;
            changes++;
        }
        if (request.RedirectUri is { } newRedirect)
        {
            var value = newRedirect.Trim() is { Length: > 0 } t ? t : null;
            if (!string.Equals(value, row.RedirectUri, StringComparison.Ordinal))
            {
                row.RedirectUri = value;
                changes++;
            }
        }
        if (request.DailyRequestCap is { } newCap && TrackerFacts.DailyRequestCapOf(row.DailyRequestCap) != newCap)
        {
            // The default is stored as null, so a later change of the default reaches servers that never chose a cap.
            row.DailyRequestCap = newCap == TrackerFacts.DefaultDailyRequestCap ? null : newCap;
            changes++;
        }
        if (changes == 0)
        {
            if (db.Entry(row).State == EntityState.Added)
                db.TrackerSettings.Remove(row);
            return null;
        }
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(TrackerAuditActions.Settings, enabledChange ?? "change", actor, correlationId: tracker, ct: ct);
        logger.LogInformation(LogEvents.Trackers.SettingsChanged, "Tracker settings changed for {Tracker}: {Count} change(s)", tracker, changes);
        return null;
    }

    /// <summary>An absolute http(s) URL without user info or a fragment (the redirect the tracker matches exactly).</summary>
    public static bool IsValidRedirect(string value) =>
        value.Length <= 1024
        && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment);
}
