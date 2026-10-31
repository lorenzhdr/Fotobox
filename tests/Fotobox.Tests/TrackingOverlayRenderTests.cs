using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Heart;
using Fotobox.Core.Peace;
using Fotobox.Core.Tracking;
using Fotobox.Views.Booth;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Zeichnung des Tracking-Overlays: Spiegelung und Lage über den <see cref="OverlayMapper"/>, Robustheit gegen ungültige Werte, beide
/// Stile, Beschriftungen – plus die Sichtprüfung per Bilddatei (nur mit <c>FOTOBOX_RENDER_DIR</c>).
/// </summary>
[Collection(WpfCollection.Name)]
public class TrackingOverlayRenderTests
{
    private const int W = 800, H = 450;

    private static readonly NormRect LeftBox = new(0.10, 0.25, 0.20, 0.50);

    private static TrackingOverlayFrame PersonFrame(NormRect box, TrackingOverlaySettings? options = null) => new()
    {
        Options = options ?? new TrackingOverlaySettings(),
        PersonImageWidth = 1280,
        PersonImageHeight = 720,
        Persons = [new TrackingPersonItem(box, 0.9, DetectionKind.Body)],
    };

    private static byte[] Render(TrackingOverlayFrame? frame, bool mirror = false, int width = W, int height = H)
    {
        var element = new TrackingOverlay { Frame = frame, Mirror = mirror, Width = width, Height = height };
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    // Anzahl nicht durchsichtiger Pixel in den Spalten [from, to).
    private static int Painted(byte[] pixels, int from, int to, int width = W, int height = H)
    {
        var count = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = from; x < to; x++)
            {
                if (pixels[(y * width + x) * 4 + 3] != 0)
                    count++;
            }
        }
        return count;
    }

    private static TrackingOverlaySettings Options(Action<TrackingOverlaySettings>? configure = null)
    {
        var o = new TrackingOverlaySettings();
        configure?.Invoke(o);
        return o;
    }

    [StaFact]
    public void EmptyOrMissingFrame_DrawsNothing()
    {
        WpfCollection.EnsurePackUris();

        Assert.Equal(0, Painted(Render(null), 0, W));
        Assert.Equal(0, Painted(Render(TrackingOverlayFrame.Empty), 0, W));
    }

    [StaFact]
    public void Box_ComesOutOnTheLeft_AndMirrorsToTheRight()
    {
        WpfCollection.EnsurePackUris();
        var frame = PersonFrame(LeftBox);

        var plain = Render(frame, mirror: false);
        var mirrored = Render(frame, mirror: true);

        Assert.True(Painted(plain, 0, W / 2) > 200);
        Assert.Equal(0, Painted(plain, W / 2 + 1, W));
        Assert.True(Painted(mirrored, W / 2, W) > 200);
        Assert.Equal(0, Painted(mirrored, 0, W / 2 - 1));
    }

    [StaFact]
    public void Hand_AndFacePoints_FollowTheMirror()
    {
        WpfCollection.EnsurePackUris();
        var hand = PeaceHandFixtures.Hand(HandPose.Peace, cx: 0.2);
        var face = PeaceHandFixtures.Face(0.2, 0.3, 0.1);
        var frame = new TrackingOverlayFrame
        {
            GestureImageWidth = 1280,
            GestureImageHeight = 720,
            Options = Options(o => o.ShowLabels = false),
            Faces = [new TrackingFaceItem(1, face)],
            Hands = [new TrackingHandItem(1, hand.Points, hand.Box, hand.Score, null, false, 0, [])],
        };

        var plain = Render(frame, mirror: false);
        var mirrored = Render(frame, mirror: true);

        Assert.True(Painted(plain, 0, W / 2) > 300);
        Assert.Equal(0, Painted(plain, W / 2 + 40, W));
        Assert.True(Painted(mirrored, W / 2, W) > 300);
        Assert.Equal(0, Painted(mirrored, 0, W / 2 - 40));
    }

    [StaFact]
    public void Moustache_AngleIsNegatedWhenMirrored()
    {
        WpfCollection.EnsurePackUris();
        // Waagerechter Bart bei y = 0,5, 30° geneigt: gespiegelt steigt die Linie in die andere Richtung.
        var pose = new MoustachePose(new NormPoint(0.5, 0.5), 400, Math.PI / 6);
        var frame = new TrackingOverlayFrame
        {
            GestureImageWidth = 1280,
            GestureImageHeight = 720,
            Options = Options(o => o.ShowLabels = false),
            Moustaches = [new TrackingMoustacheItem(1, pose)],
        };

        var plain = Render(frame, mirror: false);
        var mirrored = Render(frame, mirror: true);

        // Links der Mitte liegt die Linie ohne Spiegelung oberhalb (y kleiner), mit Spiegelung unterhalb der Mitte.
        double MeanY(byte[] px, int from, int to)
        {
            double sum = 0, n = 0;
            for (var y = 0; y < H; y++)
            for (var x = from; x < to; x++)
                if (px[(y * W + x) * 4 + 3] > 128) { sum += y; n++; }
            return n == 0 ? double.NaN : sum / n;
        }
        var leftPlain = MeanY(plain, 0, W / 2);
        var leftMirrored = MeanY(mirrored, 0, W / 2);
        Assert.False(double.IsNaN(leftPlain) || double.IsNaN(leftMirrored));
        Assert.True(leftPlain < H / 2.0);
        Assert.True(leftMirrored > H / 2.0);
    }

    [StaFact]
    public void InvalidAndAbsurdValues_AreSkippedWithoutThrowing()
    {
        WpfCollection.EnsurePackUris();
        var nan = new NormRect(double.NaN, 0, 0.2, 0.2);
        var huge = new NormRect(1e12, 1e12, 5e12, 5e12);
        var points = Enumerable.Range(0, 21).Select(i => new HandPoint(i % 2 == 0 ? double.NaN : 1e15, double.PositiveInfinity, 0)).ToArray();
        var frame = new TrackingOverlayFrame
        {
            Options = Options(o => o.ShowVerdicts = true),
            PersonImageWidth = double.NaN,
            GestureImageWidth = -5,
            Persons = [new TrackingPersonItem(nan, double.NaN, DetectionKind.Body), new TrackingPersonItem(huge, 1, DetectionKind.Face)],
            Targets = [new TrackingTargetItem(1, nan, "TARGET 1", true)],
            Faces = [new TrackingFaceItem(1, new FaceLandmarks(nan, double.NaN, new NormPoint(double.NaN, 0), new NormPoint(0, double.NaN),
                new NormPoint(1e15, 1e15), new NormPoint(-1e15, 0), new NormPoint(0, 0)))],
            Hands = [new TrackingHandItem(1, points, huge, 0.5, null, true, double.NaN, ["Test"])],
            Moustaches = [new TrackingMoustacheItem(1, new MoustachePose(new NormPoint(double.NaN, 0.5), double.PositiveInfinity, double.NaN))],
            Hearts = [new TrackingHeartItem(new NormPoint(0.5, 0.5), double.NaN, double.NaN, HeartShape.Classic)],
            Pairs = [new TrackingPairItem(new NormPoint(double.NaN, 0), ["a"])],
            MiddleFingerNow = true,
        };

        var pixels = Render(frame);   // darf nicht werfen

        Assert.NotNull(pixels);
    }

    [StaFact]
    public void HandsOutsideTheImage_AreClippedNotRejected()
    {
        WpfCollection.EnsurePackUris();
        var hand = PeaceHandFixtures.Hand(HandPose.Peace, cx: 1.05);
        var frame = new TrackingOverlayFrame
        {
            GestureImageWidth = 1280,
            GestureImageHeight = 720,
            Options = Options(o => o.ShowLabels = false),
            Hands = [new TrackingHandItem(1, hand.Points, hand.Box, hand.Score, null, false, 0, [])],
        };

        Assert.True(Painted(Render(frame), W / 2, W) > 50);
    }

    [StaFact]
    public void BothStyles_DrawTheSameData_ButDifferently()
    {
        WpfCollection.EnsurePackUris();
        var technik = Render(PersonFrame(LeftBox, Options(o => o.Style = TrackingOverlayStyle.Technik)));
        var scifi = Render(PersonFrame(LeftBox, Options(o => o.Style = TrackingOverlayStyle.SciFi)));

        Assert.True(Painted(technik, 0, W) > 200);
        Assert.True(Painted(scifi, 0, W) > 200);
        Assert.False(technik.AsSpan().SequenceEqual(scifi));
    }

    [StaFact]
    public void Labels_CanBeSwitchedOff()
    {
        WpfCollection.EnsurePackUris();
        var with = Painted(Render(PersonFrame(LeftBox, Options(o => o.ShowLabels = true))), 0, W);
        var without = Painted(Render(PersonFrame(LeftBox, Options(o => o.ShowLabels = false))), 0, W);

        Assert.True(with > without);
        Assert.True(without > 0);
    }

    [StaFact]
    public void Scale_MakesTheLinesThicker()
    {
        WpfCollection.EnsurePackUris();
        var small = Painted(Render(PersonFrame(LeftBox, Options(o => { o.ShowLabels = false; o.ScalePercent = 50; }))), 0, W);
        var large = Painted(Render(PersonFrame(LeftBox, Options(o => { o.ShowLabels = false; o.ScalePercent = 200; }))), 0, W);

        Assert.True(large > small);
    }

    // ---- Sichtprüfung

    /// <summary>
    /// Sichtprüfung per Bilddatei, kein Test im engeren Sinn: Läuft nur mit <c>FOTOBOX_RENDER_DIR</c> (sonst sofort beendet). Rendert das
    /// Overlay in beiden Stilen (Technik, SciFi) mit Personen, Ziel, Gesicht, zwei Händen samt Warum-Info, Bart, Herz und Statuszeile auf
    /// einem synthetischen Hintergrund, je ungespiegelt und gespiegelt.
    /// </summary>
    [StaFact]
    [Trait("Category", "Visual")]
    public void Render_TrackingOverlay_BothStyles()
    {
        var dir = Environment.GetEnvironmentVariable("FOTOBOX_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            return;
        Directory.CreateDirectory(dir);
        WpfCollection.EnsurePackUris();

        var peace = PeaceHandFixtures.Hand(HandPose.Peace, cx: 0.30, cy: 0.62);
        var fist = PeaceHandFixtures.Hand(HandPose.Fist, cx: 0.72, cy: 0.66);
        TrackingOverlayFrame Frame(TrackingOverlayStyle style, bool verdicts) => new()
        {
            Options = Options(o => { o.Style = style; o.ShowVerdicts = verdicts; }),
            PersonImageWidth = 1280,
            PersonImageHeight = 720,
            GestureImageWidth = 1280,
            GestureImageHeight = 720,
            Persons = [new TrackingPersonItem(new NormRect(0.08, 0.10, 0.30, 0.86), 0.91, DetectionKind.Body),
                       new TrackingPersonItem(new NormRect(0.55, 0.14, 0.28, 0.82), 0.78, DetectionKind.Body)],
            Targets = [new TrackingTargetItem(1, new NormRect(0.08, 0.10, 0.30, 0.86), "TARGET 1", true)],
            Faces = [new TrackingFaceItem(1, PeaceHandFixtures.Face(0.23, 0.26, 0.12)), new TrackingFaceItem(2, PeaceHandFixtures.Face(0.69, 0.30, 0.10))],
            Hands =
            [
                new TrackingHandItem(1, peace.Points, peace.Box, peace.Score, 0.1, true, 0.6,
                    verdicts ? ["Peace ja (0,95)", "Mittelfinger nein: Mittelfinger nicht gestreckt", "Fingerherz nein: Daumen und Zeigefinger zu weit auseinander"] : []),
                new TrackingHandItem(2, fist.Points, fist.Box, fist.Score, 0.9, false, 0,
                    verdicts ? ["Peace nein: Zeigefinger nicht gestreckt", "Mittelfinger nein: Mittelfinger nicht gestreckt", "Fingerherz nein: abgelehnt"] : []),
            ],
            Moustaches = [new TrackingMoustacheItem(1, new MoustachePose(new NormPoint(0.23, 0.33), 120, 0.12))],
            Hearts = [new TrackingHeartItem(new NormPoint(0.5, 0.45), 0.22, 0.55, HeartShape.Classic)],
            Pairs = verdicts ? [new TrackingPairItem(new NormPoint(0.5, 0.62), ["Herz nein: Hände zu weit auseinander", "Nicht bestanden: Abstand, Winkel"])] : [],
            PeaceNow = true,
            MiddleFingerNow = true,
            Status = [new TrackingStatusEntry("Personen", true, 4.8, "Ganzkörper"), new TrackingStatusEntry("Gesichter", true, 9.6, null),
                      new TrackingStatusEntry("Hände", true, 9.6, null)],
        };

        void Save(string name, TrackingOverlayFrame frame, bool mirror)
        {
            const int width = 1280, height = 720;
            var root = new Grid
            {
                Width = width, Height = height,
                Background = new LinearGradientBrush(Color.FromRgb(0x5B, 0x8D, 0xC9), Color.FromRgb(0x2E, 0x3A, 0x4E), 90),
            };
            root.Children.Add(new TrackingOverlay { Frame = frame, Mirror = mirror });
            root.Children.Add(new TrackingStatusLine { DataContext = new StatusSource(frame.StatusText) });
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(dir, name));
            encoder.Save(stream);
        }

        Save("tracking-technik.png", Frame(TrackingOverlayStyle.Technik, verdicts: false), mirror: false);
        Save("tracking-technik-warum-gespiegelt.png", Frame(TrackingOverlayStyle.Technik, verdicts: true), mirror: true);
        Save("tracking-scifi.png", Frame(TrackingOverlayStyle.SciFi, verdicts: false), mirror: false);
        Save("tracking-scifi-warum-gespiegelt.png", Frame(TrackingOverlayStyle.SciFi, verdicts: true), mirror: true);
    }

    private sealed class StatusSource(string text)
    {
        public string StatusText { get; } = text;
    }
}
