using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Core.Peace;
using Fotobox.Views.Booth.Peace;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Haartexturen der Bärte: Cache, Auflösungsstufen, Ursprung, Symmetrie, Determinismus, Palette, Zeichnen.</summary>
[Collection(WpfCollection.Name)]
public class MoustacheTexturesTests
{
    private static byte[] Pixels(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    [StaFact]
    public void Get_ReturnsTheSameFrozenInstance_ForTheSameKey()
    {
        var a = MoustacheTextures.Get(MoustacheStyle.Brush, MoustacheColor.Blond, 200);
        var b = MoustacheTextures.Get(MoustacheStyle.Brush, MoustacheColor.Blond, 190); // dieselbe Stufe (256)
        var other = MoustacheTextures.Get(MoustacheStyle.Brush, MoustacheColor.Gray, 200);

        Assert.Same(a, b);
        Assert.NotSame(a, other);
        Assert.True(a.Bitmap.IsFrozen);
        Assert.Equal(256, a.PixelsPerUnit);
        Assert.True(MoustacheTextures.IsCached(MoustacheStyle.Brush, MoustacheColor.Blond, 256));
    }

    [Fact]
    public void TierFor_PicksTheSmallestTierThatIsWideEnough()
    {
        Assert.Equal(256, MoustacheTextures.TierFor(10));
        Assert.Equal(256, MoustacheTextures.TierFor(256));
        Assert.Equal(512, MoustacheTextures.TierFor(257));
        Assert.Equal(1024, MoustacheTextures.TierFor(700));
        Assert.Equal(2048, MoustacheTextures.TierFor(1500));
        Assert.Equal(2048, MoustacheTextures.TierFor(9000)); // größer als die größte Stufe: die größte
    }

    [StaFact]
    public void RandomStyle_UsesTheTwirlTexture()
    {
        Assert.Same(
            MoustacheTextures.Get(MoustacheStyle.Twirl, MoustacheColor.Black, 100),
            MoustacheTextures.Get(MoustacheStyle.Random, MoustacheColor.Black, 100));
    }

    [StaTheory]
    [InlineData(MoustacheStyle.Twirl)]
    [InlineData(MoustacheStyle.FuManchu)]
    [InlineData(MoustacheStyle.Horseshoe)]
    [InlineData(MoustacheStyle.Handlebar)]
    public void Create_HasTheExpectedSize_AndTheOriginInsideTheImage(MoustacheStyle style)
    {
        var texture = MoustacheTextures.Create(style, MoustacheColor.DarkBrown, 256);
        var bounds = MoustacheShapes.Get(style).Bounds;

        // 96 dpi, Pixel = Einheit × Auflösung: Bild und Einheitsmaß hängen exakt zusammen.
        Assert.Equal(96, texture.Bitmap.DpiX, 3);
        Assert.Equal(texture.UnitRect.Width * 256, texture.Bitmap.PixelWidth, 3);
        Assert.Equal(texture.UnitRect.Height * 256, texture.Bitmap.PixelHeight, 3);
        Assert.Equal(PixelFormats.Pbgra32, texture.Bitmap.Format);
        // symmetrisch um den Ursprung, Form samt Rand enthalten, Ursprung (0, 0) liegt im Bild
        Assert.Equal(0, texture.UnitRect.X + texture.UnitRect.Width / 2, 6);
        Assert.True(texture.UnitRect.Contains(new Point(0, 0)));
        Assert.True(texture.UnitRect.Left < bounds.Left && texture.UnitRect.Right > bounds.Right);
        Assert.True(texture.UnitRect.Top < bounds.Top && texture.UnitRect.Bottom > bounds.Bottom);
        // Breite der Form 1 + Rand: nicht stark über 1,2
        Assert.InRange(texture.UnitRect.Width, 1.05, 1.2);
    }

    [StaFact]
    public void Create_IsDeterministic_SamePixelsInTwoRuns()
    {
        var first = MoustacheTextures.Create(MoustacheStyle.Walrus, MoustacheColor.Gray, 256);
        var second = MoustacheTextures.Create(MoustacheStyle.Walrus, MoustacheColor.Gray, 256);

        Assert.NotSame(first, second);
        Assert.Equal(Pixels(first.Bitmap), Pixels(second.Bitmap));
    }

    [StaFact]
    public void Create_IsMirrorSymmetric_BecauseDrawMirrorsByAngleOnly()
    {
        var texture = MoustacheTextures.Create(MoustacheStyle.Twirl, MoustacheColor.Ginger, 256);
        var pixels = Pixels(texture.Bitmap);
        var w = texture.Bitmap.PixelWidth;
        var h = texture.Bitmap.PixelHeight;

        // Verglichen wird die Deckkraft: Die gespiegelten Haare liegen in umgekehrter Reihenfolge übereinander (die Farbe einzelner
        // Kreuzungspunkte kann abweichen), die Lage der Haare und damit die Deckkraft ist spiegelgleich bis auf Rasterrundung.
        double totalDifference = 0;
        var maxDifference = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w / 2; x++)
            {
                var difference = Math.Abs(pixels[(y * w + x) * 4 + 3] - pixels[(y * w + (w - 1 - x)) * 4 + 3]);
                totalDifference += difference;
                maxDifference = Math.Max(maxDifference, difference);
            }
        }
        var mean = totalDifference / (h * (w / 2));
        Assert.True(mean < 3.0, $"mittlere Abweichung der Deckkraft {mean:F2}");
        Assert.True(maxDifference <= 80, $"größte Abweichung der Deckkraft {maxDifference}");
    }

    [StaFact]
    public void Create_CoversTheLipWithHair_AndPutsASoftShadowBelow_ButNotAbove()
    {
        var texture = MoustacheTextures.Create(MoustacheStyle.Chevron, MoustacheColor.DarkBrown, 512);
        var pixels = Pixels(texture.Bitmap);
        var w = texture.Bitmap.PixelWidth;

        byte AlphaAt(double ux, double uy)
        {
            var px = (int)Math.Round((ux - texture.UnitRect.X) * 512);
            var py = (int)Math.Round((uy - texture.UnitRect.Y) * 512);
            return pixels[(py * w + px) * 4 + 3];
        }

        Assert.True(AlphaAt(0.2, 0.0) > 240, "Haar deckt die Mitte der Oberlippe");
        // Chevron: Oberkante bei −0,09, Unterkante bei +0,12; darunter Schatten (halbtransparent), darüber nichts
        Assert.InRange(AlphaAt(0.2, 0.17), 20, 200);
        Assert.True(AlphaAt(0.2, -0.14) < 10);
        // Weicher Schatten: keine Stufe, sondern ein Verlauf
        Assert.True(AlphaAt(0.2, 0.17) > AlphaAt(0.2, 0.22));
    }

    [StaFact]
    public void Create_ClearlyTintsTheColors_DarkToLight()
    {
        double Luma(MoustacheColor color)
        {
            var texture = MoustacheTextures.Create(MoustacheStyle.Chevron, color, 256);
            var pixels = Pixels(texture.Bitmap);
            double sum = 0;
            var count = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] < 250)
                    continue;
                sum += 0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2];
                count++;
            }
            Assert.True(count > 500);
            return sum / count;
        }

        var black = Luma(MoustacheColor.Black);
        var dark = Luma(MoustacheColor.DarkBrown);
        var brown = Luma(MoustacheColor.Brown);
        var blond = Luma(MoustacheColor.Blond);
        var gray = Luma(MoustacheColor.Gray);
        Assert.True(black < dark && dark < brown && brown < blond, $"{black:F0} {dark:F0} {brown:F0} {blond:F0}");
        Assert.True(gray > dark); // Salz und Pfeffer: im Mittel heller als Dunkelbraun
    }

    [Fact]
    public void Palette_HasFourRealisticStops_PerColor_FromRootToTip()
    {
        foreach (var color in MoustacheStyles.AllColors)
        {
            var stops = MoustachePalette.Stops(color);
            Assert.Equal(4, stops.Count);
            double Luma(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            for (var i = 1; i < stops.Count; i++)
                Assert.True(Luma(stops[i]) > Luma(stops[i - 1]), $"{color}: Stufe {i} nicht heller");
        }
        Assert.Equal(Color.FromRgb(0x16, 0x0D, 0x08), MoustachePalette.Stops(MoustacheColor.DarkBrown)[0]);
    }

    // ---- Zeichnen

    [StaFact]
    public void Draw_PlacesTheTextureOriginAtTheCenter_AndScalesByTheShapeWidth()
    {
        MoustacheTextures.Get(MoustacheStyle.Chevron, MoustacheColor.Black, 400); // Draw wartet nie auf Texturen
        // Weißer Grund, Chevron-Breite 400 px bei (500, 300): Haar um die Oberlippe, nichts weit ab, Breite ≈ 400 (nicht Textur mit Rand).
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1000, 600));
            MoustacheRenderer.Draw(dc, MoustacheStyle.Chevron, MoustacheColor.Black, new Point(500, 300), 400, 0, mirror: false);
        }
        var bitmap = new RenderTargetBitmap(1000, 600, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var px = new byte[1000 * 600 * 4];
        bitmap.CopyPixels(px, 1000 * 4, 0);

        bool Dark(int x, int y) => px[(y * 1000 + x) * 4 + 1] < 90;
        Assert.True(Dark(500 + 60, 300), "Haar am Ursprung");
        Assert.True(Dark(500 + 150, 300), "Haar bei 0,375 Breiten");
        Assert.False(Dark(500 + 230, 300 - 200), "weit ab ist nichts");
        Assert.False(Dark(500 + 215, 300), "jenseits der halben Formbreite (200 px) plus Fransen ist kaum noch Haar");
        // Gespiegelt (Winkel 0): identisch
        Assert.True(Dark(500 - 150, 300));
    }

    [StaFact]
    public void Draw_IsFast_ButNotCheckedWithATightLimit()
    {
        var texture = MoustacheTextures.Get(MoustacheStyle.Walrus, MoustacheColor.Brown, 300); // erzeugen, nicht messen
        Assert.NotNull(texture);
        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();
        // aufwärmen
        MoustacheRenderer.Draw(dc, MoustacheStyle.Walrus, MoustacheColor.Brown, new Point(100, 100), 300, 0.1, mirror: true, 1, 0.01, 0.01);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
            MoustacheRenderer.Draw(dc, MoustacheStyle.Walrus, MoustacheColor.Brown, new Point(100 + i, 100), 300, 0.1, mirror: true, 1, 0.01, 0.01);
        var perCall = sw.Elapsed.TotalMilliseconds / 200;

        Assert.True(perCall < 1.0, $"Draw dauert {perCall:F3} ms");
    }

    [StaFact]
    public void Draw_IgnoresInvalidScaleAndWobble()
    {
        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();
        MoustacheRenderer.Draw(dc, MoustacheStyle.Twirl, MoustacheColor.Black, new Point(10, 10), 100, 0, false, scale: 0);
        MoustacheRenderer.Draw(dc, MoustacheStyle.Twirl, MoustacheColor.Black, new Point(10, 10), 100, 0, false, scale: double.NaN);
        MoustacheRenderer.Draw(dc, MoustacheStyle.Twirl, MoustacheColor.Black, new Point(10, 10), 100, 0, false, 1, double.NaN, double.PositiveInfinity);
    }

    [StaFact]
    public void RenderOnto_UsesAHigherResolutionTexture_ForAWideMoustache_AndTheTrackColor()
    {
        MoustacheTextures.Get(MoustacheStyle.Brush, MoustacheColor.Ginger, 700); // RenderOnto erzeugt nie selbst
        var photo = BitmapSource.Create(1600, 900, 96, 96, PixelFormats.Pbgra32, null, Enumerable.Repeat((byte)255, 1600 * 900 * 4).ToArray(), 1600 * 4);
        var pose = new MoustachePose(new NormPoint(0.5, 0.5), 700, 0);

        var result = MoustacheRenderer.RenderOnto(photo, [(pose, MoustacheStyle.Brush, MoustacheColor.Ginger)]);

        Assert.True(MoustacheTextures.IsCached(MoustacheStyle.Brush, MoustacheColor.Ginger, 1024));
        var pixel = new byte[4];
        result.CopyPixels(new Int32Rect(800 + 100, 450 + 6, 1, 1), pixel, 4, 0);
        Assert.True(pixel[2] > pixel[0] + 40, "rötliches Haar (R deutlich über B)");
    }


    [StaFact]
    public void Sketch_ShowsTheSameTextureAndFitsTallShapes()
    {
        foreach (var style in new[] { MoustacheStyle.FuManchu, MoustacheStyle.Horseshoe, MoustacheStyle.Handlebar })
            MoustacheTextures.Get(style, MoustacheColor.DarkBrown, 100); // Die Skizze wartet nie auf Texturen: erst fertig machen
        foreach (var style in MoustacheStyles.All)
        {
            foreach (var color in new[] { MoustacheColor.DarkBrown, MoustacheColor.Ginger, MoustacheColor.Gray, MoustacheColor.Black, MoustacheColor.Blond, MoustacheColor.Brown })
                MoustacheTextures.Get(style, color, 30);
        }

        foreach (var style in new[] { MoustacheStyle.Random, MoustacheStyle.FuManchu, MoustacheStyle.Horseshoe, MoustacheStyle.Handlebar })
        {
            var sketch = new Fotobox.Views.Admin.MoustacheSketch { StyleKind = style };
            sketch.Measure(new Size(200, 200));
            sketch.Arrange(new Rect(0, 0, 120, 68));
            sketch.UpdateLayout();

            var host = (System.Windows.Controls.Canvas)sketch.FindName("Host");
            var images = host.Children.OfType<System.Windows.Controls.Image>().ToList();
            Assert.Equal(style == MoustacheStyle.Random ? 7 : 1, images.Count);
            foreach (var image in images)
            {
                // ganz innerhalb der Skizze (nichts abgeschnitten, ausgenommen der durchsichtige Rand)
                var left = System.Windows.Controls.Canvas.GetLeft(image);
                var top = System.Windows.Controls.Canvas.GetTop(image);
                Assert.True(left > -6 && left + image.Width < 126, $"{style}: x {left}..{left + image.Width}");
                Assert.True(top > -6 && top + image.Height < 74, $"{style}: y {top}..{top + image.Height}");
            }
        }
    }

    // ---- Nie blockieren, Retry, LRU (mit ersetztem Erzeuger; die Texturen sind winzig)

    private static MoustacheTexture Tiny(int tier)
    {
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Pbgra32, null, new byte[16], 8);
        bitmap.Freeze();
        return new MoustacheTexture(bitmap, new Rect(-0.5, -0.1, 1, 0.2), tier);
    }

    private static void WithFactory(Func<MoustacheStyle, MoustacheColor, int, MoustacheTexture> factory, Action body)
    {
        using var isolation = MoustacheTextures.Isolate();
        MoustacheTextures.Factory = factory;
        body();
    }

    [StaFact]
    public void TryGetReady_ColdTexture_ReturnsImmediately_FallsBackToAnotherTier_AndRaisesTheEventWhenDone()
    {
        using var gate = new ManualResetEventSlim(false);
        var raised = 0;
        void OnReady() => Interlocked.Increment(ref raised);

        WithFactory((_, _, tier) =>
        {
            if (tier != 512)
                gate.Wait(TimeSpan.FromSeconds(20));
            return Tiny(tier);
        }, () =>
        {
            // eine größere Stufe (512) ist schon da
            MoustacheTextures.GetTier(MoustacheStyle.Walrus, MoustacheColor.Gray, 512);

            MoustacheTextures.TextureReady += OnReady;
            try
            {
                var sw = Stopwatch.StartNew();
                var found = MoustacheTextures.TryGetReady(MoustacheStyle.Walrus, MoustacheColor.Gray, 200, out var fallback);
                Assert.True(sw.ElapsedMilliseconds < 50, $"TryGetReady blockiert ({sw.ElapsedMilliseconds} ms)");
                Assert.True(found);
                Assert.Equal(512, fallback!.PixelsPerUnit); // die nächstgrößere fertige Stufe

                // ganz ohne fertige Stufe: sofort nichts
                sw.Restart();
                Assert.False(MoustacheTextures.TryGetReady(MoustacheStyle.Brush, MoustacheColor.Black, 200, out var none));
                Assert.Null(none);
                Assert.True(sw.ElapsedMilliseconds < 50);

                gate.Set();
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref raised) > 0, 10000), "Das Ereignis feuert nach der Fertigstellung");
                Assert.True(SpinWait.SpinUntil(() => MoustacheTextures.IsCached(MoustacheStyle.Walrus, MoustacheColor.Gray, 256), 5000));
                Assert.True(MoustacheTextures.TryGetReady(MoustacheStyle.Walrus, MoustacheColor.Gray, 200, out var exact));
                Assert.Equal(256, exact!.PixelsPerUnit);
            }
            finally
            {
                MoustacheTextures.TextureReady -= OnReady;
                gate.Set();
            }
        });
    }

    [StaFact]
    public async Task EnsureAsync_WaitsAtMostTheBudget_ThenSucceedsOnceTheTextureIsDone()
    {
        using var gate = new ManualResetEventSlim(false);
        using var isolation = MoustacheTextures.Isolate();
        MoustacheTextures.Factory = (_, _, tier) =>
        {
            gate.Wait(TimeSpan.FromSeconds(20));
            return Tiny(tier);
        };
        try
        {
            var sw = Stopwatch.StartNew();
            var ok = await MoustacheTextures.EnsureAsync(MoustacheStyle.Chevron, MoustacheColor.Blond, 900, TimeSpan.FromMilliseconds(150));
            Assert.False(ok);
            Assert.True(sw.ElapsedMilliseconds < 1500, $"EnsureAsync überschreitet das Budget ({sw.ElapsedMilliseconds} ms)");

            gate.Set();
            Assert.True(await MoustacheTextures.EnsureAsync(MoustacheStyle.Chevron, MoustacheColor.Blond, 900, TimeSpan.FromSeconds(10)));
        }
        finally
        {
            gate.Set();
        }
    }

    [StaFact]
    public void FailedCreation_IsLoggedAndRetried_ButOnlyUpToMaxAttempts()
    {
        var calls = 0;
        WithFactory((_, _, tier) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("Test-Fehler");
        }, () =>
        {
            for (var i = 0; i < MoustacheTextures.MaxAttempts; i++)
                Assert.Throws<InvalidOperationException>(() => MoustacheTextures.GetTier(MoustacheStyle.Brush, MoustacheColor.Brown, 256));
            Assert.Equal(MoustacheTextures.MaxAttempts, calls);

            // aufgegeben: kein weiterer Versuch, keine Endlosschleife, nichts blockiert
            Assert.Throws<InvalidOperationException>(() => MoustacheTextures.GetTier(MoustacheStyle.Brush, MoustacheColor.Brown, 256));
            Assert.False(MoustacheTextures.TryGetReady(MoustacheStyle.Brush, MoustacheColor.Brown, 100, out _));
            Assert.Equal(MoustacheTextures.MaxAttempts, calls);
        });
    }

    [StaFact]
    public void FailedCreation_DoesNotPoisonTheEntryForever()
    {
        var calls = 0;
        WithFactory((_, _, tier) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("einmaliger Fehler");
            return Tiny(tier);
        }, () =>
        {
            Assert.Throws<InvalidOperationException>(() => MoustacheTextures.GetTier(MoustacheStyle.Twirl, MoustacheColor.Black, 256));
            var texture = MoustacheTextures.GetTier(MoustacheStyle.Twirl, MoustacheColor.Black, 256); // zweiter Versuch gelingt
            Assert.Equal(256, texture.PixelsPerUnit);
            Assert.Equal(2, calls);
        });
    }

    [StaFact]
    public void Cache_KeepsAtMostEightLargeTextures_EvictingTheLeastRecentlyUsed_AndKeepsSmallTiers()
    {
        WithFactory((_, _, tier) => Tiny(tier), () =>
        {
            foreach (var color in MoustacheStyles.AllColors.Take(3))
                MoustacheTextures.GetTier(MoustacheStyle.Twirl, color, 256);

            var keys = MoustacheStyles.All.SelectMany(s => MoustacheStyles.AllColors.Take(2).Select(c => (s, c))).Take(10).ToList();
            var first = keys[0];
            foreach (var (style, color) in keys)
            {
                MoustacheTextures.GetTier(style, color, 1024);
                MoustacheTextures.GetTier(first.s, first.c, 1024); // die erste bleibt „kürzlich benutzt“
            }

            Assert.Equal(MoustacheTextures.MaxLargeEntries, MoustacheTextures.CachedCount(largeOnly: true));
            Assert.True(MoustacheTextures.IsCached(first.s, first.c, 1024), "kürzlich benutzte bleibt");
            Assert.True(MoustacheTextures.IsCached(keys[^1].s, keys[^1].c, 1024));
            Assert.False(MoustacheTextures.IsCached(keys[1].s, keys[1].c, 1024), "die am längsten unbenutzte fliegt raus");
            Assert.Equal(3, MoustacheTextures.CachedCount() - MoustacheTextures.CachedCount(largeOnly: true)); // 256er bleiben
        });
    }

    [StaFact]
    public void Draw_WithAColdTexture_DoesNotBlock_AndDrawsNothing()
    {
        using var gate = new ManualResetEventSlim(false);
        WithFactory((_, _, tier) =>
        {
            gate.Wait(TimeSpan.FromSeconds(20));
            return Tiny(tier);
        }, () =>
        {
            try
            {
                var visual = new DrawingVisual();
                using var dc = visual.RenderOpen();
                var sw = Stopwatch.StartNew();
                var drawn = MoustacheRenderer.Draw(dc, MoustacheStyle.Handlebar, MoustacheColor.Ginger, new Point(50, 50), 300, 0, false);
                Assert.False(drawn);
                Assert.True(sw.ElapsedMilliseconds < 50, $"Draw blockiert ({sw.ElapsedMilliseconds} ms)");
            }
            finally
            {
                gate.Set();
            }
        });
    }

    [StaFact]
    public void Overlay_PopWaitsForTheFirstVisibleFrame_AndSketchShowsAPlaceholderUntilTheTextureIsReady()
    {
        using var gate = new ManualResetEventSlim(false);
        WithFactory((_, _, tier) =>
        {
            gate.Wait(TimeSpan.FromSeconds(20));
            return Tiny(tier);
        }, () =>
        {
            try
            {
                long now = 0;
                var overlay = new PeaceOverlay { ImageWidth = 1920, ImageHeight = 1080, Clock = () => now };
                overlay.Moustaches = [new MoustacheView(1, MoustacheStyle.Twirl, new MoustachePose(new NormPoint(0.5, 0.5), 200, 0), MoustacheColor.Black)];
                overlay.Measure(new Size(1920, 1080));
                overlay.Arrange(new Rect(0, 0, 1920, 1080));
                overlay.UpdateLayout();
                now = Stopwatch.Frequency;
                Assert.Equal(0, overlay.PopScaleOf(1)); // keine Textur: das Plopp wartet, der Bart ist unsichtbar

                var sketch = new Fotobox.Views.Admin.MoustacheSketch { StyleKind = MoustacheStyle.Brush, HairColor = MoustacheColor.Blond };
                var host = (System.Windows.Controls.Canvas)sketch.FindName("Host");
                Assert.Single(host.Children.OfType<System.Windows.Shapes.Path>()); // Platzhalter statt Bild
                Assert.Empty(host.Children.OfType<System.Windows.Controls.Image>());

                gate.Set();
                Assert.True(SpinWait.SpinUntil(() => MoustacheTextures.HasAnyReady(MoustacheStyle.Twirl, MoustacheColor.Black), 5000));
                // erstes Bild mit Textur: Das Plopp beginnt jetzt (bei „jetzt“, nicht rückwirkend)
                overlay.InvalidateVisual(); // so wie es OnTextureReady im Livebetrieb auslöst
                overlay.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(overlay);
                Assert.Equal(0, overlay.PopScaleOf(1), 3);
                now += (long)(0.12 * Stopwatch.Frequency);
                Assert.True(overlay.PopScaleOf(1) > 0.3);
            }
            finally
            {
                gate.Set();
            }
        });
    }
}
