namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
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
    /// <remarks>
    /// Read as: control (G) / ungranted (U) / the reader's Private library under Incognito (P). "hidden" = a listing or discovery surface:
    /// under Incognito P answers like an empty library and names nothing of P. Every other P answer is a direct URL to the reader's own
    /// library, reachable under Incognito by design (<c>LibraryAuthorizationService.GetVisibleLibraryIdsAsync</c>). Recorded 2026-10-25 (lane A).
    /// Ungranted answers: 404 (as unknown) for nearly all; 200 with nothing of U where the route lists (browse, jump rail, search, Continue by
    /// library) or reports the caller's own state as "none" (read mark false, no bookmarks); 401 where the reading-state writes answer an
    /// inaccessible item with "unauthorized" (open question in the lane note: a signed-in reader gets 401, not 404); 204 for removing one's
    /// own favourite (idempotent, the caller's own row).
    /// </remarks>
    private static readonly Dictionary<string, Expect> IdRoutes = new(StringComparer.Ordinal)
    {
        // ---- library listings (hidden under Incognito, as browse)
        ["GET api/v1/libraries/{libraryId}/browse"] = Answer(200, 200, 200, hidden: true),
        ["GET api/v1/libraries/{libraryId}/jump-index"] = Answer(200, 200, 200, hidden: true),
        ["GET api/v1/reading/continue/by-library/{libraryId}"] = Answer(200, 200, 200, hidden: true),
        ["GET api/v1/search"] = Answer(200, 200, 200, hidden: true),
        ["GET api/v1/trackers/card-marks"] = Answer(200, 404, 200, hidden: true),
        // ---- the library itself and its root Info panel (direct; the panel under Incognito is an open question in the lane note)
        ["GET api/v1/libraries/{libraryId}"] = Answer(200, 404, 200),
        ["GET api/v1/libraries/{libraryId}/info"] = Answer(200, 404, 200),
        ["GET api/v1/libraries/{libraryId}/info/buckets/{bucket}"] = Answer(200, 404, 200),
        // ---- nodes (direct)
        ["GET api/v1/nodes/{nodeId}"] = Answer(200, 404, 200),
        ["GET api/v1/nodes/{nodeId}/breadcrumbs"] = Answer(200, 404, 200),
        ["GET api/v1/nodes/{nodeId}/neighbors"] = Answer(200, 404, 200),
        ["GET api/v1/nodes/{nodeId}/cover"] = Answer(202, 404, 202),
        ["GET api/v1/nodes/{nodeId}/declared-facts"] = Answer(200, 404, 200),
        ["GET api/v1/nodes/{nodeId}/info"] = Answer(200, 404, 200),
        ["GET api/v1/nodes/{nodeId}/info/buckets/{bucket}"] = Answer(200, 404, 200),
        ["GET api/v1/nodes/{nodeId}/missing"] = Answer(200, 404, 200),
        ["GET api/v1/nodes/{nodeId}/series-info"] = Answer(200, 404, 200),
        ["GET api/v1/nodes/{nodeId}/series-info/image"] = Answer(404, 404, 404),
        ["GET api/v1/nodes/{nodeId}/series-info/flags/mine"] = Answer(200, 404, 200),
        ["POST api/v1/nodes/{nodeId}/series-info/flags"] = Answer(201, 404, 201),
        ["GET api/v1/nodes/{nodeId}/volume-view"] = Answer(200, 404, 200),
        ["GET api/v1/nodes/{nodeId}/volumes/{key}"] = Answer(200, 404, 200),
        ["GET api/v1/nodes/{nodeId}/volumes/{key}/cover"] = Answer(202, 404, 202),
        ["GET api/v1/nodes/{nodeId}/collection-stacks/{key}"] = Answer(200, 404, 200),
        ["GET api/v1/nodes/{nodeId}/collection-stacks/{key}/cover"] = Answer(202, 404, 202),
        ["POST api/v1/nodes/{nodeId}/favorite"] = Answer(204, 404, 204),
        ["DELETE api/v1/nodes/{nodeId}/favorite"] = Answer(204, 204, 204),
        // ---- items and the reader (direct)
        ["GET api/v1/items/{itemId}/cover"] = Answer(202, 404, 202),
        ["GET api/v1/items/{itemId}/manifest"] = Answer(200, 404, 200),
        ["GET api/v1/items/{itemId}/preparation"] = Answer(200, 404, 200),
        ["GET api/v1/items/{itemId}/readiness"] = Answer(200, 404, 200),
        ["POST api/v1/items/{itemId}/prepare"] = Answer(200, 404, 200),
        ["GET api/v1/items/{itemId}/pages/{entryKey}"] = Answer(404, 404, 404),
        ["GET api/v1/items/{itemId}/pages/{entryKey}/thumbnail"] = Answer(404, 404, 404),
        ["PUT api/v1/items/{itemId}/spread-layout"] = Answer(200, 404, 200),
        ["GET api/v1/reading/{itemId}/effective-mode"] = Answer(200, 404, 200), // 1.48.0: was 200 for U (the LEAK, 32213ef0)
        ["GET api/v1/reading/progress/{itemId}"] = Answer(200, 404, 200),
        ["PUT api/v1/reading/progress/{itemId}"] = Answer(200, 401, 200),
        ["DELETE api/v1/reading/progress/{itemId}"] = Answer(204, 401, 204),
        ["GET api/v1/reading/{itemId}/read"] = Answer(200, 200, 200),
        ["PUT api/v1/reading/{itemId}/read"] = Answer(200, 401, 200),
        ["DELETE api/v1/reading/{itemId}/read"] = Answer(200, 401, 200),
        ["PUT api/v1/reading/folders/{nodeId}/read"] = Answer(200, 404, 200),
        ["DELETE api/v1/reading/folders/{nodeId}/read"] = Answer(200, 404, 200),
        ["DELETE api/v1/reading/continue/{itemId}"] = Answer(204, 401, 204),
        ["GET api/v1/reading/{itemId}/bookmarks"] = Answer(200, 200, 200),
        ["POST api/v1/reading/{itemId}/bookmarks"] = Answer(200, 401, 200),
        ["DELETE api/v1/reading/bookmarks/{bookmarkId}"] = Answer(204, 404, 204),
        // ---- the caller's tracker actions on a series (direct)
        ["GET api/v1/trackers/nodes/{nodeId}/summary"] = Answer(200, 404, 200),
        ["POST api/v1/trackers/{slug}/entries/{seriesId}/sync"] = Answer(200, 404, 200),
        ["POST api/v1/trackers/{slug}/entries/{seriesId}/send"] = Answer(200, 404, 200),
        ["POST api/v1/trackers/{slug}/entries/{seriesId}/set"] = Answer(200, 404, 200),
        ["POST api/v1/trackers/{slug}/entries/{seriesId}/from-tracker"] = Answer(409, 404, 409),
        ["POST api/v1/trackers/{slug}/entries/{seriesId}/volumes-from-chapters"] = Answer(200, 404, 200),
        ["PUT api/v1/trackers/{slug}/entries/{seriesId}/dont-sync"] = Answer(200, 404, 200),
        ["PUT api/v1/trackers/{slug}/entries/{seriesId}/dates"] = Answer(400, 400, 400),
    };

    /// <summary>
    /// Id routes whose granted control cannot reach past the "not found" in this host - excluded-with-reason from the reach proof (they are
    /// still swept for status, 5xx and leaks). Every other id route must answer the control differently from U, or with a body.
    /// </summary>
    private static readonly Dictionary<string, string> ControlUnproven = new(StringComparer.Ordinal)
    {
        ["GET api/v1/items/{itemId}/pages/{entryKey}"] = "pages need an archive the media worker analysed; HTTP hosts run no worker (process tests cover pages)",
        ["GET api/v1/items/{itemId}/pages/{entryKey}/thumbnail"] = "as pages: no analysed archive without the worker",
        ["GET api/v1/nodes/{nodeId}/series-info/image"] = "no stored series image in the sweep's seed (SeriesInfoImage HTTP tests)",
        ["DELETE api/v1/nodes/{nodeId}/favorite"] = "idempotent: 204 whether or not the star exists; the caller's own U star may go (RowChanges)",
        ["PUT api/v1/trackers/{slug}/entries/{seriesId}/dates"] = "only MyAnimeList keeps dates; the sweep connects MangaUpdates, refused before the series is read (TrackerRowDatesTests)",
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

    /// <summary>Lists that name the caller's Private library itself (its id, never its content) under Incognito, by design.</summary>
    private static readonly Dictionary<string, string> IncognitoMayNameItsLibrary = new(StringComparer.Ordinal)
    {
        ["/api/v1/reading/private-libraries"] = "the caller's own Private set (the Settings page edits it under Incognito too)",
    };

    /// <summary>
    /// Lists of the caller's OWN tracker data that name a series of U (an entry kept from a revoked grant: the record's title and id, no folder)
    /// or of P under Incognito (the Tracking page does not apply Incognito). Recorded as found, by design today - open questions in the lane
    /// note; the leak check still runs on them for everything else.
    /// </summary>
    private static readonly Dictionary<string, (bool Ungranted, bool PrivateUnderIncognito, string Reason)> OwnTrackerLists = new(StringComparer.Ordinal)
    {
        ["/api/v1/trackers/mangaupdates/entries"] = (true, true,
            "the caller's own tracker entries: an entry stays listed (title, series id, no folder) after the folder's library is no longer theirs"),
        ["/api/v1/trackers/mangaupdates/series"] = (false, true, "the Tracking page lists the caller's series of every library they can open, Incognito or not"),
        ["/api/v1/trackers/mangaupdates/series/ids"] = (false, true, "as the Tracking page's list"),
    };

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

        /// <summary>
        /// Every public id of the library (the library, its nodes, its records). An answer that names one the request did not send learnt
        /// it from the library itself (1.48.0 integrator note: the jump rail of a Private library under Incognito named its node ids -
        /// node ids, not names, so the word canary alone missed it).
        /// </summary>
        public IEnumerable<string> AllIds =>
            [PublicId, SeriesPublicId, ArtistPublicId, .. Chapters.Select(c => c.PublicId), .. Stories.Select(c => c.PublicId), RecordPublicId, TankPublicId,
                ShelfPublicId, .. ShelfArchives];

        /// <summary>A plain folder (no link, no Volumes or collection view) with two archives - what the jump rail and the rings read.</summary>
        public string ShelfPublicId => $"as{Key}shf";

        public IReadOnlyList<string> ShelfArchives => [$"as{Key}sh1", $"as{Key}sh2"];
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
        // 1.48.0: 93 classified (56 id routes, 19 lists, 18 writes without ids); the audit's static census counted 102 incl. the export.
        Assert.True(idRoutes.Count + listings.Count + others.Count >= 93, $"only {idRoutes.Count + listings.Count + others.Count} routes classified");
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
                // A listing's control must list something of G (an id it was not sent): an empty control would prove nothing about U / P.
                if (dimension == Dimension.Control && IdRoutes.GetValueOrDefault(key)?.HiddenUnderIncognito == true
                    && !w.G.AllIds.Except([w.G.PublicId, w.G.SeriesPublicId, w.G.ArtistPublicId, w.G.ShelfPublicId]).Any(id => text.Contains(id, StringComparison.OrdinalIgnoreCase)))
                    failures.Add($"{key}: the granted control lists nothing of G - the sweep proves nothing for it");
                if ((int)response.StatusCode >= 500)
                    failures.Add($"{key} [{dimension}] {url}: {(int)response.StatusCode}");
                // A hidden listing may echo the library / folder it was asked about, nothing else of P - not even the card ids it was sent.
                var sent = dimension == Dimension.Private && IdRoutes.GetValueOrDefault(key)?.HiddenUnderIncognito == true
                    ? string.Join(" ", w.P.PublicId, w.P.SeriesPublicId, w.P.ArtistPublicId, w.P.ShelfPublicId)
                    : url + " " + body;
                foreach (var leak in Leaks(w, text, dimension, IdRoutes.GetValueOrDefault(key), sent))
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
        failures.AddRange(RowChanges(before, await SnapshotAsync(w, w.U), w.Reader.UserId));

        foreach (var (route, answer) in IdRoutes.Where(r => r.Value.Control == r.Value.Ungranted && r.Value.Control != 200))
        {
            if (!ControlUnproven.ContainsKey(route))
                failures.Add($"{route}: the granted control answers like U ({answer.Control}) - the parameter map does not reach the code; map it or list it in {nameof(ControlUnproven)}");
        }
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
                var allowed = url;
                if (IncognitoMayNameItsLibrary.ContainsKey(url))
                    allowed += " " + w.P.PublicId;
                if (OwnTrackerLists.TryGetValue(url, out var own))
                {
                    if (own.Ungranted)
                        allowed += " " + HiddenWord + " " + w.U.RecordPublicId;
                    if (own.PrivateUnderIncognito)
                        allowed += " " + PrivateWord + " " + string.Join(" ", w.P.AllIds);
                }
                foreach (var leak in Leaks(w, text, incognito ? Dimension.Private : Dimension.Control, Answer(0, 0, 0, hidden: true), allowed))
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
        // Found 2026-10-25: the U id is stored and listed back (a mark on a library the reader cannot open; it shows and hides nothing - the
        // reader's lists below still name nothing of U). Open question in the lane note; pinned here as the current answer.
        var privateNow = await reader.GetStringAsync("/api/v1/reading/private-libraries");
        Assert.Contains(w.P.PublicId, privateNow, StringComparison.Ordinal);
        foreach (var list in new[] { "/api/v1/libraries", "/api/v1/favorites", "/api/v1/reading/continue", "/api/v1/home/recent-chapters" })
        {
            Assert.DoesNotContain(HiddenWord, await reader.GetStringAsync(list), StringComparison.Ordinal);
            Assert.DoesNotContain(HiddenWord, await w.Reader.Incognito.GetStringAsync(list), StringComparison.Ordinal);
        }

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

        var changes = RowChanges(before, await SnapshotAsync(w, w.U), w.Reader.UserId).ToList();
        Assert.True(changes.Count == 0, string.Join("\n", changes));
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
        var flat = route.EndsWith("/jump-index", StringComparison.Ordinal) || route.EndsWith("/card-marks", StringComparison.Ordinal);
        switch (name)
        {
            case "libraryId": return lib.PublicId;
            case "nodeId": return stacks ? lib.ArtistPublicId : lib.SeriesPublicId;
            // The jump rail and the rings read a plain folder in the name sort (the linked series and the artist's collection open in the
            // Volumes / collection view, where the rail is empty by design - the control would prove nothing).
            case "parentId": return flat ? lib.ShelfPublicId : lib.SeriesPublicId;
            case "itemId": return lib.Chapters[0].PublicId;
            case "ids": return flat ? lib.ShelfArchives[0] : lib.Chapters[0].PublicId;
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

    /// <summary>
    /// What an answer must not name: U's word and any U id the request did not send, always; P's word and any P id the request did not
    /// send, under Incognito where P is documented hidden.
    /// </summary>
    private static IEnumerable<string> Leaks(World w, string text, Dimension dimension, Expect? expect, string request)
    {
        if (text.Contains(HiddenWord, StringComparison.OrdinalIgnoreCase) && !request.Contains(HiddenWord, StringComparison.OrdinalIgnoreCase))
            yield return "the ungranted library's content";
        foreach (var id in w.U.AllIds.Where(id => !request.Contains(id, StringComparison.OrdinalIgnoreCase)))
        {
            if (text.Contains(id, StringComparison.OrdinalIgnoreCase))
                yield return "an ungranted id (" + id + ")";
        }
        if (dimension != Dimension.Private || !(expect?.HiddenUnderIncognito ?? true))
            yield break;
        if (text.Contains(PrivateWord, StringComparison.OrdinalIgnoreCase) && !request.Contains(PrivateWord, StringComparison.OrdinalIgnoreCase))
            yield return "the Private library's content under Incognito";
        foreach (var id in w.P.AllIds.Where(id => !request.Contains(id, StringComparison.OrdinalIgnoreCase)))
        {
            if (text.Contains(id, StringComparison.OrdinalIgnoreCase))
                yield return "a Private library id under Incognito (" + id + ")";
        }
    }

    /// <summary>
    /// Every row that points into <paramref name="lib"/> (reading state, favourites, flags, spread layouts, reader and tracker overrides,
    /// tracker entries), one text line per row, tagged with its table and owner ("u{id}", or "-" for rows of no user).
    /// </summary>
    private static Task<List<string>> SnapshotAsync(World w, Lib lib) => w.H.DbAsync(async db =>
    {
        var nodes = await db.CatalogNodes.Where(n => n.LibraryId == lib.Id).Select(n => n.Id).ToListAsync();
        var rows = new List<string>();
        void Add<T>(string table, IEnumerable<T> items, Func<T, long?> owner)
        {
            foreach (var item in items)
                rows.Add($"{table} {(owner(item) is { } u ? "u" + u.ToString(CultureInfo.InvariantCulture) : "-")} {JsonSerializer.Serialize(item)}");
        }
        Add("progress", await db.ReadingProgress.AsNoTracking().Where(p => nodes.Contains(p.ItemId)).OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.UserId, p.ItemId, p.Ordinal, p.State, p.Revision, p.HiddenFromContinue }).ToListAsync(), r => r.UserId);
        Add("read", await db.ReadMarks.AsNoTracking().Where(m => nodes.Contains(m.ItemId)).OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.UserId, m.ItemId }).ToListAsync(), r => r.UserId);
        Add("bookmark", await db.Bookmarks.AsNoTracking().Where(b => nodes.Contains(b.ItemId)).OrderBy(b => b.Id)
            .Select(b => new { b.Id, b.UserId, b.ItemId, b.Label }).ToListAsync(), r => r.UserId);
        Add("favorite", await db.Favorites.AsNoTracking().Where(f => nodes.Contains(f.CatalogNodeId)).OrderBy(f => f.Id)
            .Select(f => new { f.Id, f.UserId, f.CatalogNodeId }).ToListAsync(), r => r.UserId);
        Add("flag", await db.MetadataFlags.AsNoTracking().Where(f => f.LibraryId == lib.Id).OrderBy(f => f.Id)
            .Select(f => new { f.Id, f.NodeId, f.ReporterUserId }).ToListAsync(), r => r.ReporterUserId);
        Add("spread", await db.ArchiveSpreadLayouts.AsNoTracking().Where(l => nodes.Contains(l.NodeId)).OrderBy(l => l.NodeId)
            .Select(l => new { l.NodeId, l.ContentVersion }).ToListAsync(), _ => null);
        Add("mode", await db.ItemReaderOverrides.AsNoTracking().Where(o => nodes.Contains(o.ItemId)).OrderBy(o => o.Id)
            .Select(o => new { o.Id, o.UserId, o.ItemId }).ToListAsync(), r => r.UserId);
        Add("sync-switch", await db.TrackerNodeOverrides.AsNoTracking().Where(o => nodes.Contains(o.NodeId)).OrderBy(o => o.Id)
            .Select(o => new { o.Id, o.UserId, o.NodeId }).ToListAsync(), r => r.UserId);
        Add("tracker-entry", await db.TrackerEntries.AsNoTracking().Where(e => e.RecordId == lib.RecordId).OrderBy(e => e.Id)
            .Select(e => new { e.Id, e.UserId, e.State, e.LastSentChapters, e.LastSentVolumes, e.RemoteChapters, e.DontSync }).ToListAsync(), r => r.UserId);
        return rows;
    });

    /// <summary>
    /// What may change in U: nothing of anyone else's, and the caller's own rows may only go (removing one's own favourite or bookmark of an
    /// item one can no longer open is idempotent cleanup, not access) - never appear or change.
    /// </summary>
    private static IEnumerable<string> RowChanges(List<string> before, List<string> after, long callerId)
    {
        var caller = " u" + callerId.ToString(CultureInfo.InvariantCulture) + " ";
        foreach (var row in after.Except(before, StringComparer.Ordinal))
            yield return "a row appeared or changed: " + row;
        foreach (var row in before.Except(after, StringComparer.Ordinal).Where(r => !r.Contains(caller, StringComparison.Ordinal)))
            yield return "another user's row went: " + row;
    }

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
        var shelf = Node(library.Id, null, CatalogNodeKind.Folder, $"as{key}shf", $"{word} Shelf");
        db.CatalogNodes.AddRange(series, artist, shelf);
        await db.SaveChangesAsync();
        var shelved = new[]
        {
            Node(library.Id, shelf.Id, CatalogNodeKind.Archive, $"as{key}sh1", $"{word} Shelf - Alder"),
            Node(library.Id, shelf.Id, CatalogNodeKind.Archive, $"as{key}sh2", $"{word} Shelf - Birch"),
        };
        db.CatalogNodes.AddRange(shelved);
        await db.SaveChangesAsync();
        foreach (var archive in shelved)
            db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = archive.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 12, ByteLength = 1024 });
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
        SortKey = com.lifepixer.mangapixer.Core.Ordering.SortKey.ForNode(kind, name),
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
