using Avalonia;

namespace pxdChatterGuard;

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

    /// <summary>
    /// --restarted: started by "Restart as administrator". The copy that started it is closing, so wait for it to let go of
    /// the single-instance lock instead of giving up at once.
    /// </summary>
    public static bool Restarted { get; private set; }

    /// <summary>Whether this start may update the app before it opens (not for --safe-test).</summary>
    public static bool UpdatesBeforeStart(string[] args) =>
        !args.Any(a => a.Equals("--safe-test", StringComparison.OrdinalIgnoreCase));

    [STAThread]
    public static void Main(string[] args)
    {
        // Running an installed copy updates it first (Windows only; a no-op for bin\ builds, offline or slow GitHub). It
        // also runs when started as administrator by the sign-in task or "Restart as administrator": that copy can update
        // its own folder and restarts with the same arguments. --safe-test runs are for trying changes, so they never update.
        if (UpdatesBeforeStart(args)
            && pxdStandard.pxdUpdate.BeforeStart("pxdtek/pxdChatterGuard", @"^pxdChatterGuard-.*-win-x64\.zip$", args))
            return;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--minimized", StringComparison.OrdinalIgnoreCase))
                StartMinimized = true;
            else if (args[i].Equals("--restarted", StringComparison.OrdinalIgnoreCase))
                Restarted = true;
            else if (args[i].Equals("--safe-test", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out var s))
                SafeTestSeconds = s;
        }

        if (!AcquireSingleInstance(Restarted ? TimeSpan.FromSeconds(15) : TimeSpan.Zero))
        {
            // Already running (perhaps with its tray icon hidden): started by hand, so have that copy open its window.
            if (!StartMinimized)
                Services.SingleInstance.SignalRunning();
            return;
        }

        // First start after the rename: settings folder and start-at-sign-in move to the new name before anything reads them.
        Services.Migration.Run();

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

    /// <summary>
    /// Single-instance guard: an exclusively opened lock file (named mutexes aren't shared across processes on Linux/macOS).
    /// Waits up to <paramref name="wait"/> for another copy to let go of it.
    /// </summary>
    private static bool AcquireSingleInstance(TimeSpan wait)
    {
        var path = Path.Combine(Path.GetTempPath(), "pxdChatterGuard.instance.lock");
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                _instanceLock = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException) when (DateTime.UtcNow < until)
            {
                Thread.Sleep(200);
            }
            catch (IOException)
            {
                return false;
            }
        }
    }
}
