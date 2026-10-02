namespace com.lifepixer.mangapixer.Core.Metadata.Wikipedia;

/// <summary>
/// The precedence between MangaDex's exact volume list and a validated Wikipedia list (1.32.0, design 6.5). Pure.
/// <list type="bullet">
/// <item>MangaDex wins wherever it places a chapter: Wikipedia never moves a chapter MangaDex put in a volume.</item>
/// <item>Wikipedia FILLS: the volumes MangaDex lacks, and the chapters MangaDex leaves in its "none" bucket (or dropped as noise) -
/// the stored MangaDex <c>Unassigned</c> list - or does not list at all.</item>
/// <item>A chapter Wikipedia places is no longer "unassigned".</item>
/// <item>Without a MangaDex list (none, empty, or only an AniList ratio) the Wikipedia list is the list.</item>
/// </list>
/// </summary>
public static class VolumeListMerge
{
    /// <summary>The merged list, what is still unassigned, and which chapters Wikipedia placed (for the source line).</summary>
    public sealed record Result(
        IReadOnlyList<VolumeMapEntry> Volumes, IReadOnlyList<string> Unassigned, int ChaptersFromWikipedia, int VolumesFromWikipedia)
    {
        public bool WikipediaFilled => ChaptersFromWikipedia > 0;
    }

    /// <summary>Merges the MangaDex list (<paramref name="mangaDex"/>, may be empty) with the Wikipedia one (<paramref name="wikipedia"/>).</summary>
    public static Result Merge(
        IReadOnlyList<VolumeMapEntry> mangaDex, IReadOnlyList<string> mangaDexUnassigned, IReadOnlyList<VolumeMapEntry> wikipedia)
    {
        ArgumentNullException.ThrowIfNull(mangaDex);
        ArgumentNullException.ThrowIfNull(mangaDexUnassigned);
        ArgumentNullException.ThrowIfNull(wikipedia);

        var volumes = new SortedDictionary<decimal, (string Key, SortedSet<decimal> Chapters)>();
        var placed = new HashSet<decimal>();
        foreach (var volume in mangaDex)
        {
            if (VolumeMapJson.Parse(volume.Volume) is not { } number)
                continue;
            if (!volumes.TryGetValue(number, out var entry))
                volumes[number] = entry = (volume.Volume, []);
            foreach (var chapter in volume.Chapters.Select(c => VolumeMapJson.Parse(c)).OfType<decimal>())
            {
                entry.Chapters.Add(chapter);
                placed.Add(chapter);
            }
        }

        var existingVolumes = volumes.Keys.ToHashSet();
        var filled = new HashSet<decimal>();
        var filledVolumes = new HashSet<decimal>();
        foreach (var volume in wikipedia)
        {
            if (VolumeMapJson.Parse(volume.Volume) is not { } number)
                continue;
            foreach (var chapter in volume.Chapters.Select(c => VolumeMapJson.Parse(c)).OfType<decimal>())
            {
                if (placed.Contains(chapter))
                    continue; // MangaDex placed it: MangaDex wins
                if (!volumes.TryGetValue(number, out var entry))
                    volumes[number] = entry = (VolumeMapJson.Canonical(number), []);
                entry.Chapters.Add(chapter);
                placed.Add(chapter);
                filled.Add(chapter);
                if (!existingVolumes.Contains(number))
                    filledVolumes.Add(number);
            }
        }

        var unassigned = mangaDexUnassigned
            .Select(c => (Text: c, Number: VolumeMapJson.Parse(c)))
            .Where(c => c.Number is not { } n || !filled.Contains(n))
            .Select(c => c.Text)
            .ToList();
        var merged = volumes.Values
            .Where(v => v.Chapters.Count > 0)
            .Select(v => new VolumeMapEntry(v.Key, v.Chapters.Select(VolumeMapJson.Canonical).ToList()))
            .ToList();
        return new Result(merged, unassigned, filled.Count, filledVolumes.Count);
    }
}
