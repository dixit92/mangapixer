namespace com.lifepixer.mangapixer.Tests.Server.Operations;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests for the custom backup location (1.22.0): settings
/// persistence + marker, rotating runs into the custom folder, the fail-loud
/// unavailable path (no write, no CreateDirectory, audited once per
/// transition), symlink smuggling, and the placement of pre-restore safety
/// snapshots (always local) vs. restore-by-name (effective rotating folder).
/// </summary>
public sealed class BackupLocationServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _dataRoot;
    private readonly string _safetyDir;
    private readonly string _archive;
    private readonly string _libraryRoot;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public BackupLocationServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mangapixer-bkloc-" + Guid.NewGuid().ToString("N")[..8]);
        _dataRoot = Path.Combine(_root, "data");
        _safetyDir = Path.Combine(_dataRoot, "backups");
        _archive = Path.Combine(_root, "archive");
        _libraryRoot = Path.Combine(_root, "library");
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_archive);
        Directory.CreateDirectory(_libraryRoot);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_dataRoot, "mangapixer.db")))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed record Harness(
        MangaPixerDbContext Db,
        BackupSettingsResolver Resolver,
        RotatingBackupState State,
        BackupLocationService Location,
        BackupSettingsService Settings,
        RotatingBackupService Rotating,
        BackupService Backup) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task<Harness> SetupAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        db.Libraries.Add(new LibraryEntity
        {
            PublicId = OpaqueId.Encode(1),
            DisplayName = "Lib",
            RootPath = _libraryRoot,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var fs = new PhysicalBackupLocationFileSystem();
        var validator = BackupLocationValidator.ForCurrentPlatform();
        var resolver = new BackupSettingsResolver(new RotatingBackupOptions { SafetyBackupDirectory = _safetyDir, RetentionCount = 2 });
        var state = new RotatingBackupState();
        var audit = new AuditService(db);
        var roots = new AppRootOptions
        {
            DataRoot = _dataRoot,
            CacheRoot = Path.Combine(_root, "cache"),
            ScratchRoot = Path.Combine(_root, "scratch"),
        };
        var location = new BackupLocationService(db, resolver, validator, fs, state, roots,
            new MediaBrowseOptions { Root = Path.Combine(_root, "media") }, audit);
        var settings = new BackupSettingsService(db, resolver, location, fs, state, audit, TimeProvider.System);
        var backup = new BackupService(db);
        var rotating = new RotatingBackupService(backup, resolver, state, location);
        return new Harness(db, resolver, state, location, settings, rotating, backup);
    }

    private static UpdateBackupSettingsRequest Custom(string location, bool validateOnly = false) => new()
    {
        Location = new BackupLocationUpdate { Mode = "custom", CustomLocation = location },
        ValidateOnly = validateOnly,
    };

    [Fact]
    public async Task SettingsRoundTrip_ThroughAppSettings()
    {
        await using var h = await SetupAsync();
        var outcome = await h.Settings.ApplyAsync(new UpdateBackupSettingsRequest
        {
            Enabled = false,
            IntervalHours = 6,
            RetentionCount = 4,
        }, "admin");
        Assert.True(outcome.Succeeded);

        await using var fresh = new MangaPixerDbContext(_options);
        var row = await fresh.AppSettings.SingleAsync();
        Assert.False(row.BackupsEnabled);
        Assert.Equal(6, row.BackupIntervalHours);
        Assert.Equal(4, row.BackupRetentionCount);
        Assert.Null(row.BackupLocation);

        var reloaded = new BackupSettingsResolver(new RotatingBackupOptions { SafetyBackupDirectory = _safetyDir });
        await reloaded.ReloadAsync(fresh);
        Assert.False(reloaded.Current.Enabled);
        Assert.Equal(BackupSettingSources.Settings, reloaded.Current.IntervalHoursSource);
        Assert.Equal(1, await fresh.AuditEvents.CountAsync(e => e.Action == AuditActions.BackupSettingsChanged));
    }

    [Fact]
    public async Task CustomLocation_WritesMarker_AndRunsIntoIt()
    {
        await using var h = await SetupAsync();
        var target = Path.Combine(_archive, "mangapixer");

        var outcome = await h.Settings.ApplyAsync(Custom(target), "admin");
        Assert.True(outcome.Succeeded, outcome.ErrorCode);
        Assert.True(outcome.Result!.WillCreate);
        Assert.True(outcome.Result.LocationChanged);
        Assert.True(File.Exists(Path.Combine(target, BackupLocationValidator.MarkerFileName)));
        Assert.Equal(BackupLocationStatuses.Ok, h.State.LocationStatus);

        var run = await h.Rotating.RunAsync();
        Assert.True(run.Succeeded);
        Assert.True(File.Exists(Path.Combine(target, run.FileName!)));
        Assert.False(Directory.Exists(_safetyDir) && Directory.EnumerateFiles(_safetyDir, "rotating-*.db").Any(),
            "A custom location must not also write into the data root.");
        Assert.Equal(1, await h.Db.AuditEvents.CountAsync(e => e.Action == AuditActions.BackupLocationChanged && e.Result == AuditResults.Success));
    }

    [Fact]
    public async Task ValidateOnly_ChangesNothing()
    {
        await using var h = await SetupAsync();
        var target = Path.Combine(_archive, "probe-only");

        var outcome = await h.Settings.ApplyAsync(Custom(target, validateOnly: true), "admin");

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.Result!.ValidateOnly);
        Assert.True(outcome.Result.WillCreate);
        Assert.False(Directory.Exists(target), "validateOnly must remove a leaf it created for the probe.");
        Assert.Equal(EffectiveBackupSettings.KindDefault, h.Resolver.Current.LocationKind);
        Assert.Equal(0, await h.Db.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task MissingMarker_SkipsTheRun_Loudly_AndAuditsTransitionsOnce()
    {
        await using var h = await SetupAsync();
        var target = Path.Combine(_archive, "mangapixer");
        Assert.True((await h.Settings.ApplyAsync(Custom(target), "admin")).Succeeded);
        var marker = Path.Combine(target, BackupLocationValidator.MarkerFileName);
        var markerJson = await File.ReadAllTextAsync(marker);

        // The share "unmounts": the marker disappears.
        File.Delete(marker);
        var first = await h.Rotating.RunAsync();
        var second = await h.Rotating.RunAsync();

        Assert.False(first.Succeeded);
        Assert.Equal(BackupLocationCodes.Unavailable, first.FailureCode);
        Assert.Equal(BackupLocationCodes.Unavailable, second.FailureCode);
        Assert.Empty(Directory.EnumerateFiles(target, "rotating-*.db"));
        Assert.Equal(BackupLocationStatuses.Unavailable, h.State.LocationStatus);
        Assert.Equal(BackupLocationCodes.Unavailable, h.State.LastFailureCode);
        Assert.Equal(1, await h.Db.AuditEvents.CountAsync(e => e.Action == AuditActions.BackupLocationUnavailable));

        // It comes back: one recovery audit, the run succeeds.
        await File.WriteAllTextAsync(marker, markerJson);
        var third = await h.Rotating.RunAsync();
        Assert.True(third.Succeeded);
        Assert.Equal(BackupLocationStatuses.Ok, h.State.LocationStatus);
        Assert.Equal(1, await h.Db.AuditEvents.CountAsync(e => e.Action == AuditActions.BackupLocationAvailable));
    }

    [Fact]
    public async Task MissingFolder_IsNeverCreatedAtRunTime()
    {
        await using var h = await SetupAsync();
        var target = Path.Combine(_archive, "mangapixer");
        Assert.True((await h.Settings.ApplyAsync(Custom(target), "admin")).Succeeded);
        Directory.Delete(target, recursive: true);

        var run = await h.Rotating.RunAsync();

        Assert.Equal(BackupLocationCodes.Unavailable, run.FailureCode);
        Assert.False(Directory.Exists(target), "A missing custom folder must never be re-created at run time.");
    }

    [Fact]
    public async Task LibraryRootOverlap_IsRejected_OnSave()
    {
        await using var h = await SetupAsync();
        var outcome = await h.Settings.ApplyAsync(Custom(Path.Combine(_libraryRoot, "backups")), "admin");
        Assert.Equal(BackupLocationCodes.OverlapsProtected, outcome.ErrorCode);
        Assert.Equal(1, await h.Db.AuditEvents.CountAsync(e => e.Action == AuditActions.BackupLocationChanged && e.Result == AuditResults.Failure));
    }

    [Fact]
    public async Task SymlinkIntoLibraryRoot_IsRejected()
    {
        if (OperatingSystem.IsWindows())
            return; // symlink creation needs privileges on Windows; the rule is covered by the unit tests
        await using var h = await SetupAsync();
        var inside = Path.Combine(_libraryRoot, "sub");
        Directory.CreateDirectory(inside);
        var link = Path.Combine(_archive, "sneaky");
        Directory.CreateSymbolicLink(link, inside);

        var outcome = await h.Settings.ApplyAsync(Custom(link), "admin");

        Assert.Equal(BackupLocationCodes.OverlapsProtected, outcome.ErrorCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(inside));
    }

    [Fact]
    public async Task ForeignMarker_IsInUse_UntilAdopted()
    {
        await using var h = await SetupAsync();
        var target = Path.Combine(_archive, "shared");
        Directory.CreateDirectory(target);
        new PhysicalBackupLocationFileSystem().WriteMarker(target, "someone-else", DateTimeOffset.UtcNow);

        var refused = await h.Settings.ApplyAsync(Custom(target), "admin");
        Assert.Equal(BackupLocationCodes.InUse, refused.ErrorCode);

        var adopted = await h.Settings.ApplyAsync(Custom(target) with { AdoptExistingMarker = true }, "admin");
        Assert.True(adopted.Succeeded);
        Assert.True((await h.Rotating.RunAsync()).Succeeded);
    }

    [Fact]
    public async Task PreRestoreSnapshot_StaysLocal_RestoreByName_ReadsTheCustomFolder()
    {
        await using var h = await SetupAsync();
        var target = Path.Combine(_archive, "mangapixer");
        Assert.True((await h.Settings.ApplyAsync(Custom(target), "admin")).Succeeded);
        var run = await h.Rotating.RunAsync();
        Assert.True(run.Succeeded);

        var restore = new DbRestoreService(h.Db, h.Backup, new DbRestoreOptions(),
            new AppRootOptions { DataRoot = _dataRoot }, h.Resolver);

        // Traversal is still rejected against the custom folder.
        var traversal = await restore.StageRestoreFromBackupAsync("../data/mangapixer.db", "admin");
        Assert.Equal("invalid_backup_name", traversal.Error);

        var staged = await restore.StageRestoreFromBackupAsync(run.FileName!, "admin");
        Assert.True(staged.Succeeded, staged.Message);
        Assert.True(File.Exists(Path.Combine(_safetyDir, staged.PreRestoreBackupFileName!)),
            "The pre-restore snapshot belongs in the local safety folder.");
        Assert.False(File.Exists(Path.Combine(target, staged.PreRestoreBackupFileName!)));
    }

    [Fact]
    public async Task PreRestoreSnapshots_ArePrunedToTheNewestThree()
    {
        await using var h = await SetupAsync();
        Directory.CreateDirectory(_safetyDir);
        foreach (var ts in new[] { "20250101-000000", "20250201-000000", "20250301-000000", "20250401-000000" })
            await File.WriteAllTextAsync(Path.Combine(_safetyDir, $"pre-restore-{ts}.db"), "old");
        await File.WriteAllTextAsync(Path.Combine(_safetyDir, "pre-restore-manual-copy.db"), "not generated");
        await File.WriteAllTextAsync(Path.Combine(_safetyDir, "pre-migration-20250101-000000.db"), "other kind");

        var restore = new DbRestoreService(h.Db, h.Backup, new DbRestoreOptions(),
            new AppRootOptions { DataRoot = _dataRoot }, h.Resolver);
        var source = Path.Combine(_root, "valid.db");
        Assert.True((await h.Backup.BackupAsync(source)).Succeeded);
        await using (var stream = File.OpenRead(source))
        {
            var staged = await restore.StageRestoreAsync(stream, "admin");
            Assert.True(staged.Succeeded, staged.Message);
        }

        var remaining = Directory.EnumerateFiles(_safetyDir, "pre-restore-*.db").Select(Path.GetFileName).ToList();
        Assert.Equal(4, remaining.Count); // newest 3 generated + the non-generated file
        Assert.Contains("pre-restore-manual-copy.db", remaining);
        Assert.Contains("pre-restore-20250401-000000.db", remaining);
        Assert.Contains("pre-restore-20250301-000000.db", remaining);
        Assert.True(File.Exists(Path.Combine(_safetyDir, "pre-migration-20250101-000000.db")),
            "Pruning one kind never touches the other kind.");
    }

    [Fact]
    public async Task SwitchingBackToDefault_ResumesTheDataRootFolder_AndLeavesOldSnapshots()
    {
        await using var h = await SetupAsync();
        var target = Path.Combine(_archive, "mangapixer");
        Assert.True((await h.Settings.ApplyAsync(Custom(target), "admin")).Succeeded);
        var customRun = await h.Rotating.RunAsync();

        var back = await h.Settings.ApplyAsync(new UpdateBackupSettingsRequest
        {
            Location = new BackupLocationUpdate { Mode = "default" },
        }, "admin");
        Assert.True(back.Succeeded);
        Assert.True(back.Result!.LocationChanged);
        var defaultRun = await h.Rotating.RunAsync();

        Assert.True(File.Exists(Path.Combine(target, customRun.FileName!)), "Old snapshots are left in place.");
        Assert.True(File.Exists(Path.Combine(_safetyDir, defaultRun.FileName!)));

        // Switching back to the same custom folder needs no takeover (stable marker id).
        Assert.True((await h.Settings.ApplyAsync(Custom(target), "admin")).Succeeded);
    }
}
