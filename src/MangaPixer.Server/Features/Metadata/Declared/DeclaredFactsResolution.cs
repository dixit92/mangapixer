namespace com.lifepixer.mangapixer.Server.Features.Metadata.Declared;

using System.Linq.Expressions;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Nearest-wins resolution of declared facts, shared by the bulk reader and the per-node views. Per KEY: the
/// closest scope that declares the key wins - a folder's own row, else its parent's effective value, up to the
/// library scope. A creator list is one value: a closer non-empty list replaces a farther one (never merged).
/// </summary>
internal static class DeclaredFactsResolution
{
    /// <summary>One fact row as the resolution needs it (no timestamps).</summary>
    internal sealed record Row(long? NodeId, string Key, string Value, string? Role, int Position);

    /// <summary>The v1 keys (type, creator) matching <paramref name="scope"/>, ordered for display.</summary>
    internal static Task<List<Row>> LoadRowsAsync(MangaPixerDbContext db, Expression<Func<DeclaredFactEntity, bool>> scope, CancellationToken ct) =>
        db.DeclaredFacts.AsNoTracking()
            .Where(scope)
            .Where(f => f.Key == DeclaredFactKeys.Type || f.Key == DeclaredFactKeys.Creator)
            .OrderBy(f => f.Position).ThenBy(f => f.Id)
            .Select(f => new Row(f.NodeId, f.Key, f.Value, f.Role, f.Position))
            .ToListAsync(ct);

    /// <summary>Rows grouped into the facts declared ON each folder; the library scope's facts come out separately.</summary>
    internal static Dictionary<long, DeclaredFacts> GroupByScope(IEnumerable<Row> rows, out DeclaredFacts library)
    {
        var byNode = new Dictionary<long, DeclaredFacts>();
        library = DeclaredFacts.Empty;
        foreach (var group in rows.GroupBy(r => r.NodeId))
        {
            var facts = OwnFacts(group);
            if (group.Key is { } nodeId)
                byNode[nodeId] = facts;
            else
                library = facts;
        }
        return byNode;
    }

    /// <summary>The facts declared on one scope (sources left null - the caller knows the scope).</summary>
    internal static DeclaredFacts OwnFacts(IEnumerable<Row> rows)
    {
        string? type = null;
        var creators = new List<DeclaredCreator>();
        foreach (var r in rows)
        {
            if (r.Key == DeclaredFactKeys.Type)
                type ??= r.Value;
            else if (r.Key == DeclaredFactKeys.Creator)
                creators.Add(new DeclaredCreator(r.Value, r.Role));
        }
        return type is null && creators.Count == 0 ? DeclaredFacts.Empty : new DeclaredFacts(type, creators);
    }

    /// <summary>Marks every declared key of <paramref name="facts"/> with one source.</summary>
    internal static DeclaredFacts AsSource(DeclaredFacts facts, DeclaredFactSource source) => facts.IsEmpty
        ? DeclaredFacts.Empty
        : facts with
        {
            TypeSource = facts.Type is null ? null : source,
            CreatorsSource = facts.Creators.Count == 0 ? null : source,
        };

    /// <summary>
    /// A folder's effective facts from its own declaration and its parent's effective facts (or the library's,
    /// for a top-level folder): own keys are <see cref="DeclaredFactSource.Own"/>; keys from above keep
    /// <see cref="DeclaredFactSource.Library"/> or become <see cref="DeclaredFactSource.Inherited"/>.
    /// </summary>
    internal static DeclaredFacts Combine(DeclaredFacts? own, DeclaredFacts above)
    {
        own ??= DeclaredFacts.Empty;
        if (own.IsEmpty && above.IsEmpty)
            return DeclaredFacts.Empty;

        var ownType = own.Type is not null;
        var ownCreators = own.Creators.Count > 0;
        var type = ownType ? own.Type : above.Type;
        var creators = ownCreators ? own.Creators : above.Creators;
        return new DeclaredFacts(
            type,
            creators,
            type is null ? null : ownType ? DeclaredFactSource.Own : Downward(above.TypeSource),
            creators.Count == 0 ? null : ownCreators ? DeclaredFactSource.Own : Downward(above.CreatorsSource));
    }

    /// <summary>
    /// 1.39.0: the edition facts declared ON each of the given folders (keys <c>volumes</c>, <c>edition</c>, <c>tracking</c>) - own scope
    /// only, never inherited and never read by matching. Folders without any are absent; unreadable values are ignored. One query.
    /// </summary>
    internal static async Task<Dictionary<long, DeclaredEditionFacts>> LoadEditionAsync(
        MangaPixerDbContext db, IReadOnlyCollection<long> nodeIds, CancellationToken ct)
    {
        var result = new Dictionary<long, DeclaredEditionFacts>();
        if (nodeIds.Count == 0)
            return result;
        var ids = nodeIds as List<long> ?? nodeIds.ToList();
        var rows = await db.DeclaredFacts.AsNoTracking()
            .Where(f => f.NodeId != null && ids.Contains(f.NodeId.Value)
                && (f.Key == DeclaredFactKeys.Volumes || f.Key == DeclaredFactKeys.Edition || f.Key == DeclaredFactKeys.Tracking))
            .OrderBy(f => f.Id)
            .Select(f => new { NodeId = f.NodeId!.Value, f.Key, f.Value })
            .ToListAsync(ct);
        foreach (var group in rows.GroupBy(r => r.NodeId))
        {
            var facts = EditionFacts(group.Select(r => (r.Key, r.Value)));
            if (!facts.IsEmpty)
                result[group.Key] = facts;
        }
        return result;
    }

    /// <summary>The edition facts of one folder's rows (the first readable row per key wins).</summary>
    internal static DeclaredEditionFacts EditionFacts(IEnumerable<(string Key, string Value)> rows)
    {
        int? volumes = null;
        DeclaredEdition? edition = null;
        var trackingOff = false;
        foreach (var (key, value) in rows)
        {
            if (key == DeclaredFactKeys.Volumes)
                volumes ??= DeclaredFactKeys.ParseVolumeTotal(value);
            else if (key == DeclaredFactKeys.Edition)
                edition ??= DeclaredFactKeys.ParseEdition(value);
            else if (key == DeclaredFactKeys.Tracking)
                trackingOff |= value == DeclaredFactKeys.TrackingOff;
        }
        return new DeclaredEditionFacts(volumes, edition, trackingOff);
    }

    private static DeclaredFactSource? Downward(DeclaredFactSource? source) => source switch
    {
        null => null,
        DeclaredFactSource.Library => DeclaredFactSource.Library,
        _ => DeclaredFactSource.Inherited,
    };
}
