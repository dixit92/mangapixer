namespace com.lifepixer.mangapixer.Tests.Core.Ordering;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Ordering;
using Xunit;

/// <summary>
/// Tests for the PERSISTED sort key (<see cref="SortKey"/>), as distinct from the
/// in-memory <see cref="NaturalOrderComparer"/>.
///
/// The shared contract is that ordinal comparison of encoded keys reproduces the
/// comparer's ordering, because the catalog is ordered by
/// <c>ORDER BY SortKey</c> under SQLite BINARY collation - there is no custom
/// collation at read time. Until 1.15.0 the encoder was never called from
/// production code, so nothing guarded that contract end-to-end and two encoding
/// defects went unnoticed: digit runs opened with 'D' (pushing every digit-leading
/// name behind the letters A-C) and the run length was a fixed two digits (which
/// mis-ordered runs of 100+ digits).
/// </summary>
public sealed class SortKeyEncodingTests
{
    /// <summary>
    /// Names whose ordering the encoded key must reproduce exactly. Deliberately
    /// mixes digit-leading, letter-leading, punctuation-leading and CJK names,
    /// leading zeros, multi-segment version numbers and case variants - every
    /// class of name the comparer's golden test covers, plus the digit-vs-letter
    /// cases it does not.
    /// </summary>
    private static readonly string[] Corpus =
    [
        "Chapter 1", "Chapter 2", "Chapter 10", "Chapter 100", "Chapter 1000",
        "Chapter 001", "Chapter 01",
        "Volume 1.5", "Volume 1", "Volume 2", "Volume 10", "Volume 10.5",
        "90s Classics", "9 Lives", "10 Tigers", "2000s Revival", "7",
        "Akira", "Zebra", "appendix", "Appendix",
        "@000.cbz", "[Bonus]", "-Extras", "_Staging", "!Urgent", " Leading space",
        "第1巻", "第2巻", "第10巻", "附录",
        "page001.png", "page01.png", "page1.png", "page2.png", "page010.png", "page100.png",
        "1-2", "1-10", "1.2.3", "1.2.10",
        "Vol.1", "Vol.2", "Vol.10",
        "cover.png", "Cover.png",
        "0", "00", "000", "1", "01", "001",
    ];

    /// <summary>
    /// THE contract: sorting by the encoded key under ordinal comparison (what SQLite
    /// does with BINARY collation) gives the same order as sorting with the comparer.
    /// </summary>
    [Fact]
    public void EncodeName_OrdinalOrder_MatchesNaturalOrderComparer()
    {
        var byComparer = Corpus.OrderBy(x => x, NaturalOrderComparer.Instance).ToArray();
        var byEncodedKey = Corpus.OrderBy(SortKey.EncodeName, StringComparer.Ordinal).ToArray();

        Assert.Equal(byComparer, byEncodedKey);
    }

    /// <summary>
    /// Every pair, not just the sorted sequence: a full ordering agreement check catches
    /// disagreements that happen to be masked by the corpus order.
    /// </summary>
    [Fact]
    public void EncodeName_AgreesWithComparer_ForEveryPair()
    {
        foreach (var x in Corpus)
        {
            foreach (var y in Corpus)
            {
                var comparer = Math.Sign(NaturalOrderComparer.Instance.Compare(x, y));
                var encoded = Math.Sign(SortKey.Compare(SortKey.EncodeName(x), SortKey.EncodeName(y)));
                Assert.True(
                    comparer == encoded,
                    $"Disagreement on (\"{x}\", \"{y}\"): comparer={comparer}, encoded key={encoded}");
            }
        }
    }

    /// <summary>
    /// The reported bug: name sort was not natural order because the persisted key was
    /// the RAW display name, under which "Chapter 10" precedes "Chapter 2" ordinally.
    /// </summary>
    [Fact]
    public void EncodeName_OrdersChapterNumbersNumerically()
    {
        var names = new[] { "Chapter 10", "Chapter 2", "Chapter 1", "Chapter 20", "Chapter 3" };
        var sorted = names.OrderBy(SortKey.EncodeName, StringComparer.Ordinal).ToArray();

        Assert.Equal(
            ["Chapter 1", "Chapter 2", "Chapter 3", "Chapter 10", "Chapter 20"],
            sorted);

        // And the raw key - the pre-1.15.0 behaviour - does not.
        Assert.NotEqual(sorted, names.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Regression for the 'D' digit-run marker. A digit compares against a letter
    /// ORDINALLY, and every ASCII digit sits below every letter, so a digit-leading
    /// name must sort before a letter-leading one. The old marker ('D') put such names
    /// between "C..." and "E..." instead.
    /// </summary>
    [Theory]
    [InlineData("90s Classics", "Akira")]
    [InlineData("10 Tigers", "Zebra")]
    [InlineData("2000s Revival", "Batman")]
    [InlineData("7", "Cover.png")]
    public void EncodeName_DigitLeadingNames_SortBeforeLetterLeadingNames(string digitLeading, string letterLeading)
    {
        Assert.True(SortKey.Compare(SortKey.EncodeName(digitLeading), SortKey.EncodeName(letterLeading)) < 0);
        // Same verdict as the comparer, which compares '9' vs 'A' ordinally.
        Assert.True(NaturalOrderComparer.Instance.Compare(digitLeading, letterLeading) < 0);
    }

    /// <summary>
    /// Characters below the digit range (punctuation, space) must still sort before
    /// digit-leading names, and characters above it after - the marker must not move a
    /// digit run out of its ordinal neighbourhood in either direction.
    /// </summary>
    [Theory]
    [InlineData("!Urgent", "5 Fingers")]
    [InlineData("-Extras", "5 Fingers")]
    [InlineData(" Leading", "5 Fingers")]
    [InlineData("#Tag", "5 Fingers")]
    public void EncodeName_PunctuationBelowDigits_SortsBeforeDigitLeadingNames(string punctuation, string digitLeading)
    {
        Assert.True(SortKey.Compare(SortKey.EncodeName(punctuation), SortKey.EncodeName(digitLeading)) < 0);
        Assert.True(NaturalOrderComparer.Instance.Compare(punctuation, digitLeading) < 0);
    }

    [Theory]
    [InlineData("@000.cbz", "5 Fingers")]
    [InlineData(":Colon", "5 Fingers")]
    public void EncodeName_PunctuationAboveDigits_SortsAfterDigitLeadingNames(string punctuation, string digitLeading)
    {
        Assert.True(SortKey.Compare(SortKey.EncodeName(punctuation), SortKey.EncodeName(digitLeading)) > 0);
        Assert.True(NaturalOrderComparer.Instance.Compare(punctuation, digitLeading) > 0);
    }

    /// <summary>
    /// Leading zeros are a tie-breaker BELOW the numeric value: 001 and 1 are the same
    /// number, and the one with more zeros comes first (deterministic, matches the
    /// comparer). Numeric value still wins over zero count.
    /// </summary>
    [Fact]
    public void EncodeName_LeadingZeros_BreakTiesWithoutOverridingValue()
    {
        var names = new[] { "page10", "page2", "page1", "page001", "page01", "page100", "page020" };
        var sorted = names.OrderBy(SortKey.EncodeName, StringComparer.Ordinal).ToArray();

        Assert.Equal(
            ["page001", "page01", "page1", "page2", "page10", "page020", "page100"],
            sorted);
    }

    /// <summary>
    /// Regression for the fixed two-digit run length. With "D02"-style widths, a run of
    /// 100 significant digits encoded its length as "100", which then compared against a
    /// 10-digit run's "10" + first digit and lost. The self-delimiting length fixes it.
    /// Values well past int64 are the point: the key orders by digit count, it never parses.
    /// </summary>
    [Fact]
    public void EncodeName_VeryLongDigitRuns_OrderByMagnitude()
    {
        var nine = new string('9', 9);
        var ten = "1" + new string('0', 9);          // 10 digits, smallest 10-digit value
        var ninetyNine = new string('9', 99);
        var hundred = "1" + new string('0', 99);     // 100 digits

        var names = new[] { hundred, nine, ninetyNine, ten };
        var sorted = names.OrderBy(SortKey.EncodeName, StringComparer.Ordinal).ToArray();

        Assert.Equal([nine, ten, ninetyNine, hundred], sorted);
        Assert.Equal(names.OrderBy(x => x, NaturalOrderComparer.Instance).ToArray(), sorted);
    }

    /// <summary>
    /// Regression for the leading-zero tie-breaker. It used to be a single character
    /// computed as ('9' - zeros), which walks below '0' after ten zeros and wraps past
    /// zero after 57 - at which point MORE zeros started sorting LAST. Ordering must stay
    /// monotonic (or at worst tie) no matter how many zeros are present.
    /// </summary>
    [Fact]
    public void EncodeName_ManyLeadingZeros_NeverInvertsOrder()
    {
        for (var zeros = 1; zeros <= 120; zeros++)
        {
            var more = new string('0', zeros) + "5";
            var fewer = new string('0', zeros - 1) + "5";
            var cmp = SortKey.Compare(SortKey.EncodeName(more), SortKey.EncodeName(fewer));
            Assert.True(cmp <= 0, $"{zeros} leading zeros sorted after {zeros - 1}");
        }
    }

    /// <summary>
    /// The kind prefix keeps folders ahead of archives at the same level, whatever the
    /// names are - browse and the jump rail both rely on it.
    /// </summary>
    [Fact]
    public void ForNode_FoldersSortBeforeArchives_RegardlessOfName()
    {
        var folder = SortKey.ForNode(CatalogNodeKind.Folder, "zzz Last Folder");
        var archive = SortKey.ForNode(CatalogNodeKind.Archive, "000 First Archive.cbz");

        Assert.True(SortKey.Compare(folder, archive) < 0);
    }

    /// <summary>
    /// Within one kind, the prefix is fixed-width so it cannot disturb name ordering.
    /// </summary>
    [Fact]
    public void ForNode_WithinAKind_OrdersNaturallyByName()
    {
        var names = new[] { "Chapter 10.cbz", "Chapter 2.cbz", "Chapter 1.cbz" };
        var sorted = names
            .OrderBy(n => SortKey.ForNode(CatalogNodeKind.Archive, n), StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Chapter 1.cbz", "Chapter 2.cbz", "Chapter 10.cbz"], sorted);
    }

    /// <summary>
    /// The encoder is a pure function of (kind, name): the scanner and the backfill
    /// migration both depend on recomputing the identical key from the stored display name.
    /// </summary>
    [Fact]
    public void ForNode_IsDeterministic()
    {
        Assert.Equal(
            SortKey.ForNode(CatalogNodeKind.Archive, "Chapter 007.cbz"),
            SortKey.ForNode(CatalogNodeKind.Archive, "Chapter 007.cbz"));
    }

    [Fact]
    public void EncodeName_EmptyName_IsEmpty()
    {
        Assert.Equal(string.Empty, SortKey.EncodeName(string.Empty));
    }
}
