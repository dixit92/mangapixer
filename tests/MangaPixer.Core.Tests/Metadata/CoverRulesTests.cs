namespace com.lifepixer.mangapixer.Tests.Core.Metadata;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;

/// <summary>
/// Unit tests for the 1.29.0 cover layer rules (design P2.2): every row of the source matrix on STORED HASHES (no image,
/// no third-party art). Hashes are built by flipping a known number of bits of a base value, so each case sits exactly in
/// the Same (&lt;= 10), uncertain (11-19) or Different (&gt;= 20) band.
/// </summary>
public sealed class CoverRulesTests
{
    private const ulong Base = 0x0F0F_3C3C_5A5A_A5A5UL;

    /// <summary>A hash <paramref name="bits"/> bits away from <see cref="Base"/>.</summary>
    private static ulong Away(int bits)
    {
        var mask = bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;
        return Base ^ mask;
    }

    private static WebCoverCandidate Web(ulong? hash, AutoCoverSource source = AutoCoverSource.WebVolume, long? id = 7, bool originFallback = false) =>
        new(source, source == AutoCoverSource.Poster ? null : id, hash, originFallback);

    [Theory]
    [InlineData(1400, 1000, true)]   // a jacket spread
    [InlineData(1200, 1000, true)]   // exactly the bound
    [InlineData(1190, 1000, false)]
    [InlineData(700, 1000, false)]   // a single cover
    [InlineData(null, 1000, false)]  // size unknown
    [InlineData(1000, 0, false)]
    public void IsSpread_UsesTheAspectBound(int? width, int? height, bool spread) =>
        Assert.Equal(spread, CoverRules.IsSpread(width, height));

    [Fact]
    public void FrontSide_IsTheOuterEdgeOfTheReadingDirection()
    {
        // Left-to-right jacket: back / spine / front -> the front is the right half; right-to-left: the left half.
        Assert.Equal(CoverCropSide.Right, CoverRules.FrontSide(CoverDirection.LeftToRight));
        Assert.Equal(CoverCropSide.Left, CoverRules.FrontSide(CoverDirection.RightToLeft));
        Assert.Equal(CoverCropSide.Left, CoverRules.Other(CoverCropSide.Right));
    }

    [Theory]
    [InlineData(0, CoverVerdict.Same)]
    [InlineData(10, CoverVerdict.Same)]
    [InlineData(11, CoverVerdict.NoSignal)]
    [InlineData(19, CoverVerdict.NoSignal)]
    [InlineData(20, CoverVerdict.Different)]
    [InlineData(40, CoverVerdict.Different)]
    public void Compare_UsesThe128Bands(int bits, CoverVerdict verdict) =>
        Assert.Equal(verdict, CoverRules.Compare(Base, Away(bits)));

    [Fact]
    public void Compare_UnknownHash_IsNoSignal() => Assert.Equal(CoverVerdict.NoSignal, CoverRules.Compare(null, Base));

    // ----- volume archives ------------------------------------------------------------------------------------------

    [Fact]
    public void Volume_NoWebCover_SinglePage_KeepsTheFile_NeverThePoster()
    {
        var d = CoverRules.DecideVolume(spread: false, CoverCropSide.Right, Base, null, web: null);
        Assert.Equal(AutoCoverSource.File, d.Source);
        Assert.Equal(AutoCoverReason.NoWebCover, d.Reason);
    }

    [Fact]
    public void Volume_NoWebCover_Spread_CropsTheFront()
    {
        var d = CoverRules.DecideVolume(spread: true, CoverCropSide.Left, Base, null, web: null);
        Assert.Equal(AutoCoverSource.Crop, d.Source);
        Assert.Equal(CoverCropSide.Left, d.CropSide);
        Assert.Equal(AutoCoverReason.Spread, d.Reason);
    }

    [Fact]
    public void Volume_LocalSameAsWeb_KeepsTheLocalCover()
    {
        Assert.Equal(new CoverDecision(AutoCoverSource.File, AutoCoverReason.FileMatchesWeb),
            CoverRules.DecideVolume(false, CoverCropSide.Right, Base, null, Web(Away(4))));
        var spread = CoverRules.DecideVolume(true, CoverCropSide.Right, Base, Away(40), Web(Away(4)));
        Assert.Equal((AutoCoverSource.Crop, CoverCropSide.Right, AutoCoverReason.FileMatchesWeb), (spread.Source, spread.CropSide, spread.Reason));
    }

    [Fact]
    public void Volume_ClearlyDifferent_TakesTheWebCover()
    {
        var web = Web(Away(32));
        var d = CoverRules.DecideVolume(false, CoverCropSide.Right, Base, null, web);
        Assert.Equal(AutoCoverSource.WebVolume, d.Source);
        Assert.Equal(AutoCoverReason.LocalNotCover, d.Reason);
        Assert.Same(web, d.Web);
    }

    [Fact]
    public void Volume_ClearlyDifferentFromAnOriginLanguageFallback_KeepsTheLocalCover()
    {
        // 1.30.0 soak test: an official English volume 1 next to MangaDex's Japanese volume 1 cover (no English one listed).
        var d = CoverRules.DecideVolume(false, CoverCropSide.Right, Base, null, Web(Away(32), originFallback: true));
        Assert.Equal((AutoCoverSource.File, AutoCoverReason.OtherLanguageKept), (d.Source, d.Reason));
        Assert.False(d.NeedsRecheck); // nothing web is used; a preferred-language cover arriving later changes the inputs
        var spread = CoverRules.DecideVolume(true, CoverCropSide.Left, Base, Away(64), Web(Away(32), originFallback: true));
        Assert.Equal((AutoCoverSource.Crop, CoverCropSide.Left, AutoCoverReason.OtherLanguageKept), (spread.Source, spread.CropSide, spread.Reason));
    }

    [Fact]
    public void Volume_Uncertain_KeepsTheLocalCover()
    {
        var d = CoverRules.DecideVolume(false, CoverCropSide.Right, Base, null, Web(Away(15)));
        Assert.Equal(AutoCoverSource.File, d.Source);
        Assert.Equal(AutoCoverReason.UncertainKept, d.Reason);
    }

    [Fact]
    public void Volume_Spread_OtherHalfMatches_TakesTheOtherHalf()
    {
        // The direction guess was wrong: the right half is the front.
        var otherHalf = Away(40) ^ 0xFFFF_0000_0000_0000UL;
        var web = Web(otherHalf ^ 0b111);
        var d = CoverRules.DecideVolume(true, CoverCropSide.Left, Base, otherHalf, web);
        Assert.Equal((AutoCoverSource.Crop, CoverCropSide.Right, AutoCoverReason.SpreadOtherSide), (d.Source, d.CropSide, d.Reason));
    }

    [Fact]
    public void Volume_MissingLocalHash_KeepsTheLocalCover()
    {
        var d = CoverRules.DecideVolume(false, CoverCropSide.Right, null, null, Web(Base));
        Assert.Equal(AutoCoverReason.UncertainKept, d.Reason);
        Assert.Equal(AutoCoverSource.File, d.Source);
    }

    [Fact]
    public void OriginLanguageWebCover_AsksForARecheck_WhereItIsUsed()
    {
        // A series folder without a local volume 1 uses the original-language volume 1 cover and re-checks it later.
        Assert.True(CoverRules.DecideSeriesFolder(null, false, Web(Away(40), originFallback: true), null, null).NeedsRecheck);
        // A volume never uses it over its own cover (1.30.0), so nothing is re-checked.
        Assert.False(CoverRules.DecideVolume(false, CoverCropSide.Right, Base, null, Web(Away(40), originFallback: true)).NeedsRecheck);
        Assert.False(CoverRules.DecideVolume(false, CoverCropSide.Right, Base, null, Web(Away(4), originFallback: true)).NeedsRecheck);
    }

    // ----- one-shots ------------------------------------------------------------------------------------------------

    [Fact]
    public void OneShot_WebByDefault_InChainOrder()
    {
        var volume1 = Web(Away(30), id: 1);
        var main = Web(Away(30), AutoCoverSource.WebMain, id: 2);
        var poster = Web(Away(30), AutoCoverSource.Poster);
        Assert.Same(volume1, CoverRules.DecideOneShot(Base, [volume1, main, poster]).Web);
        Assert.Same(main, CoverRules.DecideOneShot(Base, [null, main, poster]).Web);
        var d = CoverRules.DecideOneShot(Base, [null, null, poster]);
        Assert.Equal((AutoCoverSource.Poster, AutoCoverReason.OneShotDefault), (d.Source, d.Reason));
    }

    [Fact]
    public void OneShot_PageOneAlreadyTheCover_KeepsTheFile_UncertainTakesTheWeb()
    {
        Assert.Equal(AutoCoverSource.File, CoverRules.DecideOneShot(Base, [Web(Away(9), AutoCoverSource.Poster)]).Source);
        Assert.Equal(AutoCoverSource.Poster, CoverRules.DecideOneShot(Base, [Web(Away(14), AutoCoverSource.Poster)]).Source);
        Assert.Equal(AutoCoverSource.Poster, CoverRules.DecideOneShot(null, [Web(null, AutoCoverSource.Poster)]).Source);
    }

    [Fact]
    public void OneShot_NoWebCover_KeepsTheFile() =>
        Assert.Equal(new CoverDecision(AutoCoverSource.File, AutoCoverReason.NoWebCover), CoverRules.DecideOneShot(Base, [null, null, null]));

    // ----- series folders -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(3, false)]   // volume 1's page 1 is the web cover
    [InlineData(15, false)]  // uncertain: volume 1 keeps its own cover, so does the folder (1.30.0)
    [InlineData(35, false)]  // different: volume 1's own decision takes the web cover - the folder shows volume 1 either way
    [InlineData(35, true)]   // only an original-language web cover: never over your volume 1 (1.30.0 soak test)
    public void SeriesFolder_WithALocalVolume1_ShowsThatArchivesResolvedCover(int bits, bool originFallback)
    {
        // 1.30.0 (owner soak test): the folder showed chapter 1's page 1 (the first file by name in Series/Chapters/) while it
        // compared volume 1. Now it names volume 1's archive; the resolver shows that archive's own resolved cover.
        var d = CoverRules.DecideSeriesFolder(42, false, Web(Away(bits), originFallback: originFallback), Web(Base, AutoCoverSource.WebMain),
            Web(null, AutoCoverSource.Poster));
        Assert.Equal(new CoverDecision(AutoCoverSource.LocalVolume1, AutoCoverReason.SeriesLocalVolume1, ArchiveNodeId: 42), d);
        Assert.False(d.NeedsRecheck);
    }

    [Fact]
    public void SeriesFolder_WithALocalVolume1_AndNoWebCoverAtAll_StillNamesVolume1()
    {
        // Not the main cover / poster (they usually show the newest volume) and not the first file by name.
        var d = CoverRules.DecideSeriesFolder(7, true, null, null, null);
        Assert.Equal((AutoCoverSource.LocalVolume1, 7L), (d.Source, d.ArchiveNodeId));
    }

    [Fact]
    public void SeriesFolder_WebVolume1OnlyInTheOriginLanguage_WithoutALocalVolume1_StandsIn() =>
        Assert.Equal(AutoCoverSource.WebVolume, CoverRules.DecideSeriesFolder(null, false, Web(Away(35), originFallback: true), null, null).Source);

    [Fact]
    public void SeriesFolder_NoLocalVolume1_WebVolume1_ThenMain_ThenPoster_ThenFile()
    {
        var w1 = Web(Base, id: 1);
        var main = Web(Base, AutoCoverSource.WebMain, id: 2);
        var poster = Web(null, AutoCoverSource.Poster);
        Assert.Same(w1, CoverRules.DecideSeriesFolder(null, false, w1, main, poster).Web);
        Assert.Same(main, CoverRules.DecideSeriesFolder(null, true, null, main, poster).Web);
        Assert.Same(poster, CoverRules.DecideSeriesFolder(null, true, null, null, poster).Web);
        Assert.Equal(AutoCoverSource.File, CoverRules.DecideSeriesFolder(null, true, null, null, null).Source);
        Assert.Equal(AutoCoverReason.ChapterFolderDefault, CoverRules.DecideSeriesFolder(null, true, w1, null, null).Reason);
        Assert.Equal(AutoCoverReason.SeriesVolume1, CoverRules.DecideSeriesFolder(null, false, w1, null, null).Reason);
    }

    [Fact]
    public void Webtoon_MainThenPosterThenFile()
    {
        var main = Web(Base, AutoCoverSource.WebMain);
        var poster = Web(null, AutoCoverSource.Poster);
        Assert.Equal((AutoCoverSource.WebMain, AutoCoverReason.WebtoonDefault),
            (CoverRules.DecideWebtoon(main, poster).Source, CoverRules.DecideWebtoon(main, poster).Reason));
        Assert.Equal(AutoCoverSource.Poster, CoverRules.DecideWebtoon(null, poster).Source);
        Assert.Equal(AutoCoverSource.File, CoverRules.DecideWebtoon(null, null).Source);
    }

    [Fact]
    public void Subfolder_TakesTheVolumeItStartsIn()
    {
        Assert.Equal(AutoCoverReason.SubfolderFirstVolume, CoverRules.DecideSubfolder(Web(Base)).Reason);
        Assert.Equal(AutoCoverSource.File, CoverRules.DecideSubfolder(null).Source);
    }

    // ----- language -------------------------------------------------------------------------------------------------

    [Fact]
    public void PickLanguage_PreferredThenOrigin_FlagsTheFallback()
    {
        var en = Web(Base, id: 1);
        var ja = Web(Base, id: 2);
        var fr = Web(Base, id: 3);
        Assert.Same(en, CoverRules.PickLanguage([("ja", ja), ("en", en)], "en", ["ja"]));
        var fallback = CoverRules.PickLanguage([("ja", ja), ("fr", fr)], "en", ["ja"]);
        Assert.Equal(2, fallback!.VolumeCoverId);
        Assert.True(fallback.OriginFallback);
        Assert.Null(CoverRules.PickLanguage([("fr", fr)], "en", ["ja"]));
        Assert.Same(en, CoverRules.PickLanguage([("EN", en)], "en", []));
    }

    [Fact]
    public void OriginLocales_FollowTheRecordOrigin()
    {
        Assert.Equal(["ja"], CoverRules.OriginLocales(MetadataOrigin.Japan));
        Assert.Equal(["ko"], CoverRules.OriginLocales(MetadataOrigin.Korea));
        Assert.Contains("zh-hk", CoverRules.OriginLocales(MetadataOrigin.ChinaTaiwan));
        Assert.Contains("ja", CoverRules.OriginLocales(null));
        Assert.Empty(CoverRules.OriginLocales(MetadataOrigin.French));
    }
}
