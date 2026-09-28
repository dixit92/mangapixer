namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using System.Net;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

// Test support for the stage-2 auto-match plumbing (lane B). The matcher core
// (IWorkDetector / IMatchQueryPlanner / IMatchScorer) is lane A's; these FAKES
// implement the contract with small, readable rules so the plumbing is tested on
// its own. Every provider answer is synthetic JSON (no real title, no network).

/// <summary>
/// Name-driven detector: "Collection*" = collection leaf (each archive its own
/// work), "Mixed*" = review-only, only subfolders = container (or series with
/// units when they are all "Volumes"/"Chapters"), archives only = series (one
/// archive = one-shot). "Doujin" in the name suggests the doujinshi Content.
/// </summary>
public sealed class FakeWorkDetector : IWorkDetector
{
    public int Calls { get; private set; }

    public WorkClassification Classify(FolderShape folder)
    {
        Calls++;
        var suggestion = folder.DisplayName.Contains("Doujin", StringComparison.OrdinalIgnoreCase)
            ? ContentSuggestion.DoujinshiAndAdultOneShots
            : ContentSuggestion.None;
        if (folder.DisplayName.StartsWith("Collection", StringComparison.Ordinal))
        {
            var groups = folder.ArchiveNames.Select((name, i) => new ArchiveGroup(name, [i])).ToList();
            return new WorkClassification(WorkClass.CollectionLeaf, MatchLevel.Archive, ["collection"], groups, suggestion);
        }
        if (folder.DisplayName.StartsWith("Pairs", StringComparison.Ordinal))
        {
            // Two-archive numbered mini-series: archives 0+1 are one work, 2+3 another.
            var groups = Enumerable.Range(0, (folder.ArchiveNames.Count + 1) / 2)
                .Select(g => new ArchiveGroup(folder.ArchiveNames[g * 2], Enumerable.Range(g * 2, Math.Min(2, folder.ArchiveNames.Count - (g * 2))).ToList()))
                .ToList();
            return new WorkClassification(WorkClass.CollectionLeaf, MatchLevel.Archive, ["pairs"], groups, suggestion);
        }
        if (folder.DisplayName.StartsWith("Mixed", StringComparison.Ordinal))
            return new WorkClassification(WorkClass.Mixed, MatchLevel.ReviewOnly, ["mixed"], [], suggestion);
        if (folder.Subfolders.Count > 0 && folder.ArchiveNames.Count == 0)
        {
            var units = folder.Subfolders.All(s => s.DisplayName is "Volumes" or "Chapters");
            return units
                ? new WorkClassification(WorkClass.SeriesWithUnits, MatchLevel.Folder, ["units"], [], suggestion)
                : new WorkClassification(WorkClass.CollectionContainer, MatchLevel.None, ["container"], [], suggestion);
        }
        if (folder.Subfolders.Count == 0 && folder.ArchiveNames.Count == 1)
            return new WorkClassification(WorkClass.OneShot, MatchLevel.Folder, ["one-shot"], [], suggestion);
        if (folder.Subfolders.Count == 0 && folder.ArchiveNames.Count > 1)
            return new WorkClassification(WorkClass.Series, MatchLevel.Folder, ["series"], [], suggestion);
        return new WorkClassification(WorkClass.Excluded, MatchLevel.None, ["empty"], [], suggestion);
    }
}

/// <summary>One variant: the folder's (or group's) normalized primary title, plus the ComicInfo series.</summary>
public sealed class FakeQueryPlanner : IMatchQueryPlanner
{
    public MatchQuery PlanFolder(FolderShape folder, WorkClassification classification, string? comicInfoSeries = null)
    {
        var variants = new List<QueryVariant>();
        if (comicInfoSeries is not null)
            variants.Add(new QueryVariant(comicInfoSeries, QueryVariantKind.ComicInfoSeries));
        variants.Add(new QueryVariant(TitleNormalizer.Normalize(folder.DisplayName).Primary, QueryVariantKind.Primary));
        return new MatchQuery(variants, Context(classification.Class, folder.ArchiveNames.Count, folder.CategoryHint, comicInfoSeries));
    }

    public MatchQuery PlanArchiveGroup(FolderShape folder, WorkClassification classification, ArchiveGroup group) =>
        new([new QueryVariant(TitleNormalizer.Normalize(group.QueryTitle).Primary, QueryVariantKind.Primary)],
            Context(classification.Class, group.ArchiveIndexes.Count, folder.CategoryHint, null));

    private static MatchContext Context(WorkClass cls, int archives, string? category, string? comicInfo) =>
        new(cls, archives, 0, 0, null, category, false, [], comicInfo);
}

/// <summary>
/// Raw title = best similarity to the title or an alt title. Auto when the top
/// reaches the auto threshold and leads by the margin; review above the floor;
/// ToPersist = within 0.15 of the top, at most 5.
/// </summary>
public sealed class FakeMatchScorer : IMatchScorer
{
    public MatchOutcome Score(MatchQuery query, IReadOnlyList<MatchCandidate> candidates, MatchThresholds thresholds)
    {
        var texts = query.Variants.Select(v => v.Text).ToList();
        var ranked = candidates
            .Select(c => new ScoredCandidate(c, TitleSimilarity.Best(texts, c.AltTitles.Prepend(c.Title)), 0, MatchReason.None))
            .Select(s => s with { AdjustedScore = s.TitleScore })
            .OrderByDescending(s => s.AdjustedScore)
            .ThenBy(s => s.Candidate.ExternalId, StringComparer.Ordinal)
            .ToList();
        if (ranked.Count == 0)
            return new MatchOutcome(MatchBand.Unmatched, [], []);
        var top = ranked[0];
        var margin = ranked.Count > 1 ? top.AdjustedScore - ranked[1].AdjustedScore : 1;
        if (margin < thresholds.Margin)
            ranked[0] = top = top with { Reasons = MatchReason.CloseSecond };
        var band = top.TitleScore >= thresholds.AutoTitle && margin >= thresholds.Margin ? MatchBand.Auto
            : top.TitleScore >= thresholds.ReviewFloor ? MatchBand.NeedsReview
            : MatchBand.Unmatched;
        var persist = ranked.Where(r => top.AdjustedScore - r.AdjustedScore <= 0.15).Take(5).ToList();
        return new MatchOutcome(band, ranked, persist);
    }
}

/// <summary>Synthetic MangaUpdates JSON (the fields the provider reads).</summary>
public static class MuJson
{
    public sealed record Hit(long Id, string Title, string Type = "Manga", int Year = 2001, string? Image = null);

    public static string Search(params Hit[] hits) => JsonSerializer.Serialize(new
    {
        total_hits = hits.Length,
        results = hits.Select(h => new
        {
            record = new
            {
                series_id = h.Id,
                title = h.Title,
                type = h.Type,
                year = h.Year.ToString(System.Globalization.CultureInfo.InvariantCulture),
                image = h.Image is null ? null : new { url = new { original = h.Image, thumb = h.Image } },
            },
        }),
    });

    public static string Get(long id, string title, string[]? alt = null, string type = "Manga", string status = "5 Volumes (Ongoing)",
        string? image = null, long? relatedId = null) => JsonSerializer.Serialize(new
        {
            series_id = id,
            title,
            url = $"https://www.mangaupdates.com/series/s{id}/synthetic",
            associated = (alt ?? []).Select(a => new { title = a }),
            description = "A synthetic description.",
            type,
            year = "2001",
            status,
            latest_chapter = 40,
            authors = new[] { new { name = "Synthetic Author", type = "Author", author_id = 7001L } },
            publications = new[] { new { publication_name = "Synthetic Weekly", publisher_name = "Synthetic House" } },
            related_series = relatedId is { } r ? new object[] { new { relation_type = "Sequel", related_series_id = r } } : Array.Empty<object>(),
            image = image is null ? null : new { url = new { original = image, thumb = image } },
        });
}

/// <summary>
/// A <see cref="GatewayHarness"/> plus the auto-match services, with the fake
/// matcher core and no automatic pacing (tests do not wait 1 s per request).
/// Provider answers come from <see cref="Search"/> / <see cref="Records"/>.
/// </summary>
public sealed class AutoMatchHarness : IDisposable
{
    public AutoMatchHarness(MetadataTestDb db, MetadataRateLimitOptions? rates = null)
    {
        Db = db;
        Net = new GatewayHarness(db, rates ?? new MetadataRateLimitOptions { AutomaticInterval = TimeSpan.Zero });
        Net.Handler.Respond = Route;
    }

    public MetadataTestDb Db { get; }
    public GatewayHarness Net { get; }
    public MetadataAutoMatchState State { get; } = new();
    public FakeWorkDetector Detector { get; } = new();

    /// <summary>Search answers by exact query text; unknown queries return no hits.</summary>
    public Dictionary<string, MuJson.Hit[]> Search { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>GET answers by id; unknown ids are 404.</summary>
    public Dictionary<long, string> Records { get; } = [];

    public ScriptedHandler Handler => Net.Handler;
    public ManualTime Time => Net.Time;

    private HttpResponseMessage Route(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        if (uri.Host == MetadataHttp.MangaUpdatesImageHost)
            return ScriptedHandler.Bytes(MuFixtures.Png);
        if (request.Method == HttpMethod.Post && uri.AbsolutePath == "/v1/series/search")
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var text = JsonDocument.Parse(body).RootElement.GetProperty("search").GetString()!;
            return ScriptedHandler.Json(MuJson.Search(Search.TryGetValue(text, out var hits) ? hits : []));
        }
        if (request.Method == HttpMethod.Get && long.TryParse(uri.Segments[^1], out var id) && Records.TryGetValue(id, out var json))
            return ScriptedHandler.Json(json);
        return ScriptedHandler.Json("{\"reason\":\"not found\"}", HttpStatusCode.NotFound);
    }

    public MetadataAutoMatchService Service() => new(
        Db.Db, Net.Gateway(), Net.Budget(), Net.Backoff(), Net.Settings(), Net.Identify(), State, new AuditService(Db.Db), Time,
        Net.LoggerFactory.CreateLogger<MetadataAutoMatchService>(), [Detector], [new FakeQueryPlanner()], [new FakeMatchScorer()]);

    /// <summary>The service with another detector (e.g. the real <see cref="WorkDetector"/>) and options.</summary>
    public MetadataAutoMatchService Service(IWorkDetector detector, MetadataAutoMatchOptions? options = null) => new(
        Db.Db, Net.Gateway(), Net.Budget(), Net.Backoff(), Net.Settings(), Net.Identify(), State, new AuditService(Db.Db), Time,
        Net.LoggerFactory.CreateLogger<MetadataAutoMatchService>(), [detector], [new FakeQueryPlanner()], [new FakeMatchScorer()], options);

    /// <summary>The service with the PRODUCTION matcher core (detector, planner, scorer) and a declared-facts reader.</summary>
    public MetadataAutoMatchService ServiceWithRealMatcher(com.lifepixer.mangapixer.Server.Features.Metadata.Declared.IDeclaredFactsReader? declared = null) => new(
        Db.Db, Net.Gateway(), Net.Budget(), Net.Backoff(), Net.Settings(), Net.Identify(), State, new AuditService(Db.Db), Time,
        Net.LoggerFactory.CreateLogger<MetadataAutoMatchService>(), [new WorkDetector()], [new MatchQueryPlanner()], [new MatchScorer()],
        declared: declared);

    public MetadataAutoMatchService ServiceWithoutMatcher() => new(
        Db.Db, Net.Gateway(), Net.Budget(), Net.Backoff(), Net.Settings(), Net.Identify(), State, new AuditService(Db.Db), Time,
        Net.LoggerFactory.CreateLogger<MetadataAutoMatchService>(), [], [], []);

    public MetadataCarryOverService CarryOver() =>
        new(Db.Db, new AuditService(Db.Db), Time, [Net.Images], Net.LoggerFactory.CreateLogger<MetadataCarryOverService>());

    public MetadataRefreshService Refresh() => new(Db.Db, Net.Gateway(), Service(), Net.Identify(), Net.Budget(), Net.State, Time,
        Net.LoggerFactory.CreateLogger<MetadataRefreshService>());

    /// <summary>Fetch on (current consent) for the library, and the Automatic matching switch with the current automatic consent.</summary>
    public async Task EnableAutomaticAsync(bool automatic = true, int? autoConsentVersion = null, long? libraryId = null)
    {
        await Net.EnableAsync(libraryId);
        var row = await Db.Db.AppSettings.FirstAsync(s => s.Id == AppSettingsEntity.SingletonId);
        row.MetadataAutoMatchEnabled = automatic;
        row.MetadataAutoConsentVersion = autoConsentVersion ?? MetadataAutoConsent.CurrentVersion;
        row.MetadataAutoConsentAt = Time.GetUtcNow();
        await Db.Db.SaveChangesAsync();
    }

    /// <summary>Leases and processes rows until the queue is empty or the gate refuses (like one worker pass).</summary>
    public async Task<int> DrainAsync(int max = 100, IWorkDetector? detector = null)
    {
        var processed = 0;
        for (var i = 0; i < max; i++)
        {
            Db.Db.ChangeTracker.Clear();
            var service = detector is null ? Service() : Service(detector);
            if (await service.CheckGlobalGateAsync() is not null)
                break;
            var row = await service.LeaseNextAsync("test");
            if (row is null)
                break;
            try
            {
                await service.ProcessAsync(row);
            }
            catch (MetadataGatewayException ex) when (MetadataAutoMatchService.IsRefusal(ex))
            {
                break;
            }
            processed++;
        }
        Db.Db.ChangeTracker.Clear();
        return processed;
    }

    public void Dispose() => Net.Dispose();
}
