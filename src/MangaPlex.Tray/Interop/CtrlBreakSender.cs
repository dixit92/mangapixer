using System.ComponentModel;
using System.Diagnostics;

namespace com.lifepixer.mangaplex.Tray.Interop;

/// <summary>
/// Sends CTRL_BREAK to another process's (hidden) console — the signal the
/// ASP.NET Core Generic Host maps to <c>IHostApplicationLifetime.StopApplication</c>.
/// </summary>
public static class CtrlBreakSender
{
    public const string RelaunchArgument = "--send-ctrl-break";

    private static readonly NativeMethods.ConsoleCtrlDelegate SwallowCtrlEvent = _ => true;

    /// <summary>
    /// Attaches to <paramref name="targetProcessId"/>'s console and
    /// broadcasts CTRL_BREAK. MUST only be called from a disposable helper
    /// process (see <see cref="RequestViaHelperProcess"/>) — verified
    /// empirically that the broadcast also reaches whichever process is
    /// currently attached to that console, including the sender itself, and
    /// by default terminates it (a registered handler that swallows the
    /// event was not enough to prevent this in testing). A throwaway helper
    /// dying to its own signal is harmless; the tray dying to it is not.
    /// </summary>
    public static void Send(int targetProcessId)
    {
        try
        {
            NativeMethods.FreeConsole();
            if (!NativeMethods.AttachConsole((uint)targetProcessId))
                return;

            NativeMethods.SetConsoleCtrlHandler(SwallowCtrlEvent, add: true);
            NativeMethods.GenerateConsoleCtrlEvent(NativeMethods.CtrlBreakEvent, 0);
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// Relaunches <paramref name="executablePath"/> (the tray's own exe) as
    /// a short-lived helper with <see cref="RelaunchArgument"/>, which runs
    /// <see cref="Send"/> out-of-process and exits — keeping the long-lived
    /// tray process itself out of the console group that receives the
    /// broadcast.
    /// </summary>
    public static void RequestViaHelperProcess(string? executablePath, int targetProcessId)
    {
        if (string.IsNullOrEmpty(executablePath))
            return;

        try
        {
            using var helper = Process.Start(new ProcessStartInfo(executablePath, $"{RelaunchArgument} {targetProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            helper?.WaitForExit(2000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }
}
