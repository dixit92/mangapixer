namespace com.lifepixer.mangapixer.Tests.Core.Reading;

using com.lifepixer.mangapixer.Core.Reading;
using Xunit;

/// <summary>
/// Unit tests for the Home row order rule (1.54.0): what an order means on write and on read - unknown and repeated ids dropped, the
/// missing ids appended in the default order, the default order stored as null.
/// </summary>
public sealed class HomeRowsTests
{
    private static readonly string[] Default = ["continue", "recommended", "favorites", "recent", "libraries"];

    [Fact]
    public void DefaultOrder_IsTheHomeOrderBefore154() => Assert.Equal(Default, HomeRows.DefaultOrder);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Normalize_NullOrEmpty_IsTheDefaultOrder(string? csv) =>
        Assert.Equal(Default, HomeRows.Normalize(csv is null ? null : []));

    [Fact]
    public void Normalize_KeepsAFullOrderAsGiven()
    {
        string[] order = ["libraries", "recent", "favorites", "recommended", "continue"];
        Assert.Equal(order, HomeRows.Normalize(order));
    }

    [Fact]
    public void Normalize_DropsUnknownRepeatedAndNullIds_AndAppendsTheMissingInTheDefaultOrder() =>
        Assert.Equal(["recent", "libraries", "continue", "recommended", "favorites"],
            HomeRows.Normalize(["recent", "bogus", null, "recent", "Libraries", "libraries"]));

    [Fact]
    public void ToStored_DefaultIsNull_OtherwiseTheNormalizedIdsJoinedByCommas()
    {
        Assert.Null(HomeRows.ToStored(Default));
        Assert.Null(HomeRows.ToStored([]));
        Assert.Null(HomeRows.ToStored(["continue", "recommended"])); // a prefix of the default normalizes to the default
        Assert.Equal("libraries,continue,recommended,favorites,recent", HomeRows.ToStored(["libraries"]));
    }

    [Fact]
    public void FromStored_RoundTripsAndToleratesAStoredRowThatNoLongerExists()
    {
        Assert.Equal(Default, HomeRows.FromStored(null));
        Assert.Equal(["libraries", "continue", "recommended", "favorites", "recent"],
            HomeRows.FromStored("libraries,continue,recommended,favorites,recent"));
        Assert.Equal(["favorites", "continue", "recommended", "recent", "libraries"], HomeRows.FromStored("old-row,favorites"));
    }
}
