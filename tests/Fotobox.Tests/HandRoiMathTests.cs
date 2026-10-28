using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Fotobox.Services.Detection.Peace;
using Xunit;

namespace Fotobox.Tests;

public class HandRoiMathTests
{
    private const int W = 1000, H = 500;

    private static PalmCandidate Palm(double cx, double cy, double sizePx, (double X, double Y) kp0, (double X, double Y) kp2) =>
        new(0.9, new NormPoint(cx / W, cy / H), sizePx / W, new NormPoint(kp0.X / W, kp0.Y / H), new NormPoint(kp2.X / W, kp2.Y / H));

    [Fact]
    public void FromPalm_UprightHand_HasNoRotation_AndShiftsCenterUpwards()
    {
        // Handgelenk unten, Mittelfingerwurzel direkt darüber (y kleiner).
        var palm = Palm(500, 250, 100, (500, 300), (500, 220));

        var roi = HandRoiMath.FromPalm(palm, W, H);

        Assert.Equal(0, roi.AngleRad, 6);
        Assert.Equal(290, roi.SizePx, 6);                     // 2,9 · 100 px
        Assert.Equal(500.0 / W, roi.Center.X, 6);
        Assert.Equal((250 - 0.5 * 100) / H, roi.Center.Y, 6); // um 0,5 · Palm-Größe nach oben
    }

    [Fact]
    public void FromPalm_HandPointingRight_RotatesClockwiseBy90Degrees_AndShiftsRight()
    {
        var palm = Palm(500, 250, 100, (440, 250), (520, 250));

        var roi = HandRoiMath.FromPalm(palm, W, H);

        Assert.Equal(Math.PI / 2, roi.AngleRad, 6);
        Assert.Equal((500 + 50.0) / W, roi.Center.X, 6);
        Assert.Equal(250.0 / H, roi.Center.Y, 6);
    }

    [Fact]
    public void FromPalm_HandPointingDown_IsPlusMinusPi()
    {
        var palm = Palm(500, 250, 100, (500, 200), (500, 280));

        var roi = HandRoiMath.FromPalm(palm, W, H);

        Assert.Equal(Math.PI, Math.Abs(roi.AngleRad), 6);
        Assert.Equal((250 + 50.0) / H, roi.Center.Y, 6);
    }

    [Fact]
    public void FromPalm_IsComputedInPixels_NotNormalizedUnits()
    {
        // Gleiche Pixelgeometrie, unterschiedliche Bildseiten: 45° nach rechts oben bleibt 45°.
        var palm = Palm(500, 250, 100, (460, 290), (540, 210));

        var roi = HandRoiMath.FromPalm(palm, W, H);

        Assert.Equal(Math.PI / 4, roi.AngleRad, 6);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.7)]
    [InlineData(-1.9)]
    [InlineData(3.0)]
    public void FromLandmarks_RecoversRotation_AndSizeFromSpread(double angle)
    {
        // Synthetische Hand in einem gedrehten Rahmen: Handgelenk (0,0), Mittelfingerwurzel bei (0, −100), Finger bis (±60, −220).
        var local = new (double U, double V)[21];
        local[0] = (0, 0);
        for (var i = 1; i < 21; i++)
            local[i] = ((i % 5 - 2) * 30, -100 - i * 6);
        local[9] = (0, -100);
        var center = (X: 400.0, Y: 250.0);
        var points = local.Select(p =>
        {
            var x = center.X + p.U * Math.Cos(angle) - p.V * Math.Sin(angle);
            var y = center.Y + p.U * Math.Sin(angle) + p.V * Math.Cos(angle);
            return new HandPoint(x / W, y / H, 0);
        }).ToArray();
        var hand = new HandLandmarks(points, 0.9, NormRect.Full);

        var roi = HandRoiMath.FromLandmarks(hand, W, H);

        Assert.Equal(HandRoiMath.NormalizeRadians(angle), roi.AngleRad, 6);
        var extentU = local.Max(p => p.U) - local.Min(p => p.U);
        var extentV = local.Max(p => p.V) - local.Min(p => p.V);
        Assert.Equal(2.0 * Math.Max(extentU, extentV), roi.SizePx, 6);
    }

    [Fact]
    public void FromLandmarks_CenterCoversAllPoints()
    {
        var points = Enumerable.Range(0, 21).Select(i => new HandPoint((0.4 + 0.004 * i), 0.7 - 0.01 * i, 0)).ToArray();
        var hand = new HandLandmarks(points, 0.9, NormRect.Full);

        var roi = HandRoiMath.FromLandmarks(hand, W, H);

        // Jeder Punkt liegt im (gedrehten) Quadrat der Kante SizePx um die Mitte.
        var cos = Math.Cos(roi.AngleRad);
        var sin = Math.Sin(roi.AngleRad);
        foreach (var p in points)
        {
            var dx = (p.X - roi.Center.X) * W;
            var dy = (p.Y - roi.Center.Y) * H;
            var u = dx * cos + dy * sin;
            var v = -dx * sin + dy * cos;
            Assert.InRange(Math.Abs(u), 0, roi.SizePx / 2);
            Assert.InRange(Math.Abs(v), 0, roi.SizePx / 2);
        }
    }

    [Fact]
    public void FromLandmarks_RejectsIncompleteHands()
    {
        var hand = new HandLandmarks([new HandPoint(0, 0, 0)], 0.9, NormRect.Full);

        Assert.Throws<ArgumentException>(() => HandRoiMath.FromLandmarks(hand, W, H));
    }

    [Fact]
    public void NormalizeRadians_WrapsIntoMinusPiToPi()
    {
        Assert.Equal(0, HandRoiMath.NormalizeRadians(2 * Math.PI), 9);
        Assert.Equal(-Math.PI / 2, HandRoiMath.NormalizeRadians(1.5 * Math.PI), 9);
        Assert.Equal(Math.PI / 2, HandRoiMath.NormalizeRadians(-1.5 * Math.PI), 9);
    }
}

public class AffineCropTests
{
    /// <summary>Bild mit Verlauf: R = x, G = y, B = 7 (x, y < 256).</summary>
    private static BgraImage Gradient(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                px[o] = 7;
                px[o + 1] = (byte)y;
                px[o + 2] = (byte)x;
                px[o + 3] = 255;
            }
        }
        return new BgraImage { Width = w, Height = h, Stride = w * 4, Pixels = px };
    }

    [Fact]
    public void Sample_IdentityWindow_CopiesPixelsExactly()
    {
        var img = Gradient(16, 8);
        var dest = new float[3 * 16 * 8];

        AffineCrop.Sample(img, 8, 4, 0, 16, 8, 16, 8, dest, 16, 8, bgr: false, scale: 1f);

        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                Assert.Equal(x, dest[y * 16 + x], 3);               // R
                Assert.Equal(y, dest[16 * 8 + y * 16 + x], 3);      // G
                Assert.Equal(7, dest[2 * 16 * 8 + y * 16 + x], 3);  // B
            }
        }
    }

    [Fact]
    public void Sample_BgrOrderAndScale()
    {
        var img = Gradient(4, 4);
        var dest = new float[3 * 16];

        AffineCrop.Sample(img, 2, 2, 0, 4, 4, 4, 4, dest, 4, 4, bgr: true, scale: 1f / 255f);

        Assert.Equal(7f / 255f, dest[0], 5);          // Kanal 0 = Blau
        Assert.Equal(3f / 255f, dest[2 * 16 + 3], 5); // Kanal 2 = Rot an x = 3
    }

    [Fact]
    public void Sample_OutsideImage_IsBlack()
    {
        var img = Gradient(8, 8);
        var dest = new float[3 * 8 * 8];

        // Fenster 8 × 8 um (0, 0): nur der untere rechte Viertel liegt im Bild.
        AffineCrop.Sample(img, 0, 0, 0, 8, 8, 8, 8, dest, 8, 8, bgr: false, scale: 1f);

        Assert.Equal(0f, dest[0]);                     // oben links außerhalb
        Assert.Equal(7f, dest[2 * 64 + 7 * 8 + 7], 3); // unten rechts = Pixel (3, 3)? B konstant 7
        Assert.Equal(0f, dest[64 + 0], 3);             // G: Zeile 0 außerhalb
        Assert.True(dest[7 * 8 + 7] > 0);              // R unten rechts im Bild
    }

    [Fact]
    public void Sample_PaddedPlane_LeavesRestZero()
    {
        var img = Gradient(4, 4);
        var dest = new float[3 * 8 * 8];
        Array.Fill(dest, 99f);

        AffineCrop.Sample(img, 2, 2, 0, 4, 4, 4, 4, dest, 8, 8, bgr: false, scale: 1f);

        Assert.Equal(0f, dest[4]);          // rechts neben dem Inhalt
        Assert.Equal(0f, dest[4 * 8]);      // unter dem Inhalt
        Assert.Equal(0f, dest[3 * 64 - 1]); // Ende des letzten Kanals
    }

    [Fact]
    public void Sample_Rotated90Clockwise_TurnsUpIntoRight()
    {
        // Hell nur in der rechten Bildhälfte; mit +90° zeigt „oben“ im Ausschnitt nach rechts → obere Hälfte des Ausschnitts hell.
        var w = 32;
        var px = new byte[w * w * 4];
        for (var y = 0; y < w; y++)
            for (var x = w / 2; x < w; x++)
                px[(y * w + x) * 4 + 2] = 200;
        var img = new BgraImage { Width = w, Height = w, Stride = w * 4, Pixels = px };
        var dest = new float[3 * 16 * 16];

        AffineCrop.Sample(img, 16, 16, Math.PI / 2, 24, 24, 16, 16, dest, 16, 16, bgr: false, scale: 1f);

        Assert.True(dest[2 * 16 + 8] > 150, "oben mittig sollte hell sein");
        Assert.True(dest[13 * 16 + 8] < 50, "unten mittig sollte dunkel sein");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.9)]
    [InlineData(-2.2)]
    public void CropToImage_IsInverseOfSampling(double angle)
    {
        // Ein heller Punkt im Bild bei (cx + u, cy + v) im gedrehten Rahmen muss im Ausschnitt dort liegen, wo CropToImage ihn zurückrechnet.
        const double cx = 100, cy = 80, src = 60;
        const int dst = 30;
        var w = 200;
        var px = new byte[w * w * 4];
        var (tx, ty) = AffineCrop.CropToImage(cx, cy, angle, src, src, dst, dst, 21.5, 9.5); // Zielpixel (21, 9) in der Mitte
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                px[((int)ty + dy) * w * 4 + ((int)tx + dx) * 4 + 2] = 255;
        var img = new BgraImage { Width = w, Height = w, Stride = w * 4, Pixels = px };
        var dest = new float[3 * dst * dst];

        AffineCrop.Sample(img, cx, cy, angle, src, src, dst, dst, dest, dst, dst, bgr: false, scale: 1f);

        var best = 0;
        for (var i = 1; i < dst * dst; i++)
            if (dest[i] > dest[best])
                best = i;
        Assert.InRange(best % dst, 20, 22);
        Assert.InRange(best / dst, 8, 10);
    }

    [Fact]
    public void RoiPointToNorm_CropCenterIsRoiCenter_AndCornersFollowRotation()
    {
        var roi = new HandRoi(new NormPoint(0.5, 0.4), 100, Math.PI / 2);

        var center = AffineCrop.RoiPointToNorm(roi, 1000, 500, 224, 112, 112);
        var top = AffineCrop.RoiPointToNorm(roi, 1000, 500, 224, 112, 0); // „oben“ im Ausschnitt

        Assert.Equal(0.5, center.X, 6);
        Assert.Equal(0.4, center.Y, 6);
        Assert.Equal((500 + 50.0) / 1000, top.X, 6); // +90°: oben zeigt nach rechts
        Assert.Equal(0.4, top.Y, 6);
    }

    [Fact]
    public void Sample_RejectsTooSmallBuffersAndEmptyImages()
    {
        var img = Gradient(4, 4);

        Assert.Throws<ArgumentException>(() => AffineCrop.Sample(img, 2, 2, 0, 4, 4, 4, 4, new float[10], 4, 4, false, 1f));
        Assert.Throws<ArgumentException>(() => AffineCrop.Sample(new BgraImage { Width = 4, Height = 4, Stride = 16, Pixels = new byte[8] },
            2, 2, 0, 4, 4, 4, 4, new float[48], 4, 4, false, 1f));
    }
}
