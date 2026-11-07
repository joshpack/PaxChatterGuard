using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PaxChatterGuard.Platform;

/// <summary>
/// Windows: tells which program is in front, so filtering can pause for the programs on the pause list. It listens for
/// the system's "foreground window changed" event (no polling, nothing per keystroke). Create and use it on the UI thread:
/// the events arrive through that thread's message loop.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ForegroundWatcher : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private WinEventProc? _proc;
    private IntPtr _hook;
    private readonly Dictionary<uint, string?> _names = new();

    /// <summary>The program in front, as an exe file name (e.g. "game.exe"), or null if unknown.</summary>
    public string? Current { get; private set; }

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
        var name = NameOf(hwnd);
        if (string.Equals(name, Current, StringComparison.OrdinalIgnoreCase))
            return;
        Current = name;
        Changed?.Invoke();
    }

    private string? NameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
            return null;

        // Process ids are reused, but only after the process is gone; a stale name at worst pauses one window too few.
        if (_names.Count > 256)
            _names.Clear();
        if (!_names.TryGetValue(pid, out var name))
            _names[pid] = name = ExeName(pid);
        return name;
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
