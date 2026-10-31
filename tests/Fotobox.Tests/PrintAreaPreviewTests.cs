using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Services.Layout;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Rechnung des Druckbereichs (<see cref="PrintAreaPreview"/>): Formeln je Layout, Sonderfälle und Standardwerte.</summary>
public class PrintAreaPreviewMathTests
{
    private static readonly PageSpec Portrait = new(101.6, 152.4, 300);       // 1200 × 1800
    private static readonly PageSpec Landscape = Portrait.ToOrientation(true); // 1800 × 1200

    private static void AssertRect(NormRect expected, NormRect? actual, double tolerance = 1e-9)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.X, actual!.Value.X, tolerance);
        Assert.Equal(expected.Y, actual.Value.Y, tolerance);
        Assert.Equal(expected.Width, actual.Value.Width, tolerance);
        Assert.Equal(expected.Height, actual.Value.Height, tolerance);
    }

    [Theory]
    [InlineData(LayoutKind.SingleBorderless)]
    [InlineData(LayoutKind.Overlay)]
    public void SingleAndOverlay_WidePhotoOnPortraitPage_CropsSidesCentered(LayoutKind kind)
    {
        // 1920 × 1080 auf 2:3: sichtbar ist die mittlere Breite 1080 · 2/3 = 720 von 1920.
        var rect = PrintAreaPreview.Compute(kind, StripStyle.DoubleCut, 3, false, false, Portrait, 1920, 1080);

        AssertRect(new NormRect(600.0 / 1920, 0, 720.0 / 1920, 1), rect);
    }

    [Fact]
    public void Single_WidePhotoOnLandscapePage_CropsSides()
    {
        // 16:9 auf 3:2: Breite 1080 · 1,5 = 1620.
        var rect = PrintAreaPreview.Compute(LayoutKind.SingleBorderless, StripStyle.DoubleCut, 3, true, true, Landscape, 1920, 1080);

        AssertRect(new NormRect(150.0 / 1920, 0, 1620.0 / 1920, 1), rect);
    }

    [Fact]
    public void Single_TallPhotoOnLandscapePage_CropsTopAndBottom()
    {
        // 900 × 1600 auf 3:2: Höhe 900 / 1,5 = 600 von 1600.
        var rect = PrintAreaPreview.Compute(LayoutKind.SingleBorderless, StripStyle.DoubleCut, 3, false, false, Landscape, 900, 1600);

        AssertRect(new NormRect(0, 500.0 / 1600, 1, 600.0 / 1600), rect);
    }

    [Fact]
    public void Single_PhotoWithPageAspect_HasNoCrop()
    {
        Assert.Null(PrintAreaPreview.Compute(LayoutKind.SingleBorderless, StripStyle.DoubleCut, 3, false, false, Landscape, 1500, 1000));
        Assert.Null(PrintAreaPreview.Compute(LayoutKind.Overlay, StripStyle.DoubleCut, 3, false, false, Portrait, 1000, 1500));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void StripSingle_WholePhotos_HasNoCrop(int count)
    {
        Assert.Null(PrintAreaPreview.Compute(LayoutKind.Strip, StripStyle.Single, count, true, true, Portrait, 1920, 1080));
        Assert.Null(PrintAreaPreview.Compute(LayoutKind.Strip, StripStyle.Single, count, false, false, Landscape, 1080, 1920));
    }

    [Theory]
    [InlineData(2, false, false)]
    [InlineData(3, true, false)]
    [InlineData(4, false, true)]
    [InlineData(3, true, true)]
    public void StripDoubleCut_UsesFirstCellOfTheRealGeometry(int count, bool caption, bool date)
    {
        foreach (var page in new[] { Portrait, Landscape })
        {
            var geo = LayoutMath.ComputeStrips(page.PixelWidth, page.PixelHeight, count, page.Dpi, caption, date);
            var cell = geo.Cells[0][0];
            var expected = LayoutMath.CropToFill(1920, 1080, cell.Width, cell.Height);

            var rect = PrintAreaPreview.Compute(LayoutKind.Strip, StripStyle.DoubleCut, count, caption, date, page, 1920, 1080);

            AssertRect(new NormRect(expected.X / 1920, expected.Y / 1080, expected.Width / 1920, expected.Height / 1080), rect);
        }
    }

    [Fact]
    public void StripDoubleCut_TextChangesTheCellHeight_AndTherefore_TheCrop()
    {
        var plain = PrintAreaPreview.Compute(LayoutKind.Strip, StripStyle.DoubleCut, 3, false, false, Portrait, 1920, 1080);
        var withText = PrintAreaPreview.Compute(LayoutKind.Strip, StripStyle.DoubleCut, 3, true, true, Portrait, 1920, 1080);

        Assert.NotNull(plain);
        Assert.NotNull(withText);
        Assert.NotEqual(plain!.Value.Width, withText!.Value.Width, 3);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Hochzeit", true)]
    public void GuestOverload_TakesCaptionLikeTheComposer_WhitespaceIsNoCaption(string? text, bool hasCaption)
    {
        var layout = new LayoutSettings { CaptionText = text, ShowDate = false, StripPhotoCount = 3 };

        var viaGuest = PrintAreaPreview.Compute(GuestLayout.StripDouble, layout, Portrait, 1920, 1080);
        var direct = PrintAreaPreview.Compute(LayoutKind.Strip, StripStyle.DoubleCut, 3, hasCaption, false, Portrait, 1920, 1080);

        Assert.Equal(direct, viaGuest);
        Assert.Equal(hasCaption, PrintAreaPreview.HasCaption(layout));
    }

    [Theory]
    [InlineData(GuestLayout.Single, LayoutKind.SingleBorderless, StripStyle.DoubleCut)]
    [InlineData(GuestLayout.Overlay, LayoutKind.Overlay, StripStyle.DoubleCut)]
    [InlineData(GuestLayout.StripDouble, LayoutKind.Strip, StripStyle.DoubleCut)]
    [InlineData(GuestLayout.StripSingle, LayoutKind.Strip, StripStyle.Single)]
    public void GuestOverload_MapsGuestLayoutToKindAndStyle(GuestLayout guest, LayoutKind kind, StripStyle style)
    {
        var layout = new LayoutSettings { StripPhotoCount = 4, ShowDate = true, CaptionText = "x" };

        Assert.Equal(PrintAreaPreview.Compute(kind, style, 4, true, true, Portrait, 1920, 1080),
            PrintAreaPreview.Compute(guest, layout, Portrait, 1920, 1080));
    }

    [Fact]
    public void StripPhotoCount_IsClampedLikeTheComposer()
    {
        Assert.Equal(PrintAreaPreview.Compute(LayoutKind.Strip, StripStyle.DoubleCut, 4, false, false, Portrait, 1920, 1080),
            PrintAreaPreview.Compute(LayoutKind.Strip, StripStyle.DoubleCut, 99, false, false, Portrait, 1920, 1080));
        Assert.Equal(PrintAreaPreview.Compute(LayoutKind.Strip, StripStyle.DoubleCut, 2, false, false, Portrait, 1920, 1080),
            PrintAreaPreview.Compute(LayoutKind.Strip, StripStyle.DoubleCut, 0, false, false, Portrait, 1920, 1080));
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(-1, -1)]
    [InlineData(double.NaN, 1080)]
    [InlineData(double.PositiveInfinity, 1080)]
    public void InvalidPhotoSize_GivesNoRect(double w, double h) =>
        Assert.Null(PrintAreaPreview.Compute(LayoutKind.SingleBorderless, StripStyle.DoubleCut, 3, false, false, Portrait, w, h));

    [Fact]
    public void DefaultPage_TenByFifteen_IsUsableAsFallback()
    {
        var rect = PrintAreaPreview.Compute(LayoutKind.SingleBorderless, StripStyle.DoubleCut, 3, false, false, PageSpec.Default, 1920, 1080);

        AssertRect(new NormRect(600.0 / 1920, 0, 720.0 / 1920, 1), rect, 1e-3);
    }
}

/// <summary>
/// Abgleich mit dem echten <see cref="LayoutComposer"/>: Ein Testfoto mit Verlauf (Rot wächst von links nach rechts bzw. Blau von oben
/// nach unten, sonst 0) wird komponiert; aus dem Verlauf in der ersten Fotozelle des Ergebnisbilds lässt sich ablesen, welcher
/// Ausschnitt der Composer tatsächlich gezeichnet hat. Das vergleicht die Vorhersage mit den echten Pixeln, unabhängig von
/// <see cref="LayoutMath"/>.
/// </summary>
[Collection(WpfCollection.Name)]
public class PrintAreaPreviewComposerMatchTests : IDisposable
{
    private static readonly PageSpec Portrait = new(101.6, 152.4, 150);        // 600 × 900
    private static readonly PageSpec Landscape = Portrait.ToOrientation(true);  // 900 × 600

    /// <summary>Zulässige Abweichung (Anteil der Bildkante): Verlauf mit 8 Bit, Extrapolation auf den Zellrand, Skalierung.</summary>
    private const double Tolerance = 0.015;

    private readonly LayoutComposer _composer = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-printarea-" + Guid.NewGuid().ToString("N"));

    public PrintAreaPreviewComposerMatchTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* Aufräumen ist best effort */ }
    }

    public static IEnumerable<object[]> StripCases()
    {
        foreach (var landscape in new[] { false, true })
        foreach (var count in new[] { 2, 3, 4 })
        foreach (var (caption, date) in new[] { (false, false), (true, false), (false, true), (true, true) })
        foreach (var horizontal in new[] { true, false })
            yield return [landscape, count, caption, date, horizontal];
    }

    public static IEnumerable<object[]> SingleCases()
    {
        foreach (var kind in new[] { LayoutKind.SingleBorderless, LayoutKind.Overlay })
        foreach (var landscape in new[] { false, true })
        foreach (var caption in new[] { false, true })
        foreach (var horizontal in new[] { true, false })
            yield return [kind, landscape, caption, horizontal];
    }

    [StaTheory]
    [MemberData(nameof(StripCases))]
    public void StripDoubleCut_PredictionMatchesComposedFirstCell(bool landscape, int count, bool caption, bool date, bool horizontal)
    {
        var page = landscape ? Landscape : Portrait;
        var layout = new LayoutSettings
        {
            Kind = LayoutKind.Strip, StripStyle = StripStyle.DoubleCut, StripPhotoCount = count,
            CaptionText = caption ? "Test" : null, ShowDate = date,
        };

        AssertMatches(layout, page, horizontal);
    }

    [StaTheory]
    [MemberData(nameof(SingleCases))]
    public void SingleAndOverlay_PredictionMatchesComposedPage(LayoutKind kind, bool landscape, bool caption, bool horizontal)
    {
        var page = landscape ? Landscape : Portrait;
        var layout = new LayoutSettings { Kind = kind, CaptionText = caption ? "Test" : null, ShowDate = caption };

        AssertMatches(layout, page, horizontal);
    }

    private void AssertMatches(LayoutSettings layout, PageSpec page, bool horizontal)
    {
        // Querformatfoto (16:9) mit waagerechtem Verlauf bzw. Hochformatfoto (9:16) mit senkrechtem Verlauf: Der Verlauf liegt
        // auf der Achse, die beim Zuschnitt typischerweise beschnitten wird.
        var (pw, ph) = horizontal ? (1600, 900) : (900, 1600);
        var photo = GradientPhoto(pw, ph, horizontal);
        var count = _composer.GetRequiredPhotoCount(layout);
        var composed = _composer.Compose(Enumerable.Repeat<BitmapSource>(photo, count).ToList(), layout, page, _dir,
            new DateTime(2026, 10, 23, 18, 30, 0));

        var predicted = PrintAreaPreview.Compute(layout.Kind, layout.StripStyle, layout.StripPhotoCount,
            PrintAreaPreview.HasCaption(layout), layout.ShowDate, page, pw, ph);
        var (expectedStart, expectedEnd) = predicted is { } r
            ? (horizontal ? (r.X, r.Right) : (r.Y, r.Bottom))
            : (0.0, 1.0);

        var (start, end) = MeasureFirstCell(composed, horizontal);

        Assert.InRange(start, expectedStart - Tolerance, expectedStart + Tolerance);
        Assert.InRange(end, expectedEnd - Tolerance, expectedEnd + Tolerance);
    }

    /// <summary>Testfoto: Rot (waagerecht) bzw. Blau (senkrecht) wächst von 0 auf 255, die übrigen Kanäle bleiben 0.</summary>
    private static BitmapSource GradientPhoto(int w, int h, bool horizontal)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            if (horizontal)
                px[i + 2] = (byte)Math.Round(x * 255.0 / (w - 1));
            else
                px[i] = (byte)Math.Round(y * 255.0 / (h - 1));
            px[i + 3] = 255;
        }

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Findet die erste Fotozelle (das zuerst gefundene „Foto-Pixel“ in Zeilenreihenfolge; Weiß der Streifen, grauer Text und Schnittlinie
    /// zählen nicht) und liest den Anfang und das Ende des Verlaufs am Zellrand ab (Anteil 0..1 des Fotos auf dieser Achse).
    /// </summary>
    private static (double Start, double End) MeasureFirstCell(BitmapSource image, bool horizontal)
    {
        var w = image.PixelWidth;
        var h = image.PixelHeight;
        var px = new byte[w * h * 4];
        image.CopyPixels(px, w * 4, 0);

        bool IsPhoto(int x, int y)
        {
            var i = (y * w + x) * 4;
            // Waagerechter Verlauf: Blau und Grün 0; senkrechter: Rot und Grün 0 (grauer Text hat überall mindestens 58).
            return horizontal ? px[i + 1] <= 3 && px[i] <= 3 : px[i + 1] <= 3 && px[i + 2] <= 3;
        }

        int x0 = -1, y0 = -1;
        for (var y = 0; y < h && x0 < 0; y++)
        for (var x = 0; x < w; x++)
        {
            if (IsPhoto(x, y))
            {
                x0 = x;
                y0 = y;
                break;
            }
        }
        Assert.True(x0 >= 0, "Im komponierten Bild wurde kein Foto gefunden.");

        var xr = x0;
        while (xr + 1 < w && IsPhoto(xr + 1, y0))
            xr++;
        var yb = y0;
        while (yb + 1 < h && IsPhoto(x0, yb + 1))
            yb++;
        var cellW = xr - x0 + 1;
        var cellH = yb - y0 + 1;

        // Zwei Messpunkte (5 % und 50 % der Zelle, fern von Textband und Fußbereich), linear bis zum Zellrand verlängert.
        double Level(int x, int y)
        {
            var i = (y * w + x) * 4;
            return horizontal ? px[i + 2] : px[i];
        }

        var length = horizontal ? cellW : cellH;
        var origin = horizontal ? x0 : y0;
        var p1 = origin + (int)Math.Round(length * 0.05);
        var p2 = origin + (int)Math.Round(length * 0.50);
        var across = horizontal ? y0 + cellH / 2 : x0 + cellW / 2;
        var l1 = horizontal ? Level(p1, across) : Level(across, p1);
        var l2 = horizontal ? Level(p2, across) : Level(across, p2);
        var slope = (l2 - l1) / (p2 - p1);

        // Pixel p deckt [p, p+1) und hat seine Mitte bei p + 0,5.
        var atStart = l1 - slope * (p1 + 0.5 - origin);
        var atEnd = l2 + slope * (origin + length - (p2 + 0.5));
        return (atStart / 255.0, atEnd / 255.0);
    }
}
