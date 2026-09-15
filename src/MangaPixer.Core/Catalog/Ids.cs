namespace com.lifepixer.mangapixer.Core.Catalog;

using System.Globalization;

/// <summary>
/// Opaque identifier for a library. Serialized as a string in API DTOs and worker protocol.
/// Internally a 64-bit integer; the string form is base36 to keep URLs short.
/// </summary>
public readonly record struct LibraryId(long Value) : IComparable<LibraryId>
{
    public string ToOpaque() => OpaqueId.Encode(Value);
    public static LibraryId FromOpaque(string opaque) => new(OpaqueId.Decode(opaque));
    public int CompareTo(LibraryId other) => Value.CompareTo(other.Value);
    public override string ToString() => ToOpaque();
}

/// <summary>
/// Opaque identifier for a catalog node (folder or archive).
/// </summary>
public readonly record struct CatalogNodeId(long Value) : IComparable<CatalogNodeId>
{
    public string ToOpaque() => OpaqueId.Encode(Value);
    public static CatalogNodeId FromOpaque(string opaque) => new(OpaqueId.Decode(opaque));
    public int CompareTo(CatalogNodeId other) => Value.CompareTo(other.Value);
    public override string ToString() => ToOpaque();
}

/// <summary>
/// Opaque identifier for a readable item (an archive that has been analyzed).
/// Distinct from CatalogNodeId because not all nodes are readable items.
/// </summary>
public readonly record struct ItemId(long Value) : IComparable<ItemId>
{
    public string ToOpaque() => OpaqueId.Encode(Value);
    public static ItemId FromOpaque(string opaque) => new(OpaqueId.Decode(opaque));
    public int CompareTo(ItemId other) => Value.CompareTo(other.Value);
    public override string ToString() => ToOpaque();
}

/// <summary>
/// Opaque identifier for a user account.
/// </summary>
public readonly record struct UserId(long Value) : IComparable<UserId>
{
    public string ToOpaque() => OpaqueId.Encode(Value);
    public static UserId FromOpaque(string opaque) => new(OpaqueId.Decode(opaque));
    public int CompareTo(UserId other) => Value.CompareTo(other.Value);
    public override string ToString() => ToOpaque();
}

/// <summary>
/// Zero-based page index within an item's manifest.
/// Page 0 is the first page. Negative values are invalid.
/// </summary>
public readonly record struct PageIndex(int Value)
{
    public bool IsValid => Value >= 0;
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Opaque entry key for a single page within an archive.
/// Maps to an entry path in the source archive but is never the raw path.
/// Internally an ordinal; the string form is the ordinal in base36.
/// </summary>
public readonly record struct PageEntryKey(int Ordinal)
{
    public string ToOpaque() => OpaqueId.Encode(Ordinal);
    public static PageEntryKey FromOpaque(string opaque) => new((int)OpaqueId.Decode(opaque));
    public override string ToString() => ToOpaque();
}

/// <summary>
/// Encoding/decoding for opaque ID strings using base36.
/// Keeps URL paths short while remaining case-insensitive and URL-safe.
/// </summary>
public static class OpaqueId
{
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
    private const int Base = 36;

    public static string Encode(long value)
    {
        if (value == 0) return "0";
        var negative = value < 0;
        if (negative) value = -value;
        Span<char> buffer = stackalloc char[13];
        var pos = buffer.Length;
        while (value > 0)
        {
            buffer[--pos] = Alphabet[(int)(value % Base)];
            value /= Base;
        }
        if (negative) buffer[--pos] = '-';
        return new string(buffer[pos..]);
    }

    public static long Decode(string opaque)
    {
        if (string.IsNullOrEmpty(opaque))
            throw new ArgumentException("Opaque ID cannot be null or empty", nameof(opaque));
        var negative = opaque[0] == '-';
        var start = negative ? 1 : 0;
        long value = 0;
        for (int i = start; i < opaque.Length; i++)
        {
            var c = char.ToLowerInvariant(opaque[i]);
            var idx = Alphabet.IndexOf(c);
            if (idx < 0)
                throw new ArgumentException($"Invalid character '{opaque[i]}' in opaque ID", nameof(opaque));
            value = value * Base + idx;
        }
        return negative ? -value : value;
    }
}
