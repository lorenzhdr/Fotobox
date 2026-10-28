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

        // Übersicht: 7 Formen (Spalten) × 6 Farben (Zeilen), einmal auf hellem Hautton und einmal auf dunklem Hintergrund.
        const int cellW = 300, cellH = 340, beardWidth = 230;
        foreach (var (name, background) in new[]
        {
            ("skin", Color.FromRgb(0xEE, 0xC4, 0x9C)),
            ("dark", Color.FromRgb(0x23, 0x26, 0x2E)),
        })
        {
            var styles = MoustacheStyles.All;
            var colors = MoustacheStyles.AllColors;
            foreach (var s in styles)
            {
                foreach (var c in colors)
                    MoustacheTextures.Get(s, c, beardWidth); // Draw erzeugt nie selbst: erst fertig machen
            }
            var width = cellW * styles.Count;
            var height = cellH * colors.Count;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, width, height));
                for (var row = 0; row < colors.Count; row++)
                {
                    for (var col = 0; col < styles.Count; col++)
                    {
                        // Ursprung (Oberlippe) bei 38 % der Zellhöhe: Hängeenden haben darunter Platz, Dalí-Spitzen darüber.
                        var origin = new Point(col * cellW + cellW / 2.0, row * cellH + cellH * 0.30);
                        MoustacheRenderer.Draw(dc, styles[col], colors[row], origin, beardWidth, 0, mirror: false);
                    }
                }
                for (var col = 0; col < styles.Count; col++)
                {
                    var text = new FormattedText(styles[col].ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 22, background.R > 128 ? Brushes.Black : Brushes.White, 1.0);
                    dc.DrawText(text, new Point(col * cellW + 8, 4));
                }
            }
            SavePng(RenderVisual(visual, width, height), Path.Combine(dir, $"peace_moustaches_{name}.png"));
        }

        // Großansicht: jede Form in Breite 900 (Stufe 1024) auf Hautton, zur Prüfung der Haardetails.
        foreach (var style in MoustacheStyles.All)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                MoustacheTextures.Get(style, MoustacheColor.DarkBrown, 900);
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xEE, 0xC4, 0x9C)), null, new Rect(0, 0, 1000, 1000));
                MoustacheRenderer.Draw(dc, style, MoustacheColor.DarkBrown, new Point(500, 330), 900, 0, mirror: false);
            }
            SavePng(RenderVisual(visual, 1000, 1000), Path.Combine(dir, $"peace_moustache_large_{style}.png"));
        }
        Assert.True(File.Exists(Path.Combine(dir, "peace_moustaches_skin.png")));
    }

    [Trait("Category", "Visual")]
    [StaFact]
    public void Render_MoustachesOnPlaceholderFaces()
    {
        var dir = RenderDir();
        if (dir is null)
            return;

        // Sieben Formen auf Platzhaltergesichtern (4 + 3), Farben und Winkel gemischt; Bart sitzt zwischen Nase und Mund, mitgedreht.
        MoustacheStyle[] styles = [.. MoustacheStyles.All];
        MoustacheColor[] colors = [MoustacheColor.DarkBrown, MoustacheColor.Ginger, MoustacheColor.Black, MoustacheColor.Gray,
            MoustacheColor.Blond, MoustacheColor.Brown, MoustacheColor.Black];
        double[] angles = [0.0, 0.0, 0.0, 0.0, 0.3, -0.25, 0.0];
        const double size = 380;
        var poses = new List<(MoustachePose Pose, MoustacheStyle Style, MoustacheColor Color)>();
        var photoVisual = new DrawingVisual();
        using (var dc = photoVisual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x8F, 0xA8, 0xBF), Color.FromRgb(0x4C, 0x5E, 0x70), 90), null, new Rect(0, 0, 1920, 1080));
            for (var i = 0; i < styles.Length; i++)
            {
                var nose = new Point(260 + (i % 4) * 470, 230 + (i / 4) * 520);
                DrawFace(dc, nose, size, angles[i]);
                var offset = new Vector(0, size * 0.115);
                offset = new Matrix(Math.Cos(angles[i]), Math.Sin(angles[i]), -Math.Sin(angles[i]), Math.Cos(angles[i]), 0, 0).Transform(offset);
                poses.Add((new MoustachePose(new NormPoint((nose.X + offset.X) / 1920, (nose.Y + offset.Y) / 1080), size * 0.52, angles[i]),
                    styles[i], colors[i]));
            }
        }
        var photo = RenderVisual(photoVisual, 1920, 1080);
        SavePng(photo, Path.Combine(dir, "peace_moustache_photo_without.png"));

        // „Bart auf Foto“: genau der Weg des Fotos (RenderOnto), Haartextur mit Schatten auf der Haut.
        foreach (var (p, s, c) in poses)
            MoustacheTextures.Get(s, c, p.WidthPx); // RenderOnto nutzt nur fertige Texturen
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var decorated = MoustacheRenderer.RenderOnto(photo, poses);
        timer.Stop();
        SavePng(decorated, Path.Combine(dir, "peace_moustache_photo.png"));
        File.WriteAllText(Path.Combine(dir, "peace_moustache_photo_time.txt"), $"RenderOnto (7 Bärte, Texturen fertig): {timer.ElapsedMilliseconds} ms");

        // Gespiegelte Ansicht über das Overlay: Der Bart folgt dem gespiegelten Gesicht, der Winkel kehrt sich um.
        MoustacheTextures.Get(MoustacheStyle.Twirl, MoustacheColor.Ginger, 300);
        MoustacheTextures.Get(MoustacheStyle.Handlebar, MoustacheColor.Black, 300);
        MoustacheTextures.Get(MoustacheStyle.Walrus, MoustacheColor.Brown, 300);
        long now = 0;
        var pose = new MoustachePose(new NormPoint(0.3, 0.5), 300, 0.3);
        var overlay = new PeaceOverlay
        {
            ImageWidth = 1920, ImageHeight = 1080, Mirror = true, Clock = () => now,
            Moustaches =
            [
                new MoustacheView(1, MoustacheStyle.Twirl, pose, MoustacheColor.Ginger),
                new MoustacheView(2, MoustacheStyle.Handlebar, pose with { Center = new NormPoint(0.7, 0.5), AngleRad = -0.2 }, MoustacheColor.Black),
            ],
        };
        now = 5 * System.Diagnostics.Stopwatch.Frequency; // Plopp vorbei
        var stage = new Grid { Width = 1920, Height = 1080, Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xD8, 0xC0)) };
        stage.Children.Add(overlay);
        RenderElement(Path.Combine(dir, "peace_moustaches_mirrored.png"), stage);

        // Plopp: Bild bei 120 ms (federnd über 1 hinaus) und bei Wippen (Versatz und Winkel am Limit).
        now = 0;
        var popOverlay = new PeaceOverlay { ImageWidth = 1920, ImageHeight = 1080, Clock = () => now };
        popOverlay.Moustaches = [new MoustacheView(1, MoustacheStyle.Walrus, pose, MoustacheColor.Brown)];
        now = (long)(0.19 * System.Diagnostics.Stopwatch.Frequency);
        var popStage = new Grid { Width = 1920, Height = 1080, Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xD8, 0xC0)) };
        popStage.Children.Add(popOverlay);
        RenderElement(Path.Combine(dir, "peace_moustache_pop_190ms.png"), popStage);
        Assert.True(File.Exists(Path.Combine(dir, "peace_moustaches_faces.png")) || File.Exists(Path.Combine(dir, "peace_moustache_photo.png")));
    }

    [Trait("Category", "Visual")]
    [StaFact]
    public void Measure_TextureCreationAndDrawTimes()
    {
        var dir = RenderDir();
        if (dir is null)
            return;

        var lines = new List<string>();
        foreach (var tier in MoustacheTextures.Tiers)
        {
            foreach (var style in new[] { MoustacheStyle.Brush, MoustacheStyle.Twirl, MoustacheStyle.Walrus, MoustacheStyle.FuManchu })
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var texture = MoustacheTextures.Create(style, MoustacheColor.DarkBrown, tier);
                lines.Add($"Erzeugen {style,-10} Stufe {tier,4}: {sw.ElapsedMilliseconds,5} ms  ({texture.Bitmap.PixelWidth} x {texture.Bitmap.PixelHeight})");
            }
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            MoustacheTextures.Get(MoustacheStyle.Walrus, MoustacheColor.Brown, 300);
            MoustacheRenderer.Draw(dc, MoustacheStyle.Walrus, MoustacheColor.Brown, new Point(100, 100), 300, 0.1, true, 1, 0.01, 0.01);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 1000; i++)
                MoustacheRenderer.Draw(dc, MoustacheStyle.Walrus, MoustacheColor.Brown, new Point(100 + i % 50, 100), 300, 0.1, true, 1, 0.01, 0.01);
            lines.Add($"Draw (DrawImage, Aufzeichnen): {sw.Elapsed.TotalMilliseconds / 1000:F4} ms je Aufruf");
        }
        File.WriteAllLines(Path.Combine(dir, "peace_moustache_timings.txt"), lines);
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
