namespace pxdChatterGuard.Core;

/// <summary>What to do with a keyboard event.</summary>
public enum Verdict
{
    /// <summary>Let it through untouched.</summary>
    Pass,

    /// <summary>Swallow it: it was chatter.</summary>
    Block,

    /// <summary>
    /// Swallow this release for now (protect-held-keys mode): it is held back for the key's delay, and the backend sends it
    /// later when <see cref="ChatterEngine.TakeDueReleases"/> hands it over, unless the key bounces back down first.
    /// </summary>
    Defer,

    /// <summary>
    /// Swallow this press and send programs a release followed by a press of the key instead: its held-back release was due
    /// but hadn't been sent yet, and programs must see the two in order.
    /// </summary>
    Replay,
}

/// <summary>
/// A raw keyboard (or mouse button) event as the platform backend saw it. Times are microseconds from any steady clock.
/// <paramref name="Replayed"/> marks a keystroke the backend sent itself on the engine's behalf (a held-back release).
/// <paramref name="X"/> and <paramref name="Y"/> are where the pointer was, in screen pixels, for a mouse button.
/// </summary>
public readonly record struct KeyEvent(int Key, bool Down, long Micros, bool Injected = false, bool SelfTest = false, string? Device = null, bool Replayed = false,
    int X = 0, int Y = 0);

/// <summary>One keyboard's own settings: its own sensitivity and exceptions, or switched off entirely (Enabled = false).</summary>
public sealed record DeviceCfg(bool Enabled, int GlobalMs, IReadOnlyDictionary<int, int> Rules);

/// <summary>Per-key exception: its own threshold, or "never filter this key" (a threshold of 0).</summary>
public sealed record KeyRule(int ThresholdMs);

/// <summary>Why a key wasn't filtered, if it wasn't.</summary>
public enum Exempt : byte
{
    /// <summary>The key is filtered.</summary>
    None,

    /// <summary>Filtering is switched off or paused.</summary>
    FilterOff,

    /// <summary>The keyboard it came from is set to be left alone.</summary>
    KeyboardOff,

    /// <summary>Shift, Ctrl, Alt and Win are only filtered when the user asks for it.</summary>
    Modifier,

    /// <summary>The key's own exception is 0: never filter it.</summary>
    KeyRule,

    /// <summary>Filtering is paused while a program on the pause list is in front.</summary>
    Paused,

    /// <summary>A mouse button, and mouse buttons aren't filtered.</summary>
    MouseOff,
}

/// <summary>An immutable snapshot of the settings the engine works from; swapped in whole, so the hook never sees a half-edit.</summary>
public sealed class GuardConfig
{
    /// <summary>How soon after releasing a key a new press of it counts as chatter, in milliseconds.</summary>
    public int GlobalMs { get; init; } = 30;

    public bool FilterModifiers { get; init; }

    /// <summary>Learn from chatter that slipped through and raise that key's delay just above it.</summary>
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

    /// <summary>
    /// Protect held keys: hold every release of a filtered key back for its delay, and if the key bounces back down in that
    /// time, swallow the bounce so programs see the key stay down. Only backends that can send keystrokes support it.
    /// Mouse buttons too, except that a click's release goes out early once the pointer moves (see <see cref="ChatterEngine.OnPointerMove"/>).
    /// </summary>
    public bool HoldSafe { get; init; }

    /// <summary>Filter mouse buttons too. Only backends that can watch the mouse support it; while off, they don't.</summary>
    public bool FilterMouse { get; init; }

    /// <summary>How soon after letting go of a mouse button a new click of it counts as chatter, in milliseconds.</summary>
    public int MouseMs { get; init; } = 50;

    /// <summary>The key (F9) pressed together with Ctrl+Alt+Shift.</summary>
    public const int HotkeyKey = 0x43;

    /// <summary>
    /// Security keys that type codes (YubiKey) send the same letter many times a few milliseconds apart, which looks exactly
    /// like chatter. Keyboards from these vendors are left alone unless the user gives them settings of their own.
    /// </summary>
    public static bool IsTokenDevice(string? device) => device is not null && device.StartsWith("1050:", StringComparison.OrdinalIgnoreCase);

    /// <summary>The threshold for a key in microseconds; 0 means "do not filter this key".</summary>
    public long ThresholdMicros(int key, string? device = null) => Resolve(key, device, out _, out _);

    /// <summary>
    /// The threshold that applies to a key (0 = not filtered), plus the delay to judge it by when it isn't filtered
    /// (<paramref name="reference"/>, what its keyboard's sensitivity would be) and why it isn't (<paramref name="why"/>).
    /// </summary>
    public long Resolve(int key, string? device, out long reference, out Exempt why)
    {
        int baseMs;
        int? ruleMs;
        var keyboardOn = true;
        var mouse = KeyCatalog.IsMouseButton(key);
        if (mouse)
        {
            // Buttons follow the mouse delay and the default exception list; a keyboard's own settings are for its keys.
            baseMs = MouseMs;
            ruleMs = Rules.TryGetValue(key, out var own) ? own.ThresholdMs : null;
        }
        else if (device is not null && Devices.TryGetValue(device, out var d))
        {
            baseMs = d.GlobalMs;
            keyboardOn = d.Enabled;
            ruleMs = d.Rules.TryGetValue(key, out var own) ? own : null;
        }
        else
        {
            baseMs = GlobalMs;
            ruleMs = Rules.TryGetValue(key, out var rule) ? rule.ThresholdMs : null;
            if (IsTokenDevice(device))
                keyboardOn = false;
        }

        var ms = ruleMs ?? baseMs;
        why = Exempt.None;
        if (ms <= 0)
            why = Exempt.KeyRule;
        else if (ruleMs is null && KeyCatalog.IsModifier(key) && !FilterModifiers)
            why = Exempt.Modifier;
        if (!keyboardOn)
            why = Exempt.KeyboardOff;
        if (mouse && !FilterMouse)
            why = Exempt.MouseOff;

        reference = (ms > 0 ? ms : baseMs) * 1000L;
        return why == Exempt.None ? ms * 1000L : 0;
    }
}

/// <summary>What the chatter monitor saw.</summary>
public enum ChatterKind : byte
{
    /// <summary>Dropped by the filter.</summary>
    Blocked,

    /// <summary>Let through because the key isn't filtered (filter off, key or keyboard exempt), although its delay would have dropped it.</summary>
    NotFiltered,

    /// <summary>Let through, but the timing says it was the switch and not a finger: a press or release far too short for a person.</summary>
    Slipped,

    /// <summary>Let through: a quick re-press just past the delay, most likely on purpose.</summary>
    CloseCall,
}

[Flags]
public enum ChatterFlags : byte
{
    None = 0,

    /// <summary>Made by the built-in test.</summary>
    Test = 1,

    /// <summary>A blocked press that turned out to be held down; it was let through when the key started repeating.</summary>
    HeldOn = 2,

    /// <summary>Protect-held-keys mode: the release before it was still held back, so programs saw the key stay down.</summary>
    KeptHeld = 4,
}

/// <summary>
/// One line of the chatter monitor. Times are in microseconds; -1 means not known (yet).
/// <para>
/// <see cref="GapMicros"/> is how long after the key was let go it went down again; <see cref="HeldMicros"/> how long this
/// press lasted; <see cref="PrevHeldMicros"/> how long the press before it lasted. <see cref="DelayMs"/> is the key's delay at
/// the time; <see cref="Bounces"/> counts the extra presses folded into this line (one burst of chatter is one line).
/// </para>
/// </summary>
public readonly record struct ChatterEvent(
    long Seq,
    DateTime TimeUtc,
    int Key,
    string? Device,
    ChatterKind Kind,
    Exempt Why,
    ChatterFlags Flags,
    long GapMicros,
    long HeldMicros,
    long PrevHeldMicros,
    int DelayMs,
    int Bounces);

/// <summary>
/// The decision logic, with no operating-system code in it so it behaves identically everywhere and can be tested alone.
/// <para>
/// A press is chatter when the same key was released less than the threshold ago. The time is measured from the previous
/// <em>release</em> to the new <em>press</em>: a deliberate fast re-press leaves a gap of about 50 ms, while a worn switch
/// bouncing leaves about 10 ms. The release that belongs to a blocked press is blocked too, so the system never sees a
/// release without its press. Auto-repeat while holding a key is never mistaken for chatter, and a blocked press that turns
/// out to be held down (the key starts repeating) is let through from then on, so a held key keeps working.
/// </para>
/// <para>
/// Everything that looks like chatter, blocked or not, goes into a small in-memory log for the chatter monitor. Besides the
/// gap, each press's length tells a finger from a switch: a person can't press and release a key in under
/// <see cref="TinyPressMicros"/>, a bouncing contact does it in one to five milliseconds.
/// </para>
/// <para>
/// Mouse buttons are keys with codes of their own (<see cref="KeyCatalog.MouseLeft"/> on) and their own delay
/// (<see cref="GuardConfig.MouseMs"/>), judged by the same rules. They never auto-repeat, and in protect-held-keys mode
/// the pointer matters too: see <see cref="OnPointerMove"/>.
/// </para>
/// </summary>
public sealed class ChatterEngine
{
    private const long NoTime = long.MinValue;

    /// <summary>Presses (or the press before a quick re-press) shorter than this are a switch, not a finger.</summary>
    public const long TinyPressMicros = 15_000;

    /// <summary>Quick re-presses with a tiny press this far apart are still reported as chatter that got through.</summary>
    public const long SuspectWindowMicros = 100_000;

    /// <summary>Re-presses up to this much past the delay are reported as close calls.</summary>
    public const long CloseCallMicros = 20_000;

    /// <summary>Nobody lets go of a key and presses it again sooner than this: a shorter gap is always the switch.</summary>
    public const long HumanGapMicros = 10_000;

    /// <summary>How many lines the chatter monitor keeps; older ones drop off.</summary>
    public const int LogSize = 300;

    private readonly object _lock = new();
    private readonly Dictionary<int, KeyState> _keys = new();
    private GuardConfig _config = new();
    private bool _ctrl, _alt, _shift;

    private sealed class KeyState
    {
        public bool PhysDown;
        public bool OutDown;
        public bool BlockedPress;
        public bool HotkeyPress;
        public long LastPhysUp = NoTime;
        public long LastPhysDown = NoTime;

        /// <summary>How long the last press that programs saw lasted; -1 when the last press was blocked or isn't known.</summary>
        public long LastSeenHeld = -1;

        /// <summary>The log line the current press belongs to, so its release can fill in how long it lasted (0 = none).</summary>
        public long PressSeq;

        /// <summary>The line the current burst of chatter is folded into (0 = none).</summary>
        public long BurstSeq;

        // A quick re-press that was let through, judged when it is released.
        public bool Pending;
        public long PendGap, PendPrevHeld, PendThreshold, PendReference;
        public Exempt PendWhy;
        public bool PendTest;

        // Protect-held-keys mode: the release is being held back until UpDeadline (programs still see the key down).
        public bool UpPending;
        public long UpDeadline;

        /// <summary>This press was a bounce swallowed while the release was held back; programs never saw it end.</summary>
        public bool BounceHeld;

        // Mouse buttons in protect-held-keys mode: where the press that programs see began, and whether it has been
        // dragged further than the drag distance since (then its release is held back in full, even while moving).
        public int SpotX, SpotY;
        public bool Dragged;

        // A quick re-press (whatever happened to it), judged once its release shows how long it lasted, for the
        // suggested delay: was it the switch or a finger?
        public bool Track;
        public long TrackGap, TrackPrevHeld;
    }

    private sealed class Stat
    {
        public int Caught;
        public int Slipped;
        public long LongestGapMicros;
        public long ShortestPassedMicros = long.MaxValue;
        public int Near;

        /// <summary>The longest gap of a re-press that was certainly the switch (see <see cref="Judge"/>).</summary>
        public long ChatterMaxMicros;

        /// <summary>The shortest gap of a re-press that looked like a finger.</summary>
        public long HumanMinMicros = long.MaxValue;

        /// <summary>The second-shortest such gap: one quick press could be a fluke, two are a habit.</summary>
        public long HumanNextMicros = long.MaxValue;
    }

    /// <summary>
    /// A quick re-press of a key has been released: decide whether it was the switch (the gap is under
    /// <see cref="HumanGapMicros"/>, or it or the press before it was too short for a finger) or a finger, and remember the
    /// extremes per key for <see cref="SuggestDelayMs"/>.
    /// </summary>
    private void Judge(KeyState s, in KeyEvent e, long held)
    {
        if (!s.Track)
            return;
        s.Track = false;
        var st = StatFor(e);
        var chatter = s.TrackGap < HumanGapMicros
                      || (held >= 0 && held < TinyPressMicros)
                      || (s.TrackPrevHeld >= 0 && s.TrackPrevHeld < TinyPressMicros);
        if (chatter)
        {
            st.ChatterMaxMicros = Math.Max(st.ChatterMaxMicros, s.TrackGap);
        }
        else if (s.TrackGap < st.HumanMinMicros)
        {
            st.HumanNextMicros = st.HumanMinMicros;
            st.HumanMinMicros = s.TrackGap;
        }
        else if (s.TrackGap < st.HumanNextMicros)
        {
            st.HumanNextMicros = s.TrackGap;
        }
    }

    /// <summary>
    /// The delay that suits a key best, in ms. It starts from the key's <paramref name="currentMs"/> and moves only when the
    /// data says so: up to clear the longest chatter seen (<paramref name="chatterMaxMicros"/>, with a margin of 20% or at
    /// least 4 ms; one doubled letter is already one too many), down to stay under the re-presses a finger makes
    /// (<paramref name="humanQuickMicros"/>, the second-quickest one, so a single fluke never lowers it; less 3 ms).
    /// When the two overlap, <paramref name="tight"/> is set and it suggests just above the chatter. Pass 0 for "none seen".
    /// </summary>
    public static int SuggestDelayMs(int currentMs, long chatterMaxMicros, long humanQuickMicros, out bool tight)
    {
        tight = false;
        var chatterMs = chatterMaxMicros / 1000.0;
        var lower = chatterMaxMicros > 0 ? chatterMs + Math.Max(4, chatterMs * 0.2) : 0;
        var upper = humanQuickMicros > 0 && humanQuickMicros != long.MaxValue ? humanQuickMicros / 1000.0 - 3 : double.MaxValue;

        double ms;
        if (lower <= upper)
        {
            ms = Math.Clamp(currentMs, lower, upper);
        }
        else
        {
            tight = true;
            ms = chatterMs + 1;
        }

        return Math.Clamp((int)Math.Ceiling(ms - 0.0001), 0, 250);
    }

    private readonly Dictionary<(string, int), Stat> _stats = new();

    private Stat StatFor(in KeyEvent e)
    {
        var id = (e.Device ?? "", e.Key);
        if (!_stats.TryGetValue(id, out var st))
            _stats[id] = st = new Stat();
        return st;
    }

    /// <summary>
    /// What was caught, per key (counts and gap lengths only, kept in memory and never saved). <see cref="ChatterMaxMicros"/>
    /// is the longest gap the switch chattered with; <see cref="HumanMinMicros"/> and <see cref="HumanNextMicros"/> are the
    /// two quickest re-presses a finger made (0 = none seen). <see cref="SuggestDelayMs"/> works from the first and the last.
    /// </summary>
    public readonly record struct CaughtKey(int Key, int Count, int LongestGapMs, int ShortestPassedMs, int Slipped = 0,
        long ChatterMaxMicros = 0, long HumanMinMicros = 0, long HumanNextMicros = 0);

    /// <summary>Caught and near-miss keys for one keyboard, or for all keyboards together when <paramref name="device"/> is null.</summary>
    public List<CaughtKey> CaughtKeys(string? device = null)
    {
        lock (_lock)
            return _stats.Where(kv => device is null || kv.Key.Item1 == device)
                .Where(kv => kv.Value.Caught > 0 || kv.Value.Slipped > 0 || kv.Value.ShortestPassedMicros != long.MaxValue)
                .GroupBy(kv => kv.Key.Item2)
                .Select(g =>
                {
                    // The two quickest finger re-presses across the keyboards in the group.
                    var quick = g.SelectMany(x => new[] { x.Value.HumanMinMicros, x.Value.HumanNextMicros })
                        .Where(v => v != long.MaxValue).Order().Take(2).ToList();
                    return new CaughtKey(g.Key, g.Sum(x => x.Value.Caught), (int)(g.Max(x => x.Value.LongestGapMicros) / 1000),
                        g.Min(x => x.Value.ShortestPassedMicros) is var m && m == long.MaxValue ? 0 : (int)(m / 1000),
                        g.Sum(x => x.Value.Slipped),
                        g.Max(x => x.Value.ChatterMaxMicros),
                        quick.Count > 0 ? quick[0] : 0,
                        quick.Count > 1 ? quick[1] : 0);
                })
                .OrderByDescending(c => c.Count + c.Slipped).ToList();
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
            {
                _learned.Clear(); // anything learned has been saved into the settings, which are now in `value`
                RefreshPointerWatch();
            }
        }
    }

    private readonly Dictionary<(string, int), long> _learned = new();

    /// <summary>Auto-adjust found that a key (on a keyboard, or null if unknown) needs a longer delay. Raised from the keyboard thread: hand off quickly.</summary>
    public event Action<string?, int, int>? Learned;

    /// <summary>Chatter presses blocked since the app started.</summary>
    public long TotalBlocked { get; private set; }

    /// <summary>Chatter that got through since the app started (see <see cref="ChatterKind.Slipped"/>).</summary>
    public long TotalSlipped { get; private set; }

    /// <summary>Raised when the on/off hotkey was pressed; the argument is the new Enabled state.</summary>
    public event Action<bool>? HotkeyToggled;

    public Verdict OnKey(in KeyEvent e)
    {
        bool? toggledTo;
        Verdict verdict;
        (string? Device, int Key, int Ms)? learned;
        lock (_lock)
        {
            verdict = Decide(e, out toggledTo, out learned);
            if (KeyCatalog.IsMouseButton(e.Key))
                RefreshPointerWatch();
        }

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

        if (!_keys.TryGetValue(e.Key, out var s))
        {
            _keys[e.Key] = s = new KeyState();
            if (KeyCatalog.IsMouseButton(e.Key))
                _buttons[e.Key - KeyCatalog.MouseLeft] = s;
        }

        // Keystrokes the backend sent for us (a held-back release, or a release and press re-sent in order) bring programs
        // up to date with the key: let them through and note what programs now see.
        if (e.Replayed)
        {
            s.OutDown = e.Down;
            if (!e.Down && KeyCatalog.IsMouseButton(e.Key))
                _releasing &= ~(1 << (e.Key - KeyCatalog.MouseLeft)); // a release sent early is through: the pointer may move
            return Verdict.Pass;
        }

        // Input another program injected (macros, remote desktop) is none of our business, except the self-test, which
        // is injected on purpose to look like real chatter.
        if (e.Injected && cfg.IgnoreInjected && !e.SelfTest)
            return Verdict.Pass;

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
            s.HotkeyPress = true;
            s.Pending = false;
            s.Track = false;
            s.PressSeq = 0;
            s.LastPhysDown = e.Micros;
            return Verdict.Block;
        }

        // A press we already blocked (including the hotkey itself) keeps being blocked until it is released, even if
        // filtering has just been switched off; otherwise a release would arrive without its press.
        if (s.BlockedPress && e.Down && KeyCatalog.IsMouseButton(e.Key))
        {
            // A mouse button never repeats, so pressed again while its blocked press was still down means that press's
            // release went past us (the mouse hook was being set up again). Take this as a fresh press.
            s.BlockedPress = false;
            s.PressSeq = 0;
            s.Track = false;
        }
        else if (s.BlockedPress)
        {
            if (e.Down)
            {
                if (s.HotkeyPress)
                    return Verdict.Block;

                // Still down and now repeating. A bounce is over within milliseconds and never repeats, so a finger is
                // holding this key: let the hold through from here, as if it had just been pressed. Typing gets the
                // usual repeat; a game gets the held key back.
                s.BlockedPress = false;
                s.OutDown = true;
                if (Find(s.PressSeq) is { } i)
                    _ring[i] = _ring[i] with { Flags = _ring[i].Flags | ChatterFlags.HeldOn };
                _logVersion++;
                return Verdict.Pass;
            }

            s.BlockedPress = false;
            s.HotkeyPress = false;
            s.PhysDown = false;
            Judge(s, e, FinishPress(s, e.Micros));
            s.LastSeenHeld = -1;
            s.LastPhysUp = e.Micros;
            return Verdict.Block;
        }

        var active = Active;
        var onThreshold = cfg.Resolve(e.Key, e.Device, out var reference, out var why);
        if (onThreshold > 0 && _learned.TryGetValue((e.Device ?? "", e.Key), out var boost) && boost > onThreshold)
            onThreshold = boost;
        if (onThreshold > reference)
            reference = onThreshold;
        var threshold = active ? onThreshold : 0;
        if (!active && why == Exempt.None)
            why = Enabled ? Exempt.Paused : Exempt.FilterOff;

        if (e.Down)
        {
            var replay = false;
            if (s.UpPending)
            {
                s.UpPending = false;
                if (e.Micros < s.UpDeadline)
                {
                    // Pressed again while its release was still held back: a bounce. Programs still see the key down,
                    // so swallow this press and carry on as if the key never moved. Its release is held back in turn.
                    var bounceGap = e.Micros - s.LastPhysUp;
                    s.PhysDown = true;
                    s.LastPhysDown = e.Micros;
                    s.Pending = false;
                    s.BounceHeld = true;
                    s.Track = !e.SelfTest;
                    s.TrackGap = bounceGap;
                    s.TrackPrevHeld = s.LastSeenHeld;
                    TotalBlocked++;
                    var st = StatFor(e);
                    st.Caught++;
                    st.LongestGapMicros = Math.Max(st.LongestGapMicros, bounceGap);
                    s.PressSeq = LogBurst(s, e, ChatterKind.Blocked, Exempt.None, bounceGap, s.LastSeenHeld,
                        (int)(Math.Max(onThreshold, s.UpDeadline - s.LastPhysUp) / 1000), ChatterFlags.KeptHeld);
                    return Verdict.Block;
                }

                // Its delay ran out but the held-back release hasn't been sent yet: programs must get that release
                // before this new press, so both are sent together, in order.
                replay = true;
                s.OutDown = false;
            }

            if (s.OutDown)
            {
                // Down again with no release in between: ordinary auto-repeat from holding the key.
                s.PhysDown = true;
                return Verdict.Pass;
            }

            var gap = s.LastPhysUp == NoTime ? long.MaxValue : e.Micros - s.LastPhysUp;
            var prevHeld = s.LastSeenHeld;
            s.Pending = false;
            s.LastPhysDown = e.Micros;
            s.PhysDown = true;
            s.Track = gap < 150_000 && !e.SelfTest;
            s.TrackGap = gap;
            s.TrackPrevHeld = prevHeld;

            if (threshold > 0 && gap < threshold && !replay)
            {
                s.BlockedPress = true;
                TotalBlocked++;
                var st = StatFor(e);
                st.Caught++;
                st.LongestGapMicros = Math.Max(st.LongestGapMicros, gap);
                s.PressSeq = LogBurst(s, e, ChatterKind.Blocked, Exempt.None, gap, prevHeld, (int)(threshold / 1000));
                return Verdict.Block;
            }

            s.OutDown = true;
            s.PressSeq = 0;
            if (KeyCatalog.IsMouseButton(e.Key))
            {
                // Programs see a new press here: the spot a click's release must beat the pointer away from.
                s.SpotX = e.X;
                s.SpotY = e.Y;
                s.Dragged = false;
            }

            if (gap < reference && why != Exempt.None)
            {
                // Not filtered, but this is what the filter would drop: worth showing.
                s.PressSeq = LogBurst(s, e, ChatterKind.NotFiltered, why, gap, prevHeld, (int)(reference / 1000));
            }
            else
            {
                s.BurstSeq = 0;
                if (gap < Math.Max(SuspectWindowMicros, reference + CloseCallMicros))
                {
                    // A quick re-press that was let through: it is judged when it is released, once its length is known.
                    s.Pending = true;
                    s.PendGap = gap;
                    s.PendPrevHeld = prevHeld;
                    s.PendThreshold = threshold;
                    s.PendReference = reference;
                    s.PendWhy = why;
                    s.PendTest = e.SelfTest;
                }
            }

            // A quick re-press that was let through: remember how close it came, to help choose a delay.
            if (gap < 150_000)
            {
                var st = StatFor(e);
                if (gap < st.ShortestPassedMicros)
                    st.ShortestPassedMicros = gap;
            }

            return replay ? Verdict.Replay : Verdict.Pass;
        }

        var held = FinishPress(s, e.Micros);
        Judge(s, e, held);
        if (s.Pending)
        {
            s.Pending = false;
            var bounce = (held >= 0 && held < TinyPressMicros) || (s.PendPrevHeld >= 0 && s.PendPrevHeld < TinyPressMicros);
            ChatterKind? kind = bounce && s.PendGap < SuspectWindowMicros ? ChatterKind.Slipped
                : s.PendGap < s.PendReference + CloseCallMicros ? ChatterKind.CloseCall
                : null;

            if (kind is { } k)
            {
                Append(new ChatterEvent(0, DateTime.UtcNow, e.Key, e.Device, k, s.PendWhy, s.PendTest ? ChatterFlags.Test : ChatterFlags.None,
                    s.PendGap, held, s.PendPrevHeld, (int)(s.PendReference / 1000), 1));

                if (k == ChatterKind.Slipped && !s.PendTest)
                {
                    TotalSlipped++;
                    var st = StatFor(e);
                    st.Slipped++;

                    // Auto-adjust: chatter that slipped past the delay. The second time a key does it, raise that key's
                    // delay to just above the gap (never beyond the user's limit). Only presses that can't be a finger
                    // count, so quick deliberate double taps never teach it anything.
                    if (cfg.AutoAdjust && s.PendThreshold > 0 && s.PendGap < cfg.AutoMaxMs * 1000L && ++st.Near >= 2)
                    {
                        var ms = Math.Min(cfg.AutoMaxMs, (int)(s.PendGap / 1000) + 4);
                        if (ms * 1000L > s.PendThreshold)
                        {
                            _learned[(e.Device ?? "", e.Key)] = ms * 1000L;
                            learned = (e.Device, e.Key, ms);
                        }
                    }
                }
            }
        }

        s.PhysDown = false;
        s.LastPhysUp = e.Micros;
        s.LastSeenHeld = s.BounceHeld ? -1 : held; // a swallowed bounce's length says nothing about the finger
        s.BounceHeld = false;

        // Protect held keys: hold this release back for the key's delay. If the key bounces back down in that time the
        // bounce is swallowed and programs never see the key let go; otherwise the backend sends the release when it's due.
        // Caps, Num and Scroll Lock are left out: they switch on the press, and holding them never matters.
        if (cfg.HoldSafe && threshold > 0 && s.OutDown && !KeyCatalog.IsLockKey(e.Key))
        {
            s.UpPending = true;
            s.UpDeadline = e.Micros + threshold;
            return Verdict.Defer;
        }

        s.OutDown = false;
        return Verdict.Pass;
    }

    // ---- Held-back releases (protect-held-keys mode) -------------------------------------------------------------

    /// <summary>When the next held-back release is due (same clock as the events), or null if none is waiting.</summary>
    public long? NextReleaseDue()
    {
        lock (_lock)
        {
            long? next = null;
            foreach (var s in _keys.Values)
                if (s.UpPending && (next is null || s.UpDeadline < next))
                    next = s.UpDeadline;
            return next;
        }
    }

    /// <summary>
    /// The keys whose held-back release is due by <paramref name="nowMicros"/>, for the backend to send now. From here on
    /// they count as released. With <paramref name="all"/>, every waiting release (used when stopping).
    /// </summary>
    public List<int> TakeDueReleases(long nowMicros, bool all = false)
    {
        lock (_lock)
        {
            var due = new List<int>();
            foreach (var (key, s) in _keys)
            {
                if (!s.UpPending || (!all && s.UpDeadline > nowMicros))
                    continue;
                s.UpPending = false;
                s.OutDown = false;
                due.Add(key);
            }

            if (due.Count > 0)
                RefreshPointerWatch();
            return due;
        }
    }

    // ---- Mouse buttons in protect-held-keys mode: the pointer -----------------------------------------------------

    /// <summary>
    /// How far the pointer may move with a button down before programs take it for a drag, in pixels (the system's drag
    /// rectangle; Windows' default is 4 by 4). The backend sets it from the system settings.
    /// </summary>
    public (int X, int Y) DragDistance { get; set; } = (4, 4);

    /// <summary>The longest pointer movement is held up waiting for a release sent early to come through.</summary>
    public const long ReleaseWaitMicros = 30_000;

    private readonly KeyState?[] _buttons = new KeyState?[KeyCatalog.MouseButtonCount];
    private volatile bool _pointerWatch;
    private int _releasing;           // buttons whose release was sent early and hasn't come back through yet
    private long _releasingUntil;

    /// <summary>
    /// Whether <see cref="OnPointerMove"/> has anything to do right now. Read without a lock, so the mouse hook can let
    /// ordinary movement straight through.
    /// </summary>
    public bool WatchesPointer => _pointerWatch;

    /// <summary>
    /// Whether pointer movement needs looking at: protect-held-keys mode is on and programs see a mouse button down (held,
    /// or with its release held back), or a release sent early is still on its way. Otherwise movement costs nothing.
    /// </summary>
    private void RefreshPointerWatch()
    {
        var watch = _releasing != 0;
        if (_config.HoldSafe)
            foreach (var b in _buttons)
                watch |= b is { OutDown: true };
        _pointerWatch = watch;
    }

    /// <summary>
    /// The pointer moved to (<paramref name="x"/>, <paramref name="y"/>), in screen pixels. It only matters in
    /// protect-held-keys mode while programs see a mouse button down.
    /// <para>
    /// A press that moves further than <see cref="DragDistance"/> is a drag: its release is held back in full, even while
    /// the pointer keeps moving, so a bounce mid-drag can't drop what is being dragged. A click (a press that stayed on its
    /// spot) is different: programs must get its release before the pointer leaves the spot, or they see a short drag
    /// instead of a click. So once the pointer moves more than half the drag distance away, a click's held-back release
    /// is handed over in <paramref name="releaseNow"/> to be sent at once; from here on it counts as released.
    /// </para>
    /// </summary>
    /// <returns>
    /// True to swallow this movement: the releases in <paramref name="releaseNow"/> must reach programs before it, or
    /// releases handed over a moment ago haven't come through yet (for at most <see cref="ReleaseWaitMicros"/>).
    /// </returns>
    public bool OnPointerMove(int x, int y, long micros, List<int> releaseNow)
    {
        if (!_pointerWatch)
            return false;

        lock (_lock)
        {
            if (_releasing != 0)
            {
                if (micros < _releasingUntil)
                    return true;
                _releasing = 0; // they should be back within a millisecond; never hold the pointer up for long
            }

            var (dragX, dragY) = DragDistance;
            for (var i = 0; i < _buttons.Length; i++)
            {
                var s = _buttons[i];
                if (s is null || !s.OutDown)
                    continue;

                var offX = Math.Abs(x - s.SpotX);
                var offY = Math.Abs(y - s.SpotY);
                if (!s.UpPending)
                {
                    if (offX > dragX || offY > dragY)
                        s.Dragged = true; // still held: every program takes this for a drag by now
                }
                else if (!s.Dragged && (offX > dragX / 2 || offY > dragY / 2))
                {
                    s.UpPending = false;
                    s.OutDown = false;
                    releaseNow.Add(KeyCatalog.MouseLeft + i);
                    _releasing |= 1 << i;
                }
            }

            if (releaseNow.Count > 0)
                _releasingUntil = micros + ReleaseWaitMicros;
            RefreshPointerWatch();
            return releaseNow.Count > 0;
        }
    }

    /// <summary>The key went up: how long its press lasted, also noted on the press's log line.</summary>
    private long FinishPress(KeyState s, long micros)
    {
        var held = s.LastPhysDown == NoTime ? -1 : micros - s.LastPhysDown;
        if (s.PressSeq != 0)
        {
            if (Find(s.PressSeq) is { } i && _ring[i].HeldMicros < 0)
            {
                _ring[i] = _ring[i] with { HeldMicros = held };
                _logVersion++;
            }

            s.PressSeq = 0;
        }

        return held;
    }

    // ---- The chatter log -----------------------------------------------------------------------------------------

    private readonly ChatterEvent[] _ring = new ChatterEvent[LogSize];
    private long _seq;
    private long _logVersion;

    /// <summary>Changes whenever the log does, so a window can tell cheaply whether to refresh.</summary>
    public long LogVersion
    {
        get
        {
            lock (_lock)
                return _logVersion;
        }
    }

    /// <summary>The logged chatter, newest first.</summary>
    public List<ChatterEvent> RecentEvents()
    {
        lock (_lock)
        {
            var list = new List<ChatterEvent>();
            for (var seq = _seq; seq > 0 && seq > _seq - LogSize; seq--)
                if (Find(seq) is { } i)
                    list.Add(_ring[i]);
            return list;
        }
    }

    /// <summary>When the log started: when the app started, or when it was last cleared.</summary>
    public DateTime LogSinceUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>Empties the chatter log (the blocked totals stay).</summary>
    public void ClearLog()
    {
        lock (_lock)
        {
            Array.Clear(_ring);
            LogSinceUtc = DateTime.UtcNow;
            _logVersion++;
        }
    }

    private int? Find(long seq)
    {
        if (seq <= 0)
            return null;
        var i = (int)((seq - 1) % LogSize);
        return _ring[i].Seq == seq ? i : null;
    }

    private long Append(ChatterEvent entry)
    {
        var seq = ++_seq;
        _ring[(int)((seq - 1) % LogSize)] = entry with { Seq = seq };
        _logVersion++;
        return seq;
    }

    /// <summary>Logs a press, folding a burst of bounces (one after another, with nothing let through in between) into one line.</summary>
    private long LogBurst(KeyState s, in KeyEvent e, ChatterKind kind, Exempt why, long gap, long prevHeld, int delayMs, ChatterFlags flags = ChatterFlags.None)
    {
        if (Find(s.BurstSeq) is { } i && _ring[i].Kind == kind)
        {
            var old = _ring[i];
            _ring[i] = old with { Bounces = old.Bounces + 1, GapMicros = Math.Max(old.GapMicros, gap), Flags = old.Flags | flags };
            _logVersion++;
            return old.Seq;
        }

        s.BurstSeq = Append(new ChatterEvent(0, DateTime.UtcNow, e.Key, e.Device, kind, why, (e.SelfTest ? ChatterFlags.Test : ChatterFlags.None) | flags,
            gap, -1, prevHeld, delayMs, 1));
        return s.BurstSeq;
    }
}
