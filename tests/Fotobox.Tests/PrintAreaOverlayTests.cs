using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Services.Layout;
using Fotobox.Views.Admin;
using Fotobox.Views.Booth;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Das Overlay des Druckbereichs (<see cref="PrintAreaOverlay"/>): Striche, Spiegelung, echte Pixel und Eingabedurchlässigkeit.</summary>
[Collection(WpfCollection.Name)]
public class PrintAreaOverlayTests
{
    private static readonly ViewRect Image = new(0, 0, 1600, 900);

    // ---- Striche

    [Fact]
    public void InteriorEdges_SideCrop_HasLeftAndRightOnly()
    {
        var edges = PrintAreaOverlay.InteriorEdges(new ViewRect(300, 0, 1000, 900), Image);

        Assert.Equal((true, false, true, false), edges);
    }

    [Fact]
    public void InteriorEdges_TopBottomCrop_HasTopAndBottomOnly()
    {
        var edges = PrintAreaOverlay.InteriorEdges(new ViewRect(0, 100, 1600, 700), Image);

        Assert.Equal((false, true, false, true), edges);
    }

    [Fact]
    public void Lines_SideCrop_AreTwoVerticalStrokesOverTheFullHeight()
    {
        var strokes = PrintAreaOverlay.BuildStrokes(new ViewRect(300, 0, 1000, 900), Image, PrintAreaStyle.Lines, 3);

        Assert.Equal(2, strokes.Count);
        Assert.Contains(strokes, s => s[0] == new Point(300, 0) && s[1] == new Point(300, 900));
        Assert.Contains(strokes, s => s[0] == new Point(1300, 0) && s[1] == new Point(1300, 900));
    }

    [Fact]
    public void Lines_TopBottomCrop_AreTwoHorizontalStrokesOverTheFullWidth()
    {
        var strokes = PrintAreaOverlay.BuildStrokes(new ViewRect(0, 100, 1600, 700), Image, PrintAreaStyle.Lines, 3);

        Assert.Equal(2, strokes.Count);
        Assert.Contains(strokes, s => s[0] == new Point(0, 100) && s[1] == new Point(1600, 100));
        Assert.Contains(strokes, s => s[0] == new Point(0, 800) && s[1] == new Point(1600, 800));
    }

    [Fact]
    public void LinesShaded_DrawsTheSameStrokesAsLines()
    {
        var rect = new ViewRect(300, 0, 1000, 900);

        Assert.Equal(PrintAreaOverlay.BuildStrokes(rect, Image, PrintAreaStyle.Lines, 3).Count,
            PrintAreaOverlay.BuildStrokes(rect, Image, PrintAreaStyle.LinesShaded, 3).Count);
    }

    [Fact]
    public void Corners_AreFourLShapes_WithSixPercentOfTheShorterSide_AndBorderLegsMoveInwards()
    {
        // Seitlicher Zuschnitt: links/rechts im Bild, oben/unten am Bildrand (um die halbe Linienstärke nach innen gerückt).
        var strokes = PrintAreaOverlay.BuildStrokes(new ViewRect(300, 0, 1000, 900), Image, PrintAreaStyle.Corners, 4);

        Assert.Equal(4, strokes.Count);
        Assert.All(strokes, s => Assert.Equal(3, s.Length));
        var leg = 0.06 * 900;
        // Ecke oben links: waagerechter Schenkel, Scheitel, senkrechter Schenkel.
        Assert.Equal(new Point(300 + leg, 2), strokes[0][0]);
        Assert.Equal(new Point(300, 2), strokes[0][1]);
        Assert.Equal(new Point(300, 2 + leg), strokes[0][2]);
        // Ecke unten rechts.
        Assert.Equal(new Point(1300, 898), strokes[3][1]);
        Assert.Equal(new Point(1300 - leg, 898), strokes[3][0]);
        Assert.Equal(new Point(1300, 898 - leg), strokes[3][2]);
    }

    [Fact]
    public void Corners_LegIsAtLeastThreeTimesTheThickness_AndNeverLongerThanHalfTheSide()
    {
        var tiny = PrintAreaOverlay.BuildStrokes(new ViewRect(0, 0, 40, 30), new ViewRect(-10, -10, 60, 50), PrintAreaStyle.Corners, 10);
        Assert.All(tiny, s => Assert.True(Math.Abs(s[0].X - s[1].X) + Math.Abs(s[0].Y - s[1].Y) <= 15 + 1e-9));

        var wide = PrintAreaOverlay.BuildStrokes(new ViewRect(300, 0, 1000, 900), Image, PrintAreaStyle.Corners, 10);
        Assert.True(Math.Abs(wide[0][0].X - wide[0][1].X) >= 30);
    }

    [Fact]
    public void Mirroring_MovesTheStrokesToTheMirroredPositionsOfTheView()
    {
        var area = new NormRect(0.1, 0, 0.5, 1);
        var plain = OverlayMapper.Map(area, 1600, 900, 1600, 900, OverlayFit.UniformToFill, mirror: false);
        var mirrored = OverlayMapper.Map(area, 1600, 900, 1600, 900, OverlayFit.UniformToFill, mirror: true);
        var image = OverlayMapper.Map(NormRect.Full, 1600, 900, 1600, 900, OverlayFit.UniformToFill, mirror: true);

        var strokes = PrintAreaOverlay.BuildStrokes(mirrored, image, PrintAreaStyle.Lines, 3);

        Assert.Equal(160, plain.X, 6);
        Assert.Equal(1600 - 160 - 800, mirrored.X, 6);
        Assert.Contains(strokes, s => Math.Abs(s[0].X - 640) < 1e-6);
        Assert.Contains(strokes, s => Math.Abs(s[0].X - 1440) < 1e-6);
    }

    // ---- Gerenderte Pixel

    private static BitmapSource Render(FrameworkElement element, int w, int h)
    {
        element.Measure(new Size(w, h));
        element.Arrange(new Rect(0, 0, w, h));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        return bitmap;
    }

    private static (byte B, byte G, byte R, byte A) Pixel(BitmapSource bmp, int x, int y)
    {
        var px = new byte[4];
        bmp.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return (px[0], px[1], px[2], px[3]);
    }

    private static PrintAreaOverlay Overlay(PrintAreaStyle style, NormRect? area = null) => new()
    {
        Area = area ?? new NormRect(0.25, 0, 0.5, 1),
        ImageWidth = 1600,
        ImageHeight = 900,
        AreaStyle = style,
        AreaColor = PrintAreaColor.White,
        OpacityPercent = 100,
        ThicknessPx = 4,
        ShadePercent = 50,
    };

    [StaFact]
    public void ShadedLines_DarkenOutside_LeaveInsideClear_AndDrawTheLineAtTheEdge()
    {
        var bitmap = Render(Overlay(PrintAreaStyle.LinesShaded), 400, 225);

        // Außen: schwarz mit 50 % Deckkraft; innen: durchsichtig; auf der Kante (x = 100): weiße Linie.
        var outside = Pixel(bitmap, 40, 100);
        Assert.InRange(outside.A, 124, 132);
        Assert.Equal(0, outside.R);
        Assert.Equal(0, Pixel(bitmap, 200, 100).A);
        var line = Pixel(bitmap, 100, 100);
        Assert.Equal((byte)255, line.A);
        Assert.Equal((byte)255, line.R);
        // Rechts außerhalb ebenfalls abgedunkelt.
        Assert.InRange(Pixel(bitmap, 360, 100).A, 124, 132);
    }

    [StaFact]
    public void Lines_DoNotShade()
    {
        var bitmap = Render(Overlay(PrintAreaStyle.Lines), 400, 225);

        Assert.Equal(0, Pixel(bitmap, 40, 100).A);
        Assert.Equal(0, Pixel(bitmap, 200, 100).A);
        Assert.Equal((byte)255, Pixel(bitmap, 100, 100).A);
        Assert.Equal((byte)255, Pixel(bitmap, 300, 100).A);
    }

    [StaFact]
    public void Corners_OnlyDrawNearTheCorners()
    {
        var bitmap = Render(Overlay(PrintAreaStyle.Corners), 400, 225);

        Assert.Equal((byte)255, Pixel(bitmap, 100, 5).A);   // Scheitel der Ecke oben links (waagerechter Schenkel)
        Assert.Equal((byte)255, Pixel(bitmap, 101, 10).A);  // senkrechter Schenkel
        Assert.Equal(0, Pixel(bitmap, 100, 112).A);          // mitten auf der Kante: keine Linie
        Assert.Equal(0, Pixel(bitmap, 200, 2).A);
    }

    [StaFact]
    public void Opacity_AndColor_AreApplied()
    {
        var overlay = Overlay(PrintAreaStyle.Lines);
        overlay.OpacityPercent = 50;
        overlay.AreaColor = PrintAreaColor.Red;
        var bitmap = Render(overlay, 400, 225);

        var line = Pixel(bitmap, 100, 100);
        Assert.InRange(line.A, 124, 132);
        // Vormultipliziert: Rot ≈ A, Grün/Blau ≈ A · (0x3B/0xFF) bzw. 0x30/0xFF.
        Assert.InRange(line.R, line.A - 2, line.A + 2);
        Assert.InRange(line.G, 0, 40);
    }

    [StaFact]
    public void Mirrored_PutsTheLinesAtTheMirroredPositions()
    {
        var overlay = Overlay(PrintAreaStyle.Lines, new NormRect(0.1, 0, 0.4, 1));
        overlay.Mirror = true;
        var bitmap = Render(overlay, 400, 225);

        // Ungespiegelt läge der Bereich von x = 40 bis 200, gespiegelt von 200 bis 360.
        Assert.Equal((byte)255, Pixel(bitmap, 200, 100).A);
        Assert.Equal((byte)255, Pixel(bitmap, 360, 100).A);
        Assert.Equal(0, Pixel(bitmap, 40, 100).A);
    }

    [StaFact]
    public void WithoutAnArea_NothingIsDrawn()
    {
        var overlay = Overlay(PrintAreaStyle.LinesShaded);
        overlay.Area = null;
        var bitmap = Render(overlay, 400, 225);

        Assert.All(new[] { (10, 10), (100, 100), (200, 112), (390, 220) }, p => Assert.Equal(0, Pixel(bitmap, p.Item1, p.Item2).A));
    }

    [StaFact]
    public void UniformToFill_CropsTheImage_SoTheAreaMapsOntoTheVisiblePart()
    {
        // Bild 16:9 in einer quadratischen Ansicht: sichtbar ist nur die mittlere Bildmitte, der Bereich (Mitte 50 %) füllt 100 % der Ansicht ab.
        var overlay = Overlay(PrintAreaStyle.Lines, new NormRect(0.25, 0, 0.5, 1));
        var bitmap = Render(overlay, 225, 225);

        // Das Bild wird auf 400 × 225 skaliert und zentriert (Versatz −87,5): Die Bereichskanten bei 100 und 300 liegen in der Ansicht bei 12,5 und 212,5.
        Assert.Equal((byte)255, Pixel(bitmap, 212, 100).A);
        Assert.Equal(0, Pixel(bitmap, 100, 100).A);
    }

    [StaFact]
    public void Overlay_IsNotHitTestVisible_SoClicksAndTheMouseWheelReachTheWindow()
    {
        var overlay = Overlay(PrintAreaStyle.LinesShaded);
        var host = new Grid { Width = 400, Height = 225, Background = Brushes.Black };
        host.Children.Add(overlay);
        host.Measure(new Size(400, 225));
        host.Arrange(new Rect(0, 0, 400, 225));

        Assert.False(overlay.IsHitTestVisible);
        Assert.False(overlay.Focusable);
        var hit = VisualTreeHelper.HitTest(host, new Point(200, 100));
        Assert.NotNull(hit);
        Assert.NotSame(overlay, hit!.VisualHit);
    }

    [StaFact]
    public void Sketch_UsesTheSampleAreaOrTheGivenArea()
    {
        var sketch = new PrintAreaSketch { UseSample = true, Width = 288, Height = 162 };
        var overlay = FindOverlay(sketch);
        Assert.Equal(PrintAreaSketch.SampleArea, overlay.Area);

        sketch.UseSample = false;
        Assert.Null(overlay.Area);
        sketch.Area = new NormRect(0.1, 0.1, 0.5, 0.5);
        Assert.Equal(new NormRect(0.1, 0.1, 0.5, 0.5), overlay.Area);

        sketch.AreaColor = PrintAreaColor.Green;
        sketch.AreaStyle = PrintAreaStyle.Corners;
        sketch.OpacityPercent = 40;
        sketch.ThicknessPx = 9;
        sketch.ShadePercent = 70;
        Assert.Equal((PrintAreaColor.Green, PrintAreaStyle.Corners, 40, 9, 70),
            (overlay.AreaColor, overlay.AreaStyle, overlay.OpacityPercent, overlay.ThicknessPx, overlay.ShadePercent));

        // Die Vorschau vergrößert die Linienstärke, damit sie im kleinen Bild sichtbar bleibt.
        sketch.ThicknessScale = 3;
        Assert.Equal(27, overlay.ThicknessPx);
        sketch.ThicknessPx = 1;
        Assert.Equal(3, overlay.ThicknessPx);
    }

    private static PrintAreaOverlay FindOverlay(DependencyObject root)
    {
        var sketch = (Border)root;
        var viewbox = (Viewbox)sketch.Child;
        var grid = (Grid)viewbox.Child;
        return grid.Children.OfType<PrintAreaOverlay>().Single();
    }
}

/// <summary>
/// Sichtprüfung per Bilddatei, kein Test im engeren Sinn: Läuft nur, wenn die Umgebungsvariable <c>FOTOBOX_RENDER_DIR</c> auf einen
/// Ordner zeigt (sonst sofort beendet, auch in der CI). Legt den Druckbereich in allen drei Formen über ein Testbild (Einzelfoto quer und
/// hoch, Doppelstreifen, gespiegelt) und rendert die Admin-Skizze. Aufruf:
/// <c>FOTOBOX_RENDER_DIR=C:\pfad\render dotnet test --filter "FullyQualifiedName~PrintAreaVisualRenderTests"</c>.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait("Category", "Visual")]
public class PrintAreaVisualRenderTests
{
    private static string? RenderDir()
    {
        var dir = Environment.GetEnvironmentVariable("FOTOBOX_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            return null;
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Synthetisches Livebild: Verlauf mit Raster und zwei Cartoon-Köpfen.</summary>
    private static BitmapSource SyntheticPhoto(int w, int h)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x5B, 0x8D, 0xC9), Color.FromRgb(0x2E, 0x3A, 0x4E), 90), null, new Rect(0, 0, w, h));
            var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 2);
            for (var x = 0; x <= w; x += w / 16)
                dc.DrawLine(grid, new Point(x, 0), new Point(x, h));
            for (var y = 0; y <= h; y += h / 9)
                dc.DrawLine(grid, new Point(0, y), new Point(w, y));
            var skin = new SolidColorBrush(Color.FromRgb(0xF1, 0xC2, 0x9A));
            var hair = new SolidColorBrush(Color.FromRgb(0x3A, 0x26, 0x18));
            var s = Math.Min(w, h);
            foreach (var cx in new[] { w * 0.40, w * 0.62 })
            {
                dc.DrawEllipse(hair, null, new Point(cx, h * 0.42), s * 0.17, s * 0.19);
                dc.DrawEllipse(skin, null, new Point(cx, h * 0.46), s * 0.15, s * 0.18);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x2F, 0x4F, 0x8F)), null, new Rect(cx - s * 0.22, h * 0.66, s * 0.44, h), 30, 30);
            }
        }

        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Wie die Gastansicht: Livebild (füllend, ggf. gespiegelt) mit dem Overlay darüber.</summary>
    private static BitmapSource RenderGuestView(int viewW, int viewH, PrintAreaStyle style, PrintAreaColor color, NormRect? area, bool mirror)
    {
        var photo = SyntheticPhoto(1920, 1080);
        var image = new Image { Source = photo, Stretch = Stretch.UniformToFill };
        if (mirror)
        {
            image.RenderTransformOrigin = new Point(0.5, 0.5);
            image.RenderTransform = new ScaleTransform(-1, 1);
        }

        var host = new Grid { Width = viewW, Height = viewH, ClipToBounds = true };
        host.Children.Add(image);
        host.Children.Add(new PrintAreaOverlay
        {
            Area = area, ImageWidth = 1920, ImageHeight = 1080, Mirror = mirror, AreaStyle = style, AreaColor = color,
            OpacityPercent = 80, ThicknessPx = 3, ShadePercent = 40,
        });
        host.Measure(new Size(viewW, viewH));
        host.Arrange(new Rect(0, 0, viewW, viewH));
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap(viewW, viewH, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        return bitmap;
    }

    [StaFact]
    public void Render_AllShapes()
    {
        var dir = RenderDir();
        if (dir is null)
            return;

        // Dieselbe Rechnung wie in der Gastansicht: 10 × 15 cm quer (3:2) bzw. hoch (2:3) und ein Doppelstreifen (hoch).
        var landscape = PrintAreaPreview.Compute(LayoutKind.SingleBorderless, StripStyle.DoubleCut, 3, false, false, new PageSpec(152.4, 101.6, 300), 1920, 1080);
        var portrait = PrintAreaPreview.Compute(LayoutKind.SingleBorderless, StripStyle.DoubleCut, 3, false, false, PageSpec.Default, 1920, 1080);
        var strip = PrintAreaPreview.Compute(LayoutKind.Strip, StripStyle.DoubleCut, 3, true, true, PageSpec.Default, 1920, 1080);

        foreach (var style in Enum.GetValues<PrintAreaStyle>())
        {
            SavePng(RenderGuestView(1280, 720, style, PrintAreaColor.White, landscape, mirror: true), Path.Combine(dir, $"druckbereich_quer_{style}.png"));
            SavePng(RenderGuestView(1280, 720, style, PrintAreaColor.Yellow, portrait, mirror: true), Path.Combine(dir, $"druckbereich_hoch_{style}.png"));
            SavePng(RenderGuestView(1280, 720, style, PrintAreaColor.Pink, strip, mirror: false), Path.Combine(dir, $"druckbereich_streifen_{style}.png"));
        }
        // Ansicht mit anderem Seitenverhältnis als das Kamerabild (UniformToFill beschneidet das Livebild selbst).
        SavePng(RenderGuestView(1000, 720, PrintAreaStyle.LinesShaded, PrintAreaColor.Green, portrait, mirror: true), Path.Combine(dir, "druckbereich_ansicht_4zu3.png"));
    }

    [StaFact]
    public void Render_AdminSketches()
    {
        var dir = RenderDir();
        if (dir is null)
            return;

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.White };
        foreach (var style in Enum.GetValues<PrintAreaStyle>())
        {
            panel.Children.Add(new PrintAreaSketch
            {
                Width = 288, Height = 162, Margin = new Thickness(8), UseSample = true, AreaStyle = style,
                AreaColor = PrintAreaColor.White, OpacityPercent = 80, ThicknessPx = 30, ShadePercent = 40,
            });
        }
        panel.Children.Add(new PrintAreaSketch
        {
            Width = 480, Height = 270, Margin = new Thickness(8), Area = new NormRect(0.1953, 0, 0.609, 1), AreaStyle = PrintAreaStyle.LinesShaded,
            AreaColor = PrintAreaColor.Orange, OpacityPercent = 80, ThicknessPx = 3, ThicknessScale = 3, ShadePercent = 40,
        });
        panel.Measure(new Size(1700, 300));
        panel.Arrange(new Rect(0, 0, 1700, 300));
        panel.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1700, 300, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(panel);
        SavePng(bitmap, Path.Combine(dir, "druckbereich_admin_skizzen.png"));
    }
}
