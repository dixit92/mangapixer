namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The persisted daily request budget (1.24.0, lane B2). Every outbound request
/// (search, get, image) counts one. The counter lives on <c>app_settings</c>
/// (<c>MetadataBudgetDayUtc</c> + <c>MetadataBudgetUsed</c>), resets when the UTC
/// day changes, and so survives restarts. Writes are serialized by
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
        var used = row?.MetadataBudgetDayUtc is { } day && day == Today() ? row.MetadataBudgetUsed : 0;
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

    /// <summary>Midnight UTC of the current day (the stored day key).</summary>
    public DateTimeOffset Today()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
    }
}
