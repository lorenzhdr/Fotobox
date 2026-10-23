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
        var geo = LayoutMath.ComputeStrips(pw, ph, count, true);
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
}

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
        var geo = LayoutMath.ComputeStrips(page.PixelWidth, page.PixelHeight, count, false);

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
        var geo = LayoutMath.ComputeStrips(result.PixelWidth, result.PixelHeight, 3, false);
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
        var geo = LayoutMath.ComputeStrips(result.PixelWidth, result.PixelHeight, 4, false);
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

        var stamp = new DateTime(2026, 10, 23, 18, 30, 0);
        foreach (var (name, page) in new[] { ("hoch", PageSpec.Default), ("quer", PageSpec.Default.ToOrientation(true)) })
        {
            Write(LayoutKind.SingleBorderless, 1, "einzel");
            Write(LayoutKind.Strip, 3, "streifen3");
            Write(LayoutKind.Strip, 4, "streifen4");
            Write(LayoutKind.Overlay, 1, "overlay");

            void Write(LayoutKind kind, int count, string label)
            {
                var layout = new LayoutSettings
                {
                    Kind = kind,
                    StripPhotoCount = count,
                    CaptionText = "Max & Erika – 23.10.2026",
                    ShowDate = true,
                    OverlayFileName = "sample-rahmen.png"
                };
                var photos = new[] { photoA, photoB, photoC, photoD }.Take(_composer.GetRequiredPhotoCount(layout)).ToList();
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
