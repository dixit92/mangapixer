namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Features.Metadata.Declared;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// 1.43.0 tracker sync (lane G, owner 2026-10-15 - card mark C): the browse cards' "read on &lt;tracker&gt;" flag. Display only: the
/// tracker's numbers as MangaPixer last saw them, never a request. Computed for the page being shown only (archive, volume-stack and series
/// folder cards). Called by
/// <see cref="CatalogBrowseService"/> (folder list and Volumes view) when registered.
/// </summary>
public sealed class TrackerCardMarks(MangaPixerDbContext db, TrackerSettingsService settings, TrackerSeriesResolver resolver)
{
    /// <summary>
    /// Sets <see cref="CatalogNodeDto.TrackerRead"/> on the page's cards. Owner (2026-10-15): the ring means "read on EVERY tracker this series
    /// syncs to" - an intersection. The series' synced trackers are the viewer's entries of it on trackers that are connected (account OK,
    /// current consent), allowed by the admin, read at least once, on the viewer's list and not "Don't sync"; a card gets the list of ALL of
    /// them only when every one covers it (<see cref="TrackerReadOn"/>), else nothing. A connected tracker without such an entry neither counts
    /// nor blocks; no synced tracker, no ring.
    /// <list type="bullet">
    /// <item>Archive and volume-stack cards inside a folder of a linked series (<paramref name="folderId"/>): by their own numbers.</item>
    /// <item>Owner (2026-10-15, iPad review): SERIES FOLDER cards (a folder node with its own Confirmed / Auto link) - a rollup like the folder's
    /// Read badge: the ring when EVERY archive of that series the viewer can open (<see cref="TrackerSeriesResolver.ArchivesAsync"/>, units after
    /// the series heads, declared editions as everywhere) is covered by EVERY synced tracker; none for a series without archives or without a
    /// synced tracker. A folder whose series the viewer syncs nowhere costs no archive walk.</item>
    /// </list>
    /// The common case - a viewer who tracks nothing - costs ONE indexed lookup (<c>tracker_entries</c> by user).
    /// </summary>
    public async Task<List<CatalogNodeDto>> DecorateAsync(List<CatalogNodeDto> nodes, long userId, long? folderId, VolumeEntryService volumes,
        CancellationToken ct)
    {
        var hasItems = folderId is not null && nodes.Any(n => n.Kind == CatalogNodeKind.Archive || n.VolumeStack is not null);
        var hasFolders = nodes.Any(n => n.Kind is CatalogNodeKind.Folder or CatalogNodeKind.Archive);
        if (!hasItems && !hasFolders)
            return nodes;
        if (!await db.TrackerEntries.AsNoTracking().AnyAsync(e => e.UserId == userId && e.RemoteSeenAt != null, ct))
            return nodes;
        if (hasItems)
            nodes = await DecorateItemsAsync(nodes, userId, folderId!.Value, volumes, ct);
        if (hasFolders)
            nodes = await DecorateSeriesFoldersAsync(nodes, userId, folderId, ct);
        return nodes;
    }

    /// <summary>Series whose archives were walked for a folder card in this scope (tests: an untracked series costs no walk).</summary>
    internal int SeriesWalked { get; private set; }

    /// <summary>
    /// The synced trackers of each record (see <see cref="DecorateAsync"/>), ordered by slug; records without one are left out.
    /// </summary>
    private async Task<Dictionary<long, List<(string Tracker, TrackerNumbers Numbers)>>> SyncedAsync(long userId, IReadOnlyCollection<long> recordIds,
        CancellationToken ct)
    {
        var ok = (int)TrackerAccountState.Ok;
        var ids = recordIds.Distinct().ToList();
        var candidates = await (from e in db.TrackerEntries.AsNoTracking()
                                join a in db.TrackerAccounts.AsNoTracking() on new { e.UserId, e.Tracker } equals new { a.UserId, a.Tracker }
                                where e.UserId == userId && ids.Contains(e.RecordId) && e.RemoteSeenAt != null && !e.RemoteMissing && !e.DontSync
                                      && a.State == ok
                                select new { e.RecordId, e.Tracker, e.RemoteChapters, e.RemoteVolumes, a.ConsentVersion }).ToListAsync(ct);
        var allowed = new Dictionary<string, bool>(StringComparer.Ordinal);
        var result = new Dictionary<long, List<(string, TrackerNumbers)>>();
        foreach (var c in candidates.OrderBy(c => c.Tracker, StringComparer.Ordinal))
        {
            if (!TrackerFacts.IsKnown(c.Tracker) || c.ConsentVersion != TrackerConsent.UserVersion(c.Tracker))
                continue;
            if (!allowed.TryGetValue(c.Tracker, out var isAllowed))
                // 1.46.0 lane M (measurement only, off by default): a measured instance runs with the tracker network off, which
                // would hide every mark; MangaPixer:Perf:TrackerMarksWithNetworkOff keeps the display-only marks so their cost shows.
                allowed[c.Tracker] = isAllowed = com.lifepixer.mangapixer.Server.Operations.Perf.PerfRecorder.Active is { TrackerMarksWithNetworkOff: true }
                    || await settings.IsAllowedAsync(c.Tracker, ct);
            if (!isAllowed)
                continue;
            if (!result.TryGetValue(c.RecordId, out var list))
                result[c.RecordId] = list = [];
            list.Add((c.Tracker, new TrackerNumbers(c.RemoteChapters, c.RemoteVolumes)));
        }
        return result;
    }

    /// <summary>The series folder cards' rollup (see <see cref="DecorateAsync"/>).</summary>
    /// <remarks>
    /// 1.43.0 (one-shot fix): also an ARCHIVE card with its own link to a series other than the browsed folder's (an archive linked as its
    /// own series, e.g. a one-shot in a collection folder) - its ring is the rollup of its own series, never the folder's numbers.
    /// </remarks>
    private async Task<List<CatalogNodeDto>> DecorateSeriesFoldersAsync(List<CatalogNodeDto> nodes, long userId, long? folderId, CancellationToken ct)
    {
        var ids = nodes.Where(n => n.Kind is CatalogNodeKind.Folder or CatalogNodeKind.Archive).Select(n => n.Id).ToList();
        var records = await TrackerSeriesResolver.OwnLinkedRecordsAsync(db, ids, ct);
        if (records.Count == 0)
            return nodes;
        var folderRecord = folderId is { } f ? await TrackerSeriesResolver.LinkedRecordOfAsync(db, f, ct) : null;
        var kinds = nodes.GroupBy(n => n.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Kind, StringComparer.Ordinal);
        foreach (var archive in records.Where(r => kinds.GetValueOrDefault(r.Key) == CatalogNodeKind.Archive && r.Value == folderRecord).ToList())
            records.Remove(archive.Key); // an archive of the browsed series itself: its own numbers decide (the items path)
        if (records.Count == 0)
            return nodes;
        var synced = await SyncedAsync(userId, records.Values.ToList(), ct);
        if (synced.Count == 0)
            return nodes;
        var rings = new Dictionary<long, IReadOnlyList<string>?>();
        foreach (var recordId in records.Values.Distinct().Where(synced.ContainsKey))
        {
            SeriesWalked++;
            var (archives, map) = await resolver.ArchivesAsync(userId, recordId, ct);
            var parts = TrackerReadOn.SplitPartsOf(archives.Select(a => a.Units));
            rings[recordId] = archives.Count == 0
                ? null
                : ReadOnEvery(synced[recordId], numbers => archives.All(a => TrackerReadOn.Covers(a.Units, numbers, map, a.DeclaredEdition, a.OneShot, parts)));
        }
        return nodes.Select(node =>
        {
            if (!records.TryGetValue(node.Id, out var recordId))
                return node;
            var ring = rings.GetValueOrDefault(recordId);
            // An archive of another series: its own series decides, also to "no ring".
            return node.Kind == CatalogNodeKind.Archive ? node with { TrackerRead = ring } : ring is null ? node : node with { TrackerRead = ring };
        }).ToList();
    }

    /// <summary>The archive and volume-stack cards of a folder of a linked series (see <see cref="DecorateAsync"/>).</summary>
    private async Task<List<CatalogNodeDto>> DecorateItemsAsync(List<CatalogNodeDto> nodes, long userId, long folderId, VolumeEntryService volumes,
        CancellationToken ct)
    {
        if (await TrackerSeriesResolver.LinkedRecordOfAsync(db, folderId, ct) is not { } recordId)
            return nodes;
        if (!(await SyncedAsync(userId, [recordId], ct)).TryGetValue(recordId, out var seen))
            return nodes;

        // The units of every archive of the folder as the Volumes view reads them (series heads, ComicInfo), and its stacks.
        var view = await volumes.GetEntriesAsync(folderId, ct);
        var rows = new List<GroupingRow>();
        var stacks = new Dictionary<string, VolumeStack>(StringComparer.Ordinal);
        foreach (var entry in view?.Entries ?? [])
        {
            if (entry.Row is { Kind: GroupingRowKind.Archive } row)
                rows.Add(row);
            if (entry.Stack is { } stack)
            {
                stacks[stack.Key] = stack;
                rows.AddRange(stack.Members.Select(m => m.Row));
            }
        }
        var distinct = rows.DistinctBy(r => r.Id).ToList();
        var trackerUnits = TrackerUnits.Of(distinct);
        var units = new Dictionary<string, TrackerUnit>(StringComparer.Ordinal);
        for (var i = 0; i < distinct.Count; i++)
            units[distinct[i].Id] = trackerUnits[i];
        // 1.44.0: a split part rings when its whole chapter is read (the folder's parts, as its Volumes view reads them).
        var parts = TrackerReadOn.SplitPartsOf(trackerUnits.Where(u => !u.OneShot).Select(u => u.Units));

        // A declared edition numbers its own volumes; an exact list lets a volume file count by its chapters.
        var editionFolder = view?.SeriesFolderId ?? folderId;
        var edition = (await DeclaredFactsResolution.LoadEditionAsync(db, [editionFolder], ct)).GetValueOrDefault(editionFolder);
        var declared = edition is { VolumeTotal: not null } or { Edition: not null };
        VolumeMapInput? map = null;
        if (nodes.Any(n => n.Kind == CatalogNodeKind.Archive && units.TryGetValue(n.Id, out var u) && u.Units.Chapter is null && u.Units.Volume is not null))
            (map, _) = await TrackerSeriesResolver.MapAsync(db, recordId, ct);

        return nodes.Select(node =>
        {
            var read = ReadOnEvery(seen, numbers => node.Kind == CatalogNodeKind.Archive
                ? units.TryGetValue(node.Id, out var u)
                    ? TrackerReadOn.Covers(u.Units, numbers, map, declared, u.OneShot, parts)
                    : TrackerReadOn.Covers(VolumeGrouping.UnitsOf(new GroupingRow(node.Id, GroupingRowKind.Archive, node.DisplayName, string.Empty)),
                        numbers, map, declared)
                : node.VolumeStack is { } summary && stacks.TryGetValue(summary.Key, out var stack)
                    && TrackerReadOn.CoversStack(stack.Volume,
                        stack.Members.Where(m => !m.IsVolumeArchive && m.Chapter is not null).Select(m => m.Chapter!.Value).ToList(),
                        declared ? numbers with { Volumes = null } : numbers));
            return read is null ? node : node with { TrackerRead = read };
        }).ToList();
    }

    /// <summary>
    /// The intersection rule (owner, 2026-10-15): every synced tracker's slug when ALL of them cover the card, else null (no ring); null for no
    /// synced tracker. With one synced tracker it is that tracker's own answer.
    /// </summary>
    public static IReadOnlyList<string>? ReadOnEvery(IReadOnlyList<(string Tracker, TrackerNumbers Numbers)> synced, Func<TrackerNumbers, bool> covers)
    {
        ArgumentNullException.ThrowIfNull(synced);
        ArgumentNullException.ThrowIfNull(covers);
        if (synced.Count == 0 || !synced.All(s => covers(s.Numbers)))
            return null;
        return synced.Select(s => s.Tracker).ToList();
    }
}
