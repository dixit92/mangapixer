namespace com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaDex;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Downloads one web volume cover (1.29.0, design 4.3): the 512-pixel image from <c>uploads.mangadex.org</c>, by the
/// record id and file name MangaDex returned (<see cref="MangaDexProvider.CoverImageUrl"/>), through the gateway's image
/// path (MangaDex's image host only, budget, pacing, backoff). The bytes go to a scratch workspace, the media worker
/// re-encodes them into the thumbnail variant (WebP, longest edge 400) and hashes the result in the same decode
/// (<c>cover_render</c>); the WebP lands in the data root (<see cref="VolumeCoverStore"/>, a path built here) and the
/// scratch file is deleted. The server never decodes provider bytes. Logs carry ids and codes only.
/// </summary>
public sealed class VolumeCoverFetcher
{
    private readonly MangaPixerDbContext _db;
    private readonly MetadataGateway _gateway;
    private readonly ICoverRenderer _renderer;
    private readonly ScratchWorkspaceManager _scratch;
    private readonly VolumeCoverStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<VolumeCoverFetcher> _logger;

    public VolumeCoverFetcher(
        MangaPixerDbContext db, MetadataGateway gateway, ICoverRenderer renderer, ScratchWorkspaceManager scratch, VolumeCoverStore store,
        TimeProvider time, ILogger<VolumeCoverFetcher> logger)
    {
        _db = db;
        _gateway = gateway;
        _renderer = renderer;
        _scratch = scratch;
        _store = store;
        _time = time;
        _logger = logger;
    }

    /// <summary>Worker outcomes that say "try again later" rather than "this image is bad".</summary>
    private static readonly HashSet<string> s_transient = new(StringComparer.Ordinal) { "busy", "unavailable", "timeout", "cancelled" };

    /// <summary>
    /// Downloads and stores <paramref name="cover"/> (a tracked row). True when it is Stored afterwards. A gateway
    /// refusal propagates (nothing counted); a provider failure or an undecodable image marks the row Failed (listed
    /// again on the next list refresh); a busy worker leaves it Listed.
    /// </summary>
    public async Task<bool> DownloadAsync(VolumeCoverEntity cover, long libraryId, MetadataCallContext? call, CancellationToken ct = default)
    {
        var mangaId = await _db.MetadataRecords.AsNoTracking()
            .Where(r => r.Id == cover.ProviderRecordId && r.Provider == MetadataProviderAllowlist.MangaDex)
            .Select(r => r.ExternalId)
            .FirstOrDefaultAsync(ct);
        var url = mangaId is null ? null : MangaDexProvider.CoverImageUrl(mangaId, cover.RemoteFile);
        if (url is null || !VolumeCoverStore.IsValidPublicId(cover.PublicId))
            return await FailAsync(cover, "invalid_cover", ct);

        byte[] bytes;
        try
        {
            bytes = await _gateway.FetchImageAsync(MetadataProviderAllowlist.MangaDex, libraryId, url, ct, call);
        }
        catch (MetadataGatewayException ex) when (!MetadataAutoMatchService.IsRefusal(ex))
        {
            return await FailAsync(cover, ex.Code, ct);
        }

        var version = cover.StoredVersion + 1;
        var output = _store.PathFor(cover.PublicId, version);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        CoverRenderOutcome outcome;
        using (var workspace = _scratch.AllocateWorkspace())
        {
            var input = Path.Combine(workspace.Path, "volume-cover.img");
            await File.WriteAllBytesAsync(input, bytes, ct);
            outcome = await _renderer.RenderAsync(new CoverRenderRequest
            {
                JobId = "volume-cover",
                Source = CoverRenderSources.Image,
                ImagePath = input,
                OutputPath = output,
                ComputeHash = true,
            }, ct);
            TryDelete(input);
        }

        if (!outcome.Success)
        {
            TryDelete(output);
            if (s_transient.Contains(outcome.ErrorType ?? string.Empty))
                return false; // Stays Listed: the next pass tries again.
            return await FailAsync(cover, outcome.ErrorType ?? "render_failed", ct);
        }

        var previous = cover.StoredVersion;
        cover.StoredVersion = version;
        cover.State = (int)VolumeCoverState.Stored;
        cover.Hash = outcome.Hash is { } h ? unchecked((long)h) : null;
        cover.Width = outcome.Width;
        cover.Height = outcome.Height;
        cover.StoredAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        if (previous > 0)
            _store.Delete(cover.PublicId, previous);
        _logger.LogInformation(LogEvents.Metadata.VolumeCoverStored, "Volume cover {CoverId} of record {RecordId} stored (version {Version})",
            cover.Id, cover.ProviderRecordId, version);
        return true;
    }

    private async Task<bool> FailAsync(VolumeCoverEntity cover, string code, CancellationToken ct)
    {
        // A cover stored before keeps its earlier file (and stays usable); a new one is Failed until the next list refresh.
        cover.State = cover.StoredVersion > 0 ? (int)VolumeCoverState.Stored : (int)VolumeCoverState.Failed;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(LogEvents.Metadata.VolumeCoverFailed, "Volume cover {CoverId} of record {RecordId} failed: {Code}",
            cover.Id, cover.ProviderRecordId, code);
        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
