namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Hashes a server-owned image file (<see cref="CoverHash"/>); null when it cannot be hashed.</summary>
public interface ICoverHasher
{
    Task<ulong?> HashFileAsync(string path, CancellationToken ct);
}

/// <summary>Production hasher: the media worker decodes the file (protocol v4 <c>image_hash</c>); the server never does.</summary>
public sealed class WorkerCoverHasher(MediaWorkerPool pool) : ICoverHasher
{
    public async Task<ulong?> HashFileAsync(string path, CancellationToken ct)
    {
        var outcome = await pool.HashImageAsync(path, ct);
        return outcome.Success ? outcome.Hash : null;
    }
}

/// <summary>
/// Whether automatic matching may compare covers: the "Compare covers" sub-toggle under Automatic matching (owner
/// decision (c), 1.28.0: default ON). Until its settings column exists, <see cref="DefaultCoverCompareSetting"/>
/// answers from the options.
/// </summary>
public interface ICoverCompareSetting
{
    Task<bool> IsEnabledAsync(CancellationToken ct);
}

/// <summary>
/// The sub-toggle's default (ON), with the <c>Metadata:AutoMatch:CompareCovers</c> kill switch
/// (<see cref="MetadataAutoMatchOptions.CompareCovers"/>) so the comparison can be held without a code change.
/// </summary>
public sealed class DefaultCoverCompareSetting(MetadataAutoMatchOptions options) : ICoverCompareSetting
{
    public Task<bool> IsEnabledAsync(CancellationToken ct) => Task.FromResult(options.CompareCovers);
}

/// <summary>
/// Process-wide cache of LOCAL cover hashes by (archive, content version) - the 64 bits only, bounded, never
/// persisted (no new column: a hash is computed lazily when a comparison needs it); only successes are cached.
/// Candidate images are never cached: they are downloaded, hashed and deleted.
/// </summary>
public sealed class CoverHashCache
{
    public const int Capacity = 4096;

    private readonly Dictionary<(long ItemId, long ContentVersion), ulong?> _hashes = [];
    private readonly Queue<(long, long)> _order = new();
    private readonly object _gate = new();

    public bool TryGet(long itemId, long contentVersion, out ulong? hash)
    {
        lock (_gate)
            return _hashes.TryGetValue((itemId, contentVersion), out hash);
    }

    public void Set(long itemId, long contentVersion, ulong? hash)
    {
        lock (_gate)
        {
            if (_hashes.TryAdd((itemId, contentVersion), hash))
            {
                _order.Enqueue((itemId, contentVersion));
                while (_order.Count > Capacity)
                    _hashes.Remove(_order.Dequeue());
            }
            else
            {
                _hashes[(itemId, contentVersion)] = hash;
            }
        }
    }
}

/// <summary>
/// The result of one cover comparison: the candidates whose cover matched, how many images were downloaded, and a
/// code for the decision log (<see cref="CoverCheck"/>; a code, never a name or address).
/// </summary>
public sealed record CoverComparison(IReadOnlySet<string> Matches, int ImagesCompared, string Code)
{
    public static CoverComparison Skipped(string code) => new(new HashSet<string>(StringComparer.Ordinal), 0, code);
}

/// <summary>Why a cover comparison did or did not run (logged with the decision; codes only).</summary>
public static class CoverCheck
{
    public const string NotConfigured = "not_configured";
    public const string NoTie = "no_tie";
    public const string NotVolumeShaped = "not_volume_shaped";
    public const string NoCoverArchive = "no_cover_archive";
    public const string Off = "off";
    public const string NoImages = "no_images";
    public const string NoLocalCover = "no_local_cover";
    public const string ImageFailed = "image_failed";
    public const string Compared = "compared";
    public const string Matched = "matched";
}

/// <summary>
/// Cover similarity as tie-break evidence for automatic matching (1.28.0). Called by <see cref="AutoMatchLookup"/>
/// only for a tie that <see cref="CoverEvidence.TiedPair"/> accepts (volume-shaped work, top two within 0.02 on the
/// raw title, both at or above the review floor). Order of work, cheapest first, so nothing is downloaded when no
/// comparison is possible:
/// <list type="number">
/// <item>the sub-toggle (<see cref="ICoverCompareSetting"/>);</item>
/// <item>the work's local cover: the stored thumbnail of its cover archive (the folder cover rule: the first
/// live archive below the folder by sort key; for an archive work, the anchor archive); when the thumbnail pass has
/// not reached it yet, that same durable thumbnail is made now through <see cref="ThumbnailGenerationService"/>
/// (the worker reads page 1, as analysis does); hashed once per content version (<see cref="CoverHashCache"/>);</item>
/// <item>at most <see cref="CoverEvidence.MaxCandidates"/> candidate images, by the image URL the provider returned,
/// through the gateway's image path (allowlisted host, automatic call: daily budget + automatic pacing), written
/// to a scratch workspace, hashed by the worker and deleted; a candidate without an image, or whose image fails,
/// is not compared (the other one still can be).</item>
/// </list>
/// A gateway REFUSAL (budget, backoff, switch) propagates like every automatic call; any other failure means no
/// signal. Nothing is logged here (no URL, title or path).
/// </summary>
public sealed class AutoMatchCoverComparer
{
    private readonly MangaPixerDbContext _db;
    private readonly MetadataGateway _gateway;
    private readonly ICoverHasher _hasher;
    private readonly ICoverCompareSetting _setting;
    private readonly ThumbnailStore _thumbnails;
    private readonly ScratchWorkspaceManager _scratch;
    private readonly CoverHashCache _cache;
    private readonly ThumbnailGenerationService? _generator;

    public AutoMatchCoverComparer(
        MangaPixerDbContext db, MetadataGateway gateway, ICoverHasher hasher, ICoverCompareSetting setting,
        ThumbnailStore thumbnails, ScratchWorkspaceManager scratch, CoverHashCache cache,
        ThumbnailGenerationService? generator = null)
    {
        _generator = generator;
        _db = db;
        _gateway = gateway;
        _hasher = hasher;
        _setting = setting;
        _thumbnails = thumbnails;
        _scratch = scratch;
        _cache = cache;
    }

    /// <summary>The cover archive of a work: the first live archive below the folder by sort key, or the anchor archive.</summary>
    public static long? CoverArchiveOf(LibraryTreeSnapshot tree, DetectedWork work)
    {
        if (work.Level == MatchLevel.Archive)
            return work.AnchorNodeId;
        LibraryTreeSnapshot.Node? best = null;
        var stack = new Stack<long>([work.FolderId]);
        while (stack.Count > 0)
        {
            foreach (var child in tree.ChildrenOf(stack.Pop()))
            {
                if (child.IsFolder)
                    stack.Push(child.Id);
                else if (best is null || string.CompareOrdinal(child.SortKey, best.SortKey) < 0
                    || (string.Equals(child.SortKey, best.SortKey, StringComparison.Ordinal) && child.Id < best.Id))
                    best = child;
            }
        }
        return best?.Id;
    }

    /// <summary>
    /// Compares the local cover of <paramref name="coverArchiveId"/> with the covers of the tied candidates
    /// (external id -> image URL as the provider returned it).
    /// </summary>
    public async Task<CoverComparison> CompareAsync(
        long coverArchiveId, long libraryId, string provider, IReadOnlyList<(string ExternalId, string? ImageUrl)> candidates,
        MetadataCallContext call, CancellationToken ct)
    {
        if (!await _setting.IsEnabledAsync(ct))
            return CoverComparison.Skipped(CoverCheck.Off);
        var withImages = candidates.Where(c => !string.IsNullOrWhiteSpace(c.ImageUrl)).Take(CoverEvidence.MaxCandidates).ToList();
        if (withImages.Count == 0)
            return CoverComparison.Skipped(CoverCheck.NoImages);
        if (await LocalHashAsync(coverArchiveId, ct) is not { } local)
            return CoverComparison.Skipped(CoverCheck.NoLocalCover);

        var hashes = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var compared = 0;
        using var workspace = _scratch.AllocateWorkspace();
        foreach (var (externalId, url) in withImages)
        {
            byte[] bytes;
            try
            {
                bytes = await _gateway.FetchImageAsync(provider, libraryId, url!, ct, call);
            }
            catch (MetadataGatewayException ex) when (!MetadataAutoMatchService.IsRefusal(ex))
            {
                continue; // The image failed: no evidence for this candidate.
            }
            var file = Path.Combine(workspace.Path, "candidate-" + compared.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".img");
            await File.WriteAllBytesAsync(file, bytes, ct);
            compared++;
            var hash = await _hasher.HashFileAsync(file, ct);
            File.Delete(file);
            if (hash is not null)
                hashes[externalId] = hash.Value;
        }
        var matches = CoverEvidence.Matching(local, hashes);
        var code = hashes.Count == 0 ? CoverCheck.ImageFailed : matches.Count > 0 ? CoverCheck.Matched : CoverCheck.Compared;
        return new CoverComparison(matches, compared, code);
    }

    private async Task<ulong?> LocalHashAsync(long archiveId, CancellationToken ct)
    {
        var contentVersion = await _db.ArchiveItems.AsNoTracking()
            .Where(a => a.NodeId == archiveId)
            .Select(a => (long?)a.ContentVersion)
            .FirstOrDefaultAsync(ct);
        if (contentVersion is not { } version)
            return null;
        if (_cache.TryGet(archiveId, version, out var cached))
            return cached;
        var path = _thumbnails.GetThumbnailPath(archiveId, version);
        // A work matched right after its scan may be ahead of the thumbnail pass: make that same durable thumbnail
        // now (the worker reads page 1, as analysis does), before any image is downloaded.
        if (!File.Exists(path) && (_generator is null || !await _generator.GenerateForItemAsync(archiveId, ct) || !File.Exists(path)))
            return null; // No thumbnail (yet): no comparison, nothing cached.
        var hash = await _hasher.HashFileAsync(path, ct);
        if (hash is not null)
            _cache.Set(archiveId, version, hash); // A failure (e.g. every worker busy) is not remembered: next tie, next try.
        return hash;
    }
}
