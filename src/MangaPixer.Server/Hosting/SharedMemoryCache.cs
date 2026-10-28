namespace com.lifepixer.mangapixer.Server.Hosting;

using Microsoft.Extensions.Caching.Memory;

/// <summary>
/// The size budget of the shared <see cref="IMemoryCache"/> (1.49.0 lane M). Before 1.49.0 the shared cache had no size limit: each
/// entry left only when its time was up, so a burst of distinct keys (volume views of many folders, completion answers of many levels,
/// provider search pages and candidate images while an admin identifies) stayed in memory for the whole of its life.
/// <para>
/// With <see cref="MemoryCacheOptions.SizeLimit"/> set, EVERY entry must carry a size or <see cref="IMemoryCache"/> throws - so every
/// write in this application goes through <see cref="SetSized{T}"/> (<c>Contracts/SharedMemoryCacheContractTests</c> pins it). The
/// unit is about one KiB of the entry's estimated memory (<see cref="Kib"/>); an estimate, not a measurement - it only has to keep the
/// sum in proportion. EF Core sizes its own entries (its compiled-query cache, 10 each) when it shares this cache.
/// </para>
/// <para>
/// The limit is a ceiling for bursts, far above what a normal day uses (measured in the lane M note), so it changes no answer and no
/// timing in normal use: when it is reached, the cache refuses the new entry and drops the oldest ~5 %, and a refused entry is
/// simply computed again on its next use, exactly as after its expiry.
/// </para>
/// </summary>
public static class SharedMemoryCache
{
    /// <summary>The shared cache's budget, in <see cref="Kib"/> units (about 256 MiB of entries).</summary>
    public const long SizeLimit = 256 * 1024;

    /// <summary>The size unit for <paramref name="bytes"/> estimated bytes: whole KiB, at least 1.</summary>
    public static long Kib(long bytes) => Math.Max(1, (bytes + 1023) / 1024);

    /// <summary>
    /// Stores <paramref name="value"/> under <paramref name="key"/> for <paramref name="lifetime"/> (absolute, from now) with
    /// <paramref name="size"/> units (<see cref="Kib"/>) - the only way this application writes the shared cache.
    /// </summary>
    /// <remarks>
    /// <paramref name="priority"/> orders what the cache drops first when the budget is reached: <see cref="CacheItemPriority.Normal"/>
    /// for answers computed again from the database, <see cref="CacheItemPriority.High"/> for ones that would cost a provider request,
    /// <see cref="CacheItemPriority.NeverRemove"/> for small entries whose loss a user would see (a candidate image token) - those leave
    /// only when their time is up.
    /// </remarks>
    public static T SetSized<T>(this IMemoryCache cache, object key, T value, TimeSpan lifetime, long size,
        CacheItemPriority priority = CacheItemPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        return cache.Set(key, value,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime, Size = size, Priority = priority });
    }
}
