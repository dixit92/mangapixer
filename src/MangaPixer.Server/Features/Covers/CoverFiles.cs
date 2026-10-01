namespace com.lifepixer.mangapixer.Server.Features.Covers;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;

/// <summary>
/// Where the cover layer's own files live in the DATA root (1.29.0) - server-owned, never a source path, never derived
/// from client input (the worker's <c>CoverRenderRequest.OutputPath</c> is always one of these):
/// <list type="bullet">
/// <item><c>cover-crops/&lt;shard&gt;/&lt;nodeBase36&gt;-&lt;contentVersion&gt;-&lt;side&gt;.webp</c> - the front / back half of a
/// jacket spread on an archive's page 1 (the thumbnail variant: WebP, longest edge 400);</item>
/// <item><c>volume-covers/&lt;shard&gt;/&lt;publicId&gt;-&lt;storedVersion&gt;.webp</c> - a stored web cover (written by the
/// volume-cover download; read here).</item>
/// </list>
/// File names carry opaque ids and versions only (privacy invariant).
/// </summary>
public sealed class CoverFiles
{
    public const string CropsFolder = "cover-crops";
    public const string VolumeCoversFolder = "volume-covers";

    public CoverFiles(string dataRoot)
    {
        var root = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        CropsRoot = Path.Combine(root, CropsFolder);
        VolumeCoversRoot = Path.Combine(root, VolumeCoversFolder);
    }

    public string CropsRoot { get; }
    public string VolumeCoversRoot { get; }

    /// <summary>The crop file of an archive's page 1 half at a content version. Does not check existence.</summary>
    public string CropPath(long archiveNodeId, long contentVersion, CoverCropSide side)
    {
        var id = OpaqueId.Encode(archiveNodeId);
        return Path.Combine(CropsRoot, Shard(id),
            string.Create(CultureInfo.InvariantCulture, $"{id}-{contentVersion}-{(side == CoverCropSide.Left ? "l" : "r")}.webp"));
    }

    /// <summary>A stored web cover's file (<c>volume_covers.PublicId</c> + <c>StoredVersion</c>). Does not check existence.</summary>
    public string VolumeCoverPath(string publicId, int storedVersion)
    {
        // "vc" + hex: the two characters after the prefix spread the files evenly.
        var shard = publicId.Length >= 4 ? publicId[2..4] : "00";
        return Path.Combine(VolumeCoversRoot, shard,
            string.Create(CultureInfo.InvariantCulture, $"{publicId}-{storedVersion}.webp"));
    }

    /// <summary>Deletes every crop of an archive except those of <paramref name="keepContentVersion"/> (best effort).</summary>
    public void DeleteCrops(long archiveNodeId, long? keepContentVersion = null)
    {
        var id = OpaqueId.Encode(archiveNodeId);
        var dir = Path.Combine(CropsRoot, Shard(id));
        if (!Directory.Exists(dir))
            return;
        var keep = keepContentVersion is { } v ? string.Create(CultureInfo.InvariantCulture, $"{id}-{v}-") : null;
        foreach (var file in Directory.EnumerateFiles(dir, id + "-*.webp"))
        {
            if (keep is not null && Path.GetFileName(file).StartsWith(keep, StringComparison.Ordinal))
                continue;
            TryDelete(file);
        }
    }

    /// <summary>Every crop file of an archive, at any content version (1.31.0: the trash measures and deletes them).</summary>
    public IReadOnlyList<string> CropFilesOf(long archiveNodeId)
    {
        var id = OpaqueId.Encode(archiveNodeId);
        return ParseCrops(Trash.DataRootFileNames.Enumerate(Path.Combine(CropsRoot, Shard(id)), id + "-*.webp", SearchOption.TopDirectoryOnly))
            .Where(c => c.ArchiveNodeId == archiveNodeId)
            .Select(c => c.Path)
            .ToList();
    }

    /// <summary>
    /// Every crop file whose name is exactly one <see cref="CropPath"/> writes, with the key parsed from it (1.31.0, Clean
    /// bundles). Other files are not listed.
    /// </summary>
    public IEnumerable<StoredCrop> EnumerateCrops() =>
        ParseCrops(Trash.DataRootFileNames.Enumerate(CropsRoot, "*.webp", SearchOption.AllDirectories));

    private IEnumerable<StoredCrop> ParseCrops(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var name = Path.GetFileNameWithoutExtension(path.AsSpan());
            if (name.Length < 5 || name[^2] != '-' || name[^1] is not ('l' or 'r'))
                continue;
            var side = name[^1] == 'l' ? CoverCropSide.Left : CoverCropSide.Right;
            var rest = name[..^2];
            var dash = rest.LastIndexOf('-');
            if (dash <= 0
                || !Trash.DataRootFileNames.TryParseOpaqueId(rest[..dash], out var archiveNodeId)
                || !Trash.DataRootFileNames.TryParseNumber(rest[(dash + 1)..], out var contentVersion)
                || !string.Equals(CropPath(archiveNodeId, contentVersion, side), path, StringComparison.Ordinal))
                continue;
            yield return new StoredCrop(path, archiveNodeId, contentVersion);
        }
    }

    /// <summary>Opens a file for reading, or null when it does not exist.</summary>
    public static Stream? OpenRead(string path)
    {
        try
        {
            return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Moves a rendered file into place atomically (the store's temp + rename pattern).</summary>
    public static void Publish(string renderedPath, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(renderedPath, destination, overwrite: true);
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static string Shard(string idBase36) => idBase36.Length >= 2 ? idBase36[..2] : "00";
}

/// <summary>A cover crop file and the archive / content version its name carries.</summary>
public readonly record struct StoredCrop(string Path, long ArchiveNodeId, long ContentVersion);
