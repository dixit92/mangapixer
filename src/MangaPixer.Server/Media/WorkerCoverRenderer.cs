namespace com.lifepixer.mangapixer.Server.Media;

using com.lifepixer.mangapixer.Core.WorkerProtocol;

/// <summary>
/// Renders cover thumbnails (1.29.0 cover layer): the seam the volume-cover download (re-encode + hash provider bytes) and
/// the local spread crop call, so their tests can fake the worker. Production: <see cref="WorkerCoverRenderer"/>.
/// </summary>
public interface ICoverRenderer
{
    Task<CoverRenderOutcome> RenderAsync(CoverRenderRequest request, CancellationToken ct);
}

/// <summary>The production <see cref="ICoverRenderer"/>: protocol v5 <c>cover_render</c> in a media worker.</summary>
public sealed class WorkerCoverRenderer(MediaWorkerPool pool) : ICoverRenderer
{
    public Task<CoverRenderOutcome> RenderAsync(CoverRenderRequest request, CancellationToken ct) => pool.RenderCoverAsync(request, ct);
}
