namespace com.lifepixer.mangapixer.Tests.Core;

using com.lifepixer.mangapixer.Core.Catalog;
using Xunit;

public sealed class LibraryIconsTests
{
    [Fact]
    public void IsValid_Null_ReturnsTrue()
    {
        Assert.True(LibraryIcons.IsValid(null));
    }

    [Theory]
    [InlineData("menu_book")]
    [InlineData("auto_stories")]
    [InlineData("star")]
    public void IsValid_AllowlistedName_ReturnsTrue(string icon)
    {
        Assert.True(LibraryIcons.IsValid(icon));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not_a_real_icon")]
    [InlineData("swords")]
    [InlineData("MENU_BOOK")]
    public void IsValid_UnknownOrWrongCaseName_ReturnsFalse(string icon)
    {
        Assert.False(LibraryIcons.IsValid(icon));
    }

    [Fact]
    public void Allowed_HasNoDuplicatesAndIsWithinCuratedRange()
    {
        Assert.Equal(LibraryIcons.Allowed.Count, LibraryIcons.Allowed.Distinct().Count());
        Assert.InRange(LibraryIcons.Allowed.Count, 24, 40);
    }
}
