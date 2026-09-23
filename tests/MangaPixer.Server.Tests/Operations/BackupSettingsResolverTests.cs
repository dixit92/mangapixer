namespace com.lifepixer.mangapixer.Tests.Server.Operations;

using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.Extensions.Configuration;
using Xunit;

/// <summary>
/// Unit tests for the per-field backup settings precedence (configuration,
/// then AppSettings, then default), configuration parsing, the strict
/// generated-name patterns, and the scheduler's next-due math.
/// </summary>
public sealed class BackupSettingsResolverTests
{
    private static RotatingBackupOptions Parse(params (string Key, string Value)[] pairs)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();
        return RotatingBackupOptions.FromConfiguration(config, "/data/backups");
    }

    [Fact]
    public void Defaults_WhenNothingIsSet()
    {
        var s = BackupSettingsResolver.Compute(Parse(), null);
        Assert.True(s.Enabled);
        Assert.Equal(24, s.IntervalHours);
        Assert.Equal(7, s.RetentionCount);
        Assert.Equal(EffectiveBackupSettings.KindDefault, s.LocationKind);
        Assert.Equal("/data/backups", s.RotatingDirectory);
        Assert.Equal("/data/backups", s.SafetyDirectory);
        Assert.All(new[] { s.EnabledSource, s.IntervalHoursSource, s.RetentionCountSource, s.LocationSource },
            src => Assert.Equal(BackupSettingSources.Default, src));
        Assert.True(s.LocationChangeAllowed);
    }

    [Fact]
    public void Settings_OverrideDefaults()
    {
        var row = new AppSettingsEntity
        {
            BackupsEnabled = false,
            BackupIntervalHours = 12,
            BackupRetentionCount = 3,
            BackupLocation = "/mnt/archive/mp",
            BackupLocationMarkerId = "abc",
        };
        var s = BackupSettingsResolver.Compute(Parse(), row);
        Assert.False(s.Enabled);
        Assert.Equal(12, s.IntervalHours);
        Assert.Equal(3, s.RetentionCount);
        Assert.Equal(EffectiveBackupSettings.KindCustom, s.LocationKind);
        Assert.Equal("/mnt/archive/mp", s.RotatingDirectory);
        Assert.Equal("/data/backups", s.SafetyDirectory);
        Assert.Equal("abc", s.MarkerId);
        Assert.All(new[] { s.EnabledSource, s.IntervalHoursSource, s.RetentionCountSource, s.LocationSource },
            src => Assert.Equal(BackupSettingSources.Settings, src));
    }

    [Fact]
    public void Configuration_WinsPerField_NotAllOrNothing()
    {
        var options = Parse(("MangaPixer:Backups:Location", "/backups"), ("MangaPixer:Backups:IntervalHours", "0.5"));
        var row = new AppSettingsEntity { BackupIntervalHours = 12, BackupRetentionCount = 3, BackupLocation = "/mnt/other" };
        var s = BackupSettingsResolver.Compute(options, row);

        Assert.Equal(0.5, s.IntervalHours);
        Assert.Equal(BackupSettingSources.Configuration, s.IntervalHoursSource);
        Assert.Equal("/backups", s.CustomLocation);
        Assert.Equal(BackupSettingSources.Configuration, s.LocationSource);
        Assert.False(s.LocationChangeAllowed);
        // Retention is not configured: the UI value still applies.
        Assert.Equal(3, s.RetentionCount);
        Assert.Equal(BackupSettingSources.Settings, s.RetentionCountSource);
    }

    [Theory]
    [InlineData("MangaPixer:Backups:Enabled", "maybe")]
    [InlineData("MangaPixer:Backups:IntervalHours", "-1")]
    [InlineData("MangaPixer:Backups:IntervalHours", "daily")]
    [InlineData("MangaPixer:Backups:RetentionCount", "0")]
    [InlineData("MangaPixer:Backups:AllowLocationChange", "nope")]
    public void InvalidConfiguration_IsIgnored_AndReportedByKey(string key, string value)
    {
        var options = Parse((key, value));
        Assert.Contains(key, options.InvalidKeys);
        var s = BackupSettingsResolver.Compute(options, null);
        Assert.Equal(BackupSettingSources.Default, key switch
        {
            "MangaPixer:Backups:Enabled" => s.EnabledSource,
            "MangaPixer:Backups:IntervalHours" => s.IntervalHoursSource,
            "MangaPixer:Backups:RetentionCount" => s.RetentionCountSource,
            _ => BackupSettingSources.Default,
        });
    }

    [Fact]
    public void AllowLocationChangeFalse_LocksTheLocation_WithoutPinningOne()
    {
        var s = BackupSettingsResolver.Compute(Parse(("MangaPixer:Backups:AllowLocationChange", "false")), null);
        Assert.False(s.LocationChangeAllowed);
        Assert.Equal(EffectiveBackupSettings.KindDefault, s.LocationKind);
    }

    [Fact]
    public void Apply_SignalsTheChangeToken()
    {
        var resolver = new BackupSettingsResolver(Parse());
        var token = resolver.ChangeToken;
        Assert.False(token.IsCancellationRequested);

        resolver.Apply(new AppSettingsEntity { BackupRetentionCount = 2 });

        Assert.True(token.IsCancellationRequested);
        Assert.False(resolver.ChangeToken.IsCancellationRequested);
        Assert.Equal(2, resolver.Current.RetentionCount);
    }

    [Theory]
    [InlineData("rotating-20260901-000000.db", true)]
    [InlineData("rotating-20260901-000000-0a9f.db", true)]
    [InlineData("rotating-20260901000000.db", false)]
    [InlineData("rotating-old-manual.db", false)]
    [InlineData("rotating-20260901-000000.db.bak", false)]
    [InlineData("rotating-20260901-000000-XYZW.db", false)]
    [InlineData("pre-migration-20260901-000000.db", false)]
    public void RotatingNamePattern_IsStrict(string name, bool matches)
    {
        Assert.Equal(matches, RotatingBackupService.FileNamePattern().IsMatch(name));
    }

    [Fact]
    public void ParseTimestamp_ReadsTheGeneratedName()
    {
        var ts = RotatingBackupService.ParseTimestamp("rotating-20260923-013000-ab12.db");
        Assert.Equal(new DateTimeOffset(2026, 9, 23, 1, 30, 0, TimeSpan.Zero), ts);
        Assert.Null(RotatingBackupService.ParseTimestamp("rotating-garbage.db"));
    }

    [Fact]
    public void NextDue_FirstRunWaitsForTheInitialDelay()
    {
        var start = new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(start + RotatingBackupHostedService.InitialDelay,
            RotatingBackupHostedService.NextDue(start, null, TimeSpan.FromHours(24)));
    }

    [Fact]
    public void NextDue_RecentSnapshot_DefersTheRunAfterARestart()
    {
        var start = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var last = start - TimeSpan.FromHours(3);
        Assert.Equal(last + TimeSpan.FromHours(24),
            RotatingBackupHostedService.NextDue(start, last, TimeSpan.FromHours(24)));
    }

    [Fact]
    public void NextDue_OverdueSnapshot_RunsAfterTheInitialDelay()
    {
        var start = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var last = start - TimeSpan.FromDays(3);
        Assert.Equal(start + RotatingBackupHostedService.InitialDelay,
            RotatingBackupHostedService.NextDue(start, last, TimeSpan.FromHours(24)));
    }
}
