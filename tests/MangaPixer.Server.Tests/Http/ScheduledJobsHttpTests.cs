namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Jobs;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests for the admin Scheduled jobs section (1.32.0): <c>/api/v1/admin/jobs</c> is admin-only, lists every job with the
/// server's clock, saves the hours of the daily jobs (validated, audited; trash through its own settings), the refresh cadence
/// (validated, audited) and a library's scan time of day through the scan schedule endpoint.
/// </summary>
public sealed class ScheduledJobsHttpTests : IDisposable
{
    private readonly MangaPixerWebApplicationFactory _factory = new();
    private readonly string _libRoot;

    public ScheduledJobsHttpTests()
    {
        _libRoot = Path.Combine(Path.GetTempPath(), "mangapixer-jobslib-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_libRoot, "Series A"));
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_libRoot, true); } catch { }
    }

    private static async Task<T> OkAsync<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Web))!;
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error;
    }

    private async Task<HttpClient> SignInAsync(string username, string password)
    {
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = username, Password = password })).EnsureSuccessStatusCode();
        var csrf = await (await client.GetAsync("/api/v1/auth/csrf")).Content.ReadFromJsonAsync<CsrfTokenDto>();
        client.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);
        return client;
    }

    private async Task<List<(string Action, string Result)>> AuditAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        return (await db.AuditEvents.AsNoTracking().OrderBy(a => a.Id).Select(a => new { a.Action, a.Result }).ToListAsync())
            .Select(a => (a.Action, a.Result)).ToList();
    }

    [Fact]
    public async Task TheJobs_AreAdminOnly()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        (await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest { Username = "jobsreader", Password = "TargetPass123!", IsAdmin = false }))
            .EnsureSuccessStatusCode();
        var first = await SignInAsync("jobsreader", "TargetPass123!");
        (await first.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest { CurrentPassword = "TargetPass123!", NewPassword = "TargetPassNew123!" }))
            .EnsureSuccessStatusCode();
        var reader = await SignInAsync("jobsreader", "TargetPassNew123!");

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/admin/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync("/api/v1/admin/jobs/cache-eviction", new UpdateJobScheduleRequest { Hour = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync("/api/v1/admin/jobs/metadata-refresh/cadence", new UpdateRefreshCadenceRequest { OngoingDays = 7 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/admin/jobs/metadata-refresh/series/x")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/v1/admin/jobs")).StatusCode);
    }

    [Fact]
    public async Task Overview_ListsEveryJob_WithTheServerClock_AndTheDefaults()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var library = await OkAsync<LibraryDto>(await admin.PostAsJsonAsync("/api/v1/admin/libraries",
            new RegisterLibraryRequest { DisplayName = "Jobs Library", RootPath = _libRoot }));

        var jobs = await OkAsync<ScheduledJobsDto>(await admin.GetAsync("/api/v1/admin/jobs"));

        Assert.False(string.IsNullOrEmpty(jobs.TimeZone));
        Assert.Equal(ScheduledJobKeys.Ordered, jobs.Jobs.Select(j => j.Key).Distinct().ToArray());
        var scan = Assert.Single(jobs.Jobs, j => j.Key == ScheduledJobKeys.LibraryScan);
        Assert.Equal((library.Id, "Jobs Library", "1d", "interval", (int?)null), (scan.LibraryId, scan.LibraryName, scan.ScanSchedule, scan.Kind, scan.Hour));
        var byKey = jobs.Jobs.Where(j => j.LibraryId is null).ToDictionary(j => j.Key);
        Assert.Equal((3, true, "daily"), (byKey[ScheduledJobKeys.MetadataRefresh].Hour, byKey[ScheduledJobKeys.MetadataRefresh].Configurable, byKey[ScheduledJobKeys.MetadataRefresh].Kind));
        Assert.False(byKey[ScheduledJobKeys.MetadataRefresh].Enabled); // automatic matching is off
        Assert.Equal(5, byKey[ScheduledJobKeys.CacheEviction].Hour);
        Assert.Equal((4, false), (byKey[ScheduledJobKeys.Trash].Hour, byKey[ScheduledJobKeys.Trash].Enabled));
        Assert.Equal(("continuous", false), (byKey[ScheduledJobKeys.AutoMatch].Kind, byKey[ScheduledJobKeys.AutoMatch].Configurable));
        Assert.Equal("onDemand", byKey[ScheduledJobKeys.UpdateCheck].Kind);
        Assert.Equal((30, 90, true, 200), (jobs.Refresh.OngoingDays, jobs.Refresh.FinishedDays, jobs.Refresh.FollowPace, jobs.Refresh.MaxPerDay));
        Assert.Equal([7, 14, 30], jobs.Refresh.AllowedOngoingDays);
    }

    [Fact]
    public async Task Hours_AreValidated_Saved_AndAudited()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();

        Assert.Equal("invalid_hour", await ErrorAsync(await admin.PutAsJsonAsync("/api/v1/admin/jobs/cache-eviction", new UpdateJobScheduleRequest { Hour = 24 }), HttpStatusCode.BadRequest));
        Assert.Equal("not_configurable", await ErrorAsync(await admin.PutAsJsonAsync("/api/v1/admin/jobs/auto-match", new UpdateJobScheduleRequest { Hour = 2 }), HttpStatusCode.BadRequest));
        Assert.Equal("invalid_job", await ErrorAsync(await admin.PutAsJsonAsync("/api/v1/admin/jobs/nope", new UpdateJobScheduleRequest { Hour = 2 }), HttpStatusCode.NotFound));

        var jobs = await OkAsync<ScheduledJobsDto>(await admin.PutAsJsonAsync("/api/v1/admin/jobs/cache-eviction", new UpdateJobScheduleRequest { Hour = 2 }));
        Assert.Equal(2, jobs.Jobs.Single(j => j.Key == ScheduledJobKeys.CacheEviction).Hour);
        jobs = await OkAsync<ScheduledJobsDto>(await admin.PutAsJsonAsync("/api/v1/admin/jobs/metadata-refresh", new UpdateJobScheduleRequest { Hour = 1 }));
        Assert.Equal(1, jobs.Jobs.Single(j => j.Key == ScheduledJobKeys.MetadataRefresh).Hour);
        jobs = await OkAsync<ScheduledJobsDto>(await admin.PutAsJsonAsync("/api/v1/admin/jobs/metadata-refresh", new UpdateJobScheduleRequest { Hour = null }));
        Assert.Equal(3, jobs.Jobs.Single(j => j.Key == ScheduledJobKeys.MetadataRefresh).Hour); // back to the default
        // Trash: the same setting as its card.
        await OkAsync<ScheduledJobsDto>(await admin.PutAsJsonAsync("/api/v1/admin/jobs/trash", new UpdateJobScheduleRequest { Hour = 23 }));
        Assert.Equal(23, (await OkAsync<TrashOverviewDto>(await admin.GetAsync("/api/v1/admin/trash"))).Settings.AutomaticHour);
        // Backups: an hour for a whole-days interval.
        jobs = await OkAsync<ScheduledJobsDto>(await admin.PutAsJsonAsync("/api/v1/admin/jobs/backup", new UpdateJobScheduleRequest { Hour = 2 }));
        var backup = jobs.Jobs.Single(j => j.Key == ScheduledJobKeys.Backup);
        Assert.Equal((2, "daily", 24d), (backup.Hour, backup.Kind, backup.IntervalHours));

        var audit = await AuditAsync();
        Assert.Contains(("job.schedule.change", "cache-eviction_h2"), audit);
        Assert.Contains(("job.schedule.change", "metadata-refresh_h1"), audit);
        Assert.Contains(("job.schedule.change", "metadata-refresh_hdefault"), audit);
        Assert.Contains(("job.schedule.change", "backup_h2"), audit);
        Assert.Contains(("trash.auto.hour", "hour_23"), audit);
    }

    [Fact]
    public async Task RefreshCadence_IsValidated_Saved_AndAudited()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();

        Assert.Equal("invalid_cadence", await ErrorAsync(
            await admin.PutAsJsonAsync("/api/v1/admin/jobs/metadata-refresh/cadence", new UpdateRefreshCadenceRequest { OngoingDays = 21 }), HttpStatusCode.BadRequest));
        Assert.Equal("invalid_cadence", await ErrorAsync(
            await admin.PutAsJsonAsync("/api/v1/admin/jobs/metadata-refresh/cadence", new UpdateRefreshCadenceRequest { FinishedDays = 7 }), HttpStatusCode.BadRequest));

        var jobs = await OkAsync<ScheduledJobsDto>(await admin.PutAsJsonAsync("/api/v1/admin/jobs/metadata-refresh/cadence",
            new UpdateRefreshCadenceRequest { OngoingDays = 7, FollowPace = false }));
        Assert.Equal((7, 90, false), (jobs.Refresh.OngoingDays, jobs.Refresh.FinishedDays, jobs.Refresh.FollowPace));
        Assert.Contains(("metadata.refresh.cadence", "o7_f90_fixed"), await AuditAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.GetAsync("/api/v1/admin/jobs/metadata-refresh/series/unknown")).StatusCode);
    }

    [Fact]
    public async Task ScanTimeOfDay_GoesThroughTheScanSchedule_AndShowsInTheJobs()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var library = await OkAsync<LibraryDto>(await admin.PostAsJsonAsync("/api/v1/admin/libraries",
            new RegisterLibraryRequest { DisplayName = "Timed Library", RootPath = _libRoot }));
        var url = $"/api/v1/admin/libraries/{library.Id}/scan-schedule";

        Assert.Equal("hour_not_allowed", await ErrorAsync(await admin.PutAsJsonAsync(url, new SetLibraryScanScheduleRequest { ScanSchedule = "1h", ScanHour = 3 }), HttpStatusCode.BadRequest));
        Assert.Equal("weekday_not_allowed", await ErrorAsync(await admin.PutAsJsonAsync(url,
            new SetLibraryScanScheduleRequest { ScanSchedule = "1d", ScanHour = 3, ScanWeekday = 1 }), HttpStatusCode.BadRequest));
        Assert.Equal("invalid_hour", await ErrorAsync(await admin.PutAsJsonAsync(url, new SetLibraryScanScheduleRequest { ScanSchedule = "1d", ScanHour = 24 }), HttpStatusCode.BadRequest));

        var daily = await OkAsync<LibraryDto>(await admin.PutAsJsonAsync(url, new SetLibraryScanScheduleRequest { ScanSchedule = "1d", ScanHour = 3 }));
        Assert.Equal(("1d", 3, (int?)null), (daily.ScanSchedule, daily.ScanHour, daily.ScanWeekday));
        var weekly = await OkAsync<LibraryDto>(await admin.PutAsJsonAsync(url, new SetLibraryScanScheduleRequest { ScanSchedule = "7d", ScanHour = 4, ScanWeekday = 1 }));
        Assert.Equal(("7d", 4, (int?)1), (weekly.ScanSchedule, weekly.ScanHour, weekly.ScanWeekday));

        var row = (await OkAsync<ScheduledJobsDto>(await admin.GetAsync("/api/v1/admin/jobs"))).Jobs.Single(j => j.LibraryId == library.Id);
        Assert.Equal(("weekly", 4, (int?)1), (row.Kind, row.Hour, row.Weekday));
        Assert.Contains(("library.scan_schedule.change", "7d_h4_w1"), await AuditAsync());

        // The old preset-only request clears the time (the request carries the whole schedule).
        var plain = await OkAsync<LibraryDto>(await admin.PutAsJsonAsync(url, new SetLibraryScanScheduleRequest { ScanSchedule = "6h" }));
        Assert.Equal(("6h", (int?)null), (plain.ScanSchedule, plain.ScanHour));
    }
}
