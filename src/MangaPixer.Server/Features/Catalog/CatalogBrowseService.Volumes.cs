namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Covers;
using Microsoft.EntityFrameworkCore;

// The Volumes branch of browse (1.29.0): the entry list of a folder (stacks, real volume archives, unmerged subfolders, loose
// chapters) is built and memoised by VolumeEntryService; this file pages it (VolumePaging), enriches only the page's
// entries with the SAME enrichment as the folder list, and turns the page's stacks into cards.
public sealed partial class CatalogBrowseService
{
    /// <summary>
    /// One page of the Volumes view. TotalCount is the number of ENTRIES (a stack counts once), computed before the cursor;
    /// a stack is never split across pages; the cursor is the opaque <c>v:</c> position tuple. The "Continue" row and the
    /// reader's neighbours are unchanged (they follow the real folders).
    /// </summary>
    private async Task<PageResponse<CatalogNodeDto>> BrowseVolumesAsync(
        FolderVolumeEntries view, long userId, string? cursor, string? before, int pageSize, SortDirection direction, bool hideEmpty, CancellationToken ct)
    {
        IReadOnlyList<VolumeEntry> entries = view.Entries;
        if (hideEmpty)
        {
            // Same rule as the folder list: a folder whose subtree holds no readable archive is dropped.
            var folderIds = entries.Where(e => e.Kind == VolumeEntryKind.Folder).Select(e => view.Rows[e.Row!.Id].InternalId).ToList();
            var withArchives = folderIds.Count > 0
                ? (await ResolveFolderReadRollupsAsync(folderIds, userId, ct)).Keys.ToHashSet()
                : [];
            entries = entries.Where(e => e.Kind != VolumeEntryKind.Folder || withArchives.Contains(view.Rows[e.Row!.Id].InternalId)).ToList();
        }

        var page = VolumePaging.Page(entries, cursor, before, pageSize, direction == SortDirection.Descending);
        var plainRows = page.Entries.Where(e => e.Kind is VolumeEntryKind.Archive or VolumeEntryKind.Folder).Select(e => view.Rows[e.Row!.Id]).ToList();
        var plainNodes = (await EnrichAsync(plainRows, userId, view.LibraryId, ct)).ToDictionary(n => n.Id, StringComparer.Ordinal);
        var stacks = await BuildStackCardsAsync(view, page.Entries.Where(e => e.Kind == VolumeEntryKind.Stack).Select(e => e.Stack!).ToList(), userId, ct);

        var items = page.Entries
            .Select(e => e.Kind switch
            {
                VolumeEntryKind.Stack => stacks[e.Stack!.Key],
                VolumeEntryKind.MissingVolume => MissingVolumeCard(view, e.Volume!.Value),
                _ => VolumeStackService.WithAlsoInVolume(plainNodes[e.Row!.Id], view),
            })
            .ToList();
        return new PageResponse<CatalogNodeDto>
        {
            Items = items,
            TotalCount = entries.Count,
            NextCursor = page.NextCursor,
            HasMore = page.HasMore,
            PrevCursor = page.PrevCursor,
            HasPrevious = page.HasPrevious,
            NextUnread = await ResolveNextUnreadAsync(view.LibraryId, view.FolderId, userId, ct),
        };
    }

    /// <summary>
    /// The browse cards of virtual volume stacks: <c>Kind = VolumeStack</c>, the opaque <c>vs.&lt;folder&gt;.&lt;key&gt;</c> id, the
    /// cover (a chapter-only stack: its volume's stored web cover when shown - <see cref="StackCoverService"/>; else the stack's
    /// first member, its real volume archive or its first chapter, through the cover resolver), a
    /// read rollup over the members and the summary the card badges read. Two batched queries for the whole page.
    /// </summary>
    internal async Task<Dictionary<string, CatalogNodeDto>> BuildStackCardsAsync(
        FolderVolumeEntries view, IReadOnlyList<VolumeStack> stacks, long userId, CancellationToken ct)
    {
        var cards = new Dictionary<string, CatalogNodeDto>(StringComparer.Ordinal);
        if (stacks.Count == 0)
            return cards;

        var coverTargets = stacks
            .Select(s => view.Rows[s.Members[0].Row.Id])
            .DistinctBy(r => r.InternalId)
            .Select(r => new CoverTarget(r.InternalId, r.Id, false))
            .ToList();
        var covers = await _covers.ResolveAsync(coverTargets, ct);
        // A chapter-only stack shows its volume's stored web cover (1.29.0, design 7.4) when the web-cover switches allow it.
        var webCovers = await _stackCovers.ResolveAsync(view.FolderId, view.FolderPublicId, view.LibraryId, stacks, ct);

        var memberIds = stacks.SelectMany(s => s.Members).Select(m => view.Rows[m.Row.Id].InternalId).Distinct().ToList();
        var read = (await _db.ReadMarks.AsNoTracking().Where(m => m.UserId == userId && memberIds.Contains(m.ItemId)).Select(m => m.ItemId).ToListAsync(ct)).ToHashSet();
        var inProgress = (await _db.ReadingProgress.AsNoTracking()
            .Where(p => p.UserId == userId && memberIds.Contains(p.ItemId) && p.State == (int)ReadingState.InProgress)
            .Select(p => p.ItemId).ToListAsync(ct)).ToHashSet();
        // A stack shows the star when ANY archive in it is starred (owner, 1.29.0 RC; display only - a stack is not a node).
        var starred = (await _db.Favorites.AsNoTracking().Where(f => f.UserId == userId && memberIds.Contains(f.CatalogNodeId))
            .Select(f => f.CatalogNodeId).ToListAsync(ct)).ToHashSet();

        foreach (var stack in stacks)
        {
            var ids = stack.Members.Select(m => view.Rows[m.Row.Id].InternalId).ToList();
            var readCount = ids.Count(read.Contains);
            var progressCount = ids.Count(id => !read.Contains(id) && inProgress.Contains(id));
            var first = view.Rows[stack.Members[0].Row.Id];
            ResolvedCover? cover = webCovers.TryGetValue(stack.Key, out var web) ? new ResolvedCover(web.Url, CardCoverSource.WebVolume)
                : covers.TryGetValue(first.InternalId, out var own) ? own : null;
            cards[stack.Key] = new CatalogNodeDto
            {
                Id = VolumeStackId.Encode(view.FolderPublicId, stack.Key),
                ParentId = view.FolderPublicId,
                LibraryId = view.LibraryPublicId,
                Kind = CatalogNodeKind.VolumeStack,
                DisplayName = stack.Label,
                Availability = CatalogNodeAvailability.Available,
                CoverUrl = cover?.Url,
                CoverSource = cover?.Source,
                ReadRollup = FolderReadRollupRules.Classify(ids.Count, readCount, progressCount),
                IsFavorite = ids.Any(starred.Contains),
                VolumeStack = SummaryOf(stack),
            };
        }
        return cards;
    }

    /// <summary>
    /// The placeholder card of a missing volume (1.29.0 RC): <c>Kind = VolumeStack</c> with <see cref="VolumeStackSummaryDto.Missing"/>,
    /// the opaque <c>vm.&lt;folder&gt;.&lt;key&gt;</c> id (never opened), no cover, nothing present.
    /// </summary>
    private static CatalogNodeDto MissingVolumeCard(FolderVolumeEntries view, decimal volume)
    {
        var key = VolumeGrouping.KeyOf(volume);
        var label = VolumeGrouping.LabelOf(volume, VolumeStackConfidence.Exact);
        return new CatalogNodeDto
        {
            Id = VolumeStackId.EncodeMissing(view.FolderPublicId, key),
            ParentId = view.FolderPublicId,
            LibraryId = view.LibraryPublicId,
            Kind = CatalogNodeKind.VolumeStack,
            DisplayName = label,
            Availability = CatalogNodeAvailability.Unavailable,
            VolumeStack = new VolumeStackSummaryDto
            {
                Key = key,
                Label = label,
                PresentCount = 0,
                MissingCount = 0,
                ExtraCount = 0,
                HasVolumeArchive = false,
                Confidence = VolumeStackConfidence.Exact,
                Missing = true,
            },
        };
    }

    /// <summary>
    /// The folder list of a linked series folder or one of its unit subfolders (1.30.0, reach): a chapter card whose chapters a
    /// volume FILE of the same series already holds says so ("Also in Volume 10"). Reads the memoised series entries; stored data only.
    /// </summary>
    private async Task<List<CatalogNodeDto>> WithAlsoInVolumeAsync(List<CatalogNodeDto> nodes, long folderId, CancellationToken ct)
    {
        var view = await _volumes.GetEntriesAsync(folderId, ct);
        if (view?.SeriesFolderId is not { } series)
            return nodes;
        var seriesView = series == view.FolderId ? view : await _volumes.GetEntriesAsync(series, ct);
        if (seriesView is null || seriesView.AlsoInVolume.Count == 0)
            return nodes;
        return nodes.Select(n => VolumeStackService.WithAlsoInVolume(n, seriesView)).ToList();
    }

    internal static VolumeStackSummaryDto SummaryOf(VolumeStack stack) => new()
    {
        Key = stack.Key,
        Label = stack.Label,
        PresentCount = stack.PresentCount,
        ChapterCount = stack.ChapterCount,
        MissingCount = stack.MissingChapters.Count,
        ExtraCount = stack.ExtraCount,
        HasVolumeArchive = stack.HasVolumeArchive,
        Confidence = stack.Confidence,
        FirstChapter = stack.FirstChapter,
        LastChapter = stack.LastChapter,
        ChaptersPresent = stack.ChaptersPresent,
        OfficialRelease = stack.OfficialRelease,
    };
}
