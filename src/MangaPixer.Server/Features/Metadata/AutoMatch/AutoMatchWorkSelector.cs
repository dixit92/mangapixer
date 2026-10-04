namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

/// <summary>A work the matcher should look up: a folder, or an archive group of a collection folder.</summary>
public sealed record DetectedWork(
    long AnchorNodeId,
    long FolderId,
    MatchLevel Level,
    WorkClass Class,
    IReadOnlyList<long> MemberNodeIds);

/// <summary>
/// Chooses the works to queue from a library tree (stage 2, section 1 hard
/// exclusions + owner decision 6). Pure: the tree snapshot, the own link rows and
/// the detector are its only inputs. Walks top-down so the HIGHEST candidate wins:
/// below a folder that is (or has a link as) a work, nothing else is queued -
/// unit subfolders and chapters inherit. Rules:
/// - a node with its own link row is never queued; what the row means below it is
///   <see cref="SeriesLinkStates.CoverBelow"/> (1.34.0, owner: the nearest decision wins):
///   Don't match and Needs review block the whole subtree; a linked series covers it
///   but is still walked (queuing nothing) for "Collection about" folders inside, which
///   re-open matching; a collection is classified (the detector makes it an archive-level
///   collection) and its subtree is walked normally;
/// - Folder / ReviewOnly levels queue the folder itself and cover its subtree;
/// - Archive level queues one work per archive group (anchor = its first archive
///   without an own link; the rest are members), and never auto-links a class the
///   detector made review-only;
/// - None (containers, wrappers, unit subfolders, exclusions) queues nothing and
///   descends into the children.
/// </summary>
public static class AutoMatchWorkSelector
{
    public static IReadOnlyList<DetectedWork> Select(
        LibraryTreeSnapshot tree,
        IWorkDetector detector,
        IReadOnlyDictionary<long, SeriesLinkState> ownLinks,
        IReadOnlySet<long>? scopeFolderIds = null) =>
        Walk(tree, detector, ownLinks, scopeFolderIds, tree.Roots.Where(r => r.IsFolder));

    /// <summary>
    /// 1.34.0: the works at or below one folder, walked as if nothing above it covered it (the caller checked the ancestors, e.g. a
    /// folder an admin just marked "Collection about").
    /// </summary>
    public static IReadOnlyList<DetectedWork> SelectBelow(
        LibraryTreeSnapshot tree, IWorkDetector detector, IReadOnlyDictionary<long, SeriesLinkState> ownLinks, long folderId) =>
        tree.Find(folderId) is { IsFolder: true } folder ? Walk(tree, detector, ownLinks, null, [folder]) : [];

    private static List<DetectedWork> Walk(
        LibraryTreeSnapshot tree,
        IWorkDetector detector,
        IReadOnlyDictionary<long, SeriesLinkState> ownLinks,
        IReadOnlySet<long>? scopeFolderIds,
        IEnumerable<LibraryTreeSnapshot.Node> start)
    {
        var works = new List<DetectedWork>();
        // Covered = a linked series above speaks for this folder: only a collection inside re-opens matching.
        var stack = new Stack<(LibraryTreeSnapshot.Node Folder, bool Covered)>(start.Reverse().Select(f => (f, false)));
        while (stack.Count > 0)
        {
            var (folder, covered) = stack.Pop();
            if (ownLinks.TryGetValue(folder.Id, out var state))
            {
                switch (SeriesLinkStates.CoverBelow(state))
                {
                    case MatchingCoverKind.Blocks:
                        continue; // Don't match / in review: nothing below.
                    case MatchingCoverKind.Covers:
                        PushChildren(stack, tree, folder.Id, covered: true);
                        continue; // Linked: not a work again, but a collection inside re-opens.
                    case MatchingCoverKind.Opens:
                        covered = false;
                        break; // A collection: classified below (archive level) and walked.
                }
            }
            else if (covered)
            {
                PushChildren(stack, tree, folder.Id, covered: true);
                continue;
            }

            var inScope = scopeFolderIds is null || scopeFolderIds.Contains(folder.Id);
            var classification = detector.Classify(tree.ShapeOf(folder.Id));
            switch (classification.Level)
            {
                case MatchLevel.Folder:
                case MatchLevel.ReviewOnly:
                    if (inScope && !ownLinks.ContainsKey(folder.Id))
                    {
                        works.Add(new DetectedWork(folder.Id, folder.Id, classification.Level, classification.Class, []));
                    }
                    continue; // Highest wins: the subtree inherits.

                case MatchLevel.Archive:
                    if (inScope)
                        works.AddRange(ArchiveWorks(tree, folder.Id, classification, ownLinks));
                    break;
            }

            PushChildren(stack, tree, folder.Id, covered: false);
        }
        return works;
    }

    private static void PushChildren(Stack<(LibraryTreeSnapshot.Node, bool)> stack, LibraryTreeSnapshot tree, long folderId, bool covered)
    {
        var children = tree.ChildFolders(folderId).ToList();
        for (var i = children.Count - 1; i >= 0; i--)
            stack.Push((children[i], covered));
    }

    /// <summary>One work per archive group; archives with an own link row stay out of every group.</summary>
    public static IEnumerable<DetectedWork> ArchiveWorks(
        LibraryTreeSnapshot tree, long folderId, WorkClassification classification, IReadOnlyDictionary<long, SeriesLinkState> ownLinks)
    {
        var archives = tree.ChildArchives(folderId);
        foreach (var group in classification.ArchiveGroups)
        {
            var ids = group.ArchiveIndexes
                .Where(i => i >= 0 && i < archives.Count)
                .Distinct()
                .Order()
                .Select(i => archives[i].Id)
                .Where(id => !ownLinks.ContainsKey(id))
                .ToList();
            if (ids.Count == 0)
                continue;
            yield return new DetectedWork(ids[0], folderId, MatchLevel.Archive, classification.Class, ids.Skip(1).ToList());
        }
    }
}
