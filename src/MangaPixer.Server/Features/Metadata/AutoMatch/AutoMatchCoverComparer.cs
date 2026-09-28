namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Hashes a server-owned image file (<see cref="PerceptualHash"/>); null when it cannot be hashed.</summary>
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
/// persisted (no new column: a hash is computed lazily when a comparison needs it). Candidate images are never
/// cached: they are downloaded, hashed and deleted.
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

/// <summary>The result of one cover comparison: the candidates whose cover matched, and how many images were compared.</summary>
public sealed record CoverComparison(IReadOnlySet<string> Matches, int ImagesCompared)
{
    public static CoverComparison None { get; } = new(new HashSet<string>(StringComparer.Ordinal), 0);
}

/// <summary>
/// Cover similarity as tie-break evidence for automatic matching (1.28.0). Called by <see cref="AutoMatchLookup"/>
/// only for a tie that <see cref="CoverEvidence.TiedPair"/> accepts (volume-shaped work, top two within 0.02 on the
/// raw title, both at or above the review floor). Order of work, cheapest first, so nothing is downloaded when no
/// comparison is possible:
/// <list type="number">
/// <item>the sub-toggle (<see cref="ICoverCompareSetting"/>);</item>
/// <item>the work's local cover: the stored thumbnail of its cover archive (the folder cover rule: the first
/// live archive below the folder by sort key; for an archive work, the anchor archive) - never generated here,
/// no source archive is opened; hashed once per content version (<see cref="CoverHashCache"/>);</item>
/// <item>at most <see cref="CoverEvidence.MaxCandidates"/> candidate images, by the image URL the provider returned,
/// through the gateway's image path (allowlisted host, automatic call: daily budget + automatic pacing), written
/// to a scratch workspace, hashed by the worker and deleted.</item>
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

    public AutoMatchCoverComparer(
        MangaPixerDbContext db, MetadataGateway gateway, ICoverHasher hasher, ICoverCompareSetting setting,
        ThumbnailStore thumbnails, ScratchWorkspaceManager scratch, CoverHashCache cache)
    {
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
        var withImages = candidates.Where(c => !string.IsNullOrWhiteSpace(c.ImageUrl)).Take(CoverEvidence.MaxCandidates).ToList();
        if (withImages.Count < 2 || !await _setting.IsEnabledAsync(ct))
            return CoverComparison.None;
        if (await LocalHashAsync(coverArchiveId, ct) is not { } local)
            return CoverComparison.None;

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
                break; // The image failed: one cover alone cannot break the tie, so the next one is not fetched.
            }
            var file = Path.Combine(workspace.Path, "candidate-" + compared.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".img");
            await File.WriteAllBytesAsync(file, bytes, ct);
            compared++;
            var hash = await _hasher.HashFileAsync(file, ct);
            File.Delete(file);
            if (hash is null)
                break;
            hashes[externalId] = hash.Value;
        }
        return new CoverComparison(CoverEvidence.Matching(local, hashes), compared);
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
        if (!File.Exists(path))
            return null; // Not generated yet: try again next time, nothing cached.
        var hash = await _hasher.HashFileAsync(path, ct);
        _cache.Set(archiveId, version, hash);
        return hash;
    }
}
