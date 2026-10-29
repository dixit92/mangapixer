namespace com.lifepixer.mangapixer.Server.Persistence;

using com.lifepixer.mangapixer.Core.Catalog;
using Microsoft.Data.Sqlite;
using System.Data.Common;

/// <summary>
/// The <c>mp_fold(text)</c> SQLite function (1.50.0, lane S): <see cref="SearchText.Fold"/> - lower case, accents removed, every run of
/// punctuation one space - so SQL can compare text the way the plain search does (the tag view's free text: <c>pokemon</c> finds
/// <c>Pokémon</c>). Registered beside <c>mp_sort_key</c> by <see cref="SortKeySqlFunctionInterceptor.Register"/> on every connection EF
/// Core opens; code that opens the context's connection itself calls <see cref="Register"/> first (Microsoft.Data.Sqlite keeps a
/// function on the connection object across later opens).
/// </summary>
public static class FoldSqlFunction
{
    /// <summary>Name of the function as callable from SQL.</summary>
    public const string FunctionName = "mp_fold";

    /// <summary>
    /// Attaches <c>mp_fold</c> to a SQLite connection, open or not. Deterministic (a pure function of its text). Re-registering replaces
    /// the binding, so this is safe to call more than once.
    /// </summary>
    public static void Register(DbConnection connection)
    {
        if (connection is not SqliteConnection sqlite)
            return;
        sqlite.CreateFunction(FunctionName, (string? text) => SearchText.Fold(text), isDeterministic: true);
    }
}
