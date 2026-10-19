namespace com.lifepixer.mangapixer.Server.Features.Metadata.Freshness;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The one-time readable backfill (1.45.0, lane R; MangaList's recommendation 8): MangaDex volume lists stored before 1.43.0 have the
/// released chapters but not which of them can be read (<c>ReadableChaptersJson</c>), so the export's <c>readable</c> stays empty until each
/// list's next regular read. Up to <see cref="PerDay"/> of them a day become due now (oldest read first) - the volume-cover pass re-reads
/// them with the requests it always sends, inside the daily budget and its pacing. Done (and never again) once none is left. No request here.
/// </summary>
public sealed class ReadableBackfill(MangaPixerDbContext db, TimeProvider time, ILogger<ReadableBackfill> logger)
{
    /// <summary>Lists made due per day (each costs the list's usual requests - about 4).</summary>
    public const int PerDay = 150;

    /// <summary>Makes the next lists due when a day has passed since the last batch; returns how many (0 when not due or done).</summary>
    public async Task<int> RunIfDueAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var peek = await NewChapterChecks.PeekAsync(db, ct);
        if (peek?.BackfillDoneAt is not null || (peek?.BackfillAt is { } last && now - last < TimeSpan.FromDays(1) && last <= now))
            return 0;
        var aggregate = (int)VolumeMapSource.MangaDexAggregate;
        var missing = db.SeriesVolumeMaps.Where(m => m.Source == aggregate && m.ReleasedLanguage != null && m.ReadableChaptersJson == null);
        var ids = await missing.Where(m => m.NextCheckAt == null || m.NextCheckAt > now)
            .OrderBy(m => m.FetchedAt).ThenBy(m => m.Id)
            .Select(m => m.Id).Take(PerDay).ToListAsync(ct);
        var made = ids.Count == 0 ? 0 : await db.SeriesVolumeMaps.Where(m => ids.Contains(m.Id)).ExecuteUpdateAsync(s => s.SetProperty(m => m.NextCheckAt, now), ct);
        var state = await NewChapterChecks.StateAsync(db, ct);
        state.BackfillAt = now;
        if (!await missing.AnyAsync(ct))
            state.BackfillDoneAt = now;
        await db.SaveChangesAsync(ct);
        if (made > 0 || state.BackfillDoneAt is not null)
            logger.LogInformation(LogEvents.Metadata.ReadableBackfill, "Readable backfill: {Lists} MangaDex volume lists due now{Done}", made,
                state.BackfillDoneAt is null ? string.Empty : ", done");
        return made;
    }
}
