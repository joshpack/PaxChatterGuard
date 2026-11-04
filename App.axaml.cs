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

        _toggleItem = new NativeMenuItem("Filtering") { ToggleType = MenuItemToggleType.CheckBox, IsChecked = true };
        _toggleItem.Click += (_, _) => _svc.SetEnabled(!_svc.Settings.Enabled);

        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();

        _tray = new TrayIcon
        {
            Icon = _onIcon,
            ToolTipText = "PAX Chatter Guard",
            Menu = new NativeMenu { open, _toggleItem, new NativeMenuItemSeparator(), quit },
        };
        _tray.Clicked += (_, _) => OpenSettings();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    /// <summary>Opens the settings window, creating it only now; closing it frees it again.</summary>
    private void OpenSettings()
    {
        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        _window = new SettingsWindow(_svc);
        _window.Closed += (_, _) =>
        {
            _window = null;
            // Hand the memory the window used back to the system; the tray needs almost none.
            DispatcherTimer.RunOnce(MemoryTrim.Trim, TimeSpan.FromSeconds(1));
        };
        _window.Show();
        _window.Activate();
    }

    private void UpdateTray()
    {
        _toggleItem.IsChecked = _svc.Settings.Enabled;
        _tray.ToolTipText = $"PAX Chatter Guard — {_svc.StatusText}";

        // The tray icon dims when nothing is being filtered.
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
        _svc.Dispose();
        _tray.IsVisible = false;
        _tray.Dispose();
        _lifetime.Shutdown();
    }
}
