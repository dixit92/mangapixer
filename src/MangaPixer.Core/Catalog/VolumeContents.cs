namespace com.lifepixer.mangapixer.Core.Catalog;

/// <summary>
/// One volume file's stored scan answer for a folder's rows (1.44.0): its row id, its volume number, the state and the chapter rows
/// proven inside it (row id + chapter number). A row id is the public node id the readers use for <see cref="GroupingRow.Id"/>.
/// </summary>
public sealed record ScannedVolume(string VolumeRowId, decimal Volume, VolumeContentState State, IReadOnlyList<ScannedChapter> Chapters);

/// <summary>A chapter file proven inside a volume file.</summary>
public sealed record ScannedChapter(string RowId, decimal? Number);

/// <summary>
/// The volume contents scan as every reader of a folder's rows sees it (1.44.0) - the Volumes view, progress, the Missing report, the
/// export and the duplicate counts call this right after loading the rows and the map, like <see cref="VolumeGrouping.WithSeriesHeads"/>,
/// so they all read the same answer. PURE.
/// </summary>
public static class VolumeContents
{
    /// <summary>
    /// (a) Every chapter row proven inside a volume file carries that volume (<see cref="GroupingRow.InVolumeFile"/>): it sits in that
    /// volume's stack (<see cref="VolumePlacement.Scanned"/>). (b) A <see cref="VolumeContentState.Known"/> volume joins a map that
    /// already places chapters (a linked series' list or ratio) as an exact row - its proven chapters - so it is no longer estimated
    /// and bounds its neighbours; a volume the map already lists is never changed (the list stays the authority). A
    /// <see cref="VolumeContentState.Partial"/> volume adds its proven members only. A folder without a map gets (a) only: nothing is
    /// estimated there. The map is the series' own; the scan is per FOLDER (the caller passes the scans of this folder's files).
    /// </summary>
    public static (IReadOnlyList<GroupingRow> Rows, VolumeMapInput? Map) Apply(
        IReadOnlyList<GroupingRow> rows, VolumeMapInput? map, IReadOnlyList<ScannedVolume> scans)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(scans);
        if (scans.Count == 0)
            return (rows, map);

        var volumeOf = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var scan in scans)
        {
            foreach (var chapter in scan.Chapters)
                volumeOf.TryAdd(chapter.RowId, scan.Volume);
        }
        var applied = volumeOf.Count == 0
            ? rows
            : rows.Select(r => r.Kind == GroupingRowKind.Archive && volumeOf.TryGetValue(r.Id, out var v) ? r with { InVolumeFile = v } : r).ToList();

        if (map is null || !map.HasData)
            return (applied, map);
        var listed = map.Volumes.Select(v => v.Volume).ToHashSet();
        var known = scans
            .Where(s => s.State == VolumeContentState.Known && !listed.Contains(s.Volume))
            .GroupBy(s => s.Volume)
            .Select(g => new VolumeMapVolume(g.Key,
                g.SelectMany(s => s.Chapters).Select(c => c.Number).OfType<decimal>().Distinct().Order().ToList()))
            .Where(v => v.Chapters.Count > 0)
            .ToList();
        if (known.Count == 0)
            return (applied, map);
        return (applied, map with { Volumes = map.Volumes.Concat(known).OrderBy(v => v.Volume).ToList() });
    }
}
