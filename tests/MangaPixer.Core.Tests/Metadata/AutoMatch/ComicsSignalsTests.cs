namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;

/// <summary>Unit tests for the 1.32.0 step-0 <see cref="ComicsSignals"/> contract. Synthetic names only.</summary>
public sealed class ComicsSignalsTests
{
    private static ComicsSignalInput Input(DeclaredType? declared = null, string? category = null) =>
        new(declared, category, ["Some Series 001.cbz", "Some Series 002.cbz"]);

    [Theory]
    [InlineData(DeclaredType.Comic)]
    [InlineData(DeclaredType.GraphicNovel)]
    public void DeclaredComicOrGraphicNovel_IsAStrongSign_ThatRoutes(DeclaredType declared)
    {
        var signal = ComicsSignals.Of(Input(declared));
        Assert.Equal(ComicsSignalKind.DeclaredType, signal.Kinds);
        Assert.True(signal.RoutesToComics);
    }

    [Theory]
    [InlineData(DeclaredType.Manga)]
    [InlineData(DeclaredType.Manhwa)]
    [InlineData(DeclaredType.Webtoon)]
    [InlineData(null)]
    public void OtherDeclaredTypes_AndNoCategory_GiveNoSign(DeclaredType? declared)
    {
        var signal = ComicsSignals.Of(Input(declared));
        Assert.Same(ComicsSignal.None, signal);
        Assert.False(signal.RoutesToComics);
    }

    [Theory]
    [InlineData("Comics", true)]
    [InlineData("comic", true)]
    [InlineData("Manga", false)]
    [InlineData("Comics and Manga", false)]
    public void ComicsCategoryFolder_IsAStrongSign(string category, bool expected)
    {
        Assert.Equal(expected, ComicsSignals.Of(Input(category: category)).RoutesToComics);
    }

    [Fact]
    public void OneWeakSign_DoesNotRoute_TwoDo()
    {
        Assert.False(new ComicsSignal(ComicsSignalKind.IssueNumbering).RoutesToComics);
        Assert.False(new ComicsSignal(ComicsSignalKind.CollectedFormatWord).RoutesToComics);
        Assert.True(new ComicsSignal(ComicsSignalKind.IssueNumbering | ComicsSignalKind.StartYearAfterName).RoutesToComics);
        Assert.True(new ComicsSignal(ComicsSignalKind.WesternPublisher).RoutesToComics);
        Assert.True(new ComicsSignal(ComicsSignalKind.ComicsIdInComicInfo | ComicsSignalKind.IssueNumbering).RoutesToComics);
    }

    [Fact]
    public void StrongAndWeakMasks_CoverEveryKind_WithoutOverlap()
    {
        var all = Enum.GetValues<ComicsSignalKind>().Aggregate(ComicsSignalKind.None, (a, k) => a | k);
        Assert.Equal(ComicsSignal.Strong | ComicsSignal.Weak, all);
        Assert.Equal(ComicsSignalKind.None, ComicsSignal.Strong & ComicsSignal.Weak);
    }
}
