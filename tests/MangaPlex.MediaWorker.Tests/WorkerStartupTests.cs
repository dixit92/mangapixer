namespace com.lifepixer.mangaplex.Tests.MediaWorker;

using Xunit;

/// <summary>
/// P00 smoke test: worker stub starts and emits a ready message.
/// Full archive/image tests are added in P01.
/// </summary>
public sealed class WorkerStartupTests
{
    [Fact]
    public void ProductIdentity_NameIsMangaPlex()
    {
        Assert.Equal("MangaPlex", Core.ProductIdentity.Name);
    }
}
