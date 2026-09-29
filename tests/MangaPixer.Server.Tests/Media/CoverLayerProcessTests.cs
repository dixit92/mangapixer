namespace com.lifepixer.mangapixer.Tests.Server.Media;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.MediaWorker.Images;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Covers;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch.Golden;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Process tests of the cover layer (1.29.0): a DRAWN jacket spread in a real archive, cropped by a real spawned worker
/// (protocol 5 <c>cover_render</c>) through <see cref="CoverCropService"/> into the data root's crop store, and a full
/// decision with real hashes - the direction guess (Japanese origin: right-to-left, the left half) is wrong for this book,
/// the web cover matches the OTHER half, so the other half wins.
/// </summary>
[Trait("Category", "Process")]
public sealed class CoverLayerProcessTests(WorkerProcessFixture fixture) : IClassFixture<WorkerProcessFixture>
{
    private async Task<MediaWorkerPool> StartPoolAsync()
    {
        var options = fixture.CreatePoolOptions();
        var pool = new MediaWorkerPool(options, new JobScheduler(options), new ScratchWorkspaceManager(fixture.ScratchRoot),
            NullLogger<MediaWorkerPool>.Instance, NullLoggerFactory.Instance);
        await pool.StartAsync();
        return pool;
    }

    /// <summary>Drawn cover (seed 7) on the left, a spine, drawn cover (seed 8) on the right.</summary>
    private static byte[] SpreadPng()
    {
        using var canvas = new MagickImage(MagickColors.Black, SyntheticCovers.Width * 2 + 30, SyntheticCovers.Height);
        using var left = SyntheticCovers.Draw(7);
        using var right = SyntheticCovers.Draw(8);
        canvas.Composite(left, 0, 0, CompositeOperator.Over);
        canvas.Composite(right, (int)SyntheticCovers.Width + 30, 0, CompositeOperator.Over);
        canvas.Format = MagickFormat.Png;
        return canvas.ToByteArray();
    }

    /// <summary>A ready archive whose source is a real zip holding the drawn spread as page 1.</summary>
    private static async Task<CatalogNodeEntity> AddSpreadBookAsync(CoverLayerTestKit kit, CatalogNodeEntity folder, string name)
    {
        var node = await kit.AddBookAsync(folder, name, (int)SyntheticCovers.Width * 2 + 30, (int)SyntheticCovers.Height, 0UL);
        var path = Path.Combine(kit.LibraryRoot, node.RelativePath);
        File.Delete(path);
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var s = zip.CreateEntry("page001.png").Open();
            s.Write(SpreadPng());
        }
        var info = new FileInfo(path);
        var item = await kit.Db.Db.ArchiveItems.SingleAsync(a => a.NodeId == node.Id);
        item.ModificationTicks = info.LastWriteTimeUtc.Ticks;
        item.ByteLength = info.Length;
        await kit.Db.Db.SaveChangesAsync();
        return node;
    }

    [Fact]
    public async Task ASpreadIsCroppedByTheWorker_IntoTheCropStore_AndTheOtherHalfWinsWhenItMatchesTheWebCover()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var folder = await kit.Db.AddFolderAsync(null, "Series");
        var record = await kit.Db.AddRecordAsync("301", "Series"); // Japanese origin: right-to-left, the left half first
        await kit.Db.AddLinkAsync(folder, record);
        var companion = await kit.AddCompanionAsync(record);
        var book = await AddSpreadBookAsync(kit, folder, "Series v01");
        var seed8 = ImageHasher.Hash(SyntheticCovers.Png(8), ImageHashLimits.MaxDimension).Hash;
        await kit.AddStoredCoverAsync(companion, 1, "en", seed8);

        var pool = await StartPoolAsync();
        try
        {
            var renderer = new WorkerCoverRenderer(pool);
            var crop = await new CoverCropService(kit.Db.Db, renderer, kit.Files).EnsureAsync(book.Id, CoverCropSide.Right, rerender: true, default);
            Assert.NotNull(crop);
            Assert.StartsWith(kit.Files.CropsRoot, crop!.Path, StringComparison.Ordinal);
            Assert.Equal(MagickFormat.WebP, new MagickImageInfo(crop.Path).Format);
            Assert.True(CoverHash.Distance(crop.Hash!.Value, seed8) <= CoverHash.SameMaxDistance);
            // Nothing but the crop is left in the crop store (no temp file).
            Assert.Single(Directory.EnumerateFiles(kit.Files.CropsRoot, "*", SearchOption.AllDirectories));

            Assert.Equal(CoverDecisionOutcome.Decided, await kit.Decisions(renderer, new WorkerCoverHasher(pool)).DecideAsync(book.Id, default));
            var auto = await kit.AutoAsync(book.Id);
            Assert.Equal((int)AutoCoverSource.Crop, auto!.Source);
            Assert.Equal((int)CoverCropSide.Right, auto.CropSide);
            Assert.Equal((int)AutoCoverReason.SpreadOtherSide, auto.Reason);
        }
        finally
        {
            await pool.StopAsync();
            await pool.DisposeAsync();
        }
    }
}
