namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Retires automatic work below a folder that now speaks for its subtree (1.26.1, owner review): a
/// folder-level work was decided, or an admin linked the folder or marked it Don't match. The
/// archives inside inherit from it, so their own open automatic results - Needs-review rows with
/// their stored candidates, and finished queue rows (Unmatched, failed, skipped) - are removed and
/// drop out of the review lists. Admin decisions below (Confirmed, Auto, Don't match links) stay.
/// Pending or leased rows stay too: the worker skips them once an ancestor is linked or in review.
/// 1.34.0: a linked series (Confirmed / Auto) does not speak for a "Collection about" folder inside it - the nearest decision wins, so the
/// collection's subtree keeps its works. A Don't match or a waiting folder speaks for everything below, collections included.
/// </summary>
public static class CoveredWorkRetirement
{
    /// <summary>Returns the number of rows removed (review links plus queue rows).</summary>
    public static async Task<int> RetireBelowAsync(MangaPixerDbContext db, LibraryTreeSnapshot tree, long folderId, CancellationToken ct)
    {
        var own = await db.NodeSeriesLinks.AsNoTracking().Where(l => l.NodeId == folderId).Select(l => (int?)l.State).FirstOrDefaultAsync(ct);
        var stopAtCollections = own is { } state && SeriesLinkStates.CoverBelow((SeriesLinkState)state) == MatchingCoverKind.Covers;

        var below = new List<long>();
        var stack = new Stack<long>([folderId]);
        while (stack.Count > 0)
        {
            foreach (var child in tree.ChildrenOf(stack.Pop()))
            {
                if (stopAtCollections && child.IsFolder && tree.Collections.ContainsKey(child.Id))
                    continue; // The collection and its works answer for themselves.
                below.Add(child.Id);
                if (child.IsFolder)
                    stack.Push(child.Id);
            }
        }

        var needsReview = (int)SeriesLinkState.NeedsReview;
        var retired = 0;
        foreach (var chunk in below.Chunk(500))
        {
            var ids = chunk.ToList();
            var inReview = await db.NodeSeriesLinks
                .Where(l => ids.Contains(l.NodeId) && l.State == needsReview)
                .Select(l => l.NodeId)
                .ToListAsync(ct);
            if (inReview.Count > 0)
            {
                await db.MetadataMatchCandidates.Where(c => inReview.Contains(c.NodeId)).ExecuteDeleteAsync(ct);
                retired += await db.NodeSeriesLinks.Where(l => inReview.Contains(l.NodeId) && l.State == needsReview).ExecuteDeleteAsync(ct);
            }
            retired += await db.MetadataMatchQueue
                .Where(q => ids.Contains(q.NodeId)
                    && (q.State == QueueState.Done || q.State == QueueState.Failed || q.State == QueueState.Skipped || q.State == QueueState.Cancelled)
                    && !db.NodeSeriesLinks.Any(l => l.NodeId == q.NodeId))
                .ExecuteDeleteAsync(ct);
        }
        return retired;
    }
}
