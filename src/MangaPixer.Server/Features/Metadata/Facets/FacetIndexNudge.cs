namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

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

/// <summary>
/// 1.45.1 (B2): the nudge of a write whose libraries the CALLER looks up (the nudge itself never touches the database). A stored or refreshed
/// record used to nudge every library - six full rebuilds per record on a refresh day; now only the libraries it can change are nudged.
/// </summary>
public static class FacetIndexNudges
{
    /// <summary>
    /// The link states whose record the tag index reads (a linked series, a "Collection about" folder); a Needs review candidate is not read.
    /// </summary>
    public static readonly IReadOnlyList<int> IndexedLinkStates = Enum.GetValues<SeriesLinkState>()
        .Where(s => SeriesLinkStates.IsSeries(s) || s == SeriesLinkState.CollectionAbout)
        .Select(s => (int)s)
        .ToList();

    /// <summary>
    /// After a record was stored or refreshed (committed): nudges <paramref name="alsoLibraryId"/> (the library an admin acts in) and every
    /// library with a link row of an <see cref="IndexedLinkStates">indexed state</see> naming the record or index entries of its work. Never
    /// throws: when the look-up fails, every library is nudged.
    /// </summary>
    public static async Task RecordChangedAsync(this IFacetIndexNudge nudge, MangaPixerDbContext db, long recordId, long? alsoLibraryId,
        FacetNudgeReason reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(nudge);
        ArgumentNullException.ThrowIfNull(db);
        List<long> libraries;
        try
        {
            var states = IndexedLinkStates;
            var linking = db.NodeSeriesLinks.AsNoTracking().Where(l => l.RecordId == recordId && states.Contains(l.State)).Select(l => l.LibraryId);
            var indexed = db.FacetEntries.AsNoTracking()
                .Where(e => db.FacetWorks.Any(w => w.Id == e.WorkId && w.RecordId == recordId))
                .Select(e => e.LibraryId);
            libraries = await linking.Union(indexed).ToListAsync(ct);
        }
        catch (Exception)
        {
            nudge.AllChanged(reason);
            return;
        }
        if (alsoLibraryId is { } also && !libraries.Contains(also))
            libraries.Add(also);
        foreach (var library in libraries.Distinct().Order())
            nudge.LibraryChanged(library, reason);
    }
}
