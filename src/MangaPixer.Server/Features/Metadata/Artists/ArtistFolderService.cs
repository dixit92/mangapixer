namespace com.lifepixer.mangapixer.Server.Features.Metadata.Artists;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Authors;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Declared;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Outcome of an artist-folder call: a link result code, the value, and a machine error code for 400s.</summary>
public sealed record ArtistFolderOutcome(MetadataLinkResultCode Code, ArtistFolderResultDto? Result = null, string? Error = null);

/// <summary>
/// "Artist folder" (1.37.0, owner): an admin says a FOLDER holds the works of one artist. One action that, in this order:
/// 1. marks the folder (<see cref="MetadataLinkService.SetArtistFolderAsync"/>: <see cref="SeriesLinkState.ArtistFolder"/>, no record) -
///    it is never linked to a work, nothing inside inherits a link from it, and automatic matching keeps working inside it, archive by
///    archive (unlike Don't match; like Collection about); a waiting folder-level review row and its candidates go;
/// 2. declares the artist as the folder's creator (default the folder's name, role <c>author</c> = "Story &amp; art"), keeping the
///    folder's other declared facts - declared creators raise records by that creator and flow down to the archives (nearest folder
///    wins); the name is only compared on the server, never sent;
/// 3. queues the works at and below the folder at once (<see cref="MetadataAutoMatchService.EnqueueBelowAsync"/> with the finished
///    unmatched works again - the artist is new scoring input), only while automatic matching and the library's Fetch switch are on.
/// No network here. Removing the mark removes only the row: the declared creator stays (Declared facts edits it).
/// 1.45.0: the request may name the MangaUpdates author the admin picked from an author search (<c>provider</c> + <c>externalId</c>,
/// validated, never fetched): it is stored on the declared artist as its provider reference <c>mangaupdates:&lt;id&gt;</c>
/// (<c>declared_facts.ExternalRef</c>) and the audit result is <c>author_search</c>. Still no request: the author's other names come later
/// through "Artists' other names", whose eligible ids include declared references.
/// 1.45.0 (owner, 2026-10-19: "attempt to relink"): marked from the Artist folder dialog without a picked author (none picked, or the
/// name edited away from the pick), an artist whose row carries no reference yet gets the MangaUpdates id of the one provider-backed
/// creator known here by that name (<see cref="LocalAuthorRelink"/>; audit result <c>local_relink</c>) - stored data only. Match folders
/// by name decides its own ids (its audit result is passed in) and is not relinked here.
/// </summary>
public sealed class ArtistFolderService
{
    /// <summary>The role a marked artist gets unless the admin picks another (the web shows it as "Story &amp; art").</summary>
    public const string DefaultRole = "author";

    private readonly MangaPixerDbContext _db;
    private readonly MetadataLinkService _links;
    private readonly DeclaredFactsService _declared;
    private readonly MetadataAutoMatchService _autoMatch;
    private readonly Facets.IFacetIndexNudge _facetNudge;

    /// <summary>The audit result of a folder marked with an author picked from an author search (1.45.0).</summary>
    public const string AuthorSearchAuditResult = "author_search";

    public ArtistFolderService(MangaPixerDbContext db, MetadataLinkService links, DeclaredFactsService declared, MetadataAutoMatchService autoMatch,
        Facets.IFacetIndexNudge? facetNudge = null)
    {
        _db = db;
        _links = links;
        _declared = declared;
        _autoMatch = autoMatch;
        _facetNudge = facetNudge ?? Facets.NoFacetIndexNudge.Instance;
    }

    /// <summary>
    /// Marks a folder an artist's folder. Errors (400): <c>not_a_folder</c>, <c>creator_name_invalid</c>, <c>creator_role_invalid</c>,
    /// <c>creators_too_many</c> (the folder already declares the most creators), <c>author_ref_invalid</c> (1.45.0: a provider other than
    /// MangaUpdates, a non-numeric id, or only one of the two); nothing changes on an error.
    /// </summary>
    public async Task<ArtistFolderOutcome> SetAsync(string nodePublicId, SetArtistFolderRequest? request, string? actor,
        CancellationToken ct = default, string? auditResult = null)
    {
        var node = await _db.CatalogNodes.AsNoTracking().Where(n => n.PublicId == nodePublicId)
            .Select(n => new { n.Id, n.LibraryId, n.Kind, n.DisplayName }).FirstOrDefaultAsync(ct);
        if (node is null)
            return new(MetadataLinkResultCode.NodeNotFound);
        if (node.Kind != (int)CatalogNodeKind.Folder)
            return new(MetadataLinkResultCode.NotAFolder, Error: "not_a_folder");

        var requestedName = request?.Name;
        var name = DeclaredFactKeys.CleanName(string.IsNullOrWhiteSpace(requestedName) ? node.DisplayName : requestedName);
        if (name is null)
            return new(MetadataLinkResultCode.InvalidRequest, Error: "creator_name_invalid");
        var requestedRole = request?.Role;
        var role = string.IsNullOrWhiteSpace(requestedRole) ? DefaultRole : requestedRole.Trim().ToLowerInvariant();
        if (!DeclaredFactKeys.CreatorRoles.Contains(role))
            return new(MetadataLinkResultCode.InvalidRequest, Error: "creator_role_invalid");
        var (authorRef, refError) = AuthorReference.FromRequest(request?.Provider, request?.ExternalId);
        if (refError is not null)
            return new(MetadataLinkResultCode.InvalidRequest, Error: refError);
        if (authorRef is not null)
            auditResult = AuthorSearchAuditResult;
        else if (auditResult is null && !await HasReferenceAsync(node.LibraryId, node.Id, name, role, ct)
            && await LocalAuthorRelink.FindAsync(_db, name, ct) is { } relinkId)
        {
            authorRef = AuthorReference.MangaUpdates(relinkId);
            auditResult = LocalAuthorRelink.AuditResult;
        }

        // The folder's OWN facts (its type and other creators stay); the artist goes first unless it is declared already.
        var scope = await _declared.GetFolderAsync(nodePublicId, ct);
        var own = scope.Value?.Own ?? new DeclaredFactValuesDto();
        var key = DeclaredFactsComparer.NameKey(name);
        var present = own.Creators.Any(c => DeclaredFactsComparer.NameKey(c.Name) == key
            && string.Equals(c.Role ?? string.Empty, role, StringComparison.Ordinal));
        SetDeclaredFactsRequest? facts = null;
        if (!present)
        {
            if (own.Creators.Count >= DeclaredFactKeys.MaxCreators)
                return new(MetadataLinkResultCode.InvalidRequest, Error: "creators_too_many");
            facts = new SetDeclaredFactsRequest
            {
                Type = own.Type,
                Creators = own.Creators.Prepend(new DeclaredCreatorDto { Name = name, Role = role }).ToList(),
            };
        }

        var (code, change) = await _links.SetArtistFolderAsync(nodePublicId, actor, ct, auditResult);
        if (code != MetadataLinkResultCode.Ok || change is null)
            return new(code);
        if (facts is not null)
        {
            var set = await _declared.SetFolderAsync(nodePublicId, facts, actor, ct);
            if (set.Code != MetadataLinkResultCode.Ok)
                return new(set.Code, Error: set.Error);
        }
        if (authorRef is not null)
            await SetReferenceAsync(node.LibraryId, node.Id, name, role, authorRef, ct);
        var queued = await _autoMatch.EnqueueBelowAsync(node.LibraryId, node.Id, ct, requeueFinished: true);
        return new(MetadataLinkResultCode.Ok, new ArtistFolderResultDto
        {
            Change = change,
            Artist = new DeclaredCreatorDto { Name = name, Role = role },
            CreatorAdded = facts is not null,
            Queued = queued,
            AuthorRef = authorRef,
        });
    }

    /// <summary>Whether the folder's own declared creator of that name and role already carries a MangaUpdates reference (kept then).</summary>
    private async Task<bool> HasReferenceAsync(long libraryId, long nodeId, string name, string role, CancellationToken ct)
    {
        var key = DeclaredFactsComparer.NameKey(name);
        var refs = await _db.DeclaredFacts.AsNoTracking()
            .Where(f => f.LibraryId == libraryId && f.NodeId == nodeId && f.Key == DeclaredFactKeys.Creator && f.ExternalRef != null)
            .Select(f => new { f.Value, f.Role, f.ExternalRef })
            .ToListAsync(ct);
        return refs.Any(f => DeclaredFactsComparer.NameKey(f.Value) == key && string.Equals(f.Role ?? string.Empty, role, StringComparison.Ordinal)
            && AuthorReference.MangaUpdatesIdOf(f.ExternalRef) is not null);
    }

    /// <summary>
    /// Stores the picked author's reference on the folder's own declared creator row of that name and role (the row the call above made
    /// or found); nudges the tag index when it changed. Local only.
    /// </summary>
    private async Task SetReferenceAsync(long libraryId, long nodeId, string name, string role, string reference, CancellationToken ct)
    {
        var key = DeclaredFactsComparer.NameKey(name);
        var rows = await _db.DeclaredFacts
            .Where(f => f.LibraryId == libraryId && f.NodeId == nodeId && f.Key == DeclaredFactKeys.Creator)
            .ToListAsync(ct);
        var row = rows.FirstOrDefault(f => DeclaredFactsComparer.NameKey(f.Value) == key && string.Equals(f.Role ?? string.Empty, role, StringComparison.Ordinal));
        if (row is null || string.Equals(row.ExternalRef, reference, StringComparison.Ordinal))
            return;
        row.ExternalRef = reference;
        await _db.SaveChangesAsync(ct);
        _facetNudge.LibraryChanged(libraryId, Facets.FacetNudgeReason.DeclaredFactsChanged);
    }

    /// <summary>Removes only an "Artist folder" row (any other row is left alone). Idempotent; the declared creator stays.</summary>
    public Task<(MetadataLinkResultCode Code, NodeSeriesLinkChangeDto? Change)> ClearAsync(string nodePublicId, string? actor, CancellationToken ct = default) =>
        _links.RemoveAsync(nodePublicId, SeriesLinkState.ArtistFolder, actor, ct);
}
