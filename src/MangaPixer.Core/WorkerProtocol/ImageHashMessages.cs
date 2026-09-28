namespace com.lifepixer.mangapixer.Core.WorkerProtocol;

/// <summary>
/// Server -> Worker (protocol v4, 1.28.0): hash one image file for the automatic matcher's cover comparison.
/// The file is SERVER-OWNED - a stored cover thumbnail, or provider image bytes the server wrote to its scratch
/// area - never a source-library path. The worker decodes it (the server never decodes remote bytes), reduces it
/// to a 32x32 grayscale square and answers its 64-bit perceptual hash (<c>PerceptualHash</c>) in an
/// <c>image_hash_result</c>, or an <c>image_hash_error</c>. Nothing is written.
/// </summary>
public sealed record ImageHashRequest
{
    public required string JobId { get; init; }

    /// <summary>Server-owned image file (private locator, worker-side; never logged).</summary>
    public required string ImagePath { get; init; }

    /// <summary>Larger files are refused (<c>too_large</c>) without decoding.</summary>
    public long MaxBytes { get; init; } = ImageHashLimits.MaxBytes;

    /// <summary>Images wider or taller than this are refused (<c>too_large</c>) before decoding.</summary>
    public int MaxDimension { get; init; } = ImageHashLimits.MaxDimension;
}

/// <summary>Worker -> Server: the perceptual hash of the requested image.</summary>
public sealed record ImageHashResult
{
    public required string JobId { get; init; }
    public required ulong Hash { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

/// <summary>Worker -> Server: the image could not be hashed (<see cref="ImageHashErrors"/>).</summary>
public sealed record ImageHashError
{
    public required string JobId { get; init; }
    public required string ErrorType { get; init; }
}

/// <summary>Bounds of one <c>image_hash</c> request.</summary>
public static class ImageHashLimits
{
    /// <summary>The provider image cap of the metadata gateway (2 MiB) with headroom.</summary>
    public const long MaxBytes = 4L * 1024 * 1024;

    public const int MaxDimension = 10_000;
}

/// <summary>Error codes of <c>image_hash_error</c>.</summary>
public static class ImageHashErrors
{
    public const string Missing = "missing";
    public const string TooLarge = "too_large";
    public const string DecodeFailed = "decode_failed";
}
