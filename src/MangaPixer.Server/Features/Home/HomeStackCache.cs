namespace com.lifepixer.mangapixer.Server.Features.Home;

using System.Collections.Concurrent;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
/// One recent archive of a library's "New chapters" source (1.46.0 lane HC): the archive, when it was added (also in the stored binary
/// form, <see cref="CreatedKey"/>, which is what the window's SQL comparison compares) and the node its stack is built on.
/// </summary>
internal readonly record struct HomeStackCandidate(long Id, string PublicId, string DisplayName, DateTimeOffset CreatedAt, long CreatedKey, long TopId);

/// <summary>The node a stack is built on (a series folder, or a loose archive itself): its public id, name and kind.</summary>
internal readonly record struct HomeStackNode(string PublicId, string DisplayName, int Kind);

/// <summary>
/// The user-independent part of one library's "New chapters" row (1.46.0 lane HC): every archive added since <c>cutoff</c> with the node
/// its stack is built on (the up-walk to the series folder), and those nodes. The stacks, their order and their counts follow from it for
/// any later cut-off (<see cref="RecentChaptersService"/> filters the candidates by the request's own cut-off).
/// </summary>
internal sealed record HomeStackSource(IReadOnlyList<HomeStackCandidate> Candidates, IReadOnlyDictionary<long, HomeStackNode> Nodes)
{
    /// <summary>
    /// The readable (live) archives below each stack node whose read rollup was asked for - the node itself for a loose archive - read
    /// once per source, on first use: the catalog half of the read rollup (which archives a stack holds). The other half, which of them
    /// the viewer has read or is reading, is read per request.
    /// </summary>
    public ConcurrentDictionary<long, long[]> Readable { get; } = new();
}

/// <summary>
/// The stamp of everything a library's <see cref="HomeStackSource"/> is computed from (1.46.0 lane HC), read BEFORE the rows so stored rows
/// are never older than their stamp:
/// <list type="number">
/// <item>The library's <c>CatalogRevision</c> and creation time, and its latest scan run - the catalog rows the source reads (candidates:
/// kind, availability, added date, name; their ancestors: parent, kind, name; the stack nodes). Only a scan or a move pairing writes those
/// rows, and both raise the revision after writing (as lane B's <c>LibraryItemCounts</c> and lane C's completion answers rely on).</item>
/// <item><see cref="ScanActive"/>: a scan of the library is pending or running - it writes rows before it raises the revision, so nothing
/// is reused or stored until it ends.</item>
/// <item><see cref="LinkedFolders"/>: the library's folders with a series link the up-walk counts (a record, and Confirmed / Auto /
/// Collection about) - exact.</item>
/// </list>
/// The window cut-off and the age are checked by <see cref="HomeStackCache"/>.
/// </summary>
internal sealed record HomeStackStamp(long Revision, DateTimeOffset LibraryCreatedAt, long LastScanRun, bool ScanActive, long[] LinkedFolders)
{
    public bool SameInputs(HomeStackStamp other) =>
        Revision == other.Revision && LibraryCreatedAt == other.LibraryCreatedAt && LastScanRun == other.LastScanRun
        && LinkedFolders.AsSpan().SequenceEqual(other.LinkedFolders);

    /// <summary>The stamps of <paramref name="libraryIds"/>, in a fixed number of queries (every id asked for is in the answer).</summary>
    public static async Task<Dictionary<long, HomeStackStamp>> ReadAsync(MangaPixerDbContext db, IReadOnlyCollection<long> libraryIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(libraryIds);
        var result = new Dictionary<long, HomeStackStamp>();
        if (libraryIds.Count == 0)
            return result;
        var ids = libraryIds.Distinct().ToList();

        var libraries = await db.Libraries.AsNoTracking().Where(l => ids.Contains(l.Id))
            .Select(l => new { l.Id, l.CatalogRevision, l.CreatedAt }).ToListAsync(ct);

        // The latest scan run of each library, and whether one is pending (0) or running (1) - lane B's rule.
        var runs = await db.ScanRuns.AsNoTracking()
            .Where(s => ids.Contains(s.LibraryId))
            .GroupBy(s => s.LibraryId)
            .Select(g => new { LibraryId = g.Key, Last = g.Max(s => s.Id), Active = g.Sum(s => s.Status == 0 || s.Status == 1 ? 1 : 0) })
            .ToDictionaryAsync(r => r.LibraryId, ct);

        // The up-walk's "Linked" test: a folder with a link row that has a record and is Confirmed, Auto or Collection about.
        var folder = (int)CatalogNodeKind.Folder;
        var confirmed = (int)SeriesLinkState.Confirmed;
        var auto = (int)SeriesLinkState.Auto;
        var collection = (int)SeriesLinkState.CollectionAbout;
        var linked = await (
            from l in db.NodeSeriesLinks.AsNoTracking()
            join n in db.CatalogNodes.AsNoTracking() on l.NodeId equals n.Id
            where ids.Contains(n.LibraryId) && n.Kind == folder && l.RecordId != null
                && (l.State == confirmed || l.State == auto || l.State == collection)
            select new { n.LibraryId, l.NodeId }).ToListAsync(ct);
        var linkedByLibrary = linked.GroupBy(l => l.LibraryId)
            .ToDictionary(g => g.Key, g => g.Select(l => l.NodeId).Distinct().Order().ToArray());

        foreach (var library in libraries)
        {
            var run = runs.GetValueOrDefault(library.Id);
            result[library.Id] = new HomeStackStamp(library.CatalogRevision, library.CreatedAt, run?.Last ?? 0, run is not null && run.Active > 0,
                linkedByLibrary.GetValueOrDefault(library.Id) ?? []);
        }
        // A library deleted meanwhile: a stamp that never matches a stored one and is never stored.
        foreach (var id in ids.Where(id => !result.ContainsKey(id)))
            result[id] = new HomeStackStamp(-1, default, 0, ScanActive: true, []);
        return result;
    }
}

/// <summary>
/// 1.46.0 lane HC: the home "New chapters" row's user-independent part per library, kept in memory (one instance per host, a singleton).
/// For each (library, window days) it keeps the <see cref="HomeStackSource"/> - the recent archives with their series folder, the result of
/// the candidate query and the up-walk recursive CTE (~the whole remaining cost of the row after lane D) - while its
/// <see cref="HomeStackStamp"/> is unchanged. The viewer's visibility (grants, Private + Incognito), home-excluded libraries, read rollups,
/// cap and card flags stay per request; so do the covers.
/// <para>
/// A source built at time T serves a later request at T' only when: no scan of the library is pending or running; the stamp's inputs are
/// unchanged; the source's cut-off is not later than the request's (it holds every archive the request's window can hold - the request then
/// keeps only those added since ITS cut-off, so a stack ageing out of the window leaves on time); and T' - T is under <see cref="MaxAge"/>
/// (a bound for a change that moves no input). A restored database is applied at start-up, before any request.
/// </para>
/// </summary>
public sealed class HomeStackCache
{
    /// <summary>The longest a source serves with unchanged inputs (the same bound as lane C's completion answers, owner-accepted).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    /// <summary>The stored form of a <see cref="DateTimeOffset"/> column (the model's convention), for the window comparison.</summary>
    private static readonly Func<DateTimeOffset, long> Encode = new DateTimeOffsetToBinaryConverter().ConvertToProviderExpression.Compile();

    private sealed record Entry(HomeStackStamp Stamp, long CutoffKey, DateTimeOffset BuiltAt, HomeStackSource Source);

    private readonly ConcurrentDictionary<(long LibraryId, int WindowDays), Entry> _entries = new();
    private int _hits;
    private int _builds;

    /// <summary>Requests answered from a kept source (tests).</summary>
    internal int Hits => Volatile.Read(ref _hits);

    /// <summary>Sources built (tests).</summary>
    internal int Builds => Volatile.Read(ref _builds);

    /// <summary>Kept sources, stale ones included until they are dropped (tests).</summary>
    internal int Count => _entries.Count;

    /// <summary>The stored binary form of <paramref name="value"/> - the value SQL compares a <c>CreatedAt</c> column by.</summary>
    internal static long StoredKey(DateTimeOffset value) => Encode(value);

    /// <summary>The kept source for this library and window, or null when it may not serve a request with this stamp and cut-off at <paramref name="now"/>.</summary>
    internal HomeStackSource? TryGet(long libraryId, int windowDays, HomeStackStamp stamp, long cutoffKey, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        if (stamp.ScanActive || !_entries.TryGetValue((libraryId, windowDays), out var entry))
            return null;
        if (now - entry.BuiltAt >= MaxAge)
        {
            // 1.49.0 lane M: past the age bound a source can never serve again - drop it instead of keeping it until the next build.
            _entries.TryRemove(new KeyValuePair<(long, int), Entry>((libraryId, windowDays), entry));
            return null;
        }
        if (!entry.Stamp.SameInputs(stamp) || entry.CutoffKey > cutoffKey || now < entry.BuiltAt)
            return null;
        Interlocked.Increment(ref _hits);
        return entry.Source;
    }

    /// <summary>
    /// Keeps a source built from rows read AFTER <paramref name="stamp"/> (at <paramref name="stampedAt"/>) with the window cut-off
    /// <paramref name="cutoffKey"/>; never while a scan is pending or running.
    /// </summary>
    internal void Store(long libraryId, int windowDays, HomeStackStamp stamp, long cutoffKey, DateTimeOffset stampedAt, HomeStackSource source)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        ArgumentNullException.ThrowIfNull(source);
        Interlocked.Increment(ref _builds);
        if (stamp.ScanActive)
        {
            _entries.TryRemove((libraryId, windowDays), out _);
            return;
        }
        _entries[(libraryId, windowDays)] = new Entry(stamp, cutoffKey, stampedAt, source);
        DropExpired(stampedAt);
    }

    /// <summary>
    /// 1.49.0 lane M: drops every kept source past <see cref="MaxAge"/> at <paramref name="now"/> - other windows and libraries nobody
    /// asks for any more (a window setting changed, a library deleted) - which <see cref="TryGet"/> would refuse anyway. Before, a stale
    /// source stayed in memory until the same library and window were built again.
    /// </summary>
    internal void DropExpired(DateTimeOffset now)
    {
        foreach (var pair in _entries)
        {
            if (now - pair.Value.BuiltAt >= MaxAge)
                _entries.TryRemove(pair);
        }
    }
}
