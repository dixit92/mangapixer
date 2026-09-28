namespace com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

using System.Numerics;

/// <summary>What comparing two cover hashes says (1.28.0).</summary>
public enum CoverVerdict
{
    /// <summary>Between the two thresholds: neither the same cover nor clearly a different one.</summary>
    NoSignal = 0,
    Same = 1,
    Different = 2,
}

/// <summary>
/// The 64-bit perceptual hash (pHash) of a cover (1.28.0): the 2-D DCT of a 32x32 grayscale image, the
/// top-left 8x8 (lowest) frequencies, one bit per coefficient above their median. It survives scaling,
/// re-compression, small crops and brightness / colour shifts, so a provider's cover and the first page of a
/// local volume 1 land a few bits apart while different covers land about half the bits apart. Pure math:
/// decoding and the 32x32 grayscale reduction happen in the media worker (the server never decodes remote
/// bytes); this is shared so the rule is unit-tested and the same everywhere.
/// </summary>
public static class CoverHash
{
    /// <summary>Side of the grayscale square the hash is computed on.</summary>
    public const int Side = 32;

    /// <summary>Side of the low-frequency block kept (8 x 8 = 64 bits).</summary>
    public const int LowFrequencies = 8;

    /// <summary>At or below this Hamming distance two covers are the same (calibrated on public covers).</summary>
    public const int SameMaxDistance = 10;

    /// <summary>At or above this Hamming distance two covers are different; in between there is no signal.</summary>
    public const int DifferentMinDistance = 20;

    private static readonly double[,] s_cos = BuildCosines();

    private static double[,] BuildCosines()
    {
        var table = new double[LowFrequencies, Side];
        for (var u = 0; u < LowFrequencies; u++)
            for (var x = 0; x < Side; x++)
                table[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / (2 * Side));
        return table;
    }

    /// <summary>
    /// The hash of a <see cref="Side"/> x <see cref="Side"/> grayscale image, row-major, one byte per pixel.
    /// Bit 63 is the DC coefficient, then the rest of the 8x8 block row by row.
    /// </summary>
    public static ulong Compute(ReadOnlySpan<byte> luma)
    {
        if (luma.Length != Side * Side)
            throw new ArgumentException($"Expected {Side * Side} grayscale values.", nameof(luma));

        // Separable DCT-II, only the frequencies kept: rows first (32 x 8), then columns (8 x 8).
        var rows = new double[Side, LowFrequencies];
        for (var y = 0; y < Side; y++)
            for (var u = 0; u < LowFrequencies; u++)
            {
                var sum = 0.0;
                for (var x = 0; x < Side; x++)
                    sum += luma[y * Side + x] * s_cos[u, x];
                rows[y, u] = sum;
            }

        var coefficients = new double[LowFrequencies * LowFrequencies];
        for (var v = 0; v < LowFrequencies; v++)
            for (var u = 0; u < LowFrequencies; u++)
            {
                var sum = 0.0;
                for (var y = 0; y < Side; y++)
                    sum += rows[y, u] * s_cos[v, y];
                coefficients[v * LowFrequencies + u] = sum;
            }

        var sorted = (double[])coefficients.Clone();
        Array.Sort(sorted);
        var median = (sorted[31] + sorted[32]) / 2;
        var hash = 0UL;
        for (var i = 0; i < coefficients.Length; i++)
        {
            if (coefficients[i] > median)
                hash |= 1UL << (63 - i);
        }
        return hash;
    }

    /// <summary>Number of differing bits.</summary>
    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    public static CoverVerdict Compare(ulong a, ulong b) => Distance(a, b) switch
    {
        <= SameMaxDistance => CoverVerdict.Same,
        >= DifferentMinDistance => CoverVerdict.Different,
        _ => CoverVerdict.NoSignal,
    };
}

/// <summary>
/// When comparing covers may change a decision, and what the comparison adds (1.28.0, "Compare covers" under
/// Automatic matching). Only for a tie on the title: the top two candidates are a close second with raw title
/// scores within <see cref="TieWithin"/>, both at or above the review floor; only for volume-shaped works (the
/// first page of a volume or a one-shot is a cover; the first page of a chapter or a tall strip is not); at most
/// <see cref="MaxCandidates"/> candidate images per work. Positive only: a candidate whose cover is the same as
/// the local cover gets <see cref="MatchScorer.CoverAgree"/> on its ADJUSTED score (the lead), never on the raw
/// title score the auto gate reads; no candidate is ever penalised, and a candidate without a cover is simply not
/// compared. When every compared cover matches (the same art on a series and its spin-off) there is no signal.
/// </summary>
public static class CoverEvidence
{
    /// <summary>Raw title scores of the top two at most this far apart count as a tie.</summary>
    public const double TieWithin = 0.02;

    /// <summary>Candidate images downloaded for one work, at most.</summary>
    public const int MaxCandidates = 2;

    /// <summary>
    /// Volumes, a one-shot, or a single archive holding a whole work: at least half the archives volume-like and
    /// fewer than half chapter-like, or one archive that is not a chapter; never tall strips (webtoons).
    /// </summary>
    public static bool IsVolumeShaped(MatchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TallStrips || context.ArchiveCount <= 0)
            return false;
        if (context.ArchiveCount == 1)
            return context.ChapterLikeCount == 0;
        return context.VolumeLikeCount * 2 >= context.ArchiveCount && context.ChapterLikeCount * 2 < context.ArchiveCount;
    }

    /// <summary>The top two are a close second with raw titles within <see cref="TieWithin"/>, both at or above the floor.</summary>
    public static bool IsTie(MatchOutcome outcome, MatchThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(thresholds);
        if (outcome.Ranked.Count < 2)
            return false;
        var (top, second) = (outcome.Ranked[0], outcome.Ranked[1]);
        return (top.Reasons & MatchReason.CloseSecond) != 0
            && top.TitleScore >= thresholds.ReviewFloor && second.TitleScore >= thresholds.ReviewFloor
            && Math.Abs(top.TitleScore - second.TitleScore) <= TieWithin;
    }

    /// <summary>The candidates whose covers are worth comparing (the tied top two of a volume-shaped work), or none.</summary>
    public static IReadOnlyList<ScoredCandidate> TiedPair(MatchOutcome outcome, MatchContext context, MatchThresholds thresholds) =>
        IsTie(outcome, thresholds) && IsVolumeShaped(context) ? [outcome.Ranked[0], outcome.Ranked[1]] : [];

    /// <summary>
    /// The candidates (by external id) whose cover is the same as the local one, given the hashes of the covers that
    /// could be compared (a record without a cover, or whose image failed, is simply not among them: a same cover
    /// is evidence on its own); empty when none matches, or when two or more were compared and every one matches
    /// (the same art on both records says nothing about which one it is).
    /// </summary>
    public static IReadOnlySet<string> Matching(ulong localHash, IReadOnlyDictionary<string, ulong> candidateHashes)
    {
        ArgumentNullException.ThrowIfNull(candidateHashes);
        var same = candidateHashes
            .Where(c => CoverHash.Compare(localHash, c.Value) == CoverVerdict.Same)
            .Select(c => c.Key)
            .ToHashSet(StringComparer.Ordinal);
        return candidateHashes.Count >= 2 && same.Count == candidateHashes.Count ? new HashSet<string>(StringComparer.Ordinal) : same;
    }
}
