using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PaxChatterGuard.Platform;
using PaxChatterGuard.Services;
using PaxChatterGuard.Views;

namespace PaxChatterGuard;

public partial class App : Application
{
    private GuardService _svc = null!;
    private SettingsWindow? _window;
    private MonitorWindow? _monitor;
    private TrayIcon _tray = null!;
    private NativeMenuItem _toggleItem = null!;
    private IClassicDesktopStyleApplicationLifetime _lifetime = null!;
    private WindowIcon? _onIcon, _offIcon;
    private bool _lastActive = true;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            _lifetime = lifetime;
            // The tray keeps the app (and the filter) alive with no window open.
            lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _svc = new GuardService();
            _onIcon = SpadeIcon.CreateWindowIcon(64, active: true);
            _offIcon = SpadeIcon.CreateWindowIcon(64, active: false);
            BuildTray();
            _svc.Changed += () => Dispatcher.UIThread.Post(UpdateTray);
            _svc.Start();

            // Started by hand: show the settings. Started at sign-in (--minimized): stay quietly in the tray,
            // unless something needs the user's attention.
            if (!Program.StartMinimized || _svc.Problem is not null)
                OpenSettings();
            else
                DispatcherTimer.RunOnce(MemoryTrim.Trim, TimeSpan.FromSeconds(3));

            if (Program.SafeTestSeconds > 0)
            {
                // Testing aid: shut everything down on our own so the keyboard hook can never be left behind.
                DispatcherTimer.RunOnce(Quit, TimeSpan.FromSeconds(Program.SafeTestSeconds));
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void BuildTray()
    {
        var open = new NativeMenuItem("Settings…");
        open.Click += (_, _) => OpenSettings();

        var monitor = new NativeMenuItem("Chatter monitor…");
        monitor.Click += (_, _) => OpenMonitor();

        _toggleItem = new NativeMenuItem("Filtering") { ToggleType = MenuItemToggleType.CheckBox, IsChecked = true };
        _toggleItem.Click += (_, _) => _svc.SetEnabled(!_svc.Settings.Enabled);

        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();

        var menu = new NativeMenu { open, monitor, _toggleItem };
        if (OperatingSystem.IsWindows() && !Elevation.IsElevated)
        {
            // Programs that run as administrator can only be filtered by a copy that runs as administrator too.
            var admin = new NativeMenuItem("Restart as administrator");
            admin.Click += async (_, _) => await RestartAsAdminAsync();
            menu.Add(admin);
        }

        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quit);

        _tray = new TrayIcon
        {
            Icon = _onIcon,
            ToolTipText = "PAX Chatter Guard",
            Menu = menu,
        };
        _tray.Clicked += (_, _) => OpenSettings();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    /// <summary>
    /// Windows: starts a copy that runs as administrator (Windows asks first), then quits this one, so programs that run as
    /// administrator get filtered too. The new copy waits for this one to let go of the single-instance lock. Returns null
    /// once it restarted, otherwise why not.
    /// </summary>
    private async Task<string?> RestartAsAdminAsync()
    {
        if (Autostart.LaunchTarget() is not { } target)
            return "Not restarted: can't tell how this app was started.";

        var args = (target.Args + " --restarted").TrimStart();
        var error = await Task.Run(() => OperatingSystem.IsWindows() ? Elevation.RunElevated(target.Exe, args) : "only Windows needs that");
        if (error is not null)
            return $"Not restarted: {error}.";

        Quit();
        return null;
    }

    /// <summary>Opens the settings window, creating it only now; closing it frees it again.</summary>
    private void OpenSettings()
    {
        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        _window = new SettingsWindow(_svc) { OpenMonitor = OpenMonitor, RestartAsAdmin = RestartAsAdminAsync };
        _window.Closed += (_, _) =>
        {
            _window = null;
            TrimSoon();
        };
        _window.Show();
        _window.Activate();
    }

    /// <summary>Opens the chatter monitor, creating it only now; closing it frees it again.</summary>
    private void OpenMonitor()
    {
        if (_monitor is not null)
        {
            _monitor.Activate();
            return;
        }

        _monitor = new MonitorWindow(_svc);
        _monitor.Closed += (_, _) =>
        {
            _monitor = null;
            TrimSoon();
        };
        _monitor.Show();
        _monitor.Activate();
    }

    /// <summary>Hands the memory a closed window used back to the system once no window is open; the tray needs almost none.</summary>
    private void TrimSoon() => DispatcherTimer.RunOnce(() =>
    {
        if (_window is null && _monitor is null)
            MemoryTrim.Trim();
    }, TimeSpan.FromSeconds(1));

    private void UpdateTray()
    {
        _toggleItem.IsChecked = _svc.Settings.Enabled;
        var name = OperatingSystem.IsWindows() && Elevation.IsElevated ? "PAX Chatter Guard (administrator)" : "PAX Chatter Guard";
        _tray.ToolTipText = $"{name} — {_svc.StatusText}";

        // The tray icon dims when nothing is being filtered (also while a program out of reach is in front).
        var active = _svc.IsFiltering;
        if (active != _lastActive)
        {
            _lastActive = active;
            _tray.Icon = active ? _onIcon : _offIcon;
        }
    }

    private void Quit()
    {
        _window?.Close();
        _monitor?.Close();
        _svc.Dispose();
        _tray.IsVisible = false;
        _tray.Dispose();
        _lifetime.Shutdown();
    }
}
