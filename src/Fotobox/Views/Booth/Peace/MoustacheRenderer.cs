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
    private static readonly Brush GlossFill = CreateGloss();
    private static readonly Pen Outline = CreateOutline();

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
        var geometry = MoustacheShapes.Get(style);
        dc.DrawGeometry(Fill, Outline, geometry);
        dc.DrawGeometry(GlossFill, null, geometry);
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

    // Dunkelbraun mit weichem Verlauf: oben etwas heller, unten fast schwarz.
    private static Brush CreateFill()
    {
        var brush = new LinearGradientBrush(
            [
                new GradientStop(Color.FromRgb(0x5A, 0x38, 0x20), 0.0),
                new GradientStop(Color.FromRgb(0x3A, 0x22, 0x13), 0.45),
                new GradientStop(Color.FromRgb(0x1A, 0x0E, 0x08), 1.0),
            ],
            new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    // Dezenter Glanz im oberen Drittel: durchscheinendes Hellbraun, das nach unten ausläuft.
    private static Brush CreateGloss()
    {
        var brush = new LinearGradientBrush(
            [
                new GradientStop(Color.FromArgb(0x55, 0xA8, 0x74, 0x48), 0.0),
                new GradientStop(Color.FromArgb(0x22, 0x8A, 0x5A, 0x34), 0.28),
                new GradientStop(Color.FromArgb(0x00, 0x8A, 0x5A, 0x34), 0.5),
            ],
            new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    // Feine dunkle Kontur (Einheitsmaß: wird mit der Breite skaliert).
    private static Pen CreateOutline()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x10, 0x09, 0x05));
        brush.Freeze();
        var pen = new Pen(brush, 0.006) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }
}
