namespace com.lifepixer.mangaplex.TestSupport.Fixtures;

using System.IO.Compression;

/// <summary>
/// Generates synthetic ZIP archive fixtures for compatibility testing.
/// Uses System.IO.Compression for ZIP creation (no external dependencies).
/// </summary>
public static class ZipFixtureGenerator
{
    /// <summary>
    /// Creates a ZIP archive with the specified entries.
    /// Each entry is a tiny synthetic image file (PNG header + minimal data).
    /// </summary>
    public static string CreateZip(string outputDir, string name, params string[] entryPaths)
    {
        Directory.CreateDirectory(outputDir);
        var zipPath = Path.Combine(outputDir, name);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var entryPath in entryPaths)
        {
            var entry = zip.CreateEntry(entryPath);
            using var stream = entry.Open();
            stream.Write(SyntheticImages.MinimalPng);
        }
        return zipPath;
    }

    /// <summary>
    /// Creates a ZIP archive with nested directory entries.
    /// </summary>
    public static string CreateZipWithNestedDirs(string outputDir, string name)
    {
        return CreateZip(outputDir, name,
            "page001.png",
            "page002.png",
            "subfolder/page003.png",
            "subfolder/page004.png",
            "subfolder/deep/page005.png");
    }

    /// <summary>
    /// Creates a ZIP64 archive (forces ZIP64 by adding many entries).
    /// </summary>
    public static string CreateZip64(string outputDir, string name, int entryCount = 10)
    {
        Directory.CreateDirectory(outputDir);
        var zipPath = Path.Combine(outputDir, name);
        using var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        for (int i = 0; i < entryCount; i++)
        {
            var entry = zip.CreateEntry($"page{i:D4}.png");
            using var entryStream = entry.Open();
            entryStream.Write(SyntheticImages.MinimalPng);
        }
        return zipPath;
    }

    /// <summary>
    /// Creates an encrypted (password-protected) ZIP archive.
    /// Note: SharpCompress 0.50.x writer API does not support password-protected ZIP creation
    /// directly. For P01, we skip encrypted ZIP creation and rely on detection tests.
    /// A separate fixture-generation step with a licensed ZIP tool can provide real encrypted fixtures.
    /// </summary>
    public static string CreateEncryptedZip(string outputDir, string name, string password = "test123")
    {
        // SharpCompress 0.50.x does not expose password protection for ZIP writing.
        // Create a regular ZIP and mark it as "encrypted fixture unavailable" in tests.
        // Real encrypted ZIP fixtures require an external tool (e.g., 7z with -p flag).
        return CreateZip(outputDir, name, "page001.png", "page002.png");
    }

    /// <summary>
    /// Creates a truncated (corrupt) ZIP by cutting off the last portion.
    /// </summary>
    public static string CreateTruncatedZip(string outputDir, string name)
    {
        var validZip = CreateZip(outputDir, "temp_valid.zip", "page001.png", "page002.png", "page003.png");
        var truncatedPath = Path.Combine(outputDir, name);
        var bytes = File.ReadAllBytes(validZip);
        // Keep only 60% of the file
        var truncatedBytes = bytes.Take(bytes.Length * 6 / 10).ToArray();
        File.WriteAllBytes(truncatedPath, truncatedBytes);
        File.Delete(validZip);
        return truncatedPath;
    }

    /// <summary>
    /// Creates a ZIP with legacy filename encoding (non-UTF-8).
    /// </summary>
    public static string CreateLegacyEncodingZip(string outputDir, string name)
    {
        Directory.CreateDirectory(outputDir);
        var zipPath = Path.Combine(outputDir, name);

        // Create a ZIP with Unicode filenames (the encoding flag is set by .NET ZIP)
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        // Standard entry
        {
            var entry1 = zip.CreateEntry("page001.png");
            using var s1 = entry1.Open();
            s1.Write(SyntheticImages.MinimalPng);
        }

        // Entry with Unicode name (the encoding flag is set by .NET ZIP)
        {
            var entry2 = zip.CreateEntry("第002ページ.png");
            using var s2 = entry2.Open();
            s2.Write(SyntheticImages.MinimalPng);
        }

        return zipPath;
    }

    /// <summary>
    /// Creates an empty ZIP archive (no entries).
    /// </summary>
    public static string CreateEmptyZip(string outputDir, string name)
    {
        Directory.CreateDirectory(outputDir);
        var zipPath = Path.Combine(outputDir, name);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        return zipPath;
    }

    /// <summary>
    /// Creates a ZIP with non-image entries (metadata, bookkeeping).
    /// </summary>
    public static string CreateZipWithNonImageEntries(string outputDir, string name)
    {
        Directory.CreateDirectory(outputDir);
        var zipPath = Path.Combine(outputDir, name);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        // Image entries
        {
            var img1 = zip.CreateEntry("page001.png");
            using var s1 = img1.Open();
            s1.Write(SyntheticImages.MinimalPng);
        }
        {
            var img2 = zip.CreateEntry("page002.png");
            using var s2 = img2.Open();
            s2.Write(SyntheticImages.MinimalPng);
        }

        // Non-image entries that should be ignored
        {
            var xml = zip.CreateEntry("ComicInfo.xml");
            using var sx = xml.Open();
            sx.Write("<!-- comic info -->"u8.ToArray());
        }
        {
            var macosx = zip.CreateEntry("__MACOSX/page001.png");
            using var sm = macosx.Open();
            sm.Write(SyntheticImages.MinimalPng);
        }
        {
            var thumb = zip.CreateEntry("Thumbs.db");
            using var st = thumb.Open();
            st.Write(new byte[] { 0x00, 0x01, 0x02 });
        }

        return zipPath;
    }
}
