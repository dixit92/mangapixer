namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;

/// <summary>
/// Unit tests for the cover comparison rule (1.28.0): <see cref="CoverHash"/> (the 64-bit pHash of a 32x32
/// grayscale square and its thresholds), <see cref="CoverEvidence"/> (when a comparison may run, which candidates
/// it helps) and the scorer's positive-only, adjusted-score-only bonus. Synthetic pixels and records only.
/// </summary>
public sealed class CoverEvidenceTests
{
    private const int N = CoverHash.Side;

    private static byte[] Pixels(Func<int, int, double> f)
    {
        var p = new byte[N * N];
        for (var y = 0; y < N; y++)
            for (var x = 0; x < N; x++)
                p[y * N + x] = (byte)Math.Clamp(Math.Round(f(x, y)), 0, 255);
        return p;
    }

    // Two unrelated "covers": a figure in the middle, and diagonal bands.
    private static readonly byte[] s_figure = Pixels((x, y) => Math.Abs(x - 16) < 7 && y > 6 ? 40 : 220 - y * 2);
    private static readonly byte[] s_bands = Pixels((x, y) => (x + y) / 8 % 2 == 0 ? 30 : 200);

    [Fact]
    public void Hash_IsDeterministic_AndSurvivesABrightnessShiftAndNoise()
    {
        var brighter = s_figure.Select(v => (byte)Math.Min(255, v + 20)).ToArray();
        var noisy = s_figure.Select((v, i) => (byte)Math.Clamp(v + (i * 7919 % 11) - 5, 0, 255)).ToArray();

        Assert.Equal(CoverHash.Compute(s_figure), CoverHash.Compute(s_figure));
        Assert.Equal(CoverVerdict.Same, CoverHash.Compare(CoverHash.Compute(s_figure), CoverHash.Compute(brighter)));
        Assert.Equal(CoverVerdict.Same, CoverHash.Compare(CoverHash.Compute(s_figure), CoverHash.Compute(noisy)));
        Assert.Equal(CoverVerdict.Different, CoverHash.Compare(CoverHash.Compute(s_figure), CoverHash.Compute(s_bands)));
    }

    [Fact]
    public void Hash_HasHalfItsBitsSet_TheMedianSplit()
    {
        var bits = System.Numerics.BitOperations.PopCount(CoverHash.Compute(s_figure));

        Assert.InRange(bits, 28, 36);
    }

    [Fact]
    public void Hash_RejectsAnythingButA32x32Square()
    {
        Assert.Throws<ArgumentException>(() => CoverHash.Compute(new byte[31 * 32]));
    }

    [Theory]
    [InlineData(0UL, 0UL, 0, CoverVerdict.Same)]
    [InlineData(0UL, 0x3FFUL, 10, CoverVerdict.Same)]
    [InlineData(0UL, 0x7FFUL, 11, CoverVerdict.NoSignal)]
    [InlineData(0UL, 0x7FFFFUL, 19, CoverVerdict.NoSignal)]
    [InlineData(0UL, 0xFFFFFUL, 20, CoverVerdict.Different)]
    [InlineData(0UL, ulong.MaxValue, 64, CoverVerdict.Different)]
    public void Compare_UsesTheCalibratedThresholds(ulong a, ulong b, int distance, CoverVerdict verdict)
    {
        Assert.Equal(distance, CoverHash.Distance(a, b));
        Assert.Equal(verdict, CoverHash.Compare(a, b));
    }

    private static MatchContext Context(int archives, int volumes, int chapters, bool tall = false) =>
        new(WorkClass.Series, archives, volumes, chapters, null, null, tall, []);

    [Theory]
    [InlineData(10, 10, 0, false, true)]   // volumes
    [InlineData(10, 5, 0, false, true)]    // half volumes
    [InlineData(10, 4, 0, false, false)]   // mostly unnumbered
    [InlineData(10, 6, 5, false, false)]   // half chapters
    [InlineData(1, 0, 0, false, true)]     // a one-shot / a whole work in one archive
    [InlineData(1, 0, 1, false, false)]    // a single chapter
    [InlineData(10, 10, 0, true, false)]   // tall strips: a webtoon
    [InlineData(0, 0, 0, false, false)]
    public void IsVolumeShaped(int archives, int volumes, int chapters, bool tall, bool expected)
    {
        Assert.Equal(expected, CoverEvidence.IsVolumeShaped(Context(archives, volumes, chapters, tall)));
    }

    private static MatchCandidate Rec(string id, string title) =>
        new("mangaupdates", id, title, [], MetadataFormat.Comic, "Manga", null, null, null, [], []);

    private static MatchQuery Query(string title, int volumes = 5, IReadOnlySet<string>? covers = null) =>
        new([new QueryVariant(title, QueryVariantKind.Primary)],
            new MatchContext(WorkClass.Series, 5, volumes, 0, null, null, false, [], CoverMatches: covers));

    [Fact]
    public void TiedPair_IsTheTopTwo_OfATitleTieAtTheFloor_ForAVolumeShapedWork()
    {
        var scorer = new MatchScorer();
        var tie = scorer.Score(Query("Sprout"), [Rec("1", "Sprout"), Rec("2", "Sprout"), Rec("3", "Sprout")], MatchThresholds.Default);
        var clear = scorer.Score(Query("Sprout"), [Rec("1", "Sprout"), Rec("2", "Unrelated Name")], MatchThresholds.Default);
        var chapters = scorer.Score(Query("Sprout", volumes: 0), [Rec("1", "Sprout"), Rec("2", "Sprout")], MatchThresholds.Default);
        var weak = scorer.Score(Query("Sprout Alpha Beta"), [Rec("1", "Zeta Omega"), Rec("2", "Zeta Omega")], MatchThresholds.Default);

        Assert.Equal(["1", "2"], CoverEvidence.TiedPair(tie, Query("Sprout").Context, MatchThresholds.Default).Select(c => c.Candidate.ExternalId));
        Assert.Empty(CoverEvidence.TiedPair(clear, Query("Sprout").Context, MatchThresholds.Default));
        Assert.Empty(CoverEvidence.TiedPair(chapters, Query("Sprout", volumes: 0).Context, MatchThresholds.Default));
        Assert.Empty(CoverEvidence.TiedPair(weak, Query("Sprout Alpha Beta").Context, MatchThresholds.Default)); // below the floor
    }

    [Fact]
    public void Matching_TheSameCoversOnly_UnlessEveryComparedCoverIsTheSame()
    {
        const ulong local = 0;
        var same = 0x3UL;          // 2 bits apart
        var other = 0xFFFFFFUL;    // 24 bits apart
        var unsure = 0x7FFFUL;     // 15 bits apart

        Assert.Equal(["a"], CoverEvidence.Matching(local, new Dictionary<string, ulong> { ["a"] = same, ["b"] = other }));
        Assert.Equal(["a"], CoverEvidence.Matching(local, new Dictionary<string, ulong> { ["a"] = same, ["b"] = unsure }));
        Assert.Empty(CoverEvidence.Matching(local, new Dictionary<string, ulong> { ["a"] = same, ["b"] = same })); // same art on both
        Assert.Empty(CoverEvidence.Matching(local, new Dictionary<string, ulong> { ["a"] = other, ["b"] = unsure }));
        Assert.Equal(["a"], CoverEvidence.Matching(local, new Dictionary<string, ulong> { ["a"] = same })); // the other has no cover
        Assert.Empty(CoverEvidence.Matching(local, new Dictionary<string, ulong> { ["a"] = unsure }));
        Assert.Empty(CoverEvidence.Matching(local, new Dictionary<string, ulong>()));
    }

    [Fact]
    public void Scorer_CoverMatch_RaisesTheAdjustedScoreOnly_PositiveOnly_AndATieStaysInReview()
    {
        var scorer = new MatchScorer();
        MatchCandidate[] records = [Rec("1", "Sprout"), Rec("2", "Sprout")];
        var plain = scorer.Score(Query("Sprout"), records, MatchThresholds.Default);
        var covered = scorer.Score(Query("Sprout", covers: new HashSet<string> { "2" }), records, MatchThresholds.Default);

        Assert.Equal("1", plain.Ranked[0].Candidate.ExternalId);
        Assert.Equal("2", covered.Ranked[0].Candidate.ExternalId); // the tie is broken in the ranking
        var top = covered.Ranked[0];
        var second = covered.Ranked[1];
        Assert.Equal(plain.Ranked[1].TitleScore, top.TitleScore);                        // the raw title never moves
        Assert.Equal(plain.Ranked[1].AdjustedScore + MatchScorer.CoverAgree, top.AdjustedScore, 6);
        Assert.Equal(plain.Ranked[0].AdjustedScore, second.AdjustedScore, 6);            // nobody is penalised
        Assert.True((top.Reasons & MatchReason.CoverMatch) != 0);
        Assert.Equal(MatchBand.NeedsReview, covered.Band);                               // +0.05 < the 10-point lead
        Assert.True((top.Reasons & MatchReason.CloseSecond) != 0);
        Assert.Equal(0, (int)(MatchScorer.VetoReasons & MatchReason.CoverMatch));
    }

    [Fact]
    public void Scorer_CoverMatch_NeverLiftsAWeakTitleToAuto_EvenAtTheLoosestLead()
    {
        var scorer = new MatchScorer();
        var loosest = new MatchThresholds(MatchThresholds.AutoTitleMin, MatchThresholds.MarginMin, MatchThresholds.ReviewFloorMin);
        var o = scorer.Score(Query("Sprout Garden", covers: new HashSet<string> { "1" }),
            [Rec("1", "Sprout Garden Tales of the Far North"), Rec("2", "Sprout Garden Tales of the Far South")], loosest);

        Assert.True(o.Ranked[0].TitleScore < MatchThresholds.AutoTitleMin);
        Assert.NotEqual(MatchBand.Auto, o.Band);
    }
}
