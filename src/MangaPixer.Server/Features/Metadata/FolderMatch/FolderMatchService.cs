namespace com.lifepixer.mangapixer.Server.Features.Metadata.FolderMatch;

using System.Diagnostics;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Metadata.FolderMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Artists;
using com.lifepixer.mangapixer.Server.Features.Metadata.Authors;
using com.lifepixer.mangapixer.Server.Features.Metadata.Collections;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// "Match folders by name" (1.38.0, owner): several selected FOLDERS are marked at once as artist folders or as collections about a series
/// when their name equals a name already stored on this server - stored data only, nothing is sent (the preview and the apply):
/// <list type="bullet">
/// <item>Artists: every creator spelling (roles author / artist) on every stored MangaUpdates / GCD record, grouped into one artist by the
/// MangaUpdates author id, plus the other names of a stored author record (<see cref="IAuthorAliasSource"/>; empty until author records are
/// fetched). The declared name is the MangaUpdates main name.</item>
/// <item>Collections: the titles and alt titles of the stored MangaUpdates / GCD series records, exact equality in scoring form; records
/// linked as a series (Confirmed / Auto) first.</item>
/// </list>
/// The preview lists every node with its status (proposed / ambiguous / no match / already decided); nothing changes. The apply marks each
/// ticked folder through the single action (<see cref="ArtistFolderService.SetAsync"/> / <see cref="CollectionAboutService.SetAsync"/>, audit
/// result <c>folder_match</c>) - a collection only with a STORED record (never the record fetch, never the poster download). The opt-in web
/// search for the rest is the Identify search (one request per ticked row, driven by the dialog), not this service.
/// </summary>
public sealed class FolderMatchService
{
    /// <summary>The most nodes per preview / apply (like the review bulk).</summary>
    public const int MaxNodes = 200;

    /// <summary>The audit result of a folder marked from this dialog (the single actions audit each folder).</summary>
    public const string AuditResult = "folder_match";

    /// <summary>The providers whose records can be a collection's series and whose creators are artists (Identify's sites).</summary>
    public static readonly IReadOnlyList<string> SeriesProviders = [MetadataProviderAllowlist.MangaUpdates, MetadataProviderAllowlist.Gcd];

    private static readonly int[] s_decidedStates =
    [
        (int)SeriesLinkState.Confirmed, (int)SeriesLinkState.Auto, (int)SeriesLinkState.DontMatch,
        (int)SeriesLinkState.CollectionAbout, (int)SeriesLinkState.ArtistFolder,
    ];

    private readonly MangaPixerDbContext _db;
    private readonly ArtistFolderService _artists;
    private readonly CollectionAboutService _collections;
    private readonly IAuthorAliasSource _aliases;
    private readonly ILogger<FolderMatchService> _logger;

    public FolderMatchService(MangaPixerDbContext db, ArtistFolderService artists, CollectionAboutService collections,
        IAuthorAliasSource aliases, ILogger<FolderMatchService> logger)
    {
        _db = db;
        _artists = artists;
        _collections = collections;
        _aliases = aliases;
        _logger = logger;
    }

    // --- Preview ---

    /// <summary>The preview; error <c>invalid_node_ids</c> (none, or more than <see cref="MaxNodes"/>) or <c>invalid_kind</c>.</summary>
    public async Task<(string? Error, FolderMatchPreviewDto? Result)> PreviewAsync(FolderMatchPreviewRequest request, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(request.Kind))
            return ("invalid_kind", null);
        var ids = (request.NodeIds ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0 || ids.Count > MaxNodes)
            return ("invalid_node_ids", null);

        var watch = Stopwatch.StartNew();
        var nodes = await _db.CatalogNodes.AsNoTracking().Where(n => ids.Contains(n.PublicId))
            .Select(n => new { n.Id, n.PublicId, n.Kind, n.DisplayName }).ToListAsync(ct);
        var byPublicId = nodes.ToDictionary(n => n.PublicId, StringComparer.Ordinal);
        var nodeIds = nodes.Select(n => n.Id).ToList();
        var states = await _db.NodeSeriesLinks.AsNoTracking().Where(l => nodeIds.Contains(l.NodeId))
            .Select(l => new { l.NodeId, l.State }).ToDictionaryAsync(l => l.NodeId, l => (SeriesLinkState)l.State, ct);

        ArtistNameIndex? artistIndex = null;
        TitleIndex? titleIndex = null;
        if (request.Kind == FolderMatchKind.Artists)
            artistIndex = await BuildArtistIndexAsync(ct);
        else
            titleIndex = await BuildTitleIndexAsync(ct);

        var rows = new List<FolderMatchRowDto>(ids.Count);
        foreach (var id in ids)
        {
            if (!byPublicId.TryGetValue(id, out var node))
            {
                rows.Add(new FolderMatchRowDto { NodeId = id, DisplayName = string.Empty, Status = FolderMatchStatus.NotFound });
                continue;
            }
            SeriesLinkState? state = states.TryGetValue(node.Id, out var s) ? s : null;
            if (node.Kind != (int)CatalogNodeKind.Folder)
            {
                rows.Add(new FolderMatchRowDto
                {
                    NodeId = id, DisplayName = node.DisplayName, Status = FolderMatchStatus.NotAFolder, CurrentState = state,
                });
                continue;
            }

            IReadOnlyList<FolderMatchArtistDto> artists = artistIndex is null ? [] : ArtistsFor(artistIndex, node.DisplayName);
            IReadOnlyList<FolderMatchRecordDto> records = titleIndex is null ? [] : titleIndex.RecordsFor(node.DisplayName);
            var matches = artists.Count + records.Count;
            var status = state is { } st && s_decidedStates.Contains((int)st) ? FolderMatchStatus.Decided
                : matches == 0 ? FolderMatchStatus.NoMatch
                : matches == 1 ? FolderMatchStatus.Proposed
                : FolderMatchStatus.Ambiguous;
            rows.Add(new FolderMatchRowDto
            {
                NodeId = id,
                DisplayName = node.DisplayName,
                Status = status,
                CurrentState = state,
                Artists = artists,
                Records = records,
                SearchText = request.Kind == FolderMatchKind.Collections && records.Count == 0 ? SearchTextOf(node.DisplayName) : null,
            });
        }

        var compared = artistIndex?.Count ?? titleIndex?.Index.Count ?? 0;
        _logger.LogInformation(LogEvents.Metadata.FolderMatchPreviewed,
            "Folder match preview ({Kind}): {Nodes} nodes, {Compared} compared, {Proposed} proposed, {Ambiguous} ambiguous, {NoMatch} without a match, {Decided} decided in {ElapsedMs} ms",
            request.Kind, rows.Count, compared, rows.Count(r => r.Status == FolderMatchStatus.Proposed),
            rows.Count(r => r.Status == FolderMatchStatus.Ambiguous), rows.Count(r => r.Status == FolderMatchStatus.NoMatch),
            rows.Count(r => r.Status == FolderMatchStatus.Decided), watch.ElapsedMilliseconds);
        return (null, new FolderMatchPreviewDto { Kind = request.Kind, Rows = rows, Compared = compared });
    }

    /// <summary>
    /// The text "Search the web for the rest" sends for a folder: the Identify dialog's first suggestion for the name (the cleaned name as
    /// written, else its cleaned variants, else the name itself), normalized and length-checked as the search does.
    /// </summary>
    public static string? SearchTextOf(string displayName)
    {
        var normalized = TitleNormalizer.Normalize(displayName);
        foreach (var candidate in new[] { normalized.PrimaryWithExclamation }.Concat(normalized.Variants).Append(displayName))
        {
            var q = MetadataGateway.NormalizeQuery(candidate);
            if (q.Length is > 0 and <= MetadataGateway.MaxQueryLength)
                return q;
        }
        return null;
    }

    private static IReadOnlyList<FolderMatchArtistDto> ArtistsFor(ArtistNameIndex index, string folderName)
    {
        var forms = FolderNameMatcher.FolderForms(folderName);
        return index.Match(folderName).Select(a => new FolderMatchArtistDto
        {
            Name = a.DeclaredName,
            Role = a.Role,
            // The name as the folder writes it when the artist has that spelling, else the first name that is equal by NamesEqual.
            MatchedName = a.Names.FirstOrDefault(n => forms.Contains(TitleNormalizer.ScoringForm(n), StringComparer.Ordinal))
                ?? a.Names.FirstOrDefault(n => forms.Any(f => AutoMatchText.NamesEqual(f, n))) ?? a.DeclaredName,
            Provider = a.Provider,
            RecordCount = a.RecordCount,
        }).ToList();
    }

    private async Task<ArtistNameIndex> BuildArtistIndexAsync(CancellationToken ct)
    {
        var records = await _db.MetadataRecords.AsNoTracking()
            .Where(r => SeriesProviders.Contains(r.Provider) && r.RecordKind == 0 && r.CreatorsJson != null)
            .OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.Provider, r.CreatorsJson, r.FetchedAt })
            .ToListAsync(ct);
        var credits = new List<CreatorCredit>();
        foreach (var r in records)
        {
            foreach (var c in MetadataJson.ReadList<MetadataJson.Creator>(r.CreatorsJson))
                credits.Add(new CreatorCredit(r.Provider, c.ProviderId, c.Name, c.Role, r.Id, r.FetchedAt));
        }
        var authorIds = credits.Where(c => c.Provider == MetadataProviderAllowlist.MangaUpdates && !string.IsNullOrWhiteSpace(c.AuthorId))
            .Select(c => c.AuthorId!.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var stored = authorIds.Count == 0
            ? new Dictionary<string, StoredAuthor>()
            : (await _aliases.GetAsync(authorIds, ct)).Values
                .ToDictionary(a => a.AuthorId, a => new StoredAuthor(a.AuthorId, a.Name, a.OtherNames), StringComparer.Ordinal);
        return new ArtistNameIndex(FolderNameMatcher.GroupArtists(credits, stored));
    }

    private sealed record StoredRecord(long Id, string Provider, string ExternalId, string Title, int? Year, string? ProviderType, bool Linked);

    private sealed class TitleIndex(TitleNameIndex index, IReadOnlyDictionary<long, StoredRecord> records)
    {
        public TitleNameIndex Index { get; } = index;

        public IReadOnlyList<FolderMatchRecordDto> RecordsFor(string folderName) => Index.Match(folderName)
            .Select(m => (Match: m, Record: records[m.RecordId]))
            .Select(x => new FolderMatchRecordDto
            {
                Provider = x.Record.Provider,
                ExternalId = x.Record.ExternalId,
                Title = x.Record.Title,
                MatchedTitle = x.Match.MatchedTitle,
                Year = x.Record.Year,
                ProviderType = x.Record.ProviderType,
                LinkedAsSeries = x.Record.Linked,
            }).ToList();
    }

    private async Task<TitleIndex> BuildTitleIndexAsync(CancellationToken ct)
    {
        var linked = (await _db.NodeSeriesLinks.AsNoTracking()
            .Where(l => (l.State == (int)SeriesLinkState.Confirmed || l.State == (int)SeriesLinkState.Auto) && l.RecordId != null)
            .Select(l => l.RecordId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        var rows = await _db.MetadataRecords.AsNoTracking()
            .Where(r => SeriesProviders.Contains(r.Provider) && r.RecordKind == 0)
            .OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.Provider, r.ExternalId, r.Title, r.AltTitlesJson, r.StartYear, r.ProviderType })
            .ToListAsync(ct);
        // Linked series first (the order the index answers in), then the other stored records, each by id.
        var ordered = rows.OrderBy(r => linked.Contains(r.Id) ? 0 : 1).ThenBy(r => r.Id).ToList();
        var index = new TitleNameIndex(ordered
            .Select(r => new TitledRecord(r.Id, r.Title, MetadataJson.ReadList<string>(r.AltTitlesJson)))
            .ToList());
        var records = ordered.ToDictionary(r => r.Id,
            r => new StoredRecord(r.Id, r.Provider, r.ExternalId, r.Title, r.StartYear, r.ProviderType, linked.Contains(r.Id)));
        return new TitleIndex(index, records);
    }

    // --- Apply ---

    /// <summary>
    /// Marks each item's folder through the single action, in order; one folder's error does not stop the others. Error
    /// <c>invalid_items</c> (none, or more than <see cref="MaxNodes"/>) or <c>invalid_kind</c>.
    /// </summary>
    public async Task<(string? Error, FolderMatchApplyResultDto? Result)> ApplyAsync(
        FolderMatchApplyRequest request, string? actor, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(request.Kind))
            return ("invalid_kind", null);
        var items = request.Items ?? [];
        if (items.Count == 0 || items.Count > MaxNodes || items.Any(i => i is null || string.IsNullOrWhiteSpace(i.NodeId)))
            return ("invalid_items", null);

        var watch = Stopwatch.StartNew();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<FolderMatchApplyItemResultDto>(items.Count);
        foreach (var item in items)
        {
            var (code, queued) = !seen.Add(item.NodeId) ? ("duplicate", 0)
                : request.Kind == FolderMatchKind.Artists ? await ApplyArtistAsync(item, actor, ct)
                : await ApplyCollectionAsync(item, request.SetDoujinContent, actor, ct);
            results.Add(new FolderMatchApplyItemResultDto { NodeId = item.NodeId, Code = code, Queued = queued });
        }

        var succeeded = results.Count(r => r.Code == "ok");
        _logger.LogInformation(LogEvents.Metadata.FolderMatchApplied,
            "Folder match apply ({Kind}): {Succeeded} marked, {Failed} refused, {Queued} works queued in {ElapsedMs} ms",
            request.Kind, succeeded, results.Count - succeeded, results.Sum(r => r.Queued), watch.ElapsedMilliseconds);
        return (null, new FolderMatchApplyResultDto
        {
            Kind = request.Kind,
            Succeeded = succeeded,
            Failed = results.Count - succeeded,
            Results = results,
        });
    }

    private async Task<(string Code, int Queued)> ApplyArtistAsync(FolderMatchApplyItem item, string? actor, CancellationToken ct)
    {
        var outcome = await _artists.SetAsync(item.NodeId, new SetArtistFolderRequest { Name = item.Name, Role = item.Role }, actor, ct, AuditResult);
        return outcome switch
        {
            { Code: MetadataLinkResultCode.Ok, Result: { } result } => ("ok", result.Queued),
            { Code: MetadataLinkResultCode.NodeNotFound } => ("not_found", 0),
            _ => (outcome.Error ?? "invalid_request", 0),
        };
    }

    private async Task<(string Code, int Queued)> ApplyCollectionAsync(FolderMatchApplyItem item, bool setDoujinContent, string? actor, CancellationToken ct)
    {
        if (item.Provider is not { } provider || item.ExternalId is not { } externalId || !SeriesProviders.Contains(provider))
            return ("invalid_request", 0);
        // Stored records only: the single action would fetch a record it does not have - this path never does.
        if (!await _db.MetadataRecords.AnyAsync(r => r.Provider == provider && r.ExternalId == externalId && r.RecordKind == 0, ct))
            return ("record_not_stored", 0);
        var (code, result) = await _collections.SetAsync(item.NodeId, new SetCollectionAboutRequest
        {
            Provider = provider,
            ExternalId = externalId,
            MatchMethod = MetadataMatchMethod.Search,
            SetDoujinContent = setDoujinContent,
        }, actor, ct, AuditResult, storeImage: false);
        return code switch
        {
            MetadataLinkResultCode.Ok => ("ok", result?.Queued ?? 0),
            MetadataLinkResultCode.NodeNotFound => ("not_found", 0),
            MetadataLinkResultCode.NotAFolder => ("not_a_folder", 0),
            MetadataLinkResultCode.RecordNotFound => ("record_not_stored", 0),
            _ => ("invalid_request", 0),
        };
    }
}
