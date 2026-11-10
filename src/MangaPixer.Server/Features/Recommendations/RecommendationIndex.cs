namespace com.lifepixer.mangapixer.Server.Features.Recommendations;

using com.lifepixer.mangapixer.Core.Metadata.Facets;

/// <summary>
/// The recommender's view of ONE viewer scope (1.53.0 lane C): every work with a visible, non-tombstoned location in the scope's libraries,
/// as a set of weighted tag values, plus the relations between their linked records. VIEWER-INDEPENDENT - it is built from the tag index
/// filtered to the scope's libraries only, so any two viewers with the same scope (<c>FacetViewerScope.Hash</c>) read the same index; no
/// user's signal is in it. Immutable once built; cached per (tag index generation, scope hash) by <see cref="RecommendationIndexCache"/>.
/// <para>
/// A work's vector is binary per value (a work has a tag or not) times the value's weight: the bucket weight
/// (<see cref="BucketWeights"/>) times its rarity <c>idf = ln(N / df)</c>, N = the works in the scope, df = the works in the scope that
/// carry the value. A value every work carries weighs 0; values of a zero-weight bucket (Series, Year, Custom) are dropped.
/// </para>
/// </summary>
public sealed class RecommendationIndex
{
    /// <summary>
    /// The bucket weights (owner-approved design, lane C kept them after the fixture runs): a shared creator counts most, then tags, then
    /// genres / magazine / "fan works of"; type and publisher are weak hints; series, year and custom tags not at all.
    /// </summary>
    public static double BucketWeight(FacetBucket bucket) => bucket switch
    {
        FacetBucket.Creators => 3.0,
        FacetBucket.Tags => 1.5,
        FacetBucket.Genres => 1.0,
        FacetBucket.Magazine => 1.0,
        FacetBucket.About => 1.0,
        FacetBucket.Type => 0.5,
        FacetBucket.Publisher => 0.3,
        _ => 0.0,
    };

    /// <summary>One location of a work: the folder or archive it lives at, in a library of the scope.</summary>
    public sealed record Location(long NodeId, long LibraryId);

    /// <summary>
    /// A relation edge stored under a record R: <see cref="OtherWork"/> (an index of <see cref="WorkIds"/>) and R are related by
    /// <see cref="Relation"/> - R's record lists the other one (<see cref="RecordListsOther"/>), or the other one lists R.
    /// </summary>
    public sealed record Edge(int OtherWork, string Relation, bool RecordListsOther);

    /// <summary>A raw tag-index row of the scope: a value at a location of a work.</summary>
    public readonly record struct EntryRow(long WorkId, long LocationNodeId, long LibraryId, long ValueId, int Bucket);

    /// <summary>A location of a work in sort order: the library's display name, then the node's sort key, then its id.</summary>
    public readonly record struct LocationRow(long WorkId, long NodeId, long LibraryId, string LibraryName, string SortKey);

    /// <summary>A work's linked record (works of kind Record), with the related records its stored relations name (already resolved).</summary>
    public readonly record struct RecordRow(long WorkId, long RecordId, IReadOnlyList<(long RecordId, string Relation)> Related);

    private RecommendationIndex() { }

    /// <summary>The works, by index (sorted by work id, so the order is deterministic).</summary>
    public long[] WorkIds { get; private init; } = [];

    /// <summary>Each work's linked record id, or null.</summary>
    public long?[] RecordIds { get; private init; } = [];

    /// <summary>Each work's visible locations, best first.</summary>
    public Location[][] Locations { get; private init; } = [];

    /// <summary>Each work's weighted values (indices of <see cref="ValueIds"/>), ascending.</summary>
    public int[][] WorkValues { get; private init; } = [];

    /// <summary>Each work's vector length (0 when it has no weighted value).</summary>
    public double[] WorkNorms { get; private init; } = [];

    /// <summary>The weighted values, by index (sorted by value id).</summary>
    public long[] ValueIds { get; private init; } = [];

    public FacetBucket[] ValueBuckets { get; private init; } = [];

    /// <summary>Bucket weight x idf of each value (always &gt; 0).</summary>
    public double[] ValueWeights { get; private init; } = [];

    /// <summary>The works carrying each value (indices of <see cref="WorkIds"/>), ascending.</summary>
    public int[][] ValueWorks { get; private init; } = [];

    /// <summary>Works in the scope (N of the idf), including works whose values all weigh 0.</summary>
    public int ScopeWorkCount { get; private init; }

    /// <summary>The raw entries the index was built from (for the cache's size estimate).</summary>
    public int EntryCount { get; private init; }

    private Dictionary<long, int> WorkById { get; init; } = [];
    private Dictionary<long, int> ValueById { get; init; } = [];
    private Dictionary<long, int[]> WorksAt { get; init; } = [];
    private Dictionary<long, Edge[]> EdgesByRecord { get; init; } = [];

    public int WorkCount => WorkIds.Length;

    public bool TryWork(long workId, out int work) => WorkById.TryGetValue(workId, out work);

    public bool TryValue(long valueId, out int value) => ValueById.TryGetValue(valueId, out value);

    /// <summary>The works located exactly at a node (none when the node is no location in the scope).</summary>
    public IReadOnlyList<int> WorksAtNode(long nodeId) => WorksAt.TryGetValue(nodeId, out var works) ? works : [];

    /// <summary>Whether a node is a location of some work in the scope.</summary>
    public bool IsLocation(long nodeId) => WorksAt.ContainsKey(nodeId);

    /// <summary>The relation edges stored under a record (both directions; the other side is always a work of this scope).</summary>
    public IReadOnlyList<Edge> EdgesOf(long recordId) => EdgesByRecord.TryGetValue(recordId, out var edges) ? edges : [];

    /// <summary>An empty index (an empty scope).</summary>
    public static readonly RecommendationIndex Empty = Build([], [], []);

    /// <summary>
    /// Builds the index. <paramref name="entries"/> are every entry of the scope (visible libraries, non-tombstoned locations);
    /// <paramref name="locations"/> name each location's sort position; <paramref name="records"/> carry the linked records' relations.
    /// Deterministic: the same rows in any order give the same index.
    /// </summary>
    public static RecommendationIndex Build(
        IEnumerable<EntryRow> entries, IEnumerable<LocationRow> locations, IEnumerable<RecordRow> records)
    {
        var entryList = entries.ToList();
        var workIds = entryList.Select(e => e.WorkId).Distinct().Order().ToArray();
        var workById = new Dictionary<long, int>(workIds.Length);
        for (var i = 0; i < workIds.Length; i++)
            workById[workIds[i]] = i;
        var n = workIds.Length;

        // df per value: the works carrying it (a work at two locations counts once).
        var worksByValue = new Dictionary<long, SortedSet<int>>();
        var bucketByValue = new Dictionary<long, int>();
        foreach (var e in entryList)
        {
            if (RecommendationIndex.BucketWeight((FacetBucket)e.Bucket) <= 0)
                continue;
            if (!worksByValue.TryGetValue(e.ValueId, out var set))
            {
                worksByValue[e.ValueId] = set = [];
                bucketByValue[e.ValueId] = e.Bucket;
            }
            set.Add(workById[e.WorkId]);
        }

        var weighted = worksByValue
            .Select(kv => (ValueId: kv.Key, Works: kv.Value, Bucket: (FacetBucket)bucketByValue[kv.Key],
                Weight: BucketWeight((FacetBucket)bucketByValue[kv.Key]) * Math.Log((double)n / kv.Value.Count)))
            .Where(v => v.Weight > 1e-12)
            .OrderBy(v => v.ValueId)
            .ToList();

        var valueIds = weighted.Select(v => v.ValueId).ToArray();
        var valueById = new Dictionary<long, int>(valueIds.Length);
        for (var v = 0; v < valueIds.Length; v++)
            valueById[valueIds[v]] = v;
        var valueWorks = weighted.Select(v => v.Works.ToArray()).ToArray();
        var valueWeights = weighted.Select(v => v.Weight).ToArray();

        var workValues = new List<int>[n];
        for (var i = 0; i < n; i++)
            workValues[i] = [];
        for (var v = 0; v < valueWorks.Length; v++)
        {
            foreach (var w in valueWorks[v])
                workValues[w].Add(v);
        }
        var norms = new double[n];
        for (var i = 0; i < n; i++)
            norms[i] = Math.Sqrt(workValues[i].Sum(v => valueWeights[v] * valueWeights[v]));

        // Locations, best first; works per location node.
        var locationsByWork = new List<LocationRow>[n];
        for (var i = 0; i < n; i++)
            locationsByWork[i] = [];
        var worksAt = new Dictionary<long, SortedSet<int>>();
        foreach (var row in locations.DistinctBy(l => (l.WorkId, l.NodeId)))
        {
            if (!workById.TryGetValue(row.WorkId, out var w))
                continue;
            locationsByWork[w].Add(row);
            if (!worksAt.TryGetValue(row.NodeId, out var set))
                worksAt[row.NodeId] = set = [];
            set.Add(w);
        }
        var sortedLocations = locationsByWork
            .Select(list => list
                .OrderBy(l => l.LibraryName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(l => l.SortKey, StringComparer.Ordinal)
                .ThenBy(l => l.NodeId)
                .Select(l => new Location(l.NodeId, l.LibraryId))
                .ToArray())
            .ToArray();

        // Records and relation edges (both directions); the "other" side must be a work of this scope.
        var recordIds = new long?[n];
        var workByRecord = new Dictionary<long, int>();
        var recordList = records.Where(r => workById.ContainsKey(r.WorkId)).OrderBy(r => r.WorkId).ToList();
        foreach (var r in recordList)
        {
            var w = workById[r.WorkId];
            recordIds[w] = r.RecordId;
            workByRecord.TryAdd(r.RecordId, w);
        }
        var edges = new Dictionary<long, List<Edge>>();
        void AddEdge(long record, Edge edge)
        {
            if (!edges.TryGetValue(record, out var list))
                edges[record] = list = [];
            if (!list.Contains(edge))
                list.Add(edge);
        }
        foreach (var r in recordList)
        {
            var w = workById[r.WorkId];
            foreach (var (target, relation) in r.Related.OrderBy(x => x.RecordId).ThenBy(x => x.Relation, StringComparer.Ordinal))
            {
                if (target == r.RecordId)
                    continue;
                if (workByRecord.TryGetValue(target, out var other) && other != w)
                    AddEdge(r.RecordId, new Edge(other, relation, RecordListsOther: true));
                AddEdge(target, new Edge(w, relation, RecordListsOther: false));
            }
        }

        return new RecommendationIndex
        {
            WorkIds = workIds,
            RecordIds = recordIds,
            Locations = sortedLocations,
            WorkValues = workValues.Select(l => l.ToArray()).ToArray(),
            WorkNorms = norms,
            ValueIds = valueIds,
            ValueBuckets = weighted.Select(v => v.Bucket).ToArray(),
            ValueWeights = valueWeights,
            ValueWorks = valueWorks,
            ScopeWorkCount = n,
            EntryCount = entryList.Count,
            WorkById = workById,
            ValueById = valueById,
            WorksAt = worksAt.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
            EdgesByRecord = edges.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
        };
    }

    /// <summary>A rough size of the index in bytes, for the shared cache's budget.</summary>
    public long EstimatedBytes =>
        (long)EntryCount * 24 + (long)WorkCount * 96 + (long)ValueIds.Length * 48 + EdgesByRecord.Sum(kv => 32L + kv.Value.Length * 40L);
}
