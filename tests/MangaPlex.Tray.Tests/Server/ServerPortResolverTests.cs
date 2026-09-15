namespace com.lifepixer.mangaplex.Tests.Tray.Server;

using com.lifepixer.mangaplex.Tray.Server;
using Xunit;

public sealed class ServerPortResolverTests
{
    private sealed class FakePortAvailabilityChecker(params int[] availablePorts) : IPortAvailabilityChecker
    {
        private readonly HashSet<int> _available = [.. availablePorts];

        public bool IsPortAvailable(int port) => _available.Contains(port);
    }

    [Fact]
    public void ResolveAvailablePort_WhenPreferredPortIsFree_ReturnsIt()
    {
        var resolver = new ServerPortResolver(new FakePortAvailabilityChecker(27272, 27273));

        var resolved = resolver.ResolveAvailablePort(27272);

        Assert.Equal(27272, resolved);
    }

    [Fact]
    public void ResolveAvailablePort_WhenPreferredPortIsTaken_ScansForwardToTheFirstFreeCandidate()
    {
        var resolver = new ServerPortResolver(new FakePortAvailabilityChecker(27275));

        var resolved = resolver.ResolveAvailablePort(27272);

        Assert.Equal(27275, resolved);
    }

    [Fact]
    public void ResolveAvailablePort_WhenNoCandidateInRangeIsFree_ReturnsNull()
    {
        var resolver = new ServerPortResolver(new FakePortAvailabilityChecker());

        var resolved = resolver.ResolveAvailablePort(27272, maxCandidates: 5);

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveAvailablePort_DoesNotScanPastMaxCandidates()
    {
        var resolver = new ServerPortResolver(new FakePortAvailabilityChecker(27272 + 5));

        var resolved = resolver.ResolveAvailablePort(27272, maxCandidates: 5);

        Assert.Null(resolved);
    }
}
