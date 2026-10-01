namespace com.lifepixer.mangapixer.Server.Features.Trash;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Catalog;

/// <summary>
/// Strict parsing of the id-named files the data-root stores write (1.31.0, Clean bundles). A name is recognised only when it
/// is exactly what the store would write for the parsed key, so a file the store did not write is never taken for one of its
/// own (and never deleted).
/// </summary>
public static class DataRootFileNames
{
    /// <summary>Parses an <see cref="OpaqueId"/> as the stores write it: lowercase base36, positive, no leading zero.</summary>
    public static bool TryParseOpaqueId(ReadOnlySpan<char> text, out long id)
    {
        id = 0;
        if (text.Length is 0 or > 13 || text[0] == '0')
            return false;
        foreach (var c in text)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'z')))
                return false;
        }
        long value;
        try
        {
            value = OpaqueId.Decode(text.ToString());
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (value <= 0 || !string.Equals(OpaqueId.Encode(value), text.ToString(), StringComparison.Ordinal))
            return false;
        id = value;
        return true;
    }

    /// <summary>Parses a non-negative decimal number as the stores write it (invariant, no sign, no leading zero).</summary>
    public static bool TryParseNumber(ReadOnlySpan<char> text, out long value)
    {
        value = 0;
        if (text.Length is 0 or > 18 || (text.Length > 1 && text[0] == '0'))
            return false;
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
                return false;
        }
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Every file under <paramref name="root"/> matching <paramref name="pattern"/>, read at once; nothing when the folder is
    /// missing or cannot be read (Clean bundles then simply leaves that store alone).
    /// </summary>
    public static IReadOnlyList<string> Enumerate(string root, string pattern, SearchOption option)
    {
        if (!Directory.Exists(root))
            return [];
        try
        {
            return Directory.EnumerateFiles(root, pattern, option).ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
