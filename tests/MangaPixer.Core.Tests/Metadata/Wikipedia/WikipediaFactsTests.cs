namespace com.lifepixer.mangapixer.Tests.Core.Metadata.Wikipedia;

using com.lifepixer.mangapixer.Core.Metadata.Wikipedia;
using Xunit;

/// <summary>Dates and ISBNs as the list pages write them (1.32.0): synthetic strings only.</summary>
public sealed class WikipediaFactsTests
{
    [Theory]
    [InlineData("July 3, 2026", "2026-07-03")]
    [InlineData("28 February 2002", "2002-02-28")]
    [InlineData("February 2002", "2002-02")]
    [InlineData("2002-02-28", "2002-02-28")]
    [InlineData("Sept. 5, 2010", "2010-09-05")]
    [InlineData("{{dts|2002|Feb|28}}", "2002-02-28")]
    [InlineData("{{start date|2011|7|1}}", "2011-07-01")]
    [InlineData("March 3rd, 2015", "2015-03-03")]
    [InlineData("June 1, 2020<ref name=\"a\">{{cite web|url=http://example.invalid|access-date=May 5, 2019}}</ref>", "2020-06-01")]
    public void EarliestDate_ReadsTheCommonForms(string field, string expected) =>
        Assert.Equal(expected, WikipediaFacts.EarliestDate(field));

    [Fact]
    public void EarliestDate_TakesTheFirstOfSeveralPublishers() =>
        Assert.Equal("2005-06-06", WikipediaFacts.EarliestDate("September 2009 (Yen) <br /> 6 June 2005 (ADV)"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("—")]
    [InlineData("TBA")]
    [InlineData("31 February 2020 nonsense")]
    public void EarliestDate_IsNullForPlaceholdersAndImpossibleDates(string? field) => Assert.Null(WikipediaFacts.EarliestDate(field));

    [Theory]
    [InlineData("978-1-9747-2576-2", "9781974725762")]
    [InlineData("ISBN 9781974725762", "9781974725762")]
    [InlineData("{{ISBNT|978-0-316-07387-5}} (Yen)", "9780316073875")]
    [InlineData("1-4139-0317-X", null)]
    [InlineData("0-306-40615-2", "0306406152")]
    public void FirstIsbn_ReadsValidIsbnsOnly(string field, string? expected) => Assert.Equal(expected, WikipediaFacts.FirstIsbn(field));

    [Fact]
    public void FirstIsbn_PrefersAnIsbn13_AndRejectsABadCheckDigit()
    {
        Assert.Equal("9780316073875", WikipediaFacts.FirstIsbn("0-306-40615-2 and 978-0-316-07387-5"));
        Assert.Null(WikipediaFacts.FirstIsbn("978-1-9747-2576-3"));
    }

    [Theory]
    [InlineData("2026-12", 2026, 12, 1)]
    [InlineData("2026", 2026, 1, 1)]
    [InlineData("2026-12-08", 2026, 12, 8)]
    public void FirstDayOf_ReadsPartialDates(string text, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), WikipediaFacts.FirstDayOf(text));

    [Fact]
    public void FirstDayOf_IsNullForNonDates() => Assert.Null(WikipediaFacts.FirstDayOf("soon"));
}
