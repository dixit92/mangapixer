namespace com.lifepixer.mangapixer.Server.Features.Export;

using System.Buffers.Binary;

/// <summary>
/// The export's keyset cursor (1.33.0): the last row's <c>(UpdatedAt, Id)</c>, opaque to clients (base64url of two big-endian
/// int64s: UTC ticks, row id). Rows are served in ascending <c>(UpdatedAt, Id)</c> order.
/// </summary>
public readonly record struct ExportCursor(long UpdatedAtTicks, long RowId)
{
    public string Encode()
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt64BigEndian(bytes, UpdatedAtTicks);
        BinaryPrimitives.WriteInt64BigEndian(bytes[8..], RowId);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? text, out ExportCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrEmpty(text) || text.Length > 32)
            return false;
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        Span<byte> bytes = stackalloc byte[24];
        if (!Convert.TryFromBase64String(padded, bytes, out var written) || written != 16)
            return false;
        var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes);
        var id = BinaryPrimitives.ReadInt64BigEndian(bytes[8..16]);
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks || id < 0)
            return false;
        cursor = new ExportCursor(ticks, id);
        return true;
    }
}
