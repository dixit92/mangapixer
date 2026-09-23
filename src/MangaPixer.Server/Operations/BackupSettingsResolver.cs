namespace com.lifepixer.mangapixer.Server.Operations;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Globalization;
using System.IO;

/// <summary>Where an effective backup setting came from.</summary>
public static class BackupSettingSources
{
    public const string Default = "default";
    public const string Settings = "settings";
    public const string Configuration = "configuration";
}

/// <summary>
/// Configuration layer of the backup settings (<c>MangaPixer:Backups:*</c>),
/// read once at startup. Each field is null when the key is absent or invalid;
/// the effective value is resolved per field by <see cref="BackupSettingsResolver"/>
/// (configuration, then the admin UI's AppSettings row, then the built-in default).
/// </summary>
public sealed class RotatingBackupOptions
{
    public const bool DefaultEnabled = true;
    public const double DefaultIntervalHours = 24;
    public const int DefaultRetentionCount = 7;

    public bool? Enabled { get; set; }
    public double? IntervalHours { get; set; }
    public int? RetentionCount { get; set; }

    /// <summary>Operator-pinned rotating backup directory (<c>MangaPixer:Backups:Location</c>).</summary>
    public string? Location { get; set; }

    /// <summary>
    /// <c>MangaPixer:Backups:AllowLocationChange</c> (default true). False makes the
    /// location read-only in the UI even when no location is configured.
    /// </summary>
    public bool AllowLocationChange { get; set; } = true;

    /// <summary>
    /// <c>&lt;dataRoot&gt;/backups</c>: always holds the pre-migration and
    /// pre-restore safety snapshots, and the rotating snapshots in default mode.
    /// </summary>
    public string SafetyBackupDirectory { get; set; } = string.Empty;

    /// <summary>Keys that were present but invalid (logged once at startup, by key name only).</summary>
    public List<string> InvalidKeys { get; } = new();

    public static RotatingBackupOptions FromConfiguration(IConfiguration config, string safetyBackupDirectory)
    {
        var options = new RotatingBackupOptions { SafetyBackupDirectory = safetyBackupDirectory };

        var enabled = config["MangaPixer:Backups:Enabled"];
        if (!string.IsNullOrWhiteSpace(enabled))
        {
            if (bool.TryParse(enabled, out var value)) options.Enabled = value;
            else options.InvalidKeys.Add("MangaPixer:Backups:Enabled");
        }

        var hours = config["MangaPixer:Backups:IntervalHours"];
        if (!string.IsNullOrWhiteSpace(hours))
        {
            if (double.TryParse(hours, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                value > 0 && value <= 24 * 366)
                options.IntervalHours = value;
            else
                options.InvalidKeys.Add("MangaPixer:Backups:IntervalHours");
        }

        var retention = config["MangaPixer:Backups:RetentionCount"];
        if (!string.IsNullOrWhiteSpace(retention))
        {
            if (int.TryParse(retention, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0)
                options.RetentionCount = value;
            else
                options.InvalidKeys.Add("MangaPixer:Backups:RetentionCount");
        }

        var location = config["MangaPixer:Backups:Location"];
        if (!string.IsNullOrWhiteSpace(location))
            options.Location = location.Trim();

        var allow = config["MangaPixer:Backups:AllowLocationChange"];
        if (!string.IsNullOrWhiteSpace(allow))
        {
            if (bool.TryParse(allow, out var value)) options.AllowLocationChange = value;
            else options.InvalidKeys.Add("MangaPixer:Backups:AllowLocationChange");
        }

        return options;
    }
}

/// <summary>
/// The backup settings in effect right now, with the source of every field.
/// <see cref="RotatingDirectory"/> is a server-internal value: it is never
/// emitted by an API response (only <see cref="CustomLocation"/>, and only by
/// the admin settings endpoints).
/// </summary>
public sealed record EffectiveBackupSettings
{
    public const string KindDefault = "default";
    public const string KindCustom = "custom";

    public required bool Enabled { get; init; }
    public required string EnabledSource { get; init; }
    public required double IntervalHours { get; init; }
    public required string IntervalHoursSource { get; init; }
    public required int RetentionCount { get; init; }
    public required string RetentionCountSource { get; init; }
    public required string LocationKind { get; init; }
    public required string LocationSource { get; init; }
    public required string? CustomLocation { get; init; }
    public required string RotatingDirectory { get; init; }
    public required string SafetyDirectory { get; init; }
    public required bool LocationChangeAllowed { get; init; }
    public required string? MarkerId { get; init; }

    public TimeSpan Interval => TimeSpan.FromHours(IntervalHours);

    public bool IsCustom => LocationKind == KindCustom;
}

/// <summary>
/// Resolves <see cref="EffectiveBackupSettings"/> per field from configuration,
/// the AppSettings row, and the built-in defaults, and caches the result.
/// <see cref="ReloadAsync"/> re-reads the row (after a settings PUT) and
/// cancels <see cref="ChangeToken"/>, which wakes the scheduler so a new
/// interval / enabled flag / location applies without a restart.
/// </summary>
public sealed class BackupSettingsResolver
{
    private readonly RotatingBackupOptions _config;
    private readonly IServiceScopeFactory? _scopes;
    private readonly object _gate = new();
    private EffectiveBackupSettings _current;
    private CancellationTokenSource _changed = new();

    public BackupSettingsResolver(RotatingBackupOptions config, IServiceScopeFactory? scopes = null)
    {
        _config = config;
        _scopes = scopes;
        _current = Compute(config, null);
    }

    public RotatingBackupOptions Configuration => _config;

    public EffectiveBackupSettings Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Cancelled whenever the effective settings are reloaded.</summary>
    public CancellationToken ChangeToken
    {
        get { lock (_gate) return _changed.Token; }
    }

    /// <summary>Reloads from the AppSettings row via its own DI scope.</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        if (_scopes is null)
            return;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        await ReloadAsync(db, ct);
    }

    /// <summary>Reloads from the AppSettings row using the caller's context.</summary>
    public async Task ReloadAsync(MangaPixerDbContext db, CancellationToken ct = default)
    {
        var row = await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == AppSettingsEntity.SingletonId, ct);
        Apply(row);
    }

    /// <summary>Applies an already-loaded AppSettings row (or none) and signals the change.</summary>
    public void Apply(AppSettingsEntity? row)
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            _current = Compute(_config, row);
            previous = _changed;
            _changed = new CancellationTokenSource();
        }
        previous.Cancel();
        previous.Dispose();
    }

    /// <summary>Per-field precedence: configuration, then AppSettings, then default.</summary>
    public static EffectiveBackupSettings Compute(RotatingBackupOptions config, AppSettingsEntity? row)
    {
        var (enabled, enabledSource) = Pick(config.Enabled, row?.BackupsEnabled, RotatingBackupOptions.DefaultEnabled);
        var (hours, hoursSource) = Pick(config.IntervalHours, row?.BackupIntervalHours, RotatingBackupOptions.DefaultIntervalHours);
        var (retention, retentionSource) = Pick(config.RetentionCount, row?.BackupRetentionCount, RotatingBackupOptions.DefaultRetentionCount);

        string? customLocation;
        string locationSource;
        if (!string.IsNullOrWhiteSpace(config.Location))
        {
            customLocation = config.Location;
            locationSource = BackupSettingSources.Configuration;
        }
        else if (!string.IsNullOrWhiteSpace(row?.BackupLocation))
        {
            customLocation = row!.BackupLocation;
            locationSource = BackupSettingSources.Settings;
        }
        else
        {
            customLocation = null;
            locationSource = BackupSettingSources.Default;
        }

        return new EffectiveBackupSettings
        {
            Enabled = enabled,
            EnabledSource = enabledSource,
            IntervalHours = hours,
            IntervalHoursSource = hoursSource,
            RetentionCount = retention,
            RetentionCountSource = retentionSource,
            LocationKind = customLocation is null ? EffectiveBackupSettings.KindDefault : EffectiveBackupSettings.KindCustom,
            LocationSource = locationSource,
            CustomLocation = customLocation,
            RotatingDirectory = customLocation ?? config.SafetyBackupDirectory,
            SafetyDirectory = config.SafetyBackupDirectory,
            LocationChangeAllowed = config.AllowLocationChange && string.IsNullOrWhiteSpace(config.Location),
            MarkerId = row?.BackupLocationMarkerId,
        };
    }

    private static (T Value, string Source) Pick<T>(T? fromConfig, T? fromSettings, T fallback)
        where T : struct
    {
        if (fromConfig is { } c) return (c, BackupSettingSources.Configuration);
        if (fromSettings is { } s) return (s, BackupSettingSources.Settings);
        return (fallback, BackupSettingSources.Default);
    }

    /// <summary>The default safety directory for a data root.</summary>
    public static string SafetyDirectoryFor(string? dataRoot) => Path.Combine(
        string.IsNullOrWhiteSpace(dataRoot) ? Path.Combine(AppContext.BaseDirectory, "data") : dataRoot,
        "backups");
}
