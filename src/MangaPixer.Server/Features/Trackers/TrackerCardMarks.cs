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
/// tracker's numbers as MangaPixer last saw them, never a request. Computed for the page being shown only. Called by
/// <see cref="CatalogBrowseService"/> (folder list and Volumes view) when registered.
/// </summary>
public sealed class TrackerCardMarks(MangaPixerDbContext db)
{
    /// <summary>
    /// Sets <see cref="CatalogNodeDto.TrackerRead"/> on the page's archive and volume-stack cards of a folder that belongs to a linked series
    /// the viewer tracks: for each tracker whose entry of that series was read (on a list, not "Don't sync"), the cards its chapters / volumes
    /// read cover (<see cref="TrackerReadOn"/>). The common case - a viewer who tracks nothing - costs ONE indexed lookup
    /// (<c>tracker_entries</c> by user); a tracked series adds the folder's link walk, its entries and the cached Volumes-view rows.
    /// </summary>
    public async Task<List<CatalogNodeDto>> DecorateAsync(List<CatalogNodeDto> nodes, long userId, long folderId, VolumeEntryService volumes,
        CancellationToken ct)
    {
        if (!nodes.Any(n => n.Kind == CatalogNodeKind.Archive || n.VolumeStack is not null))
            return nodes;
        if (!await db.TrackerEntries.AsNoTracking().AnyAsync(e => e.UserId == userId && e.RemoteSeenAt != null, ct))
            return nodes;
        if (await TrackerSeriesResolver.LinkedRecordOfAsync(db, folderId, ct) is not { } recordId)
            return nodes;
        var seen = (await db.TrackerEntries.AsNoTracking()
                .Where(e => e.UserId == userId && e.RecordId == recordId && e.RemoteSeenAt != null && !e.RemoteMissing && !e.DontSync)
                .Select(e => new { e.Tracker, e.RemoteChapters, e.RemoteVolumes })
                .ToListAsync(ct))
            .Select(e => (e.Tracker, Numbers: new TrackerNumbers(e.RemoteChapters, e.RemoteVolumes)))
            .Where(e => e.Numbers.Chapters > 0 || e.Numbers.Volumes > 0)
            .OrderBy(e => e.Tracker, StringComparer.Ordinal)
            .ToList();
        if (seen.Count == 0)
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
        var units = new Dictionary<string, UnitNumbers>(StringComparer.Ordinal);
        foreach (var row in VolumeGrouping.WithSeriesHeads(rows.DistinctBy(r => r.Id).ToList()))
            units[row.Id] = VolumeGrouping.UnitsOf(row);

        // A declared edition numbers its own volumes; an exact list lets a volume file count by its chapters.
        var editionFolder = view?.SeriesFolderId ?? folderId;
        var edition = (await DeclaredFactsResolution.LoadEditionAsync(db, [editionFolder], ct)).GetValueOrDefault(editionFolder);
        var declared = edition is { VolumeTotal: not null } or { Edition: not null };
        VolumeMapInput? map = null;
        if (nodes.Any(n => n.Kind == CatalogNodeKind.Archive && units.TryGetValue(n.Id, out var u) && u.Chapter is null && u.Volume is not null))
            (map, _) = await TrackerSeriesResolver.MapAsync(db, recordId, ct);

        return nodes.Select(node =>
        {
            List<string>? read = null;
            foreach (var (tracker, numbers) in seen)
            {
                var covered = node.Kind == CatalogNodeKind.Archive
                    ? TrackerReadOn.Covers(units.TryGetValue(node.Id, out var u) ? u : VolumeGrouping.UnitsOf(
                        new GroupingRow(node.Id, GroupingRowKind.Archive, node.DisplayName, string.Empty)), numbers, map, declared)
                    : node.VolumeStack is { } summary && stacks.TryGetValue(summary.Key, out var stack)
                        && TrackerReadOn.CoversStack(stack.Volume,
                            stack.Members.Where(m => !m.IsVolumeArchive && m.Chapter is not null).Select(m => m.Chapter!.Value).ToList(),
                            declared ? numbers with { Volumes = null } : numbers);
                if (covered)
                    (read ??= []).Add(tracker);
            }
            return read is null ? node : node with { TrackerRead = read };
        }).ToList();
    }
}
