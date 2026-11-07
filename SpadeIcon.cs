using Avalonia;
using Avalonia.Controls;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PaxChatterGuard;

/// <summary>The app's X mark, drawn from vector geometry so it stays crisp at every size.</summary>
public static class SpadeIcon
{
    /// <summary>A bold X in a 100x100 box.</summary>
    public const string PathData =
        "M20,6 L50,36 L80,6 L94,20 L64,50 L94,80 L80,94 L50,64 L20,94 L6,80 L36,50 L6,20 Z";

    public static readonly Geometry Geometry = Geometry.Parse(PathData);

    /// <summary>The app icon for the tray and windows, grey when filtering is off (see <see cref="BrandIcon"/>).</summary>
    public static WindowIcon CreateWindowIcon(int size = 128, bool active = true) => BrandIcon.Window(size, alt: !active);
}
