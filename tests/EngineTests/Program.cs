using pxdChatterGuard.Core;

// Tests for the filter rules. Each test feeds a made-up stream of key presses and releases (times in milliseconds) into
// a fresh engine and checks what it let through and what it logged. Nothing here touches a real keyboard.

var failures = 0;
var passed = 0;

void Test(string name, Action body)
{
    try
    {
        body();
        passed++;
        Console.WriteLine($"  ok   {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"  FAIL {name}: {ex.Message}");
    }
}

void Eq<T>(T expected, T actual, string what)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{what}: expected {expected}, got {actual}");
}

const int I = 0x17;      // the I key
const int D = 0x20;      // the D key
const int LShift = 0x2A;
const int LCtrl = 0x1D;
const int LAlt = 0x38;

ChatterEngine Engine(GuardConfig? cfg = null) => new() { Config = cfg ?? new GuardConfig { GlobalMs = 30 } };

// Feeds one event; returns "P" (pass), "B" (block), "D" (release held back) or "R" (re-send release + press).
string Ev(ChatterEngine e, int key, bool down, double ms, string? device = null, bool injected = false, bool test = false, bool replayed = false) =>
    e.OnKey(new KeyEvent(key, down, (long)(ms * 1000), injected, test, device, replayed)) switch
    {
        Verdict.Pass => "P",
        Verdict.Block => "B",
        Verdict.Defer => "D",
        _ => "R",
    };

// Feeds a whole sequence written as "d0 u80 d85 u88" (d = press, u = release, number = ms) and returns the verdicts.
string Seq(ChatterEngine e, int key, string steps, string? device = null)
{
    var result = "";
    foreach (var step in steps.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        result += Ev(e, key, step[0] == 'd', double.Parse(step[1..], System.Globalization.CultureInfo.InvariantCulture), device);
    return result;
}

Console.WriteLine("Chatter engine tests");

Test("a bounce right after letting go is blocked, with its release", () =>
{
    var e = Engine();
    Eq("PPBBPP", Seq(e, I, "d0 u80 d85 u88 d300 u380"), "verdicts");
    Eq(1L, e.TotalBlocked, "blocked count");
    var log = e.RecentEvents();
    Eq(1, log.Count, "log lines");
    Eq(ChatterKind.Blocked, log[0].Kind, "kind");
    Eq(5000L, log[0].GapMicros, "gap");
    Eq(3000L, log[0].HeldMicros, "held");
    Eq(80000L, log[0].PrevHeldMicros, "press before it");
    Eq(30, log[0].DelayMs, "delay");
});

Test("a deliberate quick double press passes and isn't logged", () =>
{
    var e = Engine();
    Eq("PPPP", Seq(e, I, "d0 u80 d140 u220"), "verdicts");
    Eq(0, e.RecentEvents().Count, "log lines");
});

Test("a re-press just past the delay is a close call", () =>
{
    var e = Engine();
    Eq("PPPP", Seq(e, I, "d0 u80 d120 u200"), "verdicts");
    var log = e.RecentEvents();
    Eq(1, log.Count, "log lines");
    Eq(ChatterKind.CloseCall, log[0].Kind, "kind");
    Eq(40000L, log[0].GapMicros, "gap");
});

Test("a 3 ms press after a 40 ms gap is chatter that got through", () =>
{
    var e = Engine();
    Eq("PPPP", Seq(e, I, "d0 u80 d120 u123"), "verdicts");
    var log = e.RecentEvents();
    Eq(ChatterKind.Slipped, log[0].Kind, "kind");
    Eq(3000L, log[0].HeldMicros, "held");
    Eq(1L, e.TotalSlipped, "slipped count");
});

Test("a 2 ms first contact then a real press is chatter that got through", () =>
{
    var e = Engine();
    Eq("PPPP", Seq(e, I, "d0 u2 d45 u130"), "verdicts");
    var log = e.RecentEvents();
    Eq(ChatterKind.Slipped, log[0].Kind, "kind");
    Eq(2000L, log[0].PrevHeldMicros, "press before it");
});

Test("a slow re-press with a tiny press is still reported up to 100 ms", () =>
{
    var e = Engine();
    Eq("PPPP", Seq(e, I, "d0 u80 d170 u173"), "verdicts");
    Eq(ChatterKind.Slipped, e.RecentEvents()[0].Kind, "kind");
    var e2 = Engine();
    Seq(e2, I, "d0 u80 d200 u203");
    Eq(0, e2.RecentEvents().Count, "beyond 100 ms");
});

Test("a bounce burst is one line with its extra bounces counted", () =>
{
    var e = Engine();
    Eq("PPBBBBPP", Seq(e, I, "d0 u80 d82 u84 d86 u88 d400 u480"), "verdicts");
    var log = e.RecentEvents();
    Eq(1, log.Count, "log lines");
    Eq(2, log[0].Bounces, "bounces");
    Eq(2L, e.TotalBlocked, "blocked count");
});

Test("a real press after a blocked bounce isn't mistaken for chatter", () =>
{
    // The bounce (3 ms) never reached any program, so its shortness says nothing about the next press.
    var e = Engine();
    Eq("PPBBPP", Seq(e, I, "d0 u80 d82 u85 d145 u230"), "verdicts");
    var log = e.RecentEvents();
    Eq(1, log.Count, "only the bounce is logged");
    Eq(ChatterKind.Blocked, log[0].Kind, "kind");
});

Test("holding a key whose press was blocked lets the hold through when it repeats", () =>
{
    var e = Engine();
    // press, release, quick re-press (blocked) that is held: the first auto-repeat passes, the rest too, and its release.
    Eq("PPB", Seq(e, I, "d0 u80 d85"), "start");
    Eq("P", Ev(e, I, true, 585), "first repeat");
    Eq("P", Ev(e, I, true, 618), "second repeat");
    Eq("P", Ev(e, I, false, 700), "release");
    var log = e.RecentEvents();
    Eq(ChatterFlags.HeldOn, log[0].Flags & ChatterFlags.HeldOn, "flag");
    Eq(615000L, log[0].HeldMicros, "held");
    // and the key works normally afterwards
    Eq("PP", Seq(e, I, "d1000 u1080"), "after");
});

Test("plain auto-repeat is never touched", () =>
{
    var e = Engine();
    Eq("PPPPP", Seq(e, I, "d0 d500 d533 d566 u600"), "verdicts");
    Eq(0, e.RecentEvents().Count, "log lines");
});

Test("the hotkey toggles filtering and is swallowed, repeats and release too", () =>
{
    var e = Engine();
    bool? toggled = null;
    e.HotkeyToggled += on => toggled = on;
    Ev(e, LCtrl, true, 0);
    Ev(e, LAlt, true, 5);
    Ev(e, LShift, true, 10);
    Eq("B", Ev(e, GuardConfig.HotkeyKey, true, 20), "F9 press");
    Eq(false, toggled, "toggled off");
    Eq(false, e.Enabled, "engine off");
    Eq("B", Ev(e, GuardConfig.HotkeyKey, true, 520), "F9 repeat");
    Eq("B", Ev(e, GuardConfig.HotkeyKey, false, 600), "F9 release");
    Ev(e, LShift, false, 700);
    Ev(e, LAlt, false, 705);
    Ev(e, LCtrl, false, 710);
    Eq("PP", Seq(e, GuardConfig.HotkeyKey, "d2000 u2080"), "plain F9 afterwards");
});

Test("with filtering off, would-be chatter passes and is logged", () =>
{
    var e = Engine();
    e.Enabled = false;
    Eq("PPPP", Seq(e, I, "d0 u80 d85 u88"), "verdicts");
    var log = e.RecentEvents();
    Eq(ChatterKind.NotFiltered, log[0].Kind, "kind");
    Eq(Exempt.FilterOff, log[0].Why, "why");
    Eq(3000L, log[0].HeldMicros, "held");
    Eq(0L, e.TotalBlocked, "blocked count");
});

Test("Shift isn't filtered by default but its chatter is logged", () =>
{
    var e = Engine();
    Eq("PPPP", Seq(e, LShift, "d0 u80 d85 u88"), "verdicts");
    Eq(Exempt.Modifier, e.RecentEvents()[0].Why, "why");
    var f = Engine(new GuardConfig { GlobalMs = 30, FilterModifiers = true });
    Eq("PPBB", Seq(f, LShift, "d0 u80 d85 u88"), "with modifiers filtered");
});

Test("a key set to 0 is never filtered", () =>
{
    var e = Engine(new GuardConfig { GlobalMs = 30, Rules = new Dictionary<int, KeyRule> { [I] = new(0) } });
    Eq("PPPP", Seq(e, I, "d0 u80 d85 u88"), "verdicts");
    Eq(Exempt.KeyRule, e.RecentEvents()[0].Why, "why");
    Eq(30, e.RecentEvents()[0].DelayMs, "judged by the default delay");
});

Test("a key's own delay applies", () =>
{
    var e = Engine(new GuardConfig { GlobalMs = 30, Rules = new Dictionary<int, KeyRule> { [D] = new(43) } });
    Eq("PPBB", Seq(e, D, "d0 u80 d120 u123"), "40 ms gap blocked at 43");
    Eq("PPPP", Seq(e, I, "d0 u80 d120 u200"), "other keys keep 30");
});

Test("per-keyboard settings: own delay, and switched off", () =>
{
    var cfg = new GuardConfig
    {
        GlobalMs = 30,
        Devices = new Dictionary<string, DeviceCfg>
        {
            ["046D:C359"] = new(true, 50, new Dictionary<int, int>()),
            ["1234:5678"] = new(false, 30, new Dictionary<int, int>()),
        },
    };
    var e = Engine(cfg);
    Eq("PPBB", Seq(e, I, "d0 u80 d120 u123", "046D:C359"), "own 50 ms");
    Eq("PPPP", Seq(e, D, "d0 u80 d85 u88", "1234:5678"), "keyboard off");
    Eq(Exempt.KeyboardOff, e.RecentEvents()[0].Why, "why");
    Eq("PPBB", Seq(e, I, "d1000 u1080 d1085 u1088", "9999:0000"), "unknown keyboard follows default");
});

Test("auto-adjust learns only from chatter a finger can't make", () =>
{
    var e = Engine(new GuardConfig { GlobalMs = 30, AutoAdjust = true, AutoMaxMs = 45 });
    var learned = new List<(int Key, int Ms)>();
    e.Learned += (_, key, ms) => learned.Add((key, ms));
    // Quick but normal double presses (held 80 ms) never count.
    Seq(e, I, "d0 u80 d118 u198 d400 u480 d518 u598");
    Eq(0, learned.Count, "normal presses");
    // Two 3 ms presses 38 ms after letting go: learned 42 ms.
    Seq(e, D, "d0 u80 d118 u121 d400 u480 d518 u521");
    Eq(1, learned.Count, "learned once");
    Eq((D, 42), learned[0], "learned value");
    Eq("PPBB", Seq(e, D, "d1000 u1080 d1118 u1121"), "now blocked");
});

Test("keystrokes from other programs pass and aren't logged", () =>
{
    var e = Engine();
    Eq("P", Ev(e, I, true, 0, injected: true), "1");
    Eq("P", Ev(e, I, false, 80, injected: true), "2");
    Eq("P", Ev(e, I, true, 85, injected: true), "3");
    Eq("P", Ev(e, I, false, 88, injected: true), "4");
    Eq(0, e.RecentEvents().Count, "log lines");
});

Test("the built-in test is blocked and marked as a test", () =>
{
    var e = Engine();
    Eq("P", Ev(e, I, true, 0, injected: true, test: true), "1");
    Eq("P", Ev(e, I, false, 30, injected: true, test: true), "2");
    Eq("B", Ev(e, I, true, 38, injected: true, test: true), "3");
    Eq("B", Ev(e, I, false, 63, injected: true, test: true), "4");
    Eq(ChatterFlags.Test, e.RecentEvents()[0].Flags & ChatterFlags.Test, "flag");
});

Test("switching filtering off mid-press still blocks that press's release", () =>
{
    var e = Engine();
    Eq("PPB", Seq(e, I, "d0 u80 d85"), "start");
    e.Enabled = false;
    Eq("B", Ev(e, I, false, 88), "release of the blocked press");
});

Test("the log keeps the newest lines, newest first, and can be cleared", () =>
{
    var e = Engine();
    for (var n = 0; n < ChatterEngine.LogSize + 20; n++)
        Seq(e, I, $"d{n * 1000} u{n * 1000 + 80} d{n * 1000 + 85} u{n * 1000 + 88}");
    var log = e.RecentEvents();
    Eq(ChatterEngine.LogSize, log.Count, "size");
    Eq(true, log[0].Seq > log[1].Seq, "newest first");
    var v = e.LogVersion;
    e.ClearLog();
    Eq(0, e.RecentEvents().Count, "cleared");
    Eq(true, e.LogVersion != v, "version moved");
    Seq(e, I, "d999000 u999080 d999085 u999088");
    Eq(1, e.RecentEvents().Count, "logs again");
});

Test("the caught-keys summary counts blocked and slipped presses", () =>
{
    var e = Engine();
    Seq(e, I, "d0 u80 d85 u88 d500 u580 d620 u623");
    var caught = e.CaughtKeys();
    Eq(1, caught.Count, "keys");
    Eq(1, caught[0].Count, "blocked");
    Eq(1, caught[0].Slipped, "slipped");
});

// ---- Protect held keys (releases held back for the delay) ----------------------------------------------------------

GuardConfig HoldSafe() => new() { GlobalMs = 30, HoldSafe = true };

Test("protect held keys: a release bounce is swallowed and one release is sent later", () =>
{
    var e = Engine(HoldSafe());
    Eq("PDBD", Seq(e, I, "d0 u80 d82 u85"), "verdicts");
    Eq(115000L, e.NextReleaseDue(), "release due 30 ms after the last let-go");
    Eq(0, e.TakeDueReleases(110000).Count, "not yet");
    var due = e.TakeDueReleases(115000);
    Eq(1, due.Count, "now");
    Eq(I, due[0], "which key");
    Eq(null, e.NextReleaseDue(), "nothing left");
    var log = e.RecentEvents();
    Eq(ChatterFlags.KeptHeld, log[0].Flags & ChatterFlags.KeptHeld, "logged as kept held");
    Eq(2000L, log[0].GapMicros, "gap");
});

Test("protect held keys: a bounce while holding keeps the key down for programs", () =>
{
    var e = Engine(HoldSafe());
    // first contact 2 ms, bounce, then the real press held with auto-repeat: programs see one long press
    Eq("PDBPPD", Seq(e, I, "d0 u2 d4 d500 d533 u800"), "verdicts");
    Eq(830000L, e.NextReleaseDue(), "release due after the real let-go");
});

Test("protect held keys: a second press after the release was sent passes normally", () =>
{
    var e = Engine(HoldSafe());
    Eq("PD", Seq(e, I, "d0 u80"), "first");
    Eq(1, e.TakeDueReleases(110000).Count, "sent");
    Eq("P", Ev(e, I, false, 110.2, replayed: true), "our release comes back through");
    Eq("PD", Seq(e, I, "d140 u220"), "second press");
    Eq(0, e.RecentEvents().Count(x => x.Kind == ChatterKind.Blocked), "nothing blocked");
});

Test("protect held keys: a press after the delay, before the release went out, is re-sent in order", () =>
{
    var e = Engine(HoldSafe());
    Eq("PD", Seq(e, I, "d0 u80"), "first");
    Eq("R", Ev(e, I, true, 115), "press 35 ms later, release not sent yet");
    Eq(null, e.NextReleaseDue(), "the held-back release is no longer waiting");
    Eq("P", Ev(e, I, false, 115.3, replayed: true), "re-sent release");
    Eq("P", Ev(e, I, true, 115.4, replayed: true), "re-sent press");
    Eq("D", Ev(e, I, false, 200), "its own release is held back");
});

Test("protect held keys: Caps Lock, unfiltered keys and a switched-off filter aren't held back", () =>
{
    var e = Engine(HoldSafe());
    Eq("PP", Seq(e, 0x3A, "d0 u80"), "Caps Lock");
    Eq("PP", Seq(e, LShift, "d0 u80"), "Shift");
    e.Enabled = false;
    Eq("PP", Seq(e, I, "d0 u80"), "filter off");
});

Test("protect held keys: stopping sends every waiting release", () =>
{
    var e = Engine(HoldSafe());
    Seq(e, I, "d0 u80");
    Seq(e, D, "d10 u90");
    Eq(2, e.TakeDueReleases(0, all: true).Count, "both");
    Eq(null, e.NextReleaseDue(), "none left");
});

Test("protect held keys: the built-in test types one x", () =>
{
    var e = Engine(HoldSafe());
    Eq("P", Ev(e, I, true, 0, injected: true, test: true), "1");
    Eq("D", Ev(e, I, false, 30, injected: true, test: true), "2");
    Eq("B", Ev(e, I, true, 38, injected: true, test: true), "3");
    Eq("D", Ev(e, I, false, 63, injected: true, test: true), "4");
    Eq(1, e.TakeDueReleases(93000).Count, "one release sent");
});

Test("YubiKey codes are never filtered unless it has its own settings", () =>
{
    const string yubi = "1050:0407";
    var e = Engine();
    Eq("PPPPPP", Seq(e, 0x2E, "d0 u5 d10 u15 d20 u25", yubi), "fast repeated c from the key");
    var f = Engine(new GuardConfig
    {
        GlobalMs = 30,
        Devices = new Dictionary<string, DeviceCfg> { [yubi] = new(true, 30, new Dictionary<int, int>()) },
    });
    Eq("PPBB", Seq(f, 0x2E, "d0 u5 d10 u15", yubi), "with its own settings switched on");
});

Test("paused for a program: chatter passes and is logged as paused", () =>
{
    var e = Engine();
    e.Suspended = true;
    Eq("PPPP", Seq(e, I, "d0 u80 d85 u88"), "verdicts");
    Eq(Exempt.Paused, e.RecentEvents()[0].Why, "why");
});

// ---- Mouse buttons ----------------------------------------------------------------------------------------------------

const int LClick = KeyCatalog.MouseLeft;
const int RClick = KeyCatalog.MouseRight;

GuardConfig Mouse(bool holdSafe = false) => new() { GlobalMs = 30, FilterMouse = true, MouseMs = 50, HoldSafe = holdSafe };

// A button event with the pointer at (x, y); same letters as Ev.
string Btn(ChatterEngine e, int button, bool down, double ms, int x = 100, int y = 100, bool replayed = false) =>
    e.OnKey(new KeyEvent(button, down, (long)(ms * 1000), Replayed: replayed, X: x, Y: y)) switch
    {
        Verdict.Pass => "P",
        Verdict.Block => "B",
        Verdict.Defer => "D",
        _ => "R",
    };

// The pointer moved: "pass", "hold" (held up, nothing to send) or "release " + the buttons to send first.
string Move(ChatterEngine e, int x, int y, double ms)
{
    var now = new List<int>();
    var hold = e.OnPointerMove(x, y, (long)(ms * 1000), now);
    return now.Count > 0 ? "release " + string.Join(", ", now.Select(KeyCatalog.NameOf)) : hold ? "hold" : "pass";
}

Test("mouse: buttons have names and codes no key uses", () =>
{
    Eq("Left click", KeyCatalog.NameOf(LClick), "left");
    Eq("Mouse forward", KeyCatalog.NameOf(KeyCatalog.MouseForward), "X2");
    Eq(5, KeyCatalog.MouseButtons.Count(b => KeyCatalog.IsMouseButton(b.Code)), "all five are buttons");
    Eq(0, KeyCatalog.All.Count(k => KeyCatalog.IsMouseButton(k.Code)), "no key is a button");
    Eq("button", KeyCatalog.NounFor(RClick), "noun");
});

Test("mouse: a click that bounces back within the mouse delay is blocked, with its release", () =>
{
    var e = Engine(Mouse());
    Eq("PPBBPP", Seq(e, LClick, "d0 u80 d85 u88 d300 u380"), "verdicts");
    var log = e.RecentEvents();
    Eq(1, log.Count, "log lines");
    Eq(LClick, log[0].Key, "button");
    Eq(ChatterKind.Blocked, log[0].Kind, "kind");
    Eq(5000L, log[0].GapMicros, "gap");
    Eq(50, log[0].DelayMs, "the mouse delay");
});

Test("mouse: buttons follow the mouse delay, keys the keyboard's", () =>
{
    var e = Engine(Mouse());
    Eq("PPPP", Seq(e, I, "d0 u80 d120 u200"), "40 ms gap on a key passes at 30 ms");
    Eq("PPBB", Seq(e, RClick, "d0 u80 d120 u200"), "40 ms gap on a button is blocked at 50 ms");
    Eq("PPPP", Seq(e, LClick, "d1000 u1080 d1160 u1240"), "a double-click 80 ms apart passes");
    var keyboards = new GuardConfig
    {
        FilterMouse = true,
        MouseMs = 50,
        Devices = new Dictionary<string, DeviceCfg> { ["046D:C359"] = new(true, 20, new Dictionary<int, int> { [LClick] = 10 }) },
    };
    Eq(50_000L, keyboards.ThresholdMicros(LClick, "046D:C359"), "a keyboard's own settings don't apply to buttons");
});

Test("mouse: a button's own exception applies, and 0 never filters it", () =>
{
    var e = Engine(new GuardConfig
    {
        FilterMouse = true,
        MouseMs = 50,
        Rules = new Dictionary<int, KeyRule> { [LClick] = new(20), [RClick] = new(0) },
    });
    Eq("PPPP", Seq(e, LClick, "d0 u80 d110 u190"), "30 ms gap passes at its own 20 ms");
    Eq("PPPP", Seq(e, RClick, "d0 u80 d85 u88"), "never filtered");
    Eq(Exempt.KeyRule, e.RecentEvents()[0].Why, "why");
});

Test("mouse: with mouse buttons off, their chatter passes and is logged as not filtered", () =>
{
    var e = Engine(new GuardConfig { GlobalMs = 30 });
    Eq("PPPP", Seq(e, LClick, "d0 u80 d85 u88"), "verdicts");
    Eq(Exempt.MouseOff, e.RecentEvents()[0].Why, "why");
    Eq(0L, e.TotalBlocked, "nothing blocked");
});

Test("mouse: a blocked press is never let through as if it repeated", () =>
{
    var e = Engine(Mouse());
    Eq("PPB", Seq(e, LClick, "d0 u80 d85"), "start");
    // Buttons don't repeat: a second press with no release in between means the release went past the hook.
    Eq("P", Btn(e, LClick, true, 300), "a fresh press");
    Eq(ChatterFlags.None, e.RecentEvents()[0].Flags & ChatterFlags.HeldOn, "not taken for a held key");
    Eq("P", Btn(e, LClick, false, 380), "its release");
});

Test("mouse: buttons show up in the caught list, but not under a keyboard", () =>
{
    var e = Engine(Mouse());
    Seq(e, LClick, "d0 u80 d85 u88");
    Seq(e, I, "d0 u80 d85 u88", "046D:C359");
    Eq(2, e.CaughtKeys().Count, "all keyboards");
    Eq(1, e.CaughtKeys().Single(k => k.Key == LClick).Count, "left click blocked once");
    Eq(false, e.CaughtKeys("046D:C359").Any(k => k.Key == LClick), "not under that keyboard");
    Eq(5000L, e.CaughtKeys().Single(k => k.Key == LClick).ChatterMaxMicros, "its chatter counts for the suggestion");
});

Test("mouse: injected clicks are left alone", () =>
{
    var e = Engine(Mouse());
    Eq("P", Ev(e, LClick, true, 0, injected: true), "1");
    Eq("P", Ev(e, LClick, false, 80, injected: true), "2");
    Eq("P", Ev(e, LClick, true, 85, injected: true), "3");
    Eq(0, e.RecentEvents().Count, "log lines");
});

// ---- Mouse buttons with protect held keys --------------------------------------------------------------------------

Test("protect held buttons: a bounce while holding still keeps the button down", () =>
{
    var e = Engine(Mouse(holdSafe: true));
    Eq("P", Btn(e, LClick, true, 0), "press");
    Eq("D", Btn(e, LClick, false, 500), "the switch lets go");
    Eq("pass", Move(e, 101, 100, 501), "a 1 px tremor changes nothing");
    Eq("B", Btn(e, LClick, true, 503), "and bounces back: swallowed");
    Eq(ChatterFlags.KeptHeld, e.RecentEvents()[0].Flags & ChatterFlags.KeptHeld, "logged as kept held");
    Eq("D", Btn(e, LClick, false, 2000), "the real release is held back");
    Eq(1, e.TakeDueReleases(2050_000).Count, "and sent 50 ms later");
});

Test("protect held buttons: a click's release goes out first when the pointer leaves the spot", () =>
{
    var e = Engine(Mouse(holdSafe: true));
    Eq("P", Btn(e, LClick, true, 0), "press at 100,100");
    Eq("D", Btn(e, LClick, false, 80), "release held back");
    Eq("pass", Move(e, 102, 99, 82), "within half the drag distance");
    Eq("release Left click", Move(e, 103, 100, 84), "leaving the spot: release first");
    Eq(null, e.NextReleaseDue(), "nothing waits any more");
    Eq("hold", Move(e, 105, 100, 84.5), "movement waits until the release is through");
    Eq("P", Btn(e, LClick, false, 84.7, replayed: true), "our release comes back");
    Eq("pass", Move(e, 107, 100, 85), "then the pointer moves freely");
    Eq(false, e.WatchesPointer, "and isn't even looked at");
});

Test("protect held buttons: a drag's release is held back in full while the pointer moves", () =>
{
    var e = Engine(Mouse(holdSafe: true));
    Eq("P", Btn(e, LClick, true, 0), "press");
    Eq("pass", Move(e, 110, 100, 100), "dragged 10 px");
    Eq("D", Btn(e, LClick, false, 500, 130), "the switch lets go mid-drag");
    Eq("pass", Move(e, 140, 100, 502), "the drag goes on");
    Eq("B", Btn(e, LClick, true, 505, 150), "bounce swallowed: nothing dropped");
    Eq("pass", Move(e, 170, 100, 600), "still dragging");
    Eq("D", Btn(e, LClick, false, 900, 170), "the real drop");
    Eq("pass", Move(e, 200, 100, 910), "still held back while moving");
    Eq(LClick, e.TakeDueReleases(950_000).Single(), "sent when its delay runs out");
});

Test("protect held buttons: a bounce after a click's early release is blocked as chatter", () =>
{
    var e = Engine(Mouse(holdSafe: true));
    Eq("PD", Btn(e, LClick, true, 0) + Btn(e, LClick, false, 80), "click");
    Eq("release Left click", Move(e, 110, 100, 82), "pointer moves off");
    Eq("P", Btn(e, LClick, false, 82.5, replayed: true), "our release");
    Eq("BB", Btn(e, LClick, true, 85, 110) + Btn(e, LClick, false, 88, 110), "the bounce");
    Eq(1L, e.TotalBlocked, "blocked once");
});

Test("protect held buttons: a release that never comes back holds the pointer up 30 ms at most", () =>
{
    var e = Engine(Mouse(holdSafe: true));
    Eq("PD", Btn(e, LClick, true, 0) + Btn(e, LClick, false, 80), "click");
    Eq("release Left click", Move(e, 110, 100, 82), "pointer moves off");
    Eq("hold", Move(e, 112, 100, 100), "waiting");
    Eq("pass", Move(e, 114, 100, 113), "gave up waiting");
});

Test("protect held buttons: a press after the delay, before the release went out, is re-sent in order", () =>
{
    var e = Engine(Mouse(holdSafe: true));
    Eq("PD", Btn(e, LClick, true, 0) + Btn(e, LClick, false, 80), "click");
    Eq("R", Btn(e, LClick, true, 140, 300, 300), "pressed again 60 ms later, elsewhere");
    Eq("PP", Btn(e, LClick, false, 140.2, replayed: true) + Btn(e, LClick, true, 140.3, replayed: true), "re-sent pair");
    Eq("pass", Move(e, 301, 300, 150), "the spot is the new press's");
    Eq("D", Btn(e, LClick, false, 200, 301, 300), "its release is held back");
    Eq("release Left click", Move(e, 300, 304, 202), "and goes out once the pointer leaves");
});

Test("protect held buttons: movement isn't looked at without a button down or with the mode off", () =>
{
    var e = Engine(Mouse(holdSafe: true));
    Eq(false, e.WatchesPointer, "nothing down");
    Eq("pass", Move(e, 500, 500, 0), "moves pass");
    Seq(e, I, "d10 u90");
    Eq(false, e.WatchesPointer, "a held-back key release has no spot");
    Eq(1, e.TakeDueReleases(120_000).Count, "and still goes out on time");
    var off = Engine(Mouse());
    Btn(off, LClick, true, 0);
    Eq(false, off.WatchesPointer, "protect held keys off");
    Eq("P", Btn(off, LClick, false, 80), "release passes at once");
});

Test("protect held buttons: stopping sends a waiting button release", () =>
{
    var e = Engine(Mouse(holdSafe: true));
    Eq("PD", Btn(e, RClick, true, 0) + Btn(e, RClick, false, 80), "click");
    Eq(RClick, e.TakeDueReleases(0, all: true).Single(), "sent");
    Eq(false, e.WatchesPointer, "nothing left to watch");
});

// ---- Suggested delay per key ------------------------------------------------------------------------------------------

int Suggest(int current, double chatterMs, double humanMs) =>
    ChatterEngine.SuggestDelayMs(current, (long)(chatterMs * 1000), (long)(humanMs * 1000), out _);

Test("suggested delay: nothing seen keeps the current delay", () => Eq(30, Suggest(30, 0, 0), "30"));
Test("suggested delay: chatter the delay already covers changes nothing", () => Eq(30, Suggest(30, 21, 0), "21 ms chatter"));
Test("suggested delay: chatter past the delay raises it with a margin", () => Eq(50, Suggest(30, 41, 0), "41 ms chatter + 20%"));
Test("suggested delay: a small chatter gets at least 4 ms of margin", () => Eq(10, Suggest(0, 6, 0), "6 ms chatter on an unfiltered key"));
Test("suggested delay: quick real presses pull it below them", () => Eq(22, Suggest(30, 0, 25), "25 ms re-press"));
Test("suggested delay: both kept apart when there's room", () => Eq(40, Suggest(30, 33, 50), "33 ms chatter, 50 ms presses"));
Test("suggested delay: overlap is flagged and stays just above the chatter", () =>
{
    var ms = ChatterEngine.SuggestDelayMs(30, 30_000, 33_000, out var tight);
    Eq(31, ms, "just above 30 ms chatter");
    Eq(true, tight, "flagged");
});

Test("each quick re-press is judged switch or finger for the suggestion", () =>
{
    var e = Engine();
    Seq(e, I, "d0 u80 d85 u88");          // bounce: 5 ms gap, 3 ms press → switch
    Seq(e, I, "d500 u580 d640 u720");     // 60 ms gap, 80 ms presses → finger
    Seq(e, D, "d0 u80 d121 u125");        // got through: 41 ms gap but a 4 ms press → switch
    Seq(e, 0x39, "d0 u60 d82 u137");      // Space blocked at 22 ms, held 55 ms → a finger
    var keys = e.CaughtKeys().ToDictionary(k => k.Key);
    Eq(5000L, keys[I].ChatterMaxMicros, "I chatter");
    Eq(60000L, keys[I].HumanMinMicros, "I finger");
    Eq(41000L, keys[D].ChatterMaxMicros, "D chatter");
    Eq(0L, keys[D].HumanMinMicros, "D no finger data");
    Eq(0L, keys[0x39].ChatterMaxMicros, "Space no chatter");
    Eq(22000L, keys[0x39].HumanMinMicros, "Space finger");
    Eq(0L, keys[0x39].HumanNextMicros, "Space only once");
    Eq(50, ChatterEngine.SuggestDelayMs(30, keys[D].ChatterMaxMicros, keys[D].HumanNextMicros, out _), "D suggestion");
    Eq(30, ChatterEngine.SuggestDelayMs(30, keys[0x39].ChatterMaxMicros, keys[0x39].HumanNextMicros, out _), "one quick Space never lowers it");
    Eq(30, ChatterEngine.SuggestDelayMs(30, keys[I].ChatterMaxMicros, keys[I].HumanNextMicros, out _), "I suggestion");

    // A second quick real Space press (25 ms): now it's a habit, and the delay goes under the slower of the two.
    Seq(e, 0x39, "d1000 u1060 d1085 u1140");
    var space = e.CaughtKeys().Single(k => k.Key == 0x39);
    Eq(22000L, space.HumanMinMicros, "quickest");
    Eq(25000L, space.HumanNextMicros, "second");
    Eq(22, ChatterEngine.SuggestDelayMs(30, space.ChatterMaxMicros, space.HumanNextMicros, out _), "Space suggestion");
});

Test("the built-in test doesn't count toward suggestions", () =>
{
    var e = Engine();
    Ev(e, I, true, 0, injected: true, test: true);
    Ev(e, I, false, 30, injected: true, test: true);
    Ev(e, I, true, 38, injected: true, test: true);
    Ev(e, I, false, 63, injected: true, test: true);
    var k = e.CaughtKeys().Single();
    Eq(0L, k.ChatterMaxMicros, "no chatter data");
});

// ---- Start when I sign in, as administrator (the Windows task's definition; nothing is registered) ------------------------

Test("the sign-in task starts the app as administrator at this user's sign-in, with no time limit", () =>
{
    const string exe = @"C:\Users\Zoë & Co\Apps\pxd <1>\pxdChatterGuard.exe";
    var xml = pxdChatterGuard.Services.SignInTask.Xml("S-1-5-21-1-2-3-1001", @"PC\Zoë & Co", exe, "--minimized", @"C:\Users\Zoë & Co\Apps\pxd <1>\");
    var doc = System.Xml.Linq.XDocument.Parse(xml); // throws if the escaping is off
    System.Xml.Linq.XNamespace t = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    string At(params string[] path) => path.Aggregate(doc.Root!, (e, name) => e.Element(t + name) ?? throw new Exception($"no {name}")).Value;

    Eq("HighestAvailable", At("Principals", "Principal", "RunLevel"), "run level");
    Eq("InteractiveToken", At("Principals", "Principal", "LogonType"), "logon type");
    Eq("S-1-5-21-1-2-3-1001", At("Principals", "Principal", "UserId"), "runs as");
    Eq(@"PC\Zoë & Co", At("Triggers", "LogonTrigger", "UserId"), "whose sign-in");
    Eq(exe, At("Actions", "Exec", "Command"), "program");
    Eq("--minimized", At("Actions", "Exec", "Arguments"), "arguments");
    Eq("PT0S", At("Settings", "ExecutionTimeLimit"), "no time limit");
    Eq("false", At("Settings", "DisallowStartIfOnBatteries"), "starts on battery");
    Eq("false", At("Settings", "StopIfGoingOnBatteries"), "keeps running on battery");
    Eq("IgnoreNew", At("Settings", "MultipleInstancesPolicy"), "one copy");
    Eq("4", At("Settings", "Priority"), "normal priority");
});

// ---- First start after the rename (in a temp folder; real settings are never touched) -----------------------------------

Test("the settings folder moves from the former name once, and a second start changes nothing", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "pxdcg-migrate-" + Guid.NewGuid().ToString("N"));
    try
    {
        var former = Path.Combine(root, pxdStandard.pxdMigrate.FormerName(pxdChatterGuard.Services.Migration.AppId));
        var current = Path.Combine(root, "pxdChatterGuard");
        Eq(false, former == current, "former name differs");
        Directory.CreateDirectory(former);
        File.WriteAllText(Path.Combine(former, "settings.json"), "{\"GlobalMs\":27}");
        File.WriteAllText(Path.Combine(former, "windows.json"), "{}");

        Eq(true, pxdChatterGuard.Services.Migration.DataFolder(root), "first start moves it");
        Eq(false, Directory.Exists(former), "former folder gone");
        Eq("{\"GlobalMs\":27}", File.ReadAllText(Path.Combine(current, "settings.json")), "settings kept");
        Eq(true, File.Exists(Path.Combine(current, "windows.json")), "window placement kept");

        File.WriteAllText(Path.Combine(current, "settings.json"), "{\"GlobalMs\":31}");
        Eq(false, pxdChatterGuard.Services.Migration.DataFolder(root), "second start: nothing to do");
        Eq("{\"GlobalMs\":31}", File.ReadAllText(Path.Combine(current, "settings.json")), "newer settings untouched");

        // An old folder left next to a new one (say, an old copy ran again) never overwrites the new settings.
        Directory.CreateDirectory(former);
        File.WriteAllText(Path.Combine(former, "settings.json"), "{\"GlobalMs\":99}");
        Eq(false, pxdChatterGuard.Services.Migration.DataFolder(root), "both there: no move");
        Eq("{\"GlobalMs\":31}", File.ReadAllText(Path.Combine(current, "settings.json")), "new settings win");

        // Nothing there at all: nothing made.
        Directory.Delete(root, recursive: true);
        Eq(false, pxdChatterGuard.Services.Migration.DataFolder(root), "fresh install: no move");
        Eq(false, Directory.Exists(current), "no folder made");
    }
    finally
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
});

Test("a Run command's arguments are kept when the startup entry moves", () =>
{
    Eq("--minimized", pxdStandard.pxdMigrate.ArgsOf("\"C:\\Users\\x\\Apps\\Old\\Old.exe\" --minimized"), "quoted");
    Eq("--minimized", pxdStandard.pxdMigrate.ArgsOf("C:\\Apps\\Old.exe --minimized"), "unquoted");
    Eq("", pxdStandard.pxdMigrate.ArgsOf("\"C:\\Apps\\Old.exe\""), "none");
});

// ---- Update before start (a fake release on a local listener; nothing real is updated) --------------------------------

Test("no update check for --self-test or a bin\\ build", () =>
{
    // Pointed at a port nothing listens on, so even a slip could never reach GitHub.
    Environment.SetEnvironmentVariable("PXD_UPDATE_API", "http://127.0.0.1:1");
    try
    {
        Eq(false, pxdStandard.pxdUpdate.BeforeStart("joshpack/pxdChatterGuard", @"^pxdChatterGuard-.*-win-x64\.zip$", ["--self-test"]), "--self-test");
        Eq(true, AppContext.BaseDirectory.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) || !OperatingSystem.IsWindows(), "tests run from bin");
        Eq(false, pxdStandard.pxdUpdate.BeforeStart("joshpack/pxdChatterGuard", @"^pxdChatterGuard-.*-win-x64\.zip$", ["--minimized"]), "bin build");
    }
    finally
    {
        Environment.SetEnvironmentVariable("PXD_UPDATE_API", null);
    }
});

Test("no update while another copy of the same exe runs", () =>
{
    if (!OperatingSystem.IsWindows())
        return;
    Eq(false, pxdStandard.pxdUpdate.OtherCopyRunning(Environment.ProcessPath!), "only this copy: free to update");
    // A second, windowless copy of a system exe stands in for an already running app.
    var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
    using var other = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ping, "-n 6 127.0.0.1")
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
    })!;
    try
    {
        Thread.Sleep(300);
        Eq(true, pxdStandard.pxdUpdate.OtherCopyRunning(ping), "another copy running: files left alone");
        Eq(false, pxdStandard.pxdUpdate.OtherCopyRunning(Path.Combine(Path.GetTempPath(), "pxd-not-running-" + Guid.NewGuid().ToString("N") + ".exe")), "a name nothing runs under");
    }
    finally
    {
        try { other.Kill(); } catch { }
    }
});

Test("a newer release replaces the app's files once its checksum matches", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "pxdcg-update-" + Guid.NewGuid().ToString("N"));
    var app = Path.Combine(root, "app");
    var pkg = Path.Combine(root, "pkg", "pxdChatterGuard");
    Directory.CreateDirectory(app);
    Directory.CreateDirectory(pkg);
    File.WriteAllText(Path.Combine(app, "pxdChatterGuard.exe"), "old exe");
    File.WriteAllText(Path.Combine(app, "pxdChatterGuard.dll"), "old dll");
    File.WriteAllText(Path.Combine(pkg, "pxdChatterGuard.exe"), "new exe");
    File.WriteAllText(Path.Combine(pkg, "pxdChatterGuard.dll"), "new dll");
    File.WriteAllText(Path.Combine(pkg, "extra.dll"), "new file");
    const string asset = "pxdChatterGuard-9.0.0-win-x64.zip";
    var zip = Path.Combine(root, asset);
    System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(root, "pkg"), zip);
    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(zip))).ToLowerInvariant();

    var port = new Random().Next(41000, 49000);
    var prefix = $"http://127.0.0.1:{port}/";
    using var listener = new System.Net.HttpListener();
    listener.Prefixes.Add(prefix);
    listener.Start();
    var badSum = false;
    var serving = Task.Run(async () =>
    {
        while (listener.IsListening)
        {
            System.Net.HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { return; }
            byte[] body = ctx.Request.Url!.AbsolutePath switch
            {
                "/repos/joshpack/pxdChatterGuard/releases/latest" => System.Text.Encoding.UTF8.GetBytes(
                    $"{{\"tag_name\":\"v9.0.0\",\"assets\":[{{\"name\":\"pxdChatterGuard-9.0.0-linux-x64.tar.gz\",\"browser_download_url\":\"{prefix}tar\"}},{{\"name\":\"{asset}\",\"browser_download_url\":\"{prefix}zip\"}},{{\"name\":\"{asset}.sha256\",\"browser_download_url\":\"{prefix}sum\"}}]}}"),
                "/zip" => File.ReadAllBytes(zip),
                "/sum" => System.Text.Encoding.ASCII.GetBytes((badSum ? new string('0', 64) : hash) + "  " + asset),
                _ => [],
            };
            ctx.Response.StatusCode = body.Length == 0 ? 404 : 200;
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        }
    });

    Environment.SetEnvironmentVariable("PXD_UPDATE_API", prefix.TrimEnd('/'));
    try
    {
        const string pattern = @"^pxdChatterGuard-.*-win-x64\.zip$";
        var plan = pxdStandard.pxdUpdate.FindUpdateAsync("joshpack/pxdChatterGuard", pattern, new Version(1, 4, 1)).GetAwaiter().GetResult();
        Eq(prefix + "zip", plan?.Zip, "the Windows zip is picked");
        Eq(prefix + "sum", plan?.Sum, "with its checksum");
        Eq(true, pxdStandard.pxdUpdate.FindUpdateAsync("joshpack/pxdChatterGuard", pattern, new Version(9, 0, 0)).GetAwaiter().GetResult() is null, "current copy: nothing to do");

        var exe = Path.Combine(app, "pxdChatterGuard.exe");
        badSum = true;
        Eq(false, pxdStandard.pxdUpdate.ApplyAsync(plan!.Value.Zip, plan.Value.Sum, app, exe, ["--minimized"], start: false).GetAwaiter().GetResult(), "bad checksum refused");
        Eq("old exe", File.ReadAllText(exe), "nothing changed");
        Eq(false, File.Exists(Path.Combine(app, "extra.dll")), "nothing added");

        badSum = false;
        Eq(true, pxdStandard.pxdUpdate.ApplyAsync(plan.Value.Zip, plan.Value.Sum, app, exe, ["--minimized"], start: false).GetAwaiter().GetResult(), "update applied");
        Eq("new exe", File.ReadAllText(exe), "exe swapped");
        Eq("new dll", File.ReadAllText(Path.Combine(app, "pxdChatterGuard.dll")), "dll swapped");
        Eq(true, File.Exists(Path.Combine(app, "extra.dll")), "new file added");
        Eq(true, File.Exists(exe + ".pxdold"), "old exe set aside");
        pxdStandard.pxdUpdate.CleanUp(app);
        Eq(0, Directory.GetFiles(app, "*.pxdold").Length, "set-aside files removed at the next start");
    }
    finally
    {
        Environment.SetEnvironmentVariable("PXD_UPDATE_API", null);
        listener.Stop();
        serving.Wait(2000);
        try { Directory.Delete(root, recursive: true); } catch { }
    }
});

Console.WriteLine($"{passed} passed, {failures} failed");
return failures == 0 ? 0 : 1;
