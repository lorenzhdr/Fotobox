using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Services.Layout;
using Xunit;

namespace Fotobox.Tests;

public class LayoutMathTests
{
    [Fact]
    public void CropToFill_WiderSource_CropsSidesCentered()
    {
        var r = LayoutMath.CropToFill(2000, 1000, 1000, 1000);
        Assert.Equal(new Rect(500, 0, 1000, 1000), r);
    }

    [Fact]
    public void CropToFill_TallerSource_CropsTopAndBottomCentered()
    {
        var r = LayoutMath.CropToFill(1000, 2000, 1000, 1000);
        Assert.Equal(new Rect(0, 500, 1000, 1000), r);
    }

    [Fact]
    public void CropToFill_SameAspect_ReturnsWholeSource()
    {
        var r = LayoutMath.CropToFill(1600, 1200, 400, 300);
        Assert.Equal(new Rect(0, 0, 1600, 1200), r);
    }

    [Fact]
    public void CropToFill_LandscapeSourceToPortraitTarget()
    {
        var r = LayoutMath.CropToFill(1800, 1200, 1200, 1800);
        Assert.Equal(800, r.Width, 6);
        Assert.Equal(1200, r.Height, 6);
        Assert.Equal(500, r.X, 6);
        Assert.Equal(0, r.Y, 6);
    }

    [Fact]
    public void CropToFill_InvalidSizes_DoNotThrow()
    {
        var r = LayoutMath.CropToFill(0, 0, 100, 100);
        Assert.Equal(0, r.Width);
    }

    [Theory]
    [InlineData(600, 900, 2)]
    [InlineData(600, 900, 4)]
    [InlineData(900, 600, 3)]
    public void ComputeStrips_CellsStayInsideStripsAndDoNotOverlap(int pw, int ph, int count)
    {
        var geo = LayoutMath.ComputeStrips(pw, ph, count);
        Assert.Equal(2, geo.Strips.Count);
        for (var s = 0; s < 2; s++)
        {
            Assert.Equal(count, geo.Cells[s].Count);
            foreach (var cell in geo.Cells[s])
            {
                Assert.True(cell.Width > 0 && cell.Height > 0);
                Assert.True(geo.Strips[s].Contains(cell));
            }
            for (var i = 1; i < count; i++)
                Assert.False(Rect.Intersect(geo.Cells[s][i - 1], geo.Cells[s][i]) is { IsEmpty: false, Width: > 0, Height: > 0 });
            Assert.True(geo.Footers[s].Top >= geo.Cells[s][count - 1].Bottom);
        }
    }

    [Theory]
    [InlineData(1200, 1800, 3, 300)]
    [InlineData(1800, 1200, 3, 300)]
    [InlineData(600, 900, 4, 150)]
    public void ComputeStrips_TextStaysInSafeZone(int pw, int ph, int count, int dpi)
    {
        var safeBottom = LayoutMath.MmToPx(LayoutMath.SafeBottomMm, dpi);
        var safeSide = LayoutMath.MmToPx(LayoutMath.SafeSideMm, dpi);
        var geo = LayoutMath.ComputeStrips(pw, ph, count, dpi);

        for (var s = 0; s < 2; s++)
        {
            var footer = geo.Footers[s];
            var strip = geo.Strips[s];
            Assert.True(footer.Bottom <= strip.Bottom - safeBottom, $"Text zu nah an der Unterkante: {footer.Bottom} > {strip.Bottom - safeBottom}");
            Assert.True(footer.Left >= strip.Left + safeSide);
            Assert.True(footer.Right <= strip.Right - safeSide);
            Assert.True(footer.Top >= geo.Cells[s][count - 1].Bottom);
            Assert.True(footer.Height > 1);
        }
    }

    [Theory]
    [InlineData(1200, 1800, 2, 1.5, true, true)]
    [InlineData(1200, 1800, 3, 1.5, true, true)]
    [InlineData(1200, 1800, 4, 1.5, true, false)]
    [InlineData(1200, 1800, 3, 16.0 / 9, true, true)]
    [InlineData(1200, 1800, 3, 4.0 / 3, false, false)]
    [InlineData(1800, 1200, 2, 1.5, true, true)]
    [InlineData(1800, 1200, 3, 4.0 / 3, true, true)]
    [InlineData(1800, 1200, 4, 16.0 / 9, true, true)]
    public void ComputeSingleStrip_CellsInsidePage_NoOverlap_KeepAspect_AreCentered(int pw, int ph, int count, double aspect, bool caption, bool date)
    {
        var geo = LayoutMath.ComputeSingleStrip(pw, ph, Enumerable.Repeat(aspect, count).ToList(), caption, date);
        var page = new Rect(0, 0, pw, ph);

        Assert.Single(geo.Strips);
        Assert.Single(geo.Cells);
        var cells = geo.Cells[0];
        Assert.Equal(count, cells.Count);
        for (var i = 0; i < count; i++)
        {
            var c = cells[i];
            Assert.True(c.Width > 0 && c.Height > 0);
            Assert.True(page.Contains(c));
            Assert.InRange(Math.Abs(c.Width / aspect - c.Height), 0, 1.0);
            Assert.InRange(Math.Abs(c.X + c.Width / 2 - pw / 2.0), 0, 1.0);
            Assert.Equal(cells[0].Width, c.Width);
            if (i > 0)
                Assert.True(c.Top >= cells[i - 1].Bottom, "Zellen überlappen");
        }

        if (caption || date)
        {
            var safeBottom = LayoutMath.MmToPx(LayoutMath.SafeBottomMm, 300);
            var footer = geo.Footers[0];
            Assert.True(footer.Bottom <= ph - safeBottom);
            Assert.True(footer.Left >= LayoutMath.MmToPx(LayoutMath.SafeSideMm, 300));
            Assert.True(footer.Right <= pw - LayoutMath.MmToPx(LayoutMath.SafeSideMm, 300));
            Assert.True(footer.Top >= cells[^1].Bottom);
        }
    }

    [Fact]
    public void ComputeSingleStrip_ThreePhotos10x15_UsesAvailableHeight()
    {
        var geo = LayoutMath.ComputeSingleStrip(1200, 1800, [1.5, 1.5, 1.5], true, true);
        var cells = geo.Cells[0];

        // Höhenbegrenzt (nicht breitenbegrenzt): Spalte ist schmaler als die Seite, aber deutlich größer als ein Drittel.
        Assert.InRange(cells[0].Width, 600, 760);
        Assert.True(cells[0].Width < 1200 - 2 * 48);
        // Foto-Block füllt die Fläche über dem Text bis auf wenige Pixel aus.
        var blockHeight = cells[^1].Bottom - cells[0].Top;
        Assert.True(geo.Footers[0].Top - cells[0].Top - blockHeight < 80);
    }

    [Fact]
    public void ComputeSingleStrip_TwoPhotos_LimitedByPageWidth()
    {
        var geo = LayoutMath.ComputeSingleStrip(1200, 1800, [1.5, 1.5], false, false);
        var c = geo.Cells[0][0];
        Assert.Equal(1200 - 2 * 48, c.Width);
    }

    [Fact]
    public void ComputeSingleStrip_MixedAspects_KeepEachAspect()
    {
        var aspects = new[] { 4.0 / 3, 16.0 / 9, 1.5 };
        var geo = LayoutMath.ComputeSingleStrip(1200, 1800, aspects, true, false);
        for (var i = 0; i < 3; i++)
            Assert.InRange(Math.Abs(geo.Cells[0][i].Width / aspects[i] - geo.Cells[0][i].Height), 0, 1.0);
    }

    [Fact]
    public void ComputeSingleStrip_InvalidAspect_DoesNotThrow()
    {
        var geo = LayoutMath.ComputeSingleStrip(1200, 1800, [0, double.NaN, -1], true, true);
        Assert.Equal(3, geo.Cells[0].Count);
        Assert.All(geo.Cells[0], c => Assert.True(c.Width > 0 && c.Height > 0));
    }
}

[Collection(WpfCollection.Name)]
public class LayoutComposerTests : IDisposable
{
    private static readonly PageSpec Portrait = new(101.6, 152.4, 150);   // 600 × 900
    private static readonly PageSpec Landscape = Portrait.ToOrientation(true);  // 900 × 600

    private readonly LayoutComposer _composer = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-layout-" + Guid.NewGuid().ToString("N"));

    public LayoutComposerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* Aufräumen ist best effort */ }
    }

    // ---------- Hilfen ----------

    private static BitmapSource Solid(Color c, int w = 1200, int h = 800)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = c.A;
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    private static (byte B, byte G, byte R, byte A) Pixel(BitmapSource bmp, int x, int y)
    {
        var px = new byte[4];
        bmp.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return (px[0], px[1], px[2], px[3]);
    }

    private static void AssertColor(Color expected, (byte B, byte G, byte R, byte A) actual, int tol = 3)
    {
        Assert.InRange(actual.R, expected.R - tol, expected.R + tol);
        Assert.InRange(actual.G, expected.G - tol, expected.G + tol);
        Assert.InRange(actual.B, expected.B - tol, expected.B + tol);
        Assert.Equal(255, actual.A);
    }

    private static LayoutSettings Settings(LayoutKind kind, int stripCount = 3) =>
        new() { Kind = kind, StripPhotoCount = stripCount };

    private BitmapSource Compose(IReadOnlyList<BitmapSource> photos, LayoutSettings s, PageSpec page) =>
        _composer.Compose(photos, s, page, _dir, new DateTime(2026, 10, 23, 18, 30, 0));

    private static void SavePng(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // ---------- Anzahl ----------

    [Theory]
    [InlineData(LayoutKind.SingleBorderless, 3, 1)]
    [InlineData(LayoutKind.Overlay, 3, 1)]
    [InlineData(LayoutKind.Strip, 3, 3)]
    [InlineData(LayoutKind.Strip, 1, 2)]
    [InlineData(LayoutKind.Strip, 9, 4)]
    public void GetRequiredPhotoCount_FollowsLayout(LayoutKind kind, int stripCount, int expected) =>
        Assert.Equal(expected, _composer.GetRequiredPhotoCount(Settings(kind, stripCount)));

    // ---------- Maße und Dpi ----------

    [StaTheory]
    [InlineData(LayoutKind.SingleBorderless, false)]
    [InlineData(LayoutKind.SingleBorderless, true)]
    [InlineData(LayoutKind.Strip, false)]
    [InlineData(LayoutKind.Strip, true)]
    [InlineData(LayoutKind.Overlay, false)]
    [InlineData(LayoutKind.Overlay, true)]
    public void Compose_ReturnsExactSizeAndDpi_AndIsFrozen(LayoutKind kind, bool landscape)
    {
        var page = landscape ? Landscape : Portrait;
        var layout = Settings(kind);
        layout.CaptionText = "Test";
        layout.ShowDate = true;
        var photos = Enumerable.Repeat(Solid(Colors.SteelBlue), _composer.GetRequiredPhotoCount(layout)).ToList();

        var result = Compose(photos, layout, page);

        Assert.Equal(page.PixelWidth, result.PixelWidth);
        Assert.Equal(page.PixelHeight, result.PixelHeight);
        Assert.Equal(page.Dpi, result.DpiX, 3);
        Assert.Equal(page.Dpi, result.DpiY, 3);
        Assert.Equal(PixelFormats.Bgra32, result.Format);
        Assert.True(result.IsFrozen);
    }

    [StaFact]
    public void Compose_DefaultPage300Dpi_HasExpectedSize()
    {
        var result = Compose([Solid(Colors.Gray)], Settings(LayoutKind.SingleBorderless), PageSpec.Default);
        Assert.Equal(1200, result.PixelWidth);
        Assert.Equal(1800, result.PixelHeight);
        Assert.Equal(300, result.DpiX, 3);
    }

    [StaFact]
    public void Compose_NoPhotos_ThrowsArgumentException() =>
        Assert.Throws<ArgumentException>(() => Compose([], Settings(LayoutKind.SingleBorderless), Portrait));

    // ---------- SingleBorderless ----------

    [StaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleBorderless_RedPhoto_AllCornersAreRed(bool landscape)
    {
        var page = landscape ? Landscape : Portrait;
        var result = Compose([Solid(Colors.Red)], Settings(LayoutKind.SingleBorderless), page);

        var w = result.PixelWidth - 1;
        var h = result.PixelHeight - 1;
        foreach (var (x, y) in new[] { (0, 0), (w, 0), (0, h), (w, h), (w / 2, h / 2) })
            AssertColor(Colors.Red, Pixel(result, x, y));
    }

    [StaFact]
    public void SingleBorderless_Caption_DarkensBottomButNotTop()
    {
        var layout = Settings(LayoutKind.SingleBorderless);
        layout.CaptionText = "Max & Erika";
        var result = Compose([Solid(Colors.White)], layout, Portrait);

        AssertColor(Colors.White, Pixel(result, 5, 5));
        var bottom = Pixel(result, 5, result.PixelHeight - 3);
        Assert.True(bottom.R < 140, $"Verlauf unten erwartet, war R={bottom.R}");
    }

    // ---------- Strip ----------

    [StaTheory]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(false, 2)]
    [InlineData(true, 4)]
    public void Strip_MarginIsWhite_CellsAreColored_InBothStrips(bool landscape, int count)
    {
        var page = landscape ? Landscape : Portrait;
        var colors = new[] { Colors.Red, Colors.Lime, Colors.Blue, Colors.Orange };
        var photos = colors.Take(count).Select(c => Solid(c)).ToList();

        var result = Compose(photos, Settings(LayoutKind.Strip, count), page);
        var geo = LayoutMath.ComputeStrips(page.PixelWidth, page.PixelHeight, count, hasCaption: false, hasDate: false);

        AssertColor(Colors.White, Pixel(result, 1, 1));
        AssertColor(Colors.White, Pixel(result, result.PixelWidth - 2, result.PixelHeight - 2));
        for (var s = 0; s < 2; s++)
        {
            for (var i = 0; i < count; i++)
            {
                var cell = geo.Cells[s][i];
                AssertColor(colors[i], Pixel(result, (int)(cell.X + cell.Width / 2), (int)(cell.Y + cell.Height / 2)));
                // Zellrand liegt exakt auf der Zelle, direkt daneben ist weiß (Rand/Abstand).
                AssertColor(colors[i], Pixel(result, (int)cell.X, (int)cell.Y));
                AssertColor(Colors.White, Pixel(result, (int)cell.X - 1, (int)cell.Y - 1));
            }
        }
    }

    [StaFact]
    public void Strip_TwoStripsAreIdentical_Portrait()
    {
        var photos = new[] { Solid(Colors.Red), Solid(Colors.Lime), Solid(Colors.Blue) };
        var result = Compose(photos, Settings(LayoutKind.Strip, 3), Portrait);
        var half = result.PixelWidth / 2;

        // Zellmitte links vs. rechts auf gleicher Höhe.
        var geo = LayoutMath.ComputeStrips(result.PixelWidth, result.PixelHeight, 3, hasCaption: false, hasDate: false);
        for (var i = 0; i < 3; i++)
        {
            var c0 = geo.Cells[0][i];
            var y = (int)(c0.Y + c0.Height / 2);
            var left = Pixel(result, (int)(c0.X + c0.Width / 2), y);
            var right = Pixel(result, (int)(c0.X + c0.Width / 2) + half, y);
            Assert.Equal(left, right);
        }
    }

    [StaFact]
    public void Strip_FewerPhotosThanRequired_RepeatsAndDoesNotThrow()
    {
        var result = Compose([Solid(Colors.Red)], Settings(LayoutKind.Strip, 4), Portrait);
        var geo = LayoutMath.ComputeStrips(result.PixelWidth, result.PixelHeight, 4, hasCaption: false, hasDate: false);
        foreach (var cell in geo.Cells[0])
            AssertColor(Colors.Red, Pixel(result, (int)(cell.X + cell.Width / 2), (int)(cell.Y + cell.Height / 2)));
    }

    [StaFact]
    public void Strip_WithCaptionAndDate_DoesNotThrowAndKeepsMargins()
    {
        var layout = Settings(LayoutKind.Strip, 3);
        layout.CaptionText = "Max & Erika – 23.10.2026 – ein sehr langer Text, der gekürzt werden muss";
        layout.ShowDate = true;
        var result = Compose([Solid(Colors.Red), Solid(Colors.Lime), Solid(Colors.Blue)], layout, Portrait);
        AssertColor(Colors.White, Pixel(result, 1, 1));
    }

    // ---------- Einzelstreifen ----------

    private const string LongCaption = "Hochzeit von Änne & Jörg – wir sagen Danke für diesen wunderschönen Tag";

    [StaTheory]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 3)]
    public void SingleStrip_PhotosAreFullAspect_CenteredAndWithoutCutLine(bool landscape, int count)
    {
        var page = (landscape ? PageSpec.Default.ToOrientation(true) : PageSpec.Default);
        var colors = new[] { Colors.Red, Colors.Lime, Colors.Blue, Colors.Orange };
        var photos = colors.Take(count).Select(c => Solid(c, 1600, 1200)).ToList();
        var layout = Settings(LayoutKind.Strip, count);
        layout.StripStyle = StripStyle.Single;
        layout.CaptionText = "Max & Erika";
        layout.ShowDate = true;

        var result = Compose(photos, layout, page);
        var geo = LayoutMath.ComputeSingleStrip(page.PixelWidth, page.PixelHeight, Enumerable.Repeat(4.0 / 3, count).ToList(), true, true);

        AssertColor(Colors.White, Pixel(result, 1, 1));
        for (var i = 0; i < count; i++)
        {
            var cell = geo.Cells[0][i];
            Assert.InRange(Math.Abs(cell.Width / (4.0 / 3) - cell.Height), 0, 1.0);
            AssertColor(colors[i], Pixel(result, (int)cell.X, (int)cell.Y));
            AssertColor(colors[i], Pixel(result, (int)cell.Right - 1, (int)cell.Bottom - 1));
            AssertColor(colors[i], Pixel(result, (int)(cell.X + cell.Width / 2), (int)(cell.Y + cell.Height / 2)));
            AssertColor(Colors.White, Pixel(result, (int)cell.X - 2, (int)(cell.Y + cell.Height / 2)));
            AssertColor(Colors.White, Pixel(result, (int)cell.Right + 1, (int)(cell.Y + cell.Height / 2)));
        }

        // Kein Schnittlinien-Strich in der Mitte zwischen den Fotos.
        var gapTop = (int)geo.Cells[0][0].Bottom;
        var gapBottom = (int)geo.Cells[0][1].Top;
        for (var y = gapTop; y < gapBottom; y++)
            for (var x = result.PixelWidth / 2 - 3; x <= result.PixelWidth / 2 + 3; x++)
                AssertColor(Colors.White, Pixel(result, x, y));
    }

    // ---------- Sicherheitszone und Text ----------

    /// <summary>Anzahl Pixel im Bereich, die laut <paramref name="isInk"/> nicht Hintergrund sind.</summary>
    private static int CountInk(BitmapSource bmp, Int32Rect area, Func<byte, byte, byte, bool> isInk, int skipColumnFrom = -1, int skipColumnTo = -1)
    {
        var stride = area.Width * 4;
        var px = new byte[stride * area.Height];
        bmp.CopyPixels(area, px, stride, 0);
        var n = 0;
        for (var y = 0; y < area.Height; y++)
            for (var x = 0; x < area.Width; x++)
            {
                var ax = area.X + x;
                if (ax >= skipColumnFrom && ax <= skipColumnTo)
                    continue;
                var i = y * stride + x * 4;
                if (isInk(px[i + 2], px[i + 1], px[i]))
                    n++;
            }
        return n;
    }

    [StaTheory]
    [InlineData(StripStyle.DoubleCut, false, "Max & Erika – 23.10.2026")]
    [InlineData(StripStyle.DoubleCut, true, "Max & Erika – 23.10.2026")]
    [InlineData(StripStyle.DoubleCut, false, LongCaption)]
    [InlineData(StripStyle.Single, false, "Max & Erika – 23.10.2026")]
    [InlineData(StripStyle.Single, true, LongCaption)]
    public void Strip_Text_StaysOutOfBottomSafeZone(StripStyle style, bool landscape, string caption)
    {
        var page = landscape ? PageSpec.Default.ToOrientation(true) : PageSpec.Default;
        var layout = Settings(LayoutKind.Strip, 3);
        layout.StripStyle = style;
        layout.CaptionText = caption;
        layout.ShowDate = true;
        var photos = new[] { Solid(Colors.Red, 1600, 1200), Solid(Colors.Lime, 1600, 1200), Solid(Colors.Blue, 1600, 1200) };

        var result = Compose(photos, layout, page);
        var geo = style == StripStyle.Single
            ? LayoutMath.ComputeSingleStrip(page.PixelWidth, page.PixelHeight, [4.0 / 3, 4.0 / 3, 4.0 / 3], true, true, page.Dpi)
            : LayoutMath.ComputeStrips(page.PixelWidth, page.PixelHeight, 3, page.Dpi);
        var safe = LayoutMath.MmToPx(LayoutMath.SafeBottomMm, page.Dpi);
        Func<byte, byte, byte, bool> notWhite = (r, g, b) => r < 235 || g < 235 || b < 235;

        for (var s = 0; s < geo.Strips.Count; s++)
        {
            var strip = geo.Strips[s];
            // Im Hochformat läuft die Schnittlinie senkrecht durch die Zone.
            var cutFrom = style == StripStyle.DoubleCut && !landscape ? (int)geo.CutStart.X - 4 : -1;
            var cutTo = style == StripStyle.DoubleCut && !landscape ? (int)geo.CutStart.X + 4 : -1;
            // Im Querformat liegt die waagerechte Schnittlinie auf der Grenze der Streifen (letzte Zeilen des oberen).
            var skipRows = style == StripStyle.DoubleCut && landscape && s == 0 ? 3 : 0;
            var bottomZone = new Int32Rect((int)strip.X, (int)strip.Bottom - safe, (int)strip.Width, safe - skipRows);
            Assert.Equal(0, CountInk(result, bottomZone, notWhite, cutFrom, cutTo));

            var footer = geo.Footers[s];
            var footerArea = new Int32Rect((int)footer.X, (int)footer.Y, (int)footer.Width, (int)footer.Height);
            Assert.True(CountInk(result, footerArea, notWhite) > 200, "Text sollte im Fußbereich stehen");
        }
    }

    [StaTheory]
    [InlineData(LayoutKind.SingleBorderless, false)]
    [InlineData(LayoutKind.SingleBorderless, true)]
    [InlineData(LayoutKind.Overlay, false)]
    public void CaptionBand_Text_StaysOutOfBottomSafeZone(LayoutKind kind, bool landscape)
    {
        var page = landscape ? PageSpec.Default.ToOrientation(true) : PageSpec.Default;
        var layout = Settings(kind);
        layout.CaptionText = LongCaption;
        layout.ShowDate = true;

        // Schwarzes Foto: Verlauf bleibt schwarz, nur der weiße Text hebt sich ab.
        var result = Compose([Solid(Colors.Black, 1600, 1200)], layout, page);
        var safe = LayoutMath.MmToPx(LayoutMath.SafeBottomMm, page.Dpi);
        var side = LayoutMath.MmToPx(LayoutMath.SafeSideMm, page.Dpi);
        Func<byte, byte, byte, bool> bright = (r, g, b) => r > 60 || g > 60 || b > 60;

        Assert.Equal(0, CountInk(result, new Int32Rect(0, result.PixelHeight - safe, result.PixelWidth, safe), bright));
        Assert.Equal(0, CountInk(result, new Int32Rect(0, result.PixelHeight - safe * 4, side, safe * 4), bright));
        Assert.Equal(0, CountInk(result, new Int32Rect(result.PixelWidth - side, result.PixelHeight - safe * 4, side, safe * 4), bright));
        Assert.True(CountInk(result, new Int32Rect(0, result.PixelHeight - safe * 4, result.PixelWidth, safe * 3), bright) > 200);
    }

    // ---------- Bildunterschrift: Umbruch statt Kürzen ----------

    [StaTheory]
    [InlineData(StripStyle.DoubleCut, false)]
    [InlineData(StripStyle.DoubleCut, true)]
    [InlineData(StripStyle.Single, false)]
    public void Caption_Typical_IsNotTruncated(StripStyle style, bool showDate)
    {
        var layout = Settings(LayoutKind.Strip, 3);
        layout.StripStyle = style;
        layout.ShowDate = showDate;
        layout.CaptionText = "Max & Erika – 23.10.2026";
        Assert.False(LayoutComposer.IsCaptionTruncated(layout, PageSpec.Default));

        layout.CaptionText = "Hochzeit von Änne & Jörg – 23.10.";   // rund 35 Zeichen
        Assert.False(LayoutComposer.IsCaptionTruncated(layout, PageSpec.Default));
    }

    [StaFact]
    public void Caption_Absurdly_Long_IsTruncated_ButDoesNotThrow()
    {
        var layout = Settings(LayoutKind.Strip, 3);
        layout.CaptionText = string.Concat(Enumerable.Repeat(LongCaption + " ", 6));
        layout.ShowDate = true;

        Assert.True(LayoutComposer.IsCaptionTruncated(layout, PageSpec.Default));
        var result = Compose([Solid(Colors.Red)], layout, PageSpec.Default);
        Assert.Equal(PageSpec.Default.PixelWidth, result.PixelWidth);
    }

    [StaTheory]
    [InlineData(StripStyle.DoubleCut)]
    [InlineData(StripStyle.Single)]
    public void IsCaptionTruncated_TinyPageWithoutFooterText_DoesNotThrow(StripStyle style)
    {
        var layout = Settings(LayoutKind.Strip, 3);
        layout.StripStyle = style;
        layout.CaptionText = "Hochzeit";
        layout.ShowDate = true;

        // Winziger Fußbereich (< 4 px): es wird kein Text gesetzt, also auch nichts gekürzt.
        Assert.False(LayoutComposer.IsCaptionTruncated(layout, new PageSpec(8, 8)));
        Assert.False(LayoutComposer.IsCaptionTruncated(layout, new PageSpec(2, 2)));
    }

    [StaTheory]
    [InlineData(LayoutKind.Strip, StripStyle.DoubleCut, false)]
    [InlineData(LayoutKind.Strip, StripStyle.DoubleCut, true)]
    [InlineData(LayoutKind.Strip, StripStyle.Single, false)]
    [InlineData(LayoutKind.SingleBorderless, StripStyle.DoubleCut, false)]
    [InlineData(LayoutKind.Overlay, StripStyle.DoubleCut, true)]
    public void Compose_LongCaptionWithUmlauts_DoesNotThrow(LayoutKind kind, StripStyle style, bool landscape)
    {
        var layout = Settings(kind, 3);
        layout.StripStyle = style;
        layout.CaptionText = LongCaption;
        layout.ShowDate = true;
        var page = landscape ? PageSpec.Default.ToOrientation(true) : PageSpec.Default;
        var photos = Enumerable.Repeat(Solid(Colors.SteelBlue), _composer.GetRequiredPhotoCount(layout)).ToList();

        var result = Compose(photos, layout, page);
        Assert.Equal(page.PixelWidth, result.PixelWidth);
        Assert.Equal(page.PixelHeight, result.PixelHeight);
    }

    // ---------- Schrift ----------

    [StaTheory]
    [InlineData("GibtEsNicht123")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void ResolveFace_UnknownOrEmpty_FallsBackToSegoeUi(string? family)
    {
        var face = LayoutComposer.ResolveFace(family);
        Assert.Equal(LayoutSettings.DefaultCaptionFont, face.FontFamily.Source);
        Assert.Equal(FontWeights.SemiBold, face.Weight);
    }

    [StaFact]
    public void ResolveFace_InstalledFamily_IsUsedCaseInsensitive_AndCached()
    {
        if (!Fonts.SystemFontFamilies.Any(f => f.Source == "Arial"))
            return;   // Schrift fehlt auf dieser Maschine

        var face = LayoutComposer.ResolveFace("aRiAl");
        Assert.Equal("Arial", face.FontFamily.Source);
        Assert.Same(face, LayoutComposer.ResolveFace("aRiAl"));
    }

    [StaTheory]
    [InlineData(LayoutKind.Strip)]
    [InlineData(LayoutKind.SingleBorderless)]
    [InlineData(LayoutKind.Overlay)]
    public void Compose_UnknownFont_DoesNotThrow(LayoutKind kind)
    {
        var layout = Settings(kind, 3);
        layout.CaptionText = "Max & Erika";
        layout.ShowDate = true;
        layout.CaptionFontFamily = "GibtEsNicht123";
        var photos = Enumerable.Repeat(Solid(Colors.SteelBlue), _composer.GetRequiredPhotoCount(layout)).ToList();

        var result = Compose(photos, layout, Portrait);
        Assert.Equal(Portrait.PixelWidth, result.PixelWidth);
    }

    // ---------- Overlay ----------

    [StaFact]
    public void Overlay_Missing_DoesNotThrow_AndRendersPhoto()
    {
        var layout = Settings(LayoutKind.Overlay);
        layout.OverlayFileName = "gibt-es-nicht.png";

        var result = Compose([Solid(Colors.Red)], layout, Portrait);

        AssertColor(Colors.Red, Pixel(result, 10, 10));
    }

    [StaFact]
    public void Overlay_NoFileNameConfigured_DoesNotThrow()
    {
        var result = Compose([Solid(Colors.Red)], Settings(LayoutKind.Overlay), Portrait);
        AssertColor(Colors.Red, Pixel(result, 10, 10));
    }

    [StaFact]
    public void Overlay_Corrupt_DoesNotThrow()
    {
        File.WriteAllText(Path.Combine(_dir, "kaputt.png"), "kein png");
        var layout = Settings(LayoutKind.Overlay);
        layout.OverlayFileName = "kaputt.png";

        var result = Compose([Solid(Colors.Red)], layout, Portrait);

        AssertColor(Colors.Red, Pixel(result, 10, 10));
    }

    [StaFact]
    public void Overlay_Present_ChangesPixels_AndIsReloadedWhenFileChanges()
    {
        var layout = Settings(LayoutKind.Overlay);
        layout.OverlayFileName = "rahmen.png";
        var photo = Solid(Colors.Red);
        var plain = Compose([photo], layout, Portrait);   // Overlay fehlt noch

        var path = Path.Combine(_dir, "rahmen.png");
        SavePng(Solid(Color.FromArgb(128, 0, 0, 255), 8, 8), path);
        var withOverlay = Compose([photo], layout, Portrait);

        var before = Pixel(plain, 100, 100);
        var after = Pixel(withOverlay, 100, 100);
        Assert.NotEqual(before, after);
        Assert.True(after.B > 60, $"Blauanteil erwartet, war B={after.B}");
        Assert.True(after.R < before.R);

        // Datei ändert sich -> Cache wird ungültig.
        SavePng(Solid(Color.FromArgb(128, 0, 255, 0), 8, 8), path);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        var reloaded = Compose([photo], layout, Portrait);
        var pixel = Pixel(reloaded, 100, 100);
        Assert.True(pixel.G > 60 && pixel.B < 60, $"Grünes Overlay erwartet, war G={pixel.G} B={pixel.B}");
    }

    // ---------- Beispielbilder ----------

    [StaFact]
    public void WriteSamples_WhenSampleDirSet()
    {
        var target = Environment.GetEnvironmentVariable("FOTOBOX_SAMPLE_DIR");
        if (string.IsNullOrWhiteSpace(target))
            return;   // nur auf Wunsch

        Directory.CreateDirectory(target);
        var photoA = GradientPhoto(1800, 1200, Colors.DeepSkyBlue, Colors.MediumVioletRed);
        var photoB = GradientPhoto(1800, 1200, Colors.Orange, Colors.SeaGreen);
        var photoC = GradientPhoto(1800, 1200, Colors.Gold, Colors.Crimson);
        var photoD = GradientPhoto(1800, 1200, Colors.MediumTurquoise, Colors.MidnightBlue);
        var overlay = FramePng();
        File.Copy(overlay, Path.Combine(_dir, "sample-rahmen.png"), true);

        // 16:9-Fotos (Kamera mit Breitbild) für die Streifen-Varianten.
        var wideA = GradientPhoto(1920, 1080, Colors.DeepSkyBlue, Colors.MediumVioletRed);
        var wideB = GradientPhoto(1920, 1080, Colors.Orange, Colors.SeaGreen);
        var wideC = GradientPhoto(1920, 1080, Colors.Gold, Colors.Crimson);
        var wideD = GradientPhoto(1920, 1080, Colors.MediumTurquoise, Colors.MidnightBlue);
        // 4:3-Fotos
        var cA = GradientPhoto(1600, 1200, Colors.DeepSkyBlue, Colors.MediumVioletRed);
        var cB = GradientPhoto(1600, 1200, Colors.Orange, Colors.SeaGreen);
        var cC = GradientPhoto(1600, 1200, Colors.Gold, Colors.Crimson);
        var cD = GradientPhoto(1600, 1200, Colors.MediumTurquoise, Colors.MidnightBlue);

        var stamp = new DateTime(2026, 10, 23, 18, 30, 0);
        var standard = new[] { photoA, photoB, photoC, photoD };
        var fourThree = new[] { cA, cB, cC, cD };
        var sixteenNine = new[] { wideA, wideB, wideC, wideD };
        foreach (var (name, page) in new[] { ("hoch", PageSpec.Default), ("quer", PageSpec.Default.ToOrientation(true)) })
        {
            Write(LayoutKind.SingleBorderless, 1, "einzel", standard);
            Write(LayoutKind.Overlay, 1, "overlay", standard);
            Write(LayoutKind.Strip, 3, "doppel3", standard);
            Write(LayoutKind.Strip, 4, "doppel4", standard);
            Write(LayoutKind.Strip, 3, "doppel3-lang", standard, caption: LongCaption);
            Write(LayoutKind.Strip, 3, "doppel3-comicsans", standard, font: "Comic Sans MS");
            foreach (var n in new[] { 2, 3, 4 })
            {
                Write(LayoutKind.Strip, n, $"einzelstreifen{n}-3zu2", standard, style: StripStyle.Single);
                Write(LayoutKind.Strip, n, $"einzelstreifen{n}-4zu3", fourThree, style: StripStyle.Single);
                Write(LayoutKind.Strip, n, $"einzelstreifen{n}-16zu9", sixteenNine, style: StripStyle.Single);
            }
            Write(LayoutKind.Strip, 3, "einzelstreifen3-lang", standard, style: StripStyle.Single, caption: LongCaption);
            Write(LayoutKind.Strip, 3, "einzelstreifen3-comicsans", standard, style: StripStyle.Single, font: "Comic Sans MS");
            Write(LayoutKind.SingleBorderless, 1, "einzel-lang", standard, caption: LongCaption);

            void Write(LayoutKind kind, int count, string label, BitmapSource[] source,
                StripStyle style = StripStyle.DoubleCut, string caption = "Max & Erika – 23.10.2026", string? font = null)
            {
                var layout = new LayoutSettings
                {
                    Kind = kind,
                    StripPhotoCount = count,
                    StripStyle = style,
                    CaptionText = caption,
                    ShowDate = true,
                    OverlayFileName = "sample-rahmen.png"
                };
                if (font is not null)
                    layout.CaptionFontFamily = font;
                var photos = source.Take(_composer.GetRequiredPhotoCount(layout)).ToList();
                var result = _composer.Compose(photos, layout, page, _dir, stamp);
                SavePng(result, Path.Combine(target, $"{label}-{name}.png"));
            }
        }
    }

    private static BitmapSource GradientPhoto(int w, int h, Color a, Color b)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(a, b, 35), null, new Rect(0, 0, w, h));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), null, new Point(w * 0.35, h * 0.4), h * 0.18, h * 0.18);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 20, 20, 20)), null, new Rect(w * 0.55, h * 0.45, w * 0.3, h * 0.4));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var conv = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        conv.Freeze();
        return conv;
    }

    private string FramePng()
    {
        const int w = 1200, h = 1800;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), 60);
            dc.DrawRectangle(null, pen, new Rect(30, 30, w - 60, h - 60));
            var inner = new Pen(new SolidColorBrush(Color.FromArgb(255, 212, 175, 55)), 6);
            dc.DrawRectangle(null, inner, new Rect(75, 75, w - 150, h - 150));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var path = Path.Combine(_dir, "frame-temp.png");
        SavePng(rtb, path);
        return path;
    }
}
