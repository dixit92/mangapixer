namespace com.lifepixer.mangapixer.Tests.Tray.Startup;

using com.lifepixer.mangapixer.Tray.Startup;
using Xunit;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void FirstGuard_ForUniqueName_IsFirstInstance()
    {
        using var guard = new SingleInstanceGuard($"MangaPlexTrayTests_{Guid.NewGuid():N}");

        Assert.True(guard.IsFirstInstance);
    }

    [Fact]
    public void SecondGuard_ForSameName_IsNotFirstInstance()
    {
        var name = $"MangaPlexTrayTests_{Guid.NewGuid():N}";
        using var first = new SingleInstanceGuard(name);

        using var second = new SingleInstanceGuard(name);

        Assert.False(second.IsFirstInstance);
    }

    [Fact]
    public void AfterFirstGuardDisposed_NewGuardForSameName_IsFirstInstance()
    {
        var name = $"MangaPlexTrayTests_{Guid.NewGuid():N}";
        using (new SingleInstanceGuard(name)) { }

        using var second = new SingleInstanceGuard(name);

        Assert.True(second.IsFirstInstance);
    }
}
