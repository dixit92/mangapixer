namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets;

using System.Data.Common;
using System.Globalization;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Raw SQL for the tag query (1.45.0, lane Q) on the context's own connection. Id lists are rendered as integer literals (never user
/// text); text always goes in as a parameter.
/// </summary>
internal static class QuerySql
{
    /// <summary>How many ids one <c>IN (...)</c> list carries before a query is split.</summary>
    public const int Chunk = 500;

    /// <summary>The tombstoned availability (<c>catalog_nodes.Availability</c>): a tombstoned location shows nothing.</summary>
    public const int Tombstoned = 5;

    public static string List(IEnumerable<long> ids) => string.Join(",", ids.Select(id => id.ToString(CultureInfo.InvariantCulture)));

    public static async Task<List<T>> ReadAsync<T>(
        MangaPixerDbContext db, string sql, IReadOnlyDictionary<string, object?>? parameters, Func<DbDataReader, T> map, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
            await connection.OpenAsync(ct);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (parameters is not null)
            {
                foreach (var (name, value) in parameters)
                    command.Parameters.Add(new SqliteParameter(name, value ?? DBNull.Value));
            }
            var rows = new List<T>();
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(map(reader));
            return rows;
        }
        finally
        {
            if (!wasOpen)
                await connection.CloseAsync();
        }
    }
}
