using Fotobox.Core.Fun;

namespace Fotobox.Core.Heart;

/// <summary>
/// Reine Mathematik für den Kisscam-Zoom: Planung des Ausschnitts (aus Herzbox und Gesichtern), Umrechnung in ein Pixelrechteck
/// für den Zuschnitt des Fotos und Transformation für die Livevorschau (Grid mit dem Kamerabild). Alle Rechtecke sind normiert
/// (0..1) und beziehen sich auf das UNgespiegelte Kamerabild.
/// </summary>
public static class KisscamZoom
{
    /// <summary>Zusätzlicher Rand um Herz und Gesichter, relativ zu deren Ausdehnung (15 %).</summary>
    public const double Margin = 0.15;

    /// <summary>
    /// Plant den Ausschnitt: Mitte = Mitte der Vereinigung aus <paramref name="heartBox"/> und <paramref name="faceBoxes"/>. Der Zoom
    /// (<paramref name="zoomPercent"/> / 100) wird so weit gesenkt, dass die Vereinigung samt 15 % Rand hineinpasst. Ergebnis ist ein
    /// Rechteck mit Breite = Höhe = 1 / Zoom (normiert, behält also das Bildseitenverhältnis), ins Bild verschoben (nie verkleinert).
    /// <c>null</c>, wenn kein Zoom übrig bleibt (Zoom ≤ 1), die Herzbox leer oder ein Wert ungültig ist.
    /// </summary>
    public static NormRect? Plan(NormRect heartBox, IReadOnlyList<NormRect> faceBoxes, int zoomPercent)
    {
        if (!IsFinite(heartBox) || heartBox.IsEmpty || zoomPercent <= 100)
            return null;

        var left = heartBox.X;
        var top = heartBox.Y;
        var right = heartBox.Right;
        var bottom = heartBox.Bottom;
        if (faceBoxes is not null)
        {
            foreach (var face in faceBoxes)
            {
                if (!IsFinite(face) || face.IsEmpty)
                    continue;
                left = Math.Min(left, face.X);
                top = Math.Min(top, face.Y);
                right = Math.Max(right, face.Right);
                bottom = Math.Max(bottom, face.Bottom);
            }
        }

        // Die Vereinigung nur im Bild betrachten (Boxen können leicht überstehen).
        left = Math.Clamp(left, 0, 1);
        top = Math.Clamp(top, 0, 1);
        right = Math.Clamp(right, 0, 1);
        bottom = Math.Clamp(bottom, 0, 1);
        var extent = Math.Max(right - left, bottom - top);
        var centerX = (left + right) / 2;
        var centerY = (top + bottom) / 2;

        var zoom = zoomPercent / 100.0;
        if (extent > 0)
            zoom = Math.Min(zoom, 1.0 / (extent * (1 + 2 * Margin)));
        if (!(zoom > 1.0 + 1e-9))
            return null;

        var size = 1.0 / zoom;
        var x = Math.Clamp(centerX - size / 2, 0, 1 - size);
        var y = Math.Clamp(centerY - size / 2, 0, 1 - size);
        return new NormRect(x, y, size, size);
    }

    /// <summary>
    /// Pixelrechteck (X, Y, Breite, Höhe) zum normierten Ausschnitt: auf das Bild geklemmt, mindestens 1 × 1 Pixel (bei einem Bild von
    /// 0 Pixeln Kantenlänge 0).
    /// </summary>
    public static (int X, int Y, int W, int H) ToPixelRect(NormRect r, int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
            return (0, 0, Math.Max(0, pixelWidth), Math.Max(0, pixelHeight));

        var (x, w) = Axis(r.X, r.Width, pixelWidth);
        var (y, h) = Axis(r.Y, r.Height, pixelHeight);
        return (x, y, w, h);

        static (int Start, int Length) Axis(double start, double length, int pixels)
        {
            if (!double.IsFinite(start) || !double.IsFinite(length))
                return (0, pixels);
            var from = (int)Math.Clamp(Math.Round(start * pixels), 0, pixels - 1);
            var to = (int)Math.Clamp(Math.Round((start + length) * pixels), from + 1, pixels);
            return (from, to - from);
        }
    }

    /// <summary>
    /// Transformation für ein Element, das die Kameravorschau zeigt (<c>p' = Scale · p + Translate</c>, also eine MatrixTransform mit
    /// Skalierung um den Ursprung und anschließender Verschiebung): Der Ausschnitt <paramref name="region"/> füllt die Ansicht,
    /// das Bild wird nie verkleinert und so verschoben, dass es die Ansicht ohne Rand bedeckt (soweit der Zoom das erlaubt).
    /// Gespiegelte Vorschau: <paramref name="mirror"/> (x' = 1 − x). Ungültige Ansicht: keine Veränderung <c>(1, 0, 0)</c>.
    /// </summary>
    public static (double Scale, double TranslateX, double TranslateY) ViewTransform(NormRect region, double imageWidth, double imageHeight,
        double viewWidth, double viewHeight, OverlayFit fit, bool mirror)
    {
        const double Identity = 1;
        if (!(viewWidth > 0) || !(viewHeight > 0) || !double.IsFinite(viewWidth) || !double.IsFinite(viewHeight) || !IsFinite(region) || region.IsEmpty)
            return (Identity, 0, 0);

        // Bild als Ganzes in Ansichtskoordinaten (ungezoomt).
        var image = OverlayMapper.Map(NormRect.Full, imageWidth, imageHeight, viewWidth, viewHeight, fit, mirror);
        var area = OverlayMapper.Map(region, imageWidth, imageHeight, viewWidth, viewHeight, fit, mirror);
        if (image.IsEmpty || area.IsEmpty)
            return (Identity, 0, 0);

        // Ausschnitt füllt die Ansicht, Bild bedeckt sie ohne Rand, nie verkleinern.
        var scale = Math.Max(Identity, Math.Max(viewWidth / area.Width, viewHeight / area.Height));
        scale = Math.Max(scale, Math.Max(viewWidth / image.Width, viewHeight / image.Height));

        var centerX = area.X + area.Width / 2;
        var centerY = area.Y + area.Height / 2;
        var tx = ClampCover(viewWidth / 2 - scale * centerX, scale, image.X, image.Right, viewWidth);
        var ty = ClampCover(viewHeight / 2 - scale * centerY, scale, image.Y, image.Bottom, viewHeight);
        return (scale, tx, ty);
    }

    /// <summary>Begrenzt die Verschiebung so, dass das skalierte Bild [from, to] die Ansicht [0, view] bedeckt (sonst mittig).</summary>
    private static double ClampCover(double translate, double scale, double from, double to, double view)
    {
        var low = view - scale * to;     // rechter/unterer Bildrand darf nicht in die Ansicht rutschen
        var high = -scale * from;        // linker/oberer Bildrand darf nicht in die Ansicht rutschen
        return low > high ? (low + high) / 2 : Math.Clamp(translate, low, high);
    }

    private static bool IsFinite(NormRect r) =>
        double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Width) && double.IsFinite(r.Height);
}
