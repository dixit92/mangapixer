namespace com.lifepixer.mangapixer.Server.Features.Export;

using com.lifepixer.mangapixer.Server.Features.Tokens;
using com.lifepixer.mangapixer.Server.Scanning;

/// <summary>The outcome of a token's scan request at the cooldown gate.</summary>
public enum TokenScanStart
{
    /// <summary>The scan was started (and the library's cooldown begins now).</summary>
    Started,

    /// <summary>A scan of that library is already running (409; does not count toward the cooldown).</summary>
    InProgress,

    /// <summary>A token-requested scan of that library started less than the cooldown ago (429).</summary>
    CoolingDown,
}

/// <summary>
/// The per-library cooldown of token-requested scans (1.36.0, owner: 1 per library per <see cref="ApiTokenOptions.ScanCooldownMinutes"/>,
/// default 5). Only scans a TOKEN started count; a scan an admin or the schedule started does not, and a refused request (409 while a
/// scan runs) does not either. In memory: a restart forgets it, which at worst allows one extra full scan per library.
/// </summary>
/// <remarks>
/// The check, the start and the record run under one lock, so two concurrent requests for the same library never both start a
/// scan inside the window (the second waits, then sees the first's start). <see cref="LibraryScanLauncher.StartAsync"/> returns as
/// soon as the lease row is written, so the lock is held for one short database write. The clock is the injected
/// <see cref="TimeProvider"/>.
/// </remarks>
public sealed class TokenScanCooldown(ApiTokenOptions options, TimeProvider clock)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<long, DateTimeOffset> _lastStart = [];

    /// <summary>The configured window; zero = no cooldown.</summary>
    public TimeSpan Window => TimeSpan.FromMinutes(Math.Max(0, options.ScanCooldownMinutes));

    /// <summary>
    /// Starts a scan of <paramref name="libraryId"/> through <paramref name="start"/> unless the library is cooling down. Returns the
    /// outcome, the launch when started, and the wait when cooling down.
    /// </summary>
    public async Task<(TokenScanStart Outcome, LibraryScanLaunch? Launch, TimeSpan RetryAfter)> TryStartAsync(
        long libraryId, Func<CancellationToken, Task<LibraryScanLaunch?>> start, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            var window = Window;
            // Forget the libraries whose window is over (keeps the map at the libraries scanned in the last few minutes).
            foreach (var stale in _lastStart.Where(e => now - e.Value >= window).Select(e => e.Key).ToList())
                _lastStart.Remove(stale);

            if (_lastStart.TryGetValue(libraryId, out var last))
                return (TokenScanStart.CoolingDown, null, last + window - now);

            var launch = await start(ct);
            if (launch is null)
                return (TokenScanStart.InProgress, null, TimeSpan.Zero);

            if (window > TimeSpan.Zero)
                _lastStart[libraryId] = now;
            return (TokenScanStart.Started, launch, TimeSpan.Zero);
        }
        finally
        {
            _gate.Release();
        }
    }
}
