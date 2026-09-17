namespace com.lifepixer.mangapixer.Server.Persistence;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Ordering;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

/// <summary>
/// Registers the <c>mp_sort_key(Kind, DisplayName)</c> SQLite user-defined function on
/// every connection the application opens.
///
/// Why a UDF: the persisted <see cref="SortKey"/> format needs a per-character scan of
/// the display name, which plain SQLite SQL cannot express. The alternative - hand-writing
/// the encoder a second time as a recursive CTE inside a migration - would create exactly
/// the drift this feature exists to remove (an encoder that agrees with production only
/// until one of the two copies changes). With the UDF the backfill migration is a single
/// set-based UPDATE that calls the SAME C# encoder the scanner calls.
///
/// Registration goes through a connection interceptor rather than any individual call site
/// because the DbContext is constructed in ~20 places (DI, backup, worker, tests, the
/// design-time factory). The interceptor attaches once, in
/// <see cref="MangaPixerDbContext.OnConfiguring"/>, and covers all of them.
/// </summary>
public sealed class SortKeySqlFunctionInterceptor : DbConnectionInterceptor
{
    /// <summary>
    /// Shared instance. A singleton keeps the EF Core options (and therefore its internal
    /// service-provider cache key) stable across contexts built from the same options.
    /// </summary>
    public static readonly SortKeySqlFunctionInterceptor Instance = new();

    /// <summary>Name of the function as callable from SQL.</summary>
    public const string FunctionName = "mp_sort_key";

    private SortKeySqlFunctionInterceptor()
    {
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Register(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Register(connection);
        return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    /// <summary>
    /// Attaches <c>mp_sort_key</c> to a SQLite connection. Deterministic (the encoder is a
    /// pure function of kind + name), so SQLite may use it in indexed contexts. Re-registering
    /// on an already-registered connection simply replaces the binding, so this is safe to
    /// call on every open.
    /// </summary>
    public static void Register(DbConnection connection)
    {
        if (connection is not SqliteConnection sqlite)
            return;

        sqlite.CreateFunction(
            FunctionName,
            (long kind, string? displayName) =>
                SortKey.ForNode((CatalogNodeKind)kind, displayName ?? string.Empty),
            isDeterministic: true);
    }
}
