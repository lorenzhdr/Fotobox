using System.Windows;
using Fotobox.Core;
using Fotobox.Core.Fun;

namespace Fotobox.Services.Layout;

/// <summary>
/// Rechnet für die Gastansicht („Druckbereich anzeigen“) aus, welcher Teil des Kamerabilds beim Drucken zu sehen ist. Reine, UI-freie
/// Rechnung ohne eigene Geometrie: Sie ruft dieselben <see cref="LayoutMath"/>-Funktionen mit denselben Argumenten auf wie
/// <see cref="LayoutComposer"/> (Seite = <see cref="PageSpec.PixelWidth"/> × <see cref="PageSpec.PixelHeight"/>, Doppelstreifen mit
/// Text-Flags wie im Composer) und ändert am Druck nichts. Ein Test vergleicht das Ergebnis mit dem echten Composer.
/// </summary>
public static class PrintAreaPreview
{
    /// <summary>Unterschiede unterhalb dieses Anteils der Bildkante gelten als „kein Zuschnitt“.</summary>
    private const double WholeTolerance = 0.002;

    /// <summary>
    /// Wie <see cref="CropInSource(LayoutKind, StripStyle, int, bool, bool, PageSpec, double, double)"/>, normiert auf das Kamerabild
    /// (0..1, ungespiegelt, wie <see cref="OverlayMapper"/> es erwartet). <c>null</c> = kein Zuschnitt (das ganze Bild wird gedruckt).
    /// </summary>
    public static NormRect? Compute(LayoutKind kind, StripStyle style, int stripPhotoCount, bool hasCaption, bool hasDate,
        PageSpec page, double photoWidth, double photoHeight)
    {
        if (CropInSource(kind, style, stripPhotoCount, hasCaption, hasDate, page, photoWidth, photoHeight) is not { } crop)
            return null;

        return new NormRect(crop.X / photoWidth, crop.Y / photoHeight, crop.Width / photoWidth, crop.Height / photoHeight);
    }

    /// <summary>
    /// Wie <see cref="Compute(LayoutKind, StripStyle, int, bool, bool, PageSpec, double, double)"/> für die Gastwahl
    /// <paramref name="guest"/> (Streifenzahl, Bildunterschrift und Datum kommen aus <paramref name="layout"/>).
    /// </summary>
    public static NormRect? Compute(GuestLayout guest, LayoutSettings layout, PageSpec page, double photoWidth, double photoHeight)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var applied = new LayoutSettings();
        GuestLayouts.ApplyTo(applied, guest);
        return Compute(applied.Kind, applied.StripStyle, layout.StripPhotoCount, HasCaption(layout), layout.ShowDate,
            page, photoWidth, photoHeight);
    }

    /// <summary>
    /// Das Rechteck des Kamerafotos (in Quellpixeln), das der <see cref="LayoutComposer"/> in die (erste) Fotozelle zeichnet:
    /// bei Einzelfoto und Overlay die ganze Seite, beim Doppelstreifen die erste Zelle (alle Zellen haben bis auf ±1 Pixel dasselbe
    /// Format). Der Einzelstreifen zeigt die ganzen Fotos und hat keinen Zuschnitt. <c>null</c> = kein Zuschnitt oder ungültige Maße.
    /// </summary>
    public static Rect? CropInSource(LayoutKind kind, StripStyle style, int stripPhotoCount, bool hasCaption, bool hasDate,
        PageSpec page, double photoWidth, double photoHeight)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!(photoWidth > 0) || !(photoHeight > 0) || !double.IsFinite(photoWidth) || !double.IsFinite(photoHeight))
            return null;

        var pw = page.PixelWidth;
        var ph = page.PixelHeight;
        if (pw <= 0 || ph <= 0)
            return null;

        Rect crop;
        if (kind == LayoutKind.Strip)
        {
            // Einzelstreifen: ganze Fotos (Seitenverhältnis des Fotos), nichts wird beschnitten.
            if (style == StripStyle.Single)
                return null;

            var count = Math.Clamp(stripPhotoCount, SettingsLimits.StripPhotosMin, SettingsLimits.StripPhotosMax);
            var geo = LayoutMath.ComputeStrips(pw, ph, count, page.Dpi, hasCaption, hasDate);
            var cell = geo.Cells[0][0];
            crop = LayoutMath.CropToFill(photoWidth, photoHeight, cell.Width, cell.Height);
        }
        else
        {
            // Einzelfoto und Overlay: das Foto füllt die ganze Seite (Text und Overlay-PNG liegen darüber).
            crop = LayoutMath.CropToFill(photoWidth, photoHeight, pw, ph);
        }

        var isWhole = crop.Width >= photoWidth * (1 - WholeTolerance) && crop.Height >= photoHeight * (1 - WholeTolerance);
        return isWhole ? null : crop;
    }

    /// <summary>Wird im Druck eine Bildunterschrift gesetzt? Wie im Composer: nicht leer und nicht nur Leerraum.</summary>
    public static bool HasCaption(LayoutSettings layout) => !string.IsNullOrWhiteSpace(layout.CaptionText);
}
