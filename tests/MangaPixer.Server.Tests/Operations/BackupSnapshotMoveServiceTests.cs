namespace com.lifepixer.mangapixer.Tests.Server.Operations;

using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service tests for the background snapshot move job
/// (<see cref="BackupSnapshotMoveService"/>): both directions and custom to
/// custom, safety snapshots untouched, retention applied in the new location,
/// per-file failures reported with where the file stayed, serialization with
/// rotating backups through the shared run gate (incl. a real VACUUM INTO
/// backup against a SQLite DB), and single-job enforcement. Per-test temp
/// directories only.
/// </summary>
public sealed class BackupSnapshotMoveServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _safety;
    private readonly string _customA;
    private readonly string _customB;

    public BackupSnapshotMoveServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mangapixer-snapmovesvc-" + Guid.NewGuid().ToString("N")[..8]);
        _safety = Path.Combine(_root, "data", "backups");
        _customA = Path.Combine(_root, "archive-a");
        _customB = Path.Combine(_root, "archive-b");
        Directory.CreateDirectory(_safety);
        Directory.CreateDirectory(_customA);
        Directory.CreateDirectory(_customB);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private BackupSettingsResolver Resolver(string? customLocation, int retention = 7)
    {
        var resolver = new BackupSettingsResolver(new RotatingBackupOptions { SafetyBackupDirectory = _safety, RetentionCount = retention });
        resolver.Apply(new AppSettingsEntity { Id = AppSettingsEntity.SingletonId, BackupLocation = customLocation });
        return resolver;
    }

    private static List<string> Seed(string dir, int count, int day = 1)
    {
        var names = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var name = $"rotating-202601{day:00}-0{i}0000.db";
            var bytes = new byte[50_000 + i];
            new Random(i + day * 100).NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(dir, name), bytes);
            names.Add(name);
        }
        return names;
    }

    private static string[] Names(string dir, string glob = "rotating-*.db") =>
        Directory.EnumerateFiles(dir, glob).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

    private static async Task<BackupSnapshotMoveStatusDto> RunAsync(BackupSnapshotMoveService service, string from, string to)
    {
        Assert.True(service.TryStart(from, to, "admin"));
        await service.CurrentJob.WaitAsync(TimeSpan.FromSeconds(30));
        return service.GetStatus();
    }

    [Fact]
    public async Task DefaultToCustom_MovesRotatingOnly_SafetySnapshotsStay()
    {
        var names = Seed(_safety, 3);
        File.WriteAllText(Path.Combine(_safety, "pre-migration-20260101-000000.db"), "m");
        File.WriteAllText(Path.Combine(_safety, "pre-restore-20260101-000000.db"), "r");
        var expectedBytes = names.Sum(n => new FileInfo(Path.Combine(_safety, n)).Length);
        var service = new BackupSnapshotMoveService(Resolver(_customA), new RotatingBackupState());

        var status = await RunAsync(service, _safety, _customA);

        Assert.Equal(BackupSnapshotMoveStates.Completed, status.State);
        Assert.Equal("default", status.FromKind);
        Assert.Equal("custom", status.ToKind);
        Assert.Equal(3, status.TotalFiles);
        Assert.Equal(3, status.FilesDone);
        Assert.Equal(3, status.MovedCount);
        Assert.Equal(expectedBytes, status.TotalBytes);
        Assert.Equal(expectedBytes, status.BytesDone);
        Assert.Empty(status.Issues);
        Assert.NotNull(status.FinishedUtc);
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), Names(_customA));
        Assert.Empty(Names(_safety));
        Assert.True(File.Exists(Path.Combine(_safety, "pre-migration-20260101-000000.db")));
        Assert.True(File.Exists(Path.Combine(_safety, "pre-restore-20260101-000000.db")));
        Assert.Empty(Directory.EnumerateFiles(_customA, "*.partial"));
    }

    [Fact]
    public async Task CustomToDefault_MovesIntoTheDataFolder_CreatingItWhenMissing()
    {
        Directory.Delete(_safety);
        var names = Seed(_customA, 2);
        var service = new BackupSnapshotMoveService(Resolver(null), new RotatingBackupState());

        var status = await RunAsync(service, _customA, _safety);

        Assert.Equal("custom", status.FromKind);
        Assert.Equal("default", status.ToKind);
        Assert.Equal(2, status.MovedCount);
        Assert.Equal(names, Names(_safety));
        Assert.Empty(Names(_customA));
    }

    [Fact]
    public async Task CustomToCustom_Moves()
    {
        var names = Seed(_customA, 2);
        var service = new BackupSnapshotMoveService(Resolver(_customB), new RotatingBackupState());

        var status = await RunAsync(service, _customA, _customB);

        Assert.Equal("custom", status.FromKind);
        Assert.Equal("custom", status.ToKind);
        Assert.Equal(names, Names(_customB));
        Assert.Empty(Names(_customA));
    }

    [Fact]
    public async Task RetentionIsAppliedInTheNewLocation_AfterTheMove()
    {
        Seed(_safety, 3, day: 1);          // older
        var newer = Seed(_customA, 2, day: 5); // already in the new location
        var service = new BackupSnapshotMoveService(Resolver(_customA, retention: 3), new RotatingBackupState());

        var status = await RunAsync(service, _safety, _customA);

        Assert.Equal(3, status.MovedCount);
        Assert.Equal(2, status.PrunedCount);
        var kept = Names(_customA);
        Assert.Equal(3, kept.Length);
        Assert.Contains(newer[0], kept);
        Assert.Contains(newer[1], kept);
        Assert.Contains("rotating-20260101-020000.db", kept); // newest of the moved ones
    }

    [Fact]
    public async Task Retention_Skipped_WhenTheDestinationIsNoLongerTheManagedFolder()
    {
        Seed(_safety, 3);
        // The effective location is B, so a move into A must not prune A.
        var service = new BackupSnapshotMoveService(Resolver(_customB, retention: 1), new RotatingBackupState());

        var status = await RunAsync(service, _safety, _customA);

        Assert.Equal(0, status.PrunedCount);
        Assert.Equal(3, Names(_customA).Length);
    }

    [Fact]
    public async Task PerFileFailure_KeepsThatSource_ReportsIt_AndContinues()
    {
        var names = Seed(_safety, 3);
        var conflict = names[1];
        File.WriteAllText(Path.Combine(_customA, conflict), "a different file with the same name");
        var service = new BackupSnapshotMoveService(Resolver(_customA), new RotatingBackupState());
        var corrupt = true;
        service.AfterTempWritten = (temp, _) =>
        {
            // Corrupt the first copy only (hash mismatch).
            if (corrupt)
            {
                corrupt = false;
                var bytes = File.ReadAllBytes(temp);
                bytes[0] ^= 0xFF;
                File.WriteAllBytes(temp, bytes);
            }
            return Task.CompletedTask;
        };

        var status = await RunAsync(service, _safety, _customA);

        Assert.Equal(BackupSnapshotMoveStates.Completed, status.State);
        Assert.Equal(3, status.FilesDone);
        Assert.Equal(1, status.MovedCount);
        Assert.Equal(2, status.Issues.Count);
        // Newest first: names[2] is corrupted, names[1] collides, names[0] moves.
        var verify = Assert.Single(status.Issues, i => i.Code == BackupSnapshotMoveOutcomes.VerifyFailed);
        Assert.Equal(names[2], verify.FileName);
        Assert.Equal(BackupSnapshotMoveLocations.Previous, verify.Location);
        var clash = Assert.Single(status.Issues, i => i.Code == BackupSnapshotMoveOutcomes.NameConflict);
        Assert.Equal(conflict, clash.FileName);
        Assert.True(File.Exists(Path.Combine(_safety, names[2])));
        Assert.True(File.Exists(Path.Combine(_safety, names[1])));
        Assert.False(File.Exists(Path.Combine(_safety, names[0])));
        Assert.Equal("a different file with the same name", File.ReadAllText(Path.Combine(_customA, conflict)));
    }

    [Fact]
    public async Task UnwritableDestination_EveryFileStaysAndIsReported()
    {
        var names = Seed(_safety, 2);
        var blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "not a folder");
        var service = new BackupSnapshotMoveService(Resolver(blocked), new RotatingBackupState());

        var status = await RunAsync(service, _safety, blocked);

        Assert.Equal(0, status.MovedCount);
        Assert.All(status.Issues, i => Assert.Equal(BackupSnapshotMoveOutcomes.CopyFailed, i.Code));
        Assert.Equal(2, status.Issues.Count);
        Assert.Equal(names, Names(_safety));
    }

    [Fact]
    public async Task EachFileIsMovedWhileHoldingTheBackupRunGate()
    {
        Seed(_safety, 2);
        var state = new RotatingBackupState();
        var service = new BackupSnapshotMoveService(Resolver(_customA), state);
        var gateHeld = new List<bool>();
        service.AfterTempWritten = (_, _) =>
        {
            gateHeld.Add(state.RunGate.CurrentCount == 0);
            return Task.CompletedTask;
        };

        // A backup "in progress" holds the gate: the move must wait for it.
        await state.RunGate.WaitAsync();
        Assert.True(service.TryStart(_safety, _customA, "admin"));
        await Task.Delay(300);
        var waiting = service.GetStatus();
        Assert.Equal(BackupSnapshotMoveStates.Running, waiting.State);
        Assert.Equal(0, waiting.FilesDone);
        Assert.Equal(2, Names(_safety).Length);
        Assert.False(service.TryStart(_safety, _customA, "admin")); // one job at a time

        state.RunGate.Release();
        await service.CurrentJob.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, service.GetStatus().MovedCount);
        Assert.Equal(new[] { true, true }, gateHeld);
        Assert.Equal(1, state.RunGate.CurrentCount);
    }

    [Fact]
    public async Task ConcurrentRotatingBackup_BothComplete_NothingLost()
    {
        var moved = Seed(_safety, 4);
        var dbPath = Path.Combine(_root, "test.db");
        var options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(dbPath)).Options;
        await using var db = new MangaPixerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        // The move goes default -> A while a backup writes into A at the same
        // time, sharing one run gate (as the singleton state does in the app).
        var state = new RotatingBackupState();
        var service = new BackupSnapshotMoveService(Resolver(_customA, retention: 100), state);

        Assert.True(service.TryStart(_safety, _customA, "admin"));
        // The backup can only run between two file moves (never mid-copy).
        var backup = await RunDefaultModeBackupAsync(db, _customA, state);
        await service.CurrentJob.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(backup.Succeeded);
        var status = service.GetStatus();
        Assert.Equal(4, status.MovedCount);
        Assert.Empty(status.Issues);
        var names = Names(_customA);
        Assert.Equal(5, names.Length);
        Assert.All(moved, n => Assert.Contains(n, names));
        Assert.Contains(backup.FileName, names);
        Assert.Empty(Directory.EnumerateFiles(_customA, "*.partial"));
    }

    [Fact]
    public async Task RotatingBackupRun_RemovesStaleMoveTemps()
    {
        var dbPath = Path.Combine(_root, "test.db");
        var options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(dbPath)).Options;
        await using var db = new MangaPixerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var stale = Path.Combine(_safety, $"rotating-move-{Guid.NewGuid():N}.partial");
        File.WriteAllText(stale, "interrupted copy");

        var outcome = await RunDefaultModeBackupAsync(db, _safety, new RotatingBackupState());

        Assert.True(outcome.Succeeded);
        Assert.False(File.Exists(stale));
    }

    /// <summary>A rotating backup in default mode whose default folder is <paramref name="dir"/>.</summary>
    private static Task<RotatingBackupOutcome> RunDefaultModeBackupAsync(MangaPixerDbContext db, string dir, RotatingBackupState state)
    {
        var resolver = new BackupSettingsResolver(new RotatingBackupOptions { SafetyBackupDirectory = dir, RetentionCount = 100 });
        return new RotatingBackupService(new BackupService(db), resolver, state).RunAsync();
    }
}
