namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;

/// <summary>Unit tests for <see cref="ArchiveNameAnatomy"/> (stage 2). Synthetic names only.</summary>
public sealed class ArchiveNameAnatomyTests
{
    [Fact]
    public void Parse_FullDoujinAnatomy()
    {
        var a = ArchiveNameAnatomy.Parse("(Event 12) [Some Circle (Some Artist)] A Short Story (Some Parody) [English] [Digital].zip");

        Assert.Equal("Event 12", a.Event);
        Assert.Equal("Some Circle", a.Circle);
        Assert.Equal("Some Artist", a.Artist);
        Assert.Equal("A Short Story", a.Title);
        Assert.Equal("Some Parody", a.Parody);
        Assert.False(a.HasUnitToken);
        Assert.True(a.IsDoujinShaped);
        Assert.Equal(["Some Circle", "Some Artist"], a.CreatorTags);
    }

    [Fact]
    public void Parse_LoneTag_IsACreatorTag_ButNotDoujinWithoutParody()
    {
        var a = ArchiveNameAnatomy.Parse("[Some Artist] A Short Story.cbz");

        Assert.Equal("Some Artist", a.LeadingTag);
        Assert.Null(a.Circle);
        Assert.Equal(["Some Artist"], a.CreatorTags);
        Assert.False(a.IsDoujinShaped);
    }

    [Fact]
    public void Parse_SceneRelease_IsNotDoujin()
    {
        var a = ArchiveNameAnatomy.Parse("[Group] Some Series v01 (2019) (Digital) (Scan Team).cbz");

        Assert.True(a.HasUnitToken);
        Assert.False(a.IsDoujinShaped);
    }

    [Fact]
    public void Parse_YearAndReleaseTags_AreNeverAParody()
    {
        var a = ArchiveNameAnatomy.Parse("[Some Artist] A Short Story (2015) (Decensored) (Colorized).cbz");

        Assert.Null(a.Parody);
    }

    [Fact]
    public void Parse_MultipleArtists_AreSplit()
    {
        var a = ArchiveNameAnatomy.Parse("[Circle Name (First Artist, Second Artist)] Story.cbz");

        Assert.Equal(["Circle Name", "First Artist", "Second Artist"], a.CreatorTags);
    }

    [Fact]
    public void Parse_PlainName_HasNoAnatomy()
    {
        var a = ArchiveNameAnatomy.Parse("Some Series 03.cbz");

        Assert.Null(a.LeadingTag);
        Assert.Null(a.Event);
        Assert.Empty(a.CreatorTags);
        Assert.False(a.IsDoujinShaped);
    }
}
