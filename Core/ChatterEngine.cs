namespace PaxChatterGuard.Core;

/// <summary>What to do with a keyboard event.</summary>
public enum Verdict
{
    /// <summary>Let it through untouched.</summary>
    Pass,

    /// <summary>Swallow it: it was chatter.</summary>
    Block,
}

/// <summary>A raw keyboard event as the platform backend saw it. Times are microseconds from any steady clock.</summary>
public readonly record struct KeyEvent(int Key, bool Down, long Micros, bool Injected = false, bool SelfTest = false, string? Device = null);

/// <summary>One keyboard's own settings: its own sensitivity and exceptions, or switched off entirely (Enabled = false).</summary>
public sealed record DeviceCfg(bool Enabled, int GlobalMs, IReadOnlyDictionary<int, int> Rules);

/// <summary>Per-key exception: its own threshold, or "never filter this key" (a threshold of 0).</summary>
public sealed record KeyRule(int ThresholdMs);

/// <summary>An immutable snapshot of the settings the engine works from; swapped in whole, so the hook never sees a half-edit.</summary>
public sealed class GuardConfig
{
    /// <summary>How soon after releasing a key a new press of it counts as chatter, in milliseconds.</summary>
    public int GlobalMs { get; init; } = 30;

    public bool FilterModifiers { get; init; }

    /// <summary>Learn from quick re-presses that slipped through and raise that key's delay just above them.</summary>
    public bool AutoAdjust { get; init; }

    /// <summary>Auto-adjust never raises a delay beyond this, so real fast typing is safe.</summary>
    public int AutoMaxMs { get; init; } = 45;

    /// <summary>Keystrokes other programs inject (macros, remote desktop) are left alone. Only tests turn this off.</summary>
    public bool IgnoreInjected { get; init; } = true;

    public IReadOnlyDictionary<int, KeyRule> Rules { get; init; } = new Dictionary<int, KeyRule>();

    /// <summary>Keyboards with their own settings, by device id. Others follow the global settings.</summary>
    public IReadOnlyDictionary<string, DeviceCfg> Devices { get; init; } = new Dictionary<string, DeviceCfg>();

    /// <summary>Whether Ctrl+Alt+Shift+F9 switches filtering on and off.</summary>
    public bool HotkeyEnabled { get; init; } = true;

    /// <summary>The key (F9) pressed together with Ctrl+Alt+Shift.</summary>
    public const int HotkeyKey = 0x43;

    /// <summary>The threshold for a key in microseconds; 0 means "do not filter this key".</summary>
    public long ThresholdMicros(int key, string? device = null)
    {
        if (device is not null && Devices.TryGetValue(device, out var d))
        {
            if (!d.Enabled)
                return 0;
            if (d.Rules.TryGetValue(key, out var own))
                return own * 1000L;
            if (KeyCatalog.IsModifier(key) && !FilterModifiers)
                return 0;
            return d.GlobalMs * 1000L;
        }

        if (Rules.TryGetValue(key, out var rule))
            return rule.ThresholdMs * 1000L;
        if (KeyCatalog.IsModifier(key) && !FilterModifiers)
            return 0;
        return GlobalMs * 1000L;
    }
}

/// <summary>
/// The decision logic, with no operating-system code in it so it behaves identically everywhere and can be tested alone.
/// <para>
/// A press is chatter when the same key was released less than the threshold ago. The time is measured from the previous
/// <em>release</em> to the new <em>press</em>: a deliberate fast re-press leaves a gap of about 50 ms, while a worn switch
/// bouncing leaves about 10 ms. The release that belongs to a blocked press is blocked too, so the system never sees a
/// release without its press. Auto-repeat while holding a key is never mistaken for chatter.
/// </para>
/// </summary>
public sealed class ChatterEngine
{
    private const long NoTime = long.MinValue;

    private readonly object _lock = new();
    private readonly Dictionary<int, KeyState> _keys = new();
    private GuardConfig _config = new();
    private bool _ctrl, _alt, _shift;

    private sealed class KeyState
    {
        public bool PhysDown;
        public bool OutDown;
        public bool BlockedPress;
        public long LastPhysUp = NoTime;
    }

    private sealed class Stat
    {
        public int Caught;
        public long LongestGapMicros;
        public long ShortestPassedMicros = long.MaxValue;
        public int Near;
    }

    private readonly Dictionary<(string, int), Stat> _stats = new();

    private Stat StatFor(in KeyEvent e)
    {
        var id = (e.Device ?? "", e.Key);
        if (!_stats.TryGetValue(id, out var st))
            _stats[id] = st = new Stat();
        return st;
    }

    /// <summary>What was caught, per key (counts and gap lengths only, kept in memory and never saved).</summary>
    public readonly record struct CaughtKey(int Key, int Count, int LongestGapMs, int ShortestPassedMs);

    /// <summary>Caught and near-miss keys for one keyboard, or for all keyboards together when <paramref name="device"/> is null.</summary>
    public List<CaughtKey> CaughtKeys(string? device = null)
    {
        lock (_lock)
            return _stats.Where(kv => device is null || kv.Key.Item1 == device)
                .Where(kv => kv.Value.Caught > 0 || kv.Value.ShortestPassedMicros != long.MaxValue)
                .GroupBy(kv => kv.Key.Item2)
                .Select(g => new CaughtKey(g.Key, g.Sum(x => x.Value.Caught), (int)(g.Max(x => x.Value.LongestGapMicros) / 1000),
                    g.Min(x => x.Value.ShortestPassedMicros) is var m && m == long.MaxValue ? 0 : (int)(m / 1000)))
                .OrderByDescending(c => c.Count).ToList();
    }

    /// <summary>Master switch (the user's on/off).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Temporary pause (e.g. a fullscreen game is in front); everything passes while set.</summary>
    public bool Suspended { get; set; }

    public bool Active => Enabled && !Suspended;

    public GuardConfig Config
    {
        get => _config;
        set
        {
            _config = value;
            lock (_lock)
                _learned.Clear(); // anything learned has been saved into the settings, which are now in `value`
        }
    }

    private readonly Dictionary<(string, int), long> _learned = new();

    /// <summary>Auto-adjust found that a key (on a keyboard, or null if unknown) needs a longer delay. Raised from the keyboard thread: hand off quickly.</summary>
    public event Action<string?, int, int>? Learned;

    /// <summary>Chatter presses blocked since the app started.</summary>
    public long TotalBlocked { get; private set; }

    /// <summary>Raised when the on/off hotkey was pressed; the argument is the new Enabled state.</summary>
    public event Action<bool>? HotkeyToggled;

    public Verdict OnKey(in KeyEvent e)
    {
        bool? toggledTo;
        Verdict verdict;
        (string? Device, int Key, int Ms)? learned;
        lock (_lock)
            verdict = Decide(e, out toggledTo, out learned);

        if (toggledTo is { } state)
            HotkeyToggled?.Invoke(state);
        if (learned is { } l)
            Learned?.Invoke(l.Device, l.Key, l.Ms);
        return verdict;
    }

    private Verdict Decide(in KeyEvent e, out bool? toggledTo, out (string? Device, int Key, int Ms)? learned)
    {
        toggledTo = null;
        learned = null;
        var cfg = _config;

        // Input another program injected (macros, remote desktop) is none of our business, except the self-test, which
        // is injected on purpose to look like real chatter.
        if (e.Injected && cfg.IgnoreInjected && !e.SelfTest)
            return Verdict.Pass;

        if (!_keys.TryGetValue(e.Key, out var s))
            _keys[e.Key] = s = new KeyState();

        switch (e.Key)
        {
            case 0x1D or 0xE01D: _ctrl = e.Down; break;
            case 0x38 or 0xE038: _alt = e.Down; break;
            case 0x2A or 0x36: _shift = e.Down; break;
        }

        // Ctrl+Alt+Shift+F9 toggles filtering. It is consumed, together with its release.
        if (cfg.HotkeyEnabled && e.Down && e.Key == GuardConfig.HotkeyKey && _ctrl && _alt && _shift && !s.PhysDown)
        {
            Enabled = !Enabled;
            toggledTo = Enabled;
            s.PhysDown = true;
            s.BlockedPress = true;
            return Verdict.Block;
        }

        // A press we already blocked (including the hotkey itself) keeps being blocked until it is released, even if
        // filtering has just been switched off; otherwise a release would arrive without its press.
        if (s.BlockedPress)
        {
            if (!e.Down)
            {
                s.BlockedPress = false;
                s.PhysDown = false;
                s.LastPhysUp = e.Micros;
            }

            return Verdict.Block;
        }

        if (!Active)
        {
            // Not filtering, but keep the key's state truthful so switching on mid-keystroke can't misjudge it.
            s.PhysDown = e.Down;
            s.OutDown = e.Down;
            if (!e.Down)
                s.LastPhysUp = e.Micros;
            return Verdict.Pass;
        }

        var threshold = cfg.ThresholdMicros(e.Key, e.Device);
        if (threshold > 0 && _learned.TryGetValue((e.Device ?? "", e.Key), out var boost) && boost > threshold)
            threshold = boost;

        if (e.Down)
        {
            if (s.OutDown)
            {
                // Down again with no release in between: ordinary auto-repeat from holding the key.
                s.PhysDown = true;
                return Verdict.Pass;
            }

            if (threshold > 0 && s.LastPhysUp != NoTime && e.Micros - s.LastPhysUp < threshold)
            {
                s.PhysDown = true;
                s.BlockedPress = true;
                TotalBlocked++;
                var st = StatFor(e);
                st.Caught++;
                st.LongestGapMicros = Math.Max(st.LongestGapMicros, e.Micros - s.LastPhysUp);
                return Verdict.Block;
            }

            // A quick re-press that was let through: remember how close it came, to help choose a delay.
            if (s.LastPhysUp != NoTime)
            {
                var gap = e.Micros - s.LastPhysUp;
                if (gap < 150_000)
                {
                    var st = StatFor(e);
                    if (gap < st.ShortestPassedMicros)
                        st.ShortestPassedMicros = gap;

                    // Auto-adjust: a re-press this quick is very unlikely to be a person. The second time it is seen
                    // for a key, raise that key's delay to just above it (never beyond the user's limit).
                    if (cfg.AutoAdjust && threshold > 0 && !e.SelfTest && gap < cfg.AutoMaxMs * 1000L && ++st.Near >= 2)
                    {
                        var ms = Math.Min(cfg.AutoMaxMs, (int)(gap / 1000) + 4);
                        if (ms * 1000L > threshold)
                        {
                            _learned[(e.Device ?? "", e.Key)] = ms * 1000L;
                            learned = (e.Device, e.Key, ms);
                        }
                    }
                }
            }

            s.PhysDown = true;
            s.OutDown = true;
            return Verdict.Pass;
        }

        s.PhysDown = false;
        s.OutDown = false;
        s.LastPhysUp = e.Micros;
        return Verdict.Pass;
    }
}
