namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// 1.43.0 (owner answer 3): the instance remembers that a tracker refused start / finish dates, across restarts. Once remembered, no dates
/// are sent to that tracker again; nothing in the app clears it (it stays for good - the owner's choice between "an admin action" and "for
/// good"; removing the <c>job_runs</c> row by hand would reset it).
/// </summary>
public interface ITrackerDatesMemory
{
    /// <summary>True when <paramref name="tracker"/> refused dates on this instance before.</summary>
    Task<bool> IsRefusedAsync(string tracker, CancellationToken ct);

    /// <summary>Remembers the refusal (idempotent).</summary>
    Task RememberRefusedAsync(string tracker, CancellationToken ct);
}

/// <summary>
/// <see cref="ITrackerDatesMemory"/> in the existing <c>job_runs</c> table (no migration): one row per tracker with the fixed key
/// <c>tracker-dates-refused-&lt;slug&gt;</c>, outcome <c>refused</c>. A singleton that opens its own scope per call.
/// </summary>
public sealed class JobRunTrackerDatesMemory(IServiceScopeFactory scopes, TimeProvider time) : ITrackerDatesMemory
{
    public const string KeyPrefix = "tracker-dates-refused-";
    public const string Outcome = "refused";

    public static string KeyOf(string tracker) => KeyPrefix + tracker;

    public async Task<bool> IsRefusedAsync(string tracker, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var key = KeyOf(tracker);
        return await db.JobRuns.AsNoTracking().AnyAsync(r => r.Key == key && r.LastOutcome == Outcome, ct);
    }

    public async Task RememberRefusedAsync(string tracker, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var key = KeyOf(tracker);
        var row = await db.JobRuns.FirstOrDefaultAsync(r => r.Key == key, ct);
        if (row is null)
        {
            row = new JobRunEntity { Key = key };
            db.JobRuns.Add(row);
        }
        else if (row.LastOutcome == Outcome)
        {
            return;
        }
        var now = time.GetUtcNow();
        row.LastStartedAt = now;
        row.LastFinishedAt = now;
        row.LastOutcome = Outcome;
        row.LastDetail = "start / finish dates refused";
        await db.SaveChangesAsync(ct);
    }
}
