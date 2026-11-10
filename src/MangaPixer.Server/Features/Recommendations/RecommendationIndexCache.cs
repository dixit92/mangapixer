namespace com.lifepixer.mangapixer.Server.Features.Recommendations;

using System.Diagnostics;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

/// <summary>
/// Loads and caches the <see cref="RecommendationIndex"/> of a viewer scope (1.53.0 lane C; singleton). The key is (tag index generation,
/// scope hash) - no user id, no user signal: two viewers share an entry only when they see exactly the same libraries, and the index holds
/// nothing but those libraries' tag rows. Access is resolved per request BEFORE the cache (the caller passes the viewer's
/// <see cref="FacetViewerScope"/>). Entries live <see cref="TimeToLive"/>; a tag index write bumps the generation, so a newer key is used at
/// once. Written only through <see cref="SharedMemoryCache.SetSized"/>.
/// </summary>
public sealed class RecommendationIndexCache
{
    /// <summary>
    /// How long an index stays without a tag index write: 30 minutes - the generation already retires it at the next write, so the time
    /// only bounds memory, and a rebuild is the costly step (about 0.1 s for a live-size library).
    /// </summary>
    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(30);

    private readonly IMemoryCache _cache;
    private readonly ILogger<RecommendationIndexCache> _logger;

    public RecommendationIndexCache(IMemoryCache cache, ILogger<RecommendationIndexCache> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    private sealed record Key(long Generation, string ScopeHash);

    /// <summary>The index of <paramref name="scope"/> at <paramref name="generation"/> (read by the caller BEFORE it queries anything).</summary>
    public async Task<RecommendationIndex> GetAsync(MangaPixerDbContext db, FacetViewerScope scope, long generation, CancellationToken ct)
    {
        if (scope.IsEmpty)
            return RecommendationIndex.Empty;
        var key = new Key(generation, scope.Hash);
        if (_cache.TryGetValue(key, out RecommendationIndex? cached) && cached is not null)
            return cached;

        var started = Stopwatch.GetTimestamp();
        var index = await LoadAsync(db, scope, ct);
        _cache.SetSized(key, index, TimeToLive, SharedMemoryCache.Kib(index.EstimatedBytes));
        _logger.LogDebug(LogEvents.Recommendations.IndexBuilt,
            "Recommendation index built: {Works} works, {Values} values, {Entries} entries, in {ElapsedMs} ms",
            index.WorkCount, index.ValueIds.Length, index.EntryCount, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return index;
    }

    /// <summary>Reads the scope's tag rows and builds the index (a handful of queries; ids rendered as integer literals, never user text).</summary>
    internal static async Task<RecommendationIndex> LoadAsync(MangaPixerDbContext db, FacetViewerScope scope, CancellationToken ct)
    {
        var shown = scope.SqlList;
        // The scope's entries without joins (one pass over facet_entries), then the value buckets and the tombstoned nodes of the scope as
        // two small lookups: an entry at a tombstoned location shows nothing (as everywhere in the tag query).
        var raw = await QuerySql.ReadAsync(db, $"""
            SELECT WorkId, LocationNodeId, LibraryId, ValueId FROM facet_entries WHERE LibraryId IN ({shown})
            """, null, r => (WorkId: r.GetInt64(0), LocationNodeId: r.GetInt64(1), LibraryId: r.GetInt64(2), ValueId: r.GetInt64(3)), ct);
        var buckets = new Dictionary<long, int>();
        foreach (var v in await QuerySql.ReadAsync(db, "SELECT Id, Bucket FROM facet_values", null, r => (Id: r.GetInt64(0), Bucket: r.GetInt32(1)), ct))
            buckets[v.Id] = v.Bucket;
        var tombstoned = (await QuerySql.ReadAsync(db, $"""
            SELECT Id FROM catalog_nodes WHERE Availability = {QuerySql.Tombstoned} AND LibraryId IN ({shown})
            """, null, r => r.GetInt64(0), ct)).ToHashSet();
        var entries = new List<RecommendationIndex.EntryRow>(raw.Count);
        foreach (var e in raw)
        {
            if (!tombstoned.Contains(e.LocationNodeId) && buckets.TryGetValue(e.ValueId, out var bucket))
                entries.Add(new RecommendationIndex.EntryRow(e.WorkId, e.LocationNodeId, e.LibraryId, e.ValueId, bucket));
        }

        // Each location's sort position (library name, then the node's sort key): one primary-key read per chunk of location nodes.
        var libraryNames = await db.Libraries.AsNoTracking().Where(l => scope.Shown.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => l.DisplayName, ct);
        var sortKeys = new Dictionary<long, string>();
        foreach (var chunk in entries.Select(e => e.LocationNodeId).Distinct().Chunk(QuerySql.Chunk))
        {
            var rows = await QuerySql.ReadAsync(db, $"SELECT Id, SortKey FROM catalog_nodes WHERE Id IN ({QuerySql.List(chunk)})",
                null, r => (Id: r.GetInt64(0), SortKey: r.GetString(1)), ct);
            foreach (var row in rows)
                sortKeys[row.Id] = row.SortKey;
        }
        var locations = entries.Select(e => (e.WorkId, e.LocationNodeId, e.LibraryId)).Distinct()
            .Select(l => new RecommendationIndex.LocationRow(l.WorkId, l.LocationNodeId, l.LibraryId,
                libraryNames.GetValueOrDefault(l.LibraryId) ?? string.Empty, sortKeys.GetValueOrDefault(l.LocationNodeId) ?? string.Empty))
            .ToList();

        // The linked records of the scope's works, with their stored relations (the first reader of RelationsJson): each relation names a
        // record of the same provider by its external id, mapped to this server's record id when it is stored here.
        var linked = await QuerySql.ReadAsync(db, $"""
            SELECT w.Id, r.Id, r.Provider, r.RelationsJson
            FROM facet_works w
            JOIN metadata_records r ON r.Id = w.RecordId
            WHERE w.RecordId IS NOT NULL
            AND EXISTS (SELECT 1 FROM facet_entries e WHERE e.WorkId = w.Id AND e.LibraryId IN ({shown}))
            """, null, r => (WorkId: r.GetInt64(0), RecordId: r.GetInt64(1), Provider: r.GetString(2), Relations: r.IsDBNull(3) ? null : r.GetString(3)), ct);

        var related = linked
            .Select(l => (l.WorkId, l.RecordId, l.Provider, Items: MetadataJson.ReadList<MetadataJson.RelatedRecord>(l.Relations)))
            .ToList();
        var wanted = related.SelectMany(l => l.Items.Select(i => i.ExternalId)).Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
        var recordByExternal = new Dictionary<(string Provider, string ExternalId), long>();
        if (wanted.Count > 0)
        {
            var all = await db.MetadataRecords.AsNoTracking()
                .Select(r => new { r.Id, r.Provider, r.ExternalId })
                .ToListAsync(ct);
            foreach (var r in all.Where(r => wanted.Contains(r.ExternalId)).OrderBy(r => r.Id))
                recordByExternal.TryAdd((r.Provider, r.ExternalId), r.Id);
        }
        var records = related.Select(l => new RecommendationIndex.RecordRow(l.WorkId, l.RecordId,
            l.Items
                .Where(i => !string.IsNullOrEmpty(i.ExternalId) && recordByExternal.ContainsKey((l.Provider, i.ExternalId)))
                .Select(i => (recordByExternal[(l.Provider, i.ExternalId)], string.IsNullOrWhiteSpace(i.Relation) ? "related" : i.Relation))
                .ToList()));

        return RecommendationIndex.Build(entries, locations, records);
    }
}
