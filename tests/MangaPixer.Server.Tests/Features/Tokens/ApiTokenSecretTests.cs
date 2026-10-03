namespace com.lifepixer.mangapixer.Tests.Server.Features.Tokens;

using System.Security.Cryptography;
using System.Text;
using com.lifepixer.mangapixer.Server.Features.Tokens;
using Xunit;

/// <summary>Unit tests of the token format, display prefix and hash (1.33.0).</summary>
public sealed class ApiTokenSecretTests
{
    [Fact]
    public void Generate_ProducesTheMarkedBase64UrlShape_AndAPrefixOfIt()
    {
        var (token, prefix, hash) = ApiTokenSecret.Generate();

        Assert.Equal(ApiTokenSecret.Length, token.Length);
        Assert.StartsWith("mpx_", token, StringComparison.Ordinal);
        Assert.Matches("^mpx_[A-Za-z0-9_-]{43}$", token);
        Assert.True(ApiTokenSecret.IsWellFormed(token));
        Assert.Equal(token[..8], prefix);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, ApiTokenSecret.Hash(token));
    }

    [Fact]
    public void Generate_NeverRepeats()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => ApiTokenSecret.Generate().Token).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(200, tokens.Count);
    }

    [Fact]
    public void Hash_IsLowercaseHexSha256OfTheWholeToken()
    {
        const string token = "mpx_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        Assert.Equal(expected, ApiTokenSecret.Hash(token));
        Assert.NotEqual(ApiTokenSecret.Hash(token), ApiTokenSecret.Hash(token[..^1] + "B"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("mpx_")]
    [InlineData("mpx_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // one short
    [InlineData("mpx_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // one long
    [InlineData("mpy_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // wrong marker
    [InlineData("MPX_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // the marker is case-sensitive
    [InlineData("mpx_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")] // base64, not base64url
    [InlineData("mpx_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")] // padding
    [InlineData("mpx_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA ")]
    [InlineData("mpx_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAé")]
    public void IsWellFormed_RejectsAnythingElse(string? value)
    {
        Assert.False(ApiTokenSecret.IsWellFormed(value));
    }

    [Fact]
    public void HashesMatch_ComparesWholeValues()
    {
        var hash = ApiTokenSecret.Hash("mpx_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        Assert.True(ApiTokenSecret.HashesMatch(hash, hash));
        Assert.False(ApiTokenSecret.HashesMatch(hash, hash[..^1] + (hash[^1] == '0' ? '1' : '0')));
        Assert.False(ApiTokenSecret.HashesMatch(hash, hash[..^1]));
    }
}
