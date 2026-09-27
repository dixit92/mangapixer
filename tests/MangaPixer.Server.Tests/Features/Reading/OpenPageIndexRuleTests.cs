namespace com.lifepixer.mangapixer.Tests.Server.Features.Reading;

using com.lifepixer.mangapixer.Server.Features.Reading;
using Xunit;

/// <summary>
/// Unit tests for the open-position rule, exercising the pure
/// <see cref="ReadingStateService.ComputeOpenPageIndex"/> decision directly. The rule
/// keys off POSITION (Ordinal vs PageCount), not the Completed enum, so it is robust to
/// the re-read State-flip.
/// </summary>
public sealed class OpenPageIndexRuleTests
{
    // No read-mark (Unread/Reading): always resume exactly where the user left off,
    // regardless of the preference.
    [Theory]
    [InlineData(0, 10, false, 0)]
    [InlineData(3, 10, false, 3)]
    [InlineData(3, 10, true, 3)]
    [InlineData(9, 10, true, 9)]
    public void NoMark_AlwaysResumes(int ordinal, int pageCount, bool alwaysFromStart, int expected)
    {
        Assert.Equal(expected, ReadingStateService.ComputeOpenPageIndex(
            hasMark: false, ordinal, pageCount, alwaysFromStart));
    }

    // Read archive finished on the last page: always reopen at page 1, even with the
    // option off (this is the "finished" case rule 2 and the manual-mark rule 3 rely on).
    [Theory]
    [InlineData(9, 10, false)]
    [InlineData(9, 10, true)]
    [InlineData(10, 10, false)] // ordinal past the last index (defensive) still counts as finished
    public void Mark_LastPage_OpensAtStart(int ordinal, int pageCount, bool alwaysFromStart)
    {
        Assert.Equal(0, ReadingStateService.ComputeOpenPageIndex(
            hasMark: true, ordinal, pageCount, alwaysFromStart));
    }

    // 1.27.0 near the end counts as the end (NearEndRule): a read archive saved within
    // the last n = min(5, max(1, 5%)) pages reopens at page 1 with the option off.
    [Theory]
    [InlineData(18, 20)]   // 19/20 - owner example
    [InlineData(19, 21)]   // 20/21
    [InlineData(94, 100)]  // 95/100
    [InlineData(194, 200)] // 195/200 - owner example
    [InlineData(1, 2)]     // two pages: only the last one
    [InlineData(0, 1)]
    public void Mark_NearTheEnd_OpensAtStart(int ordinal, int pageCount)
        => Assert.Equal(0, ReadingStateService.ComputeOpenPageIndex(
            hasMark: true, ordinal, pageCount, alwaysOpenReadFromStart: false));

    // Just outside the near-end zone: a read archive resumes the saved spot (option off).
    [Theory]
    [InlineData(17, 20)]   // 18/20
    [InlineData(18, 21)]   // 19/21
    [InlineData(93, 100)]  // 94/100
    [InlineData(189, 200)] // 190/200 - owner example: resume mid
    [InlineData(193, 200)] // 194/200
    [InlineData(0, 2)]     // page 1 of 2 is never the end
    public void Mark_JustBeforeTheEnd_Resumes(int ordinal, int pageCount)
        => Assert.Equal(ordinal, ReadingStateService.ComputeOpenPageIndex(
            hasMark: true, ordinal, pageCount, alwaysOpenReadFromStart: false));

    // Read archive, mid-archive saved position: resume when the option is OFF (default),
    // start from page 1 when it is ON.
    [Fact]
    public void Mark_MidArchive_OptionOff_Resumes()
        => Assert.Equal(4, ReadingStateService.ComputeOpenPageIndex(
            hasMark: true, ordinal: 4, pageCount: 10, alwaysOpenReadFromStart: false));

    [Fact]
    public void Mark_MidArchive_OptionOn_OpensAtStart()
        => Assert.Equal(0, ReadingStateService.ComputeOpenPageIndex(
            hasMark: true, ordinal: 4, pageCount: 10, alwaysOpenReadFromStart: true));

    // Read archive whose PageCount is unknown (cannot resolve "last page"): resume unless
    // opted in — mirrors the mid-archive branch.
    [Theory]
    [InlineData(null, false, 4)]
    [InlineData(null, true, 0)]
    [InlineData(0, false, 4)]
    public void Mark_UnknownOrZeroPageCount_FollowsOption(int? pageCount, bool alwaysFromStart, int expected)
    {
        Assert.Equal(expected, ReadingStateService.ComputeOpenPageIndex(
            hasMark: true, ordinal: 4, pageCount, alwaysFromStart));
    }
}
