using PaxChatterGuard.Core;
using PaxChatterGuard.Platform;

namespace PaxChatterGuard.Services;

/// <summary>
/// Holds the filter together: the engine, the operating-system backend and the settings, and the on/off state.
/// </summary>
public sealed class GuardService : IDisposable
{

    public GuardService()
    {
        Settings = GuardSettings.Load();
        Engine = new ChatterEngine { Enabled = Settings.Enabled };
        Backend = BackendFactory.Create();

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
            return Engine.Enabled ? "On" : "Off";
        }
    }

    public void Start()
    {
        ApplyConfig();
        Engine.Enabled = Settings.Enabled;
        StartBackend();
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
        Engine.Config = Settings.ToConfig(ignoreInjected: !acceptInjected);
        Backend.SetDeviceFilter(Settings.LinuxDevices.Count > 0 ? Settings.LinuxDevices : null);
        Changed?.Invoke();
    }

    /// <summary>Raised on the UI thread after auto-adjust changed a delay.</summary>
    public event Action? LearnedChanged;

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
        LearnedChanged?.Invoke();
    }

    public void SetEnabled(bool enabled)
    {
        Engine.Enabled = enabled;
        Settings.Enabled = enabled;
        Settings.Save();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        Backend.Dispose();
    }
}
