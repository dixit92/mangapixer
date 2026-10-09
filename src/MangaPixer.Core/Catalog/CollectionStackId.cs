namespace com.lifepixer.mangapixer.Core.Catalog;

/// <summary>
/// The opaque id of a stack of stories collected in one volume (<c>cs.&lt;folderPublicId&gt;.&lt;key&gt;</c>, 1.37.0). Not a stored node:
/// the id only names the folder and the stack key (the record's public id) that open its view. Both parts are base36 (no dot), never a
/// path or an internal id.
/// </summary>
public static class CollectionStackId
{
    public const string Prefix = "cs.";

    public static string Encode(string folderPublicId, string key) => $"{Prefix}{folderPublicId}.{key}";

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
