namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets;

/// <summary>Why the tag index of a library (or of every library) may be out of date. Logged as a number; never renumber.</summary>
public enum FacetNudgeReason
{
    /// <summary>A link row was written by an admin (link, review accept, Don't match, Collection about, Artist folder, unlink).</summary>
    LinkChanged = 1,

    /// <summary>A web record was stored or refreshed (it may be linked from several libraries).</summary>
    RecordStored = 2,

    /// <summary>The ComicInfo backfill stored a batch of ComicInfo rows.</summary>
    ComicInfoStored = 3,

    /// <summary>An admin set or cleared declared type / creators.</summary>
    DeclaredFactsChanged = 4,

    /// <summary>An artists' other names run stored author records.</summary>
    AuthorAliasesStored = 5,

    /// <summary>A library scan's reconciliation finished (the catalog revision moved).</summary>
    ScanFinished = 6,

    /// <summary>An admin merged, split or renamed a creator (lane F).</summary>
    CreatorOverrideChanged = 7,

    /// <summary>"Delete fetched web data" removed links and records.</summary>
    WebDataPurged = 8,
}

/// <summary>
/// Tells the tag index (1.45.0, D1 2.3) that its inputs may have changed - the seam between the writers of those inputs and lane F's
/// builder. Call sites call it AFTER their write is committed, one line each.
/// <para>
/// Contract: a nudge is a HINT, not a change log. It must be fast and must not touch the database or the network (mark the library
/// dirty, enqueue and return), and it must not throw (the caller's write is already saved). The builder debounces, recomputes the
/// library's inputs key and rebuilds only when that key changed; its periodic sweep catches every write that sends no nudge (automatic
/// matching, carry-over, background refresh), so a missed or extra nudge costs latency, never correctness.
/// <see cref="AllChanged"/> is for a write whose libraries the caller does not know (a record linked from several libraries, a
/// ComicInfo batch across libraries, author records).
/// </para>
/// </summary>
public interface IFacetIndexNudge
{
    void LibraryChanged(long libraryId, FacetNudgeReason reason);

    void AllChanged(FacetNudgeReason reason);
}

/// <summary>The default nudge until the index registers its own (lane F): does nothing.</summary>
public sealed class NoFacetIndexNudge : IFacetIndexNudge
{
    /// <summary>For services built outside DI (tests, static helpers) that take an optional nudge.</summary>
    public static readonly NoFacetIndexNudge Instance = new();

    public void LibraryChanged(long libraryId, FacetNudgeReason reason)
    {
    }

    public void AllChanged(FacetNudgeReason reason)
    {
    }
}
