namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

/// <summary>
/// The facet count cache (singleton, its own size-capped <see cref="IMemoryCache"/>). Key = (index generation, hash of the viewer's
/// shown library set, value id) - no user id and no unfiltered count anywhere, so a cached number is exactly the number any viewer
/// with the same shown set would compute (D1 2.5). Entries live 10 minutes; a generation bump makes older entries unreachable.
/// </summary>
public sealed class FacetCountCache : IDisposable
{
    /// <summary>The most counts kept (each entry has size 1).</summary>
    public const int SizeLimit = 50_000;

    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(10);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = SizeLimit });

    internal readonly record struct Key(long Generation, string ScopeHash, long ValueId);

    internal bool TryGet(Key key, out int count) => _cache.TryGetValue(key, out count);

    internal void Set(Key key, int count) =>
        _cache.Set(key, count, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeToLive });

    /// <summary>
    /// 1.49.0 (lane T): the key of a tag view's Refine counts = (index generation, the viewer's shown-set hash, a hash of the exact
    /// result-set statement). The statement already names the shown libraries and every resolved filter; a read filter enters it as the
    /// literal ids of the works it kept - so a cached answer is exactly what the same statement computes for any viewer, never a
    /// viewer-blind number.
    /// </summary>
    internal readonly record struct RefineKey(long Generation, string ScopeHash, string StatementHash);

    /// <summary>The cached Refine list (per bucket, the chips with their counts among the results).</summary>
    internal bool TryGet(RefineKey key, out IReadOnlyList<FacetBucketDto>? refine) => _cache.TryGetValue(key, out refine);

    /// <summary>Caches a Refine list; the entry's size is its chip count (at least 1), so a big list takes its share of the cap.</summary>
    internal void Set(RefineKey key, IReadOnlyList<FacetBucketDto> refine) =>
        _cache.Set(key, refine, new MemoryCacheEntryOptions
        {
            Size = Math.Max(1, refine.Sum(b => b.Chips.Count)),
            AbsoluteExpirationRelativeToNow = TimeToLive,
        });

    /// <summary>For tests: how many counts are held.</summary>
    public int Count => _cache.Count;

    public void Dispose() => _cache.Dispose();
}

/// <summary>
/// Per-viewer facet counts (1.45.0, lane Q; D1 2.5): for each value, how many DISTINCT works carry it at a location the viewer may see -
/// filtered in SQL BEFORE counting (<c>LibraryId IN (@shown)</c>, tombstoned locations left out). A value with no visible entry counts
/// 0, exactly like an unknown value.
/// </summary>
public sealed class FacetCountService
{
    private readonly MangaPixerDbContext _db;
    private readonly FacetCountCache _cache;
    private readonly IFacetIndexGeneration _generation;
    private readonly ILogger<FacetCountService> _logger;

    public FacetCountService(MangaPixerDbContext db, FacetCountCache cache, IFacetIndexGeneration generation, ILogger<FacetCountService> logger)
    {
        _db = db;
        _cache = cache;
        _generation = generation;
        _logger = logger;
    }

    /// <summary>The count of every value in <paramref name="valueIds"/> for <paramref name="scope"/> (0 when none is visible).</summary>
    public async Task<IReadOnlyDictionary<long, int>> CountAsync(FacetViewerScope scope, IReadOnlyCollection<long> valueIds, CancellationToken ct = default)
    {
        var result = new Dictionary<long, int>(valueIds.Count);
        if (valueIds.Count == 0)
            return result;
        if (scope.IsEmpty)
        {
            foreach (var id in valueIds)
                result[id] = 0;
            return result;
        }

        // Read the generation BEFORE querying: a count computed while a write commits lands under the older key.
        var generation = _generation.Current;
        var missing = new List<long>();
        foreach (var id in valueIds.Distinct())
        {
            if (_cache.TryGet(new FacetCountCache.Key(generation, scope.Hash, id), out var cached))
                result[id] = cached;
            else
                missing.Add(id);
        }

        if (missing.Count > 0)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var chunk in missing.Chunk(QuerySql.Chunk))
            {
                var counted = await QuerySql.ReadAsync(_db, $"""
                    SELECT e.ValueId, COUNT(DISTINCT e.WorkId)
                    FROM facet_entries e
                    JOIN catalog_nodes n ON n.Id = e.LocationNodeId AND n.Availability != {QuerySql.Tombstoned}
                    WHERE e.ValueId IN ({QuerySql.List(chunk)})
                    AND e.LibraryId IN ({scope.SqlList})
                    GROUP BY e.ValueId
                    """, null, r => (ValueId: r.GetInt64(0), Count: r.GetInt32(1)), ct);
                var byValue = counted.ToDictionary(c => c.ValueId, c => c.Count);
                foreach (var id in chunk)
                {
                    var count = byValue.GetValueOrDefault(id);
                    result[id] = count;
                    _cache.Set(new FacetCountCache.Key(generation, scope.Hash, id), count);
                }
            }
            _logger.LogDebug(LogEvents.Metadata.FacetCountsComputed,
                "Facet counts: {Values} values, {Computed} computed, {Libraries} libraries shown, in {ElapsedMs} ms",
                result.Count, missing.Count, scope.Shown.Count,
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        return result;
    }
}
