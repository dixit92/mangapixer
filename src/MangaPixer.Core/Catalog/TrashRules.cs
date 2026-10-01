namespace com.lifepixer.mangapixer.Core.Catalog;

/// <summary>Why the trash keeps a whole library's eligible tombstones this pass (1.31.0). Pure vocabulary.</summary>
public enum TrashHold
{
    None = 0,

    /// <summary>A scan of the library is pending or running: skipped this pass, never released.</summary>
    ScanRunning = 1,

    /// <summary>The library's latest finished scan could not reach its root: released only by the admin, for that library.</summary>
    RootUnavailable = 2,

    /// <summary>More than half of the library's nodes would go (an offline disk or folder): released only by the admin, for that library.</summary>
    Burst = 3,
}

/// <summary>One node the trash may purge: its id, parent, and kind (0 folder, 1 archive).</summary>
public readonly record struct TrashNode(long Id, long? ParentId, int Kind);

/// <summary>A child of an eligible folder (any availability): the folder can only go when every child goes with it.</summary>
public readonly record struct TrashChild(long Id, long ParentId);

/// <summary>
/// What one pass deletes, leaf-first: every archive, then the folders in rounds - each round holds folders whose children are
/// all gone already, so the catalog's parent key (<c>ON DELETE RESTRICT</c>) never sees a parent removed before its child.
/// <see cref="KeptFolders"/> counts eligible folders that stay because a child of theirs stays (a live or held node).
/// </summary>
public sealed record TrashPurgePlan(IReadOnlyList<long> Archives, IReadOnlyList<IReadOnlyList<long>> FolderRounds, int KeptFolders)
{
    public int FolderCount => FolderRounds.Sum(r => r.Count);

    public int NodeCount => Archives.Count + FolderCount;

    public IEnumerable<long> AllNodeIds => Archives.Concat(FolderRounds.SelectMany(r => r));
}

/// <summary>
/// The trash's pure rules (1.31.0): which library holds apply, and in which order eligible tombstones are deleted. Eligibility
/// itself (tombstoned before <see cref="TrashRetention.WindowStart"/>, not held) is a database query in the server.
/// </summary>
public static class TrashRules
{
    /// <summary>The scan error a scan records when it cannot reach the library root.</summary>
    public const string RootUnavailableError = "root_unavailable";

    /// <summary>
    /// The hold on a library, most important first: a running scan, then an unreachable root, then a burst (eligible nodes are
    /// more than half of all the library's nodes, live and tombstoned). <paramref name="latestScanError"/> is the error of the
    /// library's latest finished scan (null when it succeeded or there is none).
    /// </summary>
    public static TrashHold HoldOf(bool scanRunning, string? latestScanError, int eligibleNodes, int libraryNodes)
    {
        if (scanRunning)
            return TrashHold.ScanRunning;
        if (string.Equals(latestScanError, RootUnavailableError, StringComparison.Ordinal))
            return TrashHold.RootUnavailable;
        if (eligibleNodes > 0 && (long)eligibleNodes * 2 > libraryNodes)
            return TrashHold.Burst;
        return TrashHold.None;
    }

    /// <summary>Whether the admin's "Empty trash now" for this one library may release the hold (a running scan never is).</summary>
    public static bool IsReleasable(TrashHold hold) => hold is TrashHold.RootUnavailable or TrashHold.Burst;

    /// <summary>The API / UI code of a hold, or null for none.</summary>
    public static string? CodeOf(TrashHold hold) => hold switch
    {
        TrashHold.ScanRunning => "scan_running",
        TrashHold.RootUnavailable => "root_unavailable",
        TrashHold.Burst => "burst",
        _ => null,
    };

    /// <summary>
    /// Plans the deletion of <paramref name="eligible"/> nodes. <paramref name="childrenOfEligibleFolders"/> lists every child
    /// (any availability) of the eligible folders. A folder goes only when all of its children go in the same pass; that is
    /// resolved to a fixed point, so a kept grandchild keeps every folder above it.
    /// </summary>
    public static TrashPurgePlan Plan(IReadOnlyCollection<TrashNode> eligible, IReadOnlyCollection<TrashChild> childrenOfEligibleFolders)
    {
        var archives = eligible.Where(n => n.Kind == 1).Select(n => n.Id).Distinct().OrderBy(id => id).ToList();
        var purgeable = new HashSet<long>(archives);
        var folders = eligible.Where(n => n.Kind != 1).Select(n => n.Id).ToHashSet();
        purgeable.UnionWith(folders);

        var children = new Dictionary<long, List<long>>();
        foreach (var child in childrenOfEligibleFolders)
        {
            if (!folders.Contains(child.ParentId))
                continue;
            if (!children.TryGetValue(child.ParentId, out var list))
                children[child.ParentId] = list = [];
            if (!list.Contains(child.Id))
                list.Add(child.Id);
        }

        // Fixed point: drop a folder while any child of it stays.
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var folder in folders)
            {
                if (!purgeable.Contains(folder) || !children.TryGetValue(folder, out var list))
                    continue;
                if (list.Any(c => !purgeable.Contains(c)))
                {
                    purgeable.Remove(folder);
                    changed = true;
                }
            }
        }

        var remaining = folders.Where(purgeable.Contains).ToHashSet();
        var keptFolders = folders.Count - remaining.Count;
        var gone = new HashSet<long>(archives);
        var rounds = new List<IReadOnlyList<long>>();
        while (remaining.Count > 0)
        {
            var round = remaining
                .Where(f => !children.TryGetValue(f, out var list) || list.All(gone.Contains))
                .OrderBy(id => id)
                .ToList();
            if (round.Count == 0)
                break; // a cycle cannot happen in a tree; never loop forever on bad data
            rounds.Add(round);
            foreach (var f in round)
            {
                remaining.Remove(f);
                gone.Add(f);
            }
        }
        keptFolders += remaining.Count;

        return new TrashPurgePlan(archives, rounds, keptFolders);
    }
}
