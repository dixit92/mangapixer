namespace com.lifepixer.mangapixer.Server.Features.Home;

using System.Collections.Concurrent;
using com.lifepixer.mangapixer.Server.Features.Covers;

/// <summary>
/// 1.47.0 lane S (owner decision 12): the home "New chapters" row's stack covers, kept in memory per stack node. A cover depends only on
/// its node (<see cref="CoverResolutionService"/> reads no viewer), so one kept URL serves every viewer whose stacks show that node; which
/// stacks a viewer sees (grants, Private + Incognito, home-excluded libraries, read-state filter, cap) is decided before the covers, per
/// request, exactly as before.
/// <para>
/// A kept cover serves a later request only while ALL of these are unchanged: the cover-layer version (<see cref="CoverLayerVersion"/> -
/// every committed write to a cover input moves it), the node's library stamp (<see cref="HomeStackStamp"/>: catalog revision, creation
/// time, latest scan run, linked folders; nothing is kept or served while a scan of the library is pending or running), and its age is
/// under <see cref="MaxAge"/>. The version and the stamps are read before any cover row, so a kept cover is never older than its key.
/// Bounded: past <see cref="Capacity"/> nodes the cache starts over. No migration, nothing persisted.
/// </para>
/// </summary>
public sealed class HomeCoverCache
{
    /// <summary>The longest a cover serves with unchanged inputs (the bound <see cref="HomeStackCache"/> uses).</summary>
    public static readonly TimeSpan MaxAge = HomeStackCache.MaxAge;

    /// <summary>The most stack nodes kept (a home shows at most 50 per library; this is many homes of many libraries).</summary>
    public const int Capacity = 4096;

    private sealed record Entry(string? Url, long LayerVersion, HomeStackStamp Stamp, DateTimeOffset BuiltAt);

    private readonly ConcurrentDictionary<(long NodeId, bool IsFolder), Entry> _entries = new();
    private int _hits;
    private int _misses;

    /// <summary>Covers answered from the cache (tests, measurement).</summary>
    internal int Hits => Volatile.Read(ref _hits);

    /// <summary>Covers resolved because none was kept or the kept one no longer applied (tests, measurement).</summary>
    internal int Misses => Volatile.Read(ref _misses);

    /// <summary>
    /// The covers of <paramref name="targets"/> (each with its library's stamp): kept ones that still apply, the rest from
    /// <paramref name="resolve"/> in ONE call, then kept. Targets without a cover are absent from the answer, as from the resolver.
    /// <paramref name="layerVersion"/> is <see cref="CoverLayerVersion.Of"/> read before any cover row.
    /// </summary>
    internal async Task<IReadOnlyDictionary<long, string>> ResolveUrlsAsync(
        IReadOnlyList<(CoverTarget Target, HomeStackStamp Stamp)> targets,
        long layerVersion,
        DateTimeOffset now,
        Func<IReadOnlyCollection<CoverTarget>, CancellationToken, Task<IReadOnlyDictionary<long, string>>> resolve,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(resolve);
        var result = new Dictionary<long, string>(targets.Count);
        var misses = new List<(CoverTarget Target, HomeStackStamp Stamp)>();
        foreach (var (target, stamp) in targets)
        {
            if (!stamp.ScanActive && _entries.TryGetValue((target.NodeId, target.IsFolder), out var entry)
                && entry.LayerVersion == layerVersion && entry.Stamp.SameInputs(stamp) && now >= entry.BuiltAt && now - entry.BuiltAt < MaxAge)
            {
                Interlocked.Increment(ref _hits);
                if (entry.Url is not null)
                    result[target.NodeId] = entry.Url;
                continue;
            }
            misses.Add((target, stamp));
        }
        if (misses.Count == 0)
            return result;

        Interlocked.Add(ref _misses, misses.Count);
        var resolved = await resolve(misses.Select(m => m.Target).ToList(), ct);
        if (_entries.Count + misses.Count > Capacity)
            _entries.Clear();
        foreach (var (target, stamp) in misses)
        {
            var url = resolved.TryGetValue(target.NodeId, out var u) ? u : null;
            if (url is not null)
                result[target.NodeId] = url;
            if (stamp.ScanActive)
                _entries.TryRemove((target.NodeId, target.IsFolder), out _);
            else
                _entries[(target.NodeId, target.IsFolder)] = new Entry(url, layerVersion, stamp, now);
        }
        return result;
    }
}

/// <summary>DI registration of lane S's home cover cache (one call from <c>Program</c>).</summary>
public static class HomeCoverCacheServices
{
    public static IServiceCollection AddHomeCoverCache(this IServiceCollection services)
    {
        services.AddSingleton<HomeCoverCache>();
        return services;
    }
}
