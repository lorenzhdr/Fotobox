using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Core.Peace;

namespace Fotobox.Views.Booth.Peace;

/// <summary>
/// Zeichnet die Bärte ins Livebild-Overlay und in Fotos: Die vorberechnete Haartextur (<see cref="MoustacheTextures"/>) wird mit
/// einer Transformation platziert (nur <c>DrawImage</c>, keine Geometrie, kein Weichzeichnen). Der Ursprung der Textur ist der
/// Formursprung (Mitte der Oberlippe), die Breite kommt von der Form (Breite 1), nicht vom Bild mit Rand.
/// </summary>
public static class MoustacheRenderer
{
    /// <summary>
    /// Zeichnet einen Bart mit Mitte <paramref name="center"/> (Ansichtskoordinaten), Breite <paramref name="widthPx"/> und Winkel
    /// <paramref name="angleRad"/> (Bildkoordinaten, im Uhrzeigersinn positiv). Bei <paramref name="mirror"/> (gespiegelte Ansicht) kehrt sich der Winkel um.
    /// <paramref name="scale"/> skaliert den Bart um den Ursprung (Plopp, 1 = Endgröße); <paramref name="wobbleOffset"/> (Bartbreiten, nach unten)
    /// und <paramref name="wobbleAngleRad"/> sind der Wippausschlag (im Foto 0). Die Textur wird für die Endbreite gewählt, nicht für die gerade
    /// animierte, damit beim Wachsen keine weiteren Auflösungsstufen entstehen. Blockiert nie: Ist die Textur noch nicht fertig, nimmt es die
    /// beste schon fertige Stufe (die fehlende entsteht im Hintergrund); gibt es keine, wird nichts gezeichnet (Rückgabe <c>false</c>).
    /// </summary>
    public static bool Draw(DrawingContext dc, MoustacheStyle style, MoustacheColor color, Point center, double widthPx, double angleRad, bool mirror,
        double scale = 1, double wobbleOffset = 0, double wobbleAngleRad = 0)
    {
        if (!(widthPx > 0) || !double.IsFinite(widthPx) || !(scale > 0) || !double.IsFinite(scale))
            return false;
        if (!double.IsFinite(wobbleOffset)) wobbleOffset = 0;
        if (!double.IsFinite(wobbleAngleRad)) wobbleAngleRad = 0;

        if (!MoustacheTextures.TryGetReady(style, color, widthPx, out var texture))
            return false;
        var angle = (mirror ? -(angleRad + wobbleAngleRad) : angleRad + wobbleAngleRad) * 180 / Math.PI;
        var size = widthPx * scale;

        var matrix = Matrix.Identity;
        matrix.Translate(0, wobbleOffset); // Wippen in Bartbreiten, im Bartkoordinatensystem (dreht mit)
        matrix.Scale(size, size);
        matrix.Rotate(angle);
        matrix.Translate(center.X, center.Y);

        dc.PushTransform(new MatrixTransform(matrix));
        dc.DrawImage(texture.Bitmap, texture.UnitRect);
        dc.Pop();
        return true;
    }

    /// <summary>Wie oben mit Standardfarbe (Dunkelbraun) und ohne Animation.</summary>
    public static bool Draw(DrawingContext dc, MoustacheStyle style, Point center, double widthPx, double angleRad, bool mirror) =>
        Draw(dc, style, MoustacheColor.DarkBrown, center, widthPx, angleRad, mirror);

    /// <summary>
    /// Zeichnet die Bärte auf das rohe, ungespiegelte Foto (<see cref="MoustachePose.Center"/> × Pixelgröße, ohne Spiegelung, in
    /// Endgröße ohne Animation) und liefert ein eingefrorenes Bild gleicher Pixelgröße (96 dpi, Pbgra32). Nur auf dem UI-Thread. Erzeugt nie
    /// Texturen (nur fertige, siehe <see cref="MoustacheTextures.EnsureAsync"/>); Bärte ohne irgendeine fertige Textur fehlen im Bild.
    /// </summary>
    public static BitmapSource RenderOnto(BitmapSource photo, IReadOnlyList<(MoustachePose Pose, MoustacheStyle Style, MoustacheColor Color)> plan)
    {
        var width = photo.PixelWidth;
        var height = photo.PixelHeight;

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.Fant);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(photo, new Rect(0, 0, width, height));
            foreach (var (pose, style, color) in plan)
                Draw(dc, style, color, new Point(pose.Center.X * width, pose.Center.Y * height), pose.WidthPx, pose.AngleRad, mirror: false);
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }
}
