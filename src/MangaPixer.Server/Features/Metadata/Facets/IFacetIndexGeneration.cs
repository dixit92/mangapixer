namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets;

/// <summary>
/// The tag index's write generation (1.45.0; defined by lane Q - its per-viewer count cache keys on <see cref="Current"/>). Lane F calls
/// <see cref="Bump"/> after EVERY committed index write (each write batch of a rebuild, a creator merge / split / rename, a display-name
/// push), so a cached count never outlives the entries it was computed from. This copy has lane Q's exact signature so lane F compiles
/// alone; the integrator keeps one at merge.
/// </summary>
public interface IFacetIndexGeneration
{
    long Current { get; }

    void Bump();
}
