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

    // 1.32.0 lane D: the admin's choice and the publishing pace.

    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static RefreshEvidence Evidence(MetadataOriginStatus? status = MetadataOriginStatus.Ongoing, int? startYear = null, int? volumes = null,
        double? chapter = null, double? perVolume = null, params RefreshObservation[] history) =>
        new((int?)status, startYear, volumes, chapter, perVolume, history);

    private static RefreshObservation Seen(int daysAgo, double? chapter, int? volumes) => new(Now - TimeSpan.FromDays(daysAgo), chapter, volumes);

    [Fact]
    public void Finished_TakesTheFinishedChoice_PaceOrNot()
    {
        var policy = new RefreshCadencePolicy(7, 180, FollowPace: true);
        Assert.Equal(new RefreshCadenceResult(180, RefreshCadenceReason.Finished, null),
            RefreshCadence.For(policy, Evidence(MetadataOriginStatus.Complete, 2000, 100), Now));
        Assert.Equal(180, RefreshCadence.For(policy with { FollowPace = false }, Evidence(MetadataOriginStatus.Cancelled), Now).Days);
    }

    [Fact]
    public void PaceOff_IsTheOngoingChoice()
    {
        var result = RefreshCadence.For(new RefreshCadencePolicy(14, 90, FollowPace: false), Evidence(startYear: 2000, volumes: 200), Now);
        Assert.Equal(new RefreshCadenceResult(14, RefreshCadenceReason.Choice, null), result);
    }

    [Theory]
    [InlineData(2016, 70, 7)]   // ~56 days a volume -> weekly
    [InlineData(2016, 40, 14)]  // ~98 days -> every 2 weeks
    [InlineData(2016, 20, 30)]  // ~196 days -> the monthly choice
    public void LifetimeVolumeInterval_PicksTheBucket(int startYear, int volumes, int days)
    {
        var result = RefreshCadence.For(RefreshCadencePolicy.Default, Evidence(startYear: startYear, volumes: volumes), Now);
        Assert.Equal(days, result.Days);
        Assert.Equal(RefreshCadenceReason.Pace, result.Reason);
        Assert.NotNull(result.VolumeIntervalDays);
    }

    [Fact]
    public void ThePace_NeverGoesSlowerThanTheOngoingChoice()
    {
        var weekly = new RefreshCadencePolicy(7, 90, FollowPace: true);
        Assert.Equal(7, RefreshCadence.For(weekly, Evidence(startYear: 2000, volumes: 5), Now).Days);
    }

    [Fact]
    public void WithoutVolumes_TheChapterRateTimesAVolumesChapters()
    {
        // Started 2024 (~1,005 days), 200 chapters: a chapter every ~5 days, 10 chapters a volume -> ~50 days -> weekly.
        Assert.Equal(7, RefreshCadence.For(RefreshCadencePolicy.Default, Evidence(startYear: 2024, chapter: 200), Now).Days);
        // The volume map's average (25 chapters a volume) -> ~126 days -> monthly.
        Assert.Equal(30, RefreshCadence.For(RefreshCadencePolicy.Default, Evidence(startYear: 2024, chapter: 200, perVolume: 25), Now).Days);
    }

    [Fact]
    public void NothingKnown_OrStartedThisYear_IsTheChoice()
    {
        Assert.Equal(RefreshCadenceReason.Choice, RefreshCadence.For(RefreshCadencePolicy.Default, Evidence(), Now).Reason);
        Assert.Equal(RefreshCadenceReason.Choice, RefreshCadence.For(RefreshCadencePolicy.Default, Evidence(startYear: 2026, volumes: 3), Now).Reason);
        Assert.Equal(RefreshCadenceReason.Choice, RefreshCadence.For(RefreshCadencePolicy.Default, Evidence(MetadataOriginStatus.Unknown), Now).Reason);
    }

    [Fact]
    public void ObservedIncreases_WinOverTheLifetimeAverage()
    {
        // Lifetime says ~164 days a volume (monthly); observed: 3 volumes in the 150 days after the first increase -> 50 days -> weekly.
        var history = new[] { Seen(400, 100, 20), Seen(200, 110, 21), Seen(150, 115, 22), Seen(100, 120, 23), Seen(50, 125, 24) };
        var result = RefreshCadence.For(RefreshCadencePolicy.Default, Evidence(startYear: 2016, volumes: 24, history: history), Now);
        Assert.Equal(7, result.Days);
        Assert.Equal(50, result.VolumeIntervalDays!.Value, 3);
    }

    [Fact]
    public void ObservedIncreases_TooFewOrTooClose_FallBackToTheLifetimeAverage()
    {
        var close = new[] { Seen(300, 10, 1), Seen(100, 20, 2), Seen(30, 30, 3) }; // two increases only 70 days apart
        Assert.Null(RefreshCadence.VolumeIntervalDays(Evidence(history: close), Now));
        Assert.Equal(30, RefreshCadence.For(RefreshCadencePolicy.Default, Evidence(startYear: 2016, volumes: 20, history: close), Now).Days);
    }

    [Fact]
    public void Hiatus_OrQuietFor180Days_IsPaused()
    {
        var policy = RefreshCadencePolicy.Default;
        Assert.Equal(new RefreshCadenceResult(90, RefreshCadenceReason.Paused, null),
            RefreshCadence.For(policy, Evidence(MetadataOriginStatus.Hiatus, 2000, 100), Now));
        // Watched for 300 days, the last increase 200 days ago.
        var quiet = new[] { Seen(300, 50, 5), Seen(200, 60, 6), Seen(20, 60, 6) };
        Assert.Equal(RefreshCadenceReason.Paused, RefreshCadence.For(policy, Evidence(startYear: 2016, volumes: 6, history: quiet), Now).Reason);
        // A new chapter 20 days ago: not quiet.
        var moving = new[] { Seen(300, 50, 5), Seen(200, 60, 6), Seen(20, 61, 6) };
        Assert.False(RefreshCadence.IsQuiet(moving, Now));
        // Watched for less than 180 days: never quiet yet.
        Assert.False(RefreshCadence.IsQuiet([Seen(100, 50, 5)], Now));
    }

    [Fact]
    public void Policy_FromStored_FallsBackToTheDefaults()
    {
        Assert.Equal(RefreshCadencePolicy.Default, RefreshCadencePolicy.FromStored(null, null, true));
        Assert.Equal(new RefreshCadencePolicy(30, 90, false), RefreshCadencePolicy.FromStored(9, 1000, false));
        Assert.Equal(new RefreshCadencePolicy(7, 180, true), RefreshCadencePolicy.FromStored(7, 180, true));
        Assert.Equal(TimeSpan.FromDays(14), RefreshCadence.AgeFor(14, (int)MetadataOriginStatus.Complete));
        Assert.Equal(TimeSpan.FromDays(90), RefreshCadence.AgeFor(null, (int)MetadataOriginStatus.Complete));
    }
}
