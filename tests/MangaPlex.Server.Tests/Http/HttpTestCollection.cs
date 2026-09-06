namespace com.lifepixer.mangaplex.Tests.Server.Http;

using Xunit;

/// <summary>
/// Serializes all HTTP integration tests so they don't set conflicting
/// environment variables in parallel. Each test class gets its own
/// factory instance (and temp DataRoot) via IClassFixture, but they
/// run sequentially within this collection.
/// </summary>
[CollectionDefinition("HttpSerial", DisableParallelization = true)]
public sealed class HttpTestCollection
{
}
