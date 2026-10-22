namespace com.lifepixer.mangapixer.Server.Persistence;

using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

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
/// risk. Any further PRAGMA needs the owner. (<c>PRAGMA optimize</c> was approved too but is not run: measured on a live copy, the
/// statistics it gathers made SQLite pick whole-table scans for the home up-walks and its first run took ~6 s - lane S's note.)
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
    /// as a connection touches them, so this is a ceiling, not a reservation. Measured on a live copy (34k catalog nodes, 1.47.0 lane S):
    /// statements that walk or scan the catalog took 90-100 ms at the 2 MB default and at 4 or 8 MB (they re-read pages they had just
    /// dropped, through the array's FUSE layer) and 10-13 ms at 16 MB; 32 and 64 MB gave nothing more.
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
