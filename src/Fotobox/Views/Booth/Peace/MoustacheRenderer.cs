using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Core.Peace;

namespace Fotobox.Views.Booth.Peace;

/// <summary>Zeichnet die Bärte aus <see cref="MoustacheShapes"/> ins Livebild-Overlay und in Fotos.</summary>
public static class MoustacheRenderer
{
    private static readonly Brush Fill = CreateFill();

    /// <summary>
    /// Zeichnet einen Bart mit Mitte <paramref name="center"/> (Ansichtskoordinaten), Breite <paramref name="widthPx"/> und Winkel
    /// <paramref name="angleRad"/> (Bildkoordinaten, im Uhrzeigersinn positiv). Bei <paramref name="mirror"/> (gespiegelte Ansicht) kehrt sich der Winkel um.
    /// </summary>
    public static void Draw(DrawingContext dc, MoustacheStyle style, Point center, double widthPx, double angleRad, bool mirror)
    {
        if (!(widthPx > 0) || !double.IsFinite(widthPx))
            return;

        var angle = mirror ? -angleRad : angleRad;
        var matrix = Matrix.Identity;
        matrix.Scale(widthPx, widthPx);
        matrix.Rotate(angle * 180 / Math.PI);
        matrix.Translate(center.X, center.Y);

        dc.PushTransform(new MatrixTransform(matrix));
        dc.DrawGeometry(Fill, null, MoustacheShapes.Get(style));
        dc.Pop();
    }

    /// <summary>
    /// Zeichnet die Bärte auf das rohe, ungespiegelte Foto (<see cref="MoustachePose.Center"/> × Pixelgröße, ohne Spiegelung) und
    /// liefert ein eingefrorenes Bild gleicher Pixelgröße (96 dpi, Pbgra32). Nur auf dem UI-Thread.
    /// </summary>
    public static BitmapSource RenderOnto(BitmapSource photo, IReadOnlyList<(MoustachePose Pose, MoustacheStyle Style)> plan)
    {
        var width = photo.PixelWidth;
        var height = photo.PixelHeight;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(photo, new Rect(0, 0, width, height));
            foreach (var (pose, style) in plan)
                Draw(dc, style, new Point(pose.Center.X * width, pose.Center.Y * height), pose.WidthPx, pose.AngleRad, mirror: false);
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static Brush CreateFill()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x3B, 0x24, 0x14));
        brush.Freeze();
        return brush;
    }
}
