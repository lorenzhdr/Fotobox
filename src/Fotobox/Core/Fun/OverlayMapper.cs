namespace Fotobox.Core.Fun;

/// <summary>Wie das Kamerabild in die Ansicht passt: ganz sichtbar (Ränder) oder füllend (beschnitten).</summary>
public enum OverlayFit { Uniform, UniformToFill }

/// <summary>Rechteck in Ansichtskoordinaten (Pixel bzw. DIPs), Ursprung oben links.</summary>
public readonly record struct ViewRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// Rechnet normierte Erkennungsboxen (ungespiegeltes Kamerabild) in Koordinaten der Gastansicht um, wo das Livebild
/// zentriert skaliert (und ggf. gespiegelt) wird.
/// </summary>
public static class OverlayMapper
{
    /// <summary>Seitenverhältnis, wenn die Kameraauflösung noch unbekannt ist.</summary>
    public const double DefaultAspect = 16.0 / 9;

    /// <summary>
    /// Box → Ansicht. Ist die Bildgröße unbekannt (≤ 0), gilt <see cref="DefaultAspect"/>; ist die Ansicht ungültig (≤ 0),
    /// kommt ein leeres Rechteck zurück.
    /// </summary>
    public static ViewRect Map(NormRect box, double imageW, double imageH, double viewW, double viewH, OverlayFit fit, bool mirror)
    {
        if (!IsValid(viewW) || !IsValid(viewH))
            return default;

        (imageW, imageH) = ImageSizeOrDefault(imageW, imageH);
        var scale = Scale(imageW, imageH, viewW, viewH, fit);
        var drawW = imageW * scale;
        var drawH = imageH * scale;
        var offX = (viewW - drawW) / 2;
        var offY = (viewH - drawH) / 2;

        // Spiegeln im normierten Bild: x' = 1 − rechter Rand.
        var nx = mirror ? 1 - box.Right : box.X;
        return new ViewRect(offX + nx * drawW, offY + box.Y * drawH, box.Width * drawW, box.Height * drawH);
    }

    /// <summary>Wie <see cref="Map(NormRect, double, double, double, double, OverlayFit, bool)"/> mit unbekannter Kameraauflösung (16:9).</summary>
    public static ViewRect Map(NormRect box, double viewW, double viewH, OverlayFit fit, bool mirror) =>
        Map(box, 0, 0, viewW, viewH, fit, mirror);

    /// <summary>
    /// Welcher Teil des (ungespiegelten) Bildes in der Ansicht sichtbar ist: bei <see cref="OverlayFit.Uniform"/> alles,
    /// bei <see cref="OverlayFit.UniformToFill"/> der mittlere, nicht beschnittene Teil. Spiegelsymmetrisch, gilt also auch
    /// für das gespiegelte Bild. Ungültige Ansicht (≤ 0): ganzes Bild (es gibt nichts zu beschneiden).
    /// </summary>
    public static NormRect VisibleRegion(double imageW, double imageH, double viewW, double viewH, OverlayFit fit)
    {
        if (fit == OverlayFit.Uniform || !IsValid(viewW) || !IsValid(viewH))
            return NormRect.Full;

        (imageW, imageH) = ImageSizeOrDefault(imageW, imageH);
        var scale = Scale(imageW, imageH, viewW, viewH, fit);
        var visibleW = Math.Min(1, viewW / (imageW * scale));
        var visibleH = Math.Min(1, viewH / (imageH * scale));
        return new NormRect((1 - visibleW) / 2, (1 - visibleH) / 2, visibleW, visibleH);
    }

    /// <summary><see cref="VisibleRegion(double, double, double, double, OverlayFit)"/> mit unbekannter Kameraauflösung (16:9).</summary>
    public static NormRect VisibleRegion(double viewW, double viewH, OverlayFit fit) =>
        VisibleRegion(0, 0, viewW, viewH, fit);

    private static double Scale(double imageW, double imageH, double viewW, double viewH, OverlayFit fit)
    {
        var sx = viewW / imageW;
        var sy = viewH / imageH;
        return fit == OverlayFit.Uniform ? Math.Min(sx, sy) : Math.Max(sx, sy);
    }

    private static (double W, double H) ImageSizeOrDefault(double imageW, double imageH) =>
        IsValid(imageW) && IsValid(imageH) ? (imageW, imageH) : (DefaultAspect * 1000, 1000);

    private static bool IsValid(double value) => value > 0 && double.IsFinite(value);
}
