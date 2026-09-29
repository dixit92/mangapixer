namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Persisted provider backoff (1.24.0, lane B2). State lives on <c>app_settings</c>
/// (<c>MetadataBackoffUntil</c>, <c>MetadataBackoffStep</c>, last error), so a
/// restart does not reset it. Rules (owner-approved):
/// - 429 / 503: honour <c>Retry-After</c> (seconds or HTTP date), capped at 1 h;
///   without it the ladder 30 s -> 2 min -> 10 min -> 1 h.
/// - 3 consecutive 5xx / timeouts: 5 min.
/// - Any success resets the ladder and the failure streak.
/// Interactive calls are never retried automatically.
/// 1.29.0 (gateway generalisation): the backoff is PER PROVIDER. MangaUpdates keeps its original <c>app_settings</c>
/// columns (no destructive move); every other provider (AniList, MangaDex) has a <c>metadata_provider_state</c> row,
/// so a MangaDex 429 pauses MangaDex only.
/// </summary>
public sealed class MetadataBackoff
{
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);
    public static readonly TimeSpan FailureStreakBackoff = TimeSpan.FromMinutes(5);
    public const int FailureStreakLimit = 3;

    private static readonly TimeSpan[] s_ladder =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
    ];

    private const string Default = MetadataProviderAllowlist.MangaUpdates;

    private readonly MangaPixerDbContext _db;
    private readonly MetadataGatewayState _state;
    private readonly TimeProvider _time;
    private readonly ILogger<MetadataBackoff> _logger;

    public MetadataBackoff(MangaPixerDbContext db, MetadataGatewayState state, TimeProvider time, ILogger<MetadataBackoff> logger)
    {
        _db = db;
        _state = state;
        _time = time;
        _logger = logger;
    }

    private sealed record Row(DateTimeOffset? Until, int Step);

    /// <summary>The active backoff end of MangaUpdates, or null when calls may go out.</summary>
    public Task<DateTimeOffset?> ActiveUntilAsync(CancellationToken ct = default) => ActiveUntilAsync(Default, ct);

    /// <summary>The active backoff end of <paramref name="providerId"/>, or null when calls may go out.</summary>
    public async Task<DateTimeOffset?> ActiveUntilAsync(string providerId, CancellationToken ct = default)
    {
        var until = (await ReadAsync(providerId, ct))?.Until;
        return until is { } u && u > _time.GetUtcNow() ? u : null;
    }

    /// <summary>A 429/503: backs off by Retry-After (capped) or the next ladder step.</summary>
    public Task<DateTimeOffset> RecordRateLimitedAsync(TimeSpan? retryAfterDelta, DateTimeOffset? retryAfterDate, string errorCode, CancellationToken ct = default) =>
        RecordRateLimitedAsync(Default, retryAfterDelta, retryAfterDate, errorCode, ct);

    /// <summary>A 429/503 (or a MangaDex 403) from <paramref name="providerId"/>.</summary>
    public async Task<DateTimeOffset> RecordRateLimitedAsync(
        string providerId, TimeSpan? retryAfterDelta, DateTimeOffset? retryAfterDate, string errorCode, CancellationToken ct = default)
    {
        await _state.StateLock.WaitAsync(ct);
        try
        {
            _state.ResetFailureStreak(providerId);
            var now = _time.GetUtcNow();
            var step = (await ReadAsync(providerId, ct))?.Step ?? 0;
            var delay = retryAfterDelta
                ?? (retryAfterDate is { } date ? date - now : (TimeSpan?)null)
                ?? s_ladder[Math.Min(step, s_ladder.Length - 1)];
            delay = delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay > MaxBackoff ? MaxBackoff : delay;
            var until = now + delay;
            await WriteAsync(providerId, until, Math.Min(step + 1, s_ladder.Length), now, errorCode, ct);
            _logger.LogWarning(LogEvents.Metadata.BackoffStarted, "Metadata provider {Provider} backoff for {Seconds} s ({Code})",
                providerId, (int)delay.TotalSeconds, errorCode);
            return until;
        }
        finally
        {
            _state.StateLock.Release();
        }
    }

    /// <summary>A 5xx or timeout: records the error; the third in a row backs off 5 min. Returns the backoff end when one started.</summary>
    public Task<DateTimeOffset?> RecordFailureAsync(string errorCode, bool countsTowardStreak, CancellationToken ct = default) =>
        RecordFailureAsync(Default, errorCode, countsTowardStreak, ct);

    public async Task<DateTimeOffset?> RecordFailureAsync(string providerId, string errorCode, bool countsTowardStreak, CancellationToken ct = default)
    {
        await _state.StateLock.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            DateTimeOffset? until = null;
            if (countsTowardStreak && _state.IncrementFailureStreak(providerId) >= FailureStreakLimit)
            {
                _state.ResetFailureStreak(providerId);
                until = now + FailureStreakBackoff;
                _logger.LogWarning(LogEvents.Metadata.BackoffStarted, "Metadata provider {Provider} backoff for {Seconds} s ({Code})",
                    providerId, (int)FailureStreakBackoff.TotalSeconds, errorCode);
            }
            var step = (await ReadAsync(providerId, ct))?.Step ?? 0;
            await WriteAsync(providerId, until, step, now, errorCode, ct, keepUntil: until is null);
            return until;
        }
        finally
        {
            _state.StateLock.Release();
        }
    }

    /// <summary>A successful call: clears the ladder and the failure streak (a write only when there is something to clear).</summary>
    public Task RecordSuccessAsync(CancellationToken ct = default) => RecordSuccessAsync(Default, ct);

    public async Task RecordSuccessAsync(string providerId, CancellationToken ct = default)
    {
        _state.ResetFailureStreak(providerId);
        var step = (await ReadAsync(providerId, ct))?.Step ?? 0;
        if (step == 0)
            return;
        await _state.StateLock.WaitAsync(ct);
        try
        {
            if (providerId == Default)
            {
                await _db.AppSettings
                    .Where(s => s.Id == AppSettingsEntity.SingletonId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.MetadataBackoffStep, 0)
                        .SetProperty(x => x.MetadataBackoffUntil, (DateTimeOffset?)null), ct);
            }
            else
            {
                await _db.MetadataProviderStates
                    .Where(s => s.Provider == providerId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.BackoffStep, 0)
                        .SetProperty(x => x.BackoffUntil, (DateTimeOffset?)null), ct);
            }
        }
        finally
        {
            _state.StateLock.Release();
        }
    }

    /// <summary>The last error of a provider (code and time), for status lines; null when none is recorded.</summary>
    public async Task<(string? Code, DateTimeOffset? At)> LastErrorAsync(string providerId, CancellationToken ct = default)
    {
        if (providerId == Default)
        {
            var mu = await _db.AppSettings.AsNoTracking().Where(s => s.Id == AppSettingsEntity.SingletonId)
                .Select(s => new { s.MetadataLastErrorCode, s.MetadataLastErrorAt }).FirstOrDefaultAsync(ct);
            return (mu?.MetadataLastErrorCode, mu?.MetadataLastErrorAt);
        }
        var row = await _db.MetadataProviderStates.AsNoTracking().Where(s => s.Provider == providerId)
            .Select(s => new { s.LastErrorCode, s.LastErrorAt }).FirstOrDefaultAsync(ct);
        return (row?.LastErrorCode, row?.LastErrorAt);
    }

    private async Task<Row?> ReadAsync(string providerId, CancellationToken ct)
    {
        if (providerId == Default)
        {
            return await _db.AppSettings.AsNoTracking()
                .Where(s => s.Id == AppSettingsEntity.SingletonId)
                .Select(s => new Row(s.MetadataBackoffUntil, s.MetadataBackoffStep))
                .FirstOrDefaultAsync(ct);
        }
        return await _db.MetadataProviderStates.AsNoTracking()
            .Where(s => s.Provider == providerId)
            .Select(s => new Row(s.BackoffUntil, s.BackoffStep))
            .FirstOrDefaultAsync(ct);
    }

    private async Task WriteAsync(string providerId, DateTimeOffset? until, int step, DateTimeOffset now, string errorCode, CancellationToken ct, bool keepUntil = false)
    {
        var code = errorCode.Length <= 32 ? errorCode : errorCode[..32];
        if (providerId == Default)
        {
            var query = _db.AppSettings.Where(s => s.Id == AppSettingsEntity.SingletonId);
            if (keepUntil)
            {
                await query.ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.MetadataBackoffStep, step)
                    .SetProperty(x => x.MetadataLastErrorAt, now)
                    .SetProperty(x => x.MetadataLastErrorCode, code), ct);
            }
            else
            {
                await query.ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.MetadataBackoffUntil, until)
                    .SetProperty(x => x.MetadataBackoffStep, step)
                    .SetProperty(x => x.MetadataLastErrorAt, now)
                    .SetProperty(x => x.MetadataLastErrorCode, code), ct);
            }
            return;
        }

        // Writes are serialized by StateLock (one process), so insert-if-missing cannot race.
        if (!await _db.MetadataProviderStates.AnyAsync(s => s.Provider == providerId, ct))
        {
            _db.MetadataProviderStates.Add(new MetadataProviderStateEntity { Provider = providerId });
            await _db.SaveChangesAsync(ct);
        }
        var rows = _db.MetadataProviderStates.Where(s => s.Provider == providerId);
        if (keepUntil)
        {
            await rows.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.BackoffStep, step)
                .SetProperty(x => x.LastErrorAt, now)
                .SetProperty(x => x.LastErrorCode, code), ct);
        }
        else
        {
            await rows.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.BackoffUntil, until)
                .SetProperty(x => x.BackoffStep, step)
                .SetProperty(x => x.LastErrorAt, now)
                .SetProperty(x => x.LastErrorCode, code), ct);
        }
    }
}
