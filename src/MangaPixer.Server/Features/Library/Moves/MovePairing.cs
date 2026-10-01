namespace com.lifepixer.mangapixer.Server.Features.Library.Moves;

using com.lifepixer.mangapixer.Core.Metadata;

/// <summary>A tombstoned archive that may have moved: its content signature and when its library last saw it present.</summary>
public sealed record MoveOldCandidate(long NodeId, long LibraryId, long ByteLength, string Signature, DateTimeOffset LastSeenAt);

/// <summary>
/// A live archive that may be the moved copy. <see cref="Signature"/> is null while it waits for analysis (it then only blocks
/// the decision for an old archive of the same size).
/// </summary>
public sealed record MoveNewCandidate(long NodeId, long LibraryId, long ByteLength, string? Signature, DateTimeOffset CreatedAt);

public enum MovePairOutcome
{
    Paired,

    /// <summary>More than one old or more than one new archive share the signature.</summary>
    Ambiguous,

    /// <summary>A live archive of the same size, newer than the old one's last sighting, still waits for analysis.</summary>
    Waiting,

    NoPartner,
}

public readonly record struct MovePairDecision(MoveOldCandidate Old, MovePairOutcome Outcome, MoveNewCandidate? New);

/// <summary>
/// The pure rules of cross-library move recognition after the fact (1.31.0): which tombstoned archive is which live archive.
/// Evidence: the content signature (size + SHA-256 of the first and last 64 KiB), exactly one old and one new archive per
/// signature, and the new copy appeared only after the old one was last seen present - two copies the catalog ever saw side
/// by side stay separate items. Names, sizes alone and timestamps are never evidence.
/// </summary>
public static class MovePairing
{
    /// <summary>
    /// Decides every old candidate. <paramref name="news"/> holds the live archives that are not already the target of a move:
    /// analysed ones carrying one of the old signatures, and same-size ones still waiting for analysis (null signature).
    /// </summary>
    public static IReadOnlyList<MovePairDecision> Decide(IReadOnlyList<MoveOldCandidate> olds, IReadOnlyList<MoveNewCandidate> news)
    {
        var oldCountBySignature = olds.GroupBy(o => o.Signature, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var newsBySignature = news.Where(n => n.Signature is not null)
            .GroupBy(n => n.Signature!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var pendingBySize = news.Where(n => n.Signature is null)
            .GroupBy(n => n.ByteLength)
            .ToDictionary(g => g.Key, g => g.ToList());

        var decisions = new List<MovePairDecision>(olds.Count);
        foreach (var old in olds)
        {
            if (oldCountBySignature[old.Signature] != 1)
            {
                decisions.Add(new(old, MovePairOutcome.Ambiguous, null));
                continue;
            }
            if (pendingBySize.TryGetValue(old.ByteLength, out var pending) && pending.Any(p => p.CreatedAt > old.LastSeenAt))
            {
                decisions.Add(new(old, MovePairOutcome.Waiting, null));
                continue;
            }
            var eligible = newsBySignature.TryGetValue(old.Signature, out var list)
                ? list.Where(n => n.ByteLength == old.ByteLength && n.CreatedAt > old.LastSeenAt && n.NodeId != old.NodeId).ToList()
                : [];
            decisions.Add(eligible.Count switch
            {
                0 => new(old, MovePairOutcome.NoPartner, null),
                1 => new(old, MovePairOutcome.Paired, eligible[0]),
                _ => new(old, MovePairOutcome.Ambiguous, null),
            });
        }
        return decisions;
    }
}

/// <summary>One page of a stored manifest, as the move rules compare it.</summary>
public readonly record struct ManifestPage(int Ordinal, string EntryKey, string Locator, long ByteSize);

/// <summary>
/// Page-manifest agreement, the second check of a move recognised after the fact (1.31.0): both copies list the same entries
/// with the same sizes. Order-independent on purpose - the page order is an analyser rule, the entry names and sizes are the
/// archive's. For RAR, whose content signature covers only the first and last entries, this adds every entry's name and size.
/// Positions then map by entry: old entry key -> old page's entry -> the new page holding that entry.
/// </summary>
public static class ManifestAgreement
{
    public static bool Agree(IReadOnlyList<ManifestPage> oldPages, IReadOnlyList<ManifestPage> newPages)
    {
        if (oldPages.Count == 0 || oldPages.Count != newPages.Count)
            return false;
        var counts = new Dictionary<(string, long), int>();
        foreach (var p in oldPages)
            counts[(p.Locator, p.ByteSize)] = counts.GetValueOrDefault((p.Locator, p.ByteSize)) + 1;
        foreach (var p in newPages)
        {
            var key = (p.Locator, p.ByteSize);
            if (!counts.TryGetValue(key, out var c) || c == 0)
                return false;
            counts[key] = c - 1;
        }
        return true;
    }

    /// <summary>True when both copies list the same entry at every ordinal (an ordered layout such as a spread pairing carries over).</summary>
    public static bool SameOrder(IReadOnlyList<ManifestPage> oldPages, IReadOnlyList<ManifestPage> newPages) =>
        oldPages.Count == newPages.Count
        && oldPages.OrderBy(p => p.Ordinal).Zip(newPages.OrderBy(p => p.Ordinal))
            .All(x => x.First.Locator == x.Second.Locator && x.First.ByteSize == x.Second.ByteSize);

    /// <summary>Old entry key -> the new page holding the same entry. Repeated entry names map in order of occurrence.</summary>
    public static IReadOnlyDictionary<string, ManifestPage> MapByEntry(IReadOnlyList<ManifestPage> oldPages, IReadOnlyList<ManifestPage> newPages)
    {
        var queues = newPages.OrderBy(p => p.Ordinal)
            .GroupBy(p => (p.Locator, p.ByteSize))
            .ToDictionary(g => g.Key, g => new Queue<ManifestPage>(g));
        var map = new Dictionary<string, ManifestPage>(StringComparer.Ordinal);
        foreach (var p in oldPages.OrderBy(p => p.Ordinal))
            if (queues.TryGetValue((p.Locator, p.ByteSize), out var q) && q.Count > 0)
                map[p.EntryKey] = q.Dequeue();
        return map;
    }
}

/// <summary>A series link as the move rules see it.</summary>
public readonly record struct MoveLinkSnapshot(SeriesLinkState State, long? RecordId);

public enum MoveLinkOutcome
{
    /// <summary>Nothing to do (the old node has no link).</summary>
    Nothing,

    /// <summary>The new node has no link: the old link moves to it.</summary>
    MoveOld,

    /// <summary>The old link was an admin decision and the new node only has a suggestion (Needs review): the old link replaces it.</summary>
    ReplaceNewWithOld,

    /// <summary>The new node is Auto to the same record the old one was Confirmed to: it becomes Confirmed.</summary>
    PromoteNew,

    /// <summary>The old link is superseded (equal, a suggestion, or only an automatic result): it is deleted.</summary>
    DropOld,

    /// <summary>The old link was an admin decision and the new node holds a different one: the admin chooses.</summary>
    Conflict,
}

/// <summary>Link rules of a move (1.31.0), for folder links and archive-work links alike. Pure.</summary>
public static class MoveLinkRules
{
    public static MoveLinkOutcome Decide(MoveLinkSnapshot? old, MoveLinkSnapshot? @new)
    {
        if (old is not { } o)
            return MoveLinkOutcome.Nothing;
        if (o.State == SeriesLinkState.NeedsReview)
            return MoveLinkOutcome.DropOld;
        if (@new is not { } n)
            return MoveLinkOutcome.MoveOld;
        if (o.State == n.State && o.RecordId == n.RecordId)
            return MoveLinkOutcome.DropOld;
        var oldIsAdmin = o.State is SeriesLinkState.Confirmed or SeriesLinkState.DontMatch;
        if (!oldIsAdmin)
            return MoveLinkOutcome.DropOld;
        if (n.State == SeriesLinkState.NeedsReview)
            return MoveLinkOutcome.ReplaceNewWithOld;
        if (o.State == SeriesLinkState.Confirmed && n.State == SeriesLinkState.Auto && o.RecordId == n.RecordId)
            return MoveLinkOutcome.PromoteNew;
        return MoveLinkOutcome.Conflict;
    }
}
