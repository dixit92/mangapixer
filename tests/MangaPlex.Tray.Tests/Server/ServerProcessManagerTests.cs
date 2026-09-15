namespace com.lifepixer.mangaplex.Tests.Tray.Server;

using com.lifepixer.mangaplex.Tray.Server;
using Xunit;

/// <summary>
/// Process-level tests: exercises real child-process lifecycle (start,
/// running detection, graceful-stop-then-kill-fallback, restart) against a
/// controllable stand-in process rather than the full MangaPlex.Server.exe,
/// which needs a self-contained publish to run standalone. The health-poll
/// piece is covered separately (unit-level) in
/// <see cref="HttpServerHealthCheckerTests"/>; a true end-to-end pass against
/// the published server is a scripted manual verification step (see the
/// lane's vault note).
/// </summary>
public sealed class ServerProcessManagerTests
{
    // Ships in-box on every Windows host; runs indefinitely without needing
    // stdin (unlike e.g. `cmd /c pause` or `timeout`, which error out when
    // redirected stdin isn't a real console).
    private static readonly string LongRunningExecutable = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    // The default graceful-shutdown requester relaunches the current
    // executable as a helper (see CtrlBreakSender) — under the test host
    // that would just spawn a confused copy of the test runner. Tests exist
    // to prove StartAsync/StopAsync/RestartAsync lifecycle and the
    // timeout+Kill() fallback, not the CTRL_BREAK delivery mechanism itself
    // (covered by the lane's manual E2E pass against the real server exe),
    // so they inject a no-op requester.
    private static ServerProcessManager CreateManager(string? arguments = null) =>
        new(LongRunningExecutable, new ServerEndpointOptions { Port = 0 },
            serverArguments: arguments,
            gracefulShutdownRequester: static _ => { });

    [Fact]
    public async Task StartAsync_LaunchesProcess_ThenIsRunningIsTrue()
    {
        await using var manager = CreateManager("-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"");

        await manager.StartAsync();

        Assert.True(manager.IsRunning);
    }

    [Fact]
    public async Task StartAsync_WhenExecutableMissing_ThrowsFileNotFoundException()
    {
        await using var manager = new ServerProcessManager(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe"),
            new ServerEndpointOptions());

        await Assert.ThrowsAsync<FileNotFoundException>(() => manager.StartAsync());
    }

    [Fact]
    public async Task StopAsync_StopsARunningProcess_WithinTheGracefulTimeout()
    {
        await using var manager = CreateManager("-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"");
        await manager.StartAsync();
        Assert.True(manager.IsRunning);

        await manager.StopAsync(TimeSpan.FromSeconds(5));

        Assert.False(manager.IsRunning);
    }

    [Fact]
    public async Task StopAsync_WhenNeverStarted_CompletesWithoutThrowing()
    {
        await using var manager = CreateManager();

        await manager.StopAsync(TimeSpan.FromSeconds(1));

        Assert.False(manager.IsRunning);
    }

    [Fact]
    public async Task RestartAsync_StopsOldProcess_AndStartsANewOne()
    {
        await using var manager = CreateManager("-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"");
        await manager.StartAsync();
        Assert.True(manager.IsRunning);

        await manager.RestartAsync(TimeSpan.FromSeconds(5));

        Assert.True(manager.IsRunning);
    }

    [Fact]
    public async Task WhenProcessExitsOnItsOwn_IsRunningBecomesFalse()
    {
        await using var manager = CreateManager("-NoProfile -NonInteractive -Command \"exit 0\"");

        await manager.StartAsync();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (manager.IsRunning && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        Assert.False(manager.IsRunning);
    }
}
