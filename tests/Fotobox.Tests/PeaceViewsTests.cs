using System.Text;
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

/// <summary>Gast-Ansichten der Peaceerkennung: Bartformen, <see cref="PeaceOverlay"/>, <see cref="SelfDestructOverlay"/>, Töne.</summary>
[Collection(WpfCollection.Name)]
public class PeaceViewsTests
{
    private static readonly MoustacheStyle[] Styles = [MoustacheStyle.Twirl, MoustacheStyle.Walrus, MoustacheStyle.Chevron, MoustacheStyle.Brush];

    private static void Layout(FrameworkElement element, double width = 1920, double height = 1080)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static HandView Hand(int id, double progress, bool isPeace = true)
    {
        var points = Enumerable.Range(0, 21).Select(i => new HandPoint(0.4 + i * 0.006, 0.3 + i * 0.01, 0)).ToArray();
        return new HandView(id, points, new NormRect(0.4, 0.3, 0.15, 0.3), isPeace, progress, null);
    }

    private static MoustacheView View(int faceId, MoustacheStyle style, double x = 0.5, double y = 0.5, double angle = 0) =>
        new(faceId, style, new MoustachePose(new NormPoint(x, y), 200, angle));

    private static void RenderToBitmap(FrameworkElement element, int width = 1920, int height = 1080)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
    }

    // ---- MoustacheShapes

    [Theory]
    [InlineData(MoustacheStyle.Twirl)]
    [InlineData(MoustacheStyle.Walrus)]
    [InlineData(MoustacheStyle.Chevron)]
    [InlineData(MoustacheStyle.Brush)]
    public void Shapes_AreFrozen_NonEmpty_WidthOne_AndSymmetric(MoustacheStyle style)
    {
        var geometry = MoustacheShapes.Get(style);

        Assert.True(geometry.IsFrozen);
        var bounds = geometry.Bounds;
        Assert.False(bounds.IsEmpty);
        Assert.InRange(bounds.Width, 0.99, 1.01);
        Assert.InRange(bounds.Height, 0.02, 0.5);
        Assert.InRange(bounds.X + bounds.Width / 2, -0.01, 0.01); // um die y-Achse zentriert
    }

    [Theory]
    [InlineData(MoustacheStyle.Twirl)]
    [InlineData(MoustacheStyle.Walrus)]
    [InlineData(MoustacheStyle.Chevron)]
    [InlineData(MoustacheStyle.Brush)]
    public void Shapes_AreMirrorSymmetric(MoustacheStyle style)
    {
        var geometry = MoustacheShapes.Get(style);
        var bounds = geometry.Bounds;

        // Stichprobe: Ein Punkt in der Form hat sein Spiegelbild ebenfalls in der Form.
        var step = 0.02;
        for (var x = bounds.X + step; x < bounds.Right; x += step)
        {
            for (var y = bounds.Y + step; y < bounds.Bottom; y += step)
            {
                var inside = geometry.FillContains(new Point(x, y));
                var mirrored = geometry.FillContains(new Point(-x, y));
                if (inside != mirrored)
                {
                    // An Rändern darf eine Rasterzelle abweichen: Nachbarpunkt muss dann übereinstimmen.
                    Assert.True(geometry.FillContains(new Point(-x + 0.004, y)) || geometry.FillContains(new Point(-x - 0.004, y)) || !inside,
                        $"{style}: ({x:F2}, {y:F2}) nicht symmetrisch");
                }
            }
        }
    }

    [Fact]
    public void Shapes_RandomFallsBackToTwirl()
    {
        Assert.Same(MoustacheShapes.Get(MoustacheStyle.Twirl), MoustacheShapes.Get(MoustacheStyle.Random));
    }

    [Fact]
    public void Shapes_AreDistinct()
    {
        var geometries = Styles.Select(MoustacheShapes.Get).Distinct().ToList();
        Assert.Equal(4, geometries.Count);
    }

    // ---- MoustacheRenderer

    [StaFact]
    public void RenderOnto_KeepsSizeAndDpi_AndDarkensTheMoustacheCenter()
    {
        var white = BitmapSource.Create(200, 100, 96, 96, PixelFormats.Pbgra32, null, Enumerable.Repeat((byte)255, 200 * 100 * 4).ToArray(), 200 * 4);
        var pose = new MoustachePose(new NormPoint(0.5, 0.5), 100, 0);

        var result = MoustacheRenderer.RenderOnto(white, [(pose, MoustacheStyle.Chevron, MoustacheColor.DarkBrown)]);

        Assert.True(result.IsFrozen);
        Assert.Equal(200, result.PixelWidth);
        Assert.Equal(100, result.PixelHeight);
        Assert.Equal(96, result.DpiX, 3);
        Assert.Equal(96, result.DpiY, 3);
        Assert.Equal(PixelFormats.Pbgra32, result.Format);

        // Chevron: Die Oberlippe liegt in (0,0), der Block hängt nach oben (y < 0): ein Stück darüber sitzt Bartfarbe.
        var pixel = new byte[4];
        result.CopyPixels(new Int32Rect(100, 50 - 4, 1, 1), pixel, 4, 0);
        Assert.True(pixel[0] < 90 && pixel[1] < 90 && pixel[2] < 90, $"Pixel nicht dunkel: {pixel[2]},{pixel[1]},{pixel[0]}");

        // Weit außerhalb bleibt das Foto weiß.
        result.CopyPixels(new Int32Rect(5, 5, 1, 1), pixel, 4, 0);
        Assert.Equal(255, pixel[0]);
    }

    [StaFact]
    public void RenderOnto_WithoutPlan_ReturnsAnUnchangedCopy()
    {
        var gray = BitmapSource.Create(20, 10, 96, 96, PixelFormats.Pbgra32, null, Enumerable.Repeat((byte)128, 20 * 10 * 4).ToArray(), 20 * 4);

        var result = MoustacheRenderer.RenderOnto(gray, []);

        var pixel = new byte[4];
        result.CopyPixels(new Int32Rect(3, 3, 1, 1), pixel, 4, 0);
        Assert.Equal(128, pixel[0]);
    }

    [StaFact]
    public void Draw_IgnoresInvalidWidth()
    {
        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();
        MoustacheRenderer.Draw(dc, MoustacheStyle.Twirl, new Point(10, 10), 0, 0, false);
        MoustacheRenderer.Draw(dc, MoustacheStyle.Twirl, new Point(10, 10), double.NaN, 0, false);
        MoustacheRenderer.Draw(dc, MoustacheStyle.Twirl, new Point(10, 10), -5, 0, true);
    }

    // ---- PeaceOverlay

    [StaFact]
    public void PeaceOverlay_RendersWithoutData()
    {
        var overlay = new PeaceOverlay();
        Layout(overlay);
        RenderToBitmap(overlay);
    }

    [StaTheory]
    [InlineData(OverlayFit.Uniform, false)]
    [InlineData(OverlayFit.UniformToFill, false)]
    [InlineData(OverlayFit.Uniform, true)]
    [InlineData(OverlayFit.UniformToFill, true)]
    public void PeaceOverlay_RendersWithData(OverlayFit fit, bool mirror)
    {
        var overlay = new PeaceOverlay
        {
            Fit = fit, Mirror = mirror, ImageWidth = 1280, ImageHeight = 720, ShowHoldProgress = true, ShowLandmarks = true,
            Moustaches = Styles.Select((s, i) => View(i + 1, s, 0.2 + i * 0.2, 0.5, i * 0.2 - 0.3)).ToList(),
            Hands = [Hand(1, 0.0), Hand(2, 0.5, isPeace: false), Hand(3, 1.0)],
        };
        Layout(overlay, 1000, 1000);
        RenderToBitmap(overlay, 1000, 1000);
    }

    [StaFact]
    public void PeaceOverlay_DrawsTheMoustacheAtTheMappedPosition()
    {
        var overlay = new PeaceOverlay { ImageWidth = 1920, ImageHeight = 1080, Moustaches = [View(1, MoustacheStyle.Chevron, 0.25, 0.5)] };
        Layout(overlay);
        var bitmap = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(overlay);

        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(480, 540 - 14, 1, 1), pixel, 4, 0);
        Assert.True(pixel[3] > 200); // deckend an der Bartstelle
        bitmap.CopyPixels(new Int32Rect(1440, 540 - 14, 1, 1), pixel, 4, 0);
        Assert.Equal(0, pixel[3]);

        // Gespiegelt wandert er auf die andere Seite.
        overlay.Mirror = true;
        Layout(overlay);
        bitmap = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(overlay);
        bitmap.CopyPixels(new Int32Rect(1440, 540 - 14, 1, 1), pixel, 4, 0);
        Assert.True(pixel[3] > 200);
    }

    [StaFact]
    public void PeaceOverlay_FacePoints_AreDrawnOnlyWhenEnabled_AtTheMappedPositions()
    {
        var face = PeaceHandFixtures.Face(0.3, 0.4, 0.12);
        var overlay = new PeaceOverlay { ImageWidth = 1920, ImageHeight = 1080, Fit = OverlayFit.Uniform, Faces = [face] };
        Layout(overlay);

        byte AlphaAt(NormPoint p)
        {
            var bitmap = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(overlay);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)Math.Round(p.X * 1920), (int)Math.Round(p.Y * 1080), 1, 1), pixel, 4, 0);
            return pixel[3];
        }

        Assert.Equal(0, AlphaAt(face.Nose)); // ausgeschaltet: nichts gezeichnet
        overlay.ShowFaceLandmarks = true;
        Layout(overlay);
        Assert.True(AlphaAt(face.Nose) > 200);
        Assert.True(AlphaAt(face.RightEye) > 200);
        Assert.True(AlphaAt(face.MouthLeft) > 200);

        // Gespiegelt wandern die Punkte auf die andere Seite.
        overlay.Mirror = true;
        Layout(overlay);
        Assert.True(AlphaAt(new NormPoint(1 - face.RightEye.X, face.RightEye.Y)) > 200);
        Assert.Equal(0, AlphaAt(face.RightEye));
    }

    [StaFact]
    public void PeaceOverlay_UpdatesWithChangingDataWithoutException()
    {
        var overlay = new PeaceOverlay { ImageWidth = 1920, ImageHeight = 1080, ShowLandmarks = true, ShowHoldProgress = true };
        Layout(overlay);

        for (var i = 0; i < 5; i++)
        {
            overlay.Moustaches = [View(1, MoustacheStyle.Twirl, 0.3 + i * 0.01), View(2 + i % 2, MoustacheStyle.Walrus)];
            overlay.Hands = [Hand(1, i / 5.0)];
            Layout(overlay);
        }
        overlay.Moustaches = null;
        overlay.Hands = null;
        Layout(overlay);
        RenderToBitmap(overlay);
    }

    [Theory]
    [InlineData(120, 100, 110)]
    [InlineData(120, 1000, 200)]
    [InlineData(40, 0, 40)]
    [InlineData(200, 10, 105)]
    [InlineData(60, 20, 40)]
    public void PeaceOverlay_GlideDuration_FollowsTheMeasuredIntervalWithinLimits(double previous, double interval, double expected)
    {
        Assert.Equal(expected, PeaceOverlay.GlideDuration(previous, interval), 6);
    }

    [StaFact]
    public void PeaceOverlay_GlideDuration_StartsAtDefault_AndStaysWithinLimits()
    {
        var overlay = new PeaceOverlay { ImageWidth = 1920, ImageHeight = 1080 };
        Layout(overlay);
        Assert.Equal(PeaceOverlay.DefaultGlideMs, overlay.MoustacheGlideMs);

        for (var i = 0; i < 6; i++)
            overlay.Moustaches = [View(1, MoustacheStyle.Twirl, 0.3 + i * 0.02)]; // Aktualisierungen ohne Pause

        Assert.InRange(overlay.MoustacheGlideMs, PeaceOverlay.MinGlideMs, PeaceOverlay.MaxGlideMs);
        Assert.True(overlay.MoustacheGlideMs < PeaceOverlay.DefaultGlideMs);
    }

    [StaFact]
    public void PeaceOverlay_AnimationTimerIsNotSubscribedWhenNotLoaded()
    {
        var overlay = new PeaceOverlay { ImageWidth = 1920, ImageHeight = 1080 };
        Layout(overlay);

        overlay.Moustaches = [View(1, MoustacheStyle.Twirl, 0.3)];
        overlay.Moustaches = [View(1, MoustacheStyle.Twirl, 0.4)];

        Assert.False(overlay.IsAnimating); // nicht geladen: kein Takt
    }

    [StaFact]
    public void PeaceOverlay_AnimatesOnlyWhileLoadedAndVisible()
    {
        var overlay = new PeaceOverlay { ImageWidth = 1920, ImageHeight = 1080 };
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Width = 800, Height = 450,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0, Content = overlay,
        };
        try
        {
            window.Show();
            overlay.Moustaches = [View(1, MoustacheStyle.Twirl, 0.3)];
            overlay.Moustaches = [View(1, MoustacheStyle.Twirl, 0.6)];
            Assert.True(overlay.IsAnimating);

            overlay.Visibility = Visibility.Collapsed;
            Assert.False(overlay.IsAnimating);
        }
        finally
        {
            window.Close();
        }
        Assert.False(overlay.IsAnimating);
    }

    // ---- SelfDestructOverlay

    [StaFact]
    public void SelfDestructOverlay_LoadsAndShowsTime()
    {
        var overlay = new SelfDestructOverlay { SecondsRemaining = 7 };
        Layout(overlay);

        Assert.Equal(7, overlay.SecondsRemaining);
        Assert.Contains("00:07", FindText(overlay));
        overlay.SecondsRemaining = 2;
        Layout(overlay);
        Assert.Contains("00:02", FindText(overlay));
        RenderToBitmap(overlay);
    }

    [StaFact]
    public void SelfDestructOverlay_ChangesTheSubtitleOverTime()
    {
        var overlay = new SelfDestructOverlay { SecondsRemaining = 10 };
        Layout(overlay);
        var texts = new List<string>();
        foreach (var seconds in new[] { 10, 6, 2 })
        {
            overlay.SecondsRemaining = seconds;
            texts.Add(FindText(overlay));
        }

        Assert.Equal(3, texts.Distinct().Count());
    }

    [StaFact]
    public void SelfDestructOverlay_PlayExplosionDoesNotThrow_AlsoWhenHidden()
    {
        var overlay = new SelfDestructOverlay { SecondsRemaining = 1 };
        Layout(overlay);
        overlay.PlayExplosion();
        overlay.Visibility = Visibility.Collapsed;
        overlay.PlayExplosion();
        Assert.False(overlay.AreAnimationsRunning);
    }

    [StaFact]
    public void SelfDestructOverlay_AnimationsRunOnlyWhileVisible()
    {
        var overlay = new SelfDestructOverlay { SecondsRemaining = 5 };
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Width = 800, Height = 450,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0, Content = overlay,
        };
        try
        {
            window.Show();
            Assert.True(overlay.AreAnimationsRunning);

            overlay.PlayExplosion();
            overlay.Visibility = Visibility.Collapsed;
            Assert.False(overlay.AreAnimationsRunning);

            overlay.Visibility = Visibility.Visible;
            Assert.True(overlay.AreAnimationsRunning);
            overlay.SecondsRemaining = 2; // schnelle Stufe, wirft nicht
            Assert.True(overlay.AreAnimationsRunning);
        }
        finally
        {
            window.Close();
        }
    }

    private static string FindText(DependencyObject root)
    {
        var sb = new StringBuilder();
        Collect(root, sb);
        return sb.ToString();
    }

    private static void Collect(DependencyObject node, StringBuilder sb)
    {
        if (node is TextBlock block)
            sb.Append(block.Text).Append('|');
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            Collect(child, sb);
        if (node is ContentControl { Content: DependencyObject content })
            Collect(content, sb);
    }

    // ---- ToneSynth

    private static short[] Samples(FunSoundCue cue)
    {
        var wav = ToneSynth.Create(cue);
        var samples = new short[(wav.Length - 44) / 2];
        Buffer.BlockCopy(wav, 44, samples, 0, wav.Length - 44);
        return samples;
    }

    [Theory]
    [InlineData(FunSoundCue.SelfDestructTick)]
    [InlineData(FunSoundCue.SelfDestructTickFast)]
    [InlineData(FunSoundCue.Explosion)]
    public void PeaceTones_AreShort_NotSilent_NotClipping_AndFadeAtTheEdges(FunSoundCue cue)
    {
        var wav = ToneSynth.Create(cue);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(wav.Length - 44, BitConverter.ToInt32(wav, 40));

        var samples = Samples(cue);
        var milliseconds = samples.Length * 1000.0 / ToneSynth.SampleRate;
        Assert.InRange(milliseconds, 100, 850);

        var peak = samples.Max(s => Math.Abs((int)s));
        Assert.InRange(peak, short.MaxValue * 0.2, short.MaxValue * 0.4); // wie die übrigen Cues, kein Clipping
        Assert.True(Math.Abs((int)samples[0]) < short.MaxValue * 0.1);
        Assert.True(Math.Abs((int)samples[^1]) < short.MaxValue * 0.1);
    }

    [Theory]
    [InlineData(FunSoundCue.SelfDestructTick)]
    [InlineData(FunSoundCue.SelfDestructTickFast)]
    [InlineData(FunSoundCue.Explosion)]
    public void PeaceTones_AreDeterministic(FunSoundCue cue)
    {
        var first = ToneSynth.Create(cue);
        // Neuer Aufbau (ohne Cache) über eine frische Berechnung ist nicht zugänglich; Wiederholung muss dieselben Bytes liefern.
        Assert.Equal(first, ToneSynth.Create(cue));
        Assert.Same(first, ToneSynth.Create(cue));
    }

    [Fact]
    public void PeaceTones_DifferFromEachOther()
    {
        var tick = ToneSynth.Create(FunSoundCue.SelfDestructTick);
        var fast = ToneSynth.Create(FunSoundCue.SelfDestructTickFast);
        var boom = ToneSynth.Create(FunSoundCue.Explosion);
        Assert.NotEqual(tick.Length, boom.Length);
        Assert.False(tick.AsSpan().SequenceEqual(fast));
        Assert.True(fast.Length < tick.Length);
    }

    [Fact]
    public void ExplosionTone_DecaysTowardsTheEnd()
    {
        var samples = Samples(FunSoundCue.Explosion);
        var quarter = samples.Length / 4;
        double Rms(IEnumerable<short> part) => Math.Sqrt(part.Average(s => (double)s * s));

        Assert.True(Rms(samples.Take(quarter)) > 2 * Rms(samples.Skip(samples.Length - quarter)));
    }
}
