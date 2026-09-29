namespace com.lifepixer.mangapixer.Core.Catalog;

using com.lifepixer.mangapixer.Core.Metadata.Missing;

/// <summary>
/// Places a chapter number in a volume from a stored map (P2.3 rules 2-4): Exact (the provider's list), Bounded (between
/// two known volumes with exactly one volume missing between them, or inside a known volume's own span) and Estimated (several
/// missing volumes split evenly; after the last known volume in steps of the average chapters per volume, never past the
/// highest volume the provider knows). A chapter between two ADJACENT known volumes (no volume missing between them) goes to the
/// previous one (owner, 1.29.0 RC: at the end of its stack). Anything else stays loose (null). Extras (a fractional chapter)
/// follow their integer chapter unless the list places them. Pure.
/// </summary>
internal sealed class VolumeResolver
{
    /// <summary>Estimated volumes past the last known one are built up to this many at most (a corrupt total cannot run away).</summary>
    private const int MaxTailVolumes = 500;

    private sealed record ExactVolume(decimal Volume, decimal Min, decimal Max, IReadOnlyList<decimal> Chapters);

    private sealed record RangeVolume(decimal Volume, int Lo, int Hi, VolumePlacement Placement);

    private readonly Dictionary<decimal, decimal> _chapterToVolume = [];
    private readonly List<ExactVolume> _exact;
    private readonly List<RangeVolume> _ranges = [];
    private readonly Dictionary<decimal, RangeVolume> _rangeByVolume = [];
    private readonly Dictionary<decimal, ExactVolume> _exactByVolume = [];
    private readonly List<ExactVolume> _wholeExact;

    public VolumeResolver(VolumeMapInput map)
    {
        ArgumentNullException.ThrowIfNull(map);
        HasData = map.HasData;
        _exact = map.Volumes
            .Where(v => v.Chapters.Count > 0)
            .Select(v => new ExactVolume(v.Volume, v.Chapters.Min(), v.Chapters.Max(), v.Chapters))
            .OrderBy(v => v.Volume)
            .ToList();
        foreach (var e in _exact)
        {
            _exactByVolume.TryAdd(e.Volume, e);
            foreach (var c in e.Chapters)
                _chapterToVolume.TryAdd(c, e.Volume);
        }
        _wholeExact = _exact.Where(e => decimal.Truncate(e.Volume) == e.Volume).ToList();
        BuildRanges(map);
    }

    /// <summary>True when the map can place a chapter at all.</summary>
    public bool HasData { get; }

    private void BuildRanges(VolumeMapInput map)
    {
        // Whole-numbered volumes with chapters are the anchors; a virtual volume 0 anchors the chapters before the first
        // known volume (or all of them when the map has no exact list, only a ratio and a total).
        var anchors = new List<(int Volume, decimal Min, decimal Max)>();
        var whole = _exact.Where(e => decimal.Truncate(e.Volume) == e.Volume).ToList();
        if (whole.Count == 0 || whole[0].Volume > 1)
            anchors.Add((0, 0m, 0m));
        anchors.AddRange(whole.Select(e => ((int)e.Volume, e.Min, e.Max)));

        for (var i = 0; i + 1 < anchors.Count; i++)
        {
            var (a, b) = (anchors[i], anchors[i + 1]);
            var missingVolumes = b.Volume - a.Volume - 1;
            if (missingVolumes < 1)
                continue;
            var lo = (int)decimal.Floor(a.Max) + 1;
            var hi = (int)decimal.Ceiling(b.Min) - 1;
            if (hi < lo)
                continue;
            var count = hi - lo + 1;
            for (var k = 0; k < missingVolumes; k++)
            {
                var start = lo + (int)((long)k * count / missingVolumes);
                var end = lo + (int)((long)(k + 1) * count / missingVolumes) - 1;
                if (end >= start)
                    AddRange(new RangeVolume(a.Volume + 1 + k, start, end, missingVolumes == 1 ? VolumePlacement.Bounded : VolumePlacement.Estimated));
            }
        }

        // After the last known volume: steps of the average chapters per volume, up to the highest volume the provider knows.
        var last = anchors[^1];
        if (map.ChaptersPerVolume is { } cpv && cpv >= 1 && map.KnownVolumeCount is { } known && known > last.Volume)
        {
            var lo = (int)decimal.Floor(last.Max) + 1;
            var tail = Math.Min(known - last.Volume, MaxTailVolumes);
            for (var j = 0; j < tail; j++)
            {
                var start = lo + (int)Math.Round(j * cpv, MidpointRounding.AwayFromZero);
                var end = lo + (int)Math.Round((j + 1) * cpv, MidpointRounding.AwayFromZero) - 1;
                if (end >= start)
                    AddRange(new RangeVolume(last.Volume + 1 + j, start, end, VolumePlacement.Estimated));
            }
        }
    }

    private void AddRange(RangeVolume range)
    {
        if (_rangeByVolume.TryAdd(range.Volume, range))
            _ranges.Add(range);
    }

    /// <summary>The volume of a chapter and how it was placed, or null when it stays loose.</summary>
    public (decimal Volume, VolumePlacement Placement)? Resolve(decimal chapter)
    {
        if (_chapterToVolume.TryGetValue(chapter, out var exact))
            return (exact, VolumePlacement.Exact);
        // An extra follows its integer chapter.
        if (decimal.Truncate(chapter) != chapter)
            return Resolve(decimal.Floor(chapter));
        // Inside a known volume's own span but not listed: it belongs there.
        foreach (var e in _exact)
        {
            if (chapter >= e.Min && chapter <= e.Max)
                return (e.Volume, VolumePlacement.Bounded);
        }
        if (chapter is < 0 or > MissingUnits.MaxNumber)
            return null;
        var n = (int)chapter;
        foreach (var r in _ranges)
        {
            if (n >= r.Lo && n <= r.Hi)
                return (r.Volume, r.Placement);
        }
        // Between two adjacent known volumes (a whole chapter the list does not place): the end of the previous one.
        for (var i = 0; i + 1 < _wholeExact.Count; i++)
        {
            var (a, b) = (_wholeExact[i], _wholeExact[i + 1]);
            if (b.Volume == a.Volume + 1 && chapter > a.Max && chapter < b.Min)
                return (a.Volume, VolumePlacement.Adjacent);
        }
        return null;
    }

    /// <summary>
    /// The chapter units a volume should hold, or null when the map does not say (a volume named only by file names). From the
    /// exact list: every whole number, and a fraction only when its whole number is NOT listed - then the fractions are the
    /// PARTS of a split chapter (4.1 + 4.2 = chapter 4, owner rule 1.29.0); a fraction next to its listed whole (10 and 10.5)
    /// is an extra and never required. An estimated / bounded range: its whole numbers.
    /// </summary>
    public IReadOnlyList<decimal>? RequiredUnits(decimal volume)
    {
        if (_exactByVolume.TryGetValue(volume, out var e))
        {
            var listed = e.Chapters.Where(c => c >= 0 && c <= MissingUnits.MaxNumber).ToHashSet();
            return listed.Where(c => decimal.Truncate(c) == c || !listed.Contains(decimal.Truncate(c))).Order().ToList();
        }
        return _rangeByVolume.TryGetValue(volume, out var r)
            ? Enumerable.Range(r.Lo, r.Hi - r.Lo + 1).Select(n => (decimal)n).ToList()
            : null;
    }

    /// <summary>The highest chapter a volume holds (its list or its range), or null when the map does not say.</summary>
    public decimal? LastChapterOf(decimal volume) =>
        _exactByVolume.TryGetValue(volume, out var e) ? e.Max : _rangeByVolume.TryGetValue(volume, out var r) ? r.Hi : null;

    /// <summary>True for a volume whose chapters are an estimate (not in the exact list).</summary>
    public bool IsEstimated(decimal volume) =>
        !_exactByVolume.ContainsKey(volume) && _rangeByVolume.TryGetValue(volume, out var r) && r.Placement == VolumePlacement.Estimated;

}
