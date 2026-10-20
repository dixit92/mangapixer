namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Features.Trackers;
using com.lifepixer.mangapixer.Server.Features.VolumeScan;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 1.46.0 (lane B, measured cost #1): the series folder cards' tracker rings come from ONE batched archive walk per page
/// (<see cref="TrackerSeriesResolver.ArchivesOfRecordsAsync"/>) instead of one <see cref="TrackerSeriesResolver.ArchivesAsync"/> per tracked card.
/// Equivalence: on seeded data with two trackers, split chapters, a declared edition, volume files read through a stored volume map, a unit
/// subfolder, a series linked from three libraries (one Private to the viewer, one the viewer cannot open), a doujinshi record, an untracked
/// series and an archive linked as its own series, every card's ring equals the ring the old per-record walk gives - with and without
/// Incognito - and the batched scopes equal the per-record scopes archive for archive. Synthetic names only.
/// </summary>
public sealed class TrackerSeriesRingBatchTests
{
    private const string ReaderName = "ringreader";
    private const string ReaderPassword = "Ring-Reader-Pass-12345";

    private sealed record Seeded(long LibraryA, string LibraryAPublic, long LibraryP, string LibraryPPublic, long ReaderId, List<long> Records);

    [Fact]
    public async Task EveryCardsRing_EqualsTheOldPerRecordWalk_WithAndWithoutIncognito()
    {
        await using var h = new TrackerTestHarness();
        var (seeded, reader) = await SeedAsync(h);

        var cards = 0;
        var rings = 0;
        foreach (var (library, incognito) in new[]
                 {
                     (seeded.LibraryAPublic, false), (seeded.LibraryAPublic, true), (seeded.LibraryPPublic, false),
                 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/libraries/{library}/browse?pageSize=100");
            if (incognito)
                request.Headers.Add("X-Incognito", "1");
            var response = await reader.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var items = json.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.NotEmpty(items);

            var expected = await OldPathRingsAsync(h, seeded.ReaderId, items.Select(i => i.GetProperty("id").GetString()!).ToList());
            foreach (var item in items)
            {
                var id = item.GetProperty("id").GetString()!;
                var actual = item.TryGetProperty("trackerRead", out var r) && r.ValueKind == JsonValueKind.Array
                    ? string.Join(",", r.EnumerateArray().Select(x => x.GetString()))
                    : null;
                Assert.True(expected.TryGetValue(id, out var want) ? want == actual : actual is null, $"card {id}: expected {want ?? "none"}, got {actual ?? "none"}");
                cards++;
                if (actual is not null)
                    rings++;
            }
        }

        // Not vacuous: both outcomes occur on the seeded cards.
        Assert.True(rings >= 6, $"rings {rings}");
        Assert.True(cards - rings >= 6, $"cards without a ring {cards - rings}");
    }

    [Fact]
    public async Task BatchedScopes_EqualThePerRecordScopes_ArchiveForArchive()
    {
        await using var h = new TrackerTestHarness();
        var (seeded, _) = await SeedAsync(h);
        var adminId = await h.UserIdAsync("admin");

        using var scope = h.Factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<TrackerSeriesResolver>();
        // A record nobody links, and duplicates, are answered too (no archives, the empty map - as ArchivesAsync).
        var asked = seeded.Records.Concat([seeded.Records[0], 999_999]).ToList();
        foreach (var userId in new[] { seeded.ReaderId, adminId })
        {
            var batched = await resolver.ArchivesOfRecordsAsync(userId, asked, readMarks: true, CancellationToken.None);
            Assert.Equal(asked.Distinct().Order(), batched.Keys.Order());
            foreach (var recordId in asked.Distinct())
            {
                var (archives, map) = await resolver.ArchivesAsync(userId, recordId, CancellationToken.None);
                Assert.Equal(archives, batched[recordId].Archives);
                Assert.Equal(JsonSerializer.Serialize(map), JsonSerializer.Serialize(batched[recordId].Map));
            }
            // Not vacuous: the scopes hold archives, read ones among them, and the reader's scope differs from the admin's.
            if (userId == seeded.ReaderId)
                Assert.Contains(batched.Values, s => s.Archives.Any(a => a.Read)); // the reader's own marks
            Assert.Contains(batched.Values, s => s.Map.Volumes.Count > 0);
        }
        var readerGolf = (await resolver.ArchivesAsync(seeded.ReaderId, seeded.Records[GolfIndex], CancellationToken.None)).Archives.Count;
        var adminGolf = (await resolver.ArchivesAsync(adminId, seeded.Records[GolfIndex], CancellationToken.None)).Archives.Count;
        Assert.Equal(4, readerGolf); // libraries A + P (Private), not X
        // The volume contents scan and ComicInfo reach the rows (a chapter proven inside the volume file; ComicInfo numbers).
        var lima = (await resolver.ArchivesAsync(seeded.ReaderId, seeded.Records[12], CancellationToken.None)).Archives;
        var mike = (await resolver.ArchivesAsync(seeded.ReaderId, seeded.Records[13], CancellationToken.None)).Archives;
        Assert.Equal(4, lima.Count);
        Assert.Equal([1m, 2m], mike.Select(a => a.Units.Chapter).Order());
        Assert.Equal(6, adminGolf);
    }

    [Fact]
    public async Task ThePagesSqlCommands_DoNotGrowWithTheTrackedSeriesCards()
    {
        await using var h = new TrackerTestHarness();
        var (seeded, _) = await SeedAsync(h);
        var folders = await h.DbAsync(db => db.CatalogNodes.AsNoTracking()
            .Where(n => n.LibraryId == seeded.LibraryA && n.ParentId == null && n.Kind == (int)CatalogNodeKind.Folder)
            .OrderBy(n => n.Id)
            .Select(n => new { n.PublicId, n.DisplayName })
            .ToListAsync());
        static CatalogNodeDto Card(string id, string name, string library) => new()
        {
            Id = id,
            ParentId = string.Empty,
            LibraryId = library,
            Kind = CatalogNodeKind.Folder,
            DisplayName = name,
            Availability = CatalogNodeAvailability.Available,
        };

        async Task<(int Commands, int Walked)> DecorateAsync(int count)
        {
            using var scope = h.Factory.Services.CreateScope();
            var marks = scope.ServiceProvider.GetRequiredService<TrackerCardMarks>();
            var cards = folders.Take(count).Select(f => Card(f.PublicId, f.DisplayName, seeded.LibraryAPublic)).ToList();
            using var counter = new EfCommandCounter();
            await marks.DecorateAsync(cards, seeded.ReaderId, null, scope.ServiceProvider.GetRequiredService<VolumeEntryService>(), CancellationToken.None);
            return (counter.Count, marks.SeriesWalked);
        }

        var two = await DecorateAsync(2);
        var all = await DecorateAsync(folders.Count);
        Assert.Equal(2, two.Walked);
        Assert.True(all.Walked >= 7, $"walked {all.Walked}");
        Assert.True(two.Commands > 0);
        // The per-record walk ran ~15 more commands for every further tracked card; the batched walk runs the same handful for any page
        // (a few more only for what this page's series hold: a deeper unit-folder level, the volume files' scanned contents).
        Assert.True(all.Commands <= two.Commands + 8, $"{two.Walked} tracked cards: {two.Commands} commands; {all.Walked}: {all.Commands}");
    }

    // ------------------------------------------------------------------------------------------------------------------------- seeding

    private const int GolfIndex = 7;

    /// <summary>The rings the per-record path gives (TrackerCardMarks before 1.46.0): one ArchivesAsync per tracked series card.</summary>
    private static async Task<Dictionary<string, string?>> OldPathRingsAsync(TrackerTestHarness h, long userId, IReadOnlyCollection<string> cardIds)
    {
        using var scope = h.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var resolver = scope.ServiceProvider.GetRequiredService<TrackerSeriesResolver>();
        var marks = scope.ServiceProvider.GetRequiredService<TrackerCardMarks>();
        var records = await TrackerSeriesResolver.OwnLinkedRecordsAsync(db, cardIds, CancellationToken.None);
        var synced = await marks.SyncedAsync(userId, records.Values.ToList(), CancellationToken.None);
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (card, recordId) in records)
        {
            if (!synced.TryGetValue(recordId, out var trackers))
            {
                result[card] = null;
                continue;
            }
            var (archives, map) = await resolver.ArchivesAsync(userId, recordId, CancellationToken.None);
            result[card] = TrackerCardMarks.SeriesRing(trackers, archives, map) is { } ring ? string.Join(",", ring) : null;
        }
        return result;
    }

    private static async Task<(Seeded Seeded, HttpClient Reader)> SeedAsync(TrackerTestHarness h)
    {
        await h.AdminAsync();
        await h.AllowAsync(TrackerSlugs.MangaUpdates);
        await h.AllowAsync(TrackerSlugs.MyAnimeList);
        await h.Factory.CreateUserAsync(ReaderName, ReaderPassword, isAdmin: false);
        var reader = await h.Factory.LoginAsync(ReaderName, ReaderPassword);
        await h.ConnectMangaUpdatesAsync(reader);
        await h.ConnectMalAsync(reader);
        var readerId = await h.UserIdAsync(ReaderName);

        var seeded = await h.DbAsync(async db =>
        {
            var n = Guid.NewGuid().ToString("N")[..8];
            LibraryEntity Library(string name) => new()
            {
                PublicId = $"rb-{name}-{n}",
                DisplayName = $"Ring {name}",
                RootPath = $"/synthetic/rb-{name}-{n}",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            var a = Library("a");
            var p = Library("p");
            var x = Library("x");
            db.Libraries.AddRange(a, p, x);
            await db.SaveChangesAsync();
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = readerId, LibraryId = a.Id, GrantedAt = DateTimeOffset.UtcNow });
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = readerId, LibraryId = p.Id, GrantedAt = DateTimeOffset.UtcNow });
            db.PrivateLibraries.Add(new PrivateLibraryEntity { UserId = readerId, LibraryId = p.Id, MarkedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();

            var serial = 0;
            async Task<CatalogNodeEntity> NodeAsync(long library, long? parent, CatalogNodeKind kind, string name)
            {
                var node = new CatalogNodeEntity
                {
                    PublicId = $"rb-{n}-{++serial}",
                    LibraryId = library,
                    ParentId = parent,
                    Kind = (int)kind,
                    DisplayName = name,
                    RelativePath = $"rb-{n}-{serial}",
                    PathKey = $"rb-{n}-{serial}",
                    SortKey = SortKey.ForNode(kind, name),
                    Availability = (int)CatalogNodeAvailability.Available,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                db.CatalogNodes.Add(node);
                await db.SaveChangesAsync();
                if (kind == CatalogNodeKind.Archive)
                    db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = node.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 10, ByteLength = 1024 });
                return node;
            }
            async Task<CatalogNodeEntity> FolderAsync(long library, long? parent, string name, params string[] archives)
            {
                var folder = await NodeAsync(library, parent, CatalogNodeKind.Folder, name);
                foreach (var archive in archives)
                    await NodeAsync(library, folder.Id, CatalogNodeKind.Archive, archive);
                return folder;
            }
            async Task<MetadataRecordEntity> RecordAsync(MetadataFormat? format = null)
            {
                var record = new MetadataRecordEntity
                {
                    PublicId = $"rb-rec-{n}-{++serial}",
                    Provider = "mangaupdates",
                    ExternalId = (80000 + serial).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Title = "Synthetic Ring Saga",
                    Format = (int?)format,
                    OriginStatus = (int)MetadataOriginStatus.Ongoing,
                    FetchedAt = DateTimeOffset.UtcNow,
                };
                db.MetadataRecords.Add(record);
                await db.SaveChangesAsync();
                return record;
            }
            async Task LinkAsync(CatalogNodeEntity node, MetadataRecordEntity record)
            {
                db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
                {
                    NodeId = node.Id,
                    LibraryId = node.LibraryId,
                    State = (int)SeriesLinkState.Confirmed,
                    RecordId = record.Id,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();
            }
            void Seen(MetadataRecordEntity record, string tracker, int? chapters, int? volumes = null) => db.TrackerEntries.Add(new TrackerEntryEntity
            {
                UserId = readerId,
                Tracker = tracker,
                RecordId = record.Id,
                State = (int)TrackerEntryState.Ok,
                RemoteId = record.ExternalId,
                RemoteChapters = chapters,
                RemoteVolumes = volumes,
                RemoteSeenAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            var records = new List<MetadataRecordEntity>();
            async Task<MetadataRecordEntity> SeriesAsync(string name, string[] archives, MetadataFormat? format = null)
            {
                var record = await RecordAsync(format);
                await LinkAsync(await FolderAsync(a.Id, null, name, archives), record);
                records.Add(record);
                return record;
            }

            // 0 Alpha: both trackers cover every chapter -> ring with both.
            var alpha = await SeriesAsync("Alpha Saga", ["Chapter 001", "Chapter 002", "Chapter 003", "Chapter 004"]);
            Seen(alpha, TrackerSlugs.MangaUpdates, 4);
            Seen(alpha, TrackerSlugs.MyAnimeList, 6);
            // 1 Bravo: one tracker behind -> none.
            var bravo = await SeriesAsync("Bravo Saga", ["Chapter 001", "Chapter 002", "Chapter 003", "Chapter 004"]);
            Seen(bravo, TrackerSlugs.MangaUpdates, 4);
            Seen(bravo, TrackerSlugs.MyAnimeList, 3);
            // 2 / 3 Charlie: split chapters - a part rings when its whole chapter is read.
            var charlie = await SeriesAsync("Charlie Saga", ["Chapter 001.1", "Chapter 001.2", "Chapter 002"]);
            Seen(charlie, TrackerSlugs.MangaUpdates, 2);
            var charlie2 = await SeriesAsync("Charlie Saga Two", ["Chapter 001.1", "Chapter 001.2", "Chapter 002"]);
            Seen(charlie2, TrackerSlugs.MangaUpdates, 1);
            // 4 Delta: volume files in a declared edition -> never by the tracker's volumes.
            var delta = await SeriesAsync("Delta Saga", ["Volume 01", "Volume 02"]);
            Seen(delta, TrackerSlugs.MangaUpdates, null, 2);
            db.DeclaredFacts.Add(new DeclaredFactEntity
            {
                LibraryId = a.Id,
                NodeId = db.NodeSeriesLinks.Local.Single(l => l.RecordId == delta.Id).NodeId,
                Key = DeclaredFactKeys.Volumes,
                Value = "2",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            // 5 Echo: volume files counted by their chapters through the stored volume map.
            var echo = await SeriesAsync("Echo Saga", ["Volume 01", "Volume 02"]);
            Seen(echo, TrackerSlugs.MangaUpdates, 10);
            await VolumeTestData.AddMapAsync(db, echo.Id, volumes: [(1, 1, 5), (2, 6, 10)]);
            // 6 Foxtrot: a unit subfolder ("Volumes") and a loose chapter.
            var foxtrot = await RecordAsync();
            var foxFolder = await FolderAsync(a.Id, null, "Foxtrot Saga", "Chapter 003");
            await FolderAsync(a.Id, foxFolder.Id, "Volumes", "Chapter 001", "Chapter 002");
            await LinkAsync(foxFolder, foxtrot);
            records.Add(foxtrot);
            Seen(foxtrot, TrackerSlugs.MangaUpdates, 3);
            // 7 Golf: one series in three libraries - A, P (Private to the reader) and X (not granted).
            var golf = await SeriesAsync("Golf Saga", ["Chapter 001", "Chapter 002"]);
            await LinkAsync(await FolderAsync(p.Id, null, "Golf Saga", "Chapter 003", "Chapter 004"), golf);
            await LinkAsync(await FolderAsync(x.Id, null, "Golf Saga", "Chapter 005", "Chapter 006"), golf);
            Seen(golf, TrackerSlugs.MangaUpdates, 4);
            // 8 Hotel: a doujinshi record (in the ring's scope like any series).
            var hotel = await SeriesAsync("Hotel Saga", ["Chapter 001", "Chapter 002"], MetadataFormat.Doujinshi);
            Seen(hotel, TrackerSlugs.MangaUpdates, 9);
            // 9 India: untracked.
            await SeriesAsync("India Saga", ["Chapter 001"]);
            // 10 Juliet: an archive at the root linked as its own series (a one-shot).
            var juliet = await RecordAsync();
            await LinkAsync(await NodeAsync(a.Id, null, CatalogNodeKind.Archive, "Juliet One-shot"), juliet);
            records.Add(juliet);
            Seen(juliet, TrackerSlugs.MangaUpdates, 1);
            // 11 Kilo: a second series in P only, tracked and covered.
            var kilo = await RecordAsync();
            await LinkAsync(await FolderAsync(p.Id, null, "Kilo Saga", "Chapter 001"), kilo);
            records.Add(kilo);
            Seen(kilo, TrackerSlugs.MangaUpdates, 1);
            // 12 Lima: a volume file whose scanned contents hold chapters 1-2 (the volume contents scan), next to loose chapters.
            var lima = await SeriesAsync("Lima Saga", ["Volume 01", "Chapter 001", "Chapter 002", "Chapter 003"]);
            Seen(lima, TrackerSlugs.MangaUpdates, 3);
            var limaFolder = db.NodeSeriesLinks.Local.Single(l => l.RecordId == lima.Id).NodeId;
            var limaNodes = await db.CatalogNodes.Where(c => c.ParentId == limaFolder).ToDictionaryAsync(c => c.DisplayName, c => c.Id);
            db.VolumeContents.Add(new VolumeContentEntity
            {
                VolumeNodeId = limaNodes["Volume 01"],
                VolumeContentVersion = 1,
                Volume = 1,
                FolderId = limaFolder,
                State = (int)VolumeContentRowState.Known,
                ChaptersJson = JsonSerializer.Serialize(new[] { "Chapter 001", "Chapter 002" }.Select((name, i) =>
                    new StoredFoundChapter(limaNodes[name], 1, 1 + i, 2 + i * 10, 11 + i * 10, 10, 10)), VolumeContentReader.Json),
                CandidatesKey = "k",
                ScannedAt = DateTimeOffset.UtcNow,
            });
            // 13 Mike: archives numbered only by their ComicInfo.
            var mike = await SeriesAsync("Mike Saga", ["Mike Story A", "Mike Story B"]);
            Seen(mike, TrackerSlugs.MangaUpdates, 2);
            var mikeFolder = db.NodeSeriesLinks.Local.Single(l => l.RecordId == mike.Id).NodeId;
            var mikeNodes = await db.CatalogNodes.Where(c => c.ParentId == mikeFolder).OrderBy(c => c.Id).Select(c => c.Id).ToListAsync();
            await VolumeTestData.AddComicInfoAsync(db, mikeNodes[0], null, "1");
            await VolumeTestData.AddComicInfoAsync(db, mikeNodes[1], null, "2");
            await db.SaveChangesAsync();

            // Some read marks (ArchivesOfRecordsAsync with readMarks reads them).
            var someArchives = await db.CatalogNodes.Where(c => c.LibraryId == a.Id && c.Kind == (int)CatalogNodeKind.Archive).OrderBy(c => c.Id)
                .Take(5).Select(c => c.Id).ToListAsync();
            foreach (var item in someArchives)
                db.ReadMarks.Add(new ReadMarkEntity { UserId = readerId, ItemId = item, Source = "manual", MarkedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();

            return new Seeded(a.Id, a.PublicId, p.Id, p.PublicId, readerId, records.Select(r => r.Id).ToList());
        });
        return (seeded, reader);
    }

    /// <summary>Counts the EF Core commands executed in the current async flow (DiagnosticListener; parallel-safe).</summary>
    private sealed class EfCommandCounter : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private static readonly AsyncLocal<EfCommandCounter?> s_current = new();
        private readonly IDisposable _all;
        private readonly List<IDisposable> _subscriptions = [];
        private int _count;

        public EfCommandCounter()
        {
            s_current.Value = this;
            _all = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public int Count => Volatile.Read(ref _count);

        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == DbLoggerCategory.Name)
            {
                lock (_subscriptions)
                    _subscriptions.Add(value.Subscribe(this));
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted.Name && ReferenceEquals(s_current.Value, this))
                Interlocked.Increment(ref _count);
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void Dispose()
        {
            s_current.Value = null;
            _all.Dispose();
            lock (_subscriptions)
            {
                foreach (var s in _subscriptions)
                    s.Dispose();
            }
        }
    }
}
