namespace com.lifepixer.mangapixer.Core.Catalog;

/// <summary>
/// The opaque id of a virtual volume stack browse entry (<c>vs.&lt;folderPublicId&gt;.&lt;key&gt;</c>, 1.29.0). A stack is not a
/// stored node: its id only names the folder and the volume key that open its view. Folder public ids are base36 (no dot);
/// the key may carry one (<c>2.5</c>).
/// </summary>
public static class VolumeStackId
{
    public const string Prefix = "vs.";

    /// <summary>The id prefix of a missing-volume placeholder entry (1.29.0 RC): never opened.</summary>
    public const string MissingPrefix = "vm.";

    public static string Encode(string folderPublicId, string key) => $"{Prefix}{folderPublicId}.{key}";

    public static string EncodeMissing(string folderPublicId, string key) => $"{MissingPrefix}{folderPublicId}.{key}";

    /// <summary>Splits a stack id; false when the text is not one.</summary>
    public static bool TryDecode(string? id, out string folderPublicId, out string key)
    {
        folderPublicId = key = string.Empty;
        if (id is null || !id.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        var rest = id[Prefix.Length..];
        var dot = rest.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == rest.Length - 1)
            return false;
        folderPublicId = rest[..dot];
        key = rest[(dot + 1)..];
        return true;
    }
}
