namespace com.lifepixer.mangapixer.Tests.MediaWorker.Images;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.MediaWorker.Images;
using ImageMagick;
using ImageMagick.Drawing;
using Xunit;

/// <summary>
/// Tests for the worker-side cover hash (protocol v4 <c>image_hash</c>, 1.28.0): Magick.NET decode, alpha flattened on
/// white, grayscale, 32x32, <see cref="CoverHash"/>; the size and dimension caps; undecodable input. Synthetic images
/// drawn in the test only.
/// </summary>
public sealed class ImageHasherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mp-imghash-" + Guid.NewGuid().ToString("N")[..8]);

    public ImageHasherTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A "cover": a dark figure on a light gradient, 120 x 180.</summary>
    private static MagickImage Cover(bool transparentBackground = false)
    {
        var image = new MagickImage(transparentBackground ? MagickColors.Transparent : MagickColors.White, 120, 180);
        var draw = new Drawables()
            .FillColor(new MagickColor("#203040")).Rectangle(35, 40, 85, 170)
            .FillColor(new MagickColor("#c04030")).Ellipse(60, 30, 20, 20, 0, 360);
        if (!transparentBackground)
            draw = draw.FillColor(new MagickColor("#e0e0f0")).Rectangle(0, 0, 119, 20);
        draw.Draw(image);
        return image;
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Encode(MagickImage image, MagickFormat format, uint quality = 90)
    {
        image.Format = format;
        image.Quality = quality;
        return image.ToByteArray();
    }

    [Fact]
    public void Hash_TheSameCoverAsPngJpegAndSmallerWebp_IsTheSameCover()
    {
        using var cover = Cover();
        var png = ImageHasher.HashFile(Write("c.png", Encode(cover, MagickFormat.Png)), ImageHashLimits.MaxBytes, ImageHashLimits.MaxDimension);
        var jpeg = ImageHasher.HashFile(Write("c.jpg", Encode(cover, MagickFormat.Jpeg, 40)), ImageHashLimits.MaxBytes, ImageHashLimits.MaxDimension);
        using var small = Cover();
        small.Resize(48, 72);
        var webp = ImageHasher.HashFile(Write("c.webp", Encode(small, MagickFormat.WebP, 60)), ImageHashLimits.MaxBytes, ImageHashLimits.MaxDimension);

        Assert.All(new[] { png, jpeg, webp }, o => Assert.Null(o.Error));
        Assert.Equal((120, 180), (png.Width, png.Height));
        Assert.Equal(CoverVerdict.Same, CoverHash.Compare(png.Hash, jpeg.Hash));
        Assert.Equal(CoverVerdict.Same, CoverHash.Compare(png.Hash, webp.Hash));
    }

    [Fact]
    public void Hash_ADifferentPicture_IsADifferentCover()
    {
        using var cover = Cover();
        using var other = new MagickImage(MagickColors.Black, 120, 180);
        new Drawables().FillColor(MagickColors.White).Rectangle(0, 90, 119, 179).FillColor(MagickColors.Gray).Ellipse(20, 40, 15, 15, 0, 360).Draw(other);

        var a = ImageHasher.Hash(Encode(cover, MagickFormat.Png), ImageHashLimits.MaxDimension);
        var b = ImageHasher.Hash(Encode(other, MagickFormat.Png), ImageHashLimits.MaxDimension);

        Assert.Equal(CoverVerdict.Different, CoverHash.Compare(a.Hash, b.Hash));
    }

    [Fact]
    public void Hash_TransparencyIsFlattenedOnWhite()
    {
        using var transparent = Cover(transparentBackground: true);
        using var white = Cover(transparentBackground: true);
        white.BackgroundColor = MagickColors.White;
        white.Alpha(AlphaOption.Remove);

        var a = ImageHasher.Hash(Encode(transparent, MagickFormat.Png), ImageHashLimits.MaxDimension);
        var b = ImageHasher.Hash(Encode(white, MagickFormat.Png), ImageHashLimits.MaxDimension);

        Assert.Equal(a.Hash, b.Hash);
    }

    [Fact]
    public void HashFile_Missing_TooLarge_TooWide_AndNotAnImage_AreErrors()
    {
        using var cover = Cover();
        var png = Write("ok.png", Encode(cover, MagickFormat.Png));

        Assert.Equal(ImageHashErrors.Missing, ImageHasher.HashFile(Path.Combine(_dir, "nope.png"), ImageHashLimits.MaxBytes, ImageHashLimits.MaxDimension).Error);
        Assert.Equal(ImageHashErrors.TooLarge, ImageHasher.HashFile(png, maxBytes: 16, ImageHashLimits.MaxDimension).Error);
        Assert.Equal(ImageHashErrors.TooLarge, ImageHasher.HashFile(png, ImageHashLimits.MaxBytes, maxDimension: 100).Error);
        Assert.Equal(ImageHashErrors.TooLarge, ImageHasher.HashFile(Write("empty.png", []), ImageHashLimits.MaxBytes, ImageHashLimits.MaxDimension).Error);
        Assert.Equal(ImageHashErrors.DecodeFailed, ImageHasher.HashFile(Write("text.png", "not an image at all"u8.ToArray()),
            ImageHashLimits.MaxBytes, ImageHashLimits.MaxDimension).Error);
    }

    private const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"180\"><rect width=\"120\" height=\"180\" fill=\"#203040\"/></svg>";

    // An ImageMagick drawing program that would read another file if it were ever interpreted.
    private const string Mvg = "push graphic-context\nviewbox 0 0 120 180\nimage over 0,0 0,0 'text:/etc/hostname'\npop graphic-context\n";

    [Theory]
    [InlineData("svg")]
    [InlineData("mvg")]
    [InlineData("ps")]
    public void Hash_NonRasterPayloads_AreRefused_BeforeImageMagickSeesThem(string kind)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(kind switch
        {
            "svg" => Svg,
            "mvg" => Mvg,
            _ => "%!PS-Adobe-3.0\n0 0 moveto (x) show showpage\n",
        });

        Assert.Null(ImageHasher.SniffFormat(bytes));
        Assert.Equal(ImageHashErrors.DecodeFailed, ImageHasher.Hash(bytes, ImageHashLimits.MaxDimension).Error);
        Assert.Equal(ImageHashErrors.DecodeFailed, ImageHasher.HashFile(Write("payload." + kind, bytes), ImageHashLimits.MaxBytes, ImageHashLimits.MaxDimension).Error);
    }

    [Fact]
    public void Hash_ASvgBodyBehindPngMagic_IsDecodedAsPngOnly_AndFails()
    {
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. System.Text.Encoding.ASCII.GetBytes(Svg)];

        Assert.Equal(MagickFormat.Png, ImageHasher.SniffFormat(bytes));
        Assert.Equal(ImageHashErrors.DecodeFailed, ImageHasher.Hash(bytes, ImageHashLimits.MaxDimension).Error);
    }

    [Theory]
    [InlineData(MagickFormat.Jpeg)]
    [InlineData(MagickFormat.Png)]
    [InlineData(MagickFormat.Gif)]
    [InlineData(MagickFormat.WebP)]
    public void Hash_TheFourRasterFormats_AreSniffedAndHashed(MagickFormat format)
    {
        using var cover = Cover();
        var bytes = Encode(cover, format);

        Assert.Equal(format, ImageHasher.SniffFormat(bytes));
        Assert.Null(ImageHasher.Hash(bytes, ImageHashLimits.MaxDimension).Error);
    }

    [Fact]
    public void Hash_AFormatOutsideTheFour_IsRefused()
    {
        using var cover = Cover();

        Assert.Equal(ImageHashErrors.DecodeFailed, ImageHasher.Hash(Encode(cover, MagickFormat.Bmp), ImageHashLimits.MaxDimension).Error);
        Assert.Equal(ImageHashErrors.DecodeFailed, ImageHasher.Hash(Encode(cover, MagickFormat.Tiff), ImageHashLimits.MaxDimension).Error);
    }
}
