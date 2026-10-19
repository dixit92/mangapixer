namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets;

/// <summary>
/// The tag index's write generation (1.45.0, lane Q; D1 2.5): every cached facet count is keyed by it, so a write makes every older
/// count unreachable at once (stale entries simply age out of the cache).
/// <para>
/// Contract for the index WRITER (lane F's builder, an admin creator merge / split, a purge): call <see cref="Bump"/> AFTER each
/// committed transaction that inserts, updates or deletes rows of <c>facet_entries</c>, <c>facet_values</c>, <c>facet_works</c> or
/// <c>creators</c> / <c>creator_names</c> (once per transaction is enough). Readers read <see cref="Current"/> BEFORE they query, so a
/// count computed while a write commits is stored under the older generation and never served after the bump. Bump is cheap (one
/// interlocked increment), never throws and touches neither the database nor the network.
/// </para>
/// </summary>
public interface IFacetIndexGeneration
{
    /// <summary>The current generation (starts at 1, only grows within a process).</summary>
    long Current { get; }

    /// <summary>Marks every cached count as stale.</summary>
    void Bump();
}

/// <summary>The in-process generation counter (singleton). The cache lives in the same process, so nothing needs persisting.</summary>
public sealed class FacetIndexGeneration : IFacetIndexGeneration
{
    private long _generation = 1;

    public long Current => Interlocked.Read(ref _generation);

    public void Bump() => Interlocked.Increment(ref _generation);
}
