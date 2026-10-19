namespace com.lifepixer.mangapixer.Server.Features.Metadata.Freshness;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The rhythm of the new-chapter checks (1.45.0, lane R - MangaList's new-chapter detection, recommendations 1, 2, 7 and 8):
/// <list type="bullet">
/// <item>the MangaDex latest-upload sweep (<see cref="LatestUploadSweep"/>) every <see cref="HoursOf"/> hours - the admin's "Check for new
/// chapters" (1-6 h, default 6);</item>
/// <item>MangaUpdates' releases feed (<see cref="ReleasesFeedPoll"/>) every <see cref="FeedInterval"/>;</item>
/// <item>a refresh pass of the records that are due between the daily ones (the admin's hour stays the daily pass) every
/// <see cref="DuePassInterval"/>, and right after the feed made records due;</item>
/// <item>the one-time readable backfill (<see cref="ReadableBackfill"/>), at most <see cref="ReadableBackfill.PerDay"/> lists a day.</item>
/// </list>
/// All of it is automatic work: the automatic gate (both consents, the switches, the allowlist, the backoff, the one daily budget, one
/// request a second) applies to every request.
/// </summary>
public static class NewChapterChecks
{
    public const int DefaultHours = 6;
    public const int MinHours = 1;
    public const int MaxHours = 6;

    public static IReadOnlyList<int> AllowedHours { get; } = [1, 2, 3, 4, 5, 6];

    /// <summary>A sweep or poll that stopped (budget, backoff, a switch, a provider error) is tried again after this.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);

    /// <summary>MangaUpdates' releases feed is read this often (four times a day).</summary>
    public static readonly TimeSpan FeedInterval = TimeSpan.FromHours(6);

    /// <summary>The refresh of due records runs this often between the daily passes.</summary>
    public static readonly TimeSpan DuePassInterval = TimeSpan.FromHours(4);

    /// <summary>The admin's sweep interval in hours: the stored value when it is 1-6, else <see cref="DefaultHours"/>.</summary>
    public static int HoursOf(int? stored) => stored is >= MinHours and <= MaxHours ? stored.Value : DefaultHours;

    /// <summary>The state row (tracked), created on first use.</summary>
    public static async Task<FreshnessStateEntity> StateAsync(MangaPixerDbContext db, CancellationToken ct)
    {
        var row = await db.FreshnessState.FirstOrDefaultAsync(s => s.Id == FreshnessStateEntity.SingletonId, ct);
        if (row is not null)
            return row;
        row = new FreshnessStateEntity();
        db.FreshnessState.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>The state row without tracking, or null before the first run.</summary>
    public static Task<FreshnessStateEntity?> PeekAsync(MangaPixerDbContext db, CancellationToken ct) =>
        db.FreshnessState.AsNoTracking().FirstOrDefaultAsync(s => s.Id == FreshnessStateEntity.SingletonId, ct);

    /// <summary>True when a job last started at <paramref name="started"/> with <paramref name="outcome"/> is due again at <paramref name="now"/>.</summary>
    public static bool IsDue(DateTimeOffset? started, string? outcome, TimeSpan interval, DateTimeOffset now) =>
        started is not { } at || now - at >= (outcome == "ok" ? interval : RetryAfter) || at > now;
}

/// <summary>Why a record was made due now (<c>metadata_refresh_nudges.Reason</c>).</summary>
public enum MetadataRefreshNudgeReason
{
    /// <summary>MangaUpdates' releases feed listed a release of the series.</summary>
    ReleasesFeed = 1,
}
