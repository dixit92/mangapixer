namespace com.lifepixer.mangapixer.Tests.Core.Metadata;

using com.lifepixer.mangapixer.Core.Metadata;
using Xunit;

/// <summary>Unit tests for <see cref="RefreshCadence"/> (1.32.0 step 0: one cadence for refresh, companions and cover re-checks).</summary>
public sealed class RefreshCadenceTests
{
    [Theory]
    [InlineData(MetadataOriginStatus.Complete, 90)]
    [InlineData(MetadataOriginStatus.Cancelled, 90)]
    [InlineData(MetadataOriginStatus.Ongoing, 30)]
    [InlineData(MetadataOriginStatus.Hiatus, 30)]
    [InlineData(MetadataOriginStatus.Unknown, 30)]
    public void AgeFor_FinishedIs90Days_EverythingElse30(MetadataOriginStatus status, int days)
    {
        Assert.Equal(TimeSpan.FromDays(days), RefreshCadence.AgeFor((int)status));
        Assert.Equal(days == 90, RefreshCadence.IsFinished((int)status));
        Assert.Equal(days == 90, RefreshCadence.FinishedStatuses.Contains((int)status));
    }

    [Fact]
    public void AgeFor_UnknownStatus_IsOngoing() => Assert.Equal(RefreshCadence.OngoingAge, RefreshCadence.AgeFor(null));
}
