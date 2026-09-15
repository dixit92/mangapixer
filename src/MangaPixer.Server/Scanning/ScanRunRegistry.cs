namespace com.lifepixer.mangapixer.Server.Scanning;

using System.Collections.Concurrent;

/// <summary>
/// Tracks active scan runs and their cancellation tokens so that
/// <c>CancelScan</c> can cooperatively cancel a running scan (audit defect D34).
///
/// Previously, <c>CancelScan</c> set <c>ScanRun.Status = 4</c> but the
/// background scan task never checked the status — it called
/// <c>ScanAsync()</c> with no <c>CancellationToken</c>. This registry
/// provides the missing wiring: <c>TriggerScan</c> registers a CTS,
/// passes its token to <c>ScanAsync(ct)</c>, and <c>CancelScan</c>
/// cancels the token.
/// </summary>
public sealed class ScanRunRegistry
{
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _runs = new();

    /// <summary>
    /// Registers a cancellation token source for a scan run.
    /// Returns the token to pass to <c>ScanAsync</c>.
    /// </summary>
    public CancellationToken Register(long scanRunId)
    {
        var cts = new CancellationTokenSource();
        _runs[scanRunId] = cts;
        return cts.Token;
    }

    /// <summary>
    /// Cancels a running scan. Returns true if the scan was found and cancelled.
    /// </summary>
    public bool Cancel(long scanRunId)
    {
        if (_runs.TryGetValue(scanRunId, out var cts))
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Removes a completed scan from the registry and disposes its CTS.
    /// Called after the scan finishes (success, failure, or cancellation).
    /// </summary>
    public void Complete(long scanRunId)
    {
        if (_runs.TryRemove(scanRunId, out var cts))
        {
            cts.Dispose();
        }
    }

    /// <summary>
    /// Returns true if a scan run is currently registered (active).
    /// </summary>
    public bool IsRunning(long scanRunId) => _runs.ContainsKey(scanRunId);
}
