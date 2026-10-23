using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;

namespace Fotobox.Views.Admin;

/// <summary>Zeichnet die einfache Testseite (Rahmen, Raster, Text, Datum) für den Testdruck. Nur auf dem UI-Thread.</summary>
public static class TestPageRenderer
{
    private const double DipPerMm = 96.0 / 25.4;

    public static BitmapSource Render(PageSpec page)
    {
        // Das Visual wird in 1/96-Zoll-Einheiten gezeichnet; RenderTargetBitmap skaliert auf die Ziel-DPI.
        var width = page.WidthMm * DipPerMm;
        var height = page.HeightMm * DipPerMm;
        var pixelsPerDip = page.Dpi / 96.0;
        var culture = CultureInfo.GetCultureInfo("de-DE");

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));

            // Raster alle 10 mm
            var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(0xD0, 0xD5, 0xDD)), 0.75);
            gridPen.Freeze();
            for (var mm = 10.0; mm * DipPerMm < width; mm += 10)
                dc.DrawLine(gridPen, new Point(mm * DipPerMm, 0), new Point(mm * DipPerMm, height));
            for (var mm = 10.0; mm * DipPerMm < height; mm += 10)
                dc.DrawLine(gridPen, new Point(0, mm * DipPerMm), new Point(width, mm * DipPerMm));

            // Rahmen 5 mm vom Rand (zeigt, ob der Drucker beschneidet) und 1 mm am Rand
            var framePen = new Pen(Brushes.Black, 3);
            framePen.Freeze();
            var inset = 5 * DipPerMm;
            dc.DrawRectangle(null, framePen, new Rect(inset, inset, width - 2 * inset, height - 2 * inset));
            var edgePen = new Pen(Brushes.Red, 1.5);
            edgePen.Freeze();
            dc.DrawRectangle(null, edgePen, new Rect(1 * DipPerMm, 1 * DipPerMm, width - 2 * DipPerMm, height - 2 * DipPerMm));

            // Diagonalen
            var diagPen = new Pen(new SolidColorBrush(Color.FromRgb(0x93, 0xA3, 0xBA)), 1);
            diagPen.Freeze();
            dc.DrawLine(diagPen, new Point(inset, inset), new Point(width - inset, height - inset));
            dc.DrawLine(diagPen, new Point(width - inset, inset), new Point(inset, height - inset));

            var typeface = new Typeface("Segoe UI");
            var scale = Math.Min(width, height) / 400.0; // Textgröße relativ zur kurzen Seite

            DrawCentered(dc, "Fotobox Testdruck", typeface, 26 * scale, FontWeights.Bold, width, height * 0.42, pixelsPerDip, culture);
            DrawCentered(dc, DateTime.Now.ToString("dd.MM.yyyy HH:mm", culture), typeface, 16 * scale, FontWeights.Normal, width, height * 0.52, pixelsPerDip, culture);
            DrawCentered(dc, string.Create(culture, $"{page.WidthMm:0.#} × {page.HeightMm:0.#} mm  ·  {page.PixelWidth} × {page.PixelHeight} px  ·  {page.Dpi} dpi"),
                typeface, 11 * scale, FontWeights.Normal, width, height * 0.58, pixelsPerDip, culture);
        }

        var bitmap = new RenderTargetBitmap(page.PixelWidth, page.PixelHeight, page.Dpi, page.Dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawCentered(DrawingContext dc, string text, Typeface typeface, double size, FontWeight weight,
        double pageWidth, double y, double pixelsPerDip, CultureInfo culture)
    {
        var formatted = new FormattedText(text, culture, FlowDirection.LeftToRight,
            new Typeface(typeface.FontFamily, FontStyles.Normal, weight, FontStretches.Normal),
            size, Brushes.Black, pixelsPerDip)
        {
            MaxTextWidth = pageWidth * 0.9,
            TextAlignment = TextAlignment.Center,
        };
        dc.DrawText(formatted, new Point(pageWidth * 0.05, y));
    }
}
