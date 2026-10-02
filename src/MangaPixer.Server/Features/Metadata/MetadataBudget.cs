namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The persisted daily request budget (1.24.0, lane B2). Every outbound request
/// (search, get, image) counts one. The counter lives on <c>app_settings</c>
/// (<c>MetadataBudgetDayUtc</c> + <c>MetadataBudgetUsed</c>), resets when the day
/// changes - at midnight server time since 1.32.0 (<see cref="DayStart"/>; 00:00 UTC
/// before) - and so survives restarts. Writes are serialized by
/// <see cref="MetadataGatewayState.StateLock"/> and done with a single UPDATE, so
/// concurrent requests can never overspend.
/// </summary>
public sealed class MetadataBudget
{
    private readonly MangaPixerDbContext _db;
    private readonly MetadataGatewayState _state;
    private readonly TimeProvider _time;

    public MetadataBudget(MangaPixerDbContext db, MetadataGatewayState state, TimeProvider time)
    {
        _db = db;
        _state = state;
        _time = time;
    }

    public sealed record Status(int Used, int Limit)
    {
        public bool Exhausted => Used >= Limit;
    }

    /// <summary>Today's usage and limit (no write).</summary>
    public async Task<Status> GetAsync(CancellationToken ct = default)
    {
        var row = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => new { s.MetadataBudgetDayUtc, s.MetadataBudgetUsed, s.MetadataDailyBudget })
            .FirstOrDefaultAsync(ct);
        var limit = row?.MetadataDailyBudget ?? MetadataSettingsService.DefaultDailyBudget;
        var used = IsToday(row?.MetadataBudgetDayUtc) ? row!.MetadataBudgetUsed : 0;
        return new Status(used, limit);
    }

    /// <summary>Takes one request from today's budget; false when it is spent.</summary>
    public async Task<bool> TryConsumeAsync(CancellationToken ct = default)
    {
        await _state.StateLock.WaitAsync(ct);
        try
        {
            var status = await GetAsync(ct);
            if (status.Exhausted)
                return false;
            var today = Today();
            var used = status.Used + 1;
            var updated = await _db.AppSettings
                .Where(s => s.Id == AppSettingsEntity.SingletonId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.MetadataBudgetDayUtc, today)
                    .SetProperty(x => x.MetadataBudgetUsed, used), ct);
            return updated > 0;
        }
        finally
        {
            _state.StateLock.Release();
        }
    }

    /// <summary>The start of the current budget day (the stored day key), in UTC.</summary>
    public DateTimeOffset Today() => DayStart(_time.GetUtcNow(), _time.LocalTimeZone);

    /// <summary>When the current budget day ends (the next day's start).</summary>
    public DateTimeOffset NextDay()
    {
        var now = _time.GetUtcNow();
        return DayStart(DayStart(now, _time.LocalTimeZone).AddHours(26), _time.LocalTimeZone);
    }

    /// <summary>
    /// True when a stored day key is the current budget day. A key written before 1.32.0 (midnight UTC of the current UTC day)
    /// still counts as today until the first write replaces it, so the switch to server time spends no extra budget.
    /// </summary>
    public bool IsToday(DateTimeOffset? stored) => IsToday(stored, _time.GetUtcNow(), _time.LocalTimeZone);

    /// <inheritdoc cref="IsToday(DateTimeOffset?)"/>
    public static bool IsToday(DateTimeOffset? stored, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        if (stored is not { } day)
            return false;
        if (day == DayStart(nowUtc, zone))
            return true;
        var utc = nowUtc.UtcDateTime;
        return LocalDay && day == new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>
    /// 1.32.0 (provisional decision Q3 = B1): the budget day and the refresh cap roll over at midnight SERVER time, like every
    /// other daily thing. false = 00:00 UTC (B2). The one switch for the day boundary.
    /// </summary>
    public static readonly bool LocalDay = true;

    /// <summary>The start of the budget day containing <paramref name="nowUtc"/>: local midnight in <paramref name="zone"/> (an hour later when a clock change skips it).</summary>
    public static DateTimeOffset DayStart(DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        if (!LocalDay)
        {
            var utc = nowUtc.UtcDateTime;
            return new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
        }
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime);
        return Core.Scheduling.JobSchedule.SlotOn(date, zone, 0);
    }
}
