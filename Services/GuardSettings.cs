using System.Text.Json;
using PaxChatterGuard.Core;

namespace PaxChatterGuard.Services;

/// <summary>One keyboard's own settings.</summary>
public sealed class DeviceProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>False leaves this keyboard completely alone.</summary>
    public bool Enabled { get; set; } = true;

    public int GlobalMs { get; set; } = 30;
    public Dictionary<int, int> Rules { get; set; } = new();
}

/// <summary>The few things the user can change, saved as JSON in the per-user app-data folder.</summary>
public sealed class GuardSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();

    public bool Enabled { get; set; } = true;

    /// <summary>Milliseconds: a press of a key this soon after letting go of it counts as chatter.</summary>
    public int GlobalMs { get; set; } = 30;

    public bool FilterModifiers { get; set; }

    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>Learn each key's delay automatically from chatter that gets through.</summary>
    public bool AutoAdjust { get; set; }

    /// <summary>The most auto-adjust may ever set, in ms.</summary>
    public int AutoMaxMs { get; set; } = 45;

    /// <summary>Keyboards with their own settings (Windows). Any keyboard not listed follows the settings above.</summary>
    public List<DeviceProfile> Devices { get; set; } = new();

    /// <summary>Settings layout version, so defaults can be revised for existing files.</summary>
    public int Version { get; set; } = 2;

    /// <summary>Linux: names of the keyboards to filter; empty means all of them.</summary>
    public List<string> LinuxDevices { get; set; } = new();

    /// <summary>Per-key exceptions: key → milliseconds (0 means never filter that key).</summary>
    public Dictionary<int, int> Rules { get; set; } = new();

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaxChatterGuard");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static GuardSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<GuardSettings>(File.ReadAllText(FilePath)) ?? new();
                // Version 1 defaulted to 35 ms, which swallowed quick taps in games; the new default is 20 ms.
                if (loaded.Version < 2)
                {
                    if (loaded.GlobalMs == 35)
                        loaded.GlobalMs = 30;
                    loaded.Version = 2;
                }

                return loaded;
            }
        }
        catch
        {
            /* a damaged file just means defaults */
        }

        return new GuardSettings();
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
            }
            catch
            {
                /* best effort */
            }
        }
    }

    /// <summary>The engine's immutable view of these settings.</summary>
    public GuardConfig ToConfig(bool ignoreInjected = true) => new()
    {
        GlobalMs = Math.Clamp(GlobalMs, 0, 250),
        FilterModifiers = FilterModifiers,
        AutoAdjust = AutoAdjust,
        AutoMaxMs = Math.Clamp(AutoMaxMs, 10, 250),
        IgnoreInjected = ignoreInjected,
        HotkeyEnabled = HotkeyEnabled,
        Rules = Rules.ToDictionary(kv => kv.Key, kv => new KeyRule(Math.Clamp(kv.Value, 0, 250))),
        Devices = Devices.Where(d => d.Id.Length > 0).GroupBy(d => d.Id).ToDictionary(g => g.Key, g =>
            new DeviceCfg(g.First().Enabled, Math.Clamp(g.First().GlobalMs, 0, 250),
                g.First().Rules.ToDictionary(kv => kv.Key, kv => Math.Clamp(kv.Value, 0, 250)))),
    };
}
