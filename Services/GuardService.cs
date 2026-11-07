using PaxChatterGuard.Core;
using PaxChatterGuard.Platform;

namespace PaxChatterGuard.Services;

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

    public bool IsFiltering => Backend.IsRunning && Engine.Active;

    public string StatusText
    {
        get
        {
            if (Problem is not null)
                return "Needs attention";
            if (!Engine.Enabled)
                return "Off";
            return PausedFor is { } program ? $"Paused ({program})" : "On";
        }
    }

    /// <summary>The program on the pause list that is in front right now, or null when filtering isn't paused.</summary>
    public string? PausedFor { get; private set; }

    private ForegroundWatcher? _foreground;

    public void Start()
    {
        ApplyConfig();
        Engine.Enabled = Settings.Enabled;
        StartBackend();
    }

    /// <summary>
    /// Watches the program in front while the pause list has programs on it (and only then), and pauses filtering while
    /// one of them is in front. UI thread.
    /// </summary>
    private void UpdatePauseWatch()
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (Settings.PausedPrograms.Count == 0)
        {
            _foreground?.Dispose();
            _foreground = null;
        }
        else if (_foreground is null)
        {
            try
            {
                _foreground = new ForegroundWatcher();
                _foreground.Changed += UpdatePause;
                _foreground.Start();
            }
            catch
            {
                _foreground = null; // without it nothing pauses; filtering itself is unaffected
            }
        }

        UpdatePause();
    }

    private void UpdatePause()
    {
        var front = OperatingSystem.IsWindows() ? _foreground?.Current : null;
        var paused = front is not null && Settings.PausedPrograms.Any(p => string.Equals(p, front, StringComparison.OrdinalIgnoreCase));
        var pausedFor = paused ? front : null;
        if (pausedFor == PausedFor && Engine.Suspended == paused)
            return;
        PausedFor = pausedFor;
        Engine.Suspended = paused;
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
        var acceptInjected = Environment.GetEnvironmentVariable("PAXCG_ACCEPT_INJECTED") == "1";
        Engine.Config = Settings.ToConfig(ignoreInjected: !acceptInjected, holdSafeSupported: Backend.SupportsHoldSafe);
        Backend.SetDeviceFilter(Settings.LinuxDevices.Count > 0 ? Settings.LinuxDevices : null);
        UpdatePauseWatch();
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
            Settings.Devices.Add(profile = new DeviceProfile { Id = device, Name = name, GlobalMs = Settings.GlobalMs, Rules = new Dictionary<int, int>(Settings.Rules) });
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
    /// settings, otherwise the default list every other keyboard uses.
    /// </summary>
    private Dictionary<int, int> RulesFor(string? device) =>
        (device is null ? null : Settings.Devices.FirstOrDefault(d => d.Id == device))?.Rules ?? Settings.Rules;

    /// <summary>The key's own delay in the list it follows, or null when it has none (UI thread).</summary>
    public int? RuleFor(string? device, int key) => RulesFor(device).TryGetValue(key, out var ms) ? ms : null;

    /// <summary>The delay a key typed on <paramref name="device"/> gets right now, in ms (0 = never filtered).</summary>
    public int DelayFor(string? device, int key) => (int)(Engine.Config.ThresholdMicros(key, device) / 1000);

    /// <summary>Puts a key on the exception list with its own delay (from the chatter monitor). UI thread.</summary>
    public void SetRule(string? device, int key, int ms)
    {
        RulesFor(device)[key] = Math.Clamp(ms, 0, 250);
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
