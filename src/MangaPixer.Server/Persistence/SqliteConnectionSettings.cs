namespace com.lifepixer.mangapixer.Server.Persistence;

using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using com.lifepixer.mangapixer.Server.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// The per-connection SQLite settings (1.47.0 lane S), applied once to every physical connection the application opens - the
/// pooled ones included. Before 1.47.0 <see cref="DatabaseInitialization.ConfigureDatabaseAsync"/> ran its PRAGMAs on the start-up
/// connection only, so every other pooled connection ran with SQLite's defaults (<c>temp_store</c> FILE, no busy timeout, a 2 MB
/// page cache).
/// <para>
/// Data integrity comes first (owner, 2026-10-22: "do not use known db corruption risks for unraid/lustrefs and other known mmap
/// risks to optimize performance"). The list below is exactly the approved one, and <c>Contracts</c>-style tests pin it:
/// </para>
/// <list type="bullet">
/// <item>re-applied safe settings of the start-up connection: <c>foreign_keys = ON</c>, <c>synchronous = FULL</c>,
/// <c>busy_timeout = 30000</c>;</item>
/// <item><c>temp_store = MEMORY</c> - sorts and materialised CTEs stay in memory (the container's <c>/tmp</c> is a tmpfs anyway);</item>
/// <item><c>cache_size</c> = <see cref="CacheSizeKiB"/> KiB of page cache per connection (see its remarks).</item>
/// </list>
/// Never <c>mmap_size</c>, <c>locking_mode</c>, a <c>journal_mode</c> change (WAL is a property of the database file, set at start-up)
/// or a weaker <c>synchronous</c>: appdata lives on the Unraid array (shfs / FUSE), where memory-mapped I/O is a known corruption
/// risk. Any further PRAGMA needs the owner.
/// <para>
/// Once per physical connection: a pooled connection keeps its native handle (and these settings) between opens, so the statements
/// run only the first time a handle is seen (a weak table keyed on the handle) - EF Core opens and closes the connection around
/// almost every query.
/// </para>
/// </summary>
public sealed class SqliteConnectionSettingsInterceptor : DbConnectionInterceptor
{
    /// <summary>Shared instance (a singleton keeps the EF Core options' service-provider cache key stable, as the sort-key interceptor).</summary>
    public static readonly SqliteConnectionSettingsInterceptor Instance = new();

    /// <summary>
    /// Page cache per connection, in KiB (<c>PRAGMA cache_size</c> takes a negative number for KiB). SQLite allocates cache pages only
    /// as a connection touches them, so this is a ceiling, not a reservation. 16 MiB holds the catalog's tree index and the rows a
    /// library-wide recursive walk touches (the largest live library: ~11k nodes) where the 2 MB default made such a walk re-read pages
    /// within one statement; measured in lane S's note (1.47.0).
    /// </summary>
    public const int CacheSizeKiB = 16384;

    /// <summary>The statements, in order. Only approved settings (see the class remarks); the tests pin this text.</summary>
    internal static readonly string Statements =
        "PRAGMA foreign_keys = ON; " +
        "PRAGMA synchronous = FULL; " +
        "PRAGMA busy_timeout = 30000; " +
        "PRAGMA temp_store = MEMORY; " +
        $"PRAGMA cache_size = -{CacheSizeKiB};";

    private static readonly object Applied = new();
    private static readonly ConditionalWeakTable<SQLitePCL.sqlite3, object> Configured = new();

    private SqliteConnectionSettingsInterceptor()
    {
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        // Synchronous on purpose: five PRAGMAs on a local handle, once per physical connection.
        Apply(connection);
        return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    /// <summary>
    /// Applies the settings to an open SQLite connection the first time its native handle is seen; true when it ran them now.
    /// Connections that are not SQLite (none in production) are left alone.
    /// </summary>
    public static bool Apply(DbConnection connection)
    {
        if (connection is not SqliteConnection sqlite || sqlite.Handle is not { } handle || Configured.TryGetValue(handle, out _))
            return false;
        using (var command = sqlite.CreateCommand())
        {
            command.CommandText = Statements;
            command.ExecuteNonQuery();
        }
        Configured.AddOrUpdate(handle, Applied);
        return true;
    }
}

/// <summary>
/// <c>PRAGMA optimize</c> (1.47.0 lane S, owner-approved): lets SQLite gather the planner statistics (<c>sqlite_stat1</c>) for tables that
/// have none or changed a lot since they were gathered - with the bounded sample SQLite itself picks for <c>optimize</c> - so the
/// planner can tell a selective index (a folder's children) from a broad one (a whole library). Run once at start-up after the
/// migrations (<see cref="DatabaseInitialization.ConfigureDatabaseAsync"/>) and then every <see cref="SqliteOptimizeHostedService.Interval"/>.
/// It changes no row the application reads; it writes only SQLite's own statistics table.
/// </summary>
public static class SqliteOptimizer
{
    /// <summary>
    /// <c>0x10002</c>: check EVERY table (not only those the current connection has queried - a fresh pooled connection has queried none)
    /// and analyse the ones that need it. SQLite's documented form for start-up and periodic runs on long-lived databases.
    /// </summary>
    public const string Statement = "PRAGMA optimize = 0x10002;";

    /// <summary>Runs <see cref="Statement"/>; returns the elapsed milliseconds.</summary>
    public static async Task<long> RunAsync(MangaPixerDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var sw = Stopwatch.StartNew();
        await db.Database.ExecuteSqlRawAsync(Statement, ct);
        return sw.ElapsedMilliseconds;
    }
}

/// <summary>
/// Runs <see cref="SqliteOptimizer"/> periodically (1.47.0 lane S): SQLite recommends it for applications that keep connections open for a
/// long time. Cheap when nothing changed (SQLite re-analyses only tables whose size changed a lot). Logs milliseconds only.
/// </summary>
public sealed class SqliteOptimizeHostedService(IServiceScopeFactory scopes, ILogger<SqliteOptimizeHostedService> logger, TimeProvider time)
    : BackgroundService
{
    /// <summary>How often the periodic run happens (the start-up run is part of the database initialization).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await RunOnceAsync(stoppingToken);
        }
    }

    /// <summary>One run in its own scope; a failure is logged and never stops the service.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var ms = await SqliteOptimizer.RunAsync(db, ct);
            logger.LogInformation(LogEvents.Sqlite.OptimizeRan, "SQLite optimize ran: trigger {Trigger}, {Milliseconds} ms", "periodic", ms);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(LogEvents.Sqlite.OptimizeFailed, "SQLite optimize failed: {ExceptionType}", ex.GetType().Name);
        }
    }
}

/// <summary>DI registration of lane S's services (one call from <c>Program</c>).</summary>
public static class SqliteSettingsServices
{
    public static IServiceCollection AddSqliteSettings(this IServiceCollection services)
    {
        services.AddHostedService<SqliteOptimizeHostedService>();
        return services;
    }
}
