namespace com.lifepixer.mangaplex.Tests.MediaWorker;

using Xunit;

/// <summary>
/// Smoke test: confirms the worker process identity is wired up correctly.
/// Archive and image handling are covered by the Archives/ and Images/ test suites.
/// </summary>
public sealed class WorkerStartupTests
{
    [Fact]
    public void ProductIdentity_NameIsMangaPlex()
    {
        Assert.Equal("MangaPlex", Core.ProductIdentity.Name);
    }
}
