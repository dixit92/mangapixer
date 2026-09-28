namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch.Golden;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.MediaWorker.Images;
using ImageMagick;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The cover-hash thresholds (<see cref="CoverHash.SameMaxDistance"/> = 10, <see cref="CoverHash.DifferentMinDistance"/> = 20).
/// The calibration on PUBLIC covers was measured once (2026-09-28, 19 recorded MangaUpdates thumbnails and 3 local
/// thumbnails; the numbers are in the lane note) and only its HASHES are kept (<c>covers.json</c>; no cover art in the
/// repository): two different covers are never "the same" (a false match would add evidence to the wrong record), and a local
/// thumbnail matches its own cover and no other. What separates a provider cover from a local thumbnail - size, JPEG / WebP
/// re-compression, brightness, contrast - is checked on DRAWN covers (<see cref="SyntheticCovers"/>) with the worker's own
/// code (<see cref="ImageHasher"/>); framing changes are measured and printed, not asserted.
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

    private static void Crop(MagickImage image, double side)
    {
        var (w, h) = (image.Width, image.Height);
        image.Crop(new MagickGeometry((int)(w * side), (int)(h * side), (uint)(w * (1 - 2 * side)), (uint)(h * (1 - 2 * side))));
        image.ResetPage();
    }

    /// <summary>Renditions that must always stay "the same cover".</summary>
    private static readonly (string Name, Func<byte[], byte[]> Make)[] s_sameCover =
    [
        ("half size", b => Rendition(b, i => i.Resize(new Percentage(50)))),
        ("double size", b => Rendition(b, i => i.Resize(new Percentage(200)))),
        ("jpeg q35", b => Rendition(b, _ => { }, quality: 35)),
        ("webp q60", b => Rendition(b, _ => { }, MagickFormat.WebP, 60)),
        ("brightness +15%", b => Rendition(b, i => i.Modulate(new Percentage(115)))),
        ("contrast", b => Rendition(b, i => i.BrightnessContrast(new Percentage(0), new Percentage(15)))),
    ];

    /// <summary>Framing changes: measured and printed only.</summary>
    private static readonly (string Name, Func<byte[], byte[]> Make)[] s_framing =
    [
        ("crop 1% per side", b => Rendition(b, i => Crop(i, 0.01))),
        ("crop 3% per side", b => Rendition(b, i => Crop(i, 0.03))),
        ("white border 3%", b => Rendition(b, i =>
        {
            i.BorderColor = MagickColors.White;
            i.Border((uint)Math.Max(1, i.Width * 0.03), (uint)Math.Max(1, i.Height * 0.03));
        })),
    ];

    private static readonly int[] s_seeds = [1, 2, 3, 5, 8, 13, 21, 34];

    [Fact]
    public void StoredHashes_TwoDifferentRecordedCovers_AreNeverTheSame()
    {
        var covers = GoldenFixtures.CoverHashes.ToList();
        Assert.Equal(19, covers.Count);

        var different = new List<int>();
        for (var i = 0; i < covers.Count; i++)
            for (var j = i + 1; j < covers.Count; j++)
                different.Add(CoverHash.Distance(covers[i].Value, covers[j].Value));

        Report(Line("stored, different covers", different));
        Assert.All(different, d => Assert.True(d >= CoverHash.DifferentMinDistance, $"two different covers {d} bits apart"));
    }

    [Fact]
    public void StoredHashes_ALocalThumbnail_MatchesItsOwnProviderCover_AndNoOther()
    {
        // local hash (series id) -> the recorded thumbnail of the same series (image file name in the URL).
        var own = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["61508275290"] = "i523282",
            ["76554797640"] = "i324917",
            ["46692009496"] = "i523619",
        };
        foreach (var (series, stem) in own)
        {
            var local = GoldenFixtures.LocalHashes[series];
            var others = GoldenFixtures.CoverHashes.Where(c => c.Key != stem).ToList();
            Report($"local {series}: own cover {CoverHash.Distance(local, GoldenFixtures.CoverHashes[stem])}, nearest other {others.Min(c => CoverHash.Distance(local, c.Value))}");
            Assert.Equal(CoverVerdict.Same, CoverHash.Compare(local, GoldenFixtures.CoverHashes[stem]));
            Assert.All(others, c => Assert.NotEqual(CoverVerdict.Same, CoverHash.Compare(local, c.Value)));
        }
    }

    [Fact]
    public void DrawnCovers_ResizedRecompressedOrShifted_StayTheSameCover()
    {
        var byRendition = s_sameCover.Concat(s_framing).ToDictionary(r => r.Name, _ => new List<int>(), StringComparer.Ordinal);
        foreach (var seed in s_seeds)
        {
            var bytes = SyntheticCovers.Png(seed);
            var original = Hash(bytes);
            foreach (var (name, make) in s_sameCover.Concat(s_framing))
                byRendition[name].Add(CoverHash.Distance(original, Hash(make(bytes))));
        }
        foreach (var (name, distances) in byRendition)
            Report(Line("drawn, " + name, distances));

        foreach (var (name, _) in s_sameCover)
            Assert.All(byRendition[name], d => Assert.True(d <= CoverHash.SameMaxDistance, $"{name}: distance {d}"));
    }

    private void Report(string line)
    {
        output.WriteLine(line);
        Console.WriteLine("CALIBRATION " + line);
    }

    private static string Line(string label, List<int> distances)
    {
        distances.Sort();
        return FormattableString.Invariant(
            $"{label}: n {distances.Count}, min {distances[0]}, median {distances[distances.Count / 2]}, max {distances[^1]}; <= {CoverHash.SameMaxDistance}: {distances.Count(d => d <= CoverHash.SameMaxDistance)}, 11-19: {distances.Count(d => d is > CoverHash.SameMaxDistance and < CoverHash.DifferentMinDistance)}, >= {CoverHash.DifferentMinDistance}: {distances.Count(d => d >= CoverHash.DifferentMinDistance)}");
    }
}
