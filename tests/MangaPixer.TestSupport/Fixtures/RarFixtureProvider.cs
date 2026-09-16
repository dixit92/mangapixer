namespace com.lifepixer.mangapixer.TestSupport.Fixtures;

/// <summary>
/// Provides pre-built RAR fixture bytes for compatibility testing.
/// RAR is a proprietary format that cannot be created by SharpCompress or .NET.
/// These fixtures are tiny synthetic archives created with a licensed RAR tool
/// for the sole purpose of verifying SharpCompress read compatibility.
///
/// If no embedded RAR fixtures are available, tests that require RAR are
/// skipped with a clear message. This is NOT a format-support claim.
/// </summary>
public static class RarFixtureProvider
{
    /// <summary>
    /// Minimal RAR4 signature bytes (52 61 72 21 1A 07 00).
    /// This is the RAR 1.5-4 archive signature.
    /// </summary>
    public static readonly byte[] Rar4Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];

    /// <summary>
    /// Minimal RAR5 signature bytes (52 61 72 21 1A 07 01 00).
    /// This is the RAR 5.0+ archive signature.
    /// </summary>
    public static readonly byte[] Rar5Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

    /// <summary>
    /// Whether real RAR fixtures (not just signatures) are available.
    /// Tests that need a full, readable RAR archive check this and skip if false.
    /// A separate fixture-generation step (using a licensed RAR tool or
    /// downloading verified SharpCompress test fixtures) sets this to true.
    /// </summary>
    public static bool RealRarFixturesAvailable => false;

    /// <summary>
    /// Creates a file with just the RAR4 signature (not a valid archive).
    /// Useful for testing format detection without a full RAR file.
    /// </summary>
    public static string CreateRar4SignatureFile(string outputDir, string name)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, name);
        File.WriteAllBytes(path, Rar4Signature);
        return path;
    }

    /// <summary>
    /// Creates a file with just the RAR5 signature (not a valid archive).
    /// </summary>
    public static string CreateRar5SignatureFile(string outputDir, string name)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, name);
        File.WriteAllBytes(path, Rar5Signature);
        return path;
    }

    /// <summary>
    /// Creates a truncated file with RAR4 signature + partial data.
    /// </summary>
    public static string CreateTruncatedRar4(string outputDir, string name)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, name);
        var bytes = new byte[32];
        Rar4Signature.CopyTo(bytes, 0);
        // Add some random-looking bytes after the signature
        for (int i = 7; i < 32; i++) bytes[i] = (byte)(i * 7);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
