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

    /// <summary>Renders the app icon (X on a rounded accent tile) to a bitmap for the tray and window icon.</summary>
    public static WindowIcon CreateWindowIcon(int size = 128, bool active = true)
    {
        var tile = new Grid { Width = size, Height = size };
        tile.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(size * 0.22),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse(active ? "#00C8FF" : "#5B6272"), 0),
                    new GradientStop(Color.Parse(active ? "#19F58C" : "#3B4150"), 1),
                },
            },
        });
        tile.Children.Add(new Viewbox
        {
            Margin = new Thickness(size * 0.18),
            Child = new ShapePath
            {
                Data = Geometry,
                Fill = new SolidColorBrush(Color.Parse(active ? "#04140C" : "#B8BECB")),
                Width = 100,
                Height = 100,
                Stretch = Stretch.Uniform,
            },
        });

        tile.Measure(new Size(size, size));
        tile.Arrange(new Rect(0, 0, size, size));

        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        bitmap.Render(tile);
        return new WindowIcon(bitmap);
    }
}
