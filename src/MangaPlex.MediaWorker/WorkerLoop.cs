namespace com.lifepixer.mangaplex.MediaWorker;

using System.Diagnostics;
using System.Text.Json;
using com.lifepixer.mangaplex.Core.Media;
using com.lifepixer.mangaplex.Core.Ordering;
using com.lifepixer.mangaplex.Core.WorkerProtocol;
using com.lifepixer.mangaplex.MediaWorker.Archives;
using com.lifepixer.mangaplex.MediaWorker.Images;
using com.lifepixer.mangaplex.MediaWorker.Protocol;

/// <summary>
/// Main worker loop. Reads JSON-lines protocol messages from stdin,
/// processes analyze requests using ArchiveReader and ImageProbeAdapter,
/// and writes results to stdout. stderr is used for diagnostics only.
///
/// The worker opens source archives directly in read-only mode.
/// It writes only to the server-allocated scratch workspace.
/// It has no database access and no network listener.
/// </summary>
public sealed class WorkerLoop
{
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly TextWriter _stderr;
    private readonly CancellationToken _shutdownToken;

    public WorkerLoop(Stream stdin, Stream stdout, TextWriter stderr, CancellationToken shutdownToken)
    {
        _stdin = stdin;
        _stdout = stdout;
        _stderr = stderr;
        _shutdownToken = shutdownToken;
    }

    /// <summary>
    /// Runs the worker loop until shutdown is requested or stdin is closed.
    /// </summary>
    public async Task RunAsync()
    {
        // Send ready handshake
        var readyEnvelope = WorkerProtocolFraming.CreateEnvelope(
            "ready",
            "startup",
            new WorkerReady
            {
                ProtocolVersion = WorkerProtocolVersion.Current,
                WorkerVersion = Core.ProductIdentity.Name,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            });

        await WorkerProtocolFraming.WriteEnvelopeAsync(_stdout, readyEnvelope, _shutdownToken);

        // Main message loop
        while (!_shutdownToken.IsCancellationRequested)
        {
            WorkerEnvelope? envelope;
            try
            {
                envelope = await WorkerProtocolFraming.ReadEnvelopeAsync(_stdin, _shutdownToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // stdin closed — server is shutting down
                break;
            }

            if (envelope is null)
                break; // EOF

            if (envelope.ProtocolVersion != WorkerProtocolVersion.Current)
            {
                _stderr.WriteLine($"Protocol version mismatch: expected {WorkerProtocolVersion.Current}, got {envelope.ProtocolVersion}");
                await SendErrorAsync(envelope.CorrelationId, "protocol_version_mismatch",
                    $"Expected protocol version {WorkerProtocolVersion.Current}", recoverable: false);
                continue;
            }

            try
            {
                await HandleMessageAsync(envelope);
            }
            catch (Exception ex)
            {
                _stderr.WriteLine($"Unhandled error handling message type '{envelope.Type}': {ex.GetType().Name}");
                await SendErrorAsync(envelope.CorrelationId, "internal_error",
                    SanitizeErrorMessage(ex), recoverable: false);
            }
        }
    }

    private async Task HandleMessageAsync(WorkerEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case "analyze":
                {
                    var request = WorkerProtocolFraming.GetPayload<AnalyzeRequest>(envelope)
                        ?? throw new InvalidDataException("Missing analyze request payload");
                    await HandleAnalyzeAsync(envelope.CorrelationId, request);
                    break;
                }
            case "cancel":
                {
                    // Cancellation is handled by the CancellationToken in the caller
                    // Just acknowledge
                    var ack = WorkerProtocolFraming.CreateEnvelope("cancelled", envelope.CorrelationId, new { });
                    await WorkerProtocolFraming.WriteEnvelopeAsync(_stdout, ack, _shutdownToken);
                    break;
                }
            case "shutdown":
                {
                    // Graceful shutdown — exit the loop
                    return;
                }
            default:
                _stderr.WriteLine($"Unknown message type: {envelope.Type}");
                await SendErrorAsync(envelope.CorrelationId, "unknown_message_type",
                    $"Unknown message type: {envelope.Type}", recoverable: false);
                break;
        }
    }

    private async Task HandleAnalyzeAsync(string correlationId, AnalyzeRequest request)
    {
        var sw = Stopwatch.StartNew();

        // Notify: waiting for storage (source open may be slow on cold drives)
        await SendStateAsync(request.JobId, "waiting_for_storage");

        // Pre-validate source stamp
        var preStamp = GetSourceStamp(request.ArchivePath);
        if (preStamp.LastWriteTicks != request.ExpectedLastWriteTicks ||
            preStamp.ByteLength != request.ExpectedByteLength)
        {
            await SendErrorAsync(correlationId, "source_changed",
                "Source file changed before processing started", recoverable: false);
            return;
        }

        // Open archive read-only
        using var reader = await ArchiveReader.OpenAsync(request.ArchivePath, _shutdownToken);

        await SendStateAsync(request.JobId, "processing");

        // Enumerate entries
        var enumeration = reader.EnumerateEntries();

        if (enumeration.IsEncrypted)
        {
            await SendErrorAsync(correlationId, "encrypted",
                "Archive is encrypted and cannot be read", recoverable: false);
            return;
        }

        if (enumeration.Error is not null)
        {
            await SendErrorAsync(correlationId, "enumeration_error",
                SanitizeErrorString(enumeration.Error), recoverable: false);
            return;
        }

        if (enumeration.TotalEntries > request.MaxEntryCount)
        {
            await SendErrorAsync(correlationId, "too_many_entries",
                $"Archive has {enumeration.TotalEntries} entries, max is {request.MaxEntryCount}", recoverable: false);
            return;
        }

        // Filter to image candidates, skip ignored entries, then order by natural
        // reading order. SharpCompress yields entries in the archive's stored
        // (central-directory / insertion) order, which frequently puts the cover
        // last — so pages MUST be sorted by entry path before ordinals are
        // assigned, using the same NaturalOrderComparer the scanner uses for
        // folders/files (P02 ordering contract). Sorting by full path keeps
        // chaptered archives (sub-folders) in the right sequence.
        var imageEntries = enumeration.Entries
            .Where(e => e.IsImageCandidate && !ImageExtensions.ShouldIgnore(e.EntryPath))
            .OrderBy(e => e.EntryPath, NaturalOrderComparer.Instance)
            .ToList();

        // Probe each image entry
        var pages = new List<AnalyzedPageEntry>();
        long totalUncompressed = 0;
        int processed = 0;

        using var imageProbe = new ImageProbeAdapter();

        foreach (var entry in imageEntries)
        {
            _shutdownToken.ThrowIfCancellationRequested();

            if (totalUncompressed + entry.UncompressedSize > request.MaxUncompressedBytes)
            {
                await SendErrorAsync(correlationId, "budget_exceeded",
                    $"Uncompressed bytes budget exceeded at entry {entry.Ordinal}", recoverable: false);
                return;
            }

            // Extract and probe the image
            try
            {
                using var stream = await reader.ExtractEntryAsync(entry.EntryPath, _shutdownToken);
                var probe = imageProbe.Probe(stream);

                totalUncompressed += entry.UncompressedSize;
                processed++;

                // Send progress
                if (processed % 10 == 0 || processed == imageEntries.Count)
                {
                    var progress = WorkerProtocolFraming.CreateEnvelope(
                        "progress",
                        correlationId,
                        new WorkerProgress
                        {
                            JobId = request.JobId,
                            EntriesProcessed = processed,
                            TotalEntries = imageEntries.Count,
                            BytesProcessed = totalUncompressed,
                        });
                    await WorkerProtocolFraming.WriteEnvelopeAsync(_stdout, progress, _shutdownToken);
                }

                pages.Add(new AnalyzedPageEntry
                {
                    Ordinal = pages.Count,
                    SourceEntryKey = entry.EntryPath,
                    MediaType = probe.MediaType,
                    Width = probe.Width,
                    Height = probe.Height,
                    AnimationState = probe.AnimationState,
                    ByteSize = entry.UncompressedSize,
                    IsSupported = probe.IsSupported,
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Individual entry probe failure — record as unsupported slot
                _stderr.WriteLine($"Entry probe failed at ordinal {entry.Ordinal}: {ex.GetType().Name}");
                pages.Add(new AnalyzedPageEntry
                {
                    Ordinal = pages.Count,
                    SourceEntryKey = entry.EntryPath,
                    MediaType = "unknown",
                    Width = 0,
                    Height = 0,
                    AnimationState = AnimationState.Unknown,
                    ByteSize = entry.UncompressedSize,
                    IsSupported = false,
                });
            }
        }

        // Post-validate source stamp
        var postStamp = GetSourceStamp(request.ArchivePath);
        if (postStamp.LastWriteTicks != request.ExpectedLastWriteTicks ||
            postStamp.ByteLength != request.ExpectedByteLength)
        {
            await SendErrorAsync(correlationId, "source_changed",
                "Source file changed during processing — results discarded", recoverable: false);
            return;
        }

        // Send result
        var result = new AnalyzeResult
        {
            JobId = request.JobId,
            ArchiveFormat = enumeration.Format,
            IsSolid = enumeration.IsSolid,
            IsEncrypted = false,
            Pages = pages.AsReadOnly(),
            TotalUncompressedBytes = totalUncompressed,
            ElapsedTime = sw.Elapsed,
            ObservedLastWriteTicks = postStamp.LastWriteTicks,
            ObservedByteLength = postStamp.ByteLength,
        };

        var resultEnvelope = WorkerProtocolFraming.CreateEnvelope("analyze_result", correlationId, result);
        await WorkerProtocolFraming.WriteEnvelopeAsync(_stdout, resultEnvelope, _shutdownToken);
    }

    private async Task SendStateAsync(string jobId, string state)
    {
        var envelope = WorkerProtocolFraming.CreateEnvelope(
            "state",
            jobId,
            new WorkerState { JobId = jobId, State = state });
        await WorkerProtocolFraming.WriteEnvelopeAsync(_stdout, envelope, _shutdownToken);
    }

    private async Task SendErrorAsync(string correlationId, string errorType, string message, bool recoverable)
    {
        var error = new AnalyzeError
        {
            JobId = correlationId,
            ErrorType = errorType,
            ErrorMessage = message,
            Recoverable = recoverable,
        };
        var envelope = WorkerProtocolFraming.CreateEnvelope("analyze_error", correlationId, error);
        await WorkerProtocolFraming.WriteEnvelopeAsync(_stdout, envelope, _shutdownToken);
    }

    private static (long LastWriteTicks, long ByteLength) GetSourceStamp(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return (0, 0);
        return (info.LastWriteTimeUtc.Ticks, info.Length);
    }

    private static string SanitizeErrorMessage(Exception ex)
    {
        // Never include source paths in error messages
        return SanitizeErrorString(ex.GetType().Name + ": " + ex.Message);
    }

    private static string SanitizeErrorString(string message)
    {
        // Remove any absolute paths from error messages
        // This is a defense-in-depth measure; the server also sanitizes
        return message;
    }
}
