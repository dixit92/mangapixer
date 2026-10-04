namespace com.lifepixer.mangapixer.Core.Metadata;

/// <summary>
/// When a stored volume list counts as one (1.34.0, owner 2026-10-04). PURE: the server applies these rules where it reads the stored
/// maps (<c>VolumeMapService.IsUsable</c>, <c>SeriesProgressLoader.MapAndFacts</c>), so every reader agrees.
/// </summary>
public static class VolumeListRules
{
    /// <summary>A series has a REAL volume list with at least this many real MangaDex volumes (or a Wikipedia list).</summary>
    public const int MinRealVolumes = 2;

    /// <summary>The volumes of a list numbered 1 or more (<c>"0"</c> and <c>"none"</c> are not volumes).</summary>
    public static int RealVolumeCount(IEnumerable<VolumeMapEntry> volumes)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        return volumes.Count(v => v.Chapters.Count > 0 && VolumeMapJson.Parse(v.Volume) is >= 1);
    }

    /// <summary>
    /// A near-empty list ("no volume list" everywhere it is read): at most one real volume, and more chapters left unassigned than
    /// placed under any key (a webtoon's MangaDex list: volumes "0" and "1" with one chapter each, 272 chapters unassigned).
    /// </summary>
    public static bool IsNearEmpty(IReadOnlyList<VolumeMapEntry> volumes, IReadOnlyList<string> unassigned)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(unassigned);
        var placed = volumes.Sum(v => v.Chapters.Count);
        return RealVolumeCount(volumes) <= 1 && unassigned.Count > placed;
    }

    /// <summary>
    /// The Volumes view lists the series' CHAPTERS, never volumes from a list (owner, 2026-10-04): its record is a webtoon, or a manhwa /
    /// manhua - and it has no real list (<paramref name="hasRealList"/>: at least <see cref="MinRealVolumes"/> real MangaDex volumes, or a
    /// Wikipedia list). No list-based or estimated stacks, no missing-volume placeholders; volumes the file names state still group.
    /// </summary>
    public static bool ChaptersOnly(bool? webtoon, MetadataOrigin? origin, bool hasRealList) =>
        !hasRealList && (webtoon == true || origin is MetadataOrigin.Korea or MetadataOrigin.ChinaTaiwan);
}
