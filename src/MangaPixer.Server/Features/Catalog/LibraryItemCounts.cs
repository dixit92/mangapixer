namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using System.Collections.Concurrent;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// 1.46.0 (lane B, measured cost #8): the archive count of each library on the libraries list (<c>GET /api/v1/libraries</c>, asked on every
/// folder change, by the sidebar and by home), remembered per library catalog state instead of counted over all of <c>catalog_nodes</c> on
/// every call. The same number as before: archives (not tombstoned) of the library.
/// <para>
/// A count is reused only while nothing that changes it can have happened: the library's <c>CatalogRevision</c> (a scan bumps it when it
/// ends, a move pairing too), its latest scan run and its creation time are unchanged, and no scan of it is pending or running (a scan
/// writes nodes before it bumps the revision - while one runs, every call counts, as before). Nothing else adds, removes, tombstones or
/// revives an archive (the trash purge deletes only tombstoned ones). One instance per host (a singleton): a restored database is applied at
/// start-up, before any count.
/// </para>
/// </summary>
public sealed class LibraryItemCounts
{
    private readonly ConcurrentDictionary<long, Entry> _counts = new();

    private sealed record Entry(long Revision, long LastScanRun, DateTimeOffset CreatedAt, int Count);

    /// <summary>Calls answered from memory (tests).</summary>
    internal int Hits => _hits;

    private int _hits;

    /// <summary>The archive count of each library (every id asked for is in the answer).</summary>
    public async Task<Dictionary<long, int>> GetAsync(MangaPixerDbContext db,
        IReadOnlyCollection<(long Id, long CatalogRevision, DateTimeOffset CreatedAt)> libraries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(libraries);
        var result = new Dictionary<long, int>();
        if (libraries.Count == 0)
            return result;
        var ids = libraries.Select(l => l.Id).Distinct().ToList();

        // The latest scan run of each library, and whether one is pending (0) or running (1).
        var runs = await db.ScanRuns.AsNoTracking()
            .Where(s => ids.Contains(s.LibraryId))
            .GroupBy(s => s.LibraryId)
            .Select(g => new { LibraryId = g.Key, Last = g.Max(s => s.Id), Active = g.Sum(s => s.Status == 0 || s.Status == 1 ? 1 : 0) })
            .ToDictionaryAsync(r => r.LibraryId, ct);

        var missing = new List<(long Id, Entry Key, bool Cacheable)>();
        foreach (var library in libraries.DistinctBy(l => l.Id))
        {
            var run = runs.GetValueOrDefault(library.Id);
            var key = new Entry(library.CatalogRevision, run?.Last ?? 0, library.CreatedAt, 0);
            var cacheable = run is null || run.Active == 0;
            if (cacheable && _counts.TryGetValue(library.Id, out var known) && known with { Count = 0 } == key)
            {
                result[library.Id] = known.Count;
                Interlocked.Increment(ref _hits);
                continue;
            }
            missing.Add((library.Id, key, cacheable));
        }
        if (missing.Count == 0)
            return result;

        var missingIds = missing.Select(m => m.Id).ToList();
        var archive = (int)CatalogNodeKind.Archive;
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var counted = await db.CatalogNodes.AsNoTracking()
            .Where(n => missingIds.Contains(n.LibraryId) && n.Kind == archive && n.Availability != tombstoned)
            .GroupBy(n => n.LibraryId)
            .Select(g => new { LibraryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.LibraryId, x => x.Count, ct);
        foreach (var (id, key, cacheable) in missing)
        {
            var count = counted.GetValueOrDefault(id);
            result[id] = count;
            if (cacheable)
                _counts[id] = key with { Count = count };
            else
                _counts.TryRemove(id, out _);
        }
        return result;
    }
}
