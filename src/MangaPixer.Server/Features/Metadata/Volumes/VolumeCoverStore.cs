namespace com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;

using System.Text.RegularExpressions;

/// <summary>
/// The stored web volume covers (1.29.0): <c>DataRoot/volume-covers/&lt;shard&gt;/&lt;publicId&gt;-&lt;version&gt;.webp</c>, written by
/// the media worker (<c>cover_render</c>) from provider bytes the server put in scratch. Every path is built here from a
/// validated public id and a version number - never from client input or provider data. Files outlive the provider's
/// allowlist entry (serving local data is not a request); "Delete stored volume covers" removes them.
/// </summary>
public sealed partial class VolumeCoverStore
{
    public const string FolderName = "volume-covers";

    public VolumeCoverStore(string root)
    {
        Root = root;
    }

    public string Root { get; }

    [GeneratedRegex("^vc[0-9a-f]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex PublicIdPattern();

    public static bool IsValidPublicId(string? publicId) => publicId is not null && PublicIdPattern().IsMatch(publicId);

    /// <summary>The file of one stored version of a cover.</summary>
    public string PathFor(string publicId, int version)
    {
        if (!IsValidPublicId(publicId) || version < 1)
            throw new ArgumentException("Invalid volume cover id or version.");
        return Path.Combine(Root, publicId.Substring(2, 2), $"{publicId}-{version}.webp");
    }

    /// <summary>Deletes one stored version (best effort; a missing file is fine).</summary>
    public void Delete(string publicId, int version)
    {
        if (!IsValidPublicId(publicId) || version < 1)
            return;
        try
        {
            File.Delete(PathFor(publicId, version));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Deletes every stored cover file; returns how many files were removed.</summary>
    public int DeleteAll()
    {
        if (!Directory.Exists(Root))
            return 0;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(Root, "vc*.webp", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return removed;
    }
}
