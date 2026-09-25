namespace com.lifepixer.mangapixer.Tests.Core;

using System.Text.Json;
using com.lifepixer.mangapixer.Core.Media;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
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
    public void WorkerProtocolVersion_IsThree()
    {
        // v2 added on-demand page extraction (extract/extract_result/extract_error);
        // v3 (1.24.0) added the ComicInfo read (comicinfo/comicinfo_result/comicinfo_error
        // and the optional AnalyzeResult.ComicInfo).
        Assert.Equal(3, WorkerProtocolVersion.Current);
    }

    [Fact]
    public void ComicInfoMessages_RoundTrip_WithCamelCase()
    {
        var result = new ComicInfoResult
        {
            JobId = "ci-1",
            Outcome = new ComicInfoOutcome
            {
                Status = ComicInfoStatus.Parsed,
                Payload = new ComicInfoPayload
                {
                    Series = "Synthetic",
                    Number = "12.5",
                    Creators = [new ComicInfoCreator { Name = "A", Role = "writer" }],
                    Genres = ["Action"],
                },
            },
            ObservedLastWriteTicks = 10,
            ObservedByteLength = 20,
        };

        var json = JsonSerializer.Serialize(result, s_options);
        Assert.Contains("\"observedLastWriteTicks\"", json);
        var restored = JsonSerializer.Deserialize<ComicInfoResult>(json, s_options)!;
        Assert.Equal("Synthetic", restored.Outcome.Payload!.Series);
        Assert.Equal("12.5", restored.Outcome.Payload.Number);
        Assert.Equal("writer", restored.Outcome.Payload.Creators[0].Role);
    }

    [Fact]
    public void AnalyzeResult_WithoutComicInfo_DeserializesAsNull()
    {
        // A v3 AnalyzeResult with no ComicInfo field (older shape) still reads.
        const string json = "{\"jobId\":\"j\",\"archiveFormat\":1,\"isSolid\":false,\"isEncrypted\":false,\"pages\":[],\"totalUncompressedBytes\":0,\"elapsedTime\":\"00:00:01\",\"observedLastWriteTicks\":1,\"observedByteLength\":2}";

        var restored = JsonSerializer.Deserialize<AnalyzeResult>(json, s_options)!;

        Assert.Null(restored.ComicInfo);
    }

    [Theory]
    [InlineData(ComicInfoStatus.Absent, 0)]
    [InlineData(ComicInfoStatus.Parsed, 1)]
    [InlineData(ComicInfoStatus.Malformed, 2)]
    [InlineData(ComicInfoStatus.TooLarge, 3)]
    [InlineData(ComicInfoStatus.SkippedSolid, 4)]
    [InlineData(ComicInfoStatus.ReadError, 5)]
    [InlineData("anything-else", 5)]
    public void ComicInfoStatus_MapsToStoredState(string status, int state)
    {
        Assert.Equal(state, ComicInfoStatus.ToState(status));
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
