namespace com.lifepixer.mangapixer.Core.Media;

/// <summary>
/// Detected archive format from file signature verification.
/// </summary>
public enum ArchiveFormat
{
    Unknown = 0,
    Zip = 1,    // .cbz / .zip
    Rar4 = 2,   // .cbr / .rar (RAR 1.5-4)
    Rar5 = 3,   // .cbr / .rar (RAR 5+)
    SevenZip = 4 // .cb7 / .7z
}

/// <summary>
/// Supported embedded image media types in release one.
/// </summary>
public static class ImageMediaTypes
{
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string WebP = "image/webp";
    public const string Avif = "image/avif";
    public const string Gif = "image/gif";
    public const string Bmp = "image/bmp";
    public const string Tiff = "image/tiff";
}

/// <summary>
/// Animation state of an image entry.
/// </summary>
public enum AnimationState
{
    NotAnimated = 0,
    Animated = 1,
    AnimationError = 2,
    Unknown = 3
}
