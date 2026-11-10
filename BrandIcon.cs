using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace pxdChatterGuard;

/// <summary>
/// pxd Chatter Guard's icon: a keycap guarded by a green shield on a midnight-slate tile (grey when filtering is off).
/// Drawn from vector shapes so it stays crisp at every size; used for the tray, windows and the exe's .ico.
/// </summary>
public static class BrandIcon
{
    /// <summary>The icon as a control (<paramref name="alt"/>: filtering off).</summary>
    public static Control Build(double size, bool alt = false)
    {
        var a = new Canvas { Width = 100, Height = 100 };
        void Fill(string data, string color) =>
            a.Children.Add(new ShapePath { Data = Geometry.Parse(data), Fill = new SolidColorBrush(Color.Parse(color)) });
        void Stroke(string data, string color, double width, string? fill = null) =>
            a.Children.Add(new ShapePath
            {
                Data = Geometry.Parse(data), Stroke = new SolidColorBrush(Color.Parse(color)), StrokeThickness = width,
                StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
                Fill = fill is null ? null : new SolidColorBrush(Color.Parse(fill)),
            });

        Fill(RoundRect(8, 10, 76, 78, 13), alt ? "#8E96A6" : "#B8C2D6");
        Fill(RoundRect(16, 14, 68, 62, 10), "#FFFFFF");
        Stroke("M30,30 L30,46 M30,38 L44,30 M34,36 L44,46", alt ? "#5B6272" : "#2B3550", 5);
        Stroke("M72,46 L94,53 L94,69 Q94,86 72,96 Q50,86 50,69 L50,53 Z", "#161C2E", 5, alt ? "#6B7280" : "#22C55E");
        Stroke("M61,70 L69,78 L83,62", "#FFFFFF", 6);
        return alt ? Tile(size, "#5B6272", "#2B2F3A", a, 0.11) : Tile(size, "#475A85", "#141A2C", a, 0.11);
    }

    public static RenderTargetBitmap Render(int size, bool alt = false)
    {
        var c = Build(size, alt);
        c.Measure(new Size(size, size));
        c.Arrange(new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        bitmap.Render(c);
        return bitmap;
    }

    public static WindowIcon Window(int size = 64, bool alt = false) => new(Render(size, alt));

    /// <summary>Writes a multi-size .ico (PNG entries) for the exe, or a 256 px PNG when the path ends in .png.</summary>
    public static void Write(string path)
    {
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            using var png = File.Create(path);
            Render(256).Save(png);
            return;
        }

        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var images = sizes.Select(s =>
        {
            using var ms = new MemoryStream();
            Render(s).Save(ms);
            return ms.ToArray();
        }).ToList();
        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            var b = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            w.Write(b);
            w.Write(b);
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }

        foreach (var img in images)
            w.Write(img);
    }

    private static Grid Tile(double size, string from, string to, Canvas art, double margin)
    {
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(size * 0.22),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse(from), 0), new GradientStop(Color.Parse(to), 1) },
            },
        });
        g.Children.Add(new Viewbox { Margin = new Thickness(size * margin), Child = art });
        return g;
    }

    private static string RoundRect(double x1, double y1, double x2, double y2, double r) =>
        FormattableString.Invariant($"M{x1 + r},{y1} L{x2 - r},{y1} Q{x2},{y1} {x2},{y1 + r} L{x2},{y2 - r} Q{x2},{y2} {x2 - r},{y2} L{x1 + r},{y2} Q{x1},{y2} {x1},{y2 - r} L{x1},{y1 + r} Q{x1},{y1} {x1 + r},{y1} Z");
}