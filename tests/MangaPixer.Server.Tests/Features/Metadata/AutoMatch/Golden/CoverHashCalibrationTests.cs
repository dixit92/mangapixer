namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch.Golden;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.MediaWorker.Images;
using ImageMagick;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Calibration of the cover-hash thresholds (<see cref="CoverHash.SameMaxDistance"/> = 10,
/// <see cref="CoverHash.DifferentMinDistance"/> = 20) on the PUBLIC covers recorded for the golden set (tiny
/// re-encodes of MangaUpdates thumbnails, see the README), hashed by the worker's own code
/// (<see cref="ImageHasher"/>). "Same cover" = the same image after what separates a provider cover from a local
/// thumbnail: another size, JPEG re-compression, a brightness shift, a few percent of framing, WebP; "different" =
/// two different series. The rule that must never break: two different covers are never "the same" (a false
/// match would add evidence to the wrong record). Prints the distributions for the lane note.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CoverHashCalibrationTests(ITestOutputHelper output)
{
    private static ulong Hash(byte[] bytes)
    {
        var outcome = ImageHasher.Hash(bytes, ImageHashLimits.MaxDimension);
        Assert.Null(outcome.Error);
        return outcome.Hash;
    }

    private static byte[] Rendition(byte[] source, Action<MagickImage> change, MagickFormat format = MagickFormat.Jpeg, uint quality = 85)
    {
        using var image = new MagickImage(source);
        change(image);
        image.Format = format;
        image.Quality = quality;
        return image.ToByteArray();
    }

    private static readonly (string Name, Func<byte[], byte[]> Make)[] s_sameCover =
    [
        ("half size", b => Rendition(b, i => i.Resize(new Percentage(50)))),
        ("double size", b => Rendition(b, i => i.Resize(new Percentage(200)))),
        ("jpeg q35", b => Rendition(b, _ => { }, quality: 35)),
        ("webp q60", b => Rendition(b, _ => { }, MagickFormat.WebP, 60)),
        ("brightness +15%", b => Rendition(b, i => i.Modulate(new Percentage(115)))),
        ("contrast", b => Rendition(b, i => i.BrightnessContrast(new Percentage(0), new Percentage(15)))),
        ("crop 1% per side", b => Rendition(b, i => Crop(i, 0.01))),
        ("crop 2% per side", b => Rendition(b, i => Crop(i, 0.02))),
        ("crop 3% per side", b => Rendition(b, i => Crop(i, 0.03))),
        ("crop 5% per side", b => Rendition(b, i => Crop(i, 0.05))),
        ("white border 3%", b => Rendition(b, i =>
        {
            i.BorderColor = MagickColors.White;
            i.Border((uint)Math.Max(1, i.Width * 0.03), (uint)Math.Max(1, i.Height * 0.03));
        })),
    ];

    private static void Crop(MagickImage image, double side)
    {
        var (w, h) = (image.Width, image.Height);
        image.Crop(new MagickGeometry((int)(w * side), (int)(h * side), (uint)(w * (1 - 2 * side)), (uint)(h * (1 - 2 * side))));
        image.ResetPage();
    }

    [Fact]
    public void SameCoverRenditions_AreSame_DifferentCovers_AreNeverSame()
    {
        var covers = GoldenFixtures.ImageNames("cover.").Select(n => (Name: n, Hash: Hash(GoldenFixtures.Image(n)!))).ToList();
        Assert.True(covers.Count >= 15, $"only {covers.Count} recorded covers");

        var byRendition = s_sameCover.ToDictionary(r => r.Name, _ => new List<int>(), StringComparer.Ordinal);
        foreach (var name in covers.Select(c => c.Name))
        {
            var bytes = GoldenFixtures.Image(name)!;
            var original = Hash(bytes);
            foreach (var (rendition, make) in s_sameCover)
                byRendition[rendition].Add(CoverHash.Distance(original, Hash(make(bytes))));
        }

        var different = new List<int>();
        for (var i = 0; i < covers.Count; i++)
            for (var j = i + 1; j < covers.Count; j++)
                different.Add(CoverHash.Distance(covers[i].Hash, covers[j].Hash));

        foreach (var (rendition, distances) in byRendition)
            Report(Line("same cover, " + rendition, distances));
        Report(Line("different covers", different));

        Assert.All(different, d => Assert.True(d > CoverHash.SameMaxDistance, $"two different covers {d} bits apart"));
        foreach (var rendition in s_mustBeSame)
            Assert.All(byRendition[rendition], d => Assert.True(d <= CoverHash.SameMaxDistance, $"{rendition}: distance {d}"));
    }

    /// <summary>Renditions that must always be "the same cover" (framing changes are measured, not asserted).</summary>
    private static readonly string[] s_mustBeSame = ["half size", "double size", "jpeg q35", "webp q60", "brightness +15%", "contrast", "crop 1% per side"];

    private void Report(string line)
    {
        output.WriteLine(line);
        Console.WriteLine("CALIBRATION " + line);
    }

    [Fact]
    public void LocalCoverThumbnails_MatchTheirOwnProviderCover_AndNoOther()
    {
        // local.<series id>.webp was made from that series' full cover (cropped 3% per side, 96 px, WebP): what a
        // stored thumbnail of volume 1 looks like next to the provider's thumbnail.
        var own = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["local.61508275290.webp"] = "cover.i523282.jpg",
            ["local.76554797640.webp"] = "cover.i324917.jpg",
            ["local.46692009496.webp"] = "cover.i523619.jpg",
        };
        var covers = GoldenFixtures.ImageNames("cover.").ToDictionary(n => n, n => Hash(GoldenFixtures.Image(n)!), StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var (local, provider) in own)
        {
            var hash = Hash(GoldenFixtures.Image(local)!);
            var distance = CoverHash.Distance(hash, covers[provider]);
            var nearest = covers.Where(c => c.Key != provider).Min(c => CoverHash.Distance(hash, c.Value));
            Report($"{local}: own cover {distance}, nearest other cover {nearest}");
            lines.Add($"{local} own {distance} nearest {nearest}");
        }
        foreach (var (local, provider) in own)
        {
            var hash = Hash(GoldenFixtures.Image(local)!);
            Assert.True(CoverHash.Compare(hash, covers[provider]) == CoverVerdict.Same, string.Join("; ", lines));
            foreach (var (name, other) in covers.Where(c => c.Key != provider))
                Assert.True(CoverHash.Compare(hash, other) != CoverVerdict.Same, $"{local} vs {name}: {CoverHash.Distance(hash, other)}");
        }
    }

    private static string Line(string label, List<int> distances)
    {
        distances.Sort();
        return FormattableString.Invariant(
            $"{label}: n {distances.Count}, min {distances[0]}, median {distances[distances.Count / 2]}, max {distances[^1]}; <= {CoverHash.SameMaxDistance}: {distances.Count(d => d <= CoverHash.SameMaxDistance)}, 11-19: {distances.Count(d => d is > CoverHash.SameMaxDistance and < CoverHash.DifferentMinDistance)}, >= {CoverHash.DifferentMinDistance}: {distances.Count(d => d >= CoverHash.DifferentMinDistance)}");
    }
}
