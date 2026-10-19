namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddFacetIndex migration (1.45.0 seam I0): a database at AddVolumeContentScan (1.44.0) with a library,
/// folders, a linked record, a declared creator and a stored author record upgrades additively - every row keeps its values, the
/// declared fact's new <c>ExternalRef</c> reads null - and the eight new tables take rows through the model with their unique keys
/// and delete rules: a node, record or library going takes its derived rows; an admin override keeps its creator (restrict).
/// </summary>
public sealed class AddFacetIndexMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddFacetIndexMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-facetmig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPixerDbContext NewContext(string dbPath) => new(
        new DbContextOptionsBuilder<MangaPixerDbContext>().ConfigureSqlite(dbPath).Options);

    private static CatalogNodeEntity Folder(long id, long libraryId, long? parentId, string name) => new()
    {
        Id = id,
        PublicId = "n" + id,
        LibraryId = libraryId,
        ParentId = parentId,
        Kind = (int)CatalogNodeKind.Folder,
        DisplayName = name,
        RelativePath = name,
        PathKey = name.ToLowerInvariant(),
        SortKey = "0" + name.ToLowerInvariant(),
        Availability = (int)CatalogNodeAvailability.Available,
        LastSeenScanRevision = 1,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task Migration_FromAddVolumeContentScan_IsAdditive_AndTheNewTablesKeepTheirKeysAndDeleteRules()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        await using (var db = NewContext(dbPath))
        {
            var all = db.Database.GetMigrations().ToList();
            var from = all.FindIndex(m => m.EndsWith("_AddVolumeContentScan", StringComparison.Ordinal));
            Assert.True(from > 0);
            Assert.True(all.FindIndex(m => m.EndsWith("_AddFacetIndex", StringComparison.Ordinal)) > from);
            await db.GetService<IMigrator>().MigrateAsync(all[from]);

            // Rows in the 1.44.0 shape (columns a later migration adds are skipped by the seed).
            await LegacySchemaSeed.InsertAsync(db, new LibraryEntity
            {
                Id = 1,
                PublicId = "lib1",
                DisplayName = "Lib",
                RootPath = "/synthetic/lib",
                CaseComparisonPolicy = "ordinal",
                State = "active",
                CatalogRevision = 4,
                CreatedAt = DateTimeOffset.UnixEpoch,
            });
            await LegacySchemaSeed.InsertAsync(db, Folder(7, 1, null, "Shelf"));
            await LegacySchemaSeed.InsertAsync(db, Folder(8, 1, 7, "Series"));
            await LegacySchemaSeed.InsertAsync(db, new MetadataRecordEntity
            {
                Id = 30,
                PublicId = "rec30",
                Provider = "mangaupdates",
                ExternalId = "424242",
                Title = "Synthetic Web Title",
                FetchedAt = DateTimeOffset.UnixEpoch,
            });
            await LegacySchemaSeed.InsertAsync(db, new NodeSeriesLinkEntity
            {
                NodeId = 8,
                LibraryId = 1,
                State = 1,
                RecordId = 30,
                CreatedAt = DateTimeOffset.UnixEpoch,
                UpdatedAt = DateTimeOffset.UnixEpoch,
            });
            await LegacySchemaSeed.InsertAsync(db, new DeclaredFactEntity
            {
                LibraryId = 1,
                NodeId = 8,
                Key = "creator",
                Value = "Synthetic Artist",
                Role = "artist",
                Position = 0,
                CreatedAt = DateTimeOffset.UnixEpoch,
                UpdatedAt = DateTimeOffset.UnixEpoch,
            });
            await LegacySchemaSeed.InsertAsync(db, new MetadataAuthorEntity
            {
                Provider = "mangaupdates",
                ExternalId = "9001",
                Name = "Synthetic Artist",
                OtherNamesJson = "[\"S. Artist\"]",
                FetchedAt = DateTimeOffset.UnixEpoch,
                Status = 0,
            });
        }

        await using (var db = NewContext(dbPath))
        {
            await DatabaseInitialization.MigrateToLatestAsync(db, _dir, path =>
            {
                File.WriteAllText(path, "snapshot");
                return Task.FromResult(true);
            });
        }

        await using (var db = NewContext(dbPath))
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());

            // Existing rows: untouched; the new column reads null; the new tables are empty.
            var fact = await db.DeclaredFacts.SingleAsync();
            Assert.Equal((1L, 8L, "creator", "Synthetic Artist", "artist"), (fact.LibraryId, fact.NodeId!.Value, fact.Key, fact.Value, fact.Role));
            Assert.Null(fact.ExternalRef);
            Assert.Equal("424242", (await db.MetadataRecords.SingleAsync()).ExternalId);
            Assert.Equal(30, (await db.NodeSeriesLinks.SingleAsync()).RecordId);
            Assert.Equal("Synthetic Artist", (await db.MetadataAuthors.SingleAsync()).Name);
            Assert.Equal(2, await db.CatalogNodes.CountAsync());
            Assert.Empty(await db.Creators.ToListAsync());
            Assert.Empty(await db.CreatorNames.ToListAsync());
            Assert.Empty(await db.CreatorProviderIds.ToListAsync());
            Assert.Empty(await db.CreatorOverrides.ToListAsync());
            Assert.Empty(await db.FacetWorks.ToListAsync());
            Assert.Empty(await db.FacetValues.ToListAsync());
            Assert.Empty(await db.FacetEntries.ToListAsync());
            Assert.Empty(await db.FacetIndexLibraries.ToListAsync());

            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
            fact.ExternalRef = "mangaupdates:9001";

            var creator = new CreatorEntity
            {
                PublicId = FacetPublicIds.NewCreatorId(),
                DisplayName = "Synthetic Artist",
                DisplayNameSource = (int)CreatorDisplayNameSource.Provider,
                CreatedAt = DateTimeOffset.UnixEpoch,
                UpdatedAt = DateTimeOffset.UnixEpoch,
            };
            db.Creators.Add(creator);
            await db.SaveChangesAsync();
            db.CreatorNames.AddRange(
                new CreatorNameEntity { CreatorId = creator.Id, Name = "Synthetic Artist", NameKey = "synthetic artist", Source = (int)CreatorNameSource.ProviderAuthor },
                new CreatorNameEntity { CreatorId = creator.Id, Name = "S. Artist", NameKey = "s artist", Source = (int)CreatorNameSource.ProviderAuthor });
            db.CreatorProviderIds.Add(new CreatorProviderIdEntity { CreatorId = creator.Id, Provider = "mangaupdates", ExternalId = "9001" });
            db.CreatorOverrides.Add(new CreatorOverrideEntity
            {
                Kind = (int)CreatorOverrideKind.DisplayName,
                Subject = "mangaupdates:9001",
                TargetCreatorId = creator.Id,
                Value = "S. Artist",
                CreatedAt = DateTimeOffset.UnixEpoch,
            });
            var work = new FacetWorkEntity
            {
                WorkKey = FacetWorkKey.Record(30),
                Kind = (int)FacetWorkKind.Record,
                RecordId = 30,
                Title = "Synthetic Web Title",
                SortKey = "synthetic web title",
                UpdatedAt = DateTimeOffset.UnixEpoch,
            };
            var nodeWork = new FacetWorkEntity
            {
                WorkKey = FacetWorkKey.Node(7),
                Kind = (int)FacetWorkKind.Node,
                Title = "Shelf",
                SortKey = "shelf",
                UpdatedAt = DateTimeOffset.UnixEpoch,
            };
            var value = new FacetValueEntity
            {
                PublicId = FacetPublicIds.NewValueId(),
                Bucket = (int)FacetBucket.Creators,
                ValueKey = "c:" + creator.Id,
                Display = "S. Artist",
                CreatorId = creator.Id,
            };
            var genre = new FacetValueEntity { PublicId = FacetPublicIds.NewValueId(), Bucket = (int)FacetBucket.Genres, ValueKey = "action", Display = "Action" };
            db.AddRange(work, nodeWork, value, genre);
            await db.SaveChangesAsync();
            db.FacetEntries.AddRange(
                new FacetEntryEntity
                {
                    WorkId = work.Id,
                    LocationNodeId = 8,
                    LibraryId = 1,
                    ValueId = value.Id,
                    Sources = (int)(FacetSources.Web | FacetSources.Declared),
                    Roles = (int)FacetRoles.Artist,
                },
                new FacetEntryEntity { WorkId = work.Id, LocationNodeId = 8, LibraryId = 1, ValueId = genre.Id, Sources = (int)FacetSources.Web },
                new FacetEntryEntity { WorkId = nodeWork.Id, LocationNodeId = 7, LibraryId = 1, ValueId = genre.Id, Sources = (int)FacetSources.ComicInfo });
            db.FacetIndexLibraries.Add(new FacetIndexLibraryEntity
            {
                LibraryId = 1,
                InputsKey = "k1",
                BuiltAt = DateTimeOffset.UnixEpoch,
                Works = 2,
                Entries = 3,
                ElapsedMs = 12,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext(dbPath))
        {
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
            Assert.Equal("mangaupdates:9001", (await db.DeclaredFacts.SingleAsync()).ExternalRef);
            var creator = await db.Creators.SingleAsync();
            Assert.True(FacetPublicIds.IsCreatorId(creator.PublicId));
            Assert.Equal(2, await db.CreatorNames.CountAsync(n => n.CreatorId == creator.Id));
            Assert.Equal(3, await db.FacetEntries.CountAsync());

            // Unique keys: one creator per provider id, one work per key, one value per (bucket, key), one entry per (value, location),
            // one spelling per creator.
            await AssertRejectedAsync(db, new CreatorProviderIdEntity { CreatorId = creator.Id, Provider = "mangaupdates", ExternalId = "9001" });
            await AssertRejectedAsync(db, new FacetWorkEntity
            {
                WorkKey = FacetWorkKey.Record(30),
                Kind = 0,
                Title = "x",
                SortKey = "x",
                UpdatedAt = DateTimeOffset.UnixEpoch,
            });
            await AssertRejectedAsync(db, new FacetValueEntity { PublicId = FacetPublicIds.NewValueId(), Bucket = (int)FacetBucket.Genres, ValueKey = "action", Display = "x" });
            var genreId = await db.FacetValues.Where(v => v.ValueKey == "action").Select(v => v.Id).SingleAsync();
            var workId = await db.FacetWorks.Where(w => w.RecordId == 30).Select(w => w.Id).SingleAsync();
            await AssertRejectedAsync(db, new FacetEntryEntity { WorkId = workId, LocationNodeId = 8, LibraryId = 1, ValueId = genreId });
            await AssertRejectedAsync(db, new CreatorNameEntity { CreatorId = creator.Id, Name = "S. Artist", NameKey = "s artist" });

            // An admin override keeps its creator: deleting the creator is refused.
            await Assert.ThrowsAnyAsync<Exception>(() => db.Creators.Where(c => c.Id == creator.Id).ExecuteDeleteAsync());

            // Derived rows go with their node, record and library.
            await db.CatalogNodes.Where(n => n.Id == 8).ExecuteDeleteAsync();
            Assert.Equal(1, await db.FacetEntries.CountAsync());
            await db.MetadataRecords.Where(r => r.Id == 30).ExecuteDeleteAsync();
            Assert.False(await db.FacetWorks.AnyAsync(w => w.Id == workId));
            await db.Libraries.Where(l => l.Id == 1).ExecuteDeleteAsync();
            Assert.Empty(await db.FacetEntries.ToListAsync());
            Assert.Empty(await db.FacetIndexLibraries.ToListAsync());
            Assert.Equal(1, await db.Creators.CountAsync()); // creators are not library data: stable ids
        }
    }

    private static async Task AssertRejectedAsync<T>(MangaPixerDbContext db, T entity) where T : class
    {
        db.Add(entity);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
    }
}
