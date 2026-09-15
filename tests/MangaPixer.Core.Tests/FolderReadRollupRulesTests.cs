namespace com.lifepixer.mangapixer.Tests.Core.Reading;

using com.lifepixer.mangapixer.Core.Reading;
using Xunit;

/// <summary>
/// Unit tests for the pure tri-state rule behind the derived folder read rollup
/// (1.6.0). The browse aggregate only produces counts; this is where Read /
/// Reading / Unread / null is decided.
/// </summary>
public sealed class FolderReadRollupRulesTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(-1, 0, 0)]
    public void Classify_NoReadableDescendants_ReturnsNull(int total, int read, int inProgress)
    {
        // An empty folder has nothing to roll up: no badge, and no vacuous "all read".
        Assert.Null(FolderReadRollupRules.Classify(total, read, inProgress));
    }

    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(5, 5, 0)]
    [InlineData(5, 5, 3)] // in-progress rows on already-read items don't matter
    public void Classify_AllRead_ReturnsRead(int total, int read, int inProgress)
    {
        Assert.Equal(FolderReadRollup.Read, FolderReadRollupRules.Classify(total, read, inProgress));
    }

    [Theory]
    [InlineData(5, 2, 0)] // some read, none in progress -> still partial
    [InlineData(5, 0, 1)] // none read, one in progress
    [InlineData(5, 2, 2)] // mixed
    [InlineData(2, 1, 1)]
    public void Classify_PartiallyRead_ReturnsReading(int total, int read, int inProgress)
    {
        Assert.Equal(FolderReadRollup.Reading, FolderReadRollupRules.Classify(total, read, inProgress));
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(9, 0, 0)]
    public void Classify_NothingReadOrInProgress_ReturnsUnread(int total, int read, int inProgress)
    {
        Assert.Equal(FolderReadRollup.Unread, FolderReadRollupRules.Classify(total, read, inProgress));
    }
}
