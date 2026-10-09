namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;

/// <summary>
/// The Volumes view's read surface besides browse (1.29.0): whether a folder has a Volumes view and whether it is on for the
/// viewer, and one stack's slots (present chapter cards plus a placeholder where a whole chapter is missing). Stored data
/// only; the viewer needs access to the folder's library.
/// </summary>
public sealed class VolumeStackService(VolumeEntryService entries, CatalogBrowseService browse, LibraryAuthorizationService auth,
    StackCoverService stackCovers, VolumeCoverPass? coverPass = null)
{
    /// <summary>The Volumes-view state of a folder, or null when the viewer cannot see it.</summary>
    public async Task<VolumeViewDto?> GetViewAsync(long userId, long folderId, CancellationToken ct)
    {
        var view = await entries.GetEntriesAsync(folderId, ct);
        if (view is null || !await CanSeeAsync(userId, view.LibraryId, ct))
            return null;
        // Covers still on their way for this series (volume 1 and the volumes held here): the view shows a short note.
        var coversPending = view.Status?.RecordId is { } recordId && coverPass is not null
            // 1.32.0: a folder under "File covers" fetches none, so none is on its way.
            && !await coverPass.SkipsCoverDownloadsAsync(recordId, [view.FolderId], ct)
            // The pass's own plan (1.30.0): a volume file it skips (volume 1's own cover matches the web one) is never "on its way".
            ? await coverPass.PendingCoversAsync(recordId, await coverPass.PlannedHeldVolumesAsync(recordId, [view.FolderId], ct), ct)
            : 0;
        return new VolumeViewDto
        {
            NodeId = view.FolderPublicId,
            Available = view.Available,
            Active = view.Available && await entries.IsActiveAsync(userId, view, null, ct),
            DefaultActive = view.Available && await entries.IsDefaultActiveAsync(view, ct),
            Consolidated = view.Consolidated,
            StackCount = view.StackCount,
            CollectionStackCount = view.CollectionStackCount,
            ChaptersOnly = view.ChaptersOnly,
            HasSeriesStatus = view.Status is not null,
            SeriesStatus = view.Status?.Status,
            MissingVolumes = view.Status?.MissingVolumes ?? 0,
            MissingChapters = view.Status?.MissingChapters ?? 0,
            ReleaseKnown = view.Status?.ReleaseKnown ?? false,
            Language = view.Status?.Language,
            Origin = view.Status?.Origin,
            OriginVolumes = view.Status?.OriginVolumes,
            ReleasedVolumes = view.Status?.ReleasedVolumes,
            ReleasedChapter = view.Status?.ReleasedChapter,
            Licensed = view.Status?.Licensed,
            ScanlationComplete = view.Status?.ScanlationComplete,
            CoversPending = coversPending,
            Progress = view.Status?.Progress,
        };
    }

    /// <summary>One stack of a folder, or null (unknown folder / key, no access, nothing groups here).</summary>
    public async Task<VolumeStackDto?> GetStackAsync(long userId, long folderId, string key, CancellationToken ct)
    {
        var view = await entries.GetEntriesAsync(folderId, ct);
        if (view is null || !view.Available || !await CanSeeAsync(userId, view.LibraryId, ct))
            return null;
        var stackEntry = view.Entries.FirstOrDefault(e => e.Kind == VolumeEntryKind.Stack && string.Equals(e.Stack!.Key, key, StringComparison.Ordinal));
        if (stackEntry?.Stack is not { } stack)
            return null;

        var rows = stack.Members.Select(m => view.Rows[m.Row.Id]).ToList();
        var cards = (await browse.EnrichAsync(rows, userId, view.LibraryId, ct)).ToDictionary(n => n.Id, StringComparer.Ordinal);
        var slots = VolumeGrouping.Slots(stack)
            .Select(s => new VolumeSlotDto
            {
                Kind = s.Kind,
                Chapter = s.Chapter,
                Item = s.Member is { } member ? WithAlsoInVolume(cards[member.Row.Id], view) : null,
            })
            .ToList();

        // The header cover: the volume's stored web cover for a chapter-only stack (as its browse card), else the first member's.
        var web = await stackCovers.ResolveAsync(view.FolderId, view.FolderPublicId, view.LibraryId, [stack], ct);
        var keys = VolumeGrouping.StackKeys(view.Entries);
        var index = keys.ToList().IndexOf(stack.Key);
        return new VolumeStackDto
        {
            FolderId = view.FolderPublicId,
            Key = stack.Key,
            Label = stack.Label,
            CoverUrl = web.TryGetValue(stack.Key, out var webCover) ? webCover.Url : cards[stack.Members[0].Row.Id].CoverUrl,
            Confidence = stack.Confidence,
            Source = stack.Source,
            ListCredit = view.Status?.Progress?.ListCredit,
            PresentCount = stack.PresentCount,
            ChapterCount = stack.ChapterCount,
            ChaptersPresent = stack.ChaptersPresent,
            HasVolumeArchive = stack.HasVolumeArchive,
            MissingCount = stack.MissingChapters.Count,
            ExtraCount = stack.ExtraCount,
            PreviousKey = index > 0 ? keys[index - 1] : null,
            NextKey = index >= 0 && index < keys.Count - 1 ? keys[index + 1] : null,
            Slots = slots,
            OfficialRelease = stack.OfficialRelease,
            Duplicates = stack.Duplicates.Select(DuplicateUnits.ToDto).ToList(),
        };
    }

    /// <summary>A chapter card that a volume file of the series already holds says so (1.30.0, "Also in Volume 10").</summary>
    internal static CatalogNodeDto WithAlsoInVolume(CatalogNodeDto card, FolderVolumeEntries view) =>
        view.AlsoInVolume.TryGetValue(card.Id, out var volume) ? card with { AlsoInVolume = volume } : card;

    private async Task<bool> CanSeeAsync(long userId, long libraryId, CancellationToken ct) =>
        (await auth.GetVisibleLibraryIdsAsync(userId, incognito: false, ct)).Contains(libraryId);
}
