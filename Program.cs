using Avalonia;

namespace PaxChatterGuard;

internal static class Program
{
    private static FileStream? _instanceLock;

    /// <summary>Started by the system at login: stay in the tray instead of opening the window.</summary>
    public static bool StartMinimized { get; private set; }

    /// <summary>
    /// --safe-test N: quit by itself after N seconds. Used when testing the keyboard filter so that a mistake can never
    /// leave a filter running on someone's keyboard.
    /// </summary>
    public static int SafeTestSeconds { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--minimized", StringComparison.OrdinalIgnoreCase))
                StartMinimized = true;
            else if (args[i].Equals("--safe-test", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out var s))
                SafeTestSeconds = s;
        }

        if (!AcquireSingleInstance())
            return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Software rendering: a settings window this small doesn't need the graphics card, and skipping GPU setup keeps
    /// memory use and start-up time low.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } })
            .With(new X11PlatformOptions { RenderingMode = new[] { X11RenderingMode.Software } })
            .LogToTrace();

    /// <summary>Single-instance guard: an exclusively opened lock file (named mutexes aren't shared across processes on Linux/macOS).</summary>
    private static bool AcquireSingleInstance()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "PaxChatterGuard.instance.lock");
            _instanceLock = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
