namespace com.lifepixer.mangapixer.Tests.Server.Http;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.45.0 lane Q, D1 2.5's differential test: random grants / Private libraries / Incognito / "Show series information" off (per
/// library and for the instance) / tombstoned locations over a seeded tag index; every count the HTTP surface gives - the tag view's
/// total per value and every chip of every library-root panel - equals a brute-force recount in the test, round after round, with the
/// cache warm across rounds and an index write (entries removed + generation bump) half-way. Deterministic (fixed random seed).
/// </summary>
[Trait("Category", "Http")]
public sealed class FacetQueryDifferentialHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const int Libraries = 4;
    private const int NodesPerLibrary = 6;
    private const int Works = 18;
    private const int Values = 10; // below the container cap of 12, so a panel lists every value it has
    private const int Readers = 3;
    private const int Rounds = 6;

    private readonly MangaPixerWebApplicationFactory _factory;
    private readonly FacetQueryTestSupport _support;

    public FacetQueryDifferentialHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
        _support = new FacetQueryTestSupport(factory);
    }

    private sealed record Location(long NodeId, int Library, bool Tombstoned);

    private sealed record Entry(long EntryId, long WorkId, Location At, long ValueId);

    private sealed record Viewer(string Name, HttpClient Client, long UserId, bool IsAdmin, HashSet<int> Grants);

    [Fact]
    public async Task EveryCount_EqualsABruteForceRecount_AcrossRandomViewersAndSettings()
    {
        var random = new Random(1450);
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // ---- the seeded index ----
        var libIds = new long[Libraries];
        var libPublic = new string[Libraries];
        var locations = new List<Location>();
        for (var l = 0; l < Libraries; l++)
        {
            libPublic[l] = "dfl" + l;
            libIds[l] = await _support.LibraryAsync(libPublic[l], "Diff Library " + l);
            for (var n = 0; n < NodesPerLibrary; n++)
            {
                var tombstoned = random.NextDouble() < 0.15;
                var node = await _support.NodeAsync(libIds[l], null, $"dfn{l}x{n}", $"Diff Folder {l}-{n}", tombstoned: tombstoned);
                locations.Add(new Location(node, l, tombstoned));
            }
        }

        var values = new List<(long Id, string PublicId)>();
        for (var v = 0; v < Values; v++)
            values.Add(await _support.ValueAsync(FacetBucket.Genres, "dg" + v, "Diff Genre " + v));

        var free = locations.OrderBy(_ => random.Next()).ToList();
        var placements = new List<(long WorkId, Location At)>();
        for (var w = 0; w < Works && free.Count > 0; w++)
        {
            var work = await _support.WorkAsync($"node:{70000 + w}", $"Diff Work {w:00}");
            var count = Math.Min(free.Count, random.NextDouble() < 0.3 ? 2 : 1); // some works live in two places
            for (var i = 0; i < count; i++)
            {
                placements.Add((work, free[0]));
                free.RemoveAt(0);
            }
        }

        var entries = await _support.WithDbAsync(async db =>
        {
            var seeded = new List<(FacetEntryEntity Row, long WorkId, Location At, long ValueId)>();
            foreach (var (workId, at) in placements)
            {
                foreach (var (valueId, _) in values)
                {
                    if (random.NextDouble() >= 0.35)
                        continue;
                    var row = new FacetEntryEntity
                    {
                        WorkId = workId,
                        LocationNodeId = at.NodeId,
                        LibraryId = libIds[at.Library],
                        ValueId = valueId,
                        Sources = (int)FacetSources.Web,
                    };
                    db.FacetEntries.Add(row);
                    seeded.Add((row, workId, at, valueId));
                }
            }
            await db.SaveChangesAsync();
            return seeded.Select(s => new Entry(s.Row.Id, s.WorkId, s.At, s.ValueId)).ToList();
        });
        _support.Bump();

        // ---- the viewers ----
        var viewers = new List<Viewer> { new("admin", admin, await _support.UserIdAsync("admin"), true, [.. Enumerable.Range(0, Libraries)]) };
        for (var r = 0; r < Readers; r++)
        {
            var grants = Enumerable.Range(0, Libraries).Where(_ => random.NextDouble() < 0.5).ToHashSet();
            var (client, userId) = await _support.ReaderAsync(admin, "dfreader" + r, grants.Select(g => libIds[g]).ToArray());
            viewers.Add(new Viewer("reader" + r, client, userId, false, grants));
        }

        var checkedCounts = 0;
        try
        {
            for (var round = 0; round < Rounds; round++)
            {
                if (round == Rounds / 2)
                {
                    // An index write: drop about a fifth of the entries, then bump the generation as the writer does.
                    var dropped = entries.Where(_ => random.NextDouble() < 0.2).ToList();
                    var droppedIds = dropped.Select(d => d.EntryId).ToList();
                    await _support.WithDbAsync(db => db.FacetEntries.Where(e => droppedIds.Contains(e.Id)).ExecuteDeleteAsync());
                    entries = entries.Except(dropped).ToList();
                    _support.Bump();
                }

                var hidden = Enumerable.Range(0, Libraries).Where(_ => random.NextDouble() < 0.25).ToHashSet();
                var hiddenEverywhere = random.NextDouble() < 0.1;
                for (var l = 0; l < Libraries; l++)
                    await _support.SetSeriesInfoHiddenAsync(libIds[l], hidden.Contains(l));
                await _support.SetSeriesInfoHiddenEverywhereAsync(hiddenEverywhere);

                foreach (var viewer in viewers)
                {
                    var privateSet = Enumerable.Range(0, Libraries).Where(_ => random.NextDouble() < 0.3).ToHashSet();
                    await _support.SetPrivateAsync(viewer.UserId, privateSet.Select(p => libIds[p]).ToArray());
                    var incognito = random.NextDouble() < 0.5;

                    // Brute force: the libraries this viewer may see tags of, then distinct works per value.
                    var shown = hiddenEverywhere
                        ? new HashSet<int>()
                        : viewer.Grants.Where(l => !(incognito && privateSet.Contains(l)) && !hidden.Contains(l)).ToHashSet();
                    int Expected(long valueId) => entries
                        .Where(e => e.ValueId == valueId && !e.At.Tombstoned && shown.Contains(e.At.Library))
                        .Select(e => e.WorkId).Distinct().Count();

                    var context = $"round {round}, {viewer.Name}, incognito {incognito}";
                    foreach (var (valueId, publicId) in values)
                    {
                        var page = await FacetQueryTestSupport.GetAsync<SearchResultsDto>(viewer.Client,
                            "/api/v1/search?pageSize=100&f=" + Uri.EscapeDataString("genre:" + publicId), incognito);
                        var expected = Expected(valueId);
                        Assert.True(expected == page.TotalCount, $"{context}: value {valueId} total {page.TotalCount}, recount {expected}");
                        Assert.Equal(expected, page.Works!.Count);
                        Assert.Equal(expected > 0, page.Filters![0].Matched);
                        Assert.All(page.Works!.SelectMany(w => w.Locations),
                            loc => Assert.Contains(Array.IndexOf(libPublic, loc.LibraryId), shown));
                        checkedCounts++;
                    }

                    for (var l = 0; l < Libraries; l++)
                    {
                        var url = $"/api/v1/libraries/{libPublic[l]}/info";
                        if (!viewer.Grants.Contains(l))
                        {
                            Assert.Equal(System.Net.HttpStatusCode.NotFound, await FacetQueryTestSupport.StatusAsync(viewer.Client, url));
                            continue;
                        }
                        var panel = await FacetQueryTestSupport.GetAsync<InfoPanelDto>(viewer.Client, url, incognito);
                        var here = entries.Where(e => e.At.Library == l && !e.At.Tombstoned).ToList();
                        // 1.48.0 lane A2 (owner answer L1): the root panel of the viewer's Private library is empty under Incognito, as browse.
                        if (hiddenEverywhere || hidden.Contains(l) || here.Count == 0 || (incognito && privateSet.Contains(l)))
                        {
                            Assert.Equal(InfoPanelMode.None, panel.Mode);
                            continue;
                        }
                        var chips = panel.Buckets.SelectMany(b => b.Chips).ToDictionary(c => c.Id);
                        var expectedValues = here.Select(e => e.ValueId).Distinct().ToList();
                        Assert.Equal(expectedValues.Count, chips.Count);
                        foreach (var valueId in expectedValues)
                        {
                            var chip = chips[values.Single(v => v.Id == valueId).PublicId];
                            var expected = Expected(valueId);
                            Assert.True((expected >= 2 ? expected : (int?)null) == chip.Count,
                                $"{context}: library {l} value {valueId} chip {chip.Count}, recount {expected}");
                            Assert.Equal(here.Where(e => e.ValueId == valueId).Select(e => e.WorkId).Distinct().Count(), chip.Here);
                            checkedCounts++;
                        }
                    }
                }
            }
        }
        finally
        {
            await _support.SetSeriesInfoHiddenEverywhereAsync(false);
        }

        Assert.True(checkedCounts > Rounds * viewers.Count * Values, $"only {checkedCounts} counts checked");
    }
}
