namespace com.lifepixer.mangapixer.Server.Features.Metadata.Declared;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Bulk read of declared facts for automatic matching (1.28.0, the contract lane M consumes): positive-only
/// evidence an admin stated explicitly. Registered in DI; pure reads, no network.
/// </summary>
public interface IDeclaredFactsReader
{
    /// <summary>
    /// For every live FOLDER of the library (internal node id) that has at least one effective declared fact -
    /// its own, an ancestor's or the library's - the nearest-wins result with its sources. Folders with nothing
    /// declared above them are absent. Two queries per library, never one per node.
    /// </summary>
    Task<IReadOnlyDictionary<long, DeclaredFacts>> EffectiveForLibraryAsync(long libraryId, CancellationToken ct);
}

/// <summary>
/// <see cref="IDeclaredFactsReader"/> over <c>declared_facts</c>: one query for the library's fact rows and one
/// projected query for its live folder tree (id, parent), then a top-down pass. Facts on a tombstoned or
/// missing folder are ignored (the folder and everything below it is not in the result).
/// </summary>
public sealed class DeclaredFactsReader : IDeclaredFactsReader
{
    private readonly MangaPixerDbContext _db;

    public DeclaredFactsReader(MangaPixerDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<long, DeclaredFacts>> EffectiveForLibraryAsync(long libraryId, CancellationToken ct)
    {
        var rows = await DeclaredFactsResolution.LoadRowsAsync(_db, f => f.LibraryId == libraryId, ct);
        if (rows.Count == 0)
            return new Dictionary<long, DeclaredFacts>();

        var folder = (int)CatalogNodeKind.Folder;
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var parents = await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.LibraryId == libraryId && n.Kind == folder && n.Availability != tombstoned)
            .Select(n => new { n.Id, n.ParentId })
            .ToDictionaryAsync(n => n.Id, n => n.ParentId, ct);

        var own = DeclaredFactsResolution.GroupByScope(rows, out var library);
        var libraryFacts = DeclaredFactsResolution.AsSource(library, DeclaredFactSource.Library);
        var memo = new Dictionary<long, DeclaredFacts>(parents.Count);
        var path = new List<long>();

        foreach (var start in parents.Keys)
        {
            if (memo.ContainsKey(start))
                continue;
            // Climb to the first resolved ancestor (or the top), then resolve downwards.
            path.Clear();
            DeclaredFacts above = libraryFacts;
            long? current = start;
            while (current is { } id)
            {
                if (memo.TryGetValue(id, out var known)) { above = known; break; }
                if (!parents.TryGetValue(id, out var parent)) { path.Clear(); break; }
                path.Add(id);
                if (path.Count > parents.Count) { path.Clear(); break; } // defensive: a parent cycle
                current = parent;
            }
            for (var i = path.Count - 1; i >= 0; i--)
            {
                var id = path[i];
                above = DeclaredFactsResolution.Combine(own.GetValueOrDefault(id), above);
                memo[id] = above;
            }
        }

        return memo.Where(kv => !kv.Value.IsEmpty).ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}
