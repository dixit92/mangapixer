namespace com.lifepixer.mangapixer.Tests.Server.Features.Jobs;

using com.lifepixer.mangapixer.Server.Features.Jobs;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Operations;
using Xunit;

/// <summary>
/// Unit tests for the 1.32.0 job schedules on the shared rule: the daily refresh (and its hourly retry after the gate stopped it),
/// the cache clean-up's hour, backups at an hour every N days, and the budget day at server-local midnight with its one-time
/// carry-over of a pre-1.32.0 UTC key. Zones are built in the test.
/// </summary>
public sealed class JobSchedulesTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo Minus4 = TimeZoneInfo.CreateCustomTimeZone("test-4", TimeSpan.FromHours(-4), "test-4", "test-4");

    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Refresh_RunsDailyAtItsHour_AndRetriesHourlyAfterTheGateStoppedIt()
    {
        Assert.Equal(3, MetadataRefreshSchedule.HourOf(null));
        Assert.Equal(22, MetadataRefreshSchedule.HourOf(22));
        // Never ran: the next 03:00, never at once (no burst after the upgrade).
        Assert.Equal(At(3, 3), MetadataRefreshSchedule.NextDue(At(2, 12), Utc, 3, null, null));
        // Ran at 03:00 today: tomorrow.
        Assert.Equal(At(3, 3), MetadataRefreshSchedule.NextDue(At(2, 12), Utc, 3, At(2, 3), JobOutcomes.Ok));
        // Waiting (budget spent) at 03:00: again at 04:00, and at once when that passed - until the day ends.
        Assert.Equal(At(2, 4), MetadataRefreshSchedule.NextDue(At(2, 3, 30), Utc, 3, At(2, 3), JobOutcomes.Waiting));
        Assert.Equal(At(2, 12), MetadataRefreshSchedule.NextDue(At(2, 12), Utc, 3, At(2, 3), JobOutcomes.Waiting));
        // A wait from yesterday: today's slot, not an hourly retry.
        Assert.Equal(At(3, 3), MetadataRefreshSchedule.NextDue(At(3, 1), Utc, 3, At(2, 23, 30), JobOutcomes.Waiting));
        // Down at 03:00: caught up once.
        Assert.Equal(At(3, 9), MetadataRefreshSchedule.NextDue(At(3, 9), Utc, 3, At(2, 3), JobOutcomes.Ok));
    }

    [Fact]
    public void ANeverRunJob_WithItsStartAsTheFloor_RunsAtTheFirstSlot_EvenWhenCheckedAMinuteLate()
    {
        // Review-instance finding: with no floor the next slot is always after now, so a check at 03:00:30 skipped to tomorrow.
        // The hosted services pass their start time as the floor until the first run.
        Assert.Equal(At(2, 3, 1), MetadataRefreshSchedule.NextDue(At(2, 3, 1), Utc, 3, At(2, 1), null));
        Assert.Equal(At(2, 5, 1), MaintenanceHostedService.NextCacheEviction(At(2, 5, 1), Utc, null, At(2, 1)));
        // Started after today's slot: tomorrow.
        Assert.Equal(At(3, 3), MetadataRefreshSchedule.NextDue(At(2, 12), Utc, 3, At(2, 4), null));
    }

    [Fact]
    public void Refresh_DescribesItsRun_CountsOnly()
    {
        Assert.Equal((JobOutcomes.Ok, "12 refreshed"), MetadataRefreshSchedule.Describe(new RefreshPassResult(12, 12, null)));
        Assert.Equal((JobOutcomes.Ok, "200 refreshed, 5 left for the next day"), MetadataRefreshSchedule.Describe(new RefreshPassResult(200, 205, "refresh_cap")));
        Assert.Equal((JobOutcomes.Waiting, "3 refreshed, waiting: budget_exhausted"),
            MetadataRefreshSchedule.Describe(new RefreshPassResult(3, 40, "budget_exhausted")));
    }

    [Fact]
    public void CacheCleanUp_IsAt05ByDefault_ServerTime()
    {
        Assert.Equal(At(3, 5), MaintenanceHostedService.NextCacheEviction(At(2, 12), Utc, null, At(2, 5)));
        Assert.Equal(At(2, 22), MaintenanceHostedService.NextCacheEviction(At(2, 12), Utc, 22, At(2, 5)));
        // 05:00 at UTC-4 is 09:00 UTC.
        Assert.Equal(At(3, 9), MaintenanceHostedService.NextCacheEviction(At(2, 12), Minus4, null, At(2, 9)));
    }

    [Fact]
    public void Backups_WithAnHourAndWholeDays_RunAtThatHour_EveryNDays()
    {
        static EffectiveBackupSettings Settings(double hours, int? hour) => BackupSettingsResolver.Compute(
            new RotatingBackupOptions { SafetyBackupDirectory = "/synthetic/backups" },
            new com.lifepixer.mangapixer.Server.Persistence.Entities.AppSettingsEntity { BackupIntervalHours = hours, BackupHour = hour });
        var started = At(1, 0);
        // No backup yet: the first slot after the start, also when checked a minute late.
        Assert.Equal(At(1, 2, 1), RotatingBackupHostedService.NextDue(started, At(1, 2, 1), Utc, null, Settings(24, 2)));
        // Daily at 02:00 after yesterday's 02:00 backup: today 02:00 passed at 12:00 -> tomorrow; caught up when missed.
        Assert.Equal(At(3, 2), RotatingBackupHostedService.NextDue(started, At(2, 12), Utc, At(2, 2), Settings(24, 2)));
        Assert.Equal(At(2, 12), RotatingBackupHostedService.NextDue(started, At(2, 12), Utc, At(1, 2), Settings(24, 2)));
        // Every 2 days.
        Assert.Equal(At(4, 2), RotatingBackupHostedService.NextDue(started, At(2, 12), Utc, At(2, 2), Settings(48, 2)));
        // 6 hours: the hour does not apply.
        Assert.Equal(At(2, 8), RotatingBackupHostedService.NextDue(started, At(2, 7), Utc, At(2, 2), Settings(6, 2)));
        Assert.Null(Settings(6, 2).WholeDays);
        Assert.Equal((2, "settings"), (Settings(24, 2).Hour, Settings(24, 2).HourSource));
    }

    [Fact]
    public void BudgetDay_StartsAtMidnightServerTime_AndKeepsAPre132UtcKeyOnce()
    {
        Assert.True(MetadataBudget.LocalDay);
        // 2026-10-02 02:00 UTC is 2026-10-01 22:00 at UTC-4: the day began at 2026-10-01 04:00 UTC.
        Assert.Equal(At(1, 4), MetadataBudget.DayStart(At(2, 2), Minus4));
        Assert.Equal(At(2, 4), MetadataBudget.DayStart(At(2, 5), Minus4));
        Assert.True(MetadataBudget.IsToday(At(1, 4), At(2, 2), Minus4));
        Assert.False(MetadataBudget.IsToday(At(1, 4), At(2, 5), Minus4));
        // The old key (00:00 UTC of the current UTC day) still counts as today, so the switch spends nothing extra.
        Assert.True(MetadataBudget.IsToday(At(2, 0), At(2, 2), Minus4));
        Assert.False(MetadataBudget.IsToday(At(1, 0), At(2, 2), Minus4));
        Assert.False(MetadataBudget.IsToday(null, At(2, 2), Minus4));
    }
}
