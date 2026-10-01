namespace com.lifepixer.mangapixer.Tests.Server.Features.Trash;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests for Clean bundles (1.31.0): files in the data root that no row references go; referenced files, files
/// younger than an hour and files the stores did not write stay. Also: library delete (which shares the node purger) now
/// removes the library's cover crops too.
/// </summary>
public sealed class BundleCleanerTests : IDisposable
{
    private readonly TrashTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task OnlyOldUnreferencedFilesOfTheStores_AreCleaned()
    {
        await _kit.InitAsync();
        var lib = await _kit.AddLibraryAsync("lib1");
        var item = await _kit.AddNodeAsync(lib, null, 1, null);
        var kept = _kit.WriteArchiveFiles(item);

        long recordId;
        const string coverId = "vc0123456789abcdef";
        await using (var db = _kit.NewContext())
        {
            var record = new MetadataRecordEntity { PublicId = "r1", Provider = "mangadex", ExternalId = "1", Title = "Synthetic", ImageVersion = 2, ImageState = 1, FetchedAt = TrashTestKit.Now };
            db.MetadataRecords.Add(record);
            await db.SaveChangesAsync();
            recordId = record.Id;
            db.VolumeCovers.Add(new VolumeCoverEntity
            {
                PublicId = coverId, ProviderRecordId = record.Id, Kind = 0, Volume = 1, Locale = "en", RemoteId = "x", RemoteFile = "x.jpg",
                State = 1, StoredVersion = 3, ListedAt = TrashTestKit.Now,
            });
            await db.SaveChangesAsync();
        }

        var keptFiles = new[]
        {
            kept.Thumbnail, kept.Left, kept.Right,
            TrashTestKit.WriteFile(_kit.VolumeCovers.PathFor(coverId, 3)),
            TrashTestKit.WriteFile(Path.Combine(_kit.Images.Root, $"{recordId}-2.png")),
            // Unreferenced but younger than an hour: a write may be in flight.
            TrashTestKit.WriteFile(_kit.Thumbnails.GetThumbnailPath(item, 3), age: TimeSpan.FromMinutes(20)),
            // Not a name any store writes: never touched.
            TrashTestKit.WriteFile(Path.Combine(_kit.Thumbnails.ThumbnailsRoot, "notes.webp")),
            TrashTestKit.WriteFile(Path.Combine(_kit.CoverFiles.CropsRoot, "zz", "0abc-2-l.webp")),
            TrashTestKit.WriteFile(Path.Combine(_kit.Images.Root, "readme.txt")),
        };
        var cleaned = new[]
        {
            TrashTestKit.WriteFile(_kit.Thumbnails.GetThumbnailPath(item, 1), 10),       // an older content version
            TrashTestKit.WriteFile(_kit.Thumbnails.GetThumbnailPath(99_999, 2), 10),    // no such item
            TrashTestKit.WriteFile(_kit.Thumbnails.GetThumbnailPath(item, 2) + ".tmp", 10),
            TrashTestKit.WriteFile(_kit.CoverFiles.CropPath(item, 1, CoverCropSide.Left), 10),
            TrashTestKit.WriteFile(_kit.CoverFiles.CropPath(88_888, 2, CoverCropSide.Right), 10),
            TrashTestKit.WriteFile(_kit.VolumeCovers.PathFor(coverId, 2), 10),
            TrashTestKit.WriteFile(_kit.VolumeCovers.PathFor("vcffffffffffffffff", 1), 10),
            TrashTestKit.WriteFile(Path.Combine(_kit.Images.Root, $"{recordId}-1.jpg"), 10),
            TrashTestKit.WriteFile(Path.Combine(_kit.Images.Root, "77777-1.webp"), 10),
            TrashTestKit.WriteFile(Path.Combine(_kit.Images.Root, Guid.NewGuid().ToString("N") + ".tmp"), 10),
        };

        await using (var db = _kit.NewContext())
        {
            var preview = await _kit.Bundles(db).PreviewAsync(default);
            Assert.Equal((cleaned.Length, cleaned.Length * 10L), (preview.Files, preview.Bytes));
            Assert.All(cleaned, f => Assert.True(File.Exists(f))); // a preview deletes nothing

            await using var db2 = _kit.NewContext();
            var result = await _kit.Service(db2).CleanBundlesAsync(automatic: true, actor: null, default);
            Assert.Equal((cleaned.Length, cleaned.Length * 10L), (result.Files, result.Bytes));
        }

        Assert.All(cleaned, f => Assert.False(File.Exists(f), "should be cleaned"));
        Assert.All(keptFiles, f => Assert.True(File.Exists(f), "should be kept"));
        await using (var db = _kit.NewContext())
        {
            var row = await db.AppSettings.SingleAsync();
            Assert.Equal((cleaned.Length, true), (row.BundlesLastCleanedFiles, row.BundlesLastCleanedAutomatic));
        }
    }

    [Fact]
    public async Task LibraryDelete_StillRemovesEverything_AndNowTheCoverCropsToo()
    {
        await _kit.InitAsync();
        var lib = await _kit.AddLibraryAsync("lib1");
        var folder = await _kit.AddNodeAsync(lib, null, 0, null);
        var item = await _kit.AddNodeAsync(lib, folder, 1, null);
        await _kit.AddUserStateAsync(item);
        var files = _kit.WriteArchiveFiles(item);

        await using (var db = _kit.NewContext())
        {
            var service = new LibraryRegistrationService(db, new AppRootOptions { DataRoot = _kit.DataRoot }, _kit.Thumbnails, coverFiles: _kit.CoverFiles);
            Assert.True(await service.DeleteAsync(lib));
        }

        await using (var db = _kit.NewContext())
        {
            Assert.Equal(0, await db.CatalogNodes.CountAsync());
            Assert.Equal(0, await db.ReadingProgress.CountAsync());
            Assert.Equal(0, await db.ReadMarks.CountAsync());
            Assert.Equal(0, await db.Bookmarks.CountAsync());
            Assert.Equal(0, await db.ItemReaderOverrides.CountAsync());
            Assert.Equal(0, await db.Favorites.CountAsync());
        }
        Assert.False(File.Exists(files.Thumbnail));
        Assert.False(File.Exists(files.Left));
        Assert.False(File.Exists(files.Right));
    }
}
