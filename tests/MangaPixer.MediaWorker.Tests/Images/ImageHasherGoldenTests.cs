namespace com.lifepixer.mangapixer.Tests.MediaWorker.Images;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.MediaWorker.Images;
using Xunit;

/// <summary>
/// 1.44.0 (Magick.NET 15): the perceptual hash of FIXED image files must not move when the image library changes. Stored hashes depend on
/// it - every cover hash, and the 1.44.0 volume contents scan's page hashes - so a library update that shifts them would silently weaken
/// the cover comparison and invalidate stored page hashes. The fixtures (<c>HashGolden/</c>: one synthetic manga-like page - panels,
/// balloons, dots, a gradient band - as JPEG, WebP, GIF, and as a PNG with alpha) were written once with Magick.NET 14.17.2; the expected
/// values are what 14.17.2 computed for these exact bytes. Encoding is NOT part of this test (the files are committed), so only the decode
/// and the hash pipeline are compared.
/// </summary>
public sealed class ImageHasherGoldenTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Images", "HashGolden", name);

    [Theory]
    [InlineData("page.jpg", "ec81c3bc96c2333e", 600, 900)]
    [InlineData("page.webp", "ec81c3bc96c2333e", 300, 450)]
    [InlineData("page.gif", "ec81c3bc96c2333e", 200, 300)]
    [InlineData("page-alpha.png", "e681c99c36c29b3e", 300, 450)]
    public void TheHashOfAFixedFile_IsTheOneMagick14Computed(string file, string expected, int width, int height)
    {
        var hashed = ImageHasher.HashFile(Fixture(file), ImageHashLimits.MaxBytes, ImageHashLimits.MaxDimension);

        Assert.Null(hashed.Error);
        Assert.Equal((width, height), (hashed.Width, hashed.Height));
        var distance = CoverHash.Distance(hashed.Hash, Convert.ToUInt64(expected, 16));
        Assert.True(distance == 0, $"{file}: {hashed.Hash:x16} is {distance} bits from the 14.17.2 hash {expected}");
    }
}
