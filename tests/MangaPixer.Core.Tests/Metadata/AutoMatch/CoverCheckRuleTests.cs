namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;

/// <summary>
/// Unit tests for the post-link cover check rule (1.31.0, <see cref="CoverCheckRule"/>): covers are negative evidence only per
/// volume, in any language, on enough volumes, never from a page that repeats in every volume. Synthetic hashes with known bit
/// distances only.
/// </summary>
public sealed class CoverCheckRuleTests
{
    // Pairwise distances: W1-W2 32, L1-W1 32, L1-W2 64, L2-W1 32, L2-W2 32, L1-L2 32 - all clearly different.
    private const ulong W1 = 0x0000_0000_0000_0000;
    private const ulong W2 = 0xFFFF_FFFF_0000_0000;
    private const ulong L1 = 0x0000_0000_FFFF_FFFF;
    private const ulong L2 = 0x0000_FFFF_FFFF_0000;

    private static ulong Near(ulong hash, int bits) => hash ^ ((1UL << bits) - 1);

    private static CoverCheckVolume V(int volume, ulong[] local, ulong[] web) => new(volume, local, web);

    [Fact]
    public void Distances_OfTheFixtures_AreWhatTheTestsAssume()
    {
        Assert.Equal(32, CoverHash.Distance(L1, W1));
        Assert.Equal(32, CoverHash.Distance(L2, W2));
        Assert.Equal(32, CoverHash.Distance(L2, W1));
        Assert.Equal(32, CoverHash.Distance(L1, L2));
        Assert.Equal(4, CoverHash.Distance(Near(W1, 4), W1));
    }

    [Fact]
    public void Series_TwoVolumesClearlyDifferent_Differs_WithTheDistances()
    {
        var result = CoverCheckRule.Decide([V(1, [L1], [W1]), V(2, [L2], [W2])], [W1, W2], oneShot: false);

        Assert.Equal(CoverCheckVerdict.Differs, result.Verdict);
        Assert.True(result.Demotes);
        Assert.Equal(2, result.Compared);
        Assert.Equal([32, 32], result.Distances);
    }

    [Fact]
    public void Series_OneVolumeCompared_IsNotEnough()
    {
        var result = CoverCheckRule.Decide([V(1, [L1], [W1]), V(2, [L2], [])], [W1], oneShot: false);

        Assert.Equal(CoverCheckVerdict.NotEnough, result.Verdict);
        Assert.Equal(1, result.Compared);
        Assert.False(result.Demotes);
    }

    [Fact]
    public void OneShot_ItsOneCoverClearlyDifferent_Differs()
    {
        var result = CoverCheckRule.Decide([V(1, [L1], [W1])], [W1], oneShot: true);

        Assert.Equal(CoverCheckVerdict.Differs, result.Verdict);
        Assert.Equal([32], result.Distances);
    }

    [Fact]
    public void AnyLanguageOfTheSameVolume_Agrees()
    {
        // Volume 1 stored in two languages: the local cover is the second one.
        var result = CoverCheckRule.Decide([V(1, [Near(W2, 3)], [W1, W2]), V(2, [L2], [W2])], [W1, W2], oneShot: false);

        Assert.Equal(CoverCheckVerdict.Agrees, result.Verdict);
    }

    [Fact]
    public void ACoverOfAnotherVolumeOfTheRecord_Agrees_NumberingOffset()
    {
        // Local volume 2's page 1 is the record's volume 1 cover (an offset or an omnibus numbering): the right series.
        var result = CoverCheckRule.Decide([V(2, [Near(W1, 5)], [W2]), V(3, [L1], [L2])], [W1, W2, L2], oneShot: false);

        Assert.Equal(CoverCheckVerdict.Agrees, result.Verdict);
    }

    [Fact]
    public void OneHalfOfASpreadMatches_Agrees()
    {
        // A spread page 1: its two halves are compared; the front half is the web cover.
        var result = CoverCheckRule.Decide([V(1, [L1, Near(W1, 6)], [W1]), V(2, [L2], [W2])], [W1, W2], oneShot: false);

        Assert.Equal(CoverCheckVerdict.Agrees, result.Verdict);
    }

    [Fact]
    public void ADistanceInBetween_IsUnsure()
    {
        var result = CoverCheckRule.Decide([V(1, [Near(W1, 15)], [W1]), V(2, [L2], [W2])], [W1, W2], oneShot: false);

        Assert.Equal(CoverCheckVerdict.Unsure, result.Verdict);
        Assert.Equal([15, 32], result.Distances);
    }

    [Fact]
    public void ThePageRepeatedInEveryVolume_IsNoCover_TheVolumesAreDropped()
    {
        // A scanlator credits page as page 1 of volumes 1 and 2 (the same picture), volume 3 a real different cover.
        var credits = L1;
        var result = CoverCheckRule.Decide(
            [V(1, [credits], [W1]), V(2, [Near(credits, 2)], [W2]), V(3, [L2], [Near(W2, 40)])], [W1, W2, Near(W2, 40)], oneShot: false);

        Assert.Equal(CoverCheckVerdict.NotEnough, result.Verdict);
        Assert.Equal(2, result.Dropped);
        Assert.Equal(1, result.Compared);
    }

    [Fact]
    public void NothingToCompare_IsNotEnough_EvenForAOneShot()
    {
        Assert.Equal(CoverCheckVerdict.NotEnough, CoverCheckRule.Decide([], [], oneShot: true).Verdict);
        Assert.Equal(CoverCheckVerdict.NotEnough, CoverCheckRule.Decide([V(1, [], [W1])], [W1], oneShot: true).Verdict);
    }
}
