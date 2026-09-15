namespace com.lifepixer.mangaplex.Tests.Core;

using System.Text.Json;
using com.lifepixer.mangaplex.Core.Media;
using com.lifepixer.mangaplex.Core.WorkerProtocol;
using Xunit;

/// <summary>
/// Tests for worker protocol serialization, framing, and version handling.
/// </summary>
public sealed class WorkerProtocolTests
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void WorkerProtocolVersion_IsTwo()
    {
        // v2 added on-demand page extraction (extract/extract_result/extract_error).
        Assert.Equal(2, WorkerProtocolVersion.Current);
    }

    [Fact]
    public void WorkerEnvelope_SerializesAndDeserializes()
    {
        var envelope = new WorkerEnvelope
        {
            Type = "analyze",
            CorrelationId = "corr-123",
            ProtocolVersion = WorkerProtocolVersion.Current,
            Payload = JsonSerializer.SerializeToElement(new { jobId = "job1" }, s_options),
        };

        var json = JsonSerializer.Serialize(envelope, s_options);
        var restored = JsonSerializer.Deserialize<WorkerEnvelope>(json, s_options)!;

        Assert.Equal("analyze", restored.Type);
        Assert.Equal("corr-123", restored.CorrelationId);
        Assert.Equal(WorkerProtocolVersion.Current, restored.ProtocolVersion);
    }

    [Fact]
    public void AnalyzeRequest_IncludesSourceStampFields()
    {
        var request = new AnalyzeRequest
        {
            JobId = "job1",
            ArchivePath = "/source/archive.cbz",
            ContentVersion = 1L,
            ExpectedLastWriteTicks = 123L,
            ExpectedByteLength = 456L,
            ScratchWorkspacePath = "/scratch/ws-abc",
            Deadline = DateTimeOffset.UtcNow,
        };

        Assert.Equal(123L, request.ExpectedLastWriteTicks);
        Assert.Equal(456L, request.ExpectedByteLength);
        Assert.Equal("/scratch/ws-abc", request.ScratchWorkspacePath);
    }

    [Fact]
    public void AnalyzeRequest_DefaultLimits_AreReconciled()
    {
        var request = new AnalyzeRequest
        {
            JobId = "job1",
            ArchivePath = "/source/archive.cbz",
            ContentVersion = 1L,
            ExpectedLastWriteTicks = 0,
            ExpectedByteLength = 0,
            ScratchWorkspacePath = "/scratch/ws",
            Deadline = DateTimeOffset.UtcNow,
        };

        // Default resource limits for archive analysis
        Assert.Equal(50_000, request.MaxEntryCount);
        Assert.Equal(32L * 1024 * 1024 * 1024, request.MaxUncompressedBytes);
        Assert.Equal(50_000, request.MaxImageDimension);
    }

    [Fact]
    public void AnalyzeResult_IncludesObservedSourceStamp()
    {
        var result = new AnalyzeResult
        {
            JobId = "job1",
            ArchiveFormat = ArchiveFormat.Zip,
            IsSolid = false,
            IsEncrypted = false,
            Pages = Array.Empty<AnalyzedPageEntry>(),
            TotalUncompressedBytes = 0,
            ElapsedTime = TimeSpan.FromSeconds(1),
            ObservedLastWriteTicks = 789L,
            ObservedByteLength = 1011L,
        };

        var json = JsonSerializer.Serialize(result, s_options);
        var restored = JsonSerializer.Deserialize<AnalyzeResult>(json, s_options)!;

        Assert.Equal(789L, restored.ObservedLastWriteTicks);
        Assert.Equal(1011L, restored.ObservedByteLength);
    }

    [Fact]
    public void AnalyzedPageEntry_IncludesSourceEntryKey()
    {
        var entry = new AnalyzedPageEntry
        {
            Ordinal = 0,
            SourceEntryKey = "page001.png",
            MediaType = "image/png",
            Width = 800,
            Height = 1200,
            ByteSize = 1024,
        };

        var json = JsonSerializer.Serialize(entry, s_options);
        var restored = JsonSerializer.Deserialize<AnalyzedPageEntry>(json, s_options)!;

        Assert.Equal("page001.png", restored.SourceEntryKey);
        Assert.True(restored.IsSupported);
    }

    [Fact]
    public void WorkerState_SerializesCorrectly()
    {
        var state = new WorkerState
        {
            JobId = "job1",
            State = "waiting_for_storage",
        };

        var json = JsonSerializer.Serialize(state, s_options);
        var restored = JsonSerializer.Deserialize<WorkerState>(json, s_options)!;

        Assert.Equal("waiting_for_storage", restored.State);
    }

    [Fact]
    public void AnalyzeError_DistinguishesRecoverableAndNonRecoverable()
    {
        var recoverable = new AnalyzeError
        {
            JobId = "job1",
            ErrorType = "timeout",
            ErrorMessage = "Operation timed out",
            Recoverable = true,
        };

        var nonRecoverable = new AnalyzeError
        {
            JobId = "job2",
            ErrorType = "corrupt",
            ErrorMessage = "Archive is corrupt",
            Recoverable = false,
        };

        Assert.True(recoverable.Recoverable);
        Assert.False(nonRecoverable.Recoverable);
    }

    [Fact]
    public void AnalyzeRequest_ArchivePath_IsPrivateSourceLocator()
    {
        // The ArchivePath field is a private validated source locator for worker IPC.
        // It should never appear in public HTTP DTOs.
        var request = new AnalyzeRequest
        {
            JobId = "job1",
            ArchivePath = "/private/source/path/archive.cbz",
            ContentVersion = 1L,
            ExpectedLastWriteTicks = 0,
            ExpectedByteLength = 0,
            ScratchWorkspacePath = "/scratch/ws",
            Deadline = DateTimeOffset.UtcNow,
        };

        // Verify the field exists and holds the source path (private IPC data)
        Assert.Equal("/private/source/path/archive.cbz", request.ArchivePath);
    }
}
