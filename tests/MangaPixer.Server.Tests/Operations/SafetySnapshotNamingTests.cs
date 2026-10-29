namespace com.lifepixer.mangapixer.Tests.Server.Operations;

using com.lifepixer.mangapixer.Server.Operations;
using Xunit;

/// <summary>
/// Service-with-files tests (1.50.0, lane B) for the safety snapshots (<c>pre-restore-*</c>, <c>pre-migration-*</c>) in
/// <c>&lt;dataRoot&gt;/backups</c>: a second snapshot of the same second takes a <c>-xxxx</c> suffix (it used to fail, because
/// <c>VACUUM INTO</c> refuses an existing target), and the pruner keeps the newest three by the timestamp in the name, a
/// suffixed same-second snapshot counting as LATER than the plain one (ordinal name order ranks it older: '-' &lt; '.').
/// </summary>
public sealed class SafetySnapshotNamingTests : IDisposable
{
    private static readonly DateTime Second = new(2026, 10, 28, 12, 0, 5, DateTimeKind.Utc);
    private readonly string _dir;

    public SafetySnapshotNamingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-safety-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Touch(string name, DateTime writtenUtc)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    private string[] Names(string glob) => Directory.EnumerateFiles(_dir, glob).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

    [Theory]
    [InlineData("pre-restore")]
    [InlineData("pre-migration")]
    public void UniqueName_IsThePlainSecondName_UntilItIsTaken_ThenSuffixed(string kind)
    {
        var plain = SafetySnapshotPruner.UniqueName(_dir, kind, Second);
        Assert.Equal($"{kind}-20261028-120005.db", plain);

        File.WriteAllText(Path.Combine(_dir, plain), "x");
        var second = SafetySnapshotPruner.UniqueName(_dir, kind, Second);
        Assert.Matches($@"^{kind}-20261028-120005-[0-9a-f]{{4}}\.db$", second);
        Assert.NotEqual(plain, second);

        File.WriteAllText(Path.Combine(_dir, second), "x");
        var third = SafetySnapshotPruner.UniqueName(_dir, kind, Second);
        Assert.NotEqual(second, third);
        Assert.False(File.Exists(Path.Combine(_dir, third)));
        var pattern = kind == "pre-restore" ? SafetySnapshotPruner.PreRestorePattern() : SafetySnapshotPruner.PreMigrationPattern();
        Assert.All(new[] { plain, second, third }, n => Assert.Matches(pattern, n));
    }

    [Fact]
    public void PrunePreRestore_SameSecondSnapshots_KeepTheNewestThree_BySuffixedAfterPlain()
    {
        // One second, four snapshots, written in this order. By ordinal name the plain one would rank NEWEST ('-' < '.') and a
        // suffixed (newer) snapshot would be pruned as the 4th. The suffixes are chosen so name order disagrees with write order.
        var t0 = Second;
        Touch("pre-restore-20261028-120005.db", t0);
        Touch("pre-restore-20261028-120005-ffff.db", t0.AddMilliseconds(10));
        Touch("pre-restore-20261028-120005-8000.db", t0.AddMilliseconds(20));
        Touch("pre-restore-20261028-120005-0001.db", t0.AddMilliseconds(30));

        var deleted = SafetySnapshotPruner.PrunePreRestore(_dir);

        Assert.Equal(1, deleted);
        Assert.Equal(
            new[] { "pre-restore-20261028-120005-0001.db", "pre-restore-20261028-120005-8000.db", "pre-restore-20261028-120005-ffff.db" },
            Names("pre-restore-*.db"));
    }

    [Fact]
    public void PrunePreRestore_OrdersByTheTimestampInTheName_BeforeAnythingElse()
    {
        // The older second has the newer write time (a restored copy of the folder): the name's timestamp still decides.
        Touch("pre-restore-20261028-120001-aaaa.db", Second.AddHours(1));
        Touch("pre-restore-20261028-120002.db", Second);
        Touch("pre-restore-20261028-120003.db", Second);
        Touch("pre-restore-20261028-120004-0000.db", Second);
        Touch("pre-restore-manual-copy.db", Second);
        Touch("pre-migration-20250101-000000.db", Second);

        var deleted = SafetySnapshotPruner.PrunePreRestore(_dir);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(Path.Combine(_dir, "pre-restore-20261028-120001-aaaa.db")));
        Assert.True(File.Exists(Path.Combine(_dir, "pre-restore-manual-copy.db")));
        Assert.True(File.Exists(Path.Combine(_dir, "pre-migration-20250101-000000.db")));
        Assert.Equal(4, Names("pre-restore-*.db").Length); // the newest three + the non-generated name
    }

    [Fact]
    public void PrunePreMigration_SameSecondSnapshots_AlsoKeepTheNewestThree()
    {
        Touch("pre-migration-20261028-120005.db", Second);
        Touch("pre-migration-20261028-120005-ffff.db", Second.AddMilliseconds(10));
        Touch("pre-migration-20261028-120005-0001.db", Second.AddMilliseconds(20));
        Touch("pre-migration-20261028-120004.db", Second.AddMinutes(5));

        var deleted = SafetySnapshotPruner.PrunePreMigration(_dir);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(Path.Combine(_dir, "pre-migration-20261028-120004.db")));
        Assert.Equal(3, Names("pre-migration-*.db").Length);
    }
}
