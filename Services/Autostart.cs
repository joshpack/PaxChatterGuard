using Microsoft.Win32;

namespace PaxChatterGuard.Services;

/// <summary>Start with the system: a per-user registry entry on Windows, a LaunchAgent on macOS, an autostart entry on Linux.</summary>
public static class Autostart
{
    private const string AppName = "PaxChatterGuard";
    private const string WindowsRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        try
        {
            var command = LaunchCommand();
            if (command is null)
                return false;

            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey);
                return key?.GetValue(AppName) is string existing
                       && string.Equals(existing, command + " --minimized", StringComparison.OrdinalIgnoreCase);
            }

            var path = OperatingSystem.IsMacOS() ? MacPlistPath : LinuxDesktopPath;
            return File.Exists(path) && File.ReadAllText(path).Contains(command.Trim('"'), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        var command = LaunchCommand() ?? throw new InvalidOperationException("Can't determine how this app was launched.");

        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(WindowsRunKey);
            if (enabled)
                key.SetValue(AppName, command + " --minimized");
            else
                key.DeleteValue(AppName, throwOnMissingValue: false);
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
        File.WriteAllText(path, OperatingSystem.IsMacOS() ? MacPlist(command) : LinuxDesktop(command));
    }

    private static string MacPlistPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "com.paxchatterguard.app.plist");

    private static string LinuxDesktopPath =>
        Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "autostart",
            "pax-chatter-guard.desktop");

    private static string MacPlist(string command) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key><string>com.paxchatterguard.app</string>
          <key>ProgramArguments</key><array><string>{System.Security.SecurityElement.Escape(command.Trim('"'))}</string><string>--minimized</string></array>
          <key>RunAtLoad</key><true/>
        </dict>
        </plist>
        """;

    private static string LinuxDesktop(string command) =>
        $"[Desktop Entry]\nType=Application\nName=PAX Chatter Guard\nExec={command} --minimized\nX-GNOME-Autostart-enabled=true\nTerminal=false\n";

    /// <summary>The quoted command line that relaunches this app (the --minimized flag is added where it's registered).</summary>
    private static string? LaunchCommand()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return null;

        // Running through "dotnet PaxChatterGuard.dll" (e.g. dotnet run): register the dll under dotnet instead.
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dll = Environment.GetCommandLineArgs().FirstOrDefault();
            return string.IsNullOrEmpty(dll) ? null : $"\"{exe}\" \"{dll}\"";
        }

        return $"\"{exe}\"";
    }
}
