namespace com.lifepixer.mangapixer.Tests.Core.Reading;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;
using Xunit;

/// <summary>
/// Unit tests for the reading-mode suggestion rules (1.41.0): which local signal picks the mode, the base confidence, and how the
/// other signals raise or lower it (below Low = no suggestion).
/// </summary>
public sealed class ReadingModeSuggestionRulesTests
{
    private static ReadingModeEvidence E(
        int measured = 0, int tall = 0, int rtl = 0, int ltr = 0, int stated = 0, MetadataOrigin? origin = null, bool? webtoon = null) =>
        new(measured, tall, rtl, ltr, stated, origin, webtoon);

    [Fact]
    public void NoEvidence_NoSuggestion()
    {
        Assert.Null(ReadingModeSuggestionRules.Suggest(E()));
        Assert.Null(ReadingModeSuggestionRules.Suggest(E(measured: 100, tall: 3)));          // paged, nothing else
        Assert.Null(ReadingModeSuggestionRules.Suggest(E(rtl: 1, stated: 2)));               // not a majority
        Assert.Null(ReadingModeSuggestionRules.Suggest(E(origin: MetadataOrigin.EnglishOriginal)));
    }

    [Fact]
    public void TallPages_Vertical_HighFromEightyPercent()
    {
        Assert.Equal(new ReadingModeVerdict(ReaderMode.VerticalWebtoon, ReadingModeConfidence.High, ReadingModeSignal.TallPages),
            ReadingModeSuggestionRules.Suggest(E(measured: 40, tall: 32)));
        Assert.Equal(new ReadingModeVerdict(ReaderMode.VerticalWebtoon, ReadingModeConfidence.Medium, ReadingModeSignal.TallPages),
            ReadingModeSuggestionRules.Suggest(E(measured: 40, tall: 20)));
        Assert.Null(ReadingModeSuggestionRules.Suggest(E(measured: 19, tall: 19))); // too few measured, nothing else
    }

    [Fact]
    public void TallPages_WinOverComicInfo_ButLoseConfidence()
    {
        var v = ReadingModeSuggestionRules.Suggest(E(measured: 40, tall: 40, rtl: 3, stated: 3));
        Assert.Equal(new ReadingModeVerdict(ReaderMode.VerticalWebtoon, ReadingModeConfidence.Medium, ReadingModeSignal.TallPages), v);
    }

    [Fact]
    public void ComicInfoRightToLeft_Majority_Medium()
    {
        Assert.Equal(new ReadingModeVerdict(ReaderMode.PagedRtl, ReadingModeConfidence.Medium, ReadingModeSignal.ComicInfo),
            ReadingModeSuggestionRules.Suggest(E(measured: 100, tall: 0, rtl: 3, stated: 4)));
        // A Japanese record agrees: High.
        Assert.Equal(ReadingModeConfidence.High,
            ReadingModeSuggestionRules.Suggest(E(rtl: 3, stated: 3, origin: MetadataOrigin.Japan))!.Confidence);
        // A Korean record disagrees: Low.
        Assert.Equal(ReadingModeConfidence.Low,
            ReadingModeSuggestionRules.Suggest(E(rtl: 3, stated: 3, origin: MetadataOrigin.Korea))!.Confidence);
    }

    [Fact]
    public void LinkedRecord_IsAHint_Low()
    {
        Assert.Equal(new ReadingModeVerdict(ReaderMode.VerticalWebtoon, ReadingModeConfidence.Low, ReadingModeSignal.LinkedRecord),
            ReadingModeSuggestionRules.Suggest(E(origin: MetadataOrigin.Korea)));
        Assert.Equal(new ReadingModeVerdict(ReaderMode.VerticalWebtoon, ReadingModeConfidence.Low, ReadingModeSignal.LinkedRecord),
            ReadingModeSuggestionRules.Suggest(E(origin: MetadataOrigin.ChinaTaiwan)));
        Assert.Equal(new ReadingModeVerdict(ReaderMode.VerticalWebtoon, ReadingModeConfidence.Low, ReadingModeSignal.LinkedRecord),
            ReadingModeSuggestionRules.Suggest(E(origin: MetadataOrigin.Japan, webtoon: true)));
        Assert.Equal(new ReadingModeVerdict(ReaderMode.PagedRtl, ReadingModeConfidence.Low, ReadingModeSignal.LinkedRecord),
            ReadingModeSuggestionRules.Suggest(E(origin: MetadataOrigin.Japan)));
        // "Not a webtoon" on a Korean record: no vertical hint.
        Assert.Null(ReadingModeSuggestionRules.Suggest(E(origin: MetadataOrigin.Korea, webtoon: false)));
    }

    [Fact]
    public void LinkedRecord_ContradictedByLocalEvidence_NoSuggestion()
    {
        // A Korean record over measured pages that are not tall: a paged manhwa.
        Assert.Null(ReadingModeSuggestionRules.Suggest(E(measured: 60, tall: 2, origin: MetadataOrigin.Korea)));
        // A Japanese record whose archives' ComicInfo says left-to-right.
        Assert.Null(ReadingModeSuggestionRules.Suggest(E(ltr: 2, stated: 2, origin: MetadataOrigin.Japan)));
    }

    [Fact]
    public void LinkedRecord_ConfirmedByTallPages_High()
    {
        Assert.Equal(new ReadingModeVerdict(ReaderMode.VerticalWebtoon, ReadingModeConfidence.High, ReadingModeSignal.TallPages),
            ReadingModeSuggestionRules.Suggest(E(measured: 40, tall: 20, origin: MetadataOrigin.Korea)));
    }

    [Fact]
    public void ConflictingSignals_CanCancelOut()
    {
        // Tall by the rule but only 50%, ComicInfo right-to-left and a Japanese record: no suggestion.
        Assert.Null(ReadingModeSuggestionRules.Suggest(E(measured: 40, tall: 20, rtl: 2, stated: 2, origin: MetadataOrigin.Japan)));
    }
}
