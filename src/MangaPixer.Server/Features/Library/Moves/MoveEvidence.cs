namespace com.lifepixer.mangapixer.Server.Features.Library.Moves;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Shared evidence reads of cross-library move recognition (1.31.0), used by the scan (a move recognised while the destination
/// library is scanned) and by the pairing pass (a move recognised after the fact).
/// </summary>
public static class MoveEvidence
{
    public const int ScanRunning = 1;
    public const int Tombstoned = (int)CatalogNodeAvailability.Tombstoned;

    /// <summary>The oldest tombstone time still inside the move window (= the trash retention) at <paramref name="now"/>.</summary>
    public static async Task<DateTimeOffset> WindowStartAsync(MangaPixerDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var days = await db.AppSettings.AsNoTracking().Select(s => s.TrashRetentionDays).FirstOrDefaultAsync(ct);
        return TrashRetention.WindowStart(now, days);
    }

    /// <summary>A stored signature that may be used: present and describing the stored length.</summary>
    public static bool IsUsable(string? signature, long byteLength) =>
        !string.IsNullOrEmpty(signature) && ContentSignature.TryGetByteLength(signature) == byteLength;

    /// <summary>Libraries with a scan running now (a live lease); their nodes are left alone until that scan ends.</summary>
    public static IQueryable<long> RunningScanLibraryIds(MangaPixerDbContext db, DateTimeOffset now) =>
        db.ScanRuns.Where(r => r.Status == ScanRunning && r.LeaseExpiry > now).Select(r => r.LibraryId);

    /// <summary>A tombstoned node: its library, the last scan revision that saw it, and when it was tombstoned.</summary>
    public readonly record struct Sighting(long NodeId, long LibraryId, long LastSeenScanRevision, DateTimeOffset? TombstonedAt);

    /// <summary>
    /// When each tombstoned node was last seen present: the latest completion of a scan of its library at the revision that
    /// last observed it. Without such a run, the time it was tombstoned (later, so stricter: fewer pairings).
    /// </summary>
    public static async Task<Dictionary<long, DateTimeOffset>> LastSeenAtAsync(
        MangaPixerDbContext db, IReadOnlyCollection<Sighting> nodes, CancellationToken ct)
    {
        var result = new Dictionary<long, DateTimeOffset>(nodes.Count);
        if (nodes.Count == 0)
            return result;
        var libraries = nodes.Select(n => n.LibraryId).Distinct().ToList();
        var revisions = nodes.Select(n => n.LastSeenScanRevision).Distinct().ToList();
        var runs = new List<(long LibraryId, long Revision, DateTimeOffset At)>();
        foreach (var chunk in revisions.Chunk(500))
        {
            var rows = await db.ScanRuns.AsNoTracking()
                .Where(r => libraries.Contains(r.LibraryId) && chunk.Contains(r.ScanRevision))
                .Select(r => new { r.LibraryId, r.ScanRevision, r.StartedAt, r.CompletedAt })
                .ToListAsync(ct);
            runs.AddRange(rows.Select(r => (r.LibraryId, r.ScanRevision, r.CompletedAt ?? r.StartedAt)));
        }
        var latest = runs.GroupBy(r => (r.LibraryId, r.Revision)).ToDictionary(g => g.Key, g => g.Max(r => r.At));
        foreach (var n in nodes)
        {
            if (latest.TryGetValue((n.LibraryId, n.LastSeenScanRevision), out var at))
                result[n.NodeId] = at;
            else
                result[n.NodeId] = n.TombstonedAt ?? DateTimeOffset.MaxValue;
        }
        return result;
    }
}
