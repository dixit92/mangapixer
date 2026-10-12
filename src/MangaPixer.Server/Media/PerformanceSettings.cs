namespace com.lifepixer.mangapixer.Server.Media;

using System.Globalization;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Configuration layer of the performance settings (1.42.0), read once at startup. Each field is null when its key
/// is absent or invalid; a configured value wins over the admin setting and is shown as pinned in the UI.
/// </summary>
public sealed class PerformanceConfiguration
{
    public const string WorkersKey = "MangaPixer:Media:MaxConcurrentJobs";
    public const string PageCacheKey = "MangaPixer:Storage:CacheBudgetBytes";
    public const string BackgroundKey = "MangaPixer:Media:BackgroundWhileReading";

    public int? MediaWorkers { get; init; }
    public long? PageCacheBytes { get; init; }
    public BackgroundWhileReading? BackgroundWhileReading { get; init; }

    /// <summary>Keys present but invalid (logged once at startup, by key name only).</summary>
    public IReadOnlyList<string> InvalidKeys { get; init; } = [];

    public static PerformanceConfiguration FromConfiguration(IConfiguration config)
    {
        var invalid = new List<string>();
        int? workers = null;
        long? cache = null;
        BackgroundWhileReading? background = null;

        var w = config[WorkersKey];
        if (!string.IsNullOrWhiteSpace(w))
        {
            if (int.TryParse(w.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0)
                workers = n;
            else
                invalid.Add(WorkersKey);
        }

        var c = config[PageCacheKey];
        if (!string.IsNullOrWhiteSpace(c))
        {
            if (long.TryParse(c.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) && bytes > 0)
                cache = bytes;
            else
                invalid.Add(PageCacheKey);
        }

        var b = config[BackgroundKey];
        if (!string.IsNullOrWhiteSpace(b))
        {
            if (PerformanceSettingsResolver.TryParseBackground(b, out var mode))
                background = mode;
            else
                invalid.Add(BackgroundKey);
        }

        return new PerformanceConfiguration { MediaWorkers = workers, PageCacheBytes = cache, BackgroundWhileReading = background, InvalidKeys = invalid };
    }
}

/// <summary>The performance settings in effect, with the source of every field (<see cref="BackupSettingSources"/> vocabulary).</summary>
public sealed record EffectivePerformanceSettings(
    int MediaWorkers, string MediaWorkersSource,
    long PageCacheBytes, string PageCacheSource,
    BackgroundWhileReading BackgroundWhileReading, string BackgroundWhileReadingSource);

/// <summary>
/// Resolves the performance settings per field - configuration, then the AppSettings row, then the built-in
/// default (as the backup settings) - and applies them LIVE to the media worker pool (worker cap, background policy)
/// and the page cache (budget). <see cref="ReloadAsync"/> runs at startup and after every admin change.
/// </summary>
public sealed class PerformanceSettingsResolver
{
    public const int MinWorkers = 1;
    public const int MaxWorkers = 8;
    public const long MinPageCacheBytes = 256L * 1024 * 1024;
    public const long MaxPageCacheBytes = 64L * 1024 * 1024 * 1024;
    public const long DefaultPageCacheBytes = 1L * 1024 * 1024 * 1024;
    public const BackgroundWhileReading DefaultBackground = BackgroundWhileReading.Slow;

    private readonly PerformanceConfiguration _config;
    private readonly MediaWorkerPool? _pool;
    private readonly CacheService? _cache;
    private readonly IServiceScopeFactory? _scopes;
    private readonly ILogger<PerformanceSettingsResolver>? _logger;
    private readonly int _processorCount;
    private readonly object _gate = new();
    private EffectivePerformanceSettings _current;

    public PerformanceSettingsResolver(
        PerformanceConfiguration config,
        MediaWorkerPool? pool = null,
        CacheService? cache = null,
        IServiceScopeFactory? scopes = null,
        ILogger<PerformanceSettingsResolver>? logger = null,
        int? processorCount = null)
    {
        _config = config;
        _pool = pool;
        _cache = cache;
        _scopes = scopes;
        _logger = logger;
        _processorCount = processorCount ?? Environment.ProcessorCount;
        _current = Compute(config, null, _processorCount);
    }

    public PerformanceConfiguration Configuration => _config;

    /// <summary>Processor count the default worker number is derived from.</summary>
    public int ProcessorCount => _processorCount;

    /// <summary>The built-in default worker number: a quarter of the processors, at least 2 and at most 4.</summary>
    public int DefaultWorkers => DefaultWorkersFor(_processorCount);

    public static int DefaultWorkersFor(int processorCount) => Math.Clamp(processorCount / 4, 2, 4);

    public EffectivePerformanceSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public static bool TryParseBackground(string? value, out BackgroundWhileReading mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "pause": mode = BackgroundWhileReading.Pause; return true;
            case "slow": mode = BackgroundWhileReading.Slow; return true;
            case "normal": mode = BackgroundWhileReading.Normal; return true;
            default: mode = DefaultBackground; return false;
        }
    }

    public static string BackgroundName(BackgroundWhileReading mode) => mode switch
    {
        BackgroundWhileReading.Pause => "pause",
        BackgroundWhileReading.Normal => "normal",
        _ => "slow",
    };

    /// <summary>Reloads from the AppSettings row in its own scope and applies the result.</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        if (_scopes is null)
        {
            Apply(null);
            return;
        }
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == AppSettingsEntity.SingletonId, ct);
        Apply(row);
    }

    /// <summary>Computes the effective settings from <paramref name="row"/> (or none) and applies them to the pool and the cache.</summary>
    public EffectivePerformanceSettings Apply(AppSettingsEntity? row)
    {
        EffectivePerformanceSettings next;
        lock (_gate)
        {
            next = Compute(_config, row, _processorCount);
            _current = next;
        }
        if (_pool is not null)
        {
            if (_pool.MaxConcurrentJobs != next.MediaWorkers)
                _pool.SetMaxConcurrentJobs(next.MediaWorkers);
            if (_pool.BackgroundWhileReading != next.BackgroundWhileReading)
                _pool.BackgroundWhileReading = next.BackgroundWhileReading;
        }
        if (_cache is not null && _cache.BudgetBytes != next.PageCacheBytes)
            _cache.SetBudget(next.PageCacheBytes);
        _logger?.LogInformation(LogEvents.Worker.PerformanceSettingsApplied,
            "Performance settings: {Workers} media workers ({WorkersSource}), page cache {CacheBytes} bytes ({CacheSource}), background while reading {Background} ({BackgroundSource})",
            next.MediaWorkers, next.MediaWorkersSource, next.PageCacheBytes, next.PageCacheSource,
            BackgroundName(next.BackgroundWhileReading), next.BackgroundWhileReadingSource);
        return next;
    }

    /// <summary>Per-field precedence: configuration, then the AppSettings row, then the default.</summary>
    public static EffectivePerformanceSettings Compute(PerformanceConfiguration config, AppSettingsEntity? row, int processorCount)
    {
        var (workers, workersSource) = Pick(config.MediaWorkers, row?.PerformanceMediaWorkers is { } w && w is >= MinWorkers and <= MaxWorkers ? w : null, DefaultWorkersFor(processorCount));
        var (cache, cacheSource) = Pick(config.PageCacheBytes, row?.PerformancePageCacheBytes is { } c && c is >= MinPageCacheBytes and <= MaxPageCacheBytes ? c : null, DefaultPageCacheBytes);
        BackgroundWhileReading? stored = row?.PerformanceBackgroundWhileReading is { } b && Enum.IsDefined(typeof(BackgroundWhileReading), b) ? (BackgroundWhileReading)b : null;
        var (background, backgroundSource) = Pick(config.BackgroundWhileReading, stored, DefaultBackground);
        return new EffectivePerformanceSettings(workers, workersSource, cache, cacheSource, background, backgroundSource);
    }

    private static (T Value, string Source) Pick<T>(T? configured, T? stored, T fallback) where T : struct =>
        configured is { } c ? (c, BackupSettingSources.Configuration)
        : stored is { } s ? (s, BackupSettingSources.Settings)
        : (fallback, BackupSettingSources.Default);
}

/// <summary>Applies the stored performance settings when the server starts (before the first worker is needed).</summary>
public sealed class PerformanceSettingsStartup(PerformanceSettingsResolver resolver, ILogger<PerformanceSettingsStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var key in resolver.Configuration.InvalidKeys)
            logger.LogWarning(LogEvents.Worker.PerformanceConfigInvalid, "Ignoring invalid performance configuration value for {Key}", key);
        try
        {
            await resolver.ReloadAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The configuration / default values stay in effect; the admin page shows them.
            logger.LogWarning(LogEvents.Worker.PerformanceSettingsLoadFailed, "Could not load the stored performance settings: {Error}", ex.GetType().Name);
            resolver.Apply(null);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Admin view of the performance settings (<c>GET/PUT /api/v1/admin/performance</c>). Every field carries its source.</summary>
public sealed record PerformanceSettingsDto
{
    /// <summary>Media worker processes in effect.</summary>
    public required int MediaWorkers { get; init; }

    /// <summary><c>default</c> | <c>settings</c> | <c>configuration</c> (configuration = pinned, read-only in the UI).</summary>
    public required string MediaWorkersSource { get; init; }

    public required int MediaWorkersDefault { get; init; }
    public required int MediaWorkersMin { get; init; }
    public required int MediaWorkersMax { get; init; }

    /// <summary>Processors the server sees (the default is a quarter of them, 2-4).</summary>
    public required int ProcessorCount { get; init; }

    /// <summary>Page cache budget in bytes in effect.</summary>
    public required long PageCacheBytes { get; init; }

    public required string PageCacheSource { get; init; }
    public required long PageCacheDefaultBytes { get; init; }
    public required long PageCacheMinBytes { get; init; }
    public required long PageCacheMaxBytes { get; init; }

    /// <summary>Bytes the page cache holds right now.</summary>
    public required long PageCacheUsageBytes { get; init; }

    /// <summary><c>pause</c> | <c>slow</c> | <c>normal</c>.</summary>
    public required string BackgroundWhileReading { get; init; }

    public required string BackgroundWhileReadingSource { get; init; }
    public required string BackgroundWhileReadingDefault { get; init; }
}

/// <summary>
/// Partial update of the performance settings. A field left out is unchanged; a field named in <see cref="Reset"/>
/// goes back to its default. A field pinned by configuration is refused (409).
/// </summary>
public sealed record UpdatePerformanceSettingsRequest
{
    public int? MediaWorkers { get; init; }
    public long? PageCacheBytes { get; init; }

    /// <summary><c>pause</c> | <c>slow</c> | <c>normal</c>.</summary>
    public string? BackgroundWhileReading { get; init; }

    /// <summary>Fields to return to their default: <c>mediaWorkers</c>, <c>pageCacheBytes</c>, <c>backgroundWhileReading</c>.</summary>
    public IReadOnlyList<string>? Reset { get; init; }
}

/// <summary>Reads and changes the performance settings (admin).</summary>
public sealed class PerformanceSettingsService(MangaPixerDbContext db, PerformanceSettingsResolver resolver, CacheService cache)
{
    public const string ResetWorkers = "mediaWorkers";
    public const string ResetPageCache = "pageCacheBytes";
    public const string ResetBackground = "backgroundWhileReading";

    public PerformanceSettingsDto Get()
    {
        var e = resolver.Current;
        return new PerformanceSettingsDto
        {
            MediaWorkers = e.MediaWorkers,
            MediaWorkersSource = e.MediaWorkersSource,
            MediaWorkersDefault = resolver.DefaultWorkers,
            MediaWorkersMin = PerformanceSettingsResolver.MinWorkers,
            MediaWorkersMax = PerformanceSettingsResolver.MaxWorkers,
            ProcessorCount = resolver.ProcessorCount,
            PageCacheBytes = e.PageCacheBytes,
            PageCacheSource = e.PageCacheSource,
            PageCacheDefaultBytes = PerformanceSettingsResolver.DefaultPageCacheBytes,
            PageCacheMinBytes = PerformanceSettingsResolver.MinPageCacheBytes,
            PageCacheMaxBytes = PerformanceSettingsResolver.MaxPageCacheBytes,
            PageCacheUsageBytes = cache.GetCurrentUsageBytes(),
            BackgroundWhileReading = PerformanceSettingsResolver.BackgroundName(e.BackgroundWhileReading),
            BackgroundWhileReadingSource = e.BackgroundWhileReadingSource,
            BackgroundWhileReadingDefault = PerformanceSettingsResolver.BackgroundName(PerformanceSettingsResolver.DefaultBackground),
        };
    }

    /// <summary>Validates and stores the change, then applies it live. Returns (status, error code, message) on refusal.</summary>
    public async Task<(PerformanceSettingsDto? Result, int Status, string? Error, string? Message)> UpdateAsync(
        UpdatePerformanceSettingsRequest request, CancellationToken ct)
    {
        var reset = new HashSet<string>(request.Reset ?? [], StringComparer.Ordinal);
        if (reset.Any(r => r is not (ResetWorkers or ResetPageCache or ResetBackground)))
            return (null, 400, "invalid_request", "reset may name mediaWorkers, pageCacheBytes or backgroundWhileReading.");
        if (request.MediaWorkers is { } w && w is < PerformanceSettingsResolver.MinWorkers or > PerformanceSettingsResolver.MaxWorkers)
            return (null, 400, "invalid_request", $"mediaWorkers must be between {PerformanceSettingsResolver.MinWorkers} and {PerformanceSettingsResolver.MaxWorkers}.");
        if (request.PageCacheBytes is { } c && c is < PerformanceSettingsResolver.MinPageCacheBytes or > PerformanceSettingsResolver.MaxPageCacheBytes)
            return (null, 400, "invalid_request", "pageCacheBytes must be between 256 MiB and 64 GiB.");
        BackgroundWhileReading? background = null;
        if (request.BackgroundWhileReading is not null)
        {
            if (!PerformanceSettingsResolver.TryParseBackground(request.BackgroundWhileReading, out var mode))
                return (null, 400, "invalid_request", "backgroundWhileReading must be pause, slow or normal.");
            background = mode;
        }

        var config = resolver.Configuration;
        if ((config.MediaWorkers is not null && (request.MediaWorkers is not null || reset.Contains(ResetWorkers)))
            || (config.PageCacheBytes is not null && (request.PageCacheBytes is not null || reset.Contains(ResetPageCache)))
            || (config.BackgroundWhileReading is not null && (background is not null || reset.Contains(ResetBackground))))
            return (null, 409, "managed_by_config", "This setting is set by server configuration and cannot be changed here.");

        var row = await db.AppSettings.FirstOrDefaultAsync(x => x.Id == AppSettingsEntity.SingletonId, ct);
        if (row is null)
        {
            row = new AppSettingsEntity();
            db.AppSettings.Add(row);
        }
        if (request.MediaWorkers is not null)
            row.PerformanceMediaWorkers = request.MediaWorkers;
        if (reset.Contains(ResetWorkers))
            row.PerformanceMediaWorkers = null;
        if (request.PageCacheBytes is not null)
            row.PerformancePageCacheBytes = request.PageCacheBytes;
        if (reset.Contains(ResetPageCache))
            row.PerformancePageCacheBytes = null;
        if (background is not null)
            row.PerformanceBackgroundWhileReading = (int)background.Value;
        if (reset.Contains(ResetBackground))
            row.PerformanceBackgroundWhileReading = null;
        await db.SaveChangesAsync(ct);

        resolver.Apply(row);
        return (Get(), 200, null, null);
    }
}
