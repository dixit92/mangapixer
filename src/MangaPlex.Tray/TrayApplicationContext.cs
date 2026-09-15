using System.Diagnostics;
using com.lifepixer.mangaplex.Tray.Server;
using com.lifepixer.mangaplex.Tray.Settings;
using com.lifepixer.mangaplex.Tray.Startup;

namespace com.lifepixer.mangaplex.Tray;

/// <summary>
/// The WinForms shell: a <see cref="NotifyIcon"/> plus its context menu. Owns
/// no process/settings/registry logic itself — that lives in
/// <see cref="ServerProcessManager"/>, <see cref="TraySettingsStore"/>, and
/// <see cref="RunKeyService"/> so it is testable without a UI.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartupHealthTimeout = TimeSpan.FromSeconds(30);

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _lanItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly System.Windows.Forms.Timer _healthTimer;
    private readonly TraySettingsStore _settingsStore;
    private readonly RunKeyService _runKeyService;
    private readonly ServerProcessManager _serverManager;
    private readonly ServerPortResolver _portResolver;
    private readonly SynchronizationContext _uiContext;
    private TraySettings _settings;

    public TrayApplicationContext()
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _settingsStore = new TraySettingsStore(TraySettingsStore.DefaultDirectory);
        _settings = _settingsStore.Load();
        _runKeyService = new RunKeyService("MangaPlex");
        _portResolver = new ServerPortResolver();

        var serverExecutablePath = ServerExecutableLocator.Resolve(
            Path.GetDirectoryName(Application.ExecutablePath) ?? AppContext.BaseDirectory);
        _serverManager = new ServerProcessManager(
            serverExecutablePath,
            new ServerEndpointOptions { Port = _settings.Port, AllowLanAccess = _settings.AllowLanAccess });
        _serverManager.StateChanged += (_, state) => _uiContext.Post(_ => UpdateStatusText(state), null);

        _statusItem = new ToolStripMenuItem("Status: Stopped") { Enabled = false };
        var openItem = new ToolStripMenuItem("Open MangaPlex", null, (_, _) => OpenInBrowser());
        var startItem = new ToolStripMenuItem("Start Server", null, async (_, _) => await StartServerAsync());
        var stopItem = new ToolStripMenuItem("Stop Server", null, async (_, _) => await _serverManager.StopAsync(GracefulStopTimeout));
        var restartItem = new ToolStripMenuItem("Restart Server", null, async (_, _) => await RestartServerAsync());

        _lanItem = new ToolStripMenuItem("Allow LAN Access") { CheckOnClick = true, Checked = _settings.AllowLanAccess };
        _lanItem.Click += OnLanToggleClicked;

        _startupItem = new ToolStripMenuItem("Start MangaPlex When I Sign In") { CheckOnClick = true, Checked = _runKeyService.IsEnabled() };
        _startupItem.Click += OnStartupToggleClicked;

        var exitItem = new ToolStripMenuItem("Exit", null, async (_, _) => await ExitAsync());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _statusItem,
            new ToolStripSeparator(),
            openItem,
            startItem,
            stopItem,
            restartItem,
            new ToolStripSeparator(),
            _lanItem,
            _startupItem,
            new ToolStripSeparator(),
            exitItem,
        ]);

        _notifyIcon = new NotifyIcon
        {
            // Pulls the icon embedded in this exe's own Win32 resources (set
            // via <ApplicationIcon> in the csproj) rather than the generic
            // SystemIcons.Application placeholder.
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = "MangaPlex",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => OpenInBrowser();

        // Windows hides newly added tray icons in the overflow area by
        // default, so a fresh install can look like nothing happened. A
        // one-time balloon on first run points the user at it.
        if (!_settings.HasShownTrayIntroBalloon)
        {
            _notifyIcon.BalloonTipTitle = "MangaPlex";
            _notifyIcon.BalloonTipText = "MangaPlex is running in the system tray — click to open.";
            _notifyIcon.ShowBalloonTip(10000);
            _settings.HasShownTrayIntroBalloon = true;
            _settingsStore.Save(_settings);
        }

        _healthTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _healthTimer.Tick += async (_, _) => await PollHealthAsync();
        _healthTimer.Start();

        _ = StartServerAsync();
    }

    private async Task StartServerAsync()
    {
        var resolvedPort = _portResolver.ResolveAvailablePort(_settings.Port);
        if (resolvedPort is null)
        {
            MessageBox.Show(
                $"Could not find a free port to start MangaPlex on near {_settings.Port}.",
                "MangaPlex",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            UpdateStatusText(ServerState.Faulted);
            return;
        }

        if (resolvedPort != _settings.Port)
        {
            _settings.Port = resolvedPort.Value;
            _settingsStore.Save(_settings);
        }

        _serverManager.UpdateEndpointOptions(new ServerEndpointOptions { Port = _settings.Port, AllowLanAccess = _settings.AllowLanAccess });
        await _serverManager.StartAsync();
        await _serverManager.WaitForHealthyAsync(StartupHealthTimeout);
    }

    private async Task RestartServerAsync()
    {
        await _serverManager.StopAsync(GracefulStopTimeout);
        await StartServerAsync();
    }

    private void OpenInBrowser()
    {
        var url = _serverManager.BuildOpenUrl();
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void OnLanToggleClicked(object? sender, EventArgs e)
    {
        if (_lanItem.Checked)
        {
            var confirmed = MessageBox.Show(
                "Allowing LAN access exposes MangaPlex to other devices on your local network "
                    + "(e.g. other computers or phones on your Wi-Fi). Only enable this on networks "
                    + "you trust, such as your home network.\n\nThis takes effect the next time the "
                    + "server restarts.",
                "Allow LAN Access",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning) == DialogResult.OK;

            if (!confirmed)
            {
                _lanItem.Checked = false;
                return;
            }
        }

        _settings.AllowLanAccess = _lanItem.Checked;
        _settingsStore.Save(_settings);
    }

    private void OnStartupToggleClicked(object? sender, EventArgs e)
    {
        if (_startupItem.Checked)
            _runKeyService.Enable(Application.ExecutablePath);
        else
            _runKeyService.Disable();
    }

    private async Task PollHealthAsync()
    {
        if (!_serverManager.IsRunning)
        {
            UpdateStatusText(ServerState.Stopped);
            return;
        }

        await _serverManager.WaitForHealthyAsync(TimeSpan.FromSeconds(1));
    }

    private void UpdateStatusText(ServerState state)
    {
        _statusItem.Text = state switch
        {
            ServerState.Running => $"Status: Running (port {_serverManager.Port})",
            ServerState.Starting => "Status: Starting...",
            ServerState.Stopping => "Status: Stopping...",
            ServerState.Faulted => "Status: Faulted",
            _ => "Status: Stopped",
        };
    }

    private async Task ExitAsync()
    {
        _healthTimer.Stop();
        await _serverManager.StopAsync(GracefulStopTimeout);
        _notifyIcon.Visible = false;
        Application.Exit();
    }
}
