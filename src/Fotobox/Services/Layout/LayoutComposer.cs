using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;

namespace Fotobox.Services.Layout;

/// <summary>Komponiert die Druckseite per DrawingVisual + RenderTargetBitmap in Pixelkoordinaten.</summary>
public sealed class LayoutComposer : ILayoutComposer
{
    private const string DateFormat = "dd.MM.yyyy";

    // Schriftgrößen im Streifen-Fußbereich (Em-Höhe in mm); passen zu LayoutMath.CaptionBlockMm/DateBlockMm.
    private const double CaptionEmMm = 3.8;
    private const double DateEmMm = 2.9;

    // Bildunterschrift darf auf bis zu 2 Zeilen umbrechen, erst danach wird verkleinert (bis 40 %) und gekürzt.
    private const int CaptionMaxLines = 2;
    private const double MinTextScale = 0.40;

    private static readonly Brush White = Frozen(new SolidColorBrush(Colors.White));
    private static readonly Brush StripTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)));
    private static readonly Brush CutBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC4, 0xC4, 0xC4)));

    private static readonly Brush ShadeBrush = Frozen(new LinearGradientBrush(
        [new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Color.FromArgb(185, 0, 0, 0), 1)],
        new Point(0, 0), new Point(0, 1)));

    private static readonly ConcurrentDictionary<string, Typeface> Faces = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<IReadOnlyDictionary<string, string>> SystemFamilies =
        new(LoadSystemFamilies, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly object _overlayGate = new();
    private string? _overlayPath;
    private DateTime _overlayStamp;
    private BitmapSource? _overlayBitmap;

    public int GetRequiredPhotoCount(LayoutSettings layout) =>
        layout.Kind == LayoutKind.Strip ? Math.Clamp(layout.StripPhotoCount, 2, 4) : 1;

    public BitmapSource Compose(IReadOnlyList<BitmapSource> photos, LayoutSettings layout, PageSpec page, string overlayDirectory, DateTime timestamp)
    {
        if (photos is null || photos.Count == 0)
            throw new ArgumentException("Es wird mindestens ein Foto benötigt.", nameof(photos));
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(page);

        var pw = page.PixelWidth;
        var ph = page.PixelHeight;
        if (pw <= 0 || ph <= 0)
            throw new ArgumentException("Ungültige Seitengröße.", nameof(page));

        var caption = string.IsNullOrWhiteSpace(layout.CaptionText) ? null : layout.CaptionText.Trim();
        var date = layout.ShowDate ? timestamp.ToString(DateFormat, CultureInfo.InvariantCulture) : null;
        var pageRect = new Rect(0, 0, pw, ph);
        var face = ResolveFace(layout.CaptionFontFamily);

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            switch (layout.Kind)
            {
                case LayoutKind.Strip:
                    DrawStrips(dc, photos, layout.StripStyle, GetRequiredPhotoCount(layout), pw, ph, caption, date, page.Dpi, face);
                    break;

                case LayoutKind.Overlay:
                    dc.DrawRectangle(Brushes.Black, null, pageRect);
                    DrawPhotoFill(dc, photos[0], pageRect);
                    var overlay = LoadOverlay(overlayDirectory, layout.OverlayFileName);
                    if (overlay is not null)
                        dc.DrawImage(overlay, pageRect);
                    DrawCaptionBand(dc, pw, ph, caption, date, page.Dpi, face);
                    break;

                default:
                    dc.DrawRectangle(Brushes.Black, null, pageRect);
                    DrawPhotoFill(dc, photos[0], pageRect);
                    DrawCaptionBand(dc, pw, ph, caption, date, page.Dpi, face);
                    break;
            }
        }

        var target = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        var converted = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
        var stride = pw * 4;
        var pixels = new byte[stride * ph];
        converted.CopyPixels(pixels, stride, 0);

        var result = BitmapSource.Create(pw, ph, page.Dpi, page.Dpi, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    /// <summary>
    /// Würde die Bildunterschrift bei diesem Layout und dieser Seite gekürzt („…“)? Rechnet mit denselben Maßen wie
    /// <see cref="Compose"/> (bei Einzelstreifen mit 3:2-Fotos, die Textbreite hängt davon nicht ab).
    /// </summary>
    public static bool IsCaptionTruncated(LayoutSettings layout, PageSpec page)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(page);

        var caption = string.IsNullOrWhiteSpace(layout.CaptionText) ? null : layout.CaptionText.Trim();
        if (caption is null)
            return false;

        var pw = page.PixelWidth;
        var ph = page.PixelHeight;
        var date = layout.ShowDate ? "01.01.2026" : null;
        var face = ResolveFace(layout.CaptionFontFamily);

        List<FittedText> texts;
        if (layout.Kind == LayoutKind.Strip)
        {
            var count = Math.Clamp(layout.StripPhotoCount, 2, 4);
            var geo = BuildStripGeometry(layout.StripStyle, count, pw, ph, caption, date, page.Dpi, Enumerable.Repeat(1.5, count).ToList());
            texts = LayoutFooterTexts(geo.Footers[0], caption, date, face, page.Dpi, StripTextBrush);
        }
        else
        {
            texts = LayoutBandTexts(pw, ph, caption, date, face, page.Dpi);
        }

        return texts[0].Truncated;
    }

    // ---------- Schrift ----------

    /// <summary>
    /// Typeface (SemiBold) der gewünschten Familie. Ist sie nicht installiert, wird auf Segoe UI zurückgefallen;
    /// das Ergebnis wird pro Familienname gemerkt.
    /// </summary>
    public static Typeface ResolveFace(string? family)
    {
        var name = string.IsNullOrWhiteSpace(family) ? LayoutSettings.DefaultCaptionFont : family.Trim();
        return Faces.GetOrAdd(name, static n =>
        {
            var source = SystemFamilies.Value.TryGetValue(n, out var found) ? found : LayoutSettings.DefaultCaptionFont;
            return new Typeface(new FontFamily(source), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        });
    }

    private static IReadOnlyDictionary<string, string> LoadSystemFamilies()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var family in Fonts.SystemFontFamilies)
            {
                map[family.Source] = family.Source;
                foreach (var localized in family.FamilyNames.Values)
                    map.TryAdd(localized, family.Source);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Systemschriften konnten nicht gelesen werden – es wird Segoe UI verwendet.", ex);
        }

        return map;
    }

    // ---------- Zeichnen ----------

    /// <summary>Zeichnet das Foto crop-to-fill in die (ganzzahlig ausgerichtete) Zelle.</summary>
    private static void DrawPhotoFill(DrawingContext dc, BitmapSource photo, Rect cell)
    {
        var src = LayoutMath.CropToFill(photo.PixelWidth, photo.PixelHeight, cell.Width, cell.Height);
        if (src.Width <= 0 || src.Height <= 0)
            return;

        var scale = cell.Width / src.Width;
        var dest = new Rect(
            cell.X - src.X * scale,
            cell.Y - src.Y * scale,
            photo.PixelWidth * scale,
            photo.PixelHeight * scale);

        dc.PushClip(new RectangleGeometry(cell));
        dc.DrawImage(photo, dest);
        dc.Pop();
    }

    private static StripGeometry BuildStripGeometry(StripStyle style, int count, int pw, int ph, string? caption, string? date,
        int dpi, IReadOnlyList<double> aspects) =>
        style == StripStyle.Single
            ? LayoutMath.ComputeSingleStrip(pw, ph, aspects, caption is not null, date is not null, dpi)
            : LayoutMath.ComputeStrips(pw, ph, count, caption is not null || date is not null, dpi, caption is not null, date is not null);

    private static void DrawStrips(DrawingContext dc, IReadOnlyList<BitmapSource> photos, StripStyle style, int count, int pw, int ph,
        string? caption, string? date, int dpi, Typeface face)
    {
        var aspects = Enumerable.Range(0, count)
            .Select(i => photos[i % photos.Count])
            .Select(p => p.PixelHeight > 0 ? (double)p.PixelWidth / p.PixelHeight : 1.5)
            .ToList();
        var geo = BuildStripGeometry(style, count, pw, ph, caption, date, dpi, aspects);

        for (var s = 0; s < geo.Strips.Count; s++)
        {
            dc.DrawRectangle(White, null, geo.Strips[s]);
            for (var i = 0; i < geo.Cells[s].Count; i++)
                DrawPhotoFill(dc, photos[i % photos.Count], geo.Cells[s][i]);
            DrawFooter(dc, geo.Footers[s], caption, date, face, dpi);
        }

        if (style == StripStyle.Single)
            return;

        // Dezente, gestrichelte Schnittlinie zwischen den Streifen.
        var thickness = Math.Max(1, Math.Round(dpi / 150.0));
        var off = thickness % 2 == 1 ? 0.5 : 0;
        var pen = new Pen(CutBrush, thickness) { DashStyle = new DashStyle([5, 5], 0) };
        pen.Freeze();
        var landscape = pw > ph;
        dc.DrawLine(pen,
            new Point(geo.CutStart.X + (landscape ? 0 : off), geo.CutStart.Y + (landscape ? off : 0)),
            new Point(geo.CutEnd.X + (landscape ? 0 : off), geo.CutEnd.Y + (landscape ? off : 0)));
    }

    /// <summary>Text im Fußbereich (Textrechteck liegt bereits in der Sicherheitszone): zentriert, dunkelgrau.</summary>
    private static List<FittedText> LayoutFooterTexts(Rect footer, string? caption, string? date, Typeface face, int dpi, Brush brush)
    {
        var texts = new List<FittedText>();
        if ((caption is null && date is null) || footer.Height < 4)
            return texts;

        // Auf sehr kleinen Seiten ist das Rechteck kleiner als der Nennblock: Schriftgrößen entsprechend verkleinern.
        var nominal = LayoutMath.TextBlockHeight(caption is not null, date is not null, dpi);
        var k = nominal > 0 ? Math.Min(1.0, footer.Height / nominal) : 1.0;

        if (caption is not null)
        {
            texts.Add(FitText(caption, face, LayoutMath.MmToPx(CaptionEmMm, dpi) * k, brush, footer.Width,
                CaptionMaxLines, LayoutMath.MmToPx(LayoutMath.CaptionBlockMm, dpi) * k));
        }
        if (date is not null)
        {
            texts.Add(FitText(date, face, LayoutMath.MmToPx(DateEmMm, dpi) * k, brush, footer.Width,
                1, LayoutMath.MmToPx(LayoutMath.DateBlockMm, dpi) * k));
        }

        return texts;
    }

    private static void DrawFooter(DrawingContext dc, Rect footer, string? caption, string? date, Typeface face, int dpi)
    {
        var texts = LayoutFooterTexts(footer, caption, date, face, dpi, StripTextBrush);
        if (texts.Count == 0)
            return;

        var gap = texts.Count > 1 ? LayoutMath.MmToPx(LayoutMath.TextGapMm, dpi) : 0;
        var total = texts.Sum(t => t.Text.Height) + gap;
        var y = footer.Y + Math.Max(0, (footer.Height - total) / 2);
        if (y + total > footer.Bottom)
            y = footer.Bottom - total;
        foreach (var t in texts)
        {
            dc.DrawText(t.Text, new Point(footer.X, y));
            y += t.Text.Height + gap;
        }
    }

    private static List<FittedText> LayoutBandTexts(int pw, int ph, string? caption, string? date, Typeface face, int dpi)
    {
        var both = caption is not null && date is not null;
        var maxWidth = Math.Min(pw * 0.88, pw - 2.0 * LayoutMath.MmToPx(LayoutMath.SafeSideMm, dpi));
        var texts = new List<FittedText>();
        if (caption is not null)
        {
            var size = ph * 0.035;
            texts.Add(FitText(caption, face, size, Brushes.White, maxWidth, CaptionMaxLines, size * 2 * 1.8));
        }
        if (date is not null)
        {
            var size = ph * (both ? 0.024 : 0.032);
            texts.Add(FitText(date, face, size, Brushes.White, maxWidth, 1, size * 1.8));
        }

        return texts;
    }

    /// <summary>Dunkler Verlauf am unteren Seitenrand mit weißem, zentriertem Text (Caption und/oder Datum).</summary>
    private static void DrawCaptionBand(DrawingContext dc, int pw, int ph, string? caption, string? date, int dpi, Typeface face)
    {
        if (caption is null && date is null)
            return;

        var texts = LayoutBandTexts(pw, ph, caption, date, face, dpi);
        var maxWidth = texts.Max(t => t.Text.MaxTextWidth);

        var total = texts.Sum(t => t.Text.Height);
        // Text nie näher als die Sicherheitszone an der Unterkante.
        var pad = Math.Max(ph * 0.035, LayoutMath.MmToPx(LayoutMath.SafeBottomMm, dpi));
        var bandHeight = Math.Min(ph, total + pad * 2 + ph * 0.06);
        dc.DrawRectangle(ShadeBrush, null, new Rect(0, ph - bandHeight, pw, bandHeight));

        var y = ph - pad - total;
        foreach (var t in texts)
        {
            dc.DrawText(t.Text, new Point((pw - maxWidth) / 2, y));
            y += t.Text.Height;
        }
    }

    private sealed record FittedText(FormattedText Text, bool Truncated);

    /// <summary>
    /// Zentrierter Text, der auf bis zu <paramref name="maxLines"/> Zeilen umbricht. Passt er in Breite und Höhe nicht,
    /// wird schrittweise verkleinert (bis <see cref="MinTextScale"/>), erst danach mit „…“ gekürzt.
    /// </summary>
    private static FittedText FitText(string text, Typeface face, double size, Brush brush, double maxWidth, int maxLines, double maxHeight)
    {
        size = Math.Max(size, 4);
        maxWidth = Math.Max(1, maxWidth);
        var min = size * MinTextScale;
        var current = size;

        while (true)
        {
            var probe = Create(current);
            var lineHeight = Create(current, "Ag").Height;
            var lines = lineHeight > 0 ? (int)Math.Round(probe.Height / lineHeight) : 1;
            if (lines <= maxLines && probe.Height <= maxHeight + 0.5)
                return new FittedText(probe, false);
            if (current <= min)
                break;
            current = Math.Max(min, current * 0.92);
        }

        var ft = Create(current);
        ft.MaxLineCount = maxLines;
        ft.Trimming = TextTrimming.CharacterEllipsis;
        return new FittedText(ft, true);

        FormattedText Create(double s, string? t = null) => new(t ?? text, CultureInfo.GetCultureInfo("de-DE"),
            FlowDirection.LeftToRight, face, s, brush, 1.0)
        {
            TextAlignment = TextAlignment.Center,
            MaxTextWidth = maxWidth,
        };
    }

    // ---------- Overlay ----------

    private BitmapSource? LoadOverlay(string overlayDirectory, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            AppLog.Warn("Overlay-Layout gewählt, aber keine Overlay-Datei konfiguriert – Seite wird ohne Overlay erzeugt.");
            return null;
        }

        try
        {
            var path = Path.Combine(overlayDirectory, Path.GetFileName(fileName));
            if (!File.Exists(path))
            {
                AppLog.Warn($"Overlay-Datei nicht gefunden: {path} – Seite wird ohne Overlay erzeugt.");
                return null;
            }

            var stamp = File.GetLastWriteTimeUtc(path);
            lock (_overlayGate)
            {
                if (_overlayBitmap is not null && _overlayPath == path && _overlayStamp == stamp)
                    return _overlayBitmap;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                _overlayPath = path;
                _overlayStamp = stamp;
                _overlayBitmap = bitmap;
                return bitmap;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Overlay-Datei konnte nicht geladen werden ({fileName}) – Seite wird ohne Overlay erzeugt.", ex);
            return null;
        }
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
