namespace com.lifepixer.mangapixer.Tests.Core.Metadata.Wikipedia;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Wikipedia;
using Xunit;

/// <summary>The two network-free decisions of the Wikipedia companion (1.32.0): which titles to read, and whether Wikipedia can add anything.</summary>
public sealed class WikipediaDiscoveryTests
{
    [Fact]
    public void AnArticle_IsReadWithTheUsualListTitles_TheDisambiguatorStripped() =>
        Assert.Equal(["Example Saga (manga)", "List of Example Saga chapters", "Lists of Example Saga chapters"],
            WikipediaDiscovery.TitlesToRead("Example Saga (manga)"));

    [Fact]
    public void ARecordTitleGuess_IsNeverReadAsAnArticle() =>
        Assert.Equal(["List of Example Saga chapters", "Lists of Example Saga chapters"],
            WikipediaDiscovery.TitlesToRead("Example Saga", includeArticle: false));

    [Theory]
    [InlineData("List of Example Saga chapters")]
    [InlineData("Lists of Example Saga chapters")]
    [InlineData("List_of_Example_Saga_chapters")]
    public void AListTitle_IsReadAlone(string title) => Assert.Single(WikipediaDiscovery.TitlesToRead(title));

    [Fact]
    public void Underscores_AreSpaces_AndABlankTitleReadsNothing()
    {
        Assert.Equal("Example Saga", WikipediaDiscovery.TitlesToRead("Example_Saga")[0]);
        Assert.Empty(WikipediaDiscovery.TitlesToRead("   "));
    }

    private static VolumeMapEntry Volume(int number) => new(number.ToString(), [(number * 2).ToString()]);

    [Fact]
    public void NoUsableMangaDexList_CanAlwaysBeAdded() =>
        Assert.True(WikipediaDiscovery.CanAdd(false, [], [], 10));

    [Fact]
    public void UnassignedChapters_OrMissingNewestVolumes_CanBeAdded()
    {
        var list = new[] { Volume(1), Volume(2), Volume(3) };
        Assert.True(WikipediaDiscovery.CanAdd(true, list, ["40"], 3)); // chapters left without a volume
        Assert.True(WikipediaDiscovery.CanAdd(true, list, [], 4)); // stops below the series' volume total
    }

    [Fact]
    public void ACompleteMangaDexList_AddsNothing()
    {
        var list = new[] { Volume(1), Volume(2), Volume(3) };
        Assert.False(WikipediaDiscovery.CanAdd(true, list, [], 3));
        Assert.False(WikipediaDiscovery.CanAdd(true, list, [], null)); // no total known: nothing says it is incomplete
    }
}
