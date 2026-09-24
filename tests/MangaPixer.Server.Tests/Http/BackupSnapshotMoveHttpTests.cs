namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP (WebApplicationFactory) tests for moving the existing rotating
/// snapshots on a backup location change (1.23.0): the snapshot summary in
/// the settings GET, the opt-in <c>moveExistingSnapshots</c> flag on the PUT,
/// progress / result at <c>GET backups/move</c> (default to custom and back),
/// safety snapshots never moved, the audit row, no folder in any body, the
/// 409 while a move runs, and admin-only access.
///
/// "HttpSerial" because every host boot reassigns the process-global Serilog logger.
/// </summary>
[Collection("HttpSerial")]
public sealed class BackupSnapshotMoveHttpTests : IDisposable
{
    private const string AdminPassword = "TestPassword123!";
    private const string SettingsUrl = "/api/v1/operations/backups/settings";
    private const string MoveUrl = "/api/v1/operations/backups/move";

    private readonly MangaPixerWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private string DefaultDir => Path.Combine(_factory.DataRoot, "backups");

    private static object Location(string mode, string? customLocation, bool move) => new
    {
        location = new { mode, customLocation },
        currentPassword = AdminPassword,
        moveExistingSnapshots = move,
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static List<string> SeedSnapshots(string dir, int count)
    {
        Directory.CreateDirectory(dir);
        var names = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var name = $"rotating-20260101-0{i}0000.db";
            File.WriteAllBytes(Path.Combine(dir, name), new byte[1000 + i]);
            names.Add(name);
        }
        return names;
    }

    private static async Task<(JsonElement Status, string Body)> WaitForMoveAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var response = await client.GetAsync(MoveUrl);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            var status = JsonDocument.Parse(body).RootElement;
            if (status.GetProperty("state").GetString() != BackupSnapshotMoveStates.Running)
                return (status, body);
            Assert.True(DateTime.UtcNow < deadline, "The snapshot move did not finish in time.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task MoveToCustomAndBack_MovesRotatingSnapshotsOnly_WithProgressAndAudit()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var target = Path.Combine(_factory.TempRoot, "archive");
        var names = SeedSnapshots(DefaultDir, 3);
        var safety = Path.Combine(DefaultDir, "pre-migration-20260101-000000.db");
        File.WriteAllText(safety, "safety");

        var settings = await JsonAsync(await client.GetAsync(SettingsUrl));
        Assert.Equal(3, settings.GetProperty("rotatingSnapshotCount").GetInt32());
        Assert.Equal(3003, settings.GetProperty("rotatingSnapshotBytes").GetInt64());

        var idle = await JsonAsync(await client.GetAsync(MoveUrl));
        Assert.Equal(BackupSnapshotMoveStates.Idle, idle.GetProperty("state").GetString());

        // Default -> custom, with the move.
        var save = await client.PutAsJsonAsync(SettingsUrl, Location("custom", target, move: true));
        save.EnsureSuccessStatusCode();
        var saved = await JsonAsync(save);
        Assert.True(saved.GetProperty("locationChanged").GetBoolean());
        Assert.True(saved.GetProperty("snapshotMoveStarted").GetBoolean());

        var (status, body) = await WaitForMoveAsync(client);
        Assert.Equal(BackupSnapshotMoveStates.Completed, status.GetProperty("state").GetString());
        Assert.Equal("default", status.GetProperty("fromKind").GetString());
        Assert.Equal("custom", status.GetProperty("toKind").GetString());
        Assert.Equal(3, status.GetProperty("totalFiles").GetInt32());
        Assert.Equal(3, status.GetProperty("filesDone").GetInt32());
        Assert.Equal(3, status.GetProperty("movedCount").GetInt32());
        Assert.Equal(3003, status.GetProperty("bytesDone").GetInt64());
        Assert.Equal(0, status.GetProperty("issues").GetArrayLength());
        Assert.DoesNotContain(target, body);
        Assert.DoesNotContain(_factory.DataRoot, body);

        foreach (var name in names)
        {
            Assert.True(File.Exists(Path.Combine(target, name)));
            Assert.False(File.Exists(Path.Combine(DefaultDir, name)));
        }
        Assert.True(File.Exists(safety), "Safety snapshots never move.");

        // The restore list now shows them from the new location.
        var list = await (await client.GetAsync("/api/v1/operations/backups/files")).Content.ReadAsStringAsync();
        Assert.All(names, n => Assert.Contains(n, list));

        // Custom -> default, with the move.
        var back = await client.PutAsJsonAsync(SettingsUrl, Location("default", null, move: true));
        back.EnsureSuccessStatusCode();
        Assert.True((await JsonAsync(back)).GetProperty("snapshotMoveStarted").GetBoolean());
        var (again, _) = await WaitForMoveAsync(client);
        Assert.Equal("custom", again.GetProperty("fromKind").GetString());
        Assert.Equal("default", again.GetProperty("toKind").GetString());
        Assert.Equal(3, again.GetProperty("movedCount").GetInt32());
        Assert.All(names, n => Assert.True(File.Exists(Path.Combine(DefaultDir, n))));
        Assert.Empty(Directory.EnumerateFiles(target, "rotating-*"));
        Assert.True(File.Exists(Path.Combine(target, BackupLocationValidator.MarkerFileName)),
            "Only snapshots move; the marker stays.");

        Assert.Equal(2, await CountAuditAsync(AuditActions.BackupSnapshotsMoved, AuditResults.Success));
    }

    [Fact]
    public async Task WithoutTheFlag_SnapshotsStayInThePreviousLocation()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var target = Path.Combine(_factory.TempRoot, "archive");
        var names = SeedSnapshots(DefaultDir, 2);

        var save = await client.PutAsJsonAsync(SettingsUrl, Location("custom", target, move: false));

        save.EnsureSuccessStatusCode();
        var saved = await JsonAsync(save);
        Assert.True(saved.GetProperty("locationChanged").GetBoolean());
        Assert.False(saved.GetProperty("snapshotMoveStarted").GetBoolean());
        Assert.Equal(0, saved.GetProperty("settings").GetProperty("rotatingSnapshotCount").GetInt32());
        var status = await JsonAsync(await client.GetAsync(MoveUrl));
        Assert.Equal(BackupSnapshotMoveStates.Idle, status.GetProperty("state").GetString());
        Assert.All(names, n => Assert.True(File.Exists(Path.Combine(DefaultDir, n))));
        Assert.Empty(Directory.EnumerateFiles(target, "rotating-*"));
    }

    [Fact]
    public async Task NameConflict_IsReported_AndBothFilesKept()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var target = Path.Combine(_factory.TempRoot, "archive");
        var names = SeedSnapshots(DefaultDir, 2);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, names[0]), "different content");

        (await client.PutAsJsonAsync(SettingsUrl, Location("custom", target, move: true))).EnsureSuccessStatusCode();
        var (status, _) = await WaitForMoveAsync(client);

        Assert.Equal(1, status.GetProperty("movedCount").GetInt32());
        var issue = Assert.Single(status.GetProperty("issues").EnumerateArray());
        Assert.Equal(names[0], issue.GetProperty("fileName").GetString());
        Assert.Equal(BackupSnapshotMoveOutcomes.NameConflict, issue.GetProperty("code").GetString());
        Assert.Equal(BackupSnapshotMoveLocations.Previous, issue.GetProperty("location").GetString());
        Assert.True(File.Exists(Path.Combine(DefaultDir, names[0])));
        Assert.Equal("different content", File.ReadAllText(Path.Combine(target, names[0])));
        Assert.Equal(1, await CountAuditAsync(AuditActions.BackupSnapshotsMoved, AuditResults.Failure));
    }

    [Fact]
    public async Task LocationChange_WhileAMoveRuns_Returns409()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var target = Path.Combine(_factory.TempRoot, "archive");
        SeedSnapshots(DefaultDir, 1);
        var state = _factory.Services.GetRequiredService<RotatingBackupState>();

        // Hold the backup run gate so the move stays "running".
        await state.RunGate.WaitAsync();
        try
        {
            (await client.PutAsJsonAsync(SettingsUrl, Location("custom", target, move: true))).EnsureSuccessStatusCode();
            var running = await JsonAsync(await client.GetAsync(MoveUrl));
            Assert.Equal(BackupSnapshotMoveStates.Running, running.GetProperty("state").GetString());

            var again = await client.PutAsJsonAsync(SettingsUrl, Location("default", null, move: true));
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Equal("snapshot_move_in_progress", (await JsonAsync(again)).GetProperty("error").GetString());

            // Other settings can still change.
            (await client.PutAsJsonAsync(SettingsUrl, new { retentionCount = 5 })).EnsureSuccessStatusCode();
        }
        finally
        {
            state.RunGate.Release();
        }

        var (done, _) = await WaitForMoveAsync(client);
        Assert.Equal(1, done.GetProperty("movedCount").GetInt32());
    }

    [Fact]
    public async Task MoveStatus_RequiresSignIn()
    {
        var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync(MoveUrl);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<int> CountAuditAsync(string action, string result)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        return await db.AuditEvents.CountAsync(e => e.Action == action && e.Result == result);
    }
}
