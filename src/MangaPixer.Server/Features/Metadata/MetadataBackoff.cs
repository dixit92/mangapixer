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

    /// <summary>The active backoff end, or null when calls may go out.</summary>
    public async Task<DateTimeOffset?> ActiveUntilAsync(CancellationToken ct = default)
    {
        var until = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => s.MetadataBackoffUntil)
            .FirstOrDefaultAsync(ct);
        return until is { } u && u > _time.GetUtcNow() ? u : null;
    }

    /// <summary>A 429/503: backs off by Retry-After (capped) or the next ladder step.</summary>
    public async Task<DateTimeOffset> RecordRateLimitedAsync(TimeSpan? retryAfterDelta, DateTimeOffset? retryAfterDate, string errorCode, CancellationToken ct = default)
    {
        await _state.StateLock.WaitAsync(ct);
        try
        {
            _state.ResetFailureStreak();
            var now = _time.GetUtcNow();
            var step = await CurrentStepAsync(ct);
            var delay = retryAfterDelta
                ?? (retryAfterDate is { } date ? date - now : (TimeSpan?)null)
                ?? s_ladder[Math.Min(step, s_ladder.Length - 1)];
            delay = delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay > MaxBackoff ? MaxBackoff : delay;
            var until = now + delay;
            await WriteAsync(until, Math.Min(step + 1, s_ladder.Length), now, errorCode, ct);
            _logger.LogWarning(LogEvents.Metadata.BackoffStarted, "Metadata provider backoff for {Seconds} s ({Code})", (int)delay.TotalSeconds, errorCode);
            return until;
        }
        finally
        {
            _state.StateLock.Release();
        }
    }

    /// <summary>A 5xx or timeout: records the error; the third in a row backs off 5 min. Returns the backoff end when one started.</summary>
    public async Task<DateTimeOffset?> RecordFailureAsync(string errorCode, bool countsTowardStreak, CancellationToken ct = default)
    {
        await _state.StateLock.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            DateTimeOffset? until = null;
            if (countsTowardStreak && _state.IncrementFailureStreak() >= FailureStreakLimit)
            {
                _state.ResetFailureStreak();
                until = now + FailureStreakBackoff;
                _logger.LogWarning(LogEvents.Metadata.BackoffStarted, "Metadata provider backoff for {Seconds} s ({Code})", (int)FailureStreakBackoff.TotalSeconds, errorCode);
            }
            var step = await CurrentStepAsync(ct);
            await WriteAsync(until, step, now, errorCode, ct, keepUntil: until is null);
            return until;
        }
        finally
        {
            _state.StateLock.Release();
        }
    }

    /// <summary>A successful call: clears the ladder and the failure streak (a write only when there is something to clear).</summary>
    public async Task RecordSuccessAsync(CancellationToken ct = default)
    {
        _state.ResetFailureStreak();
        var step = await CurrentStepAsync(ct);
        if (step == 0)
            return;
        await _state.StateLock.WaitAsync(ct);
        try
        {
            await _db.AppSettings
                .Where(s => s.Id == AppSettingsEntity.SingletonId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.MetadataBackoffStep, 0)
                    .SetProperty(x => x.MetadataBackoffUntil, (DateTimeOffset?)null), ct);
        }
        finally
        {
            _state.StateLock.Release();
        }
    }

    private Task<int> CurrentStepAsync(CancellationToken ct) =>
        _db.AppSettings.AsNoTracking()
            .Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => s.MetadataBackoffStep)
            .FirstOrDefaultAsync(ct);

    private async Task WriteAsync(DateTimeOffset? until, int step, DateTimeOffset now, string errorCode, CancellationToken ct, bool keepUntil = false)
    {
        var code = errorCode.Length <= 32 ? errorCode : errorCode[..32];
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
    }
}
