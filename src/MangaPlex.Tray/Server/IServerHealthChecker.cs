namespace com.lifepixer.mangaplex.Tray.Server;

/// <summary>
/// Abstracts the <c>/health</c> poll so <see cref="ServerProcessManager"/> can
/// be unit-tested without a real HTTP listener.
/// </summary>
public interface IServerHealthChecker
{
    Task<bool> IsHealthyAsync(Uri baseUri, CancellationToken cancellationToken);
}
