namespace com.lifepixer.mangapixer.Tests.Server.Http;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

/// <summary>
/// Service overrides every test host's <c>ConfigureTestServices</c> applies first, so a disposed host becomes garbage at once (the suite
/// boots thousands in one process - <c>TestHostLifetimeTests</c>).
/// </summary>
public static class TestHostServices
{
    /// <summary>
    /// <see cref="IHttpClientFactory"/> arms a timer per handler for its lifetime (2 minutes by default); the timer's callback is bound
    /// to the factory, which holds the host's root service provider, so each host that created a client stayed reachable for at least
    /// 2 minutes after Dispose (~150 hosts at once in the Server suite). With an infinite lifetime no timer is armed. Test clients talk
    /// to stubs, so rotating their connections buys nothing.
    /// </summary>
    public static void ReleaseWithTheHost(IServiceCollection services) =>
        services.PostConfigureAll<HttpClientFactoryOptions>(options => options.HandlerLifetime = Timeout.InfiniteTimeSpan);
}
