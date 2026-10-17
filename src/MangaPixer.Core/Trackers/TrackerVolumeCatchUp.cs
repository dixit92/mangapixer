namespace com.lifepixer.mangapixer.Core.Trackers;

using com.lifepixer.mangapixer.Core.Catalog;

/// <summary>
/// 1.44.0 (owner, 2026-10-17: MyAnimeList at 271 chapters / 25 volumes, while 271 chapters finish volume 27): the volume count a tracker's OWN
/// chapter count completes on the series' exact volume list (MangaDex / Wikipedia), when it is above the tracker's volume count - offered to
/// the user as "Set &lt;tracker&gt;'s volumes to N" (sent only on their click). PURE.
/// <list type="bullet">
/// <item>Only an entry the user is reading or plans to read: never one on hold, dropped, completed or re-read (left as the user put it).</item>
/// <item>Only an exact list - never an estimate or the AniList ratio; a volume counts when its last whole chapter (above 0) is at or below
/// the tracker's chapters; a volume the list gives no whole chapter never counts.</item>
/// <item>Raise only; never above the tracker's own volume total when it has one (the tracker knows fewer volumes: left alone).</item>
/// </list>
/// The local edition plays no part: the tracker's numbers are the standard edition's, and so is the exact list.
/// </summary>
public static class TrackerVolumeCatchUp
{
    public static int? Offer(TrackerEntryStatus? status, int? chapters, int? volumes, int? totalVolumes, VolumeMapInput? map)
    {
        if (status is not (TrackerEntryStatus.Reading or TrackerEntryStatus.PlanToRead))
            return null;
        if (chapters is not > 0 || map is null || !TrackerReadOn.IsExact(map))
            return null;
        int? best = null;
        foreach (var v in map.Volumes)
        {
            if (decimal.Truncate(v.Volume) != v.Volume || v.Volume < 1 || v.Volume > 100_000)
                continue;
            var wholes = v.Chapters.Where(c => decimal.Truncate(c) == c && c >= 1).ToList();
            if (wholes.Count > 0 && wholes.Max() <= chapters.Value)
                best = Math.Max(best ?? 0, (int)v.Volume);
        }
        if (best is not { } offer || offer <= (volumes ?? 0))
            return null;
        if (totalVolumes is > 0 and var total && offer > total)
            return null;
        return offer;
    }

    /// <summary>The offer for a tracker entry as just read.</summary>
    public static int? Offer(TrackerEntry? remote, VolumeMapInput? map) =>
        remote is null ? null : Offer(remote.Status, remote.ChaptersRead, remote.VolumesRead, remote.TotalVolumes, map);
}
