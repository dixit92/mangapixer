namespace com.lifepixer.mangapixer.Server.Features.Covers;

using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
/// The cover layer's version (1.47.0 lane S, owner decision 12): a counter per database that moves after every committed write to
/// anything a card cover is resolved from, so an in-memory cover cache (<see cref="Home.HomeCoverCache"/>) can tell a kept cover from a
/// stale one without reading the database. What <see cref="CoverResolutionService"/> reads, and therefore what moves the version:
/// <list type="bullet">
/// <item>any write to <c>node_cover_choices</c> (admin choices and pins), <c>node_auto_covers</c> (automatic decisions),
/// <c>volume_covers</c> (stored web covers), <c>folder_cover_preferences</c> and <c>node_series_links</c> (the poster's record);</item>
/// <item>a row added to or removed from <c>catalog_nodes</c>, <c>archive_items</c>, <c>metadata_records</c>, <c>libraries</c> or
/// <c>app_settings</c>, and an update of the columns the layer reads there: a node's parent, kind, sort key, availability, public id or
/// library; an archive's content version; a record's poster state / version; a library's "Show saved web covers"; the crop and
/// web-cover switches. Every other update of those busy tables (scan bookkeeping, thumbnails, the request budget, metadata refresh
/// text) leaves the version alone.</item>
/// </list>
/// Every foreign-key cascade into the cover tables starts at one of these tables, so cascaded rows are covered by the statement that
/// caused them. Writes are seen as EF Core commands (tracked saves, <c>ExecuteUpdate</c> / <c>ExecuteDelete</c>, raw SQL through EF);
/// no code writes these tables through a bare ADO command.
/// <para>
/// Ordering: the version moves BEFORE such a statement runs and again AFTER its write is committed (the statement itself without a
/// transaction, the save, the transaction commit, or at the latest the connection's close). A reader that takes the version before it
/// reads cover rows and keeps its answer under that version therefore never keeps a pre-commit answer under a version that outlives the
/// commit.
/// </para>
/// </summary>
public static partial class CoverLayerVersion
{
    // One counter per database file (the connection's data source): the app has one; a test process has many, which must not move
    // each other's.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, StrongBox<long>> Versions = new(StringComparer.Ordinal);

    /// <summary>The current version of <paramref name="db"/>'s database. Read it BEFORE reading the rows an answer is computed from.</summary>
    public static long Of(DbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return Volatile.Read(ref Counter(db.Database.GetDbConnection()).Value);
    }

    /// <summary>The interceptors that move the version (added to every context in <c>MangaPixerDbContext.OnConfiguring</c>).</summary>
    public static IInterceptor[] Interceptors { get; } =
        [new CommandWatch(), new SaveWatch(), new TransactionWatch(), new ConnectionWatch()];

    private static StrongBox<long> Counter(DbConnection connection) =>
        Versions.GetOrAdd(connection.DataSource ?? string.Empty, static _ => new StrongBox<long>());

    internal static void Bump(DbConnection connection) => Interlocked.Increment(ref Counter(connection).Value);

    /// <summary>Tables whose every write moves the version (null), or the quoted columns whose UPDATE does.</summary>
    private static readonly Dictionary<string, string[]?> Watched = new(StringComparer.OrdinalIgnoreCase)
    {
        ["node_cover_choices"] = null,
        ["node_auto_covers"] = null,
        ["volume_covers"] = null,
        ["folder_cover_preferences"] = null,
        ["node_series_links"] = null,
        ["catalog_nodes"] = ["\"ParentId\"", "\"Kind\"", "\"SortKey\"", "\"Availability\"", "\"PublicId\"", "\"LibraryId\""],
        ["archive_items"] = ["\"ContentVersion\""],
        ["metadata_records"] = ["\"ImageState\"", "\"ImageVersion\""],
        ["libraries"] = ["\"WebCoversHidden\""],
        ["app_settings"] = ["\"CoverSpreadCropEnabled\"", "\"MetadataVolumeCoversEnabled\""],
    };

    [GeneratedRegex("""\b(?<verb>INSERT|REPLACE|UPDATE|DELETE)\b(?:\s+OR\s+\w+)?(?:\s+INTO|\s+FROM)?\s+["`\[]?(?<table>\w+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WriteTarget();

    /// <summary>Whether a command's text writes something the cover layer reads (see the class remarks).</summary>
    public static bool WritesCoverInputs(string? sql)
    {
        if (string.IsNullOrEmpty(sql))
            return false;
        var head = sql.AsSpan().TrimStart();
        if (head.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || head.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (Match m in WriteTarget().Matches(sql))
        {
            if (!Watched.TryGetValue(m.Groups["table"].Value, out var columns))
                continue;
            if (columns is null || !m.Groups["verb"].Value.Equals("UPDATE", StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (var column in columns)
            {
                if (sql.Contains(column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    // Connections with a cover write whose commit has not been followed by a bump yet.
    private static readonly ConditionalWeakTable<DbConnection, object> Pending = new();
    private static readonly object Mark = new();

    private static void Before(DbCommand command)
    {
        if (command.Connection is not { } connection || !WritesCoverInputs(command.CommandText))
            return;
        Bump(connection);
        Pending.AddOrUpdate(connection, Mark);
    }

    private static void After(DbCommand command)
    {
        // Without a transaction a non-query statement is committed when it returns.
        if (command.Transaction is null && command.Connection is { } connection && WritesCoverInputs(command.CommandText))
            Bump(connection);
    }

    private static void Settle(DbConnection? connection)
    {
        if (connection is not null && Pending.Remove(connection))
            Bump(connection);
    }

    private sealed class CommandWatch : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Before(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Before(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Before(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Before(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Before(command);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Before(command);
            return ValueTask.FromResult(result);
        }

        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            After(command);
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            After(command);
            return ValueTask.FromResult(result);
        }

        public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
        {
            After(command);
            return result;
        }

        public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result,
            CancellationToken cancellationToken = default)
        {
            After(command);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>A save's statements (often <c>... RETURNING</c> readers) are committed when the save returns outside a transaction.</summary>
    private sealed class SaveWatch : SaveChangesInterceptor
    {
        private static void Saved(DbContext? context)
        {
            if (context is not null && context.Database.CurrentTransaction is null)
                Settle(context.Database.GetDbConnection());
        }

        public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        {
            Saved(eventData.Context);
            return result;
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Saved(eventData.Context);
            return ValueTask.FromResult(result);
        }

        public override void SaveChangesFailed(DbContextErrorEventData eventData) => Saved(eventData.Context);

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Saved(eventData.Context);
            return Task.CompletedTask;
        }
    }

    /// <summary>A SQLite transaction forgets its connection once it completes, so the context names it.</summary>
    private sealed class TransactionWatch : DbTransactionInterceptor
    {
        private static void Ended(DbTransaction transaction, TransactionEndEventData eventData) =>
            Settle(eventData.Context?.Database.GetDbConnection() ?? transaction.Connection);

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Ended(transaction, eventData);

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Ended(transaction, eventData);
            return Task.CompletedTask;
        }

        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Ended(transaction, eventData);

        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Ended(transaction, eventData);
            return Task.CompletedTask;
        }
    }

    /// <summary>The last resort: whatever was pending on a connection is committed or rolled back by the time it closes.</summary>
    private sealed class ConnectionWatch : DbConnectionInterceptor
    {
        public override void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData) => Settle(connection);

        public override Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
        {
            Settle(connection);
            return Task.CompletedTask;
        }

        public override InterceptionResult ConnectionDisposing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            Settle(connection);
            return result;
        }

        public override ValueTask<InterceptionResult> ConnectionDisposingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            Settle(connection);
            return ValueTask.FromResult(result);
        }
    }
}
