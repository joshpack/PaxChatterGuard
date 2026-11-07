using Avalonia.Controls;
using Avalonia.Media;
using PaxStandard;

namespace PaxChatterGuard.Views;

/// <summary>
/// The PAX App Standard for this app's windows: the PAX title bar (icon, name, minimize / maximize / close) instead of the
/// system one, and the window's size, position and maximized state remembered under a key in windows.json.
/// </summary>
public static class Chrome
{
    public const string AppId = "PaxChatterGuard";

    private static IImage? _icon;

    /// <summary>The app icon for the title bar, drawn at twice its 18 px so it stays sharp on high-DPI screens.</summary>
    public static IImage Icon => _icon ??= BrandIcon.Render(36);

    /// <summary>
    /// Puts the PAX title bar above the window's content and restores its placement. Call at the end of the constructor,
    /// once the title is final. macOS keeps its own title bar (its window buttons sit on the left and can't be removed).
    /// </summary>
    public static void Apply(Window w, string placementKey)
    {
        if (!OperatingSystem.IsMacOS() && w.Content is Control content)
        {
            w.Content = null;
            w.Content = PaxChrome.Wrap(w, content, w.Title ?? "PAX Chatter Guard", Icon, resizable: w.CanResize);
        }

        PaxChrome.RememberPlacement(w, placementKey);
    }

    /// <summary>
    /// The window header's logo tile shows the app icon (keycap and shield) instead of the old X on a green tile.
    /// </summary>
    public static void UseBrandMark(Avalonia.Controls.Shapes.Path mark)
    {
        if (mark.Parent is Viewbox { Parent: Border tile })
        {
            tile.Background = Brushes.Transparent;
            tile.BoxShadow = default;
            tile.Child = new Image { Source = BrandIcon.Render(96) };
        }
        else
            mark.Data = SpadeIcon.Geometry;
    }
}
