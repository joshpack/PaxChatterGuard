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

    /// <summary>Protect held keys: hold each release back by the key's delay so a bounce can't break a held key (Windows).</summary>
    public bool HoldSafe { get; set; }

    /// <summary>Programs (exe file names) that pause filtering while they are in front (Windows).</summary>
    public List<string> PausedPrograms { get; set; } = new();

    /// <summary>Keyboards with their own settings (Windows). Any keyboard not listed follows the settings above.</summary>
    public List<DeviceProfile> Devices { get; set; } = new();

    /// <summary>Settings layout version, so defaults can be revised for existing files.</summary>
    public int Version { get; set; } = 2;

    /// <summary>Linux: names of the keyboards to filter; empty means all of them.</summary>
    public List<string> LinuxDevices { get; set; } = new();

    /// <summary>Per-key exceptions: key → milliseconds (0 means never filter that key).</summary>
    public Dictionary<int, int> Rules { get; set; } = new();

    /// <summary>Where the settings live. PAXCG_DATA_DIR points it elsewhere (tests use it so they never touch real settings).</summary>
    public static string Folder =>
        Environment.GetEnvironmentVariable("PAXCG_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaxChatterGuard");

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

    /// <summary>The engine's immutable view of these settings. Protect-held-keys only applies where the backend supports it.</summary>
    public GuardConfig ToConfig(bool ignoreInjected = true, bool holdSafeSupported = false) => new()
    {
        GlobalMs = Math.Clamp(GlobalMs, 0, 250),
        FilterModifiers = FilterModifiers,
        AutoAdjust = AutoAdjust,
        AutoMaxMs = Math.Clamp(AutoMaxMs, 10, 250),
        IgnoreInjected = ignoreInjected,
        HotkeyEnabled = HotkeyEnabled,
        HoldSafe = HoldSafe && holdSafeSupported,
        Rules = Rules.ToDictionary(kv => kv.Key, kv => new KeyRule(Math.Clamp(kv.Value, 0, 250))),
        Devices = Devices.Where(d => d.Id.Length > 0).GroupBy(d => d.Id).ToDictionary(g => g.Key, g =>
            new DeviceCfg(g.First().Enabled, Math.Clamp(g.First().GlobalMs, 0, 250),
                g.First().Rules.ToDictionary(kv => kv.Key, kv => Math.Clamp(kv.Value, 0, 250)))),
    };
}
