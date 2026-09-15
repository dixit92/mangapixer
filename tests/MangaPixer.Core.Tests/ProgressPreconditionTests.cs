namespace com.lifepixer.mangapixer.Tests.Core.Catalog;

using com.lifepixer.mangapixer.Core.Catalog;
using Xunit;

/// <summary>
/// Tests for progress preconditions and content version compatibility.
/// </summary>
public sealed class ProgressPreconditionTests
{
    [Fact]
    public void Validate_MatchingContentVersion_ReturnsNull()
    {
        var error = ProgressPreconditions.Validate(
            expectedContentVersion: 42L,
            actualContentVersion: 42L,
            requestedPageIndex: 5,
            actualPageCount: 20);
        Assert.Null(error);
    }

    [Fact]
    public void Validate_MismatchedContentVersion_ReturnsError()
    {
        var error = ProgressPreconditions.Validate(
            expectedContentVersion: 41L,
            actualContentVersion: 42L,
            requestedPageIndex: 5,
            actualPageCount: 20);
        Assert.NotNull(error);
        Assert.Contains("Content version", error);
    }

    [Fact]
    public void Validate_NegativePageIndex_ReturnsError()
    {
        var error = ProgressPreconditions.Validate(
            expectedContentVersion: 42L,
            actualContentVersion: 42L,
            requestedPageIndex: -1,
            actualPageCount: 20);
        Assert.NotNull(error);
        Assert.Contains("negative", error);
    }

    [Fact]
    public void Validate_PageIndexBeyondCount_ReturnsError()
    {
        var error = ProgressPreconditions.Validate(
            expectedContentVersion: 42L,
            actualContentVersion: 42L,
            requestedPageIndex: 20,
            actualPageCount: 20);
        Assert.NotNull(error);
        Assert.Contains("out of range", error);
    }

    [Fact]
    public void Validate_LastValidPageIndex_ReturnsNull()
    {
        var error = ProgressPreconditions.Validate(
            expectedContentVersion: 42L,
            actualContentVersion: 42L,
            requestedPageIndex: 19,
            actualPageCount: 20);
        Assert.Null(error);
    }

    [Fact]
    public void Validate_ZeroPageIndex_ReturnsNull()
    {
        var error = ProgressPreconditions.Validate(
            expectedContentVersion: 42L,
            actualContentVersion: 42L,
            requestedPageIndex: 0,
            actualPageCount: 20);
        Assert.Null(error);
    }

    [Fact]
    public void ContentVersion_IsCompatibleWith_MatchingVersion_ReturnsTrue()
    {
        var version = new ContentVersion { Version = 42L, IsHashBased = true, IsConfirmed = true };
        Assert.True(version.IsCompatibleWith(42L));
    }

    [Fact]
    public void ContentVersion_IsCompatibleWith_MismatchedVersion_ReturnsFalse()
    {
        var version = new ContentVersion { Version = 42L, IsHashBased = true, IsConfirmed = true };
        Assert.False(version.IsCompatibleWith(41L));
    }
}
