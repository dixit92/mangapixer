namespace com.lifepixer.mangapixer.Server.Features.Metadata.Volumes.Wikipedia;

using System.Text.Json;
using System.Text.Json.Serialization;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Wikipedia;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaDex;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The Wikipedia companion of a linked series (1.32.0, design 6.5): where MangaDex's volume list has gaps, the series' English
/// "List of ... chapters" page fills them - and gives each volume's English release date and ISBN. Not an <see cref="Providers.IMetadataProvider"/>:
/// it never identifies or matches a folder, and it is asked only about series already linked to a MangaUpdates record.
/// <list type="number">
/// <item><b>Only where it can add something</b> (<see cref="WikipediaDiscovery.CanAdd"/>): MangaDex has no exact list, leaves chapters
/// unassigned, or stops below the series' volume total. An admin's explicit request (or a page title an admin chose) always asks.</item>
/// <item><b>Discovery</b>: Wikidata's item with the series' MangaUpdates id (P11149) -> its English article; else the linked record's title
/// (list titles only - a guess is never read as an article). An admin may choose the page. The list is on the article, on a
/// <c>List of ... chapters</c> page, or on the range pages a hub page links to.</item>
/// <item><b>Revision first</b>: once the pages are known, every later check is one cheap <c>prop=info</c> call; the wikitext is downloaded
/// only when a revision changed.</item>
/// <item><b>Validation</b> before use (<see cref="WikipediaListValidator"/>): a refused list is never used; the last good list stays.</item>
/// <item><b>Storage</b>: a <c>series_volume_maps</c> row (Source WikipediaList: volumes, chapters, the not-yet-collected chapters) and a
/// <c>wikipedia_lists</c> row (state, pages with revisions, per-volume English date + ISBN). Numbers, dates, ISBNs, page titles and revision
/// ids only. <see cref="VolumeListMerge"/> reads the two stored lists together at view time.</item>
/// </list>
/// Every request goes through the gateway (switches, allowlist, backoff, daily budget, the one Wikimedia limiter at 1 / s); a refusal
/// propagates, a provider failure is recorded (retried after a day) and never thrown. Logs carry ids, counts and codes only.
/// </summary>
public sealed class WikipediaVolumeService
{
    private const string Provider = MetadataProviderAllowlist.Wikipedia;

    /// <summary>Range pages read from a hub page (One Piece has six).</summary>
    public const int MaxSubPages = 12;

    private readonly MangaPixerDbContext _db;
    private readonly MetadataGateway _gateway;
    private readonly IWikipediaApi _api;
    private readonly VolumeMapService _maps;
    private readonly TimeProvider _time;
    private readonly ILogger<WikipediaVolumeService> _logger;

    public WikipediaVolumeService(
        MangaPixerDbContext db, MetadataGateway gateway, IWikipediaApi api, VolumeMapService maps, TimeProvider time,
        ILogger<WikipediaVolumeService> logger)
    {
        _db = db;
        _gateway = gateway;
        _api = api;
        _maps = maps;
        _time = time;
        _logger = logger;
    }

    // --- Stored shapes -----------------------------------------------------------------------------------------------------

    /// <summary>A page the stored list was read from.</summary>
    public sealed record StoredPage(
        [property: JsonPropertyName("t")] string Title,
        [property: JsonPropertyName("r")] long Revision);

    /// <summary>A volume's English release date (partial ISO) and ISBN.</summary>
    public sealed record StoredDetail(
        [property: JsonPropertyName("v")] string Volume,
        [property: JsonPropertyName("d"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Date,
        [property: JsonPropertyName("i"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Isbn);

    public static IReadOnlyList<StoredPage> ReadPages(string? json) => Read<StoredPage>(json);

    public static IReadOnlyList<StoredDetail> ReadDetails(string? json) => Read<StoredDetail>(json);

    private static List<T> Read<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // --- Reading ----------------------------------------------------------------------------------------------------------

    public Task<WikipediaListEntity?> FindAsync(long seriesRecordId, CancellationToken ct = default) =>
        _db.WikipediaLists.FirstOrDefaultAsync(l => l.RecordId == seriesRecordId, ct);

    public static bool IsDue(WikipediaListEntity? row, DateTimeOffset now) => row is null || row.NextCheckAt is not { } next || next <= now;

    // --- The step ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One look at the Wikipedia companion of <paramref name="series"/>. <paramref name="force"/>: do not wait for the next check time (an
    /// admin's Refresh). <paramref name="adminAsked"/>: an explicit "Check Wikipedia" - ask even where MangaDex has no gap and read the
    /// wikitext even when no revision changed. Returns the stored row (null when nothing was asked or stored). Gateway refusals propagate.
    /// </summary>
    public async Task<WikipediaListEntity?> StepAsync(
        MetadataRecordEntity series, long libraryId, MetadataCallContext? call, bool force, bool adminAsked, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var row = await FindAsync(series.Id, ct);
        if (row is { State: (int)WikipediaListState.None })
            return row;

        var mangaDex = await _maps.FindAsync(series.Id, VolumeMapSource.MangaDexAggregate, ct);
        var mangaDexVolumes = mangaDex is { State: (int)VolumeMapState.Ok } ? VolumeMapJson.Read(mangaDex.VolumesJson) : [];
        var unassigned = VolumeMapJson.ReadChapters(mangaDex?.UnassignedJson);
        var hasList = VolumeMapService.HasVolumeList(mangaDex);
        if (!adminAsked && row?.AdminTitle is null && !WikipediaDiscovery.CanAdd(hasList, mangaDexVolumes, unassigned, series.OriginVolumes))
            return row;
        if (!force && !adminAsked && !IsDue(row, now))
            return row;

        var isNew = row is null;
        row ??= new WikipediaListEntity { RecordId = series.Id, State = (int)WikipediaListState.NotFound, Method = (int)WikipediaListMethod.Wikidata };
        var requestsBefore = call?.RequestsSent ?? 0;
        try
        {
            await CheckAsync(series, libraryId, row, new WikipediaReference(mangaDexVolumes, series.OriginVolumes), call, adminAsked, now, ct);
        }
        catch (MetadataGatewayException ex) when (!MetadataAutoMatchService.IsRefusal(ex))
        {
            row.State = (int)WikipediaListState.Failed;
            row.RejectCode = Truncate(ex.Code);
            row.CheckedAt = now;
            row.NextCheckAt = CompanionSchedule.AfterFailure(now);
        }
        catch (MetadataGatewayException)
        {
            if (isNew)
                _db.Entry(row).State = EntityState.Detached;
            throw;
        }

        if (isNew)
            _db.WikipediaLists.Add(row);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(LogEvents.Metadata.CompanionChecked,
            "Wikipedia list of record {RecordId}: {State} ({Code}), {Requests} requests",
            series.Id, (WikipediaListState)row.State, row.RejectCode ?? "ok", (call?.RequestsSent ?? 0) - requestsBefore);
        return row;
    }

    /// <summary>Looks at the pages, downloads what changed, validates and stores. Fills <paramref name="row"/>; saves only the map.</summary>
    private async Task CheckAsync(
        MetadataRecordEntity series, long libraryId, WikipediaListEntity row, WikipediaReference reference, MetadataCallContext? call,
        bool adminAsked, DateTimeOffset now, CancellationToken ct)
    {
        var next = CompanionSchedule.NextCheck(series, now);

        // Known pages: one cheap revision check; the wikitext is downloaded only when a revision changed.
        var stored = ReadPages(row.PagesJson);
        if (!adminAsked && stored.Count > 0 && row.State is (int)WikipediaListState.Found or (int)WikipediaListState.Rejected or (int)WikipediaListState.NotFound)
        {
            var infos = await CallAsync("revisions", libraryId, c => _api.RevisionsAsync(stored.Select(p => p.Title).ToList(), c), call, ct);
            if (stored.All(p => infos.Any(i => !i.Missing && i.RevisionId == p.Revision && string.Equals(i.Title, p.Title, StringComparison.Ordinal))))
            {
                row.CheckedAt = now;
                row.NextCheckAt = next;
                return;
            }
        }

        var (article, method, guessed) = await DiscoverAsync(series, libraryId, row, call, ct);
        row.Method = (int)method;
        if (article is null)
        {
            Settle(row, WikipediaListState.NotFound, "no_page", [], now, next);
            return;
        }

        var read = (await CallAsync("pages", libraryId, c => _api.PagesAsync(WikipediaDiscovery.TitlesToRead(article, includeArticle: !guessed), c), call, ct))
            .Where(p => !p.Missing && p.Wikitext is not null && p.RevisionId is not null)
            .DistinctBy(p => p.Title, StringComparer.Ordinal)
            .Select(p => (Page: p, Parse: WikipediaChapterListParser.Parse(p.Wikitext!)))
            .ToList();
        if (read.Count == 0)
        {
            Settle(row, WikipediaListState.NotFound, "no_page", [], now, next);
            return;
        }

        IReadOnlyList<WikipediaTable> tables;
        List<StoredPage> used;
        var ownList = read.Where(p => p.Parse.Tables.Count > 0).OrderBy(p => WikipediaDiscovery.IsListTitle(p.Page.Title) ? 0 : 1).FirstOrDefault();
        if (ownList.Page is not null)
        {
            tables = ownList.Parse.Tables;
            used = [new StoredPage(ownList.Page.Title, ownList.Page.RevisionId!.Value)];
        }
        else if (read.FirstOrDefault(p => p.Parse.SubPageTitles.Count > 0) is { Page: not null } hub)
        {
            var subs = (await CallAsync("pages", libraryId, c => _api.PagesAsync(hub.Parse.SubPageTitles.Take(MaxSubPages).ToList(), c), call, ct))
                .Where(p => !p.Missing && p.Wikitext is not null && p.RevisionId is not null)
                .DistinctBy(p => p.Title, StringComparer.Ordinal)
                .Select(p => (Page: p, Parse: WikipediaChapterListParser.Parse(p.Wikitext!)))
                .Where(p => p.Parse.Tables.Count > 0)
                .ToList();
            tables = subs.Count == 0 ? [] : [Concatenate(subs.Select(s => s.Parse.Tables.FirstOrDefault(t => t.Volumes.Any(v => v.Chapters.Count > 0)) ?? s.Parse.Tables[0]))];
            used = [new StoredPage(hub.Page.Title, hub.Page.RevisionId!.Value), .. subs.Select(s => new StoredPage(s.Page.Title, s.Page.RevisionId!.Value))];
        }
        else
        {
            // The page(s) exist but hold no list: remembered by revision, so an unchanged article is not read again.
            tables = [];
            used = read.Select(p => new StoredPage(p.Page.Title, p.Page.RevisionId!.Value)).Take(MaxSubPages).ToList();
        }

        if (tables.Count == 0)
        {
            Settle(row, WikipediaListState.NotFound, "no_list", used, now, next);
            return;
        }

        var table = WikipediaListValidator.Pick(tables, reference, out var verdict);
        if (table is null && verdict.Code == WikipediaValidation.NoVolumes)
            table = RowsOnly(tables, reference); // an article with volume rows but no chapter lists: dates and ISBNs only
        if (table is null)
        {
            Settle(row, WikipediaListState.Rejected, verdict.Code, used, now, next);
            return;
        }

        await StoreAsync(series, row, table, used, now, next, ct);
    }

    private async Task<(string? Article, WikipediaListMethod Method, bool Guessed)> DiscoverAsync(
        MetadataRecordEntity series, long libraryId, WikipediaListEntity row, MetadataCallContext? call, CancellationToken ct)
    {
        if (row.AdminTitle is { } chosen)
            return (chosen, WikipediaListMethod.Admin, false);
        if (long.TryParse(series.ExternalId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            var mangaUpdatesId = MangaUpdatesReference.ToBase36(id);
            var items = await CallAsync("find-item", libraryId, c => _api.FindItemsAsync(mangaUpdatesId, c), call, ct);
            if (items.Count > 0 && await CallAsync("sitelink", libraryId, c => _api.EnglishArticleAsync(items, c), call, ct) is { } article)
                return (article, WikipediaListMethod.Wikidata, false);
        }
        var title = MangaDexCrossLink.Query1(series.Title);
        return (WikipediaApi.IsValidTitle(title) ? title : null, WikipediaListMethod.Title, true);
    }

    private Task<T> CallAsync<T>(string operation, long libraryId, Func<CancellationToken, Task<T>> call, MetadataCallContext? context, CancellationToken ct) =>
        _gateway.CompanionCallAsync(Provider, operation, libraryId, call, context, ct);

    /// <summary>One synthetic table from the range pages of a split list: volumes in page order, the first row of a volume number wins.</summary>
    internal static WikipediaTable Concatenate(IEnumerable<WikipediaTable> tables)
    {
        var rows = new List<WikipediaVolumeRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var notYet = new SortedSet<decimal>();
        int implicitChapters = 0, specials = 0;
        foreach (var table in tables)
        {
            foreach (var row in table.Volumes.Where(v => seen.Add(v.Volume)))
                rows.Add(row);
            foreach (var chapter in table.NotInVolume.Select(c => VolumeMapJson.Parse(c)).OfType<decimal>())
                notYet.Add(chapter);
            implicitChapters += table.ImplicitChapters;
            specials += table.Specials;
        }
        return new WikipediaTable(null, rows, notYet.Select(VolumeMapJson.Canonical).ToList(), implicitChapters, specials);
    }

    /// <summary>The first table without chapter lists whose volume rows could be this series' (not above the volume total + 1).</summary>
    private static WikipediaTable? RowsOnly(IReadOnlyList<WikipediaTable> tables, WikipediaReference reference) =>
        tables.FirstOrDefault(t => t.Volumes.Count > 0 && t.Volumes.Any(v => v.EnglishDate is not null || v.EnglishIsbn is not null)
            && (reference.MangaUpdatesVolumes is not { } total || t.Volumes.Count <= total + 1));

    private static void Settle(
        WikipediaListEntity row, WikipediaListState state, string code, IReadOnlyList<StoredPage> pages, DateTimeOffset now, DateTimeOffset next)
    {
        row.State = (int)state;
        row.RejectCode = Truncate(code);
        row.PagesJson = pages.Count == 0 ? null : JsonSerializer.Serialize(pages);
        row.CheckedAt = now;
        row.NextCheckAt = next;
    }

    private async Task StoreAsync(
        MetadataRecordEntity series, WikipediaListEntity row, WikipediaTable table, IReadOnlyList<StoredPage> pages, DateTimeOffset now, DateTimeOffset next,
        CancellationToken ct)
    {
        var volumes = table.Volumes.Where(v => v.Chapters.Count > 0).Select(v => new VolumeMapEntry(v.Volume, v.Chapters)).ToList();
        var notYet = table.NotInVolume.Count > 0 ? VolumeMapJson.WriteChapters(table.NotInVolume) : null;
        await _maps.StoreAsync(series, VolumeMapSource.WikipediaList, volumes.Count > 0 ? VolumeMapState.Ok : VolumeMapState.Empty,
            volumes.Count > 0 ? VolumeMapJson.Write(volumes) : null, notYet, ChaptersPerVolume(volumes), null, ct);

        var details = table.Volumes
            .Where(v => v.EnglishDate is not null || v.EnglishIsbn is not null)
            .Select(v => new StoredDetail(v.Volume, v.EnglishDate, v.EnglishIsbn))
            .ToList();
        row.DetailsJson = details.Count == 0 ? null : JsonSerializer.Serialize(details);
        Settle(row, WikipediaListState.Found, string.Empty, pages, now, next);
        row.RejectCode = null;
    }

    /// <summary>Average whole chapters per volume over the volumes numbered 1 and up (for estimated volumes beyond the list); null with none.</summary>
    internal static double? ChaptersPerVolume(IReadOnlyList<VolumeMapEntry> volumes)
    {
        var counts = volumes
            .Where(v => VolumeMapJson.Parse(v.Volume) is >= 1)
            .Select(v => v.Chapters.Count(c => VolumeMapJson.Parse(c) is { } n && n == decimal.Truncate(n)))
            .Where(n => n > 0)
            .ToList();
        return counts.Count == 0 ? null : Math.Round(counts.Average(), 2);
    }

    private static string Truncate(string code) => code.Length <= 32 ? code : code[..32];

    // --- Admin ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// An admin chose the page (<paramref name="title"/> parsed locally by <see cref="WikipediaApi.TryParseTitle"/>): the previous result is
    /// dropped and the page is read now. Refusals propagate.
    /// </summary>
    public async Task<WikipediaListEntity?> SetPageAsync(
        MetadataRecordEntity series, long libraryId, string title, MetadataCallContext? call, CancellationToken ct = default)
    {
        var row = await FindAsync(series.Id, ct);
        if (row is null)
        {
            row = new WikipediaListEntity { RecordId = series.Id };
            _db.WikipediaLists.Add(row);
        }
        row.AdminTitle = title;
        row.Method = (int)WikipediaListMethod.Admin;
        row.State = (int)WikipediaListState.NotFound;
        row.PagesJson = null;
        row.RejectCode = null;
        row.NextCheckAt = null;
        await _db.SaveChangesAsync(ct);
        return await StepAsync(series, libraryId, call, force: true, adminAsked: true, ct);
    }

    /// <summary>"No Wikipedia list for this series": never asked again; the stored list and its details are removed. Local only.</summary>
    public async Task ClearAsync(MetadataRecordEntity series, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var row = await FindAsync(series.Id, ct);
        if (row is null)
        {
            row = new WikipediaListEntity { RecordId = series.Id };
            _db.WikipediaLists.Add(row);
        }
        row.State = (int)WikipediaListState.None;
        row.Method = (int)WikipediaListMethod.Admin;
        row.AdminTitle = null;
        row.PagesJson = null;
        row.DetailsJson = null;
        row.RejectCode = null;
        row.CheckedAt = now;
        row.NextCheckAt = null;
        await _db.SaveChangesAsync(ct);
        await _db.SeriesVolumeMaps.Where(m => m.RecordId == series.Id && m.Source == (int)VolumeMapSource.WikipediaList).ExecuteDeleteAsync(ct);
    }

    /// <summary>"Check Wikipedia again": "none" is cleared, then the page is asked for now (an admin request).</summary>
    public async Task<WikipediaListEntity?> RecheckAsync(MetadataRecordEntity series, long libraryId, MetadataCallContext? call, CancellationToken ct = default)
    {
        var row = await FindAsync(series.Id, ct);
        if (row is { State: (int)WikipediaListState.None })
        {
            row.State = (int)WikipediaListState.NotFound;
            row.NextCheckAt = null;
            await _db.SaveChangesAsync(ct);
        }
        return await StepAsync(series, libraryId, call, force: true, adminAsked: true, ct);
    }
}
