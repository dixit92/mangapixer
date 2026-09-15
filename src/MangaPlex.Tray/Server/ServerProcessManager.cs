using System.Diagnostics;
using com.lifepixer.mangaplex.Tray.Interop;
using com.lifepixer.mangaplex.Tray.Logging;

namespace com.lifepixer.mangaplex.Tray.Server;

/// <summary>
/// Launches, health-polls, and stops the <c>MangaPlex.Server.exe</c> child
/// process. Plain, UI-independent class so it is unit/process-testable
/// without WinForms.
/// </summary>
/// <summary>Requests graceful shutdown of the process with the given id.</summary>
public delegate void GracefulShutdownRequester(int processId);

/// <summary>Receives one drained line of the child process's stdout/stderr.</summary>
public delegate void ServerOutputSink(string line);

public sealed class ServerProcessManager : IAsyncDisposable
{
    private readonly string _serverExecutablePath;
    private readonly IServerHealthChecker _healthChecker;
    private readonly GracefulShutdownRequester _gracefulShutdownRequester;
    private readonly ServerOutputSink _outputSink;
    private readonly ServerOutputLog? _ownedOutputLog;
    private readonly object _lock = new();
    private Process? _process;
    private ServerEndpointOptions _endpointOptions;
    private readonly string? _serverArguments;

    public event EventHandler<ServerState>? StateChanged;

    public ServerState State { get; private set; } = ServerState.Stopped;

    public int Port => _endpointOptions.Port;

    /// <summary>Loopback URL for "Open MangaPlex" and manual browsing.</summary>
    public string BuildOpenUrl() => _endpointOptions.BuildLocalBaseUri().ToString();

    /// <param name="serverArguments">
    /// Test-only hook: MangaPlex.Server.exe itself never takes arguments (it
    /// is configured via <c>ASPNETCORE_URLS</c>), but process-level tests use
    /// this to point <see cref="StartAsync"/> at a controllable stand-in
    /// process instead.
    /// </param>
    /// <param name="gracefulShutdownRequester">
    /// Defaults to relaunching the tray's own exe as a disposable helper
    /// (<see cref="CtrlBreakSender.RequestViaHelperProcess"/>) — see that
    /// type for why the signal must not be sent in-process. Overridable so
    /// process-level tests exercise <see cref="StopAsync"/>'s timeout +
    /// Kill() fallback directly, without spawning (and waiting 2s on) a
    /// helper that has nothing meaningful to attach to.
    /// </param>
    /// <param name="outputSink">
    /// Receives each drained stdout/stderr line. Draining is not optional —
    /// see the remarks on <see cref="StartAsync"/>. Defaults to persisting
    /// via <see cref="ServerOutputLog"/>; tests inject a capturing delegate
    /// instead of touching the real log file.
    /// </param>
    public ServerProcessManager(
        string serverExecutablePath,
        ServerEndpointOptions endpointOptions,
        IServerHealthChecker? healthChecker = null,
        string? serverArguments = null,
        GracefulShutdownRequester? gracefulShutdownRequester = null,
        ServerOutputSink? outputSink = null)
    {
        _serverExecutablePath = serverExecutablePath;
        _endpointOptions = endpointOptions;
        _healthChecker = healthChecker ?? new HttpServerHealthChecker();
        _serverArguments = serverArguments;
        _gracefulShutdownRequester = gracefulShutdownRequester
            ?? (processId => CtrlBreakSender.RequestViaHelperProcess(Environment.ProcessPath, processId));

        if (outputSink is not null)
        {
            _outputSink = outputSink;
        }
        else
        {
            _ownedOutputLog = new ServerOutputLog(ServerOutputLog.DefaultFilePath);
            _outputSink = _ownedOutputLog.WriteLine;
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
                return _process is { HasExited: false };
        }
    }

    /// <summary>
    /// Applies on the next <see cref="StartAsync"/>/restart — a running
    /// server keeps its current bind until stopped, matching the tray's
    /// "LAN toggle applies on restart" contract.
    /// </summary>
    public void UpdateEndpointOptions(ServerEndpointOptions options)
    {
        lock (_lock)
            _endpointOptions = options;
    }

    /// <summary>
    /// Launches the server. Redirected stdout/stderr are drained via
    /// <c>Begin{Output,Error}ReadLine</c> into <see cref="_outputSink"/> —
    /// required, not cosmetic: Windows pipes have a small (~4KB) buffer, and
    /// the server's Serilog console sink writes continuously, so an
    /// undrained pipe eventually fills and the server's console writes block
    /// forever, hanging the whole process.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_process is { HasExited: false })
                return Task.CompletedTask;

            if (!File.Exists(_serverExecutablePath))
                throw new FileNotFoundException("MangaPlex.Server.exe was not found next to the tray executable.", _serverExecutablePath);

            var startInfo = new ProcessStartInfo(_serverExecutablePath)
            {
                Arguments = _serverArguments ?? string.Empty,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_serverExecutablePath) ?? Environment.CurrentDirectory,
            };
            startInfo.Environment["ASPNETCORE_URLS"] = _endpointOptions.BuildAspNetCoreUrls();

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += OnProcessExited;
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) _outputSink(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _outputSink(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            SetState(ServerState.Starting);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Polls <c>/health</c> until it returns 2xx or <paramref name="timeout"/>
    /// elapses. Transitions to <see cref="ServerState.Running"/> on success.
    /// </summary>
    public async Task<bool> WaitForHealthyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (!timeoutCts.IsCancellationRequested)
        {
            if (!IsRunning)
                return false;

            if (await _healthChecker.IsHealthyAsync(_endpointOptions.BuildLocalBaseUri(), timeoutCts.Token))
            {
                SetState(ServerState.Running);
                return true;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return false;
    }

    /// <summary>
    /// Requests graceful shutdown (CTRL_BREAK, which the ASP.NET Core
    /// Generic Host maps to <c>IHostApplicationLifetime.StopApplication</c>),
    /// waits up to <paramref name="gracefulTimeout"/>, then force-kills the
    /// process tree if it has not exited.
    /// </summary>
    public async Task StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
    {
        Process? process;
        lock (_lock)
            process = _process;

        if (process is null || process.HasExited)
        {
            SetState(ServerState.Stopped);
            return;
        }

        SetState(ServerState.Stopping);
        _gracefulShutdownRequester(process.Id);

        try
        {
            using var timeoutCts = new CancellationTokenSource(gracefulTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
        }

        SetState(ServerState.Stopped);
    }

    public async Task RestartAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
    {
        await StopAsync(gracefulTimeout, cancellationToken);
        await StartAsync(cancellationToken);
    }

    private void OnProcessExited(object? sender, EventArgs e) => SetState(ServerState.Stopped);

    private void SetState(ServerState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning)
            await StopAsync(TimeSpan.FromSeconds(5));

        lock (_lock)
        {
            _process?.Dispose();
            _process = null;
        }

        _ownedOutputLog?.Dispose();
    }
}
