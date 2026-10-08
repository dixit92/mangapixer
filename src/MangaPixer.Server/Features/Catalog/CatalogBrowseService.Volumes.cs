namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.Missing;
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
        FolderVolumeEntries view, long userId, string? cursor, string? before, int pageSize, SortDirection direction, bool hideEmpty,
        BrowseReadStateFilter readState, bool favoritesOnly, CancellationToken ct)
    {
        IReadOnlyList<VolumeEntry> entries = view.Entries;
        if (readState != BrowseReadStateFilter.All || favoritesOnly)
            entries = await FilterVolumeEntriesAsync(view, entries, userId, readState, favoritesOnly, ct);
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
        var collections = await BuildCollectionCardsAsync(view,
            page.Entries.Where(e => e.Kind == VolumeEntryKind.CollectionStack).Select(e => e.Collection!).ToList(), userId, ct);

        var items = page.Entries
            .Select(e => e.Kind switch
            {
                VolumeEntryKind.Stack => stacks[e.Stack!.Key],
                VolumeEntryKind.CollectionStack => collections[e.Collection!.Key],
                VolumeEntryKind.MissingVolume => MissingVolumeCard(view, e.Volume!.Value),
                _ => VolumeStackService.WithAlsoInVolume(VolumeTitled(plainNodes[e.Row!.Id], e), view),
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
    /// The read-state and favourites filters inside the Volumes view (1.31.0; until then a filter flattened the list), with the
    /// folder list's rules so a filter always agrees with the badges: a STACK by the same read rollup its card shows
    /// (<see cref="FolderReadRollupRules.Classify"/> over its members - Read = all read, Reading = some read or in progress,
    /// Unread = none) and starred when any member is (the card's star); an ARCHIVE by its own read mark / progress / star; a
    /// FOLDER by its descendant rollup (<see cref="MatchesFolderReadState"/>) and its own star. Missing-volume placeholders are
    /// not items and are hidden while a filter is on. Three batched queries, before paging (so TotalCount and the cursor see
    /// the filtered list).
    /// </summary>
    private async Task<IReadOnlyList<VolumeEntry>> FilterVolumeEntriesAsync(
        FolderVolumeEntries view, IReadOnlyList<VolumeEntry> entries, long userId, BrowseReadStateFilter readState, bool favoritesOnly,
        CancellationToken ct)
    {
        long IdOf(string publicId) => view.Rows[publicId].InternalId;
        // A stack of either kind (a volume, or 1.37.0 stories collected in one volume) filters by its member archives.
        List<long> MemberIds(VolumeEntry entry) => entry.Kind == VolumeEntryKind.CollectionStack
            ? entry.Collection!.Members.Select(m => IdOf(m.Id)).ToList()
            : entry.Stack!.Members.Select(m => IdOf(m.Row.Id)).ToList();

        var archiveIds = entries.Where(e => e.Kind == VolumeEntryKind.Archive).Select(e => IdOf(e.Row!.Id))
            .Concat(entries.Where(e => e.Kind is VolumeEntryKind.Stack or VolumeEntryKind.CollectionStack).SelectMany(MemberIds))
            .Distinct().ToList();
        var folderIds = entries.Where(e => e.Kind == VolumeEntryKind.Folder).Select(e => IdOf(e.Row!.Id)).ToList();

        HashSet<long> read = [], inProgress = [], starred = [];
        Dictionary<long, FolderReadRollup> folderRollups = [];
        if (readState != BrowseReadStateFilter.All)
        {
            if (archiveIds.Count > 0)
            {
                read = (await _db.ReadMarks.AsNoTracking().Where(m => m.UserId == userId && archiveIds.Contains(m.ItemId))
                    .Select(m => m.ItemId).ToListAsync(ct)).ToHashSet();
                inProgress = (await _db.ReadingProgress.AsNoTracking()
                    .Where(p => p.UserId == userId && archiveIds.Contains(p.ItemId) && p.State == (int)ReadingState.InProgress)
                    .Select(p => p.ItemId).ToListAsync(ct)).ToHashSet();
            }
            if (folderIds.Count > 0)
                folderRollups = await ResolveFolderReadRollupsAsync(folderIds, userId, ct);
        }
        if (favoritesOnly)
        {
            var nodeIds = archiveIds.Concat(folderIds).ToList();
            starred = (await _db.Favorites.AsNoTracking().Where(f => f.UserId == userId && nodeIds.Contains(f.CatalogNodeId))
                .Select(f => f.CatalogNodeId).ToListAsync(ct)).ToHashSet();
        }

        bool ArchiveMatches(long id) => readState switch
        {
            BrowseReadStateFilter.Read => read.Contains(id),
            BrowseReadStateFilter.Reading => !read.Contains(id) && inProgress.Contains(id),
            BrowseReadStateFilter.Unread => !read.Contains(id) && !inProgress.Contains(id),
            _ => true,
        };

        bool StackMatches(VolumeEntry entry)
        {
            var ids = MemberIds(entry);
            if (favoritesOnly && !ids.Any(starred.Contains))
                return false;
            if (readState == BrowseReadStateFilter.All)
                return true;
            var rollup = FolderReadRollupRules.Classify(ids.Count, ids.Count(read.Contains), ids.Count(id => !read.Contains(id) && inProgress.Contains(id)));
            return readState switch
            {
                BrowseReadStateFilter.Read => rollup == FolderReadRollup.Read,
                BrowseReadStateFilter.Reading => rollup == FolderReadRollup.Reading,
                _ => rollup is null or FolderReadRollup.Unread,
            };
        }

        return entries.Where(e => e.Kind switch
        {
            VolumeEntryKind.Stack or VolumeEntryKind.CollectionStack => StackMatches(e),
            VolumeEntryKind.Archive => ArchiveMatches(IdOf(e.Row!.Id)) && (!favoritesOnly || starred.Contains(IdOf(e.Row!.Id))),
            VolumeEntryKind.Folder => MatchesFolderReadState(folderRollups, IdOf(e.Row!.Id), readState)
                && (!favoritesOnly || starred.Contains(IdOf(e.Row!.Id))),
            _ => false,
        }).ToList();
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
    /// The browse cards of stacks of stories collected in one volume (1.37.0): <c>Kind = VolumeStack</c> with
    /// <see cref="CatalogNodeDto.CollectionStack"/> (never <see cref="CatalogNodeDto.VolumeStack"/>), the opaque
    /// <c>cs.&lt;folder&gt;.&lt;key&gt;</c> id, the record's title, the record's stored poster where web covers are shown (else the first
    /// story's cover, through the cover resolver), a read rollup and the star over the stories. No completion, volume or missing count.
    /// </summary>
    internal async Task<Dictionary<string, CatalogNodeDto>> BuildCollectionCardsAsync(
        FolderVolumeEntries view, IReadOnlyList<StoryCollection> collections, long userId, CancellationToken ct)
    {
        var cards = new Dictionary<string, CatalogNodeDto>(StringComparer.Ordinal);
        if (collections.Count == 0)
            return cards;

        var heads = await _collectionCovers.ResolveAsync(view.FolderId, view.FolderPublicId, view.LibraryId,
            collections.Where(c => view.CollectionRecords.ContainsKey(c.Key)).ToDictionary(c => c.Key, c => view.CollectionRecords[c.Key], StringComparer.Ordinal),
            ct);
        var fallbackTargets = collections.Where(c => heads.GetValueOrDefault(c.Key)?.Poster is null)
            .Select(c => view.Rows[c.Members[0].Id]).DistinctBy(r => r.InternalId)
            .Select(r => new CoverTarget(r.InternalId, r.Id, false)).ToList();
        var covers = fallbackTargets.Count > 0 ? await _covers.ResolveAsync(fallbackTargets, ct) : new Dictionary<long, ResolvedCover>();

        var memberIds = collections.SelectMany(c => c.Members).Select(m => view.Rows[m.Id].InternalId).Distinct().ToList();
        var read = (await _db.ReadMarks.AsNoTracking().Where(m => m.UserId == userId && memberIds.Contains(m.ItemId)).Select(m => m.ItemId).ToListAsync(ct)).ToHashSet();
        var inProgress = (await _db.ReadingProgress.AsNoTracking()
            .Where(p => p.UserId == userId && memberIds.Contains(p.ItemId) && p.State == (int)ReadingState.InProgress)
            .Select(p => p.ItemId).ToListAsync(ct)).ToHashSet();
        var starred = (await _db.Favorites.AsNoTracking().Where(f => f.UserId == userId && memberIds.Contains(f.CatalogNodeId))
            .Select(f => f.CatalogNodeId).ToListAsync(ct)).ToHashSet();

        foreach (var collection in collections)
        {
            var ids = collection.Members.Select(m => view.Rows[m.Id].InternalId).ToList();
            var first = view.Rows[collection.Members[0].Id];
            var head = heads.GetValueOrDefault(collection.Key);
            ResolvedCover? cover = head?.Poster is { } poster ? new ResolvedCover(poster.Url, CardCoverSource.Poster)
                : covers.TryGetValue(first.InternalId, out var own) ? own : null;
            var title = head?.Title ?? first.DisplayName;
            cards[collection.Key] = new CatalogNodeDto
            {
                Id = CollectionStackId.Encode(view.FolderPublicId, collection.Key),
                ParentId = view.FolderPublicId,
                LibraryId = view.LibraryPublicId,
                Kind = CatalogNodeKind.VolumeStack,
                DisplayName = title,
                Availability = CatalogNodeAvailability.Available,
                CoverUrl = cover?.Url,
                CoverSource = cover?.Source,
                ReadRollup = FolderReadRollupRules.Classify(ids.Count, ids.Count(read.Contains), ids.Count(id => !read.Contains(id) && inProgress.Contains(id))),
                IsFavorite = ids.Any(starred.Contains),
                CollectionStack = new CollectionStackSummaryDto { Key = collection.Key, Title = title, StoryCount = collection.Members.Count },
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
    /// <summary>
    /// 1.34.1 (owner): a volume archive shown as its own card in the Volumes view is titled like the stacks around it - "Volume 3" - not
    /// by its file name (the folder view keeps the file name; opening it reads the archive as before).
    /// </summary>
    private static CatalogNodeDto VolumeTitled(CatalogNodeDto card, VolumeEntry entry) =>
        entry is { Kind: VolumeEntryKind.Archive, Rank: 0, Volume: { } volume }
            ? card with { DisplayName = VolumeGrouping.LabelOf(volume, VolumeStackConfidence.Exact) }
            : card;

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
        Duplicates = stack.Duplicates.Select(DuplicateUnits.ToDto).ToList(),
    };
}
