using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Fotobox.Views.Booth.Peace;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Sichtprüfung per Bilddatei, kein Test im engeren Sinn: Läuft nur, wenn die Umgebungsvariable <c>FOTOBOX_RENDER_DIR</c> auf einen
/// Ordner zeigt (sonst sofort beendet, auch in der CI). Rendert die vier Bartformen, Bärte auf einem gezeichneten Platzhaltergesicht,
/// das Selbstzerstörungs-Overlay (7 s und 2 s, Explosion) und den Fortschrittsring mit Handpunkten als PNG.
/// Aufruf: <c>FOTOBOX_RENDER_DIR=C:\pfad\render dotnet test --filter "FullyQualifiedName~PeaceVisualRenderTests"</c>.
/// </summary>
[Collection(WpfCollection.Name)]
public class PeaceVisualRenderTests
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

    private static BitmapSource RenderVisual(Visual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static void RenderElement(string path, FrameworkElement element, int width = 1920, int height = 1080, Action<FrameworkElement>? before = null)
    {
        var window = new Window
        {
            SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0, Content = element,
        };
        try
        {
            window.Show();
            element.Measure(new Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();
            before?.Invoke(element);
            element.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            SavePng(RenderVisual(element, width, height), path);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Einfaches Cartoon-Gesicht (Hautton, Augen, Nase, Mund) mit dem Nasen-/Mundpunkt als Bezug für den Bart.</summary>
    private static void DrawFace(DrawingContext dc, Point nose, double size, double angleRad)
    {
        dc.PushTransform(new RotateTransform(angleRad * 180 / Math.PI, nose.X, nose.Y));
        var skin = new SolidColorBrush(Color.FromRgb(0xF1, 0xC2, 0x9A));
        var dark = new SolidColorBrush(Color.FromRgb(0x2A, 0x1A, 0x12));
        dc.DrawEllipse(skin, new Pen(new SolidColorBrush(Color.FromRgb(0xC8, 0x92, 0x6A)), size * 0.012), new Point(nose.X, nose.Y - size * 0.12), size * 0.46, size * 0.58);
        // Augen
        dc.DrawEllipse(Brushes.White, null, new Point(nose.X - size * 0.20, nose.Y - size * 0.34), size * 0.07, size * 0.045);
        dc.DrawEllipse(Brushes.White, null, new Point(nose.X + size * 0.20, nose.Y - size * 0.34), size * 0.07, size * 0.045);
        dc.DrawEllipse(dark, null, new Point(nose.X - size * 0.20, nose.Y - size * 0.34), size * 0.03, size * 0.03);
        dc.DrawEllipse(dark, null, new Point(nose.X + size * 0.20, nose.Y - size * 0.34), size * 0.03, size * 0.03);
        // Nase
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xE3, 0xA5, 0x7C)), null, new Point(nose.X, nose.Y - size * 0.04), size * 0.06, size * 0.08);
        // Mund
        var mouth = new StreamGeometry();
        using (var ctx = mouth.Open())
        {
            ctx.BeginFigure(new Point(nose.X - size * 0.14, nose.Y + size * 0.22), false, false);
            ctx.QuadraticBezierTo(new Point(nose.X, nose.Y + size * 0.34), new Point(nose.X + size * 0.14, nose.Y + size * 0.22), true, true);
        }
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(0xA8, 0x3A, 0x3A)), size * 0.02) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, mouth);
        dc.Pop();
    }

    [Trait("Category", "Visual")]
    [StaFact]
    public void Render_AllMoustacheShapes()
    {
        var dir = RenderDir();
        if (dir is null)
            return;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF4, 0xEC, 0xDD)), null, new Rect(0, 0, 1920, 540));
            var styles = new[] { MoustacheStyle.Twirl, MoustacheStyle.Walrus, MoustacheStyle.Chevron, MoustacheStyle.Brush };
            for (var i = 0; i < styles.Length; i++)
            {
                var center = new Point(240 + i * 480, 270);
                MoustacheRenderer.Draw(dc, styles[i], center, 400, 0, mirror: false);
                var text = new FormattedText(styles[i].ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 32, Brushes.Black, 1.0);
                dc.DrawText(text, new Point(center.X - text.Width / 2, 470));
            }
        }
        SavePng(RenderVisual(visual, 1920, 540), Path.Combine(dir, "peace_moustaches.png"));
        Assert.True(File.Exists(Path.Combine(dir, "peace_moustaches.png")));
    }

    [Trait("Category", "Visual")]
    [StaFact]
    public void Render_MoustachesOnPlaceholderFaces()
    {
        var dir = RenderDir();
        if (dir is null)
            return;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x3A, 0x3D, 0x45), Color.FromRgb(0x1C, 0x1E, 0x24), 90), null, new Rect(0, 0, 1920, 1080));
            var styles = new[] { MoustacheStyle.Twirl, MoustacheStyle.Walrus, MoustacheStyle.Chevron, MoustacheStyle.Brush, MoustacheStyle.Twirl, MoustacheStyle.Walrus };
            var angles = new[] { 0.0, 0.0, 0.0, 0.0, 0.35, -0.3 };
            for (var i = 0; i < styles.Length; i++)
            {
                var nose = new Point(320 + (i % 3) * 640, 250 + (i / 3) * 520);
                const double size = 380;
                DrawFace(dc, nose, size, angles[i]);
                // Bart sitzt zwischen Nase und Mund, mitgedreht.
                var offset = new Vector(0, size * 0.115);
                offset = new Matrix(Math.Cos(angles[i]), Math.Sin(angles[i]), -Math.Sin(angles[i]), Math.Cos(angles[i]), 0, 0).Transform(offset);
                MoustacheRenderer.Draw(dc, styles[i], new Point(nose.X + offset.X, nose.Y + offset.Y), size * 0.52, angles[i], mirror: false);
            }
        }
        SavePng(RenderVisual(visual, 1920, 1080), Path.Combine(dir, "peace_moustaches_faces.png"));

        // Gespiegelte Ansicht über das Overlay: Der Bart folgt dem gespiegelten Gesicht, der Winkel kehrt sich um.
        var pose = new MoustachePose(new NormPoint(0.3, 0.5), 300, 0.3);
        var overlay = new PeaceOverlay
        {
            ImageWidth = 1920, ImageHeight = 1080, Mirror = true,
            Moustaches = [new MoustacheView(1, MoustacheStyle.Twirl, pose), new MoustacheView(2, MoustacheStyle.Chevron, pose with { Center = new NormPoint(0.7, 0.5), AngleRad = -0.2 })],
        };
        var stage = new Grid { Width = 1920, Height = 1080, Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xD8, 0xC0)) };
        stage.Children.Add(overlay);
        RenderElement(Path.Combine(dir, "peace_moustaches_mirrored.png"), stage);
        Assert.True(File.Exists(Path.Combine(dir, "peace_moustaches_faces.png")));
    }

    [Trait("Category", "Visual")]
    [StaFact]
    public void Render_SelfDestructOverlay()
    {
        var dir = RenderDir();
        if (dir is null)
            return;

        foreach (var seconds in new[] { 7, 2 })
        {
            var stage = Stage();
            stage.Children.Add(new SelfDestructOverlay { SecondsRemaining = seconds });
            RenderElement(Path.Combine(dir, $"peace_selfdestruct_{seconds}s.png"), stage);
        }

        var explosionStage = Stage();
        var explosion = new SelfDestructOverlay { SecondsRemaining = 0 };
        explosionStage.Children.Add(explosion);
        // Etwa 250 ms nach dem Blitz: Puls, Glitch-Balken und Versatz sind sichtbar.
        RenderElement(Path.Combine(dir, "peace_selfdestruct_explosion.png"), explosionStage, before: _ =>
        {
            explosion.PlayExplosion();
            Pump(TimeSpan.FromMilliseconds(250));
        });
        Assert.True(File.Exists(Path.Combine(dir, "peace_selfdestruct_2s.png")));
    }

    [Trait("Category", "Visual")]
    [StaFact]
    public void Render_ProgressRingAndLandmarks()
    {
        var dir = RenderDir();
        if (dir is null)
            return;

        var stage = Stage();
        stage.Children.Add(new PeaceOverlay
        {
            ImageWidth = 1920, ImageHeight = 1080, ShowHoldProgress = true, ShowLandmarks = true,
            Hands = [Hand(1, 0.30, 0.5, 0.65, isPeace: true), Hand(2, 0.70, 0.5, 1.0, isPeace: true), Hand(3, 0.5, 0.3, 0.0, isPeace: false)],
        });
        RenderElement(Path.Combine(dir, "peace_hands.png"), stage);
        Assert.True(File.Exists(Path.Combine(dir, "peace_hands.png")));
    }

    /// <summary>Lässt den Dispatcher (und damit die Animationen) die angegebene Zeit laufen.</summary>
    private static void Pump(TimeSpan duration)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static Grid Stage() => new()
    {
        Width = 1920, Height = 1080, ClipToBounds = true,
        Background = new LinearGradientBrush(Color.FromRgb(0x4A, 0x4D, 0x55), Color.FromRgb(0x22, 0x24, 0x2A), 90),
    };

    /// <summary>Peace-Hand mit gestrecktem Zeige-/Mittelfinger (V) um die Handmitte (cx, cy), gebeugten übrigen Fingern.</summary>
    private static HandView Hand(int id, double cx, double cy, double progress, bool isPeace)
    {
        const double aspect = 1080.0 / 1920;
        HandPoint P(double dx, double dy) => new(cx + dx * aspect, cy + dy, 0);
        HandPoint[] points =
        [
            P(0, 0.16),
            P(-0.04, 0.12), P(-0.065, 0.08), P(-0.08, 0.05), P(-0.095, 0.02),   // Daumen
            P(-0.03, 0.02), P(-0.045, -0.04), P(-0.06, -0.09), P(-0.075, -0.14), // Zeigefinger
            P(0.0, 0.0), P(0.005, -0.06), P(0.01, -0.11), P(0.015, -0.16),       // Mittelfinger
            P(0.03, 0.02), P(0.03, 0.0), P(0.025, 0.03), P(0.02, 0.05),          // Ringfinger (gebeugt)
            P(0.055, 0.04), P(0.055, 0.02), P(0.05, 0.05), P(0.045, 0.07),       // kleiner Finger (gebeugt)
        ];
        var box = new NormRect(cx - 0.08 * aspect, cy - 0.17, 0.16 * aspect, 0.34);
        return new HandView(id, points, box, isPeace, progress, null);
    }
}
