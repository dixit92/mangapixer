namespace com.lifepixer.mangapixer.Tests.Server.Operations;

using System.IO.Compression;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests (1.49.0, lane B) for compressed rotating backups: the gzip snapshot is created through the
/// scheduler's own path (<see cref="RotatingBackupService.RunAsync"/>), is a valid database once unpacked, leaves no
/// temporary file, mixes with plain <c>.db</c> snapshots in retention / listing / seeding, and restore unpacks a gzip
/// stream (by its magic bytes) under a cap on the UNPACKED bytes.
/// </summary>
public sealed class CompressedBackupServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dataRoot;
    private readonly string _backupsDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public CompressedBackupServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-gzbackup-" + Guid.NewGuid().ToString("N")[..8]);
        _dataRoot = Path.Combine(_tempDir, "data");
        _backupsDir = Path.Combine(_dataRoot, "backups");
        Directory.CreateDirectory(_backupsDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_dataRoot, "mangapixer.db")))
            .Options;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<MangaPixerDbContext> OpenDbAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        db.Libraries.Add(new LibraryEntity
        {
            PublicId = OpaqueId.Encode(1),
            DisplayName = "Test",
            RootPath = "/private/test",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Users.Add(new UserEntity
        {
            PublicId = OpaqueId.Encode(2),
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            IsActive = true,
            IsAdmin = true,
            PasswordHash = "hash",
            SecurityStamp = "stamp",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return db;
    }

    private RotatingBackupService RotatingFor(MangaPixerDbContext db, bool compress = true, int retention = 7) =>
        new(new BackupService(db),
            new BackupSettingsResolver(new RotatingBackupOptions
            {
                SafetyBackupDirectory = _backupsDir,
                RetentionCount = retention,
                Compress = compress,
            }),
            new RotatingBackupState());

    private DbRestoreService RestoreFor(MangaPixerDbContext db, long maxBytes = 64 * 1024 * 1024) =>
        new(db, new BackupService(db), new DbRestoreOptions { MaxUploadBytes = maxBytes },
            new AppRootOptions { DataRoot = _dataRoot },
            new BackupSettingsResolver(new RotatingBackupOptions { SafetyBackupDirectory = _backupsDir }));

    private static async Task GunzipAsync(string gz, string target)
    {
        await using var input = File.OpenRead(gz);
        await using var unpack = new GZipStream(input, CompressionMode.Decompress);
        await using var output = File.Create(target);
        await unpack.CopyToAsync(output);
    }

    private static byte[] Gzip(byte[] plain)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(plain);
        return ms.ToArray();
    }

    private static string[] Files(string dir) => Directory.EnumerateFiles(dir).Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private string PendingStaged => Path.Combine(_dataRoot, "restore-pending", "mangapixer.db.staged");

    // --- creation ---

    [Fact]
    public async Task Run_CreatesGzipSnapshot_ThatUnpacksToAValidDatabase_AndLeavesNoTemporaryFile()
    {
        await using var db = await OpenDbAsync();
        var service = RotatingFor(db);

        var outcome = await service.RunAsync();

        Assert.True(outcome.Succeeded);
        Assert.EndsWith(".db.gz", outcome.FileName);
        Assert.Matches(RotatingBackupService.FileNamePattern(), outcome.FileName!);
        var gz = Path.Combine(_backupsDir, outcome.FileName!);
        var head = new byte[2];
        await using (var fs = File.OpenRead(gz)) _ = await fs.ReadAsync(head);
        Assert.True(BackupCompression.HasGzipMagic(head));

        var plain = Path.Combine(_tempDir, "unpacked.db");
        await GunzipAsync(gz, plain);
        var validation = await DbRestoreService.ValidateBackupAsync(plain);
        Assert.True(validation.IsValid, validation.Error);
        // The only file is the snapshot: no .partial, no rotating-build-*.tmp, no -wal/-shm.
        Assert.Equal(new[] { outcome.FileName! }, Files(_backupsDir));
        Assert.Equal(1, outcome.RetainedCount);
    }

    [Fact]
    public async Task Run_WithCompressionOff_WritesAPlainDbAsBefore()
    {
        await using var db = await OpenDbAsync();
        var outcome = await RotatingFor(db, compress: false).RunAsync();

        Assert.True(outcome.Succeeded);
        Assert.EndsWith(".db", outcome.FileName);
        Assert.DoesNotContain(".gz", outcome.FileName);
        Assert.True((await DbRestoreService.ValidateBackupAsync(Path.Combine(_backupsDir, outcome.FileName!))).IsValid);
    }

    [Fact]
    public void CompressionOption_ReadsTheConfigurationKey_AndFlagsAnInvalidValue()
    {
        static RotatingBackupOptions Parse(string? value)
        {
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(value is null ? new Dictionary<string, string?>() : new() { ["MangaPixer:Backups:Compress"] = value })
                .Build();
            return RotatingBackupOptions.FromConfiguration(config, "/backups");
        }

        Assert.True(Parse(null).Compress);
        Assert.False(Parse("false").Compress);
        var bad = Parse("sometimes");
        Assert.True(bad.Compress);
        Assert.Contains("MangaPixer:Backups:Compress", bad.InvalidKeys);
    }

    [Fact]
    public async Task Run_WhenAPlainSnapshotHoldsTheSameSecond_TakesTheSuffixedName()
    {
        await using var db = await OpenDbAsync();
        // Occupy the plain names of this second and the next: the compressed run must count them as taken.
        var now = DateTime.UtcNow;
        var occupied = new[] { now, now.AddSeconds(1) }.Select(t => $"rotating-{t:yyyyMMdd-HHmmss}.db").ToArray();
        foreach (var name in occupied)
            await File.WriteAllTextAsync(Path.Combine(_backupsDir, name), "occupied");

        var outcome = await RotatingFor(db).RunAsync();

        Assert.True(outcome.Succeeded);
        Assert.Matches(@"^rotating-\d{8}-\d{6}-[0-9a-f]{4}\.db\.gz$", outcome.FileName!);
    }

    [Fact]
    public async Task BackupCompressed_WhenThePartialNameIsTaken_FailsWithoutAFinalFileOrTemporaryFile()
    {
        await using var db = await OpenDbAsync();
        var target = Path.Combine(_backupsDir, "rotating-20260101-000000.db.gz");
        await File.WriteAllTextAsync(target + ".partial", "in the way");
        var work = Path.Combine(_dataRoot, "work");

        var result = await new BackupService(db).BackupCompressedAsync(target, work);

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(target));
        Assert.Empty(Files(work));
        // The foreign file at the partial name is removed too (a stale partial is never kept).
        Assert.False(File.Exists(target + ".partial"));
    }

    [Fact]
    public async Task Run_RemovesLeftoversOfAnInterruptedCompression_AndOnlyThose()
    {
        await using var db = await OpenDbAsync();
        var stalePartial = "rotating-20260101-000000.db.gz.partial";
        var staleTemp = "rotating-build-" + new string('a', 32) + ".tmp";
        var staleWal = "rotating-build-" + new string('b', 32) + ".tmp-wal";
        foreach (var name in new[] { stalePartial, staleTemp, staleWal, "rotating-notes.txt", "rotating-build-x.tmp", "pre-restore-20260101-000000.db" })
            await File.WriteAllTextAsync(Path.Combine(_backupsDir, name), "x");

        var outcome = await RotatingFor(db).RunAsync();

        Assert.True(outcome.Succeeded);
        var left = Files(_backupsDir);
        Assert.DoesNotContain(stalePartial, left);
        Assert.DoesNotContain(staleTemp, left);
        Assert.DoesNotContain(staleWal, left);
        Assert.Contains("rotating-notes.txt", left);
        Assert.Contains("rotating-build-x.tmp", left);
        Assert.Contains("pre-restore-20260101-000000.db", left);
    }

    [Fact]
    public async Task Restore_PreRestoreSafetySnapshotStaysPlainDb()
    {
        await using var db = await OpenDbAsync();
        var restore = RestoreFor(db);
        var made = await new BackupService(db).BackupAsync(Path.Combine(_tempDir, "src.db"));
        Assert.True(made.Succeeded);

        await using var upload = File.OpenRead(made.Path!);
        var staged = await restore.StageRestoreAsync(upload, "admin");

        Assert.True(staged.Succeeded);
        Assert.Matches(@"^pre-restore-\d{8}-\d{6}\.db$", staged.PreRestoreBackupFileName!);
    }

    // --- names, retention, listing, seeding ---

    [Theory]
    [InlineData("rotating-20260901-000000.db.gz", true)]
    [InlineData("rotating-20260901-000000-0a9f.db.gz", true)]
    [InlineData("rotating-20260901-000000.db.gz.partial", false)]
    [InlineData("rotating-20260901-000000.db.gzz", false)]
    [InlineData("rotating-20260901-000000.gz", false)]
    [InlineData("pre-restore-20260901-000000.db.gz", false)]
    public void FileNamePattern_AcceptsCompressedNames(string name, bool expected)
    {
        Assert.Equal(expected, RotatingBackupService.FileNamePattern().IsMatch(name));
    }

    [Fact]
    public async Task Prune_MixesPlainAndGzipByTimestamp()
    {
        await using var db = await OpenDbAsync();
        var service = RotatingFor(db, retention: 3);
        // Five days, alternating kinds: retention counts both kinds together, oldest by timestamp first.
        var names = new[]
        {
            "rotating-20260901-000000.db", "rotating-20260902-000000.db.gz", "rotating-20260903-000000.db",
            "rotating-20260904-000000.db.gz", "rotating-20260905-000000.db",
        };
        foreach (var n in names) await File.WriteAllTextAsync(Path.Combine(_backupsDir, n), "x");

        var deleted = service.Prune(_backupsDir);

        Assert.Equal(2, deleted);
        Assert.Equal(
            new[] { "rotating-20260903-000000.db", "rotating-20260904-000000.db.gz", "rotating-20260905-000000.db" },
            Files(_backupsDir));
    }

    [Fact]
    public async Task ListBackups_OrdersMixedKindsNewestFirstByTimestamp_NotByName()
    {
        await using var db = await OpenDbAsync();
        var service = RotatingFor(db);
        // By ordinal name the "-ab12.db" would sort BEFORE ".db.gz" of the same second ('-' < '.'); by timestamp they tie
        // and the day decides. A later day with a plain name must still come first.
        foreach (var n in new[] { "rotating-20260902-000000.db.gz", "rotating-20260902-000000-ab12.db", "rotating-20260903-000000.db", "rotating-20260901-235959.db.gz" })
            await File.WriteAllTextAsync(Path.Combine(_backupsDir, n), "x");

        var listed = service.ListBackups(_backupsDir).Select(f => f.FileName).ToArray();

        Assert.Equal("rotating-20260903-000000.db", listed[0]);
        Assert.Equal("rotating-20260901-235959.db.gz", listed[^1]);
        Assert.Equal(4, listed.Length);
        // The same-second pair keeps a stable order (name, descending).
        Assert.Equal(new[] { "rotating-20260902-000000.db.gz", "rotating-20260902-000000-ab12.db" }, listed[1..3]);
    }

    [Fact]
    public async Task NewestSnapshotTimestamp_AndParseTimestamp_ReadACompressedName()
    {
        await File.WriteAllTextAsync(Path.Combine(_backupsDir, "rotating-20260903-120000.db.gz"), "x");
        await File.WriteAllTextAsync(Path.Combine(_backupsDir, "rotating-20260902-120000.db"), "x");

        var newest = RotatingBackupService.NewestSnapshotTimestamp(_backupsDir);

        Assert.Equal(new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero), newest);
        Assert.Equal(new DateTimeOffset(2026, 9, 3, 12, 0, 1, TimeSpan.Zero), RotatingBackupService.ParseTimestamp("rotating-20260903-120001-ab12.db.gz"));
    }

    [Fact]
    public async Task Mover_MovesACompressedSnapshotByteForByte()
    {
        var to = Path.Combine(_tempDir, "to");
        Directory.CreateDirectory(to);
        const string name = "rotating-20260101-000000.db.gz";
        var bytes = new byte[50_000];
        new Random(7).NextBytes(bytes);
        await File.WriteAllBytesAsync(Path.Combine(_backupsDir, name), bytes);

        var result = await BackupSnapshotMover.MoveFileAsync(_backupsDir, to, name);

        Assert.Equal(BackupSnapshotMoveOutcomes.Moved, result.Outcome);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(to, name)));
        Assert.False(File.Exists(Path.Combine(_backupsDir, name)));
    }

    // --- restore ---

    [Fact]
    public async Task Stage_GzipStream_IsUnpackedByItsMagicBytes_WhateverItIsCalled()
    {
        await using var db = await OpenDbAsync();
        var outcome = await RotatingFor(db).RunAsync();
        var restore = RestoreFor(db);

        // The stream has no name at all: detection is by content.
        await using var upload = File.OpenRead(Path.Combine(_backupsDir, outcome.FileName!));
        var staged = await restore.StageRestoreAsync(upload, "admin");

        Assert.True(staged.Succeeded, staged.Message);
        var head = new byte[16];
        await using (var fs = File.OpenRead(PendingStaged)) _ = await fs.ReadAsync(head);
        Assert.Equal("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(head));
        Assert.True((await DbRestoreService.ValidateBackupAsync(PendingStaged)).IsValid);
    }

    [Fact]
    public async Task Stage_GzipFromAForwardOnlyStream_Works()
    {
        await using var db = await OpenDbAsync();
        var outcome = await RotatingFor(db).RunAsync();
        var bytes = await File.ReadAllBytesAsync(Path.Combine(_backupsDir, outcome.FileName!));
        await using var forwardOnly = new ForwardOnlyStream(bytes);

        var staged = await RestoreFor(db).StageRestoreAsync(forwardOnly, "admin");

        Assert.True(staged.Succeeded, staged.Message);
    }

    [Fact]
    public async Task Stage_PlainDbBackup_StillRestores()
    {
        await using var db = await OpenDbAsync();
        var old = await RotatingFor(db, compress: false).RunAsync();
        await using var upload = File.OpenRead(Path.Combine(_backupsDir, old.FileName!));

        var staged = await RestoreFor(db).StageRestoreAsync(upload, "admin");

        Assert.True(staged.Succeeded, staged.Message);
        Assert.True((await DbRestoreService.ValidateBackupAsync(PendingStaged)).IsValid);
    }

    [Fact]
    public async Task StageFromBackup_PicksACompressedSnapshotByName()
    {
        await using var db = await OpenDbAsync();
        var outcome = await RotatingFor(db).RunAsync();

        var staged = await RestoreFor(db).StageRestoreFromBackupAsync(outcome.FileName!, "admin");

        Assert.True(staged.Succeeded, staged.Message);
        Assert.True((await DbRestoreService.ValidateBackupAsync(PendingStaged)).IsValid);
    }

    [Fact]
    public async Task Stage_CorruptGzip_IsRefusedBeforeAnythingIsStaged()
    {
        await using var db = await OpenDbAsync();
        var outcome = await RotatingFor(db).RunAsync();
        var bytes = await File.ReadAllBytesAsync(Path.Combine(_backupsDir, outcome.FileName!));
        Assert.True(bytes.Length > 600);
        // Damage the deflate body, and (second case) the trailer's CRC-32.
        var body = (byte[])bytes.Clone();
        for (var i = 200; i < 260; i++) body[i] ^= 0x5A;
        var trailer = (byte[])bytes.Clone();
        trailer[^8] ^= 0xFF;
        var restore = RestoreFor(db);
        var preRestoreBefore = Files(_backupsDir).Count(n => n.StartsWith("pre-restore-", StringComparison.Ordinal));

        foreach (var damaged in new[] { body, trailer, bytes[..(bytes.Length / 2)], new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03 } })
        {
            await using var upload = new MemoryStream(damaged);
            var staged = await restore.StageRestoreAsync(upload, "admin");

            Assert.False(staged.Succeeded);
            Assert.Equal("invalid_backup", staged.Error);
            Assert.False(File.Exists(PendingStaged));
            Assert.False(File.Exists(Path.Combine(_dataRoot, "restore-pending", "restore.json")));
        }
        // No pre-restore snapshot was taken for any of them.
        Assert.Equal(preRestoreBefore, Files(_backupsDir).Count(n => n.StartsWith("pre-restore-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Stage_GzipThatUnpacksPastTheCap_IsRefusedBeforeAnythingIsStaged()
    {
        await using var db = await OpenDbAsync();
        // 8 MiB of zeros is a few KiB of gzip; the cap is 1 MiB of UNPACKED bytes.
        var bomb = Gzip(new byte[8 * 1024 * 1024]);
        Assert.True(bomb.Length < 64 * 1024);
        await using var upload = new MemoryStream(bomb);
        var restore = RestoreFor(db, maxBytes: 1024 * 1024);

        var staged = await restore.StageRestoreAsync(upload, "admin");

        Assert.False(staged.Succeeded);
        Assert.Equal("upload_too_large", staged.Error);
        Assert.False(File.Exists(PendingStaged));
        Assert.False(File.Exists(Path.Combine(_dataRoot, "restore-pending", "restore.json")));
        Assert.DoesNotContain(Files(_backupsDir), n => n.StartsWith("pre-restore-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stage_GzipExactlyAtTheCap_IsNotRefusedForSize()
    {
        await using var db = await OpenDbAsync();
        // Exactly the cap: not "too large" (it then fails validation, being zeros).
        var atCap = Gzip(new byte[1024 * 1024]);
        await using var upload = new MemoryStream(atCap);

        var staged = await RestoreFor(db, maxBytes: 1024 * 1024).StageRestoreAsync(upload, "admin");

        Assert.Equal("invalid_backup", staged.Error);
    }

    [Fact]
    public async Task Stage_PlainBackupBeyondTheCap_IsStillRefused_AndItsStagingFileRemoved()
    {
        await using var db = await OpenDbAsync();
        var bytes = new byte[2 * 1024 * 1024];
        System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(bytes, 0);
        await using var upload = new MemoryStream(bytes);

        var staged = await RestoreFor(db, maxBytes: 1024 * 1024).StageRestoreAsync(upload, "admin");

        Assert.Equal("upload_too_large", staged.Error);
        Assert.False(File.Exists(PendingStaged));
    }

    private sealed class ForwardOnlyStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 7919)); // short reads
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
