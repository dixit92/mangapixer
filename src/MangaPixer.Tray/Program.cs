using com.lifepixer.mangaplex.Tray.Interop;
using com.lifepixer.mangaplex.Tray.Startup;

namespace com.lifepixer.mangaplex.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Disposable-helper mode (see CtrlBreakSender): the tray relaunches
        // its own exe with this argument to send CTRL_BREAK to the server
        // process out-of-process, so the broadcast can never take down the
        // long-lived tray itself. No UI, no single-instance check — this is
        // not "the tray", just a one-shot signal sender.
        if (args.Length == 2
            && args[0] == CtrlBreakSender.RelaunchArgument
            && int.TryParse(args[1], out var targetProcessId))
        {
            CtrlBreakSender.Send(targetProcessId);
            return;
        }

        using var singleInstanceGuard = new SingleInstanceGuard("com.lifepixer.mangaplex.Tray.SingleInstance");
        if (!singleInstanceGuard.IsFirstInstance)
        {
            MessageBox.Show(
                "MangaPlex is already running in the system tray.",
                "MangaPlex",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
