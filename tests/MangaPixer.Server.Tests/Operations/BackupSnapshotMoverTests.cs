namespace com.lifepixer.mangapixer.Tests.Server.Operations;

using com.lifepixer.mangapixer.Server.Operations;
using Xunit;

/// <summary>
/// Unit / filesystem tests for the file-level snapshot move engine
/// (<see cref="BackupSnapshotMover"/>): strict name filter, copy + verify +
/// atomic rename + source delete, and every failure path keeping the source.
/// Uses only per-test temp directories.
/// </summary>
public sealed class BackupSnapshotMoverTests : IDisposable
{
    private readonly string _root;
    private readonly string _from;
    private readonly string _to;

    public BackupSnapshotMoverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mangapixer-snapmove-" + Guid.NewGuid().ToString("N")[..8]);
        _from = Path.Combine(_root, "from");
        _to = Path.Combine(_root, "to");
        Directory.CreateDirectory(_from);
        Directory.CreateDirectory(_to);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static byte[] Content(int seed, int length = 300_000)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Write(string dir, string name, byte[] content)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private IEnumerable<string> TempFiles() =>
        Directory.EnumerateFiles(_to).Select(Path.GetFileName).Where(n => n!.EndsWith(".partial"))!;

    [Fact]
    public void ListCandidates_OnlyStrictRotatingNames()
    {
        Write(_from, "rotating-20260101-000000.db", Content(1, 10));
        Write(_from, "rotating-20260102-000000-ab12.db", Content(2, 10));
        Write(_from, "pre-migration-20260101-000000.db", Content(3, 10));
        Write(_from, "pre-restore-20260101-000000.db", Content(4, 10));
        Write(_from, "rotating-latest.db", Content(5, 10));
        Write(_from, "rotating-20260101-000000.db.bak", Content(6, 10));
        Write(_from, ".mangapixer-backups.json", Content(7, 10));

        var names = BackupSnapshotMover.ListCandidates(_from).Select(f => f.Name).ToList();

        Assert.Equal(new[] { "rotating-20260102-000000-ab12.db", "rotating-20260101-000000.db" }, names);
        Assert.Equal((2, 20L), BackupSnapshotMover.Summarize(_from));
    }

    [Theory]
    [InlineData("pre-migration-20260101-000000.db")]
    [InlineData("pre-restore-20260101-000000.db")]
    [InlineData(".mangapixer-backups.json")]
    [InlineData("../rotating-20260101-000000.db")]
    public async Task MoveFile_RefusesNonGeneratedNames(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => BackupSnapshotMover.MoveFileAsync(_from, _to, name));
    }

    [Fact]
    public async Task MoveFile_CopiesVerifiesRenamesThenDeletesSource()
    {
        const string name = "rotating-20260101-000000.db";
        var content = Content(1);
        var source = Write(_from, name, content);
        long reported = 0;

        var result = await BackupSnapshotMover.MoveFileAsync(_from, _to, name, n => reported += n);

        Assert.Equal(BackupSnapshotMoveOutcomes.Moved, result.Outcome);
        Assert.False(result.IsIssue);
        Assert.Equal(BackupSnapshotMoveLocations.New, result.Location);
        Assert.False(File.Exists(source));
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(_to, name)));
        Assert.Equal(content.Length, reported);
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task MoveFile_IdenticalFileAlreadyThere_SkipsCopyAndDeletesSource()
    {
        const string name = "rotating-20260101-000000.db";
        var content = Content(1);
        var source = Write(_from, name, content);
        Write(_to, name, content);

        var result = await BackupSnapshotMover.MoveFileAsync(_from, _to, name);

        Assert.Equal(BackupSnapshotMoveOutcomes.AlreadyPresent, result.Outcome);
        Assert.False(result.IsIssue);
        Assert.False(File.Exists(source));
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(_to, name)));
    }

    [Fact]
    public async Task MoveFile_DifferentFileAlreadyThere_KeepsBothAndReports()
    {
        const string name = "rotating-20260101-000000.db";
        var mine = Content(1);
        var theirs = Content(2);
        var source = Write(_from, name, mine);
        var existing = Write(_to, name, theirs);

        var result = await BackupSnapshotMover.MoveFileAsync(_from, _to, name);

        Assert.Equal(BackupSnapshotMoveOutcomes.NameConflict, result.Outcome);
        Assert.True(result.IsIssue);
        Assert.Equal(BackupSnapshotMoveLocations.Previous, result.Location);
        Assert.Equal(mine, File.ReadAllBytes(source));
        Assert.Equal(theirs, File.ReadAllBytes(existing)); // never overwritten
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task MoveFile_HashMismatch_DiscardsCopyAndKeepsSource()
    {
        const string name = "rotating-20260101-000000.db";
        var content = Content(1);
        var source = Write(_from, name, content);

        // Flip one byte of the flushed copy: same size, different SHA-256.
        var result = await BackupSnapshotMover.MoveFileAsync(_from, _to, name, afterTempWritten: (temp, _) =>
        {
            var bytes = File.ReadAllBytes(temp);
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(temp, bytes);
            return Task.CompletedTask;
        });

        Assert.Equal(BackupSnapshotMoveOutcomes.VerifyFailed, result.Outcome);
        Assert.Equal(BackupSnapshotMoveLocations.Previous, result.Location);
        Assert.Equal(content, File.ReadAllBytes(source));
        Assert.False(File.Exists(Path.Combine(_to, name)));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task MoveFile_SizeMismatch_DiscardsCopyAndKeepsSource()
    {
        const string name = "rotating-20260101-000000.db";
        var source = Write(_from, name, Content(1));

        var result = await BackupSnapshotMover.MoveFileAsync(_from, _to, name, afterTempWritten: (temp, _) =>
        {
            File.AppendAllText(temp, "x");
            return Task.CompletedTask;
        });

        Assert.Equal(BackupSnapshotMoveOutcomes.VerifyFailed, result.Outcome);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(Path.Combine(_to, name)));
    }

    [Fact]
    public async Task MoveFile_DestinationUnwritable_KeepsSource()
    {
        const string name = "rotating-20260101-000000.db";
        var content = Content(1);
        var source = Write(_from, name, content);
        // A regular FILE where the destination folder should be: nothing can be
        // created in it (works even when the tests run as root).
        var blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "not a folder");

        var result = await BackupSnapshotMover.MoveFileAsync(_from, blocked, name);

        Assert.Equal(BackupSnapshotMoveOutcomes.CopyFailed, result.Outcome);
        Assert.Equal(BackupSnapshotMoveLocations.Previous, result.Location);
        Assert.Equal(content, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task MoveFile_WriteFailsMidway_KeepsSourceAndRemovesTemp()
    {
        const string name = "rotating-20260101-000000.db";
        var source = Write(_from, name, Content(1));

        var result = await BackupSnapshotMover.MoveFileAsync(_from, _to, name,
            afterTempWritten: (_, _) => throw new IOException("disk full"));

        Assert.Equal(BackupSnapshotMoveOutcomes.CopyFailed, result.Outcome);
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.EnumerateFiles(_to));
    }

    [Fact]
    public async Task MoveFile_Cancelled_KeepsSourceAndRemovesTemp()
    {
        const string name = "rotating-20260101-000000.db";
        var source = Write(_from, name, Content(1));
        using var cts = new CancellationTokenSource();

        var result = await BackupSnapshotMover.MoveFileAsync(_from, _to, name, afterTempWritten: (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, ct: cts.Token);

        Assert.Equal(BackupSnapshotMoveOutcomes.Cancelled, result.Outcome);
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.EnumerateFiles(_to));
    }

    [Fact]
    public async Task MoveFile_SourceGone_IsNotAnIssue()
    {
        var result = await BackupSnapshotMover.MoveFileAsync(_from, _to, "rotating-20260101-000000.db");

        Assert.Equal(BackupSnapshotMoveOutcomes.SourceMissing, result.Outcome);
        Assert.False(result.IsIssue);
    }

    [Fact]
    public void DeleteStaleTemps_OnlyStrictTempNames()
    {
        var stale = Write(_to, $"rotating-move-{Guid.NewGuid():N}.partial", Content(1, 10));
        var snapshot = Write(_to, "rotating-20260101-000000.db", Content(2, 10));
        var lookalike = Write(_to, "rotating-move-notahexguid.partial", Content(3, 10));
        var other = Write(_to, "notes.partial", Content(4, 10));

        Assert.Equal(1, BackupSnapshotMover.DeleteStaleTemps(_to));

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(snapshot));
        Assert.True(File.Exists(lookalike));
        Assert.True(File.Exists(other));
        Assert.Equal(0, BackupSnapshotMover.DeleteStaleTemps(Path.Combine(_root, "missing")));
    }
}
