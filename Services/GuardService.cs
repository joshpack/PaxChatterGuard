using pxdChatterGuard.Core;
using pxdChatterGuard.Platform;

namespace pxdChatterGuard.Services;

/// <summary>
/// Holds the filter together: the engine, the operating-system backend and the settings, and the on/off state.
/// </summary>
public sealed class GuardService : IDisposable
{

    public GuardService()
        : this(BackendFactory.Create())
    {
    }

    /// <summary>With a given backend; tests pass a stand-in so no real keyboard is ever touched.</summary>
    public GuardService(IKeyboardBackend backend)
    {
        Settings = GuardSettings.Load();
        Engine = new ChatterEngine { Enabled = Settings.Enabled };
        Backend = backend;

        Engine.HotkeyToggled += enabled =>
        {
            Settings.Enabled = enabled;
            Settings.Save();
            Changed?.Invoke();
        };
        Backend.Notice += message => Notice?.Invoke(message);
        Engine.Learned += (device, key, ms) => Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyLearned(device, key, ms));
    }

    public GuardSettings Settings { get; }
    public ChatterEngine Engine { get; }
    public IKeyboardBackend Backend { get; }

    /// <summary>Why the backend could not start (permissions etc.), or null when all is well.</summary>
    public string? Problem { get; private set; }

    /// <summary>Status or settings changed. Raised on any thread.</summary>
    public event Action? Changed;

    /// <summary>Something notable happened (e.g. the hook was reinstalled). Raised on any thread.</summary>
    public event Action<string>? Notice;

    /// <summary>Whether keys are being filtered right now: on, not paused, and not typed into a program out of reach.</summary>
    public bool IsFiltering => Backend.IsRunning && Engine.Active && ElevatedFront is null;

    public string StatusText
    {
        get
        {
            if (Problem is not null)
                return "Needs attention";
            if (!Engine.Enabled)
                return "Off";
            if (PausedFor is { } program)
                return $"Paused ({program})";
            return ElevatedFront is { } admin ? $"Not filtering in {admin} (runs as administrator)" : "On";
        }
    }

    /// <summary>The program on the pause list that is in front right now, or null when filtering isn't paused.</summary>
    public string? PausedFor { get; private set; }

    /// <summary>
    /// The program in front when it runs as administrator and this app doesn't (Windows), or null. Windows keeps the keys
    /// typed into it from the keyboard hook, so while it is in front nothing is filtered, and the monitor sees nothing.
    /// </summary>
    public string? ElevatedFront { get; private set; }

    /// <summary>The program run as administrator that was in front most recently since the app started, or null (Windows).</summary>
    public string? LastElevated { get; private set; }

    private string? _hiddenNotice;

    /// <summary>The program to offer "Restart as administrator" for, unless the user put that offer away for it.</summary>
    public string? ElevatedNotice =>
        LastElevated is { } program && !string.Equals(program, _hiddenNotice, StringComparison.OrdinalIgnoreCase) ? program : null;

    /// <summary>Puts the restart offer away until another program that runs as administrator is in front. UI thread.</summary>
    public void HideElevatedNotice()
    {
        _hiddenNotice = LastElevated;
        Changed?.Invoke();
    }

    private ForegroundWatcher? _foreground;

    public void Start()
    {
        ApplyConfig();
        Engine.Enabled = Settings.Enabled;
        StartBackend();
    }

    /// <summary>
    /// Watches the program in front (Windows): to pause filtering while a program on the pause list is in front, and, while
    /// this app doesn't run as administrator, to tell when the program in front does (its keys are out of the hook's
    /// reach). Only runs while one of those applies. UI thread.
    /// </summary>
    private void UpdateForegroundWatch()
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (Settings.PausedPrograms.Count == 0 && Elevation.IsElevated)
        {
            _foreground?.Dispose();
            _foreground = null;
        }
        else if (_foreground is null)
        {
            try
            {
                _foreground = new ForegroundWatcher();
                _foreground.Changed += UpdateForeground;
                _foreground.Start();
            }
            catch
            {
                _foreground = null; // without it nothing pauses; filtering itself is unaffected
            }
        }

        UpdateForeground();
    }

    private void UpdateForeground()
    {
        string? front = null;
        var elevated = false;
        if (OperatingSystem.IsWindows() && _foreground is { } watcher)
        {
            front = watcher.Current;
            elevated = watcher.CurrentElevated;
        }

        var paused = front is not null && Settings.PausedPrograms.Any(p => string.Equals(p, front, StringComparison.OrdinalIgnoreCase));
        var pausedFor = paused ? front : null;
        var admin = elevated ? front ?? "this program" : null;
        if (admin is not null)
            LastElevated = admin;
        if (pausedFor == PausedFor && Engine.Suspended == paused && admin == ElevatedFront)
            return;
        PausedFor = pausedFor;
        Engine.Suspended = paused;
        ElevatedFront = admin;
        Changed?.Invoke();
    }

    public void StartBackend()
    {
        Problem = Backend.Start(Engine);
        Changed?.Invoke();
    }

    /// <summary>Pushes the current settings into the engine and backend. Call after any change.</summary>
    public void ApplyConfig()
    {
        // Only testing sets this: it lets keystrokes created by test tools count as real ones.
        var acceptInjected = Environment.GetEnvironmentVariable("PXDCG_ACCEPT_INJECTED") == "1";
        Engine.Config = Settings.ToConfig(ignoreInjected: !acceptInjected, holdSafeSupported: Backend.SupportsHoldSafe,
            mouseSupported: Backend.SupportsMouse);
        Backend.SetMouseFilter(Engine.Config.FilterMouse); // the mouse is only watched while its filter is on
        Backend.SetDeviceFilter(Settings.LinuxDevices.Count > 0 ? Settings.LinuxDevices : null);
        UpdateForegroundWatch();
        Changed?.Invoke();
    }

    /// <summary>Raised on the UI thread after a key's delay was changed from outside the settings window (auto-adjust, the monitor).</summary>
    public event Action? RulesChanged;

    /// <summary>Saves a delay auto-adjust worked out: in the keyboard's own settings when it has a name, else the defaults.</summary>
    private void ApplyLearned(string? device, int key, int ms)
    {
        if (!Settings.AutoAdjust)
            return;

        var profile = device is null ? null : Settings.Devices.FirstOrDefault(d => d.Id == device);
        if (device is not null && profile is null)
        {
            var name = Backend.Seen.FirstOrDefault(d => d.Path == device)?.Name ?? device;
            Settings.Devices.Add(profile = new DeviceProfile { Id = device, Name = name, GlobalMs = Settings.GlobalMs, Rules = DeviceProfile.KeysOnly(Settings.Rules) });
        }

        var rules = profile?.Rules ?? Settings.Rules;
        if (rules.TryGetValue(key, out var current) && current >= ms)
            return;
        rules[key] = ms;
        Settings.Save();
        ApplyConfig();
        RulesChanged?.Invoke();
    }

    /// <summary>
    /// The exception list a key typed on <paramref name="device"/> follows: that keyboard's own list when it has its own
    /// settings, otherwise the default list every other keyboard uses. Mouse buttons always follow the default list.
    /// </summary>
    private Dictionary<int, int> RulesFor(string? device, int key) =>
        (device is null || KeyCatalog.IsMouseButton(key) ? null : Settings.Devices.FirstOrDefault(d => d.Id == device))?.Rules ?? Settings.Rules;

    /// <summary>The key's own delay in the list it follows, or null when it has none (UI thread).</summary>
    public int? RuleFor(string? device, int key) => RulesFor(device, key).TryGetValue(key, out var ms) ? ms : null;

    /// <summary>The delay a key typed on <paramref name="device"/> gets right now, in ms (0 = never filtered).</summary>
    public int DelayFor(string? device, int key) => (int)(Engine.Config.ThresholdMicros(key, device) / 1000);

    /// <summary>Puts a key on the exception list with its own delay (from the chatter monitor). UI thread.</summary>
    public void SetRule(string? device, int key, int ms)
    {
        RulesFor(device, key)[key] = Math.Clamp(ms, 0, 250);
        Settings.Save();
        ApplyConfig();
        RulesChanged?.Invoke();
    }

    /// <summary>A keyboard's name for showing, from its own settings or from what the system reported.</summary>
    public string? DeviceName(string? device)
    {
        if (device is null)
            return null;
        return Backend.Seen.FirstOrDefault(d => d.Path == device)?.Name
               ?? Settings.Devices.FirstOrDefault(d => d.Id == device)?.Name
               ?? device;
    }

    /// <summary>Whether a keyboard has settings of its own (otherwise it follows the defaults).</summary>
    public bool HasOwnSettings(string? device) => device is not null && Settings.Devices.Any(d => d.Id == device);

    public void SetEnabled(bool enabled)
    {
        Engine.Enabled = enabled;
        Settings.Enabled = enabled;
        Settings.Save();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
            _foreground?.Dispose();
        _foreground = null;
        Backend.Dispose();
    }
}
