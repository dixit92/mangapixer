namespace com.lifepixer.mangapixer.Server.Logging;

/// <summary>
/// Marks the async flow of a save whose unique-constraint failure is an EXPECTED, recovered first-write race
/// (two requests both found no row and both inserted). <see cref="RecoveredRaceNoiseFilter"/> drops EF Core's own
/// error lines for a SQLite constraint failure only while a scope is open, so a unique violation anywhere else
/// still logs.
/// </summary>
/// <remarks>
/// EF Core logs the failed command (<c>CommandError</c>) and the failed save (<c>SaveChangesFailed</c>) at Error
/// from inside <c>SaveChangesAsync</c>, before the caller's <c>catch</c> can recover. Serilog filters run
/// synchronously on the logging thread, which is inside the awaited save, so an <see cref="AsyncLocal{T}"/>
/// opened around that save is visible to the filter. Matching on the failing table's name instead (the filter's
/// original rule) misses every race whose message names another table of the same save (the sticky
/// <c>read_marks</c> row a completion adds) and needs a new rule per table.
/// </remarks>
internal static class ExpectedRaceScope
{
    private static readonly AsyncLocal<int> s_depth = new();

    /// <summary>True while the current async flow is inside <see cref="Begin"/>.</summary>
    public static bool IsActive => s_depth.Value > 0;

    /// <summary>Opens a scope around a save that recovers from a unique-constraint race. Dispose it right after the save.</summary>
    public static IDisposable Begin()
    {
        s_depth.Value++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            s_depth.Value--;
        }
    }
}
