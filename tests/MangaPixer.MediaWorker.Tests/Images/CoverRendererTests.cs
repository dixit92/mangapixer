namespace com.lifepixer.mangapixer.Tests.MediaWorker.Images;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.MediaWorker.Images;
using ImageMagick;
using ImageMagick.Drawing;
using Xunit;

/// <summary>
/// Tests for the worker-side cover render (protocol v5 <c>cover_render</c>, 1.29.0): the crop geometry of a jacket
/// spread, the thumbnail bound, the hash of the written file, and the strict formats for provider images. Synthetic
/// images drawn in the test only.
/// </summary>
public sealed class CoverRendererTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mp-coverrender-" + Guid.NewGuid().ToString("N")[..8]);

    public CoverRendererTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A "cover" of <paramref name="width"/> x <paramref name="height"/>: a figure on a background, varied by the seed.</summary>
    private static MagickImage Cover(int seed, uint width = 700, uint height = 1000)
    {
        var r = new Random(seed);
        MagickColor Color() => MagickColor.FromRgb((byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256));
        var image = new MagickImage(Color(), width, height);
        var draw = new Drawables();
        for (var i = 0; i < 6; i++)
        {
            var (x, y) = (r.Next(0, (int)width), r.Next(0, (int)height));
            draw = r.Next(2) == 0
                ? draw.FillColor(Color()).Rectangle(x, y, x + r.Next(100, 400), y + r.Next(100, 500))
                : draw.FillColor(Color()).Ellipse(x, y, r.Next(50, 250), r.Next(50, 300), 0, 360);
        }
        draw.Draw(image);
        return image;
    }

    /// <summary>A jacket spread: the cover of seed <paramref name="left"/>, a black spine strip, the cover of seed <paramref name="right"/>.</summary>
    private static byte[] Spread(int left, int right, uint spine = 100)
    {
        using var canvas = new MagickImage(MagickColors.Black, 700 + spine + 700, 1000);
        using var l = Cover(left);
        using var rr = Cover(right);
        canvas.Composite(l, 0, 0, CompositeOperator.Over);
        canvas.Composite(rr, (int)(700 + spine), 0, CompositeOperator.Over);
        canvas.Format = MagickFormat.Png;
        return canvas.ToByteArray();
    }

    private static byte[] Png(MagickImage image)
    {
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }

    private CoverRenderRequest Request(string crop = CoverCropSides.None) => new()
    {
        JobId = "t",
        Source = CoverRenderSources.Image,
        CropSide = crop,
        OutputPath = Path.Combine(_dir, Guid.NewGuid().ToString("N")[..8] + ".webp"),
    };

    [Theory]
    [InlineData(1400, 1000, CoverCropSides.Left, 0, 700)]
    [InlineData(1400, 1000, CoverCropSides.Right, 700, 700)]
    // A visible spine: the cover keeps the outer edge and drops the spine.
    [InlineData(1500, 1000, CoverCropSides.Right, 800, 700)]
    [InlineData(1500, 1000, CoverCropSides.Left, 0, 700)]
    // A narrow "spread": half the width is the limit.
    [InlineData(1000, 1000, CoverCropSides.Right, 500, 500)]
    public void CropColumns_KeepTheOuterEdge_AndDropTheSpine(int width, int height, string side, int x, int w)
        => Assert.Equal((x, w), CoverRenderer.CropColumns(width, height, side, CoverRenderLimits.DefaultCropAspect));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void CropColumns_AnOutOfRangeAspect_MeansTheDefault(double aspect)
        => Assert.Equal((0, 700), CoverRenderer.CropColumns(1400, 1000, CoverCropSides.Left, aspect));

    [Fact]
    public void CropColumns_IsNeverEmpty()
        => Assert.Equal((0, 1), CoverRenderer.CropColumns(1, 1, CoverCropSides.Left, 0.7));

    [Fact]
    public void Render_ASpread_CroppedLeftOrRight_IsThatHalfsCover_AndTheHashIsTheWrittenFilesHash()
    {
        var spread = Spread(left: 11, right: 22);
        using var leftAlone = Cover(11);
        using var rightAlone = Cover(22);
        var leftHash = ImageHasher.Hash(Png(leftAlone), ImageHashLimits.MaxDimension).Hash;
        var rightHash = ImageHasher.Hash(Png(rightAlone), ImageHashLimits.MaxDimension).Hash;

        var leftRequest = Request(CoverCropSides.Left);
        var left = CoverRenderer.Render(spread, strictFormats: true, leftRequest);
        var right = CoverRenderer.Render(spread, strictFormats: true, Request(CoverCropSides.Right));

        Assert.Null(left.Error);
        Assert.Null(right.Error);
        Assert.Equal((1500, 1000), (left.SourceWidth, left.SourceHeight));
        Assert.Equal((280, 400), (left.Width, left.Height)); // 700 x 1000 bounded to 400
        Assert.True(CoverHash.Distance(left.Hash!.Value, leftHash) <= CoverHash.SameMaxDistance);
        Assert.True(CoverHash.Distance(right.Hash!.Value, rightHash) <= CoverHash.SameMaxDistance);
        Assert.True(CoverHash.Distance(left.Hash.Value, rightHash) >= CoverHash.DifferentMinDistance);
        // The hash is exactly what image_hash answers for the written thumbnail.
        Assert.Equal(ImageHasher.Hash(File.ReadAllBytes(leftRequest.OutputPath), ImageHashLimits.MaxDimension).Hash, left.Hash);
        Assert.Equal(MagickFormat.WebP, new MagickImageInfo(leftRequest.OutputPath).Format);
    }

    [Fact]
    public void Render_WithoutCrop_BoundsTheLongestEdge_AndNeverUpscales()
    {
        using var big = Cover(3, 1024, 1536);
        using var small = Cover(4, 200, 300);
        var bigOut = CoverRenderer.Render(Png(big), strictFormats: true, Request());
        var smallOut = CoverRenderer.Render(Png(small), strictFormats: true, Request());
        Assert.Equal((267, 400), (bigOut.Width, bigOut.Height));
        Assert.Equal((200, 300), (smallOut.Width, smallOut.Height));
    }

    [Fact]
    public void Render_WithoutHash_AnswersNoHash()
    {
        using var cover = Cover(5, 300, 450);
        var outcome = CoverRenderer.Render(Png(cover), strictFormats: true, Request() with { ComputeHash = false });
        Assert.Null(outcome.Error);
        Assert.Null(outcome.Hash);
    }

    [Fact]
    public void Render_ProviderBytesThatAreNotARasterImage_AreRefusedWithoutDecoding()
    {
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\"/></svg>"u8.ToArray();
        var request = Request();
        Assert.Equal(CoverRenderErrors.DecodeFailed, CoverRenderer.Render(svg, strictFormats: true, request).Error);
        Assert.Equal(CoverRenderErrors.DecodeFailed, CoverRenderer.Render("not an image"u8.ToArray(), strictFormats: true, request).Error);
        Assert.False(File.Exists(request.OutputPath));
    }

    [Fact]
    public void Render_AnInputOverTheDimensionCap_IsTooLarge_BeforeDecoding()
    {
        using var cover = Cover(6, 300, 450);
        var outcome = CoverRenderer.Render(Png(cover), strictFormats: true, Request() with { MaxInputDimension = 200 });
        Assert.Equal(CoverRenderErrors.TooLarge, outcome.Error);
    }

    [Fact]
    public void Render_AnUnknownCropSide_IsAnInvalidRequest()
    {
        using var cover = Cover(7, 300, 450);
        Assert.Equal(CoverRenderErrors.InvalidRequest, CoverRenderer.Render(Png(cover), strictFormats: true, Request("middle")).Error);
    }
}
