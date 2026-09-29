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
        var plainRows = page.Entries.Where(e => e.Kind != VolumeEntryKind.Stack).Select(e => view.Rows[e.Row!.Id]).ToList();
        var plainNodes = (await EnrichAsync(plainRows, userId, view.LibraryId, ct)).ToDictionary(n => n.Id, StringComparer.Ordinal);
        var stacks = await BuildStackCardsAsync(view, page.Entries.Where(e => e.Kind == VolumeEntryKind.Stack).Select(e => e.Stack!).ToList(), userId, ct);

        var items = page.Entries
            .Select(e => e.Kind == VolumeEntryKind.Stack ? stacks[e.Stack!.Key] : plainNodes[e.Row!.Id])
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
    /// cover of the stack's first member (its real volume archive, else its first chapter - through the cover resolver), a
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
        var covers = await _covers.ResolveUrlsAsync(coverTargets, ct);

        var memberIds = stacks.SelectMany(s => s.Members).Select(m => view.Rows[m.Row.Id].InternalId).Distinct().ToList();
        var read = (await _db.ReadMarks.AsNoTracking().Where(m => m.UserId == userId && memberIds.Contains(m.ItemId)).Select(m => m.ItemId).ToListAsync(ct)).ToHashSet();
        var inProgress = (await _db.ReadingProgress.AsNoTracking()
            .Where(p => p.UserId == userId && memberIds.Contains(p.ItemId) && p.State == (int)ReadingState.InProgress)
            .Select(p => p.ItemId).ToListAsync(ct)).ToHashSet();

        foreach (var stack in stacks)
        {
            var ids = stack.Members.Select(m => view.Rows[m.Row.Id].InternalId).ToList();
            var readCount = ids.Count(read.Contains);
            var progressCount = ids.Count(id => !read.Contains(id) && inProgress.Contains(id));
            var first = view.Rows[stack.Members[0].Row.Id];
            cards[stack.Key] = new CatalogNodeDto
            {
                Id = VolumeStackId.Encode(view.FolderPublicId, stack.Key),
                ParentId = view.FolderPublicId,
                LibraryId = view.LibraryPublicId,
                Kind = CatalogNodeKind.VolumeStack,
                DisplayName = stack.Label,
                Availability = CatalogNodeAvailability.Available,
                CoverUrl = covers.GetValueOrDefault(first.InternalId),
                ReadRollup = FolderReadRollupRules.Classify(ids.Count, readCount, progressCount),
                VolumeStack = SummaryOf(stack),
            };
        }
        return cards;
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
    };
}
