namespace com.lifepixer.mangapixer.Core.Metadata;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>One volume of a stored volume -> chapters map: the canonical volume number and its chapters, ascending.</summary>
public sealed record VolumeMapEntry(string Volume, IReadOnlyList<string> Chapters);

/// <summary>
/// The persisted form of a volume -> chapters map (1.29.0, <c>series_volume_maps.VolumesJson</c>):
/// <c>[{"v":"3","c":["17","18",...,"25","25.5"]}, ...]</c>. Unit numbers are canonical invariant strings (<c>"45.5"</c>,
/// never <c>"045.50"</c>), so equal numbers compare equal as text; the list is ordered by volume, chapters ascending.
/// </summary>
public static class VolumeMapJson
{
    private sealed record Row([property: JsonPropertyName("v")] string V, [property: JsonPropertyName("c")] List<string> C);

    public static string Write(IReadOnlyList<VolumeMapEntry> volumes) =>
        JsonSerializer.Serialize(volumes.Select(v => new Row(v.Volume, v.Chapters.ToList())).ToList());

    /// <summary>The stored map; empty for null or malformed JSON.</summary>
    public static IReadOnlyList<VolumeMapEntry> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return (JsonSerializer.Deserialize<List<Row>>(json) ?? [])
                .Where(r => r?.V is not null)
                .Select(r => new VolumeMapEntry(r.V, (IReadOnlyList<string>?)r.C ?? []))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string WriteChapters(IReadOnlyList<string> chapters) => JsonSerializer.Serialize(chapters);

    public static IReadOnlyList<string> ReadChapters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Parses a unit number (<c>"012"</c>, <c>"45.5"</c>); null for anything else (<c>"none"</c>, <c>"10a"</c>, negatives).</summary>
    public static decimal? Parse(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 16)
            return null;
        return decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) && d >= 0 && d < 100_000 ? d : null;
    }

    /// <summary>The canonical string of a unit number (<c>45.5m</c> -> <c>"45.5"</c>, <c>3.0m</c> -> <c>"3"</c>).</summary>
    public static string Canonical(decimal value) => (value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Builds a clean volume -> chapters map from a provider's raw volume list (1.29.0, design 1.4 + P2.1; MangaDex
/// <c>aggregate?includeUnavailable=1</c>). Pure. Rules:
/// <list type="bullet">
/// <item>Only numbered volumes count (the <c>none</c> bucket and unparseable keys do not); unnumbered chapters are skipped.</item>
/// <item>A chapter listed under several volumes goes to the numbered one with the most uploads, then the lower volume;
/// a chapter found only in the <c>none</c> bucket is "not in a volume yet" (<see cref="Result.Unassigned"/>).</item>
/// <item>Noise clean-up (a mis-tagged upload, e.g. chapter 232 under volume 1): with the volumes in order, a chapter
/// below the previous volume's median or above the next volume's median by more than 1 - and nearer that neighbour's
/// median than its own volume's - is dropped from its volume.</item>
/// <item>Fractional chapters (extras) stay where the list puts them.</item>
/// </list>
/// </summary>
public static class VolumeListBuilder
{
    /// <summary>A raw volume: its key and its chapters with their upload counts.</summary>
    public sealed record RawVolume(string Volume, IReadOnlyList<(string Chapter, int Count)> Chapters);

    public sealed record Result(IReadOnlyList<VolumeMapEntry> Volumes, IReadOnlyList<string> Unassigned)
    {
        /// <summary>The highest integer volume number, or null.</summary>
        public int? HighestVolume => Volumes.Select(v => VolumeMapJson.Parse(v.Volume)).Where(v => v is not null)
            .Select(v => (int?)decimal.Truncate(v!.Value)).Max();

        /// <summary>
        /// Average whole chapters per volume over the volumes numbered 1 and up (for estimated volumes); null with no
        /// such volume.
        /// </summary>
        public double? ChaptersPerVolume
        {
            get
            {
                var counts = Volumes
                    .Where(v => VolumeMapJson.Parse(v.Volume) is >= 1)
                    .Select(v => v.Chapters.Count(c => VolumeMapJson.Parse(c) is { } n && n == decimal.Truncate(n)))
                    .Where(n => n > 0)
                    .ToList();
                return counts.Count == 0 ? null : Math.Round(counts.Average(), 2);
            }
        }
    }

    public static Result Build(IReadOnlyList<RawVolume> raw)
    {
        // chapter -> (volume -> uploads)
        var placements = new Dictionary<decimal, Dictionary<decimal, int>>();
        var unnumbered = new HashSet<decimal>();
        foreach (var volume in raw)
        {
            var v = VolumeMapJson.Parse(volume.Volume);
            foreach (var (chapterText, count) in volume.Chapters)
            {
                if (VolumeMapJson.Parse(chapterText) is not { } c)
                    continue;
                if (v is not { } number)
                {
                    unnumbered.Add(c);
                    continue;
                }
                if (!placements.TryGetValue(c, out var byVolume))
                    placements[c] = byVolume = [];
                byVolume[number] = byVolume.GetValueOrDefault(number) + Math.Max(count, 1);
            }
        }

        var volumes = new SortedDictionary<decimal, List<decimal>>();
        foreach (var (chapter, byVolume) in placements)
        {
            var best = byVolume.OrderByDescending(p => p.Value).ThenBy(p => p.Key).First().Key;
            if (!volumes.TryGetValue(best, out var list))
                volumes[best] = list = [];
            list.Add(chapter);
        }

        // Monotone clean-up against the neighbours' medians.
        var ordered = volumes.Where(p => p.Value.Count > 0).Select(p => (Volume: p.Key, Chapters: p.Value.Order().ToList())).ToList();
        var medians = ordered.Select(v => Median(v.Chapters)).ToList();
        var dropped = new List<decimal>();
        var cleaned = new List<VolumeMapEntry>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var keep = new List<decimal>();
            foreach (var c in ordered[i].Chapters)
            {
                // Dropped only when it is also nearer the neighbour it violates than its own volume (so one noisy
                // neighbour cannot empty a healthy volume).
                var own = Math.Abs(c - medians[i]);
                var belowPrevious = i > 0 && c < medians[i - 1] - 1 && Math.Abs(c - medians[i - 1]) < own;
                var aboveNext = i < ordered.Count - 1 && c > medians[i + 1] + 1 && Math.Abs(c - medians[i + 1]) < own;
                if (belowPrevious || aboveNext)
                    dropped.Add(c);
                else
                    keep.Add(c);
            }
            if (keep.Count > 0)
                cleaned.Add(new VolumeMapEntry(VolumeMapJson.Canonical(ordered[i].Volume), keep.Select(VolumeMapJson.Canonical).ToList()));
        }

        // A dropped chapter's real volume is unknown: it is "not in a volume yet", like a chapter only in the none bucket.
        var unassigned = unnumbered.Where(c => !placements.ContainsKey(c)).Concat(dropped).Distinct().Order().Select(VolumeMapJson.Canonical).ToList();
        return new Result(cleaned, unassigned);
    }

    private static decimal Median(List<decimal> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
}
