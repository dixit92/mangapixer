namespace com.lifepixer.mangapixer.Server.Features.VolumeScan;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Jobs;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Folders a reader opened in the Volumes view (1.44.0): they go to the FRONT of the next sweep. In memory only, deduplicated, bounded.
/// </summary>
public sealed class VolumeScanQueue
{
    private const int MaxQueued = 256;
    private readonly Lock _lock = new();
    private readonly LinkedList<long> _order = new();
    private readonly HashSet<long> _queued = [];
    private readonly SemaphoreSlim _signal = new(0);

    public void Enqueue(long folderId)
    {
        lock (_lock)
        {
            if (!_queued.Add(folderId))
                return;
            _order.AddLast(folderId);
            if (_order.Count > MaxQueued)
            {
                _queued.Remove(_order.First!.Value);
                _order.RemoveFirst();
            }
        }
        if (_signal.CurrentCount == 0)
            _signal.Release();
    }

    public bool TryDequeue(out long folderId)
    {
        lock (_lock)
        {
            if (_order.First is { } first)
            {
                folderId = first.Value;
                _order.RemoveFirst();
                _queued.Remove(folderId);
                return true;
            }
        }
        folderId = 0;
        return false;
    }

    /// <summary>Waits until a folder is queued or <paramref name="timeout"/> passes.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

/// <summary>
/// The volume contents scan in the background (1.44.0, owner Q1-Q2: on by default, no cap - it shares the Background slot). A sweep runs
/// after a library scan that changed something and hourly; folders a reader opened in the Volumes view go first. ONE request at a time
/// (one folder, one archive, one chunk), always on a Background slot of the worker pool, so "Background work while someone reads" (Pause
/// / Slow / Normal) applies. Resumable: the page hashes are stored chunk by chunk. Off when the admin switch or the configuration
/// (<c>MangaPixer:VolumeScan:Enabled=false</c>) says so. Admin > Jobs: "Volume contents".
/// </summary>
public sealed class VolumeContentScanService(
    IServiceScopeFactory scopes,
    VolumeScanQueue queue,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<VolumeContentScanService> logger,
    JobRunRecorder? runs = null) : BackgroundService
{
    public const string ConfigEnabledKey = "MangaPixer:VolumeScan:Enabled";
    public const string InitialDelayKey = "MangaPixer:VolumeScan:InitialDelaySeconds";
    public const string PollKey = "MangaPixer:VolumeScan:PollSeconds";

    /// <summary>The hourly sweep (also after every library scan that changed something).</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private volatile bool _enabled = true;
    private volatile bool _running;
    private volatile int _waiting = -1;
    private DateTimeOffset? _lastSweepStart;
    private DateTimeOffset? _lastLibraryScan;

    /// <summary>False when configuration switches the scan off (whatever the admin switch says).</summary>
    public bool ConfigEnabled => configuration.GetValue(ConfigEnabledKey, true);

    /// <summary>The effective switch (configuration AND the admin switch), as last read.</summary>
    public bool Enabled => ConfigEnabled && _enabled;

    public bool Running => _running;

    /// <summary>Folders the current or last sweep still had to check; null before the first sweep.</summary>
    public int? FoldersWaiting => _waiting < 0 ? null : _waiting;

    /// <summary>Applies a changed admin switch at once (the next chunk sees it).</summary>
    public void SetEnabled(bool enabled) => _enabled = enabled;

    /// <summary>Asks for a sweep soon (tests, the admin switch turned on).</summary>
    public void RequestSweep()
    {
        _lastSweepStart = null;
        queue.Enqueue(0);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue(PollKey, 30), 1, 3600));
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue(InitialDelayKey, 120), 0, 3600)), time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(LogEvents.Worker.VolumeScanSweepFailed, "Volume contents sweep failed: {Error}", ex.GetType().Name);
                }
                await queue.WaitAsync(poll, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One look: the opened folders first, then a sweep when one is due.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        await RefreshSwitchAsync(ct);
        if (!Enabled)
        {
            while (queue.TryDequeue(out _)) { }
            return;
        }

        var front = new List<long>();
        while (queue.TryDequeue(out var folder))
        {
            if (folder != 0)
                front.Add(folder);
        }
        var lastScan = await LastLibraryScanAsync(ct);
        var now = time.GetUtcNow();
        var due = _lastSweepStart is null || now - _lastSweepStart >= SweepInterval || (lastScan is not null && lastScan != _lastLibraryScan);
        if (!due)
        {
            foreach (var folder in front)
            {
                if (await ScanOneAsync(folder, ct) == FolderScanOutcome.Stopped)
                    return;
            }
            return;
        }
        _lastLibraryScan = lastScan;
        await SweepAsync(front, ct);
    }

    private async Task SweepAsync(List<long> front, CancellationToken ct)
    {
        _running = true;
        _lastSweepStart = time.GetUtcNow();
        var startedAt = runs is null ? time.GetUtcNow() : await runs.StartedAsync(ScheduledJobKeys.VolumeContents, ct);
        var outcome = JobOutcomes.Failed;
        string? detail = null;
        int scanned = 0, stopped = 0;
        try
        {
            List<long> folders;
            using (var scope = scopes.CreateScope())
                folders = [.. await scope.ServiceProvider.GetRequiredService<VolumeContentScanner>().CandidateFoldersAsync(ct)];
            var ordered = front.Where(folders.Contains).Concat(folders.Where(f => !front.Contains(f))).ToList();
            _waiting = ordered.Count;
            foreach (var folder in ordered)
            {
                var result = await ScanOneAsync(folder, ct);
                if (result == FolderScanOutcome.Stopped)
                {
                    stopped++;
                    break;
                }
                if (result == FolderScanOutcome.Scanned)
                    scanned++;
                _waiting--;
            }
            outcome = JobOutcomes.Ok;
            detail = string.Create(CultureInfo.InvariantCulture,
                $"{ordered.Count} folders to check, {scanned} scanned{(stopped > 0 ? ", paused" : string.Empty)}");
            if (scanned > 0)
                logger.LogInformation(LogEvents.Worker.VolumeScanSweep, "Volume contents sweep: {Folders} folders to check, {Scanned} scanned",
                    ordered.Count, scanned);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            detail = ex.GetType().Name;
            throw;
        }
        finally
        {
            _running = false;
            if (runs is not null)
                await runs.FinishedAsync(ScheduledJobKeys.VolumeContents, startedAt, outcome, detail, CancellationToken.None);
        }
    }

    private async Task<FolderScanOutcome> ScanOneAsync(long folderId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var scanner = scope.ServiceProvider.GetRequiredService<VolumeContentScanner>();
        return await scanner.ScanFolderAsync(folderId, () => Enabled, ct);
    }

    private async Task RefreshSwitchAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        _enabled = await db.VolumeScanState.AsNoTracking().Where(s => s.Id == VolumeScanStateEntity.SingletonId)
            .Select(s => (bool?)s.Enabled).FirstOrDefaultAsync(ct) ?? true;
    }

    private async Task<DateTimeOffset?> LastLibraryScanAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var completed = await db.Libraries.AsNoTracking().Where(l => l.LastScanCompleted != null).Select(l => l.LastScanCompleted).ToListAsync(ct);
        return completed.Count == 0 ? null : completed.Max();
    }
}

/// <summary>The admin side of the scan: the switch and the counts (Admin > Performance).</summary>
public sealed class VolumeScanSettingsService(MangaPixerDbContext db, IEnumerable<IHostedService> hosted, ILogger<VolumeScanSettingsService> logger)
{
    private VolumeContentScanService? Service => hosted.OfType<VolumeContentScanService>().FirstOrDefault();

    public async Task<VolumeScanStatusDto> GetAsync(CancellationToken ct)
    {
        var enabled = await db.VolumeScanState.AsNoTracking().Where(s => s.Id == VolumeScanStateEntity.SingletonId)
            .Select(s => (bool?)s.Enabled).FirstOrDefaultAsync(ct) ?? true;
        var rows = await db.VolumeContents.AsNoTracking().Select(v => new { v.State, v.ChaptersJson }).ToListAsync(ct);
        var withChapters = rows.Where(r => r.State is (int)VolumeContentRowState.Known or (int)VolumeContentRowState.Partial).ToList();
        var service = Service;
        return new VolumeScanStatusDto
        {
            Enabled = enabled,
            ConfigDisabled = service is { ConfigEnabled: false },
            VolumesChecked = rows.Count,
            VolumesWithChapters = withChapters.Count,
            ChaptersInside = withChapters.Sum(r => VolumeContentReader.ParseChapters(r.ChaptersJson).Count),
            FoldersWaiting = service?.FoldersWaiting,
            Running = service?.Running ?? false,
        };
    }

    public async Task<VolumeScanStatusDto> SetAsync(bool enabled, CancellationToken ct)
    {
        var row = await db.VolumeScanState.FirstOrDefaultAsync(s => s.Id == VolumeScanStateEntity.SingletonId, ct);
        if (row is null)
        {
            row = new VolumeScanStateEntity();
            db.VolumeScanState.Add(row);
        }
        row.Enabled = enabled;
        await db.SaveChangesAsync(ct);
        if (Service is { } service)
        {
            service.SetEnabled(enabled);
            if (enabled)
                service.RequestSweep();
        }
        logger.LogInformation(LogEvents.Worker.VolumeScanSwitchChanged, "Volume contents scan switched {State}", enabled ? "on" : "off");
        return await GetAsync(ct);
    }
}
