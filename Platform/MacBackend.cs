using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using pxdChatterGuard.Core;

namespace pxdChatterGuard.Platform;

/// <summary>
/// macOS: a Quartz event tap on key-down/key-up. Returning no event from the tap callback drops the keystroke. Needs the
/// "Accessibility" permission (System Settings → Privacy &amp; Security). Modifier keys arrive as a different kind of event
/// on macOS, so they are never filtered and the global hotkey is not available there (use the tray menu instead).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacBackend : IKeyboardBackend
{
    private const string CoreGraphics = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const uint KeyDown = 10, KeyUp = 11;
    private const uint TapDisabledByTimeout = 0xFFFFFFFE, TapDisabledByUserInput = 0xFFFFFFFF;
    private const uint FieldKeycode = 9, FieldSourcePid = 41, FieldUserData = 42;
    private const long SelfTestMagic = 0x50415832;

    private delegate IntPtr TapCallback(IntPtr proxy, uint type, IntPtr evt, IntPtr userInfo);

    private ChatterEngine? _engine;
    private Thread? _tapThread;
    private volatile bool _running;
    private IntPtr _tap, _runLoop;
    private TapCallback? _callback;
    private string? _startError;
    private readonly ManualResetEventSlim _ready = new(false);

    public string Name => "macOS event tap";
    public bool IsRunning => _running;
    public string? SetupHint =>
        "Open System Settings → Privacy & Security → Accessibility, switch pxd Chatter Guard on, then press Retry.";
    public event Action<string>? Notice;
    public IReadOnlyList<KeyboardDevice> Devices => Array.Empty<KeyboardDevice>();
    public void SetDeviceFilter(IReadOnlyCollection<string>? names) { }

    public string? Start(ChatterEngine engine)
    {
        if (_running)
            return null;

        if (!AXIsProcessTrusted())
            return "macOS hasn't allowed this app to watch the keyboard yet. " + SetupHint;

        _engine = engine;
        _startError = null;
        _ready.Reset();
        _running = true;
        _tapThread = new Thread(TapThreadMain) { IsBackground = true, Name = "Chatter tap" };
        _tapThread.Start();
        if (!_ready.Wait(4000))
            _startError ??= "The keyboard tap did not start in time.";

        if (_startError is not null)
        {
            _running = false;
            return _startError;
        }

        return null;
    }

    public void Stop()
    {
        if (!_running)
            return;
        _running = false;
        if (_runLoop != IntPtr.Zero)
            CFRunLoopStop(_runLoop);
        _tapThread?.Join(1500);
    }

    public void Dispose() => Stop();

    private void TapThreadMain()
    {
        _callback = OnEvent;
        var mask = (1UL << (int)KeyDown) | (1UL << (int)KeyUp);
        _tap = CGEventTapCreate(1 /* session */, 0 /* head of the line */, 0 /* may change/drop events */, mask, _callback, IntPtr.Zero);
        if (_tap == IntPtr.Zero)
        {
            _startError = "macOS refused to create the keyboard tap. " + SetupHint;
            _ready.Set();
            return;
        }

        var source = CFMachPortCreateRunLoopSource(IntPtr.Zero, _tap, 0);
        _runLoop = CFRunLoopGetCurrent();
        CFRunLoopAddSource(_runLoop, source, CommonModes());
        CGEventTapEnable(_tap, true);
        _ready.Set();
        CFRunLoopRun();

        CGEventTapEnable(_tap, false);
        CFRelease(source);
        CFRelease(_tap);
        _tap = IntPtr.Zero;
    }

    private IntPtr OnEvent(IntPtr proxy, uint type, IntPtr evt, IntPtr userInfo)
    {
        try
        {
            if (type is TapDisabledByTimeout or TapDisabledByUserInput)
            {
                // macOS switches a tap off if it answers slowly; switch it straight back on.
                CGEventTapEnable(_tap, true);
                Notice?.Invoke("macOS paused the keyboard tap; it was turned back on.");
                return evt;
            }

            if (type is not (KeyDown or KeyUp))
                return evt;

            var marker = CGEventGetIntegerValueField(evt, FieldUserData);
            var key = ToCanonical((int)CGEventGetIntegerValueField(evt, FieldKeycode));
            if (key == 0)
                return evt;

            var injected = CGEventGetIntegerValueField(evt, FieldSourcePid) != 0;
            var verdict = _engine!.OnKey(new KeyEvent(key, type == KeyDown, NowMicros(), injected, marker == SelfTestMagic));
            return verdict == Verdict.Pass ? evt : IntPtr.Zero;
        }
        catch
        {
            return evt; // when in doubt, let the key through
        }
    }

    public bool SendSelfTest(int key, int gapMs)
    {
        if (!_running)
            return false;
        _ = Task.Run(() =>
        {
            SendKey(key, down: true);
            Thread.Sleep(30);
            SendKey(key, down: false);
            Thread.Sleep(Math.Max(1, gapMs));
            SendKey(key, down: true);
            Thread.Sleep(25);
            SendKey(key, down: false);
        });
        return true;
    }

    private static void SendKey(int canonical, bool down)
    {
        var code = FromCanonical(canonical);
        if (code < 0)
            return;
        var evt = CGEventCreateKeyboardEvent(IntPtr.Zero, (ushort)code, down);
        if (evt == IntPtr.Zero)
            return;
        CGEventSetIntegerValueField(evt, FieldUserData, SelfTestMagic);
        CGEventPost(0 /* HID level */, evt);
        CFRelease(evt);
    }

    private static long NowMicros() => Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;

    // ---- Key code translation (macOS virtual key codes <-> the app's scancode-based ids) ---------------------------------

    private static readonly (int Mac, int Canonical)[] Map =
    {
        (0, 0x1E), (1, 0x1F), (2, 0x20), (3, 0x21), (4, 0x23), (5, 0x22), (6, 0x2C), (7, 0x2D), (8, 0x2E), (9, 0x2F),
        (11, 0x30), (12, 0x10), (13, 0x11), (14, 0x12), (15, 0x13), (16, 0x15), (17, 0x14),
        (18, 0x02), (19, 0x03), (20, 0x04), (21, 0x05), (22, 0x07), (23, 0x06), (24, 0x0D), (25, 0x0A), (26, 0x08),
        (27, 0x0C), (28, 0x09), (29, 0x0B), (30, 0x1B), (31, 0x18), (32, 0x16), (33, 0x1A), (34, 0x17), (35, 0x19),
        (36, 0x1C), (37, 0x26), (38, 0x24), (39, 0x28), (40, 0x25), (41, 0x27), (42, 0x2B), (43, 0x33), (44, 0x35),
        (45, 0x31), (46, 0x32), (47, 0x34), (48, 0x0F), (49, 0x39), (50, 0x29), (51, 0x0E), (53, 0x01),
        (96, 0x3F), (97, 0x40), (98, 0x41), (99, 0x3D), (100, 0x42), (101, 0x43), (103, 0x57), (109, 0x44), (111, 0x58),
        (115, 0xE047), (116, 0xE049), (117, 0xE053), (118, 0x3E), (119, 0xE04F), (120, 0x3C), (121, 0xE051), (122, 0x3B),
        (123, 0xE04B), (124, 0xE04D), (125, 0xE050), (126, 0xE048),
        (65, 0x53), (67, 0x37), (69, 0x4E), (75, 0xE035), (76, 0xE01C), (78, 0x4A),
        (82, 0x52), (83, 0x4F), (84, 0x50), (85, 0x51), (86, 0x4B), (87, 0x4C), (88, 0x4D), (89, 0x47), (91, 0x48), (92, 0x49),
    };

    private static int ToCanonical(int mac)
    {
        foreach (var (m, c) in Map)
            if (m == mac)
                return c;
        return 0;
    }

    private static int FromCanonical(int canonical)
    {
        foreach (var (m, c) in Map)
            if (c == canonical)
                return m;
        return -1;
    }

    // ---- Native interop ------------------------------------------------------------------------------------------

    private static IntPtr CommonModes()
    {
        var lib = NativeLibrary.Load(CoreFoundation);
        return Marshal.ReadIntPtr(NativeLibrary.GetExport(lib, "kCFRunLoopCommonModes"));
    }

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGEventTapCreate(uint tap, uint place, uint options, ulong eventsOfInterest, TapCallback callback, IntPtr userInfo);

    [DllImport(CoreGraphics)]
    private static extern void CGEventTapEnable(IntPtr tap, [MarshalAs(UnmanagedType.I1)] bool enable);

    [DllImport(CoreGraphics)]
    private static extern long CGEventGetIntegerValueField(IntPtr evt, uint field);

    [DllImport(CoreGraphics)]
    private static extern void CGEventSetIntegerValueField(IntPtr evt, uint field, long value);

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, [MarshalAs(UnmanagedType.I1)] bool keyDown);

    [DllImport(CoreGraphics)]
    private static extern void CGEventPost(uint tapLocation, IntPtr evt);

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AXIsProcessTrusted();

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFMachPortCreateRunLoopSource(IntPtr allocator, IntPtr port, long order);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFRunLoopGetCurrent();

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopRun();

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopStop(IntPtr runLoop);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);
}
