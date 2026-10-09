namespace com.lifepixer.mangapixer.Tests.Server.Http;

using com.lifepixer.mangapixer.Server.Features.Metadata.Authors;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// The artists' other-names seam (1.38.0) is registered in the running app and answers from stored data only: with no stored
/// author record, every lookup is empty.
/// </summary>
public sealed class AuthorAliasSourceWiringTests : IDisposable
{
    private readonly MangaPixerWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task TheAliasSource_IsRegistered_AndKnowsNoAuthorWithoutAStoredRecord()
    {
        using var scope = _factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<IAuthorAliasSource>();

        var found = await source.GetAsync(["12345", "67890"], CancellationToken.None);

        Assert.Empty(found);
    }
}
