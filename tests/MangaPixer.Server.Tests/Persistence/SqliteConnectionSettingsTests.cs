namespace com.lifepixer.mangapixer.Tests.Server.Persistence;

using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data.Common;
using Xunit;

/// <summary>
/// 1.47.0 lane S: every pooled connection carries the safe settings, not only the start-up connection (before 1.47.0 the PRAGMAs of
/// <see cref="DatabaseInitialization.ConfigureDatabaseAsync"/> reached that one connection only), and the list never trades durability
/// for speed (owner, 2026-10-22). File-backed SQLite with the production connection string, as <c>AuthServicesExtensions</c> builds it.
/// </summary>
public sealed class SqliteConnectionSettingsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public SqliteConnectionSettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "settings.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        var connectionString = DatabaseInitialization.BuildConnectionString(_dbPath);
        services.AddDbContext<MangaPixerDbContext>(o => o.UseSqlite(connectionString)); // the production registration's form
        return services.BuildServiceProvider();
    }

    private static long Scalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Text(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private static string Settings(DbConnection c) =>
        $"foreign_keys={Scalar(c, "PRAGMA foreign_keys")} synchronous={Scalar(c, "PRAGMA synchronous")} " +
        $"busy_timeout={Scalar(c, "PRAGMA busy_timeout")} temp_store={Scalar(c, "PRAGMA temp_store")} cache_size={Scalar(c, "PRAGMA cache_size")} " +
        $"journal_mode={Text(c, "PRAGMA journal_mode")} mmap_size={Scalar(c, "PRAGMA mmap_size")} locking_mode={Text(c, "PRAGMA locking_mode")}";

    private static readonly string Expected =
        $"foreign_keys=1 synchronous=2 busy_timeout=30000 temp_store=2 cache_size=-{SqliteConnectionSettingsInterceptor.CacheSizeKiB} " +
        "journal_mode=wal mmap_size=0 locking_mode=normal";

    [Fact]
    public async Task EveryPooledConnection_CarriesTheSafeSettings_NotOnlyTheStartUpOne()
    {
        await using var sp = Provider();
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            await db.Database.EnsureCreatedAsync();
            await DatabaseInitialization.ConfigureDatabaseAsync(db);
        }

        // Three contexts holding their connections at once: at most one can reuse the start-up connection's handle from the pool.
        var scopes = Enumerable.Range(0, 3).Select(_ => sp.CreateScope()).ToList();
        try
        {
            foreach (var scope in scopes)
            {
                var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
                await db.Database.OpenConnectionAsync();
                Assert.Equal(Expected, Settings(db.Database.GetDbConnection()));
            }
        }
        finally
        {
            foreach (var scope in scopes)
                scope.Dispose();
        }
    }

    [Fact]
    public async Task Settings_RunOncePerPhysicalConnection_AndSurviveThePool()
    {
        var connectionString = DatabaseInitialization.BuildConnectionString(_dbPath);
        await using (var first = new SqliteConnection(connectionString))
        {
            await first.OpenAsync();
            Assert.True(SqliteConnectionSettingsInterceptor.Apply(first));
            Assert.False(SqliteConnectionSettingsInterceptor.Apply(first)); // the same handle: nothing runs again
        }
        // Back from the pool (same native handle): the settings are still there and are not run again.
        await using var again = new SqliteConnection(connectionString);
        await again.OpenAsync();
        Assert.False(SqliteConnectionSettingsInterceptor.Apply(again));
        Assert.Equal(2, Scalar(again, "PRAGMA temp_store"));
        Assert.Equal(-SqliteConnectionSettingsInterceptor.CacheSizeKiB, Scalar(again, "PRAGMA cache_size"));
    }

    [Fact]
    public void Statements_AreOnlyTheApprovedSettings_NeverOnesThatTradeDurability()
    {
        // Owner, 2026-10-22: no mmap, no locking_mode, no journal_mode change, synchronous stays FULL, foreign keys ON. A further
        // PRAGMA needs the owner - extend this list only with that approval.
        var statements = SqliteConnectionSettingsInterceptor.Statements
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(
            [
                "PRAGMA foreign_keys = ON",
                "PRAGMA synchronous = FULL",
                "PRAGMA busy_timeout = 30000",
                "PRAGMA temp_store = MEMORY",
                $"PRAGMA cache_size = -{SqliteConnectionSettingsInterceptor.CacheSizeKiB}",
            ],
            statements);
        foreach (var forbidden in new[] { "mmap", "locking_mode", "journal_mode", "OFF", "NORMAL", "writable_schema", "cell_size_check = OFF" })
            Assert.DoesNotContain(forbidden, SqliteConnectionSettingsInterceptor.Statements, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("PRAGMA optimize = 0x10002;", SqliteOptimizer.Statement);
    }

    [Fact]
    public async Task StartUp_GathersPlannerStatistics_AndThePeriodicRunIsRegistered()
    {
        await using var sp = Provider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'sqlite_stat1'"));
        // The integrity of the file after it: optimize writes only SQLite's own statistics.
        Assert.Equal("ok", Text(connection, "PRAGMA quick_check"));

        // The periodic run: registered by the one Program call, and a run on a scope succeeds.
        var services = new ServiceCollection();
        services.AddSqliteSettings();
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(SqliteOptimizeHostedService));
        var hosted = new SqliteOptimizeHostedService(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<SqliteOptimizeHostedService>.Instance,
            TimeProvider.System);
        await hosted.RunOnceAsync(CancellationToken.None);
    }
}
