using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PaxChatterGuard.Core;

namespace PaxChatterGuard.Platform;

/// <summary>
/// Windows: a low-level keyboard hook (WH_KEYBOARD_LL) on its own thread with its own message loop. The hook callback does
/// almost nothing but ask the engine, because Windows silently drops a hook that answers too slowly. A watchdog checks the
/// hook is still alive and reinstalls it if it isn't.
/// <para>
/// With mouse buttons switched on, a low-level mouse hook (WH_MOUSE_LL) runs on the same thread. Only button events go to
/// the engine; movement and the wheel go straight on. With the setting off, no mouse hook is installed at all.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsBackend : IKeyboardBackend
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int HC_ACTION = 0;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_REINSTALL = 0x8001;
    private const uint WM_MOUSEHOOK = 0x8002;   // match the mouse hook to the setting; wParam 1 = replace it as well
    private const uint LLKHF_EXTENDED = 0x01;
    private const uint LLKHF_INJECTED = 0x10;
    private const uint LLMHF_INJECTED = 0x01;
    private const int SM_SWAPBUTTON = 23, SM_CXDRAG = 68, SM_CYDRAG = 69;

    // Mouse messages that a pen or touch screen made carry this in dwExtraInfo (MI_WP_SIGNATURE under SIGNATURE_MASK).
    private const long PenTouchMask = 0xFFFFFF00, PenTouchSignature = 0xFF515700;

    // Markers we put in dwExtraInfo of the keystrokes we create ourselves, so the hook knows them when they come back.
    private const long SelfTestMagic = 0x50415832;  // simulated chatter
    private const long ProbeMagic = 0x50415833;     // watchdog heartbeat
    private const long ReleaseMagic = 0x50415834;   // a held-back release (protect held keys), or a re-sent release + press

    // Protect held keys: a thread that sends held-back releases when they are due, on a high-resolution timer (a tray app
    // has no visible window, so Windows 11 may ignore timeBeginPeriod for it).
    private Thread? _flushThread;
    private readonly AutoResetEvent _flushWake = new(false);
    private readonly ConcurrentQueue<int> _replays = new();

    // The virtual-key code each key last went down with, so a release we send matches the press exactly (by scan code
    // alone, Shift+numpad and similar keys can come out as a different key and stay stuck down).
    private readonly ushort[] _vk = new ushort[0x200];

    // Mouse buttons, while their filter is on: the mouse hook, on the hook thread. A click's held-back release that must
    // go out before the pointer moves on is handed to the release thread through _releaseNow.
    private IntPtr _mouseHook;
    private HookProc? _mouseProc;
    private volatile bool _wantMouse;
    private volatile bool _mouseSeen;   // the mouse hook was called since the watchdog last looked
    private readonly ConcurrentQueue<int> _releaseNow = new();
    private readonly List<int> _early = new(); // hook thread only

    private ChatterEngine? _engine;
    private Thread? _hookThread;
    private Thread? _watchdogThread;
    private volatile bool _running;
    private uint _hookThreadId;
    private IntPtr _hook;
    private HookProc? _proc;
    private long _lastProbeSeen;
    private readonly ManualResetEventSlim _ready = new(false);
    private string? _startError;

    // Which physical keyboard is typing: Raw Input names the device, the hook (which can block keys) can't. The hook runs
    // just before the matching Raw Input message arrives, so each key is attributed to the keyboard that sent the previous one.
    private volatile string? _lastDevice;
    private readonly ConcurrentDictionary<IntPtr, string> _handleToId = new();
    private readonly ConcurrentDictionary<string, KeyboardDevice> _seen = new();
    private WndProcDelegate? _wndProc;
    private IntPtr _rawWindow;

    public string Name => "Windows keyboard hook";
    public bool IsRunning => _running;
    public bool SupportsHoldSafe => true;
    public bool SupportsMouse => true;
    public string? SetupHint => null;
    public IReadOnlyList<KeyboardDevice> Devices => Array.Empty<KeyboardDevice>();
    public IReadOnlyList<KeyboardDevice> Seen => _seen.Values.OrderBy(d => d.Name).ToList();
    public event Action<string>? Notice;

    public void SetDeviceFilter(IReadOnlyCollection<string>? names) { }

    public void SetMouseFilter(bool on)
    {
        if (_wantMouse == on)
            return;
        _wantMouse = on;
        // Before Start the hook thread picks it up when it starts; after Stop there is nothing to change.
        if (_running && _hookThreadId != 0)
            PostThreadMessage(_hookThreadId, WM_MOUSEHOOK, IntPtr.Zero, IntPtr.Zero);
    }

    public string? Start(ChatterEngine engine)
    {
        if (_running)
            return null;

        _engine = engine;
        _startError = null;
        _ready.Reset();
        _running = true;

        _hookThread = new Thread(HookThreadMain) { IsBackground = true, Name = "Chatter hook", Priority = ThreadPriority.Highest };
        _hookThread.Start();
        if (!_ready.Wait(4000))
            _startError ??= "The keyboard hook did not start in time.";

        if (_startError is not null)
        {
            _running = false;
            return _startError;
        }

        _watchdogThread = new Thread(WatchdogMain) { IsBackground = true, Name = "Chatter watchdog" };
        _watchdogThread.Start();
        _flushThread = new Thread(FlushMain) { IsBackground = true, Name = "Chatter releases", Priority = ThreadPriority.Highest };
        _flushThread.Start();
        return null;
    }

    public void Stop()
    {
        if (!_running)
            return;

        _running = false;
        if (_hookThreadId != 0)
            PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _hookThread?.Join(1500);
        _flushWake.Set();
        _flushThread?.Join(500);
        _watchdogThread?.Join(500);

        // Never leave a key or button down in programs: with the hooks gone (so nothing new can be held back), send every
        // release that is still waiting, including a click's release handed over a moment ago.
        while (_releaseNow.TryDequeue(out var early))
            SendRelease(early, thenPress: false);
        if (_engine is not null)
            foreach (var key in _engine.TakeDueReleases(0, all: true))
                SendRelease(key, thenPress: false);
    }

    public void Dispose() => Stop();

    // ---- The hook ------------------------------------------------------------------------------------------------

    private void HookThreadMain()
    {
        _hookThreadId = GetCurrentThreadId();
        _proc = HookCallback;
        _mouseProc = MouseCallback;
        if (!Install())
        {
            _startError = $"Windows refused to install the keyboard hook (error {Marshal.GetLastWin32Error()}).";
            _ready.Set();
            return;
        }

        UpdateMouseHook(renew: false);
        CreateRawInputWindow();
        _ready.Set();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_REINSTALL)
            {
                if (_hook != IntPtr.Zero)
                    UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
                Install();
                // The mouse hook lives on this thread too: whatever made Windows drop one may well have dropped the other.
                UpdateMouseHook(renew: true);
                continue;
            }

            if (msg.message == WM_MOUSEHOOK)
            {
                UpdateMouseHook(renew: msg.wParam != UIntPtr.Zero);
                continue;
            }

            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_rawWindow != IntPtr.Zero)
            DestroyWindow(_rawWindow);
        _rawWindow = IntPtr.Zero;
        if (_mouseHook != IntPtr.Zero)
            UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
        if (_hook != IntPtr.Zero)
            UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>
    /// Installs or removes the mouse hook to match the setting; with <paramref name="renew"/>, an installed one is replaced
    /// too (Windows may have dropped it without a word). Hook thread only.
    /// </summary>
    private void UpdateMouseHook(bool renew)
    {
        if (_mouseHook != IntPtr.Zero && (renew || !_wantMouse))
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (!_wantMouse || _mouseHook != IntPtr.Zero)
            return;

        // The system's drag rectangle tells a click from a drag (protect held keys); read it fresh each time.
        _engine!.DragDistance = (Math.Max(2, GetSystemMetrics(SM_CXDRAG)), Math.Max(2, GetSystemMetrics(SM_CYDRAG)));
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc!, GetModuleHandle(null), 0);
        // If Windows refused, the watchdog sees no mouse activity this round and tries again.
    }

    // ---- Raw Input: which keyboard ---------------------------------------------------------------------------------

    private void CreateRawInputWindow()
    {
        try
        {
            _wndProc = RawWndProc;
            var cls = new WNDCLASS { lpfnWndProc = _wndProc, hInstance = GetModuleHandle(null), lpszClassName = "PaxChatterGuardRaw" };
            RegisterClass(ref cls);
            _rawWindow = CreateWindowEx(0, "PaxChatterGuardRaw", "", 0, 0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
            if (_rawWindow == IntPtr.Zero)
                return;

            var rid = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = 0x100 /* RIDEV_INPUTSINK */, hwndTarget = _rawWindow };
            RegisterRawInputDevices(&rid, 1, (uint)sizeof(RAWINPUTDEVICE));
        }
        catch
        {
            /* without it everything still works, just not per keyboard */
        }
    }

    private IntPtr RawWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == 0x00FF /* WM_INPUT */)
        {
            try
            {
                RAWINPUTHEADER header;
                var size = (uint)sizeof(RAWINPUTHEADER);
                if (GetRawInputData(lParam, 0x10000005 /* RID_HEADER */, &header, ref size, (uint)sizeof(RAWINPUTHEADER)) != unchecked((uint)-1)
                    && header.hDevice != IntPtr.Zero)
                    _lastDevice = IdOf(header.hDevice);
            }
            catch
            {
                /* ignore */
            }
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private string? IdOf(IntPtr handle)
    {
        if (_handleToId.TryGetValue(handle, out var known))
            return known;

        uint len = 0;
        GetRawInputDeviceInfo(handle, 0x20000007 /* RIDI_DEVICENAME */, null, ref len);
        if (len == 0)
            return null;
        var sb = new StringBuilder((int)len);
        GetRawInputDeviceInfo(handle, 0x20000007, sb, ref len);
        var path = sb.ToString();

        // One model shows up as several devices (one per interface), so identify it by vendor and product.
        var m = Regex.Match(path, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})");
        var id = m.Success ? $"{m.Groups[1].Value}:{m.Groups[2].Value}".ToUpperInvariant() : "builtin";
        var name = ProductName(path) ?? (m.Success ? "Keyboard" : "Built-in keyboard");
        var label = m.Success ? $"{name} ({id})" : name;

        _handleToId[handle] = id;
        _seen.AddOrUpdate(id, _ => new KeyboardDevice(label, id), (_, old) => old.Name.StartsWith("Keyboard (", StringComparison.Ordinal) ? new KeyboardDevice(label, id) : old);
        return id;
    }

    private static string? ProductName(string devicePath)
    {
        try
        {
            using var h = CreateFile(devicePath, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid)
                return null;
            var buf = new StringBuilder(256);
            return HidD_GetProductString(h, buf, 512) && buf.Length > 0 ? buf.ToString().Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private bool Install()
    {
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc!, GetModuleHandle(null), 0);
        Volatile.Write(ref _lastProbeSeen, Stopwatch.GetTimestamp());
        return _hook != IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION)
        {
            try
            {
                var kb = (KBDLLHOOKSTRUCT*)lParam;
                var extra = (long)kb->dwExtraInfo;

                if (extra == ProbeMagic)
                {
                    Volatile.Write(ref _lastProbeSeen, Stopwatch.GetTimestamp());
                    return (IntPtr)1; // the heartbeat never reaches any program
                }

                // Scancode 0 / VK_PACKET are text injected by input methods and on-screen keyboards, not key switches.
                if (kb->scanCode != 0 && kb->vkCode != 0xE7)
                {
                    var msg = (int)wParam;
                    var down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    var key = (int)kb->scanCode | ((kb->flags & LLKHF_EXTENDED) != 0 ? 0xE000 : 0);
                    var injected = (kb->flags & LLKHF_INJECTED) != 0;

                    // A release (or release + press) we sent for a held-back key: the engine notes it, programs get it.
                    if (extra == ReleaseMagic)
                    {
                        _engine!.OnKey(new KeyEvent(key, down, NowMicros(), injected, Replayed: true));
                        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                    }

                    if (down)
                        _vk[VkSlot(key)] = (ushort)kb->vkCode;

                    switch (_engine!.OnKey(new KeyEvent(key, down, NowMicros(), injected, extra == SelfTestMagic, _lastDevice)))
                    {
                        case Verdict.Pass:
                            break;
                        case Verdict.Defer:
                            _flushWake.Set(); // a release is now waiting; make sure the timer knows when it's due
                            return (IntPtr)1;
                        case Verdict.Replay:
                            _replays.Enqueue(key);
                            _flushWake.Set();
                            return (IntPtr)1;
                        default:
                            return (IntPtr)1;
                    }
                }
            }
            catch
            {
                // Never let an error escape into Windows; letting the key through is always the safe choice.
            }
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>
    /// The mouse hook. Only button events go to the engine. Movement goes straight on, unless protect-held-keys mode has a
    /// button down to look after (see <see cref="ChatterEngine.OnPointerMove"/>); the wheel always does.
    /// </summary>
    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        _mouseSeen = true;
        if (nCode != HC_ACTION)
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        try
        {
            var msg = (int)wParam;
            var m = (MSLLHOOKSTRUCT*)lParam;
            if (msg == WM_MOUSEMOVE)
            {
                if (!_engine!.WatchesPointer)
                    return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

                _early.Clear();
                if (_engine.OnPointerMove(m->pt.x, m->pt.y, NowMicros(), _early))
                {
                    // A click's held-back release has to reach programs before the pointer leaves the spot, or they
                    // would see a short drag: hold this movement back and send the release first.
                    foreach (var key in _early)
                        _releaseNow.Enqueue(key);
                    if (_early.Count > 0)
                        _flushWake.Set();
                    return (IntPtr)1;
                }

                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            int button;
            bool down;
            switch (msg)
            {
                case WM_LBUTTONDOWN: button = KeyCatalog.MouseLeft; down = true; break;
                case WM_LBUTTONUP: button = KeyCatalog.MouseLeft; down = false; break;
                case WM_RBUTTONDOWN: button = KeyCatalog.MouseRight; down = true; break;
                case WM_RBUTTONUP: button = KeyCatalog.MouseRight; down = false; break;
                case WM_MBUTTONDOWN: button = KeyCatalog.MouseMiddle; down = true; break;
                case WM_MBUTTONUP: button = KeyCatalog.MouseMiddle; down = false; break;
                case WM_XBUTTONDOWN or WM_XBUTTONUP:
                    switch (m->mouseData >> 16)
                    {
                        case 1: button = KeyCatalog.MouseBack; break;
                        case 2: button = KeyCatalog.MouseForward; break;
                        default: return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                    }

                    down = msg == WM_XBUTTONDOWN;
                    break;
                default:
                    return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam); // the wheel and anything else
            }

            var extra = (long)m->dwExtraInfo;

            // A release (or release + press) we sent for a held-back button: the engine notes it, programs get it.
            if (extra == ReleaseMagic)
            {
                _engine!.OnKey(new KeyEvent(button, down, NowMicros(), Injected: true, Replayed: true));
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            // Clicks other programs create (macros, remote desktop) are left alone, and so are taps on a touch screen or
            // with a pen: neither is a worn switch.
            var injected = (m->flags & LLMHF_INJECTED) != 0;
            if ((injected && _engine!.Config.IgnoreInjected) || (extra & PenTouchMask) == PenTouchSignature)
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

            switch (_engine!.OnKey(new KeyEvent(button, down, NowMicros(), injected, X: m->pt.x, Y: m->pt.y)))
            {
                case Verdict.Pass:
                    break;
                case Verdict.Defer:
                    _flushWake.Set(); // a release is now waiting; make sure the timer knows when it's due
                    return (IntPtr)1;
                case Verdict.Replay:
                    _replays.Enqueue(button);
                    _flushWake.Set();
                    return (IntPtr)1;
                default:
                    return (IntPtr)1;
            }
        }
        catch
        {
            // Never let an error escape into Windows; letting the click through is always the safe choice.
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    // ---- Watchdog ------------------------------------------------------------------------------------------------

    private void WatchdogMain()
    {
        while (_running)
        {
            _mouseSeen = false;
            for (var i = 0; i < 300 && _running; i++)
                Thread.Sleep(100); // every 30 s
            if (!_running)
                return;

            var before = Volatile.Read(ref _lastProbeSeen);
            SendKey(0x76, down: false, ProbeMagic); // a release of F24 with no press: nothing ever reacts to it
            for (var i = 0; i < 10 && Volatile.Read(ref _lastProbeSeen) == before; i++)
                Thread.Sleep(50);

            if (Volatile.Read(ref _lastProbeSeen) == before && _running)
            {
                Notice?.Invoke("Windows removed the keyboard hook; it was reinstalled.");
                PostThreadMessage(_hookThreadId, WM_REINSTALL, IntPtr.Zero, IntPtr.Zero); // the mouse hook too
            }
            else if (_wantMouse && !_mouseSeen && _running)
            {
                // Windows drops a mouse hook that answers too slowly just the same, and the mouse hook, called for every
                // movement, is the likelier one to be hit. Nothing at all came through it for a whole round: replace it.
                // If the mouse simply wasn't touched, that costs nothing.
                PostThreadMessage(_hookThreadId, WM_MOUSEHOOK, (IntPtr)1, IntPtr.Zero);
            }
        }
    }

    // ---- Held-back releases (protect held keys) --------------------------------------------------------------------

    /// <summary>
    /// Sends held-back releases when they fall due, and re-sends release + press pairs the hook asked for. Sleeps on a
    /// high-resolution waitable timer (about 0.5 ms precision) until the next release is due, or until the hook wakes it.
    /// </summary>
    private void FlushMain()
    {
        var timer = IntPtr.Zero;
        try
        {
            timer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        }
        catch
        {
            /* older Windows: fall back to an ordinary timed wait */
        }

        var handles = stackalloc IntPtr[2];
        handles[1] = _flushWake.SafeWaitHandle.DangerousGetHandle();

        try
        {
            while (_running)
            {
                // First a click's release the pointer is waiting on (its movement is held back until it's through).
                while (_releaseNow.TryDequeue(out var early))
                    SendRelease(early, thenPress: false);
                while (_replays.TryDequeue(out var replay))
                    SendRelease(replay, thenPress: true);
                foreach (var key in _engine!.TakeDueReleases(NowMicros()))
                    SendRelease(key, thenPress: false);

                var next = _engine.NextReleaseDue();
                if (next is null)
                {
                    _flushWake.WaitOne();
                    continue;
                }

                var waitMicros = next.Value - NowMicros();
                if (waitMicros <= 0)
                    continue;

                if (timer != IntPtr.Zero)
                {
                    var due = -waitMicros * 10; // negative = relative, in 100 ns units
                    if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                    {
                        handles[0] = timer;
                        WaitForMultipleObjects(2, handles, false, 1000);
                        continue;
                    }
                }

                _flushWake.WaitOne(TimeSpan.FromMilliseconds(Math.Max(1, (waitMicros + 999) / 1000)));
            }
        }
        catch
        {
            // Should never happen; if it does, the releases still waiting are sent when the filter stops.
        }
        finally
        {
            if (timer != IntPtr.Zero)
                CloseHandle(timer);
        }
    }

    private static int VkSlot(int key) => (key & 0xFF) | ((key & 0xE000) != 0 ? 0x100 : 0);

    /// <summary>
    /// Sends a key's release (and, with <paramref name="thenPress"/>, a press right after it, in one go so nothing can come
    /// between them). Uses the virtual key the press had plus its scan code and extended flag, so the release matches it.
    /// </summary>
    private void SendRelease(int key, bool thenPress)
    {
        if (KeyCatalog.IsMouseButton(key))
        {
            SendButton(key, thenPress);
            return;
        }

        const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;
        var scan = (ushort)(key & 0xFF);
        var extended = (key & 0xE000) != 0;
        var vk = _vk[VkSlot(key)];
        if (vk == 0)
            vk = (ushort)MapVirtualKey((uint)(scan | (extended ? 0xE000 : 0)), 3 /* MAPVK_VSC_TO_VK_EX */);

        var inputs = stackalloc INPUT[2];
        var flags = extended ? KEYEVENTF_EXTENDEDKEY : 0u;
        inputs[0] = new INPUT { type = 1, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags | KEYEVENTF_KEYUP, dwExtraInfo = (UIntPtr)(ulong)ReleaseMagic } } };
        inputs[1] = new INPUT { type = 1, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = (UIntPtr)(ulong)ReleaseMagic } } };
        SendInput(thenPress ? 2u : 1u, inputs, sizeof(INPUT));
    }

    /// <summary>
    /// Sends a mouse button's release (and, with <paramref name="thenPress"/>, a press right after it, in one go) where the
    /// pointer is. The hook reports the primary button as the left one; when the buttons are swapped in the system settings,
    /// Windows swaps sent clicks as well, so the other physical button is sent to come out as the same one.
    /// </summary>
    private static void SendButton(int key, bool thenPress)
    {
        const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004, RIGHTDOWN = 0x0008, RIGHTUP = 0x0010;
        const uint MIDDLEDOWN = 0x0020, MIDDLEUP = 0x0040, XDOWN = 0x0080, XUP = 0x0100;
        var swapped = GetSystemMetrics(SM_SWAPBUTTON) != 0;
        var (up, press, data) = key switch
        {
            KeyCatalog.MouseLeft => swapped ? (RIGHTUP, RIGHTDOWN, 0u) : (LEFTUP, LEFTDOWN, 0u),
            KeyCatalog.MouseRight => swapped ? (LEFTUP, LEFTDOWN, 0u) : (RIGHTUP, RIGHTDOWN, 0u),
            KeyCatalog.MouseMiddle => (MIDDLEUP, MIDDLEDOWN, 0u),
            KeyCatalog.MouseBack => (XUP, XDOWN, 1u),    // XBUTTON1
            _ => (XUP, XDOWN, 2u),                       // XBUTTON2
        };

        var inputs = stackalloc INPUT[2];
        inputs[0] = new INPUT { type = 0, u = new INPUTUNION { mi = new MOUSEINPUT { mouseData = data, dwFlags = up, dwExtraInfo = (UIntPtr)(ulong)ReleaseMagic } } };
        inputs[1] = new INPUT { type = 0, u = new INPUTUNION { mi = new MOUSEINPUT { mouseData = data, dwFlags = press, dwExtraInfo = (UIntPtr)(ulong)ReleaseMagic } } };
        SendInput(thenPress ? 2u : 1u, inputs, sizeof(INPUT));
    }

    // ---- Self-test -----------------------------------------------------------------------------------------------

    public bool SendSelfTest(int key, int gapMs)
    {
        if (!_running)
            return false;

        _ = Task.Run(() =>
        {
            SendKey(key, down: true, SelfTestMagic);
            Thread.Sleep(30);
            SendKey(key, down: false, SelfTestMagic);
            Thread.Sleep(Math.Max(1, gapMs));
            SendKey(key, down: true, SelfTestMagic);
            Thread.Sleep(25);
            SendKey(key, down: false, SelfTestMagic);
        });
        return true;
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    private static long NowMicros() => Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;

    private static void SendKey(int key, bool down, long magic)
    {
        const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_SCANCODE = 0x8;
        var input = new INPUT
        {
            type = 1,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = (ushort)(key & 0xFF),
                    dwFlags = KEYEVENTF_SCANCODE | (down ? 0u : KEYEVENTF_KEYUP) | ((key & 0xE000) != 0 ? KEYEVENTF_EXTENDEDKEY : 0u),
                    time = 0,
                    dwExtraInfo = (UIntPtr)(ulong)magic,
                },
            },
        };
        SendInput(1, &input, sizeof(INPUT));
    }

    // ---- Native interop ------------------------------------------------------------------------------------------

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x, y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x, y;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage, usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType, dwSize;
        public IntPtr hDevice, wParam;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE* devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, RAWINPUTHEADER* data, ref uint size, uint headerSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder? data, ref uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(Microsoft.Win32.SafeHandles.SafeFileHandle h, StringBuilder buffer, int length);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT* pInputs, int cbSize);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForMultipleObjects(uint count, IntPtr* handles, bool waitAll, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
