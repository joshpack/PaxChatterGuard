using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace pxdChatterGuard.Platform;

/// <summary>
/// Windows: programs that run as administrator. Windows keeps the keys typed into such a program away from programs that
/// don't run as administrator (User Interface Privilege Isolation), and a low-level keyboard hook is one of the things it
/// keeps them from. So while one is in front, what is typed into it can only be filtered if this app runs as administrator
/// too.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Elevation
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_CANCELLED = 1223;

    private static readonly Lazy<bool> Self = new(() => TokenElevated(GetCurrentProcess()) == true);

    /// <summary>Whether this app runs as administrator (its token is elevated).</summary>
    public static bool IsElevated => Self.Value;

    /// <summary>
    /// Whether a process runs as administrator, going by its token. A process Windows won't let us look into counts as one:
    /// that is what processes of the system and of other accounts (such as an administrator's) give.
    /// </summary>
    public static bool IsProcessElevated(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
            return Marshal.GetLastPInvokeError() == ERROR_ACCESS_DENIED;

        try
        {
            return TokenElevated(process) == true;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>The token's elevation; true when the token can't be opened for lack of rights; null when unknown.</summary>
    private static bool? TokenElevated(IntPtr process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
            return Marshal.GetLastPInvokeError() == ERROR_ACCESS_DENIED ? true : null;

        try
        {
            return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _) ? elevated != 0 : null;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>
    /// Runs a program as administrator through ShellExecute's "runas", so Windows asks first. Returns null once it has
    /// started (with <paramref name="wait"/>: once it has finished with exit code 0), otherwise a few words saying why not,
    /// e.g. "permission wasn't given". Blocks while Windows asks, so call it off the UI thread.
    /// </summary>
    public static string? RunElevated(string exe, string arguments, bool hidden = false, TimeSpan? wait = null)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
                WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
            });

            if (wait is null || process is null)
                return null;
            if (!process.WaitForExit(wait.Value))
                return "it didn't finish in time";
            return process.ExitCode == 0 ? null : $"it failed with error {process.ExitCode}";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            return "permission wasn't given";
        }
        catch (Exception ex)
        {
            return ex.Message.TrimEnd('.');
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
