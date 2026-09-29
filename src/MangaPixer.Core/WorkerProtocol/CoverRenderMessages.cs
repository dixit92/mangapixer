namespace com.lifepixer.mangapixer.Core.WorkerProtocol;

/// <summary>
/// Server -> Worker (protocol v5, 1.29.0): render one cover thumbnail - from an archive page (the local spread crop) or
/// from a server-owned image file (provider cover bytes the server wrote to scratch) - optionally cropped to one half,
/// downscaled to the thumbnail variant (WebP, longest edge <see cref="MaxDimension"/>) and hashed in the same decode.
/// The worker is the only process that decodes images: the server never decodes provider bytes or archive pages.
/// Answered by a <c>cover_render_result</c> or a <c>cover_render_error</c>.
/// </summary>
public sealed record CoverRenderRequest
{
    public required string JobId { get; init; }

    /// <summary><see cref="CoverRenderSources.Archive"/> or <see cref="CoverRenderSources.Image"/>.</summary>
    public required string Source { get; init; }

    /// <summary>Source "archive": the archive (private validated source locator, worker-side; never logged).</summary>
    public string? ArchivePath { get; init; }

    /// <summary>Source "archive": the page entry key (as <see cref="ExtractRequest.SourceEntryKey"/>).</summary>
    public string? SourceEntryKey { get; init; }

    /// <summary>Source "archive": the expected last-write ticks - a changed source is refused (<c>source_changed</c>).</summary>
    public long ExpectedLastWriteTicks { get; init; }

    /// <summary>Source "archive": the expected byte length.</summary>
    public long ExpectedByteLength { get; init; }

    /// <summary>
    /// Source "image": a SERVER-OWNED file (provider image bytes in scratch; never a source path). Only JPEG, PNG, GIF and
    /// WebP are decoded (magic bytes first, as <c>image_hash</c>).
    /// </summary>
    public string? ImagePath { get; init; }

    /// <summary><see cref="CoverCropSides"/>: "none" (default), "left" or "right".</summary>
    public string CropSide { get; init; } = CoverCropSides.None;

    /// <summary>
    /// Width / height of the cropped cover. The crop keeps the OUTER edge of the chosen half (a jacket reads back / spine /
    /// front for left-to-right books, front / spine / back for right-to-left) and is <c>min(width / 2, height x aspect)</c>
    /// wide, so a visible spine is dropped. Values outside (0, 1] mean <see cref="CoverRenderLimits.DefaultCropAspect"/>.
    /// </summary>
    public double CropAspect { get; init; } = CoverRenderLimits.DefaultCropAspect;

    /// <summary>Server-owned path to write the WebP thumbnail to.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Longest edge (px) of the output; the worker never upscales.</summary>
    public int MaxDimension { get; init; } = CoverRenderLimits.DefaultMaxDimension;

    /// <summary>WebP quality (1-100).</summary>
    public int WebpQuality { get; init; } = CoverRenderLimits.DefaultWebpQuality;

    /// <summary>Also answer the 64-bit perceptual hash of the written thumbnail (as <c>image_hash</c> would of that file).</summary>
    public bool ComputeHash { get; init; } = true;

    /// <summary>Source "image": larger files are refused (<c>too_large</c>) without decoding.</summary>
    public long MaxBytes { get; init; } = CoverRenderLimits.MaxImageBytes;

    /// <summary>Inputs wider or taller than this are refused (<c>too_large</c>) before decoding.</summary>
    public int MaxInputDimension { get; init; } = CoverRenderLimits.MaxInputDimension;
}

/// <summary>Worker -> Server: the rendered thumbnail is at <see cref="OutputPath"/>.</summary>
public sealed record CoverRenderResult
{
    public required string JobId { get; init; }
    public required string OutputPath { get; init; }

    /// <summary>Size of the written thumbnail.</summary>
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Size of the decoded source (before crop and resize).</summary>
    public required int SourceWidth { get; init; }

    public required int SourceHeight { get; init; }

    /// <summary>The thumbnail's 64-bit perceptual hash (<c>CoverHash</c>), or null when not requested.</summary>
    public ulong? Hash { get; init; }
}

/// <summary>Worker -> Server: the cover could not be rendered (<see cref="CoverRenderErrors"/>).</summary>
public sealed record CoverRenderError
{
    public required string JobId { get; init; }
    public required string ErrorType { get; init; }
}

/// <summary>Values of <see cref="CoverRenderRequest.Source"/>.</summary>
public static class CoverRenderSources
{
    public const string Archive = "archive";
    public const string Image = "image";
}

/// <summary>Values of <see cref="CoverRenderRequest.CropSide"/>.</summary>
public static class CoverCropSides
{
    public const string None = "none";
    public const string Left = "left";
    public const string Right = "right";
}

/// <summary>Bounds and defaults of one <c>cover_render</c> request.</summary>
public static class CoverRenderLimits
{
    /// <summary>The durable thumbnail's longest edge (<c>ThumbnailGenerationService</c>).</summary>
    public const int DefaultMaxDimension = 400;

    public const int DefaultWebpQuality = 80;

    /// <summary>A printed cover's width / height (tankobon ~0.70); drops the spine of a jacket spread.</summary>
    public const double DefaultCropAspect = 0.70;

    /// <summary>The provider image cap of the metadata gateway (2 MiB) with headroom, as <see cref="ImageHashLimits.MaxBytes"/>.</summary>
    public const long MaxImageBytes = 4L * 1024 * 1024;

    public const int MaxInputDimension = 10_000;
}

/// <summary>Error codes of <c>cover_render_error</c>.</summary>
public static class CoverRenderErrors
{
    /// <summary>The image file (or the archive) is missing.</summary>
    public const string Missing = "missing";

    public const string TooLarge = "too_large";
    public const string DecodeFailed = "decode_failed";

    /// <summary>The archive changed since the server read its stamp.</summary>
    public const string SourceChanged = "source_changed";

    public const string PageNotFound = "page_not_found";
    public const string Encrypted = "encrypted";
    public const string UnsupportedSolid = "unsupported_solid";

    /// <summary>The request is malformed (unknown source, missing locator, unknown crop side).</summary>
    public const string InvalidRequest = "invalid_request";

    public const string EncodeFailed = "encode_failed";
}
