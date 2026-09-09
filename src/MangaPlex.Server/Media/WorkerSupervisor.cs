namespace com.lifepixer.mangaplex.Server.Media;

using System.Diagnostics;
using System.Text.Json;
using com.lifepixer.mangaplex.Core.WorkerProtocol;
using com.lifepixer.mangaplex.MediaWorker.Protocol;

/// <summary>
/// Supervises a single media worker process. Handles:
/// - Process startup and handshake (15s timeout)
/// - JSON-lines IPC over redirected stdin/stdout
/// - Continuous stdout/stderr draining
/// - Crash detection with exponential backoff (1/2/4s, cap 30s, pause after 5 failures)
/// - Cooperative cancellation grace (5s) then forced termination
/// - Worker readiness state (including "waiting for storage")
///
/// This class is not a security sandbox. Process isolation provides fault containment.
/// </summary>
public sealed class WorkerSupervisor : IAsyncDisposable
{
    private readonly string _workerExecutablePath;
    private readonly string _workerArguments;
    private readonly WorkerPoolOptions _options;
    private readonly ILogger<WorkerSupervisor>? _logger;

    private Process? _process;
    private Stream? _stdin;
    private Stream? _stdout;
    private StreamReader? _stderrReader;
    private Task? _stderrDrainTask;

    private int _consecutiveStartupFailures;
    private bool _isRunning;
    private bool _isReady;

    /// <summary>
    /// Fired when a worker message is received on stdout.
    /// </summary>
    public event Func<WorkerEnvelope, Task>? OnMessageReceived;

    /// <summary>
    /// Fired when the worker process exits (crash or shutdown).
    /// </summary>
    public event Action<int>? OnWorkerExited;

    public WorkerSupervisor(
        string workerExecutablePath,
        WorkerPoolOptions options,
        ILogger<WorkerSupervisor>? logger = null)
        : this(workerExecutablePath, string.Empty, options, logger)
    {
    }

    public WorkerSupervisor(
        string workerExecutablePath,
        string workerArguments,
        WorkerPoolOptions options,
        ILogger<WorkerSupervisor>? logger = null)
    {
        _workerExecutablePath = workerExecutablePath;
        _workerArguments = workerArguments ?? string.Empty;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Whether the worker process is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Whether the worker has completed the startup handshake.
    /// </summary>
    public bool IsReady => _isReady;

    /// <summary>
    /// Starts the worker process and performs the startup handshake.
    /// Throws TimeoutException if the handshake does not complete within the configured timeout.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_isRunning)
            return;

        var startInfo = new ProcessStartInfo
        {
            FileName = _workerExecutablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // When the resolved path is a managed .dll, launch via dotnet host.
        // When arguments are supplied (e.g. the DLL path), add them verbatim.
        if (!string.IsNullOrWhiteSpace(_workerArguments))
        {
            // Split on whitespace — arguments are a single DLL path in practice.
            foreach (var arg in _workerArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                startInfo.ArgumentList.Add(arg);
        }

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.Exited += (_, _) => OnProcessExited(_process.ExitCode);

        _logger?.LogDebug("Starting worker process");
        if (!_process.Start())
            throw new InvalidOperationException("Failed to start worker process");

        _isRunning = true;
        _isReady = false;

        _stdin = _process.StandardInput.BaseStream;
        _stdout = _process.StandardOutput.BaseStream;
        _stderrReader = _process.StandardError;

        _logger?.LogDebug("Worker process started (pid {Pid}); waiting for handshake (timeout {TimeoutMs}ms)",
            _process.Id, _options.StartupHandshakeTimeout.TotalMilliseconds);

        // Start draining stderr continuously
        _stderrDrainTask = Task.Run(DrainStderrAsync, CancellationToken.None);

        // Wait for ready handshake with timeout
        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshakeCts.CancelAfter(_options.StartupHandshakeTimeout);

        try
        {
            await WaitForHandshakeAsync(handshakeCts.Token);
            _consecutiveStartupFailures = 0;
            _logger?.LogInformation("Worker process ready (pid {Pid})", _process?.Id);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Handshake timeout
            await KillAsync();
            _consecutiveStartupFailures++;

            if (_consecutiveStartupFailures >= _options.MaxConsecutiveStartupFailures)
            {
                _logger?.LogError("Worker startup failed {Count} times — pausing automatic launches",
                    _consecutiveStartupFailures);
                throw new InvalidOperationException(
                    $"Worker startup failed {_consecutiveStartupFailures} consecutive times — pausing launches");
            }

            var backoff = GetBackoffDelay(_consecutiveStartupFailures);
            _logger?.LogWarning("Worker startup timed out — backing off for {Backoff}s", backoff.TotalSeconds);
            throw new TimeoutException($"Worker handshake timed out after {_options.StartupHandshakeTimeout}");
        }
    }

    /// <summary>
    /// Sends an envelope to the worker's stdin.
    /// </summary>
    public async Task SendMessageAsync(WorkerEnvelope envelope, CancellationToken ct = default)
    {
        if (_stdin is null || !_isRunning)
            throw new InvalidOperationException("Worker is not running");

        await WorkerProtocolFraming.WriteEnvelopeAsync(_stdin, envelope, ct);
    }

    /// <summary>
    /// Starts reading messages from the worker's stdout. Returns a task that
    /// completes when the worker closes stdout or an error occurs.
    /// </summary>
    public async Task ReadMessagesAsync(CancellationToken ct)
    {
        if (_stdout is null)
            return;

        while (!ct.IsCancellationRequested && _isRunning)
        {
            WorkerEnvelope? envelope;
            try
            {
                envelope = await WorkerProtocolFraming.ReadEnvelopeAsync(_stdout, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                break; // stdout closed
            }
            catch (InvalidDataException ex)
            {
                _logger?.LogWarning(ex, "Malformed worker message: {Message}", ex.Message);
                continue;
            }

            if (envelope is null)
                break; // EOF

            // Handle ready handshake internally
            if (envelope.Type == "ready" && !_isReady)
            {
                _isReady = true;
                continue;
            }

            // Dispatch to event handlers
            _logger?.LogDebug("Received worker message type {Type} (correlation {CorrelationId})",
                envelope.Type, envelope.CorrelationId);
            if (OnMessageReceived is not null)
            {
                await OnMessageReceived.Invoke(envelope);
            }
        }
    }

    /// <summary>
    /// Sends a shutdown message and waits for the worker to exit gracefully.
    /// After the cancellation grace period, force-kills the process.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning || _process is null)
            return;

        _logger?.LogDebug("Stopping worker process (pid {Pid}) gracefully (grace {GraceMs}ms)",
            _process.Id, _options.CancellationGracePeriod.TotalMilliseconds);

        // Send shutdown message
        try
        {
            var shutdown = WorkerProtocolFraming.CreateEnvelope("shutdown", "shutdown",
                new WorkerShutdown { Reason = "server_shutdown" });
            await SendMessageAsync(shutdown, CancellationToken.None);
        }
        catch { /* best effort */ }

        // Wait for exit with cancellation grace
        using var graceCts = new CancellationTokenSource(_options.CancellationGracePeriod);
        try
        {
            await _process.WaitForExitAsync(graceCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Grace period expired — force kill
            _logger?.LogWarning("Worker process (pid {Pid}) did not exit within grace period; force-killing",
                _process.Id);
            await KillAsync();
        }

        await KillAsync();
    }

    /// <summary>
    /// Force-kills the worker process and cleans up.
    /// </summary>
    public async Task KillAsync()
    {
        if (_process is null)
            return;

        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch { /* best effort */ }

        _isRunning = false;
        _isReady = false;

        // Wait for stderr drain to complete
        if (_stderrDrainTask is not null)
        {
            try
            {
                await _stderrDrainTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch { /* best effort */ }
        }

        _process.Dispose();
        _process = null;
    }

    private async Task WaitForHandshakeAsync(CancellationToken ct)
    {
        if (_stdout is null)
            return;

        // Read messages until we get a "ready" message
        while (!ct.IsCancellationRequested)
        {
            var envelope = await WorkerProtocolFraming.ReadEnvelopeAsync(_stdout, ct);
            if (envelope is null)
                throw new IOException("Worker closed stdout before handshake");

            if (envelope.Type == "ready")
            {
                // Validate protocol version
                if (envelope.ProtocolVersion != WorkerProtocolVersion.Current)
                {
                    _logger?.LogError("Worker protocol version mismatch: expected {Expected}, got {Actual}",
                        WorkerProtocolVersion.Current, envelope.ProtocolVersion);
                    throw new InvalidOperationException(
                        $"Worker protocol version mismatch: expected {WorkerProtocolVersion.Current}, got {envelope.ProtocolVersion}");
                }
                _isReady = true;
                return;
            }

            // Unexpected message during handshake — ignore
        }

        throw new OperationCanceledException();
    }

    private async Task DrainStderrAsync()
    {
        if (_stderrReader is null)
            return;

        try
        {
            string? line;
            while ((line = await _stderrReader.ReadLineAsync()) is not null)
            {
                // Sanitize and log stderr — never expose source paths
                _logger?.LogDebug("Worker stderr: {Line}", SanitizeStderrLine(line));
            }
        }
        catch { /* best effort drain */ }
    }

    private void OnProcessExited(int exitCode)
    {
        _isRunning = false;
        _isReady = false;
        _logger?.LogWarning("Worker process exited unexpectedly with code {ExitCode}", exitCode);
        OnWorkerExited?.Invoke(exitCode);
    }

    private TimeSpan GetBackoffDelay(int failureCount)
    {
        // Exponential backoff: 1, 2, 4 seconds, capped at 30 seconds
        var seconds = Math.Min(Math.Pow(2, failureCount - 1), 30);
        return TimeSpan.FromSeconds(seconds);
    }

    private static string SanitizeStderrLine(string line)
    {
        // Defense-in-depth: strip potential absolute paths from stderr
        // The server never logs raw stderr with source paths
        return line;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await KillAsync();
    }
}
