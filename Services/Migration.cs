using pxdStandard;

namespace pxdChatterGuard.Services;

/// <summary>
/// First start after the rename to pxd (1.4.0): moves what earlier versions kept under the app's former name. Every step
/// is safe to repeat (it does nothing once the move is done) and never throws.
/// </summary>
public static class Migration
{
    public const string AppId = "pxdChatterGuard";

    /// <summary>Runs before anything reads the settings.</summary>
    public static void Run()
    {
        // With PXDCG_DATA_DIR set (tests), the real settings folder is never touched.
        if (Environment.GetEnvironmentVariable("PXDCG_DATA_DIR") is not { Length: > 0 })
            DataFolder(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Autostart.MigrateFormer();
    }

    /// <summary>
    /// Moves the settings folder under <paramref name="root"/> (%APPDATA% on Windows, ~/.config elsewhere) from the former
    /// name to <see cref="AppId"/> when only the former one exists. True if it moved.
    /// </summary>
    public static bool DataFolder(string root) =>
        pxdMigrate.Folder(Path.Combine(root, pxdMigrate.FormerName(AppId)), Path.Combine(root, AppId));
}
