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
/// - a node with its own link row (any state, incl. Don't match) is never queued,
///   and its subtree is covered by it (Needs review counts: the work already exists);
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
        IReadOnlySet<long>? scopeFolderIds = null)
    {
        var works = new List<DetectedWork>();
        var stack = new Stack<LibraryTreeSnapshot.Node>(tree.Roots.Where(r => r.IsFolder).Reverse());
        while (stack.Count > 0)
        {
            var folder = stack.Pop();
            if (ownLinks.ContainsKey(folder.Id))
                continue; // Linked, Don't match or already in review: covers the subtree.

            var inScope = scopeFolderIds is null || scopeFolderIds.Contains(folder.Id);
            var classification = detector.Classify(tree.ShapeOf(folder.Id));
            switch (classification.Level)
            {
                case MatchLevel.Folder:
                case MatchLevel.ReviewOnly:
                    if (inScope)
                    {
                        works.Add(new DetectedWork(folder.Id, folder.Id, classification.Level, classification.Class, []));
                    }
                    continue; // Highest wins: the subtree inherits.

                case MatchLevel.Archive:
                    if (inScope)
                        works.AddRange(ArchiveWorks(tree, folder.Id, classification, ownLinks));
                    break;
            }

            var children = tree.ChildFolders(folder.Id).ToList();
            for (var i = children.Count - 1; i >= 0; i--)
                stack.Push(children[i]);
        }
        return works;
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
