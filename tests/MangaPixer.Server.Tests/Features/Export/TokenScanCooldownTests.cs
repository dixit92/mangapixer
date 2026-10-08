namespace com.lifepixer.mangapixer.Tests.Server.Features.Export;

using com.lifepixer.mangapixer.Server.Features.Export;
using com.lifepixer.mangapixer.Server.Features.Tokens;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Scanning;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using Xunit;

/// <summary>
/// Unit tests of <see cref="TokenScanCooldown"/> (1.36.0) on a manual clock: the window per library, a refused start (409) not
/// counting, zero turning it off, and two concurrent requests for one library starting at most one scan.
/// </summary>
public sealed class TokenScanCooldownTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static Func<CancellationToken, Task<LibraryScanLaunch?>> Starts(Func<int> onCall, bool running = false) => _ =>
    {
        onCall();
        return Task.FromResult(running ? null : new LibraryScanLaunch(new ScanRunEntity(), Task.CompletedTask));
    };

    [Fact]
    public async Task TheWindow_IsPerLibrary_AndCountsFromTheStart()
    {
        var clock = new ManualTime(Start);
        var cooldown = new TokenScanCooldown(new ApiTokenOptions(), clock);
        var calls = 0;

        Assert.Equal(TimeSpan.FromMinutes(5), cooldown.Window);
        Assert.Equal(TokenScanStart.Started, (await cooldown.TryStartAsync(1, Starts(() => calls++), default)).Outcome);

        clock.Advance(TimeSpan.FromSeconds(90));
        var cooling = await cooldown.TryStartAsync(1, Starts(() => calls++), default);
        Assert.Equal(TokenScanStart.CoolingDown, cooling.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(210), cooling.RetryAfter);
        Assert.Null(cooling.Launch);
        Assert.Equal(1, calls); // a cooling-down request never reaches the launcher

        Assert.Equal(TokenScanStart.Started, (await cooldown.TryStartAsync(2, Starts(() => calls++), default)).Outcome);

        clock.Advance(TimeSpan.FromSeconds(210));
        Assert.Equal(TokenScanStart.Started, (await cooldown.TryStartAsync(1, Starts(() => calls++), default)).Outcome);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ARunningScan_IsInProgress_AndDoesNotStartTheWindow()
    {
        var cooldown = new TokenScanCooldown(new ApiTokenOptions(), new ManualTime(Start));
        var calls = 0;

        Assert.Equal(TokenScanStart.InProgress, (await cooldown.TryStartAsync(1, Starts(() => calls++, running: true), default)).Outcome);
        Assert.Equal(TokenScanStart.Started, (await cooldown.TryStartAsync(1, Starts(() => calls++), default)).Outcome);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task ZeroOrLess_TurnsTheCooldownOff(int minutes)
    {
        var cooldown = new TokenScanCooldown(new ApiTokenOptions { ScanCooldownMinutes = minutes }, new ManualTime(Start));
        Assert.Equal(TimeSpan.Zero, cooldown.Window);
        for (var i = 0; i < 3; i++)
            Assert.Equal(TokenScanStart.Started, (await cooldown.TryStartAsync(1, Starts(() => 0), default)).Outcome);
    }

    [Fact]
    public async Task ConcurrentRequestsForOneLibrary_StartAtMostOneScan()
    {
        var cooldown = new TokenScanCooldown(new ApiTokenOptions(), new ManualTime(Start));
        var release = new TaskCompletionSource();
        var calls = 0;

        async Task<LibraryScanLaunch?> SlowStart(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return new LibraryScanLaunch(new ScanRunEntity(), Task.CompletedTask);
        }

        var first = cooldown.TryStartAsync(7, SlowStart, default);
        var second = cooldown.TryStartAsync(7, SlowStart, default);
        release.SetResult();
        var outcomes = (await Task.WhenAll(first, second)).Select(r => r.Outcome).Order().ToList();

        Assert.Equal([TokenScanStart.Started, TokenScanStart.CoolingDown], outcomes);
        Assert.Equal(1, calls);
    }
}
