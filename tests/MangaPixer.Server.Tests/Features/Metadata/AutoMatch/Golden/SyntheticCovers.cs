namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch.Golden;

using ImageMagick;
using ImageMagick.Drawing;

/// <summary>
/// Drawn "covers" for the cover-hash tests (1.28.0): a seeded composition of flat shapes on a two-tone background, drawn with
/// Magick.NET in the test - no publisher art. The same seed always draws the same picture.
/// </summary>
public static class SyntheticCovers
{
    public const uint Width = 200, Height = 300;

    private static MagickColor Color(Random r) => MagickColor.FromRgb((byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256));

    public static MagickImage Draw(int seed)
    {
        var r = new Random(seed);
        var image = new MagickImage(Color(r), Width, Height);
        var draw = new Drawables().FillColor(Color(r)).Rectangle(0, r.Next(60, 240), Width, Height);
        for (var i = 0; i < 6; i++)
        {
            var (x, y) = (r.Next(0, (int)Width), r.Next(0, (int)Height));
            draw = r.Next(2) == 0
                ? draw.FillColor(Color(r)).Rectangle(x, y, x + r.Next(20, 110), y + r.Next(20, 140))
                : draw.FillColor(Color(r)).Ellipse(x, y, r.Next(10, 60), r.Next(10, 80), 0, 360);
        }
        draw.Draw(image);
        return image;
    }

    public static byte[] Png(int seed)
    {
        using var image = Draw(seed);
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }
}
