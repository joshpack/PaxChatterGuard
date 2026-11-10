using System.Diagnostics;
using System.Runtime.InteropServices;

namespace pxdChatterGuard.Platform;

/// <summary>
/// Gives back memory the app isn't using right now. After the settings window closes (or at start-up in the tray) almost
/// all of the process can be paged out; it is brought back in pieces only if something needs it. Purely cosmetic for
/// correctness, but it keeps the tray footprint small.
/// </summary>
public static class MemoryTrim
{
    public static void Trim()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            if (OperatingSystem.IsWindows())
            {
                using var self = Process.GetCurrentProcess();
                EmptyWorkingSet(self.Handle);
            }
        }
        catch
        {
            /* an optimisation only: never let it affect the app */
        }
    }

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr process);
}
