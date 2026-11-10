using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using pxdChatterGuard.Platform;
using pxdStandard;

namespace pxdChatterGuard.Services;

/// <summary>How "Start when I sign in" is set up for this copy of the app.</summary>
public enum AutostartMode
{
    Off,

    /// <summary>Starts at sign-in; on Windows through the Run key, without administrator rights.</summary>
    On,

    /// <summary>Windows: starts at sign-in as administrator, through a scheduled task, without Windows asking.</summary>
    AsAdministrator,
}

/// <summary>
/// Start with the system: a per-user registry entry on Windows (a scheduled task with highest privileges instead while
/// running as administrator), a LaunchAgent on macOS, an autostart entry on Linux.
/// </summary>
public static class Autostart
{
    private const string AppName = "pxdChatterGuard";
    private const string WindowsRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled() => Mode() != AutostartMode.Off;

    public static AutostartMode Mode()
    {
        try
        {
            if (LaunchTarget() is not { } target)
                return AutostartMode.Off;

            if (OperatingSystem.IsWindows())
            {
                // The task from before the rename keeps starting the app until a start as administrator moves it.
                if (TaskStartsThis(target) || FormerTaskPending())
                    return AutostartMode.AsAdministrator;
                using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey);
                return key?.GetValue(AppName) is string existing
                       && string.Equals(existing, Command(target) + " --minimized", StringComparison.OrdinalIgnoreCase)
                    ? AutostartMode.On
                    : AutostartMode.Off;
            }

            var path = OperatingSystem.IsMacOS() ? MacPlistPath : LinuxDesktopPath;
            return File.Exists(path) && File.ReadAllText(path).Contains(Command(target).Trim('"'), StringComparison.Ordinal)
                ? AutostartMode.On
                : AutostartMode.Off;
        }
        catch
        {
            return AutostartMode.Off;
        }
    }

    /// <summary>
    /// Switches starting at sign-in on or off. On Windows, switching on while running as administrator registers the task
    /// that starts it as administrator (and drops the Run entry, which would start a second copy without those rights);
    /// switching off removes both. Removing a task made as administrator may make Windows ask first, so call this off the
    /// UI thread.
    /// </summary>
    public static void Set(bool enabled)
    {
        var target = LaunchTarget() ?? throw new InvalidOperationException("Can't determine how this app was launched.");

        if (OperatingSystem.IsWindows())
        {
            SetWindows(enabled, target);
            return;
        }

        var path = OperatingSystem.IsMacOS() ? MacPlistPath : LinuxDesktopPath;
        if (!enabled)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var command = Command(target);
        File.WriteAllText(path, OperatingSystem.IsMacOS() ? MacPlist(command) : LinuxDesktop(command));
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindows(bool enabled, (string Exe, string Args) target)
    {
        using var key = Registry.CurrentUser.CreateSubKey(WindowsRunKey);
        if (enabled && Elevation.IsElevated)
        {
            using var user = WindowsIdentity.GetCurrent();
            var sid = user.User?.Value ?? throw new InvalidOperationException("Can't tell which account this is.");
            SignInTask.Register(SignInTask.Xml(sid, user.Name, target.Exe, TaskArguments(target), AppContext.BaseDirectory));
            key.DeleteValue(AppName, throwOnMissingValue: false);
            DeleteFormerTask();
            return;
        }

        if (enabled)
        {
            key.SetValue(AppName, Command(target) + " --minimized");
            return;
        }

        key.DeleteValue(AppName, throwOnMissingValue: false);
        var left = new List<string>();
        foreach (var name in new[] { SignInTask.Name, FormerName })
        {
            try
            {
                if (!SignInTask.TryDelete(name))
                    left.Add(name);
            }
            catch
            {
                return; // no Task Scheduler to ask, and so no task that could start anything either
            }
        }

        if (left.Count == 0)
            return;

        // The task was made as administrator, so removing it takes administrator rights: Windows' own tool does it, once
        // Windows has asked. Whether it worked is judged by the task being gone.
        var schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        string? error = null;
        foreach (var name in left)
            error = Elevation.RunElevated(schtasks, $"/Delete /TN {name} /F", hidden: true, wait: TimeSpan.FromSeconds(30)) ?? error;
        if (left.All(TaskGone))
            return;
        throw new InvalidOperationException($"removing the start as administrator takes administrator rights, and {error ?? "it is still there"}.");
    }

    // ── One-time move from the app's former name (versions before 1.4.0) ──

    /// <summary>The Run value and task name the app had before the rename.</summary>
    private static string FormerName => pxdMigrate.FormerName(AppName);

    /// <summary>Windows: the start-as-administrator task from before the rename is still there.</summary>
    public static bool FormerTaskPending()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            return SignInTask.Find(out _, out _, FormerName);
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteFormerTask()
    {
        try
        {
            SignInTask.TryDelete(FormerName);
        }
        catch
        {
            /* no Task Scheduler */
        }
    }

    /// <summary>
    /// Moves an existing "start when I sign in" from the former name to the new one, pointing at this copy: the Run value
    /// (its arguments and Task Manager's enabled state kept), the start-as-administrator task (only while running as
    /// administrator, which replacing it needs; until then the old task keeps starting the app), the macOS LaunchAgent and
    /// the Linux autostart entry. Never switches starting at sign-in on when it wasn't. Safe to repeat; never throws.
    /// </summary>
    public static void MigrateFormer()
    {
        try
        {
            if (LaunchTarget() is not { } target)
                return;

            if (OperatingSystem.IsWindows())
            {
                MigrateFormerWindows(target);
                return;
            }

            var former = OperatingSystem.IsMacOS() ? FormerMacPlistPath : FormerLinuxDesktopPath;
            if (!File.Exists(former))
                return;
            var path = OperatingSystem.IsMacOS() ? MacPlistPath : LinuxDesktopPath;
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var command = Command(target);
                File.WriteAllText(path, OperatingSystem.IsMacOS() ? MacPlist(command) : LinuxDesktop(command));
            }

            File.Delete(former);
        }
        catch
        {
            /* tried again at the next start */
        }
    }

    [SupportedOSPlatform("windows")]
    private static void MigrateFormerWindows((string Exe, string Args) target)
    {
        // A copy run through "dotnet <dll>" (dotnet run) isn't something to start at sign-in.
        if (target.Args.Length == 0)
            pxdMigrate.RunEntry(FormerName, AppName, target.Exe);

        if (!Elevation.IsElevated || !SignInTask.Find(out _, out _, FormerName))
            return;
        using var user = WindowsIdentity.GetCurrent();
        if (user.User?.Value is not { } sid)
            return;
        var enabled = SignInTask.IsEnabled(FormerName);
        SignInTask.Register(SignInTask.Xml(sid, user.Name, target.Exe, TaskArguments(target), AppContext.BaseDirectory));
        if (enabled == false)
            SignInTask.Disable(); // switched off in Task Scheduler: keep it off
        SignInTask.TryDelete(FormerName);
    }

    private static string FormerMacPlistPath =>
        Path.Combine(Path.GetDirectoryName(MacPlistPath)!, $"com.{FormerName.ToLowerInvariant()}.app.plist");

    private static string FormerLinuxDesktopPath =>
        Path.Combine(Path.GetDirectoryName(LinuxDesktopPath)!, $"{pxdMigrate.FormerPrefix.ToLowerInvariant()}-chatter-guard.desktop");

    [SupportedOSPlatform("windows")]
    private static bool TaskGone(string name)
    {
        try
        {
            return !SignInTask.Find(out _, out _, name);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Whether the scheduled task starts this copy. One that can't be read without administrator rights counts.</summary>
    [SupportedOSPlatform("windows")]
    private static bool TaskStartsThis((string Exe, string Args) target)
    {
        try
        {
            if (!SignInTask.Find(out var command, out var arguments))
                return false;
            return command is null
                   || (string.Equals(command.Trim('"'), target.Exe, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(arguments?.Trim(), TaskArguments(target), StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false; // no Task Scheduler to ask
        }
    }

    private static string TaskArguments((string Exe, string Args) target) => (target.Args + " --minimized").TrimStart();

    private static string MacPlistPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "com.pxdchatterguard.app.plist");

    private static string LinuxDesktopPath =>
        Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "autostart",
            "pxd-chatter-guard.desktop");

    private static string MacPlist(string command) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key><string>com.pxdchatterguard.app</string>
          <key>ProgramArguments</key><array><string>{System.Security.SecurityElement.Escape(command.Trim('"'))}</string><string>--minimized</string></array>
          <key>RunAtLoad</key><true/>
        </dict>
        </plist>
        """;

    private static string LinuxDesktop(string command) =>
        $"[Desktop Entry]\nType=Application\nName=pxd Chatter Guard\nExec={command} --minimized\nX-GNOME-Autostart-enabled=true\nTerminal=false\n";

    /// <summary>The quoted command line that relaunches this app (the --minimized flag is added where it's registered).</summary>
    private static string Command((string Exe, string Args) target) =>
        target.Args.Length > 0 ? $"\"{target.Exe}\" {target.Args}" : $"\"{target.Exe}\"";

    /// <summary>
    /// The program that relaunches this app, and the arguments that go before any of its own: none for the app's exe, the
    /// dll when it runs through "dotnet pxdChatterGuard.dll" (e.g. dotnet run). Null when that can't be told.
    /// </summary>
    public static (string Exe, string Args)? LaunchTarget()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return null;

        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dll = Environment.GetCommandLineArgs().FirstOrDefault();
            return string.IsNullOrEmpty(dll) ? null : (exe, $"\"{dll}\"");
        }

        return (exe, "");
    }
}
