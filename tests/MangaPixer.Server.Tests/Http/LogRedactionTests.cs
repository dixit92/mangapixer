namespace com.lifepixer.mangapixer.Tests.Server.Http;

using com.lifepixer.mangapixer.Server.Hosting;
using Xunit;

/// <summary>
/// Tests that the Serilog redacting enricher scrubs properties whose names
/// suggest sensitive or path-bearing content.
/// </summary>
public sealed class LogRedactionTests
{
    [Theory]
    [InlineData("ArchivePath")]
    [InlineData("RootPath")]
    [InlineData("SourcePath")]
    [InlineData("ScratchPath")]
    [InlineData("CachePath")]
    [InlineData("DataRoot")]
    [InlineData("EntryName")]
    [InlineData("Title")]
    [InlineData("Password")]
    [InlineData("Token")]
    [InlineData("Cookie")]
    [InlineData("Secret")]
    [InlineData("ApiKey")]
    [InlineData("ConnectionString")]
    [InlineData("MyPath")]
    [InlineData("UserToken")]
    public void IsSensitiveName_DetectsSensitiveNames(string name)
    {
        Assert.True(RedactingDestructuringPolicy.IsSensitiveName(name));
    }

    [Theory]
    [InlineData("JobId")]
    [InlineData("Count")]
    [InlineData("Error")]
    [InlineData("UserName")]
    [InlineData("LibraryId")]
    [InlineData("PageCount")]
    [InlineData("Duration")]
    [InlineData("")]
    public void IsSensitiveName_PassesThroughSafeNames(string name)
    {
        Assert.False(RedactingDestructuringPolicy.IsSensitiveName(name));
    }
}
