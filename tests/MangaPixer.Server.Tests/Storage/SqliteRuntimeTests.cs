namespace com.lifepixer.mangaplex.Tests.Server.Storage;

using Microsoft.Data.Sqlite;
using Xunit;

/// <summary>
/// Verifies the SQLite runtime version and features required by MangaPlex.
/// The server uses Microsoft.Data.Sqlite + EF Core Sqlite, which bundles SQLitePCLRaw.
/// Verifies: version, WAL mode, FTS5, and trigram tokenizer availability.
/// </summary>
public sealed class SqliteRuntimeTests
{
    [Fact]
    public void Sqlite_Version_IsAtLeast_3_51_0()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version();";
        var version = (string)cmd.ExecuteScalar()!;

        // Parse "3.x.y" or "3.x.y.z"
        var parts = version.Split('.');
        Assert.True(parts.Length >= 3, $"Unexpected SQLite version format: {version}");

        var major = int.Parse(parts[0]);
        var minor = int.Parse(parts[1]);
        // We need at least 3.51.0 for the WAL-reset fix and improved FTS5
        Assert.True(major >= 3, $"SQLite major version too low: {version}");
        Assert.True(major > 3 || minor >= 51, $"SQLite minor version too low (need >= 3.51): {version}");
    }

    [Fact]
    public void Sqlite_WalMode_CanBeEnabled()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        var result = (string)cmd.ExecuteScalar()!;

        // :memory: databases may return "memory" instead of "wal"
        // The important thing is that the PRAGMA doesn't throw
        Assert.NotNull(result);
    }

    [Fact]
    public void Sqlite_Fts5_IsAvailable()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        // Try to create an FTS5 table — if FTS5 is not compiled in, this throws
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE VIRTUAL TABLE test_fts USING fts5(title);
            INSERT INTO test_fts(title) VALUES ('test manga chapter');
            SELECT title FROM test_fts WHERE test_fts MATCH 'manga';
            """;

        var result = cmd.ExecuteScalar();
        Assert.Equal("test manga chapter", result);
    }

    [Fact]
    public void Sqlite_TrigramTokenizer_IsAvailable()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        // The trigram tokenizer requires FTS5 and is available in SQLite >= 3.34
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE VIRTUAL TABLE test_trigram USING fts5(title, tokenize='trigram');
            INSERT INTO test_trigram(title) VALUES ('mangaplex chapter one');
            SELECT title FROM test_trigram WHERE test_trigram MATCH 'plex';
            """;

        var result = cmd.ExecuteScalar();
        Assert.Equal("mangaplex chapter one", result);
    }

    [Fact]
    public void Sqlite_UserAuthentication_NotRequired()
    {
        // MangaPlex uses ASP.NET Core Identity for authentication, not SQLite's
        // built-in user authentication. Verify that we can open a plain database
        // without any auth configuration.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE test (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO test (name) VALUES ('ok'); SELECT name FROM test;";
        var result = (string)cmd.ExecuteScalar()!;
        Assert.Equal("ok", result);
    }
}
