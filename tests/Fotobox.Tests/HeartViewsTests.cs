using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Heart;
using Fotobox.Core.Peace;
using Fotobox.Views.Booth.Heart;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Ansichten der Herzerkennung: Zoom-Host, Kisscam-Ansicht und Haltefortschritt am Herz.</summary>
[Collection(WpfCollection.Name)]
public class HeartViewsTests
{
    private static readonly PhotoFrame Frame = new(PhotoFrameKind.Kisscam, "KISS CAM", KisscamColor.PinkRed);

    private static void Layout(FrameworkElement element, double width = 1920, double height = 1080)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static BitmapSource Render(FrameworkElement element, int width = 1920, int height = 1080)
    {
        // Eine Änderung zeichnet erst der nächste Layoutdurchgang neu.
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    private static (byte B, byte G, byte R, byte A) PixelAt(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return (pixel[0], pixel[1], pixel[2], pixel[3]);
    }

    // ---- LiveZoomHost: Mathematik

    [Fact]
    public void ComputeTarget_WithoutRegion_OrWithoutView_IsIdentity()
    {
        Assert.Equal((1.0, 0.0, 0.0), LiveZoomHost.ComputeTarget(null, 1280, 720, 1920, 1080, OverlayFit.UniformToFill, false));
        Assert.Equal((1.0, 0.0, 0.0), LiveZoomHost.ComputeTarget(new NormRect(0.25, 0.25, 0.5, 0.5), 1280, 720, 0, 0, OverlayFit.UniformToFill, false));
        Assert.Equal((1.0, 0.0, 0.0), LiveZoomHost.ComputeTarget(NormRect.Full, 1280, 720, 1920, 1080, OverlayFit.UniformToFill, false));
    }

    [Fact]
    public void ComputeTarget_CenteredRegion_ScalesAroundTheCenter()
    {
        var (scale, tx, ty) = LiveZoomHost.ComputeTarget(new NormRect(0.25, 0.25, 0.5, 0.5), 1920, 1080, 1920, 1080, OverlayFit.UniformToFill, false);

        Assert.Equal(2.0, scale, 6);
        Assert.Equal(-960, tx, 3);
        Assert.Equal(-540, ty, 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComputeTarget_NeverRevealsTheAreaOutsideTheView_ForAMismatchedAspect(bool mirror)
    {
        // 4:3-Kamera auf 16:9-Ansicht, füllend: Das Bild ragt oben und unten über die Ansicht hinaus (dort ist es beschnitten).
        const double vw = 1920, vh = 1080;
        var regions = new[]
        {
            new NormRect(0.25, 0.5, 0.5, 0.5), new NormRect(0.25, 0.0, 0.5, 0.5), new NormRect(0.0, 0.0, 0.5, 0.5),
            new NormRect(0.5, 0.5, 0.5, 0.5), new NormRect(0.3, 0.2, 0.4, 0.4), new NormRect(0.1, 0.55, 0.6, 0.45),
        };

        foreach (var region in regions)
        {
            var (scale, tx, ty) = LiveZoomHost.ComputeTarget(region, 1600, 1200, vw, vh, OverlayFit.UniformToFill, mirror);

            Assert.True(scale >= 1, $"Maßstab {scale} für {region}");
            Assert.InRange(tx, vw * (1 - scale) - 1e-6, 1e-6);
            Assert.InRange(ty, vh * (1 - scale) - 1e-6, 1e-6);
        }
    }

    [Fact]
    public void ComputeTarget_Mirror_MovesTheRegionToTheOtherSide()
    {
        var region = new NormRect(0.0, 0.0, 0.5, 0.5); // links oben im ungespiegelten Bild
        var plain = LiveZoomHost.ComputeTarget(region, 1920, 1080, 1920, 1080, OverlayFit.UniformToFill, false);
        var mirrored = LiveZoomHost.ComputeTarget(region, 1920, 1080, 1920, 1080, OverlayFit.UniformToFill, true);

        Assert.Equal(0, plain.TranslateX, 3);       // links: kein Versatz
        Assert.Equal(-1920, mirrored.TranslateX, 3); // gespiegelt liegt es rechts
        Assert.Equal(plain.TranslateY, mirrored.TranslateY, 3);
    }

    // ---- LiveZoomHost: Steuerelement

    [StaFact]
    public void LiveZoomHost_ZoomsTheChild_AndReturnsToIdentity()
    {
        var child = new Grid();
        var host = new LiveZoomHost { Child = child, ImageWidth = 1920, ImageHeight = 1080 };
        Layout(host);
        Assert.Equal((1.0, 0.0, 0.0), host.Target);
        Assert.IsType<TransformGroup>(child.RenderTransform);

        host.Region = new NormRect(0.25, 0.25, 0.5, 0.5);
        Assert.Equal((2.0, -960.0, -540.0), host.Target);

        host.Region = null;
        Assert.Equal((1.0, 0.0, 0.0), host.Target);
    }

    [StaFact]
    public void LiveZoomHost_RecomputesOnResize_AndOnMirror()
    {
        var host = new LiveZoomHost { Child = new Grid(), ImageWidth = 1920, ImageHeight = 1080, Region = new NormRect(0.25, 0.25, 0.5, 0.5) };
        Layout(host, 1920, 1080); // erst mit der Größe gibt es einen Zoom
        Assert.Equal(2.0, host.Target.Scale, 6);

        Layout(host, 960, 540);
        Assert.Equal(2.0, host.Target.Scale, 6);
        Assert.Equal(-480, host.Target.TranslateX, 3);

        host.Region = new NormRect(0, 0, 0.5, 0.5);
        Layout(host, 1920, 1080);
        Assert.Equal(0, host.Target.TranslateX, 3);
        host.Mirror = true;
        Assert.Equal(-1920, host.Target.TranslateX, 3);
    }

    // ---- KisscamOverlay

    [StaFact]
    public void KisscamOverlay_ShowsFrameAndText_BetweenTheBottomHearts_BelowTheBanner()
    {
        WpfCollection.EnsurePackUris();
        var overlay = new KisscamOverlay
        {
            Frame = Frame,
            Text = "Du hast 10 Sekunden Zeit, dir jemanden zum Küssen zu suchen!",
            ShowText = true,
        };
        Layout(overlay);
        var band = (Border)overlay.FindName("TextBand");
        Assert.NotNull(band);
        Assert.Equal(Visibility.Visible, band.Visibility);

        var geometry = KisscamFrameGeometry.For(1920, 1080);
        var topLeft = band.TranslatePoint(new Point(0, 0), overlay);
        // Nicht mit Banner, Rahmen und Eckherzen kollidieren, im unteren Drittel stehen und innerhalb der Fläche bleiben.
        Assert.True(topLeft.Y > geometry.Banner.Bottom);
        Assert.True(topLeft.Y >= 1080 * 2.0 / 3);
        Assert.True(topLeft.Y + band.ActualHeight <= 1080 - geometry.Thickness);
        Assert.True(topLeft.X >= geometry.CornerHearts[2].Right);
        Assert.True(topLeft.X + band.ActualWidth <= geometry.CornerHearts[3].X);
        Assert.True(band.ActualWidth > 0);
        var text = (TextBlock)overlay.FindName("GuestText");
        Assert.True(text.FontSize > 30);

        var bitmap = Render(overlay);
        Assert.Equal(0, PixelAt(bitmap, 960, 540).A); // Mitte bleibt frei (Ziffern der letzten Sekunden)
        Assert.True(PixelAt(bitmap, 5, 540).A > 200);  // Rahmenband links
    }

    // Zählt helle (weiße) Bildpunkte im Textband unten zwischen den Eckherzen (Rahmen, Eckherzen und Lichterkette liegen außerhalb).
    private static int CountTextPixels(BitmapSource bitmap)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var x0 = (int)(width * 0.22);
        var y0 = (int)(height * 0.70);
        var w = (int)(width * 0.56);
        var h = (int)(height * 0.20);
        var stride = w * 4;
        var pixels = new byte[stride * h];
        bitmap.CopyPixels(new Int32Rect(x0, y0, w, h), pixels, stride, 0);
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 200 && pixels[i + 1] > 200 && pixels[i + 2] > 200)
                count++;
        }
        return count;
    }

    // Die Admin-Vorschau erbt vom Fenster TextFormattingMode=Display; in diesem Modus zeichnete der große Gasttext (und andere
    // große Texte) im gerenderten Bild gar nichts. Das Overlay erzwingt deshalb Ideal für seinen Teilbaum.
    [StaTheory]
    [InlineData(TextFormattingMode.Ideal)]
    [InlineData(TextFormattingMode.Display)]
    public void KisscamOverlay_DrawsTheGuestText_EvenUnderAnInheritedTextFormattingMode(TextFormattingMode inherited)
    {
        WpfCollection.EnsurePackUris();
        var overlay = new KisscamOverlay { Frame = Frame, Text = "Du hast 10 Sekunden Zeit, dir jemanden zum Küssen zu suchen!", ShowText = true };
        var host = new Grid { Width = 1920, Height = 1080 };
        TextOptions.SetTextFormattingMode(host, inherited); // wie AdminWindow (Display)
        host.Children.Add(overlay);
        Layout(host);

        var bitmap = Render(overlay);

        Assert.True(CountTextPixels(bitmap) > 2000, $"Kein Gasttext im Textband ({CountTextPixels(bitmap)} helle Pixel).");
    }

    [StaFact]
    public void KisscamOverlay_WithoutText_HasNoBrightPixelsInTheBand()
    {
        WpfCollection.EnsurePackUris();
        var overlay = new KisscamOverlay { Frame = Frame, Text = "Hallo", ShowText = false };
        Layout(overlay);

        Assert.Equal(0, CountTextPixels(Render(overlay)));
    }

    [StaFact]
    public void KisscamOverlay_HidesTheText_WhenTurnedOffOrEmpty_AndKeepsTheFrame()
    {
        WpfCollection.EnsurePackUris();
        var overlay = new KisscamOverlay { Frame = Frame, Text = "Hallo", ShowText = false };
        Layout(overlay);
        var band = (Border)overlay.FindName("TextBand");
        Assert.Equal(Visibility.Collapsed, band.Visibility);

        overlay.ShowText = true;
        Assert.Equal(Visibility.Visible, band.Visibility);
        overlay.Text = "  ";
        Assert.Equal(Visibility.Collapsed, band.Visibility);

        var bitmap = Render(overlay);
        Assert.True(PixelAt(bitmap, 5, 540).A > 200);
    }

    [StaFact]
    public void KisscamOverlay_WithoutFrame_DrawsNothingButCanBeMeasured()
    {
        WpfCollection.EnsurePackUris();
        var overlay = new KisscamOverlay { ShowText = false };

        Layout(overlay);
        var bitmap = Render(overlay);

        Assert.Equal(0, PixelAt(bitmap, 5, 540).A);
    }

    [StaFact]
    public void KisscamFrameElement_Draws_LikeTheRenderer()
    {
        var element = new KisscamFrameElement { Frame = Frame };
        Layout(element, 1280, 720);

        var bitmap = Render(element, 1280, 720);

        Assert.True(PixelAt(bitmap, 4, 360).A > 200);
        Assert.Equal(0, PixelAt(bitmap, 640, 360).A);
    }

    // ---- HeartFeedbackOverlay

    private static bool IsPinkish((byte B, byte G, byte R, byte A) p) => p.A > 200 && p.R > 200 && p.G < 160;

    [StaFact]
    public void HeartFeedbackOverlay_FillsTheHeartFromBelow_WithTheProgress()
    {
        var overlay = new HeartFeedbackOverlay { ImageWidth = 1920, ImageHeight = 1080 };
        Layout(overlay);

        // Ohne Herz nichts.
        Assert.Equal(0, PixelAt(Render(overlay), 960, 540).A);

        // Voll gefüllt (Herz in der Bildmitte, 30 % der Bildhöhe): die Mitte ist rosa.
        overlay.Heart = new HeartView(new NormPoint(0.5, 0.5), 0.3, 1.0, HeartShape.Classic);
        Assert.True(IsPinkish(PixelAt(Render(overlay), 960, 560)));

        // Halb gefüllt: unten rosa, oben nur der dunkle Grund.
        overlay.Heart = new HeartView(new NormPoint(0.5, 0.5), 0.3, 0.5, HeartShape.Classic);
        var half = Render(overlay);
        Assert.True(IsPinkish(PixelAt(half, 960, 620)));
        var top = PixelAt(half, 920, 440);
        Assert.True(top.A > 0 && !IsPinkish(top));

        // Fortschritt 0: nur Umriss und Grund.
        overlay.Heart = new HeartView(new NormPoint(0.5, 0.5), 0.3, 0.0, HeartShape.Classic);
        Assert.False(IsPinkish(PixelAt(Render(overlay), 960, 620)));
    }

    [StaFact]
    public void HeartFeedbackOverlay_Mirror_PutsTheHeartOnTheOtherSide()
    {
        var overlay = new HeartFeedbackOverlay
        {
            ImageWidth = 1920,
            ImageHeight = 1080,
            Heart = new HeartView(new NormPoint(0.25, 0.5), 0.3, 1.0, HeartShape.Classic),
        };
        Layout(overlay);
        var plain = Render(overlay);
        Assert.True(IsPinkish(PixelAt(plain, 480, 560)));
        Assert.Equal(0, PixelAt(plain, 1440, 560).A);

        overlay.Mirror = true;
        var mirrored = Render(overlay);
        Assert.True(IsPinkish(PixelAt(mirrored, 1440, 560)));
        Assert.Equal(0, PixelAt(mirrored, 480, 560).A);
    }

    [StaFact]
    public void HeartFeedbackOverlay_ToleratesAnEmptyView_AndUnknownImageSize()
    {
        var overlay = new HeartFeedbackOverlay { Heart = new HeartView(new NormPoint(0.5, 0.5), 0.2, 0.5, HeartShape.Roof) };
        Layout(overlay, 0, 0);

        var ex = Record.Exception(() =>
        {
            Layout(overlay, 640, 360); // Bildgröße unbekannt: 16:9
            Render(overlay, 640, 360);
        });

        Assert.Null(ex);
    }
}
