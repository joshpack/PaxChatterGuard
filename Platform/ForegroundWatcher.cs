using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace pxdChatterGuard.Platform;

/// <summary>
/// Windows: tells which program is in front, so filtering can pause for the programs on the pause list, and whether it runs
/// as administrator while this app doesn't (Windows then keeps the keys typed into it from the hook). It listens for the
/// system's "foreground window changed" event (no polling, nothing per keystroke). Create and use it on the UI thread: the
/// events arrive through that thread's message loop.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ForegroundWatcher : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private WinEventProc? _proc;
    private IntPtr _hook;
    private readonly Dictionary<uint, (string? Name, bool Elevated)> _seen = new();

    // Running as administrator ourselves, every program's keys reach the hook, so there is nothing to check.
    private readonly bool _checkElevation = !Elevation.IsElevated;

    /// <summary>The program in front, as an exe file name (e.g. "game.exe"), or null if unknown.</summary>
    public string? Current { get; private set; }

    /// <summary>
    /// The program in front runs as administrator and this app doesn't, so Windows keeps the keys typed into it from the
    /// keyboard hook and they can't be filtered.
    /// </summary>
    public bool CurrentElevated { get; private set; }

    /// <summary>The program in front changed. Raised on the UI thread.</summary>
    public event Action? Changed;

    public void Start()
    {
        if (_hook != IntPtr.Zero)
            return;
        _proc = OnEvent;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        Update(GetForegroundWindow());
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
            UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    private void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time) => Update(hwnd);

    private void Update(IntPtr hwnd)
    {
        var (name, elevated) = Lookup(hwnd);
        if (string.Equals(name, Current, StringComparison.OrdinalIgnoreCase) && elevated == CurrentElevated)
            return;
        Current = name;
        CurrentElevated = elevated;
        Changed?.Invoke();
    }

    /// <summary>The exe name of the window's program, and whether it runs as administrator while this app doesn't.</summary>
    private (string? Name, bool Elevated) Lookup(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return (null, false);
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
            return (null, false);

        // Process ids are reused, but only after the process is gone; a stale entry at worst gets one window wrong. A
        // process can't change whether it runs as administrator, so that is looked up once per process too.
        if (_seen.Count > 256)
            _seen.Clear();
        if (!_seen.TryGetValue(pid, out var found))
            _seen[pid] = found = (ExeName(pid), _checkElevation && Elevation.IsProcessElevated(pid));
        return found;
    }

    /// <summary>A process's exe file name. Limited query rights are enough, also for programs running as administrator.</summary>
    public static string? ExeName(uint pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var buffer = new StringBuilder(1024);
                var size = buffer.Capacity;
                if (QueryFullProcessImageName(handle, 0, buffer, ref size))
                    return Path.GetFileName(buffer.ToString());
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName + ".exe";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Programs that have a window open right now, as exe file names, for choosing one to add.</summary>
    public static List<string> RunningPrograms()
    {
        var own = Environment.ProcessId;
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id != own && p.MainWindowHandle != IntPtr.Zero && ExeName((uint)p.Id) is { Length: > 0 } name)
                    names.Add(name);
            }
            catch
            {
                /* gone already, or not ours to look at */
            }
            finally
            {
                p.Dispose();
            }
        }

        return names.ToList();
    }

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
