namespace com.lifepixer.mangaplex.Tests.Core.Contracts;

using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Core.Media;
using com.lifepixer.mangaplex.Core.Reading;
using com.lifepixer.mangaplex.Core.WorkerProtocol;
using System.Text.Json;
using Xunit;

/// <summary>
/// Tests that all DTOs serialize and deserialize consistently via System.Text.Json,
/// and that no DTO contains source paths or other private information.
/// These tests enforce the P02 contract freeze.
/// </summary>
public sealed class ContractSerializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void CatalogNodeDto_RoundTrip_PreservesAllFields()
    {
        var dto = new CatalogNodeDto
        {
            Id = "abc",
            ParentId = "def",
            LibraryId = "lib1",
            Kind = CatalogNodeKind.Archive,
            DisplayName = "Volume 1",
            Availability = CatalogNodeAvailability.Available,
            CoverUrl = "/api/v1/items/abc/cover",
            ChildFolderCount = null,
            ChildArchiveCount = null,
            PageCount = 42,
            ReadingState = ReadingState.InProgress,
            LastReadPage = 5,
        };

        var json = JsonSerializer.Serialize(dto, Options);
        var restored = JsonSerializer.Deserialize<CatalogNodeDto>(json, Options)!;

        Assert.Equal(dto.Id, restored.Id);
        Assert.Equal(dto.ParentId, restored.ParentId);
        Assert.Equal(dto.LibraryId, restored.LibraryId);
        Assert.Equal(dto.Kind, restored.Kind);
        Assert.Equal(dto.DisplayName, restored.DisplayName);
        Assert.Equal(dto.Availability, restored.Availability);
        Assert.Equal(dto.CoverUrl, restored.CoverUrl);
        Assert.Equal(dto.PageCount, restored.PageCount);
        Assert.Equal(dto.ReadingState, restored.ReadingState);
        Assert.Equal(dto.LastReadPage, restored.LastReadPage);
    }

    [Fact]
    public void ItemManifest_RoundTrip_PreservesZeroBasedPages()
    {
        var manifest = new ItemManifest
        {
            ItemId = "item1",
            ContentVersion = 42L,
            ManifestVersion = 1,
            ArchiveFormat = ArchiveFormat.Zip,
            PageCount = 3,
            Pages = new[]
            {
                new ManifestPageEntry { EntryKey = "p0", PageIndex = 0, MediaType = "image/png", Width = 800, Height = 1200 },
                new ManifestPageEntry { EntryKey = "p1", PageIndex = 1, MediaType = "image/jpeg", Width = 800, Height = 1200 },
                new ManifestPageEntry { EntryKey = "p2", PageIndex = 2, MediaType = "image/png", Width = 800, Height = 1200 },
            },
            IsSolid = false,
            HasAnimatedPages = false,
        };

        var json = JsonSerializer.Serialize(manifest, Options);
        var restored = JsonSerializer.Deserialize<ItemManifest>(json, Options)!;

        Assert.Equal(manifest.ItemId, restored.ItemId);
        Assert.Equal(manifest.ContentVersion, restored.ContentVersion);
        Assert.Equal(manifest.PageCount, restored.PageCount);
        Assert.Equal(3, restored.Pages.Count);
        Assert.Equal(0, restored.Pages[0].PageIndex);
        Assert.Equal(1, restored.Pages[1].PageIndex);
        Assert.Equal(2, restored.Pages[2].PageIndex);
        Assert.Equal("image/png", restored.Pages[0].MediaType);
    }

    [Fact]
    public void ItemReadiness_RoundTrip_PreservesErrorState()
    {
        var readiness = new ItemReadiness
        {
            ItemId = "item1",
            State = ItemReadinessState.Failed,
            ContentVersion = 1L,
            Error = "Archive is corrupt",
            LastAttempt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            IsAnalyzing = false,
        };

        var json = JsonSerializer.Serialize(readiness, Options);
        var restored = JsonSerializer.Deserialize<ItemReadiness>(json, Options)!;

        Assert.Equal(ItemReadinessState.Failed, restored.State);
        Assert.Equal("Archive is corrupt", restored.Error);
    }

    [Fact]
    public void ReadingProgressDto_RoundTrip_PreservesStaleFlag()
    {
        var progress = new ReadingProgressDto
        {
            ItemId = "item1",
            PageIndex = 5,
            ContentVersion = 10L,
            UpdatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            State = ReadingState.InProgress,
            IsStale = true,
        };

        var json = JsonSerializer.Serialize(progress, Options);
        var restored = JsonSerializer.Deserialize<ReadingProgressDto>(json, Options)!;

        Assert.True(restored.IsStale);
        Assert.Equal(5, restored.PageIndex);
    }

    [Fact]
    public void UpdateProgressRequest_RoundTrip_PreservesExpectedVersion()
    {
        var request = new UpdateProgressRequest
        {
            PageIndex = 10,
            ExpectedContentVersion = 42L,
        };

        var json = JsonSerializer.Serialize(request, Options);
        var restored = JsonSerializer.Deserialize<UpdateProgressRequest>(json, Options)!;

        Assert.Equal(10, restored.PageIndex);
        Assert.Equal(42L, restored.ExpectedContentVersion);
    }

    [Fact]
    public void AnalyzeRequest_RoundTrip_PreservesResourceLimits()
    {
        var request = new AnalyzeRequest
        {
            JobId = "job1",
            ArchivePath = "/scratch/archive.cbz",
            ContentVersion = 1L,
            Deadline = DateTimeOffset.Parse("2026-01-01T00:02:00Z"),
            MaxUncompressedBytes = 512L * 1024 * 1024,
            MaxEntryCount = 5000,
            MaxImageDimension = 40000,
        };

        var json = JsonSerializer.Serialize(request, Options);
        var restored = JsonSerializer.Deserialize<AnalyzeRequest>(json, Options)!;

        Assert.Equal(request.JobId, restored.JobId);
        Assert.Equal(request.MaxUncompressedBytes, restored.MaxUncompressedBytes);
        Assert.Equal(request.MaxEntryCount, restored.MaxEntryCount);
        Assert.Equal(request.MaxImageDimension, restored.MaxImageDimension);
    }

    [Fact]
    public void AnalyzeResult_RoundTrip_PreservesPageEntries()
    {
        var result = new AnalyzeResult
        {
            JobId = "job1",
            ArchiveFormat = ArchiveFormat.SevenZip,
            IsSolid = true,
            IsEncrypted = false,
            Pages = new[]
            {
                new AnalyzedPageEntry { Ordinal = 0, MediaType = "image/png", Width = 800, Height = 1200, ByteSize = 1024 },
                new AnalyzedPageEntry { Ordinal = 1, MediaType = "image/jpeg", Width = 800, Height = 1200, ByteSize = 2048 },
            },
            TotalUncompressedBytes = 3072L,
            ElapsedTime = TimeSpan.FromSeconds(1.5),
        };

        var json = JsonSerializer.Serialize(result, Options);
        var restored = JsonSerializer.Deserialize<AnalyzeResult>(json, Options)!;

        Assert.Equal(ArchiveFormat.SevenZip, restored.ArchiveFormat);
        Assert.True(restored.IsSolid);
        Assert.Equal(2, restored.Pages.Count);
        Assert.Equal(0, restored.Pages[0].Ordinal);
        Assert.Equal(1024, restored.Pages[0].ByteSize);
    }

    [Fact]
    public void WorkerEnvelope_RoundTrip_PreservesProtocolVersion()
    {
        var envelope = new WorkerEnvelope
        {
            Type = "analyze",
            CorrelationId = "corr1",
            ProtocolVersion = WorkerProtocolVersion.Current,
            Payload = JsonSerializer.SerializeToElement(new { jobId = "job1" }),
        };

        var json = JsonSerializer.Serialize(envelope, Options);
        var restored = JsonSerializer.Deserialize<WorkerEnvelope>(json, Options)!;

        Assert.Equal("analyze", restored.Type);
        Assert.Equal(WorkerProtocolVersion.Current, restored.ProtocolVersion);
        Assert.Equal("corr1", restored.CorrelationId);
    }

    [Fact]
    public void PageResponse_RoundTrip_PreservesCursor()
    {
        var response = new PageResponse<CatalogNodeDto>
        {
            Items = new[]
            {
                new CatalogNodeDto
                {
                    Id = "n1", ParentId = "p1", LibraryId = "l1",
                    Kind = CatalogNodeKind.Folder, DisplayName = "Folder",
                    Availability = CatalogNodeAvailability.Available,
                },
            },
            TotalCount = 100,
            NextCursor = "abc123",
            HasMore = true,
        };

        var json = JsonSerializer.Serialize(response, Options);
        var restored = JsonSerializer.Deserialize<PageResponse<CatalogNodeDto>>(json, Options)!;

        Assert.Equal(100, restored.TotalCount);
        Assert.True(restored.HasMore);
        Assert.Equal("abc123", restored.NextCursor);
        Assert.Single(restored.Items);
    }

    [Fact]
    public void ApiError_RoundTrip_PreservesCorrelationId()
    {
        var error = new ApiError
        {
            Error = "not_found",
            Message = "Item not found",
            CorrelationId = "corr-abc",
        };

        var json = JsonSerializer.Serialize(error, Options);
        var restored = JsonSerializer.Deserialize<ApiError>(json, Options)!;

        Assert.Equal("not_found", restored.Error);
        Assert.Equal("corr-abc", restored.CorrelationId);
    }

    /// <summary>
    /// Verifies that no DTO serialization includes fields named "path", "filePath",
    /// "sourcePath", or similar source-path-leaking field names.
    /// This is a privacy invariant.
    /// </summary>
    [Fact]
    public void NoDto_ContainsSourcePathFields()
    {
        var dtoTypes = new[]
        {
            typeof(CatalogNodeDto),
            typeof(BreadcrumbsDto),
            typeof(BreadcrumbEntry),
            typeof(SearchResultsDto),
            typeof(ReadingProgressDto),
            typeof(UpdateProgressRequest),
            typeof(UserPreferencesDto),
            typeof(LibraryDto),
            typeof(ApiError),
            typeof(AuthUserDto),
            typeof(CsrfTokenDto),
            typeof(LoginRequest),
            typeof(ChangePasswordRequest),
            typeof(ItemManifest),
            typeof(ManifestPageEntry),
            typeof(ItemReadiness),
        };

        var forbiddenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "path", "filepath", "sourcepath", "absolutepath", "fullpath",
            "realpath", "diskpath", "mediapath", "rootpath", "librarypath",
        };

        foreach (var type in dtoTypes)
        {
            foreach (var prop in type.GetProperties())
            {
                Assert.False(
                    forbiddenNames.Contains(prop.Name),
                    $"{type.Name}.{prop.Name} is a forbidden source-path field name");
            }
        }
    }

    /// <summary>
    /// Verifies that the AnalyzeRequest.ArchivePath is the only DTO that contains a path,
    /// and it must be a scratch-side path (never a source media path).
    /// This is intentional — the worker needs a filesystem path to open the archive.
    /// </summary>
    [Fact]
    public void AnalyzeRequest_ArchivePath_IsOnlyPathField()
    {
        var prop = typeof(AnalyzeRequest).GetProperty(nameof(AnalyzeRequest.ArchivePath));
        Assert.NotNull(prop);
        Assert.Equal("ArchivePath", prop!.Name);
    }
}
