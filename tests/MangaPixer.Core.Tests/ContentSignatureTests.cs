namespace com.lifepixer.mangapixer.Tests.Core;

using com.lifepixer.mangapixer.Core.Media;
using Xunit;

/// <summary>
/// Unit tests for the cheap move-detection content signature (1.5.0).
/// The signature must be deterministic, embed the byte length, and change when
/// the head or tail bytes change — while being cheap (bounded read) for large
/// inputs.
/// </summary>
public sealed class ContentSignatureTests
{
    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static string Sig(byte[] data)
    {
        using var ms = new MemoryStream(data);
        return ContentSignature.Compute(ms);
    }

    [Fact]
    public void Same_bytes_produce_same_signature()
    {
        var a = Bytes(300_000, 1);
        var b = (byte[])a.Clone();
        Assert.Equal(Sig(a), Sig(b));
    }

    [Fact]
    public void Signature_embeds_format_and_length()
    {
        var sig = Sig(Bytes(1234, 7));
        Assert.StartsWith(ContentSignature.FormatVersion + ":1234:", sig, StringComparison.Ordinal);
        Assert.Equal(1234, ContentSignature.TryGetByteLength(sig));
        Assert.True(sig.Length <= ContentSignature.MaxLength);
    }

    [Fact]
    public void Same_length_different_head_differs()
    {
        var a = Bytes(300_000, 2);
        var b = (byte[])a.Clone();
        b[10] ^= 0xFF;
        Assert.NotEqual(Sig(a), Sig(b));
    }

    [Fact]
    public void Same_length_different_tail_differs()
    {
        var a = Bytes(300_000, 3);
        var b = (byte[])a.Clone();
        b[^5] ^= 0xFF;
        Assert.NotEqual(Sig(a), Sig(b));
    }

    [Fact]
    public void Different_length_differs_even_with_same_prefix()
    {
        var a = Bytes(200_000, 4);
        var b = new byte[a.Length + 1];
        a.CopyTo(b, 0);
        Assert.NotEqual(Sig(a), Sig(b));
    }

    [Fact]
    public void Small_and_empty_inputs_are_supported()
    {
        Assert.Equal(Sig([]), Sig([]));
        Assert.NotEqual(Sig([1]), Sig([2]));
        var small = Bytes(ContentSignature.SampleBytes / 2, 5);
        Assert.Equal(Sig(small), Sig((byte[])small.Clone()));
    }

    [Fact]
    public void Middle_bytes_are_not_sampled_on_large_inputs()
    {
        // Documents the trade-off: a change strictly between the head and tail
        // windows is invisible to the cheap signature. Callers therefore always
        // combine it with byte length and never use it to skip a stamp-based
        // content-change check.
        var a = Bytes(4 * ContentSignature.SampleBytes, 6);
        var b = (byte[])a.Clone();
        b[2 * ContentSignature.SampleBytes] ^= 0xFF;
        Assert.Equal(Sig(a), Sig(b));
    }

    [Fact]
    public void TryComputeFile_matches_stream_and_is_null_for_missing_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mangapixer-sig-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var data = Bytes(150_000, 8);
            var path = Path.Combine(dir, "a.cbz");
            File.WriteAllBytes(path, data);
            Assert.Equal(Sig(data), ContentSignature.TryComputeFile(path));
            Assert.Null(ContentSignature.TryComputeFile(Path.Combine(dir, "missing.cbz")));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("v0:12:abc")]
    [InlineData("v1:notanumber:abc")]
    public void TryGetByteLength_rejects_malformed(string? value)
    {
        Assert.Null(ContentSignature.TryGetByteLength(value));
    }
}
