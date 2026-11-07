using PaxChatterGuard.Core;

namespace PaxChatterGuard.Platform;

/// <summary>A keyboard the backend can filter (only meaningful on Linux, where filtering is per device).</summary>
public sealed record KeyboardDevice(string Name, string Path);

/// <summary>
/// The operating-system side: catch every keystroke, ask the <see cref="ChatterEngine"/> what to do with it, and
/// swallow it if told to. One implementation per OS; the decision logic is shared.
/// </summary>
public interface IKeyboardBackend : IDisposable
{
    /// <summary>Short description for the UI, e.g. "Windows keyboard hook".</summary>
    string Name { get; }

    bool IsRunning { get; }

    /// <summary>Help for getting this backend working on the user's system (permissions etc.), or null if none needed.</summary>
    string? SetupHint { get; }

    /// <summary>Starts intercepting. Returns null on success, otherwise a plain-language reason it could not start.</summary>
    string? Start(ChatterEngine engine);

    void Stop();

    /// <summary>Something worth telling the user happened (e.g. the hook had to be reinstalled). Any thread.</summary>
    event Action<string>? Notice;

    /// <summary>
    /// Sends a tiny simulated chatter burst (press, release, then another press <paramref name="gapMs"/> later) through the
    /// real pipeline, as if a worn switch had produced it. Returns false if this backend can't.
    /// </summary>
    bool SendSelfTest(int key, int gapMs);

    /// <summary>Keyboards found (Linux). Empty elsewhere.</summary>
    IReadOnlyList<KeyboardDevice> Devices { get; }

    /// <summary>Keyboards seen typing so far, one entry per model (Windows). Path is the stable device id.</summary>
    IReadOnlyList<KeyboardDevice> Seen => Array.Empty<KeyboardDevice>();

    /// <summary>Limits filtering to these device names (Linux); null or empty means every keyboard.</summary>
    void SetDeviceFilter(IReadOnlyCollection<string>? names);

    /// <summary>
    /// Whether this backend can hold releases back and send them later (protect-held-keys mode): it acts on
    /// <see cref="Verdict.Defer"/> and <see cref="Verdict.Replay"/> and sends what <see cref="ChatterEngine.TakeDueReleases"/> hands it.
    /// </summary>
    bool SupportsHoldSafe => false;

    /// <summary>Whether this backend can filter mouse buttons too (<see cref="SetMouseFilter"/>).</summary>
    bool SupportsMouse => false;

    /// <summary>
    /// Watches the mouse buttons (on) or leaves the mouse completely alone (off, the default). Any thread, also before
    /// <see cref="Start"/>; the backend remembers it.
    /// </summary>
    void SetMouseFilter(bool on) { }
}

public static class BackendFactory
{
    public static IKeyboardBackend Create()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsBackend();
        if (OperatingSystem.IsLinux())
            return new LinuxBackend();
        if (OperatingSystem.IsMacOS())
            return new MacBackend();
        return new UnsupportedBackend();
    }
}

/// <summary>Stand-in for platforms nothing is implemented for.</summary>
public sealed class UnsupportedBackend : IKeyboardBackend
{
    public string Name => "Not supported on this system";
    public bool IsRunning => false;
    public string? SetupHint => null;
    public event Action<string>? Notice { add { } remove { } }
    public IReadOnlyList<KeyboardDevice> Devices => Array.Empty<KeyboardDevice>();
    public string? Start(ChatterEngine engine) => "Keyboard filtering isn't available on this operating system.";
    public void Stop() { }
    public bool SendSelfTest(int key, int gapMs) => false;
    public void SetDeviceFilter(IReadOnlyCollection<string>? names) { }
    public void Dispose() { }
}
