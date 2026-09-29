namespace com.lifepixer.mangapixer.Tests.Server.Media;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.MediaWorker.Images;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch.Golden;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Volumes;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Process test (1.29.0): the volume-cover pass downloads a (scripted, synthetic) MangaDex cover and a REAL spawned media
/// worker re-encodes it into the thumbnail WebP and hashes it (protocol v5 <c>cover_render</c> through the production
/// <see cref="WorkerCoverRenderer"/>); the scratch copy is gone afterwards. No real network, no third-party art.
/// </summary>
[Trait("Category", "Process")]
public sealed class VolumeCoverDownloadProcessTests(WorkerProcessFixture fixture) : IClassFixture<WorkerProcessFixture>
{
    [Fact]
    public async Task ADownloadedCover_IsReEncodedAndHashedByTheWorker_IntoTheDataRoot()
    {
        await using var db = await MetadataTestDb.CreateAsync();
        using var h = new VolumePassHarness(db);
        var options = fixture.CreatePoolOptions();
        var pool = new MediaWorkerPool(options, new JobScheduler(options), new ScratchWorkspaceManager(fixture.ScratchRoot),
            NullLogger<MediaWorkerPool>.Instance, NullLoggerFactory.Instance);
        await pool.StartAsync();
        try
        {
            h.RendererOverride = new WorkerCoverRenderer(pool);
            h.ImageBytes = SyntheticCovers.Png(5);
            await h.Auto.EnableAutomaticAsync();
            var folder = await db.AddFolderAsync(null, "Synthetic Shelf");
            await db.AddArchiveAsync(folder, "Synthetic Shelf v01.cbz");
            await db.AddLinkAsync(folder, await db.AddRecordAsync(MdFixtures.MuBerserk, "Berserk"), SeriesLinkState.Auto);

            await h.TickAsync();

            var cover = await db.Db.VolumeCovers.SingleAsync(c => c.State == (int)VolumeCoverState.Stored);
            var file = h.Store.PathFor(cover.PublicId, cover.StoredVersion);
            Assert.Equal(MagickFormat.WebP, new MagickImageInfo(file).Format);
            Assert.Equal(((int)SyntheticCovers.Width, (int)SyntheticCovers.Height), (cover.Width!.Value, cover.Height!.Value)); // never upscaled
            Assert.Equal(unchecked((long)ImageHasher.Hash(File.ReadAllBytes(file), ImageHashLimits.MaxDimension).Hash), cover.Hash);
            Assert.Empty(Directory.GetFiles(Path.Combine(h.Root, "scratch"), "*.img", SearchOption.AllDirectories));
        }
        finally
        {
            await pool.StopAsync();
            await pool.DisposeAsync();
        }
    }
}
