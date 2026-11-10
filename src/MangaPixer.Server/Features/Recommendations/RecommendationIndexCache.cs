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
    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(10);

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

    /// <summary>Reads the scope's tag rows and builds the index (four queries; ids rendered as integer literals, never user text).</summary>
    internal static async Task<RecommendationIndex> LoadAsync(MangaPixerDbContext db, FacetViewerScope scope, CancellationToken ct)
    {
        var shown = scope.SqlList;
        var entries = await QuerySql.ReadAsync(db, $"""
            SELECT e.WorkId, e.LocationNodeId, e.LibraryId, e.ValueId, v.Bucket
            FROM facet_entries e
            JOIN catalog_nodes n ON n.Id = e.LocationNodeId AND n.Availability != {QuerySql.Tombstoned}
            JOIN facet_values v ON v.Id = e.ValueId
            WHERE e.LibraryId IN ({shown})
            """, null, r => new RecommendationIndex.EntryRow(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt32(4)), ct);

        var locations = await QuerySql.ReadAsync(db, $"""
            SELECT DISTINCT e.WorkId, e.LocationNodeId, e.LibraryId, l.DisplayName, n.SortKey
            FROM facet_entries e
            JOIN catalog_nodes n ON n.Id = e.LocationNodeId AND n.Availability != {QuerySql.Tombstoned}
            JOIN libraries l ON l.Id = e.LibraryId
            WHERE e.LibraryId IN ({shown})
            """, null, r => new RecommendationIndex.LocationRow(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4)), ct);

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
