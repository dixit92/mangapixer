namespace com.lifepixer.mangapixer.Server.Features.Metadata.Authors;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Logging;

/// <summary>
/// Runs the admin-started look-up of artists' other names (1.38.0) in the background, one at a time (single-flight): a start while
/// one runs joins it. Each run uses its own DI scope and stops with the app (the ids not reached stay unfetched; the next start
/// resumes there). Keeps the running run's progress and the last finished run in memory - the stored author records are the durable
/// progress. Never starts on its own: only <see cref="TryStart"/>, called by the admin endpoint.
/// </summary>
public sealed class AuthorAliasLookupRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<AuthorAliasLookupRunner> _logger;
    private readonly CancellationToken _stopping;
    private readonly object _gate = new();
    private Task? _task;
    private CancellationTokenSource? _cts;
    private AuthorAliasRunState? _running;
    private AuthorAliasRunState? _last;

    public AuthorAliasLookupRunner(
        IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<AuthorAliasLookupRunner> logger, IHostApplicationLifetime? lifetime = null)
    {
        _scopeFactory = scopeFactory;
        _time = time;
        _logger = logger;
        _stopping = lifetime?.ApplicationStopping ?? CancellationToken.None;
    }

    /// <summary>The running look-up's progress, or null.</summary>
    public AuthorAliasRunDto? Running
    {
        get
        {
            lock (_gate)
                return _running?.Snapshot();
        }
    }

    /// <summary>The last finished look-up since the server started, or null.</summary>
    public AuthorAliasRunDto? LastRun
    {
        get
        {
            lock (_gate)
                return _last?.Snapshot();
        }
    }

    /// <summary>The running look-up's task (tests await it), or a completed task.</summary>
    public Task Current
    {
        get
        {
            lock (_gate)
                return _task ?? Task.CompletedTask;
        }
    }

    /// <summary>Starts a look-up of <paramref name="targets"/>; false (and nothing started) when one is already running.</summary>
    public bool TryStart(IReadOnlyList<AuthorAliasTarget> targets)
    {
        lock (_gate)
        {
            if (_task is { IsCompleted: false })
                return false;
            var state = new AuthorAliasRunState(targets.Count, _time.GetUtcNow());
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            _running = state;
            _cts = cts;
            _task = Task.Run(() => RunAsync(targets, state, cts), CancellationToken.None);
            return true;
        }
    }

    /// <summary>Asks the running look-up to stop after its current request; false when none runs.</summary>
    public bool Cancel()
    {
        lock (_gate)
        {
            if (_task is not { IsCompleted: false } || _cts is null)
                return false;
            _cts.Cancel();
            return true;
        }
    }

    private async Task RunAsync(IReadOnlyList<AuthorAliasTarget> targets, AuthorAliasRunState state, CancellationTokenSource cts)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AuthorAliasLookupService>().RunAsync(targets, state, cts.Token);
        }
        catch (Exception ex)
        {
            state.Finish(AuthorAliasRunState.FailedOutcome, null, _time.GetUtcNow());
            _logger.LogWarning(LogEvents.Metadata.AuthorLookupFailed, "Author look-up failed: {Error}", ex.GetType().Name);
        }
        finally
        {
            lock (_gate)
            {
                _last = state;
                if (ReferenceEquals(_running, state))
                    _running = null;
                if (ReferenceEquals(_cts, cts))
                    _cts = null;
            }
            cts.Dispose();
        }
    }
}
