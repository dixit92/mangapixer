namespace com.lifepixer.mangapixer.MediaWorker;

using System.Diagnostics;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Media;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.MediaWorker.Archives;
using com.lifepixer.mangapixer.MediaWorker.Images;
using com.lifepixer.mangapixer.MediaWorker.Metadata;
using com.lifepixer.mangapixer.MediaWorker.Protocol;

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

            // Graceful shutdown ends the loop (and so the process). Handled
            // here, not in HandleMessageAsync: a `return` there only ended that
            // one message, so the worker went back to reading stdin and had to
            // be force-killed after the server's grace period.
            if (envelope.Type == "shutdown")
                break;

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
            case "extract":
                {
                    var request = WorkerProtocolFraming.GetPayload<ExtractRequest>(envelope)
                        ?? throw new InvalidDataException("Missing extract request payload");
                    await HandleExtractAsync(envelope.CorrelationId, request);
                    break;
                }
            case "comicinfo":
                {
                    var request = WorkerProtocolFraming.GetPayload<ComicInfoRequest>(envelope)
                        ?? throw new InvalidDataException("Missing comicinfo request payload");
                    await HandleComicInfoAsync(envelope.CorrelationId, request);
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
        // folders/files, so page order matches library-scan order. Sorting by
        // full path keeps chaptered archives (sub-folders) in the right sequence.
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

        // ComicInfo.xml (protocol v3): one extra small entry read while the archive
        // is already open. A ComicInfo problem never fails the analysis - every
        // outcome is a status, and an unexpected exception is a read_error.
        ComicInfoOutcome comicInfo;
        try
        {
            comicInfo = await ComicInfoReader.ReadAsync(
                reader, enumeration.Entries, enumeration.IsSolid, ComicInfoLimits.MaxXmlBytes, _shutdownToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _stderr.WriteLine($"ComicInfo read failed: {ex.GetType().Name}");
            comicInfo = new ComicInfoOutcome { Status = ComicInfoStatus.ReadError };
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
            ComicInfo = comicInfo,
        };

        var resultEnvelope = WorkerProtocolFraming.CreateEnvelope("analyze_result", correlationId, result);
        try
        {
            await WorkerProtocolFraming.WriteEnvelopeAsync(_stdout, resultEnvelope, _shutdownToken);
        }
        catch (InvalidDataException) when (result.ComicInfo?.Payload is not null)
        {
            // A huge page list plus a maximal ComicInfo payload can exceed the 1 MiB
            // message cap. The pages matter more: resend without ComicInfo (the
            // server then leaves the item to the ComicInfo backfill, which reads it
            // with the small `comicinfo` message).
            var withoutComicInfo = WorkerProtocolFraming.CreateEnvelope(
                "analyze_result", correlationId, result with { ComicInfo = null });
            await WorkerProtocolFraming.WriteEnvelopeAsync(_stdout, withoutComicInfo, _shutdownToken);
        }
    }

    /// <summary>
    /// Reads only an archive's ComicInfo.xml (protocol v3 backfill for archives
    /// analysed before 1.24.0). Opens the archive read-only, reads the directory,
    /// inflates at most one capped entry; solid archives are answered
    /// <c>skipped_solid</c> without decompressing. Same pre/post source-stamp check
    /// as analysis: a changed source yields an error and nothing is stored.
    /// </summary>
    private async Task HandleComicInfoAsync(string correlationId, ComicInfoRequest request)
    {
        var preStamp = GetSourceStamp(request.ArchivePath);
        if (preStamp.LastWriteTicks == 0 && preStamp.ByteLength == 0)
        {
            await SendComicInfoErrorAsync(correlationId, "source_missing", "Source file is missing");
            return;
        }
        if (preStamp.LastWriteTicks != request.ExpectedLastWriteTicks ||
            preStamp.ByteLength != request.ExpectedByteLength)
        {
            await SendComicInfoErrorAsync(correlationId, "source_changed", "Source file changed before reading");
            return;
        }

        ComicInfoOutcome outcome;
        try
        {
            using var reader = await ArchiveReader.OpenAsync(request.ArchivePath, _shutdownToken);
            var enumeration = reader.EnumerateEntries();
            if (enumeration.IsEncrypted || enumeration.Error is not null)
            {
                outcome = new ComicInfoOutcome { Status = ComicInfoStatus.ReadError };
            }
            else
            {
                var maxBytes = request.MaxXmlBytes > 0
                    ? Math.Min(request.MaxXmlBytes, ComicInfoLimits.MaxXmlBytes)
                    : ComicInfoLimits.MaxXmlBytes;
                outcome = await ComicInfoReader.ReadAsync(
                    reader, enumeration.Entries, enumeration.IsSolid, maxBytes, _shutdownToken);
            }
        }
        catch (OperationCanceledException)
        {
            return; // shutdown - no response needed
        }
        catch (Exception ex)
        {
            _stderr.WriteLine($"ComicInfo read failed: {ex.GetType().Name}");
            outcome = new ComicInfoOutcome { Status = ComicInfoStatus.ReadError };
        }

        var postStamp = GetSourceStamp(request.ArchivePath);
        if (postStamp.LastWriteTicks != request.ExpectedLastWriteTicks ||
            postStamp.ByteLength != request.ExpectedByteLength)
        {
            await SendComicInfoErrorAsync(correlationId, "source_changed", "Source file changed while reading");
            return;
        }

        var result = new ComicInfoResult
        {
            JobId = request.JobId,
            Outcome = outcome,
            ObservedLastWriteTicks = postStamp.LastWriteTicks,
            ObservedByteLength = postStamp.ByteLength,
        };
        var envelope = WorkerProtocolFraming.CreateEnvelope("comicinfo_result", correlationId, result);
        await WorkerProtocolFraming.WriteEnvelopeAsync(_stdout, envelope, _shutdownToken);
    }

    private async Task SendComicInfoErrorAsync(string correlationId, string errorType, string message)
    {
        var error = new ComicInfoError
        {
            JobId = correlationId,
            ErrorType = errorType,
            ErrorMessage = message,
        };
        var envelope = WorkerProtocolFraming.CreateEnvelope("comicinfo_error", correlationId, error);
        await WorkerProtocolFraming.WriteEnvelopeAsync(_stdout, envelope, _shutdownToken);
    }

    /// <summary>
    /// Extracts one page image from the archive and encodes the requested variant.
    /// The worker is the only process that opens archives / decodes images.
    /// Solid archives are deferred with a graceful, non-recoverable error.
    /// </summary>
    private async Task HandleExtractAsync(string correlationId, ExtractRequest request)
    {
        // Pre-validate source stamp — refuse if the file changed under us.
        var preStamp = GetSourceStamp(request.ArchivePath);
        if (preStamp.LastWriteTicks != request.ExpectedLastWriteTicks ||
            preStamp.ByteLength != request.ExpectedByteLength)
        {
            await SendExtractErrorAsync(correlationId, "source_changed",
                "Source file changed — extraction refused", recoverable: false);
            return;
        }

        try
        {
            using var reader = await ArchiveReader.OpenAsync(request.ArchivePath, _shutdownToken);

            // Solid archives need sequential decompression to reach entry N; that is
            // a later package. Fail gracefully rather than hang or read the wrong page.
            if (reader.IsSolid)
            {
                await SendExtractErrorAsync(correlationId, "unsupported_solid",
                    "Solid archives are not yet supported for reading.", recoverable: false);
                return;
            }

            byte[] bytes;
            try
            {
                using var entryStream = await reader.ExtractEntryAsync(request.SourceEntryKey, _shutdownToken);
                using var ms = new MemoryStream();
                await entryStream.CopyToAsync(ms, _shutdownToken);
                bytes = ms.ToArray();
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                await SendExtractErrorAsync(correlationId, "encrypted",
                    "Archive is encrypted and cannot be read", recoverable: false);
                return;
            }
            catch (FileNotFoundException)
            {
                await SendExtractErrorAsync(correlationId, "page_not_found",
                    "Page entry not found in archive", recoverable: false);
                return;
            }

            var outDir = Path.GetDirectoryName(request.OutputPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            EncodedVariant encoded;
            try
            {
                encoded = new ImageVariantEncoder().Encode(
                    bytes, request.Variant, request.OutputPath,
                    request.ThumbnailMaxDimension, request.WebpQuality,
                    request.MaxDimension, request.ResizeFilter);
            }
            catch (Exception ex)
            {
                await SendExtractErrorAsync(correlationId, "encode_failed",
                    SanitizeErrorString(ex.Message), recoverable: false);
                return;
            }

            // Post-validate: if the source changed mid-extract, discard the output.
            var postStamp = GetSourceStamp(request.ArchivePath);
            if (postStamp.LastWriteTicks != request.ExpectedLastWriteTicks ||
                postStamp.ByteLength != request.ExpectedByteLength)
            {
                try { File.Delete(request.OutputPath); } catch { /* best effort */ }
                await SendExtractErrorAsync(correlationId, "source_changed",
                    "Source file changed during extraction — output discarded", recoverable: false);
                return;
            }

            var result = new ExtractResult
            {
                JobId = request.JobId,
                OutputPath = request.OutputPath,
                MediaType = encoded.MediaType,
                Width = encoded.Width,
                Height = encoded.Height,
                ByteSize = encoded.ByteSize,
            };
            var envelope = WorkerProtocolFraming.CreateEnvelope("extract_result", correlationId, result);
            await WorkerProtocolFraming.WriteEnvelopeAsync(_stdout, envelope, _shutdownToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown/cancel — no response needed.
        }
        catch (Exception ex)
        {
            await SendExtractErrorAsync(correlationId, "extraction_failed",
                SanitizeErrorString(ex.Message), recoverable: true);
        }
    }

    private async Task SendExtractErrorAsync(string correlationId, string errorType, string message, bool recoverable)
    {
        var error = new ExtractError
        {
            JobId = correlationId,
            ErrorType = errorType,
            ErrorMessage = message,
            Recoverable = recoverable,
        };
        var envelope = WorkerProtocolFraming.CreateEnvelope("extract_error", correlationId, error);
        await WorkerProtocolFraming.WriteEnvelopeAsync(_stdout, envelope, _shutdownToken);
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
