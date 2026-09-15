namespace com.lifepixer.mangapixer.Server.Features.Import.YacReader;

using Microsoft.Data.Sqlite;

/// <summary>
/// Reads YACReader reading-progress data from a <c>library.ydb</c> SQLite
/// database in a strictly read-only fashion. The database is never opened for
/// write: the caller either passes a path that is opened with
/// <see cref="SqliteOpenMode.ReadOnly"/>, or (by default) a snapshot copied
/// into an app-owned scratch directory so the source library directory is
/// never touched for write (no journal/wal/shm sidecar creation).
///
/// The schema mirrored here is the verified YACReader <c>library.ydb</c>
/// schema (see project feature backlog). Only the columns needed for progress
/// import are projected; metadata such as covers, ratings, and tags are
/// intentionally ignored (MangaPixer regenerates covers and does not import
/// YACReader metadata in release one).
/// </summary>
public sealed class YacReaderLibraryReader
{
    /// <summary>
    /// Opens the YACReader database read-only and returns its schema version
    /// (<c>db_info.version</c>), or null if the table is absent/empty.
    /// </summary>
    public string? ReadVersion(string dbPath, CancellationToken ct = default)
    {
        using var conn = OpenReadOnly(dbPath);
        return TryReadVersion(conn);
    }

    /// <summary>
    /// Reads every comic's progress-relevant fields from the database. Each
    /// row carries the raw YACReader path/filename (used only for in-memory
    /// mapping, never persisted or echoed) and the reading state. Returns an
    /// empty list if the expected tables are absent (e.g. an unsupported or
    /// corrupt database).
    /// </summary>
    public IReadOnlyList<YacReaderComicRecord> ReadComics(string dbPath, CancellationToken ct = default)
    {
        using var conn = OpenReadOnly(dbPath);

        if (!TableExists(conn, "comic") || !TableExists(conn, "comic_info"))
            return [];

        var records = new List<YacReaderComicRecord>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.id, c.fileName, c.path,
                   ci.currentPage, ci.read, ci.hasBeenOpened, ci.lastTimeOpened
            FROM comic c
            JOIN comic_info ci ON c.comicInfoId = ci.id
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            records.Add(new YacReaderComicRecord
            {
                ComicId = reader.GetInt64(0),
                FileName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Path = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                CurrentPage = reader.IsDBNull(3) ? 1 : reader.GetInt32(3),
                Read = !reader.IsDBNull(4) && reader.GetInt64(4) != 0,
                HasBeenOpened = !reader.IsDBNull(5) && reader.GetInt64(5) != 0,
                LastTimeOpened = reader.IsDBNull(6) ? null : reader.GetInt64(6),
            });
        }

        return records;
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        var conn = new SqliteConnection(builder.ToString());
        conn.Open();
        return conn;
    }

    private static bool TableExists(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name";
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteScalar() is not null;
    }

    private static string? TryReadVersion(SqliteConnection conn)
    {
        if (!TableExists(conn, "db_info"))
            return null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM db_info LIMIT 1";
        var result = cmd.ExecuteScalar();
        return result is null || result == DBNull.Value ? null : Convert.ToString(result);
    }
}

/// <summary>
/// A single comic's progress-relevant fields read from a YACReader database.
/// <see cref="Path"/>/<see cref="FileName"/> are raw YACReader values used only
/// for in-memory path mapping; they are never persisted or echoed in API
/// responses (source-path privacy invariant).
/// </summary>
public sealed record YacReaderComicRecord
{
    public long ComicId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;

    /// <summary>YACReader's 1-based current page (DEFAULT 1 in the schema).</summary>
    public int CurrentPage { get; init; }

    public bool Read { get; init; }
    public bool HasBeenOpened { get; init; }
    public long? LastTimeOpened { get; init; }
}
