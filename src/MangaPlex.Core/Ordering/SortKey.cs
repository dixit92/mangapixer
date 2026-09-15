namespace com.lifepixer.mangaplex.Core.Ordering;

using com.lifepixer.mangaplex.Core.Catalog;
using System.Globalization;
using System.Text;

/// <summary>
/// Persisted sort key for catalog nodes. Encodes the natural-order comparison
/// into a fixed-width string that can be stored in SQLite and compared with
/// plain BINARY collation. This avoids needing a custom collation function
/// for keyset pagination.
///
/// Format per name segment:
/// - Digit runs are zero-padded to 19 digits (max int64 range) and prefixed with length.
/// - Text characters are compared ordinally.
/// - Folders sort before archives at the same level (prefix '0' vs '1').
/// - The full key is a concatenation of segment keys joined by '\x1F' (unit separator).
///
/// This encoding is persisted to SQLite and used for keyset pagination and
/// indexing, so it is a stable on-disk format — changing it requires a data
/// migration, not just a code change.
/// </summary>
public static class SortKey
{
    private const char SegmentSeparator = '\x1F';
    private const string FolderPrefix = "0";
    private const string ArchivePrefix = "1";
    private const int MaxDigitWidth = 19; // int64.MaxValue has 19 digits

    /// <summary>
    /// Builds a persisted sort key for a catalog node.
    /// </summary>
    public static string ForNode(CatalogNodeKind kind, string displayName, string parentKey)
    {
        var prefix = kind == CatalogNodeKind.Folder ? FolderPrefix : ArchivePrefix;
        var nameKey = EncodeName(displayName);
        return parentKey + SegmentSeparator + prefix + nameKey;
    }

    /// <summary>
    /// Builds the root sort key for a library's synthetic root folder.
    /// </summary>
    public static string ForLibraryRoot() => FolderPrefix + EncodeName(string.Empty);

    /// <summary>
    /// Encodes a display name into a sortable string segment.
    /// The encoding produces the same ordering as <see cref="NaturalOrderComparer"/>.
    /// - Text characters are compared ordinally (uppercase before lowercase, matching ASCII).
    /// - Digit runs are encoded by significant length then significant digits,
    ///   with an inverted leading-zero count as tie-breaker (more zeros sort first).
    /// </summary>
    public static string EncodeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;

        var sb = new StringBuilder(name.Length * 2);
        int i = 0;
        while (i < name.Length)
        {
            if (char.IsDigit(name[i]))
            {
                // Extract digit run
                int start = i;
                while (i < name.Length && char.IsDigit(name[i])) i++;
                var digits = name.AsSpan(start, i - start);

                // Strip leading zeros for the numeric value
                var sigStart = 0;
                while (sigStart < digits.Length - 1 && digits[sigStart] == '0') sigStart++;
                var sigDigits = digits[sigStart..];
                int leadingZeros = sigStart;

                // Encoding: 'D' + 2-digit significant length + significant digits
                // Then 'Z' + inverted leading-zero count (9-zeros, so more zeros = lower = sorts first)
                // This matches NaturalOrderComparer: 001 < 01 < 1
                sb.Append('D');
                sb.Append(sigDigits.Length.ToString("D2", CultureInfo.InvariantCulture));
                sb.Append(sigDigits.ToString());
                sb.Append('Z');
                sb.Append((char)('9' - leadingZeros));
            }
            else
            {
                // Text character: append as-is (ordinal comparison, matching NaturalOrderComparer)
                sb.Append(name[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Compares two sort keys using ordinal string comparison.
    /// This is the same as SQLite BINARY collation.
    /// </summary>
    public static int Compare(string? x, string? y)
    {
        return string.CompareOrdinal(x, y);
    }
}
