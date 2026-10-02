namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;

/// <summary>
/// The comics evidence of 1.32.0 (lane B): the pure rules (shape from page counts and format words, language codes, publisher
/// names, distinct issue numbers) and the scorer's comics additions - start year, language, shape and publisher - which read
/// Grand Comics Database records ONLY (a manga record scores exactly as before) and never change the raw title score or the bands.
/// Synthetic records; the "editions" cases mirror recorded GCD shapes (one series per edition and translation).
/// </summary>
public sealed class ComicsEvidenceTests
{
    private readonly MatchScorer _scorer = new();

    private static MatchCandidate Gcd(string id, string title, int? year, string? language, ComicsShape shape, string? publisher = null,
        string origin = "EnglishOriginal") =>
        new("gcd", id, title, [], MetadataFormat.Comic, origin, year, null, null, [], [], false, null, null, null,
            language, shape, publisher is null ? [] : [publisher]);

    private static MatchQuery Query(string title, ComicsEvidence? evidence, int? earliestYear = null) =>
        new([new QueryVariant(title, QueryVariantKind.Primary)],
            new MatchContext(WorkClass.Series, 6, 0, 0, earliestYear, "comics", false, [], ComicsEvidence: evidence));

    // --- Rules ---

    [Theory]
    [InlineData(new[] { "Saga 001.cbz", "Saga 002.cbz" }, 32, ComicsShape.Issues)]
    [InlineData(new[] { "Saga 001.cbz", "Saga 002.cbz" }, 48, ComicsShape.Issues)]
    [InlineData(new[] { "Saga v01.cbz", "Saga v02.cbz" }, 160, ComicsShape.Collected)]
    [InlineData(new[] { "Saga v01.cbz", "Saga v02.cbz" }, 70, ComicsShape.Unknown)]
    [InlineData(new[] { "Saga TPB 01.cbz", "Saga TPB 02.cbz" }, 30, ComicsShape.Collected)]
    [InlineData(new[] { "Blacksad - Intégrale.cbz" }, null, ComicsShape.Collected)]
    [InlineData(new[] { "A.cbz" }, null, ComicsShape.Unknown)]
    public void ShapeOf_FormatWordsThenMedianPages(string[] names, int? median, ComicsShape shape)
    {
        Assert.Equal(shape, ComicsEvidenceRules.ShapeOf(names, median));
    }

    [Fact]
    public void Median_IgnoresEmptyPages()
    {
        Assert.Equal(28, ComicsEvidenceRules.Median([28, 24, 0, 200, 30]));
        Assert.Null(ComicsEvidenceRules.Median([]));
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("EN-us", "en")]
    [InlineData("fr_BE", "fr")]
    [InlineData("eng", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void LanguageCode(string? value, string? code)
    {
        Assert.Equal(code, ComicsEvidenceRules.LanguageCode(value));
    }

    [Theory]
    [InlineData("Image Comics", "Image", true)]
    [InlineData("Dark Horse Books", "dark horse", true)]
    [InlineData("Dargaud", "Dupuis", false)]
    [InlineData("", "", false)]
    public void PublishersEqual(string a, string b, bool equal)
    {
        Assert.Equal(equal, ComicsEvidenceRules.PublishersEqual(a, b));
    }

    [Fact]
    public void DistinctIssueNumbers_PrintingsAndVariantsCountOnce()
    {
        var numbers = ComicsEvidenceRules.DistinctIssueNumbers(
            ["1 [First Printing]", "1 [Second Printing]", "2", "2 [Variant]", "3 - Ame rouge", "[1] [Andra upplagan]", "", "[nn]", "4.5"],
            out var unnumbered);
        Assert.Equal([1m, 2m, 3m, 4.5m], numbers);
        Assert.Equal(1, unnumbered);
    }

    // --- Scoring ---

    [Fact]
    public void Editions_LanguageYearAndShapeSeparateSameNamedSeries()
    {
        // Blacksad-like: the French original (2000, albums), its US edition (2003, graphic novels), a Danish one (2004).
        var fr = Gcd("51161", "Blacksad", 2000, "fr", ComicsShape.Collected, origin: "French");
        var us = Gcd("51169", "Blacksad", 2003, "en", ComicsShape.Collected);
        var dk = Gcd("138839", "Blacksad", 2004, "da", ComicsShape.Unknown, origin: "Nordic");
        var evidence = new ComicsEvidence(2003, "en", ComicsShape.Collected, null);

        var o = _scorer.Score(Query("Blacksad", evidence), [fr, us, dk], MatchThresholds.Default);

        Assert.Equal("51169", o.Ranked[0].Candidate.ExternalId);
        Assert.Equal(1.0, o.Ranked[0].TitleScore, 3); // The raw title is untouched.
        Assert.True((o.Ranked[0].Reasons & MatchReason.ComicsStartYear) != 0);
        Assert.True((o.Ranked.Single(r => r.Candidate.ExternalId == "51161").Reasons & MatchReason.ComicsLanguageMismatch) != 0);
        // 0.05 (year) + 0.03 (language) + 0.03 (shape) over the French edition's -0.05: past the margin, an automatic link.
        Assert.Equal(MatchBand.Auto, o.Band);
    }

    [Fact]
    public void Editions_WithoutEvidence_StayAReview()
    {
        var fr = Gcd("51161", "Blacksad", 2000, "fr", ComicsShape.Collected, origin: "French");
        var us = Gcd("51169", "Blacksad", 2003, "en", ComicsShape.Collected);

        var o = _scorer.Score(Query("Blacksad", null), [fr, us], MatchThresholds.Default);

        Assert.Equal(MatchBand.NeedsReview, o.Band);
        Assert.True((o.Ranked[0].Reasons & MatchReason.CloseSecond) != 0);
    }

    [Fact]
    public void IssueRunVsTrades_ShapeDecides()
    {
        var run = Gcd("63051", "Saga", 2012, "en", ComicsShape.Issues, "Image");
        var tpb = Gcd("69146", "Saga", 2012, "en", ComicsShape.Collected, "Image");

        var issues = _scorer.Score(Query("Saga", new ComicsEvidence(2012, "en", ComicsShape.Issues, null)), [run, tpb], MatchThresholds.Default);
        Assert.Equal("63051", issues.Ranked[0].Candidate.ExternalId);
        Assert.True((issues.Ranked[1].Reasons & MatchReason.ComicsShapeMismatch) != 0);

        var books = _scorer.Score(Query("Saga", new ComicsEvidence(2012, "en", ComicsShape.Collected, null)), [run, tpb], MatchThresholds.Default);
        Assert.Equal("69146", books.Ranked[0].Candidate.ExternalId);
    }

    [Fact]
    public void Publisher_AgreesWithComicInfo()
    {
        var a = Gcd("1", "Watchmen", 1986, "en", ComicsShape.Issues, "DC");
        var b = Gcd("2", "Watchmen", 1986, "en", ComicsShape.Issues, "Panini");

        var o = _scorer.Score(Query("Watchmen", new ComicsEvidence(null, null, ComicsShape.Unknown, "DC Comics")), [a, b], MatchThresholds.Default);

        Assert.Equal("1", o.Ranked[0].Candidate.ExternalId);
        Assert.True((o.Ranked[0].Reasons & MatchReason.ComicsPublisherAgree) != 0);
        Assert.Equal(MatchScorer.ComicsPublisherAgree, o.Ranked[0].AdjustedScore - o.Ranked[1].AdjustedScore, 6);
    }

    [Fact]
    public void StartYear_OneOffIsNear_FurtherIsNothing()
    {
        var evidence = new ComicsEvidence(1991, null, ComicsShape.Unknown, null);
        var near = _scorer.Score(Query("Bone", evidence), [Gcd("1", "Bone", 1992, null, ComicsShape.Unknown)], MatchThresholds.Default);
        var far = _scorer.Score(Query("Bone", evidence), [Gcd("1", "Bone", 1995, null, ComicsShape.Unknown)], MatchThresholds.Default);
        Assert.Equal(MatchScorer.ComicsStartYearNear, near.Ranked[0].AdjustedScore - near.Ranked[0].TitleScore, 6);
        Assert.Equal(0, far.Ranked[0].AdjustedScore - far.Ranked[0].TitleScore, 6);
    }

    [Fact]
    public void MangaRecords_NeverReadComicsEvidence()
    {
        var manga = new MatchCandidate("mangaupdates", "9", "Blacksad", [], MetadataFormat.Comic, "Manga", 2003, null, null, [], [],
            Language: "fr", Shape: ComicsShape.Issues);
        var evidence = new ComicsEvidence(2003, "en", ComicsShape.Collected, null);

        var with = _scorer.Score(Query("Blacksad", evidence), [manga], MatchThresholds.Default);
        var without = _scorer.Score(Query("Blacksad", null), [manga], MatchThresholds.Default);

        Assert.Equal(without.Ranked[0].AdjustedScore, with.Ranked[0].AdjustedScore, 9);
        Assert.Equal(without.Ranked[0].Reasons, with.Ranked[0].Reasons);
    }
}
