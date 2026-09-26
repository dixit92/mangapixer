namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Scanning;

/// <summary>
/// The metadata work after a successful scan (stage 2), called by
/// <see cref="LibraryScanLauncher"/> next to the ComicInfo kick, after the scan
/// lease and maintenance are released - never inside reconcile:
/// 1. carry-over of stranded folder rows along the moved-archive ledger (local;
///    one query when nothing is stranded, nothing when nothing moved);
/// 2. ONE boolean read (the global Automatic matching switch with its consent);
///    when on, the works among this scan's new folders and their parents are
///    queued and the worker is woken. No network here.
/// </summary>
public sealed class MetadataPostScanHook
{
    private readonly MetadataCarryOverService _carryOver;
    private readonly MetadataAutoMatchService _autoMatch;
    private readonly ILogger<MetadataPostScanHook> _logger;

    public MetadataPostScanHook(MetadataCarryOverService carryOver, MetadataAutoMatchService autoMatch, ILogger<MetadataPostScanHook> logger)
    {
        _carryOver = carryOver;
        _autoMatch = autoMatch;
        _logger = logger;
    }

    public async Task OnScanCompletedAsync(long libraryId, DateTimeOffset scanStartedAt, ScanResult result, CancellationToken ct = default)
    {
        try
        {
            if (result.MoveLedgerTruncated)
                _logger.LogInformation(LogEvents.Metadata.CarryOver, "Metadata carry-over skipped for library {LibraryId}: too many moves", libraryId);
            else if (result.Moves.Count > 0)
                await _carryOver.CarryAsync(libraryId, result.Moves, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(LogEvents.Metadata.CarryOverFailed, "Metadata carry-over failed for library {LibraryId}: {Error}", libraryId, ex.GetType().Name);
        }

        try
        {
            if (!await _autoMatch.IsAutomaticEnabledAsync(ct))
                return;
            await _autoMatch.EnqueueNewFoldersAsync(libraryId, scanStartedAt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(LogEvents.Metadata.AutoMatchFailed, "Automatic matching enqueue failed for library {LibraryId}: {Error}", libraryId, ex.GetType().Name);
        }
    }
}
