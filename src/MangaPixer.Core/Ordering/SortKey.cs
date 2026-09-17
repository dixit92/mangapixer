namespace com.lifepixer.mangapixer.Core.Ordering;

using com.lifepixer.mangapixer.Core.Catalog;
using System.Globalization;
using System.Text;

/// <summary>
/// Persisted sort key for catalog nodes. Encodes the natural-order comparison
/// into a string that can be stored in SQLite and compared with plain BINARY
/// collation. This avoids needing a custom collation function for keyset
/// pagination: <c>ORDER BY SortKey</c> (ordinal) IS natural order.
///
/// Format:
/// - Folders sort before archives at the same level (prefix '0' vs '1').
/// - Within the name, runs of digits are encoded so they compare numerically,
///   and every other character is copied verbatim so it compares ordinally.
///
/// The key is per-node, not hierarchical: it orders a node against its SIBLINGS,
/// which is what every ordering query does (all of them filter by ParentId, and
/// the covering index is (ParentId, Kind, SortKey)). A hierarchical key that
/// embedded the parent chain would have to be recomputed for a whole subtree
/// whenever a folder is renamed or moved - which the scanner does not do - so it
/// would silently go stale.
///
/// This encoding is persisted to SQLite and used for keyset pagination and
/// indexing, so it is a stable on-disk format - changing it requires a data
/// migration, not just a code change (see BackfillNaturalSortKeys).
/// </summary>
public static class SortKey
{
    private const string FolderPrefix = "0";
    private const string ArchivePrefix = "1";

    /// <summary>
    /// Marker that opens an encoded digit run.
    ///
    /// It must be a character that occupies the same ordinal neighbourhood as the
    /// digits it stands in for, because <see cref="NaturalOrderComparer"/> compares a
    /// digit against a non-digit ORDINALLY (it only switches to numeric comparison when
    /// BOTH sides are digits). Every ASCII digit lives in the single gap U+0030..U+0039,
    /// above all ASCII punctuation and below every letter, so one fixed marker inside
    /// that gap reproduces digit-vs-text ordering for all of them. '0' is that marker.
    ///
    /// A marker outside the gap (the original 'D') silently moved every digit-leading
    /// name behind the letters A-C: "90s Classics" sorted after "Akira".
    /// </summary>
    private const char DigitRunMarker = '0';

    /// <summary>
    /// Terminates the significant digits of an encoded run and introduces the
    /// leading-zero tie-breaker. Purely structural: two encoded runs always reach
    /// this character at the same offset (see <see cref="EncodeName"/>), so it is
    /// only ever compared against itself.
    /// </summary>
    private const char DigitRunTerminator = 'Z';

    /// <summary>Widest leading-zero count the tie-breaker distinguishes.</summary>
    private const int MaxLeadingZeros = 99;

    /// <summary>
    /// Builds the persisted sort key for a catalog node: the kind prefix (folders
    /// before archives) followed by the encoded display name. This is the single
    /// production encoder - the scanner stores exactly this, and the migration
    /// backfills exactly this.
    /// </summary>
    public static string ForNode(CatalogNodeKind kind, string displayName)
    {
        var prefix = kind == CatalogNodeKind.Folder ? FolderPrefix : ArchivePrefix;
        return prefix + EncodeName(displayName);
    }

    /// <summary>
    /// Encodes a display name into a string whose ORDINAL order is natural order,
    /// matching <see cref="NaturalOrderComparer"/>:
    /// - Non-digit characters are copied verbatim, so they compare ordinally
    ///   (uppercase before lowercase, matching ASCII).
    /// - A digit run is encoded as marker + length-of-length + length + significant
    ///   digits + terminator + inverted leading-zero count. Comparing two encoded runs
    ///   therefore compares significant LENGTH first (2 &lt; 10), then the digits
    ///   themselves, then leading zeros with more zeros first (001 &lt; 01 &lt; 1) -
    ///   exactly the comparer's rules.
    ///
    /// The length field is self-delimiting (one character giving how many digits the
    /// length itself has, then the length in decimal) so runs of ANY length compare
    /// correctly; a fixed two-digit width silently mis-ordered runs of 100+ digits.
    ///
    /// Known deviation: <see cref="NaturalOrderComparer"/> treats every Unicode digit
    /// (<c>char.IsDigit</c>) as numeric, so a fullwidth or Arabic-Indic digit compares
    /// numerically against an ASCII digit but ORDINALLY against a letter - which is not a
    /// transitive order, and therefore cannot be reproduced by ANY key encoding. Writing
    /// U+FF11 (fullwidth one) as F: "10" &gt; F because a two-digit run beats a one-digit
    /// run, F &gt; "A" because U+FF11 &gt; 'A' ordinally, and "A" &gt; "10" because 'A' &gt;
    /// '1' ordinally - a cycle. (Two non-ASCII digits of the SAME run length stay
    /// consistent; it takes a length difference to close the loop.) For those non-ASCII
    /// digits the encoded key groups the name with the numerics. ASCII digit runs - every
    /// realistic chapter/volume name - match the comparer exactly.
    /// </summary>
    public static string EncodeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;

        var sb = new StringBuilder(name.Length * 2);
        int i = 0;
        while (i < name.Length)
        {
            if (!char.IsDigit(name[i]))
            {
                // Text character: append as-is (ordinal comparison, matching NaturalOrderComparer).
                sb.Append(name[i]);
                i++;
                continue;
            }

            // Extract the digit run.
            int start = i;
            while (i < name.Length && char.IsDigit(name[i])) i++;
            var digits = name.AsSpan(start, i - start);

            // Split into leading zeros and significant digits (keep at least one digit,
            // so "000" is the value 0 with two leading zeros).
            var sigStart = 0;
            while (sigStart < digits.Length - 1 && digits[sigStart] == '0') sigStart++;
            var sigDigits = digits[sigStart..];
            int leadingZeros = sigStart;

            sb.Append(DigitRunMarker);
            AppendSelfDelimitingLength(sb, sigDigits.Length);
            sb.Append(sigDigits);
            sb.Append(DigitRunTerminator);

            // Inverted count: more leading zeros -> smaller field -> sorts first.
            // Clamped, so a pathological run of 100+ zeros ties rather than wrapping
            // past '0' into control characters and inverting the order.
            var inverted = MaxLeadingZeros - Math.Min(leadingZeros, MaxLeadingZeros);
            sb.Append(inverted.ToString("D2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Appends <paramref name="length"/> so that longer lengths always sort after
    /// shorter ones and the field is self-delimiting: one character holding the number
    /// of decimal digits, then the decimal digits. 9 -> "19", 10 -> "210", 100 -> "3100";
    /// "19" &lt; "210" &lt; "3100" ordinally. An int has at most 10 decimal digits, so the
    /// leading character stays within '1'..':' and remains monotonic.
    /// </summary>
    private static void AppendSelfDelimitingLength(StringBuilder sb, int length)
    {
        var text = length.ToString(CultureInfo.InvariantCulture);
        sb.Append((char)('0' + text.Length));
        sb.Append(text);
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
