namespace com.lifepixer.mangapixer.Server.Operations;

using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

/// <summary>
/// Gzip helpers for rotating backups (1.49.0). Only <c>System.IO.Compression</c>.
///
/// Writing: a verified plain database copy is stream-compressed to a
/// <c>.partial</c> file, flushed to disk and read back (the gzip trailer's
/// CRC-32 is checked by the reader, and the unpacked length + SHA-256 are
/// compared with the source's) before the caller renames it to its final name.
/// Reading (restore): a stream is treated as gzip by its two magic bytes, never
/// by a file name.
/// </summary>
public static class BackupCompression
{
    /// <summary>Gzip member header: <c>1F 8B</c>.</summary>
    public static bool HasGzipMagic(ReadOnlySpan<byte> head) => head.Length >= 2 && head[0] == 0x1F && head[1] == 0x8B;

    private const int BufferSize = 1 << 20;

    /// <summary>
    /// Compresses <paramref name="source"/> into <paramref name="partialPath"/> (created new), flushes it to disk and
    /// verifies it by reading it back. Throws on any problem (the caller deletes the partial file).
    /// </summary>
    /// <returns>The source length and the compressed length in bytes.</returns>
    public static async Task<(long RawBytes, long GzipBytes)> CompressAndVerifyAsync(
        string source, string partialPath, CompressionLevel level, CancellationToken ct)
    {
        byte[] sourceHash;
        long rawLength = 0;
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            await using var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            await using (var gzip = new GZipStream(output, level, leaveOpen: true))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    rawLength += read;
                    await gzip.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            } // disposing the GZipStream writes the trailer (CRC-32 + length)
            await output.FlushAsync(ct);
            output.Flush(flushToDisk: true);
            sourceHash = hash.GetHashAndReset();
        }

        // Read it back: the decompressor verifies the trailer's CRC-32 and length (InvalidDataException on a mismatch);
        // the SHA-256 of the unpacked bytes must also equal the source's.
        long gzipLength;
        using (var check = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            await using var packed = new FileStream(partialPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            gzipLength = packed.Length;
            await using var gzip = new GZipStream(packed, CompressionMode.Decompress);
            var buffer = new byte[BufferSize];
            long unpacked = 0;
            int read;
            while ((read = await gzip.ReadAsync(buffer, ct)) > 0)
            {
                check.AppendData(buffer, 0, read);
                unpacked += read;
            }
            if (unpacked != rawLength || !CryptographicOperations.FixedTimeEquals(check.GetHashAndReset(), sourceHash))
                throw new InvalidDataException("compressed backup does not match its source");
        }

        return (rawLength, gzipLength);
    }

    /// <summary>
    /// A read-only stream that replays an already-read head, then continues with the rest of the underlying stream.
    /// Lets restore look at the first bytes of a (possibly non-seekable) upload before choosing how to read it.
    /// </summary>
    internal sealed class HeadThenStream : Stream
    {
        private readonly ReadOnlyMemory<byte> _head;
        private readonly Stream _rest;
        private int _position;

        public HeadThenStream(ReadOnlyMemory<byte> head, Stream rest)
        {
            _head = head;
            _rest = rest;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position < _head.Length)
            {
                var n = Math.Min(buffer.Length, _head.Length - _position);
                _head.Span.Slice(_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }
            return _rest.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < _head.Length)
                return ValueTask.FromResult(Read(buffer.Span));
            return _rest.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
