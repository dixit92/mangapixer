namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Covers;

/// <summary>
/// The read surface of stacks of stories collected in one volume besides browse (1.37.0, "tankoubon stacks"): one stack's stories as
/// browse cards. Stored data only; the viewer needs access to the folder's library.
/// </summary>
public sealed class CollectionStackService(VolumeEntryService entries, CatalogBrowseService browse, LibraryAuthorizationService auth,
    CollectionStackCoverService covers)
{
    /// <summary>One stack of stories in a folder and its head, or null (unknown folder / key, no access, no such stack here).</summary>
    public async Task<(FolderVolumeEntries View, StoryCollection Collection, CollectionStackHead? Head)?> FindAsync(
        long userId, long folderId, string key, CancellationToken ct)
    {
        var view = await entries.GetEntriesAsync(folderId, ct);
        if (view is null || view.CollectionStackCount == 0 || !await CanSeeAsync(userId, view.LibraryId, ct))
            return null;
        var collection = view.Entries
            .FirstOrDefault(e => e.Kind == VolumeEntryKind.CollectionStack && string.Equals(e.Collection!.Key, key, StringComparison.Ordinal))
            ?.Collection;
        if (collection is null)
            return null;
        var heads = view.CollectionRecords.TryGetValue(collection.Key, out var recordId)
            ? await covers.ResolveAsync(view.FolderId, view.FolderPublicId, view.LibraryId,
                new Dictionary<string, long>(StringComparer.Ordinal) { [collection.Key] = recordId }, ct)
            : new Dictionary<string, CollectionStackHead>();
        return (view, collection, heads.GetValueOrDefault(collection.Key));
    }

    /// <summary>One stack of stories of a folder, or null (unknown folder / key, no access, nothing stacks here).</summary>
    public async Task<CollectionStackDto?> GetStackAsync(long userId, long folderId, string key, CancellationToken ct)
    {
        if (await FindAsync(userId, folderId, key, ct) is not { } found)
            return null;
        var (view, collection, head) = found;

        var rows = collection.Members.Select(m => view.Rows[m.Id]).ToList();
        var cards = (await browse.EnrichAsync(rows, userId, view.LibraryId, ct)).ToDictionary(n => n.Id, StringComparer.Ordinal);
        var items = collection.Members.Select(m => cards[m.Id]).ToList();
        var keys = StoryCollectionGrouping.CollectionKeys(view.Entries);
        var index = keys.ToList().IndexOf(collection.Key);
        return new CollectionStackDto
        {
            FolderId = view.FolderPublicId,
            Key = collection.Key,
            Title = head?.Title ?? rows[0].DisplayName,
            CoverUrl = head?.Poster?.Url ?? items[0].CoverUrl,
            StoryCount = items.Count,
            Items = items,
            PreviousKey = index > 0 ? keys[index - 1] : null,
            NextKey = index >= 0 && index < keys.Count - 1 ? keys[index + 1] : null,
        };
    }

    private async Task<bool> CanSeeAsync(long userId, long libraryId, CancellationToken ct) =>
        (await auth.GetVisibleLibraryIdsAsync(userId, incognito: false, ct)).Contains(libraryId);
}
