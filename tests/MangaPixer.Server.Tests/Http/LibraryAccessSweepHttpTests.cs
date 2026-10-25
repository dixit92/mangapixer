namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Export;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Trackers;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.48.0 lane A (access invariants, gap 1): EVERY non-admin route, swept through the real pipeline as a signed-in reader against one
/// seeded host - three libraries: G (granted), U (NOT granted; the reader's own rows there are left over from a revoked grant) and P
/// (granted, and the reader's own Private library). Each library's names carry a canary word that appears nowhere else.
/// <list type="bullet">
/// <item>A route that takes a library, node, item, series or bookmark id (path or query) is in <see cref="IdRoutes"/> with its documented
/// answers: the granted control (proves the parameter map reaches the code), U (the "not yours" answer), and P under Incognito (hidden
/// like browse, or reachable by direct URL by design - <c>LibraryAuthorizationService.GetVisibleLibraryIdsAsync</c>). No answer may carry
/// U's canary; a hidden one may not carry P's either; nothing answers 5xx; and no U row of anyone changes.</item>
/// <item>Every other GET (lists, the caller's own settings) is swept automatically: no U canary, and no P canary under Incognito.</item>
/// <item>Writes without such an id are <see cref="Excluded"/> with a reason; routes that carry ids in the BODY are in
/// <see cref="BodyIdRoutes"/> and checked by <see cref="BodyIds_NeverReachAnUngrantedLibrary"/>.</item>
/// </list>
/// A new route fails <see cref="Sweep_EveryRouteIsClassified"/> until it is classified here; a new parameter name fails the sweep until it
/// is mapped in <see cref="Fill"/>. The table is printed in full on any failure.
/// </summary>
public sealed class LibraryAccessSweepHttpTests
{
    internal const string GrantedWord = "Gqbright";
    internal const string HiddenWord = "Zqhidden";
    internal const string PrivateWord = "Xqprivate";

    private static readonly string[] AdminPrefixes = ["api/v1/admin", "api/v1/operations"];

    /// <summary>The documented answers of a route: granted control, ungranted (U), the reader's Private library under Incognito (P).</summary>
    private sealed record Expect(int Control, int Ungranted, int Private, bool HiddenUnderIncognito);

    private static Expect Answer(int control, int ungranted, int @private, bool hidden = false) => new(control, ungranted, @private, hidden);

    /// <summary>Every route that takes a library / node / item / series / bookmark id, with its documented answers (method + template).</summary>
    private static readonly Dictionary<string, Expect> IdRoutes = new(StringComparer.Ordinal)
    {
    };

    /// <summary>Routes outside the id sweep, with the reason. Admin-only routes are swept by <see cref="AdminRouteAccessSweepHttpTests"/>.</summary>
    private static readonly Dictionary<string, string> Excluded = new(StringComparer.Ordinal)
    {
        ["POST api/v1/auth/activate"] = "anonymous account activation by one-time token; no library or node id",
        ["POST api/v1/auth/change-password"] = "the caller's own password",
        ["POST api/v1/auth/login"] = "sign-in; no library or node id",
        ["POST api/v1/auth/logout"] = "the caller's own session",
        ["POST api/v1/auth/setup"] = "first-run setup; refuses once a user exists",
        ["PUT api/v1/reading/library-preferences"] = "the caller's own view settings; no library or node id",
        ["PUT api/v1/reading/preferences"] = "the caller's own reader settings; no library or node id",
        ["PUT api/v1/reading/preferences/appearance"] = "the caller's own theme; no library or node id",
        ["POST api/v1/trackers/{slug}/connect"] = "the caller's own tracker account (slug = tracker, not content)",
        ["POST api/v1/trackers/{slug}/connect/password"] = "the caller's own tracker account (slug = tracker, not content)",
        ["POST api/v1/trackers/{slug}/connect/start"] = "the caller's own tracker account (slug = tracker, not content)",
        ["POST api/v1/trackers/{slug}/disconnect"] = "the caller's own tracker account; would end the sweep's connection",
        ["POST api/v1/trackers/{slug}/preview"] = "the caller's own tracker list (first-connect preview); no library or node id",
        ["PUT api/v1/trackers/{slug}/settings"] = "the caller's own tracker settings; no library or node id",
    };

    /// <summary>Writes that carry library / node / series ids in their BODY - checked one by one in <see cref="BodyIds_NeverReachAnUngrantedLibrary"/>.</summary>
    private static readonly string[] BodyIdRoutes =
    [
        "POST api/v1/trackers/{slug}/entries/batch",
        "PUT api/v1/reading/home-libraries",
        "PUT api/v1/reading/private-libraries",
        "PUT api/v1/trackers/sync-overrides",
    ];

    private enum Dimension
    {
        Control,
        Ungranted,
        Private,
        Mixed,
    }

    private sealed record Lib(
        string Key, string Word, long Id, string PublicId, long SeriesId, string SeriesPublicId, long ArtistId, string ArtistPublicId,
        IReadOnlyList<(long Id, string PublicId)> Chapters, IReadOnlyList<(long Id, string PublicId)> Stories, long RecordId, string RecordPublicId,
        string TankPublicId)
    {
        public long ReaderBookmarkId { get; set; }
        public long AdminBookmarkId { get; set; }

        /// <summary>The library's ids never sent as a parameter: an answer that names one learnt it from the library itself.</summary>
        public IEnumerable<string> UnrequestedIds => [Chapters[2].PublicId, Chapters[3].PublicId, Stories[0].PublicId, Stories[1].PublicId];
    }

    private sealed record World(TrackerTestHarness H, HttpClient Admin, AccessReader Reader, Lib G, Lib U, Lib P)
    {
        public Lib Of(Dimension d) => d switch { Dimension.Ungranted => U, Dimension.Private => P, _ => G };
    }

    // ---------------------------------------------------------------- the sweeps

    [Fact]
    public async Task Sweep_EveryRouteIsClassified()
    {
        await using var h = new TrackerTestHarness();
        var endpoints = AccessSweepSupport.Endpoints(h.Factory.Services);
        var (idRoutes, listings, others) = Classify(endpoints);

        var problems = new List<string>();
        problems.AddRange(idRoutes.Where(k => !IdRoutes.ContainsKey(k)).Select(k => "id route not in IdRoutes: " + k));
        problems.AddRange(IdRoutes.Keys.Where(k => !idRoutes.Contains(k)).Select(k => "IdRoutes entry with no such id route: " + k));
        problems.AddRange(others.Where(k => !Excluded.ContainsKey(k) && !BodyIdRoutes.Contains(k)).Select(k => "write route not classified: " + k));
        problems.AddRange(Excluded.Keys.Concat(BodyIdRoutes).Where(k => !others.Contains(k)).Select(k => "classified route that does not exist: " + k));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        // The audit's census at 1.47.0: 102 non-admin (method, route) pairs, 70 of them with a parameter.
        Assert.True(idRoutes.Count + listings.Count + others.Count >= 95, $"only {idRoutes.Count + listings.Count + others.Count} routes classified");
    }

    [Fact]
    public async Task Sweep_IdRoutes_AnswerUngrantedAndPrivateIdsAsDocumented_AndLeakNothing()
    {
        var w = await WorldAsync();
        await using var host = w.H;
        var endpoints = AccessSweepSupport.Endpoints(w.H.Factory.Services);
        var (idRoutes, _, _) = Classify(endpoints);
        var before = await SnapshotAsync(w, w.U);

        var observed = new SortedDictionary<string, (int Control, int Ungranted, int Private, string Notes)>(StringComparer.Ordinal);
        var failures = new List<string>();
        // GETs first (all dimensions), then the writes: the answers do not depend on which write ran before.
        var ordered = endpoints
            .SelectMany(e => AccessSweepSupport.MethodsOf(e).Select(m => (Method: m, Endpoint: e, Key: AccessSweepSupport.Key(m, e))))
            .Where(r => idRoutes.Contains(r.Key))
            .OrderBy(r => r.Method == "GET" ? 0 : 1).ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToList();
        foreach (var (method, endpoint, key) in ordered)
        {
            var notes = new StringBuilder();
            var codes = new Dictionary<Dimension, int>();
            foreach (var dimension in new[] { Dimension.Control, Dimension.Ungranted, Dimension.Private, Dimension.Mixed })
            {
                if (dimension == Dimension.Mixed && !TakesLibraryAndNode(endpoint))
                    continue;
                var (url, body) = Build(w, endpoint, method, dimension, failures);
                var client = dimension == Dimension.Private ? w.Reader.Incognito : w.Reader.Client;
                using var request = AccessSweepSupport.Request(method, url);
                if (body is not null)
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                codes[dimension] = (int)response.StatusCode;
                if ((int)response.StatusCode >= 500)
                    failures.Add($"{key} [{dimension}] {url}: {(int)response.StatusCode}");
                foreach (var leak in Leaks(w, text, dimension, IdRoutes.GetValueOrDefault(key)))
                {
                    failures.Add($"LEAK {key} [{dimension}] {url}: {(int)response.StatusCode} names {leak}");
                    notes.Append(CultureInfo.InvariantCulture, $" leak[{dimension}]={leak}");
                }
            }
            observed[key] = (codes[Dimension.Control], codes[Dimension.Ungranted], codes[Dimension.Private], notes.ToString());
            if (IdRoutes.TryGetValue(key, out var expect)
                && (expect.Control, expect.Ungranted, expect.Private) != (codes[Dimension.Control], codes[Dimension.Ungranted], codes[Dimension.Private]))
            {
                failures.Add($"{key}: answered {codes[Dimension.Control]} / {codes[Dimension.Ungranted]} / {codes[Dimension.Private]}, "
                    + $"documented {expect.Control} / {expect.Ungranted} / {expect.Private} (control / ungranted / Private under Incognito)");
            }
        }

        // No row of anyone in U changed (a refused write never lands; a read never marks anything).
        var after = await SnapshotAsync(w, w.U);
        if (before != after)
            failures.Add("rows in the ungranted library changed:\nbefore " + before + "\nafter  " + after);

        Assert.True(observed.Count == idRoutes.Count, $"{observed.Count} of {idRoutes.Count} id routes swept");
        Assert.True(failures.Count == 0, $"{failures.Count} failures over {observed.Count} id routes:\n" + string.Join("\n", failures)
            + "\n\nobserved (control / ungranted / Private under Incognito):\n"
            + string.Join("\n", observed.Select(o => $"[\"{o.Key}\"] = Answer({o.Value.Control}, {o.Value.Ungranted}, {o.Value.Private}),{o.Value.Notes}")));
    }

    [Fact]
    public async Task Sweep_EveryOtherGet_ShowsNoUngrantedContent_AndNoPrivateContentUnderIncognito()
    {
        var w = await WorldAsync();
        await using var host = w.H;
        var (_, listings, _) = Classify(AccessSweepSupport.Endpoints(w.H.Factory.Services));
        var urls = listings.Select(k => "/" + k["GET ".Length..].Replace("{slug}", TrackerSlugs.MangaUpdates, StringComparison.Ordinal)).ToList();
        // The search without a library (its libraryId variant is an id route) and the tracker list's filters.
        urls.Add("/api/v1/search?q=Saga");
        urls.Add("/api/v1/search?q=" + HiddenWord);
        urls.Add("/api/v1/search?q=" + PrivateWord);

        var failures = new List<string>();
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var url in urls)
        {
            foreach (var incognito in new[] { false, true })
            {
                using var response = await (incognito ? w.Reader.Incognito : w.Reader.Client).GetAsync(url);
                var text = await response.Content.ReadAsStringAsync();
                if ((int)response.StatusCode >= 500)
                    failures.Add($"GET {url} (Incognito {incognito}): {(int)response.StatusCode}");
                foreach (var leak in Leaks(w, text, incognito ? Dimension.Private : Dimension.Control, Answer(0, 0, 0, hidden: true)))
                    failures.Add($"LEAK GET {url} (Incognito {incognito}): {(int)response.StatusCode} names {leak}");
                bodies[(incognito ? "I " : "N ") + url] = text;
            }
        }

        // The canaries are live: the reader's own lists DO show G and P (without Incognito) - the same check would see U.
        Assert.Contains(GrantedWord, bodies["N /api/v1/libraries"], StringComparison.Ordinal);
        Assert.Contains(PrivateWord, bodies["N /api/v1/libraries"], StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateWord, bodies["I /api/v1/libraries"], StringComparison.Ordinal);
        Assert.Contains(GrantedWord, bodies["N /api/v1/favorites"], StringComparison.Ordinal);
        Assert.Contains(PrivateWord, bodies["N /api/v1/favorites"], StringComparison.Ordinal);
        Assert.Contains(GrantedWord, bodies["N /api/v1/reading/continue"], StringComparison.Ordinal);
        Assert.Contains(PrivateWord, bodies["N /api/v1/search?q=Saga"], StringComparison.Ordinal);
        var adminLibraries = await w.Admin.GetStringAsync("/api/v1/libraries");
        Assert.Contains(HiddenWord, adminLibraries, StringComparison.Ordinal);
        Assert.True(urls.Count >= 20, $"only {urls.Count} list routes swept");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public async Task BodyIds_NeverReachAnUngrantedLibrary()
    {
        var w = await WorldAsync();
        await using var host = w.H;
        var reader = w.Reader.Client;
        var before = await SnapshotAsync(w, w.U);

        // Home libraries: excluding U is a no-op for the reader's lists (nothing of U shows either way); never a 5xx.
        using (var home = await reader.PutAsync("/api/v1/reading/home-libraries", Json($"{{\"excludedLibraryIds\":[\"{w.U.PublicId}\"]}}")))
            Assert.True((int)home.StatusCode < 500, $"home-libraries {(int)home.StatusCode}");
        Assert.DoesNotContain(w.U.PublicId, await reader.GetStringAsync("/api/v1/reading/home-libraries"), StringComparison.Ordinal);

        // Private libraries: U cannot be marked Private by a reader without access; P stays the reader's.
        using (var priv = await reader.PutAsync("/api/v1/reading/private-libraries",
                   Json($"{{\"libraryIds\":[\"{w.U.PublicId}\",\"{w.P.PublicId}\"]}}")))
            Assert.True((int)priv.StatusCode < 500, $"private-libraries {(int)priv.StatusCode}");
        var privateNow = await reader.GetStringAsync("/api/v1/reading/private-libraries");
        Assert.DoesNotContain(w.U.PublicId, privateNow, StringComparison.Ordinal);

        // Tracker sync switches: a U folder never gets a switch, and the list never names it.
        using (var overrides = await reader.PutAsync("/api/v1/trackers/sync-overrides",
                   Json($"{{\"nodeIds\":[\"{w.U.SeriesPublicId}\"],\"enabled\":true}}")))
            Assert.True((int)overrides.StatusCode < 500, $"sync-overrides {(int)overrides.StatusCode}");
        var overridesNow = await reader.GetStringAsync("/api/v1/trackers/sync-overrides");
        Assert.DoesNotContain(HiddenWord, overridesNow, StringComparison.Ordinal);
        Assert.DoesNotContain(w.U.SeriesPublicId, overridesNow, StringComparison.Ordinal);

        // Tracker batch: a U series is not acted on (the answer names nothing of U).
        foreach (var action in new[] { "FromTracker", "ToTracker" })
        {
            using var batch = await reader.PostAsync($"/api/v1/trackers/{TrackerSlugs.MangaUpdates}/entries/batch",
                Json($"{{\"action\":\"{action}\",\"seriesIds\":[\"{w.U.RecordPublicId}\"]}}"));
            var text = await batch.Content.ReadAsStringAsync();
            Assert.True((int)batch.StatusCode < 500, $"batch {action} {(int)batch.StatusCode}");
            Assert.DoesNotContain(HiddenWord, text, StringComparison.Ordinal);
        }
        await w.H.IdleAsync();

        Assert.Equal(before, await SnapshotAsync(w, w.U));
        // The tracker-override rows are the reader's own: none points into U.
        Assert.Equal(0, await w.H.DbAsync(db => db.TrackerNodeOverrides.CountAsync(o => o.UserId == w.Reader.UserId
            && db.CatalogNodes.Any(n => n.Id == o.NodeId && n.LibraryId == w.U.Id))));
    }

    // ---------------------------------------------------------------- classification and requests

    private static (HashSet<string> IdRoutes, HashSet<string> Listings, HashSet<string> Others) Classify(IReadOnlyList<RouteEndpoint> endpoints)
    {
        var idRoutes = new HashSet<string>(StringComparer.Ordinal);
        var listings = new HashSet<string>(StringComparer.Ordinal);
        var others = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in endpoints)
        {
            var raw = AccessSweepSupport.RawRoute(endpoint);
            if (!raw.StartsWith("api/", StringComparison.Ordinal) || AccessSweepSupport.Under(endpoint, AdminPrefixes)
                || AccessSweepSupport.Under(endpoint, ExportApi.RoutePrefix) || AccessSweepSupport.PoliciesOf(endpoint).Contains("Admin"))
                continue;
            var takesId = AccessSweepSupport.PathParameters(endpoint).Any(p => p != "slug")
                || AccessSweepSupport.QueryParameters(endpoint).Any(AccessSweepSupport.IsIdQueryParameter);
            foreach (var method in AccessSweepSupport.MethodsOf(endpoint))
            {
                var key = AccessSweepSupport.Key(method, endpoint);
                if (takesId)
                    idRoutes.Add(key);
                else if (method == "GET")
                    listings.Add(key);
                else
                    others.Add(key);
            }
        }
        return (idRoutes, listings, others);
    }

    private static bool TakesLibraryAndNode(RouteEndpoint endpoint)
    {
        var names = AccessSweepSupport.PathParameters(endpoint).Concat(AccessSweepSupport.QueryParameters(endpoint)).ToList();
        return names.Contains("libraryId") && names.Any(n => n is "parentId" or "ids" or "nodeId" or "itemId");
    }

    /// <summary>
    /// The value of a route / query parameter for <paramref name="dimension"/>'s library. Mixed: the library of the control (G), every node of
    /// U (a smuggled id must not open U through a granted library). An unmapped name is a failure.
    /// </summary>
    private static string Fill(World w, string route, string name, Dimension dimension, List<string> failures)
    {
        var lib = name == "libraryId" && dimension == Dimension.Mixed ? w.G : w.Of(dimension == Dimension.Mixed ? Dimension.Ungranted : dimension);
        var stacks = route.Contains("/collection-stacks/", StringComparison.Ordinal);
        switch (name)
        {
            case "libraryId": return lib.PublicId;
            case "nodeId": return stacks ? lib.ArtistPublicId : lib.SeriesPublicId;
            case "parentId": return lib.SeriesPublicId;
            case "itemId": return lib.Chapters[0].PublicId;
            case "ids": return lib.Chapters[0].PublicId;
            case "entryKey": return OpaqueId.Encode(0);
            case "key": return stacks ? lib.TankPublicId : "1";
            case "bucket": return "creator";
            case "slug": return TrackerSlugs.MangaUpdates;
            case "seriesId": return lib.RecordPublicId;
            // The reader's own bookmark in G (control) and P (their own), another user's (the admin's) in U.
            case "bookmarkId": return OpaqueId.Encode(dimension == Dimension.Ungranted ? lib.AdminBookmarkId : lib.ReaderBookmarkId);
            default:
                failures.Add($"unmapped parameter '{name}' in {route} - map it in {nameof(Fill)}");
                return "1";
        }
    }

    private static (string Url, string? Body) Build(World w, RouteEndpoint endpoint, string method, Dimension dimension, List<string> failures)
    {
        var raw = AccessSweepSupport.RawRoute(endpoint);
        var path = "/" + AccessSweepSupport.Concrete(endpoint.RoutePattern, name => Fill(w, raw, name, dimension, failures));
        var query = AccessSweepSupport.QueryParameters(endpoint).Where(AccessSweepSupport.IsIdQueryParameter)
            .Select(name => name + "=" + Uri.EscapeDataString(Fill(w, raw, name, dimension, failures))).ToList();
        if (raw == "api/v1/search")
            query.Add("q=Saga");
        var url = query.Count == 0 ? path : path + "?" + string.Join("&", query);
        var body = (method + " " + raw) switch
        {
            "PUT api/v1/reading/progress/{itemId}" =>
                $"{{\"pageIndex\":1,\"expectedContentVersion\":1,\"mutationId\":\"sweep-{dimension}-{Guid.NewGuid():N}\",\"normalizedAnchor\":0}}",
            "PUT api/v1/items/{itemId}/spread-layout" => "{\"expectedContentVersion\":1,\"spreadStarts\":[]}",
            "POST api/v1/reading/{itemId}/bookmarks" => "{\"ordinal\":1,\"label\":\"sweep\"}",
            "POST api/v1/nodes/{nodeId}/series-info/flags" => "{\"reason\":\"WrongDetails\",\"note\":\"sweep\"}",
            "POST api/v1/trackers/{slug}/entries/{seriesId}/set" => "{\"chapters\":1}",
            "PUT api/v1/trackers/{slug}/entries/{seriesId}/dont-sync" => "{\"dontSync\":true}",
            _ => null,
        };
        return (url, body);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>What an answer must not name: U's canary and unrequested ids always; P's under Incognito where P is hidden.</summary>
    private static IEnumerable<string> Leaks(World w, string text, Dimension dimension, Expect? expect)
    {
        foreach (var id in w.U.UnrequestedIds.Prepend(HiddenWord))
        {
            if (text.Contains(id, StringComparison.OrdinalIgnoreCase))
                yield return id == HiddenWord ? "the ungranted library's content" : "an ungranted node id";
        }
        if (dimension == Dimension.Private && (expect?.HiddenUnderIncognito ?? true)
            && text.Contains(PrivateWord, StringComparison.OrdinalIgnoreCase))
            yield return "the Private library's content under Incognito";
    }

    /// <summary>Every row of anyone's that points into <paramref name="lib"/> (reading state, favourites, flags, tracker state), as text.</summary>
    private static Task<string> SnapshotAsync(World w, Lib lib) => w.H.DbAsync(async db =>
    {
        var nodes = await db.CatalogNodes.Where(n => n.LibraryId == lib.Id).Select(n => n.Id).ToListAsync();
        var parts = new List<object>
        {
            await db.ReadingProgress.AsNoTracking().Where(p => nodes.Contains(p.ItemId)).OrderBy(p => p.Id)
                .Select(p => new { p.Id, p.UserId, p.ItemId, p.Ordinal, p.State, p.Revision, p.HiddenFromContinue }).ToListAsync(),
            await db.ReadMarks.AsNoTracking().Where(m => nodes.Contains(m.ItemId)).OrderBy(m => m.Id).Select(m => new { m.Id, m.UserId, m.ItemId }).ToListAsync(),
            await db.Bookmarks.AsNoTracking().Where(b => nodes.Contains(b.ItemId)).OrderBy(b => b.Id).Select(b => new { b.Id, b.UserId, b.ItemId, b.Label }).ToListAsync(),
            await db.Favorites.AsNoTracking().Where(f => nodes.Contains(f.CatalogNodeId)).OrderBy(f => f.Id).Select(f => new { f.Id, f.UserId, f.CatalogNodeId }).ToListAsync(),
            await db.MetadataFlags.AsNoTracking().Where(f => f.LibraryId == lib.Id).OrderBy(f => f.Id).Select(f => new { f.Id, f.NodeId }).ToListAsync(),
            await db.ArchiveSpreadLayouts.AsNoTracking().Where(s => nodes.Contains(s.NodeId)).OrderBy(s => s.NodeId).Select(s => new { s.NodeId, s.ContentVersion }).ToListAsync(),
            await db.ItemReaderOverrides.AsNoTracking().Where(o => nodes.Contains(o.ItemId)).OrderBy(o => o.Id).Select(o => new { o.Id, o.UserId, o.ItemId }).ToListAsync(),
            await db.TrackerNodeOverrides.AsNoTracking().Where(o => nodes.Contains(o.NodeId)).OrderBy(o => o.Id).Select(o => new { o.Id, o.UserId, o.NodeId }).ToListAsync(),
            await db.TrackerEntries.AsNoTracking().Where(e => e.RecordId == lib.RecordId).OrderBy(e => e.Id)
                .Select(e => new { e.Id, e.UserId, e.State, e.LastSentChapters, e.LastSentVolumes, e.RemoteChapters }).ToListAsync(),
        };
        return JsonSerializer.Serialize(parts);
    });

    // ---------------------------------------------------------------- the seeded world

    /// <summary>
    /// The host (tracker harness: fake trackers, no network), the admin with MangaUpdates allowed, three libraries and the reader (granted G
    /// and P, P Private, MangaUpdates connected). The reader's and the admin's own rows in each library: a favourite, progress, a read mark, a
    /// bookmark, a tracker entry.
    /// </summary>
    private static async Task<World> WorldAsync()
    {
        var h = new TrackerTestHarness();
        var admin = await h.AdminAsync();
        await h.AllowAsync(TrackerSlugs.MangaUpdates);
        var g = await SeedLibraryAsync(h, "G", GrantedWord);
        var u = await SeedLibraryAsync(h, "U", HiddenWord);
        var p = await SeedLibraryAsync(h, "P", PrivateWord);
        await SeedFacetsAsync(h, g, u, p);
        var reader = await AccessTestUsers.ReaderAsync(h.Factory, admin, "sweepreader", [g.Id, p.Id], [p.Id]);
        await h.ConnectMangaUpdatesAsync(reader.Client);
        var adminId = await h.UserIdAsync("admin");
        foreach (var lib in new[] { g, u, p })
        {
            lib.ReaderBookmarkId = await SeedOwnRowsAsync(h, lib, reader.UserId);
            lib.AdminBookmarkId = await SeedOwnRowsAsync(h, lib, adminId);
        }
        return new World(h, admin, reader, g, u, p);
    }

    private static Task<Lib> SeedLibraryAsync(TrackerTestHarness h, string key, string word) => h.DbAsync(async db =>
    {
        var library = new LibraryEntity { PublicId = $"as{key}lib", DisplayName = $"{word} Library", RootPath = $"/synthetic/as-{key}", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        var series = Node(library.Id, null, CatalogNodeKind.Folder, $"as{key}ser", $"{word} Saga");
        var artist = Node(library.Id, null, CatalogNodeKind.Folder, $"as{key}art", $"{word} Artist");
        db.CatalogNodes.AddRange(series, artist);
        await db.SaveChangesAsync();
        var chapters = new List<CatalogNodeEntity>();
        for (var c = 1; c <= 4; c++)
            chapters.Add(Node(library.Id, series.Id, CatalogNodeKind.Archive, $"as{key}c{c}", $"{word} Saga - Chapter {c:000}"));
        var stories = new List<CatalogNodeEntity>
        {
            Node(library.Id, artist.Id, CatalogNodeKind.Archive, $"as{key}s1", $"{word} Artist - First Story"),
            Node(library.Id, artist.Id, CatalogNodeKind.Archive, $"as{key}s2", $"{word} Artist - Second Story"),
        };
        db.CatalogNodes.AddRange(chapters.Concat(stories));
        await db.SaveChangesAsync();
        foreach (var archive in chapters.Concat(stories))
            db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = archive.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 12, ByteLength = 1024 });

        var record = new MetadataRecordEntity
        {
            PublicId = $"as{key}rec",
            Provider = "mangaupdates",
            ExternalId = (91000 + key[0]).ToString(CultureInfo.InvariantCulture),
            Title = $"{word} Saga",
            OriginStatus = (int)MetadataOriginStatus.Ongoing,
            FetchedAt = DateTimeOffset.UtcNow,
        };
        var tank = new MetadataRecordEntity
        {
            PublicId = $"as{key}tank",
            Provider = "mangaupdates",
            ExternalId = (92000 + key[0]).ToString(CultureInfo.InvariantCulture),
            Title = $"{word} Collected Volume",
            OriginVolumes = 1,
            OriginStatus = (int)MetadataOriginStatus.Complete,
            FetchedAt = DateTimeOffset.UtcNow,
        };
        db.MetadataRecords.AddRange(record, tank);
        await db.SaveChangesAsync();
        db.NodeSeriesLinks.Add(Link(series, record.Id, SeriesLinkState.Confirmed));
        foreach (var story in stories)
            db.NodeSeriesLinks.Add(Link(story, tank.Id, SeriesLinkState.Auto));
        db.SeriesVolumeMaps.Add(new SeriesVolumeMapEntity
        {
            RecordId = record.Id,
            Source = (int)VolumeMapSource.MangaDexAggregate,
            State = (int)VolumeMapState.Ok,
            VolumesJson = "[{\"v\":\"1\",\"c\":[\"1\",\"2\"]},{\"v\":\"2\",\"c\":[\"3\",\"4\"]}]",
            ContentHash = "h1",
            Version = 1,
            FetchedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return new Lib(key, word, library.Id, library.PublicId, series.Id, series.PublicId, artist.Id, artist.PublicId,
            chapters.Select(c => (c.Id, c.PublicId)).ToList(), stories.Select(s => (s.Id, s.PublicId)).ToList(), record.Id, record.PublicId, tank.PublicId);
    });

    /// <summary>A work per library with a creator and a genre named by the library's word (the Info panel's and the tag view's chips).</summary>
    private static async Task SeedFacetsAsync(TrackerTestHarness h, params Lib[] libs)
    {
        var s = new FacetQueryTestSupport(h.Factory);
        foreach (var lib in libs)
        {
            var work = await s.WorkAsync($"as-work-{lib.Key}", $"{lib.Word} Saga");
            var creator = await s.CreatorAsync($"{lib.Word} Author", []);
            var creatorValue = await s.ValueAsync(FacetBucket.Creators, "c:" + creator, $"{lib.Word} Author", creator);
            var genre = await s.ValueAsync(FacetBucket.Genres, $"as-genre-{lib.Key}", $"{lib.Word} Genre");
            await s.EntryAsync(work, lib.SeriesId, lib.Id, creatorValue.Id, roles: FacetRoles.Writer);
            await s.EntryAsync(work, lib.SeriesId, lib.Id, genre.Id);
        }
        s.Bump();
    }

    /// <summary>A user's own rows in a library: the series starred, progress on chapter 1, chapter 2 read, a bookmark on chapter 3, a tracker entry.</summary>
    private static Task<long> SeedOwnRowsAsync(TrackerTestHarness h, Lib lib, long userId) => h.DbAsync(async db =>
    {
        db.Favorites.Add(new FavoriteEntity { UserId = userId, CatalogNodeId = lib.SeriesId, CreatedAt = DateTimeOffset.UtcNow });
        db.ReadingProgress.Add(new ReadingProgressEntity
        {
            UserId = userId,
            ItemId = lib.Chapters[0].Id,
            ContentVersion = 1,
            EntryKey = OpaqueId.Encode(3),
            Ordinal = 3,
            State = (int)ReadingState.InProgress,
            Revision = 1,
            LastMutationId = $"seed-{lib.Key}-{userId}",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = lib.Chapters[1].Id, MarkedAt = DateTimeOffset.UtcNow, Source = "manual" });
        var bookmark = new BookmarkEntity
        {
            UserId = userId,
            ItemId = lib.Chapters[2].Id,
            ContentVersion = 1,
            EntryKey = OpaqueId.Encode(2),
            Ordinal = 2,
            Label = $"{lib.Word} note",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Bookmarks.Add(bookmark);
        db.TrackerEntries.Add(new TrackerEntryEntity
        {
            UserId = userId,
            Tracker = TrackerSlugs.MangaUpdates,
            RecordId = lib.RecordId,
            State = (int)TrackerEntryState.Ok,
            RemoteId = (91000 + lib.Key[0]).ToString(CultureInfo.InvariantCulture),
            RemoteChapters = 2,
            RemoteSeenAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return bookmark.Id;
    });

    private static CatalogNodeEntity Node(long libraryId, long? parentId, CatalogNodeKind kind, string publicId, string name) => new()
    {
        PublicId = publicId,
        LibraryId = libraryId,
        ParentId = parentId,
        Kind = (int)kind,
        DisplayName = name,
        RelativePath = publicId,
        PathKey = publicId,
        SortKey = SortKey.ForNode(kind, name),
        Availability = (int)CatalogNodeAvailability.Available,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static NodeSeriesLinkEntity Link(CatalogNodeEntity node, long recordId, SeriesLinkState state) => new()
    {
        NodeId = node.Id,
        LibraryId = node.LibraryId,
        State = (int)state,
        RecordId = recordId,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
