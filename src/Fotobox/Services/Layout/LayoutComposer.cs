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

    private static readonly Typeface CaptionFace =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static readonly Brush White = Frozen(new SolidColorBrush(Colors.White));
    private static readonly Brush StripTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)));
    private static readonly Brush CutBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC4, 0xC4, 0xC4)));

    private static readonly Brush ShadeBrush = Frozen(new LinearGradientBrush(
        [new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Color.FromArgb(185, 0, 0, 0), 1)],
        new Point(0, 0), new Point(0, 1)));

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

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            switch (layout.Kind)
            {
                case LayoutKind.Strip:
                    DrawStrips(dc, photos, GetRequiredPhotoCount(layout), pw, ph, caption, date, page.Dpi);
                    break;

                case LayoutKind.Overlay:
                    dc.DrawRectangle(Brushes.Black, null, pageRect);
                    DrawPhotoFill(dc, photos[0], pageRect);
                    var overlay = LoadOverlay(overlayDirectory, layout.OverlayFileName);
                    if (overlay is not null)
                        dc.DrawImage(overlay, pageRect);
                    DrawCaptionBand(dc, pw, ph, caption, date);
                    break;

                default:
                    dc.DrawRectangle(Brushes.Black, null, pageRect);
                    DrawPhotoFill(dc, photos[0], pageRect);
                    DrawCaptionBand(dc, pw, ph, caption, date);
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

    private static void DrawStrips(DrawingContext dc, IReadOnlyList<BitmapSource> photos, int count, int pw, int ph,
        string? caption, string? date, int dpi)
    {
        var geo = LayoutMath.ComputeStrips(pw, ph, count, caption is not null || date is not null);

        for (var s = 0; s < geo.Strips.Count; s++)
        {
            dc.DrawRectangle(White, null, geo.Strips[s]);
            for (var i = 0; i < geo.Cells[s].Count; i++)
                DrawPhotoFill(dc, photos[i % photos.Count], geo.Cells[s][i]);
            DrawFooter(dc, geo.Footers[s], caption, date);
        }

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

    /// <summary>Zentrierter, dunkelgrauer Text im Fußbereich eines Streifens.</summary>
    private static void DrawFooter(DrawingContext dc, Rect footer, string? caption, string? date)
    {
        if ((caption is null && date is null) || footer.Height < 4)
            return;

        var both = caption is not null && date is not null;
        var maxWidth = Math.Max(1, footer.Width * 0.92);
        var texts = new List<FormattedText>();
        if (caption is not null)
            texts.Add(MakeText(caption, footer.Height * (both ? 0.40 : 0.44), StripTextBrush, maxWidth));
        if (date is not null)
            texts.Add(MakeText(date, footer.Height * (both ? 0.30 : 0.44), StripTextBrush, maxWidth));

        var total = texts.Sum(t => t.Height);
        var y = footer.Y + (footer.Height - total) / 2;
        foreach (var t in texts)
        {
            dc.DrawText(t, new Point(footer.X + (footer.Width - maxWidth) / 2, y));
            y += t.Height;
        }
    }

    /// <summary>Dunkler Verlauf am unteren Seitenrand mit weißem, zentriertem Text (Caption und/oder Datum).</summary>
    private static void DrawCaptionBand(DrawingContext dc, int pw, int ph, string? caption, string? date)
    {
        if (caption is null && date is null)
            return;

        var both = caption is not null && date is not null;
        var maxWidth = pw * 0.88;
        var texts = new List<FormattedText>();
        if (caption is not null)
            texts.Add(MakeText(caption, ph * 0.035, Brushes.White, maxWidth));
        if (date is not null)
            texts.Add(MakeText(date, ph * (both ? 0.024 : 0.032), Brushes.White, maxWidth));

        var total = texts.Sum(t => t.Height);
        var pad = ph * 0.035;
        var bandHeight = Math.Min(ph, total + pad * 2 + ph * 0.06);
        dc.DrawRectangle(ShadeBrush, null, new Rect(0, ph - bandHeight, pw, bandHeight));

        var y = ph - pad - total;
        foreach (var t in texts)
        {
            dc.DrawText(t, new Point((pw - maxWidth) / 2, y));
            y += t.Height;
        }
    }

    /// <summary>Einzeiliger, zentrierter Text; wird bei Überbreite verkleinert (min. 55 %), dann gekürzt.</summary>
    private static FormattedText MakeText(string text, double size, Brush brush, double maxWidth)
    {
        size = Math.Max(size, 4);
        var ft = Create(size);
        if (ft.Width > maxWidth)
        {
            size = Math.Max(size * 0.55, size * maxWidth / ft.Width);
            ft = Create(size);
        }

        ft.TextAlignment = TextAlignment.Center;
        ft.MaxTextWidth = maxWidth;
        ft.MaxLineCount = 1;
        ft.Trimming = TextTrimming.CharacterEllipsis;
        return ft;

        FormattedText Create(double s) => new(text, CultureInfo.GetCultureInfo("de-DE"),
            FlowDirection.LeftToRight, CaptionFace, s, brush, 1.0);
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
