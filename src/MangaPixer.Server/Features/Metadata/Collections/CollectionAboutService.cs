namespace com.lifepixer.mangapixer.Server.Features.Metadata.Collections;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// "Collection about" (1.34.0): a folder of works about one series (fan works) - linked to the REAL series record as context, while
/// its items are works of their own. Marking a folder does three things, in this order:
/// 1. the row (<see cref="MetadataIdentifyService.SetCollectionAboutAsync"/>: the record is fetched first when not stored, as for a link);
/// 2. with <see cref="SetCollectionAboutRequest.SetDoujinContent"/>, the folder's Content becomes "Doujinshi &amp; adult one-shots" when
///    its effective Content is another one (automatic searches include doujinshi records only below such a folder);
/// 3. the works at and below the folder are queued (<see cref="MetadataAutoMatchService.EnqueueBelowAsync"/>, only while automatic
///    matching is on); their searches put the series in front (<c>&lt;series&gt; dj - &lt;title&gt;</c>, the planner's parody form).
/// </summary>
public sealed class CollectionAboutService
{
    private readonly MangaPixerDbContext _db;
    private readonly MetadataIdentifyService _identify;
    private readonly MetadataLinkService _links;
    private readonly MetadataFolderContentService _content;
    private readonly MetadataAutoMatchService _autoMatch;

    public CollectionAboutService(MangaPixerDbContext db, MetadataIdentifyService identify, MetadataLinkService links,
        MetadataFolderContentService content, MetadataAutoMatchService autoMatch)
    {
        _db = db;
        _identify = identify;
        _links = links;
        _content = content;
        _autoMatch = autoMatch;
    }

    /// <summary>
    /// Marks a folder "Collection about" a record. May throw <see cref="MetadataGatewayException"/> when the record must be fetched.
    /// <paramref name="storeImage"/> false (1.38.0, Match folders by name - stored data only): a stored record's poster that is not stored
    /// yet is not downloaded now.
    /// </summary>
    public async Task<(MetadataLinkResultCode Code, CollectionAboutResultDto? Result)> SetAsync(
        string nodePublicId, SetCollectionAboutRequest request, string? actor, CancellationToken ct = default, string? auditResult = null,
        bool storeImage = true)
    {
        var (code, change) = await _identify.SetCollectionAboutAsync(nodePublicId, new LinkSeriesRequest
        {
            Provider = request.Provider,
            ExternalId = request.ExternalId,
            MatchMethod = request.MatchMethod,
        }, actor, ct, auditResult, storeImage);
        if (code != MetadataLinkResultCode.Ok || change is null)
            return (code, null);

        var node = await _db.CatalogNodes.AsNoTracking().Where(n => n.PublicId == nodePublicId)
            .Select(n => new { n.Id, n.LibraryId }).FirstAsync(ct);
        var contentSet = false;
        if (request.SetDoujinContent
            && (await MetadataFolderContentService.ResolveAsync(_db, node.Id, ct)).Content != MetadataFolderContent.DoujinshiAndAdultOneShots)
        {
            var (contentCode, _) = await _content.SetAsync(nodePublicId, MetadataFolderContent.DoujinshiAndAdultOneShots, actor, ct);
            contentSet = contentCode == MetadataLinkResultCode.Ok;
        }
        var queued = await _autoMatch.EnqueueBelowAsync(node.LibraryId, node.Id, ct);
        return (MetadataLinkResultCode.Ok, new CollectionAboutResultDto { Change = change, ContentSet = contentSet, Queued = queued });
    }

    /// <summary>Removes only a "Collection about" row (any other row is left alone). Idempotent; the Content stays.</summary>
    public Task<(MetadataLinkResultCode Code, NodeSeriesLinkChangeDto? Change)> ClearAsync(string nodePublicId, string? actor, CancellationToken ct = default) =>
        _links.RemoveAsync(nodePublicId, SeriesLinkState.CollectionAbout, actor, ct);
}
