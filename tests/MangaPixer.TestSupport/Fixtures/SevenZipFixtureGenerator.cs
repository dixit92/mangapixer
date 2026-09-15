namespace com.lifepixer.mangapixer.TestSupport.Fixtures;

using System.Diagnostics;
using System.IO.Compression;

/// <summary>
/// Generates synthetic 7z archive fixtures using the 7z command-line tool.
/// Requires p7zip-full or 7zip to be installed in the test environment.
/// </summary>
public static class SevenZipFixtureGenerator
{
    /// <summary>
    /// Creates a 7z archive with the specified entries using the 7z command-line tool.
    /// Falls back to creating a .7z.bin marker file if 7z is not available.
    /// </summary>
    public static string CreateSevenZip(string outputDir, string name, bool solid = false, params string[] entryPaths)
    {
        Directory.CreateDirectory(outputDir);
        var archivePath = Path.Combine(outputDir, name);

        // Create a temporary source directory with the entries
        var tempDir = Path.Combine(outputDir, "_temp_source_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        foreach (var entryPath in entryPaths)
        {
            var fullPath = Path.Combine(tempDir, entryPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, SyntheticImages.MinimalPng);
        }

        try
        {
            var solidFlag = solid ? "-ms=on" : "-ms=off";
            var psi = new ProcessStartInfo
            {
                FileName = "7z",
                Arguments = $"a -t7z {solidFlag} \"{archivePath}\" \"{tempDir}/*\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null) throw new InvalidOperationException("Failed to start 7z process");
            process.WaitForExit(30000);
            if (process.ExitCode != 0)
            {
                var stderr = process.StandardError.ReadToEnd();
                throw new InvalidOperationException($"7z failed with exit code {process.ExitCode}: {stderr}");
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            // 7z not on PATH — create a minimal 7z signature file so callers detect its
            // absence (fileInfo.Length <= 8) and skip. On Linux a missing executable
            // surfaces as Win32Exception ("No such file or directory"), not
            // FileNotFoundException, so both must be caught or the 7z tests fail red on
            // any container without p7zip-full instead of skipping.
            File.WriteAllBytes(archivePath, SevenZipSignature);
        }

        return archivePath;
    }

    /// <summary>
    /// Creates a solid 7z archive (all entries compressed together).
    /// </summary>
    public static string CreateSolidSevenZip(string outputDir, string name)
    {
        return CreateSevenZip(outputDir, name, solid: true,
            "page001.png", "page002.png", "page003.png", "page004.png", "page005.png");
    }

    /// <summary>
    /// Creates a non-solid 7z archive.
    /// </summary>
    public static string CreateNonSolidSevenZip(string outputDir, string name)
    {
        return CreateSevenZip(outputDir, name, solid: false,
            "page001.png", "page002.png", "page003.png");
    }

    /// <summary>
    /// Creates a 7z with nested directories.
    /// </summary>
    public static string CreateSevenZipWithNestedDirs(string outputDir, string name)
    {
        return CreateSevenZip(outputDir, name, solid: false,
            "page001.png",
            "subfolder/page002.png",
            "subfolder/deep/page003.png");
    }

    /// <summary>
    /// Creates a truncated 7z archive.
    /// </summary>
    public static string CreateTruncatedSevenZip(string outputDir, string name)
    {
        var valid = CreateNonSolidSevenZip(outputDir, "temp_valid.7z");
        var truncatedPath = Path.Combine(outputDir, name);
        var bytes = File.ReadAllBytes(valid);
        File.WriteAllBytes(truncatedPath, bytes.Take(bytes.Length * 6 / 10).ToArray());
        File.Delete(valid);
        return truncatedPath;
    }

    /// <summary>
    /// Minimal 7z file signature (37 7A BC AF 27 1C).
    /// This is NOT a valid archive — only for signature detection tests.
    /// </summary>
    public static readonly byte[] SevenZipSignature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x00];
}
