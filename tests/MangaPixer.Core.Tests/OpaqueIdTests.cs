namespace com.lifepixer.mangaplex.Tests.Core.Catalog;

using com.lifepixer.mangaplex.Core.Catalog;
using Xunit;

/// <summary>
/// Tests for opaque ID encoding/decoding and round-trip behavior.
/// </summary>
public sealed class OpaqueIdTests
{
    [Theory]
    [InlineData(0L, "0")]
    [InlineData(1L, "1")]
    [InlineData(10L, "a")]
    [InlineData(35L, "z")]
    [InlineData(36L, "10")]
    [InlineData(100L, "2s")]
    [InlineData(1000L, "rs")]
    [InlineData(long.MaxValue, "1y2p0ij32e8e7")]
    public void Encode_KnownValues_ReturnsExpectedString(long value, string expected)
    {
        Assert.Equal(expected, OpaqueId.Encode(value));
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("1", 1L)]
    [InlineData("a", 10L)]
    [InlineData("z", 35L)]
    [InlineData("10", 36L)]
    [InlineData("2s", 100L)]
    [InlineData("rs", 1000L)]
    public void Decode_KnownStrings_ReturnsExpectedValue(string opaque, long expected)
    {
        Assert.Equal(expected, OpaqueId.Decode(opaque));
    }

    [Fact]
    public void EncodeDecode_RoundTrip_PreservesValue()
    {
        var values = new[] { 0L, 1L, 42L, 1000L, 999999L, long.MaxValue / 2, long.MaxValue };
        foreach (var value in values)
        {
            var encoded = OpaqueId.Encode(value);
            var decoded = OpaqueId.Decode(encoded);
            Assert.Equal(value, decoded);
        }
    }

    [Fact]
    public void EncodeDecode_NegativeValues_RoundTrip()
    {
        var values = new[] { -1L, -42L, -1000L, long.MinValue / 2 };
        foreach (var value in values)
        {
            var encoded = OpaqueId.Encode(value);
            var decoded = OpaqueId.Decode(encoded);
            Assert.Equal(value, decoded);
        }
    }

    [Theory]
    [InlineData("ABC")]
    [InlineData("AbC")]
    [InlineData("aBc")]
    public void Decode_CaseInsensitive_AcceptsUppercase(string input)
    {
        // base36 is case-insensitive
        var lower = OpaqueId.Decode(input.ToLowerInvariant());
        var actual = OpaqueId.Decode(input);
        Assert.Equal(lower, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Decode_EmptyOrNull_Throws(string? input)
    {
        Assert.Throws<ArgumentException>(() => OpaqueId.Decode(input!));
    }

    [Fact]
    public void Decode_InvalidCharacter_Throws()
    {
        Assert.Throws<ArgumentException>(() => OpaqueId.Decode("abc!123"));
    }

    [Fact]
    public void LibraryId_RoundTrip_PreservesValue()
    {
        var id = new LibraryId(12345);
        var opaque = id.ToOpaque();
        var restored = LibraryId.FromOpaque(opaque);
        Assert.Equal(id, restored);
    }

    [Fact]
    public void CatalogNodeId_RoundTrip_PreservesValue()
    {
        var id = new CatalogNodeId(67890);
        var opaque = id.ToOpaque();
        var restored = CatalogNodeId.FromOpaque(opaque);
        Assert.Equal(id, restored);
    }

    [Fact]
    public void ItemId_RoundTrip_PreservesValue()
    {
        var id = new ItemId(11111);
        var opaque = id.ToOpaque();
        var restored = ItemId.FromOpaque(opaque);
        Assert.Equal(id, restored);
    }

    [Fact]
    public void UserId_RoundTrip_PreservesValue()
    {
        var id = new UserId(22222);
        var opaque = id.ToOpaque();
        var restored = UserId.FromOpaque(opaque);
        Assert.Equal(id, restored);
    }

    [Fact]
    public void PageEntryKey_RoundTrip_PreservesValue()
    {
        var key = new PageEntryKey(42);
        var opaque = key.ToOpaque();
        var restored = PageEntryKey.FromOpaque(opaque);
        Assert.Equal(key, restored);
    }

    [Fact]
    public void PageIndex_ValidIndex_IsValidTrue()
    {
        Assert.True(new PageIndex(0).IsValid);
        Assert.True(new PageIndex(1).IsValid);
        Assert.True(new PageIndex(100).IsValid);
    }

    [Fact]
    public void PageIndex_NegativeIndex_IsValidFalse()
    {
        Assert.False(new PageIndex(-1).IsValid);
        Assert.False(new PageIndex(-100).IsValid);
    }
}
