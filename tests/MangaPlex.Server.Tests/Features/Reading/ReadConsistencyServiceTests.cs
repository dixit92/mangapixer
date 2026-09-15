namespace com.lifepixer.mangaplex.Tests.Server.Features.Reading;

using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Core.Reading;
using com.lifepixer.mangaplex.Server.Features.Auth;
using com.lifepixer.mangaplex.Server.Features.Reading;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests for read-state consistency:
/// clear-mark = full reset (rule 1), manual mark-read = read-at-end (rule 3), the
/// open-position rule end-to-end through <see cref="ReadingStateService.GetProgressAsync"/>
/// (rule 2), re-read keeps the mark dominant (rule 4), and the shared reset primitive
/// used by the folder "unread" path.
/// </summary>
public sealed class ReadConsistencyServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPlexDbContext> _options;

    public ReadConsistencyServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangaplex-readcons-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        var cs = DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "rc.db"));
        _options = new DbContextOptionsBuilder<MangaPlexDbContext>().UseSqlite(cs).Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPlexDbContext db, long userId, long folderId, long itemId)> SetupAsync(int pageCount = 10)
    {
        var db = new MangaPlexDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var library = new LibraryEntity { PublicId = OpaqueId.Encode(1), DisplayName = "T", RootPath = "/p/t", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        var user = new UserEntity
        {
            PublicId = OpaqueId.Encode(2),
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            IsActive = true,
            IsAdmin = true,
            PasswordHash = "h",
            SecurityStamp = "s",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var folder = new CatalogNodeEntity
        {
            PublicId = OpaqueId.Encode(50),
            LibraryId = library.Id,
            ParentId = null,
            Kind = (int)CatalogNodeKind.Folder,
            DisplayName = "F",
            RelativePath = "F",
            PathKey = "F",
            SortKey = "0F",
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(folder);
        await db.SaveChangesAsync();

        var node = new CatalogNodeEntity
        {
            PublicId = OpaqueId.Encode(100),
            LibraryId = library.Id,
            ParentId = folder.Id,
            Kind = (int)CatalogNodeKind.Archive,
            DisplayName = "A.cbz",
            RelativePath = "F/A.cbz",
            PathKey = "F/A.cbz",
            SortKey = "1A",
            Availability = (int)CatalogNodeAvailability.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();

        db.ArchiveItems.Add(new ArchiveItemEntity
        {
            NodeId = node.Id,
            ArchiveFormat = 0,
            ByteLength = 1024,
            ModificationTicks = 0,
            ContentVersion = 1,
            AnalysisState = 0,
            PageCount = pageCount,
        });
        await db.SaveChangesAsync();

        return (db, user.Id, folder.Id, node.Id);
    }

    private static async Task SetAlwaysOpenFromStartAsync(ReadingStateService service, long userId, bool on)
        => await service.SetPreferencesAsync(userId, new UserPreferencesDto { AlwaysOpenReadFromStart = on });

    // --- Rule 2: open-position rule end to end ---

    [Fact]
    public async Task OpenRule_NoMark_ResumesAtSavedPosition()
    {
        var (db, userId, _, itemId) = await SetupAsync();
        try
        {
            var service = new ReadingStateService(db, new LibraryAuthorizationService(db));
            await service.UpdateProgressAsync(userId, itemId, 4, 1, mutationId: "m1"); // InProgress, no mark

            var p = await service.GetProgressAsync(userId, itemId);
            Assert.False(await service.IsReadAsync(userId, itemId));
            Assert.Equal(4, p!.OpenPageIndex); // resumes regardless of the preference
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task OpenRule_Finished_OpensAtStart_EvenWithOptionOff()
    {
        var (db, userId, _, itemId) = await SetupAsync();
        try
        {
            var service = new ReadingStateService(db, new LibraryAuthorizationService(db));
            await service.UpdateProgressAsync(userId, itemId, 9, 1, mutationId: "m1"); // completes + auto-marks read

            var p = await service.GetProgressAsync(userId, itemId);
            Assert.True(await service.IsReadAsync(userId, itemId));
            Assert.Equal(9, p!.PageIndex);        // stored position is untouched (non-destructive)
            Assert.Equal(0, p.OpenPageIndex);      // but it opens at the start
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task OpenRule_ReadMidArchive_OptionOff_Resumes_OptionOn_OpensAtStart()
    {
        var (db, userId, _, itemId) = await SetupAsync();
        try
        {
            var service = new ReadingStateService(db, new LibraryAuthorizationService(db));
            // Finish (mark read), then re-read back to page 4 — mark persists, position mid.
            await service.UpdateProgressAsync(userId, itemId, 9, 1, mutationId: "m1");
            await service.UpdateProgressAsync(userId, itemId, 4, 1, mutationId: "m2");
            Assert.True(await service.IsReadAsync(userId, itemId)); // rule 4: still read

            var off = await service.GetProgressAsync(userId, itemId);
            Assert.Equal(4, off!.OpenPageIndex); // option off (default) -> resume the re-read spot

            await SetAlwaysOpenFromStartAsync(service, userId, true);
            var on = await service.GetProgressAsync(userId, itemId);
            Assert.Equal(0, on!.OpenPageIndex); // option on -> page 1, non-destructively
            Assert.Equal(4, on.PageIndex);       // stored Ordinal never rewritten by the toggle
        }
        finally { await db.DisposeAsync(); }
    }

    // --- Rule 3: manual mark-read records read-at-the-end ---

    [Fact]
    public async Task ManualMark_MidRead_MovesToLastPage_AndOpensAtStart()
    {
        var (db, userId, _, itemId) = await SetupAsync();
        try
        {
            var service = new ReadingStateService(db, new LibraryAuthorizationService(db));
            await service.UpdateProgressAsync(userId, itemId, 3, 1, mutationId: "m1"); // mid-read, no mark
            await service.SetItemReadAsync(userId, itemId, read: true);                 // manual mark

            var p = await service.GetProgressAsync(userId, itemId);
            Assert.True(await service.IsReadAsync(userId, itemId));
            Assert.Equal(ReadingState.Completed, p!.State);
            Assert.Equal(9, p.PageIndex);   // recorded at the last page
            Assert.Equal(0, p.OpenPageIndex); // so it opens at the start
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task ManualMark_DropsOutOfContinueReading()
    {
        var (db, userId, _, itemId) = await SetupAsync();
        try
        {
            var service = new ReadingStateService(db, new LibraryAuthorizationService(db));
            await service.UpdateProgressAsync(userId, itemId, 3, 1, mutationId: "m1");
            Assert.Contains(await service.GetContinueReadingAsync(userId), e => e.PageIndex == 3);

            await service.SetItemReadAsync(userId, itemId, read: true);
            Assert.DoesNotContain(await service.GetContinueReadingAsync(userId), e => e.ItemId == OpaqueId.Encode(itemId));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task ManualMark_UnknownPageCount_JustAddsMark()
    {
        var (db, userId, _, itemId) = await SetupAsync();
        try
        {
            // Clear PageCount to simulate an unanalyzed archive.
            var item = await db.ArchiveItems.FirstAsync(a => a.NodeId == itemId);
            item.PageCount = null;
            await db.SaveChangesAsync();

            var service = new ReadingStateService(db, new LibraryAuthorizationService(db));
            await service.SetItemReadAsync(userId, itemId, read: true);

            Assert.True(await service.IsReadAsync(userId, itemId));
            // No last page to compute -> no progress row written; open-rule still -> page 1.
            Assert.False(await db.ReadingProgress.AnyAsync(p => p.UserId == userId && p.ItemId == itemId));
            var p = await service.GetProgressAsync(userId, itemId);
            Assert.Equal(0, p!.OpenPageIndex);
        }
        finally { await db.DisposeAsync(); }
    }

    // --- Rule 1: clear-mark = full reset (shared primitive) ---

    [Fact]
    public async Task ClearMark_OnReadingItem_AlsoResetsProgress()
    {
        // A multi-select "mark unread" can hit a Reading (InProgress, no mark) item via
        // the single-item clear; the full reset must still wipe its progress.
        var (db, userId, _, itemId) = await SetupAsync();
        try
        {
            var service = new ReadingStateService(db, new LibraryAuthorizationService(db));
            await service.UpdateProgressAsync(userId, itemId, 3, 1, mutationId: "m1"); // Reading, no mark

            await service.SetItemReadAsync(userId, itemId, read: false);

            Assert.False(await db.ReadingProgress.AnyAsync(p => p.UserId == userId && p.ItemId == itemId));
            var p = await service.GetProgressAsync(userId, itemId);
            Assert.Equal(ReadingState.Unread, p!.State);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task FolderUnread_ResetsMarkAndProgress_ForDescendants()
    {
        var (db, userId, folderId, itemId) = await SetupAsync();
        try
        {
            var service = new ReadingStateService(db, new LibraryAuthorizationService(db));
            await service.UpdateProgressAsync(userId, itemId, 9, 1, mutationId: "m1"); // completed + marked

            var result = await service.SetFolderReadAsync(userId, folderId, read: false);
            Assert.NotNull(result);
            Assert.Equal(1, result!.Affected);
            Assert.False(await service.IsReadAsync(userId, itemId));
            Assert.False(await db.ReadingProgress.AnyAsync(p => p.UserId == userId && p.ItemId == itemId));
        }
        finally { await db.DisposeAsync(); }
    }
}
