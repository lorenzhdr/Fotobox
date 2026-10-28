using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Prüft die Bartlage aus den fünf Gesichtspunkten (Mitte, Breite, Winkel) in Bildpixeln.</summary>
public sealed class MoustacheGeometryTests
{
    private const double W = 1000, H = 1000;

    // Aufrechtes Gesicht im quadratischen Bild: Augenabstand 200 px, Mundbreite 100 px, Nase 100 px über dem Mundband.
    private static FaceLandmarks Upright() => new(
        new NormRect(0.3, 0.25, 0.4, 0.5), 0.9,
        RightEye: new NormPoint(0.4, 0.4), LeftEye: new NormPoint(0.6, 0.4),
        Nose: new NormPoint(0.5, 0.5),
        MouthRight: new NormPoint(0.45, 0.6), MouthLeft: new NormPoint(0.55, 0.6));

    // Neutrales Gesicht im quadratischen Bild: Augenabstand 200 px, Mundbreite 0,68 · Augenabstand, Nase → Mundmitte 0,59 · Augenabstand
    // (die Proportionen der Test-Gesichter in PeaceHandFixtures).
    private static FaceLandmarks Neutral() => new(
        new NormRect(0.3, 0.25, 0.4, 0.5), 0.9,
        RightEye: new NormPoint(0.4, 0.4), LeftEye: new NormPoint(0.6, 0.4),
        Nose: new NormPoint(0.5, 0.5),
        MouthRight: new NormPoint(0.432, 0.618), MouthLeft: new NormPoint(0.568, 0.618));

    // Die frühere Formel (Breite aus dem größeren von Augenabstand und 1,7 · Mundbreite, Mitte 55 % von der Nase zur Mundmitte),
    // bewusst hier ausgeschrieben, damit die Kalibrierung nicht an den geänderten Konstanten hängt.
    private static (double Width, double X, double Y) OldFormula(FaceLandmarks f, double w, double h)
    {
        var eye = EyeDistance(f, w, h);
        var mouth = Math.Sqrt(Math.Pow((f.MouthLeft.X - f.MouthRight.X) * w, 2) + Math.Pow((f.MouthLeft.Y - f.MouthRight.Y) * h, 2));
        var mx = (f.MouthLeft.X + f.MouthRight.X) / 2 * w;
        var my = (f.MouthLeft.Y + f.MouthRight.Y) / 2 * h;
        return (Math.Max(1.0 * eye, 1.7 * mouth), f.Nose.X * w + 0.55 * (mx - f.Nose.X * w), f.Nose.Y * h + 0.55 * (my - f.Nose.Y * h));
    }

    private static double EyeDistance(FaceLandmarks f, double w, double h) =>
        Math.Sqrt(Math.Pow((f.LeftEye.X - f.RightEye.X) * w, 2) + Math.Pow((f.LeftEye.Y - f.RightEye.Y) * h, 2));

    private static NormPoint Rotate(NormPoint p, NormPoint pivot, double rad, double w, double h)
    {
        var dx = (p.X - pivot.X) * w;
        var dy = (p.Y - pivot.Y) * h;
        var c = Math.Cos(rad);
        var s = Math.Sin(rad);
        return new NormPoint(pivot.X + (dx * c - dy * s) / w, pivot.Y + (dx * s + dy * c) / h);
    }

    private static FaceLandmarks Rotated(FaceLandmarks f, double rad, double w, double h) => f with
    {
        RightEye = Rotate(f.RightEye, f.Nose, rad, w, h),
        LeftEye = Rotate(f.LeftEye, f.Nose, rad, w, h),
        MouthRight = Rotate(f.MouthRight, f.Nose, rad, w, h),
        MouthLeft = Rotate(f.MouthLeft, f.Nose, rad, w, h),
    };

    // Horizontale Spiegelung des Bilds: x' = 1 − x, und „rechts/links der Person“ tauschen die Namen.
    private static FaceLandmarks Flipped(FaceLandmarks f)
    {
        static NormPoint F(NormPoint p) => new(1 - p.X, p.Y);
        return f with
        {
            Box = new NormRect(1 - f.Box.Right, f.Box.Y, f.Box.Width, f.Box.Height),
            RightEye = F(f.LeftEye),
            LeftEye = F(f.RightEye),
            Nose = F(f.Nose),
            MouthRight = F(f.MouthLeft),
            MouthLeft = F(f.MouthRight),
        };
    }

    [Fact]
    public void Upright_CenterBetweenNoseAndMouth_WidthFromEyesLimitedByMouth_AngleZero()
    {
        var pose = MoustacheGeometry.FromFace(Upright(), W, H);

        Assert.Equal(0.5, pose.Center.X, 6);
        // Nase y = 0,5, Mundmitte y = 0,6 → 55 % des Wegs. (Der Mundwert 55 px liegt an der unteren Grenze des Bands um
        // 0,325 · 200 px = 65 px.)
        Assert.Equal(0.5 + MoustacheGeometry.NoseToMouthFactor * 0.1, pose.Center.Y, 6);
        // Mundbreite 100 px × 1,7 = 170 px liegt unter der Augenbreite 232 px: höchstens 10 % darunter.
        Assert.Equal(200 * MoustacheGeometry.EyeWidthFactor * 0.9, pose.WidthPx, 6);
        Assert.Equal(0, pose.AngleRad, 6);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(25)]
    [InlineData(-25)]
    [InlineData(-35)]
    [InlineData(70)]
    public void Tilted_AngleFollowsTheFace_WidthStaysTheSame(double degrees)
    {
        var rad = degrees * Math.PI / 180;
        var pose = MoustacheGeometry.FromFace(Rotated(Upright(), rad, W, H), W, H);

        Assert.Equal(rad, pose.AngleRad, 6);
        Assert.Equal(200 * MoustacheGeometry.EyeWidthFactor * 0.9, pose.WidthPx, 4);
    }

    [Fact]
    public void Tilted_CenterRotatesAroundTheNose()
    {
        var rad = 30 * Math.PI / 180;
        var pose = MoustacheGeometry.FromFace(Rotated(Upright(), rad, W, H), W, H);

        // Der Abstand Nase → Bartmitte (55 px) bleibt, die Richtung dreht mit.
        var dx = (pose.Center.X - 0.5) * W;
        var dy = (pose.Center.Y - 0.5) * H;
        Assert.Equal(55, Math.Sqrt(dx * dx + dy * dy), 4);
        Assert.Equal(-Math.Sin(rad) * 55, dx, 4);
        Assert.Equal(Math.Cos(rad) * 55, dy, 4);
    }

    [Fact]
    public void Mirrored_CenterMirrors_AngleChangesSign_WidthStaysTheSame()
    {
        var rad = 25 * Math.PI / 180;
        var tilted = Rotated(Upright(), rad, W, H);
        var normal = MoustacheGeometry.FromFace(tilted, W, H);
        var flipped = MoustacheGeometry.FromFace(Flipped(tilted), W, H);

        Assert.Equal(1 - normal.Center.X, flipped.Center.X, 6);
        Assert.Equal(normal.Center.Y, flipped.Center.Y, 6);
        Assert.Equal(normal.WidthPx, flipped.WidthPx, 6);
        Assert.Equal(-normal.AngleRad, flipped.AngleRad, 6);
    }

    [Fact]
    public void WideMouth_WidthGrowsAtMostTenPercent()
    {
        var face = Upright() with { MouthRight = new NormPoint(0.4, 0.6), MouthLeft = new NormPoint(0.6, 0.6) };
        var pose = MoustacheGeometry.FromFace(face, W, H);

        // Mundbreite 200 px × 1,7 = 340 px läge weit über den 232 px der Augen: gedeckelt auf +10 %.
        Assert.Equal(200 * MoustacheGeometry.EyeWidthFactor * 1.1, pose.WidthPx, 6);
    }

    [Fact]
    public void NeutralFixtureFace_MatchesTheOldFormulaWithinFivePercent()
    {
        var face = PeaceHandFixtures.Face(0.4, 0.4, 0.12);
        const double w = 1920, h = 1080;
        var old = OldFormula(face, w, h);
        var eye = EyeDistance(face, w, h);
        var pose = MoustacheGeometry.FromFace(face, w, h);

        Assert.InRange(pose.WidthPx / old.Width, 0.95, 1.05);
        Assert.InRange(Math.Abs(pose.Center.X * w - old.X), 0, 0.05 * eye);
        Assert.InRange(Math.Abs(pose.Center.Y * h - old.Y), 0, 0.05 * eye);
        Assert.Equal(0, pose.AngleRad, 6);
    }

    [Fact]
    public void NeutralTestFace_MatchesTheOldFormulaWithinFivePercent()
    {
        var old = OldFormula(Neutral(), W, H);
        var pose = MoustacheGeometry.FromFace(Neutral(), W, H);

        Assert.InRange(pose.WidthPx / old.Width, 0.95, 1.05);
        Assert.InRange(Math.Abs(pose.Center.X * W - old.X), 0, 0.05 * 200);
        Assert.InRange(Math.Abs(pose.Center.Y * H - old.Y), 0, 0.05 * 200);
    }

    [Fact]
    public void LaughingWideMouth_WidthAtMostTenPercentWider_AnchorBarelyMoves()
    {
        const double e = 200;
        var neutral = MoustacheGeometry.FromFace(Neutral(), W, H);
        // Mund 1,0 × Augenabstand breit, die Winkel 10 % des Augenabstands höher.
        var laughing = Neutral() with
        {
            MouthRight = new NormPoint(0.5 - 0.5 * e / W, 0.618 - 0.1 * e / H),
            MouthLeft = new NormPoint(0.5 + 0.5 * e / W, 0.618 - 0.1 * e / H),
        };
        var pose = MoustacheGeometry.FromFace(laughing, W, H);

        // Höchstens +10 % über dem Augenwert (das neutrale Gesicht liegt 0,3 % darunter, weil sein Mund 0,68 · Augenabstand misst).
        Assert.True(pose.WidthPx <= e * MoustacheGeometry.EyeWidthFactor * 1.1 + 1e-9, $"Breite {pose.WidthPx}");
        Assert.True(pose.WidthPx <= neutral.WidthPx * 1.105, $"Breite {pose.WidthPx} gegen neutral {neutral.WidthPx}");
        Assert.True(pose.WidthPx >= neutral.WidthPx);
        Assert.InRange(Math.Abs((pose.Center.Y - neutral.Center.Y) * H), 0, MoustacheGeometry.MouthAnchorTolerance * e + 1e-9);
        Assert.Equal(neutral.Center.X, pose.Center.X, 6);
        Assert.Equal(0, pose.AngleRad, 6);
    }

    [Fact]
    public void Approaching_WidthAndAnchorDistanceScaleLinearly()
    {
        // Alles ×3 um die Nase (Gesicht kommt näher; Augenabstand 60 → 180 px).
        FaceLandmarks Scaled(double k)
        {
            NormPoint S(NormPoint p) => new(0.5 + (p.X - 0.5) * k, 0.5 + (p.Y - 0.5) * k);
            var f = Neutral();
            return f with { RightEye = S(f.RightEye), LeftEye = S(f.LeftEye), MouthRight = S(f.MouthRight), MouthLeft = S(f.MouthLeft) };
        }

        var far = MoustacheGeometry.FromFace(Scaled(0.3), W, H);
        var near = MoustacheGeometry.FromFace(Scaled(0.9), W, H);

        Assert.Equal(3, near.WidthPx / far.WidthPx, 6);
        Assert.Equal(3, (near.Center.Y - 0.5) / (far.Center.Y - 0.5), 6);
        Assert.Equal(far.AngleRad, near.AngleRad, 6);
    }

    [Fact]
    public void FarAndNearFaces_AnchorBelowTheNoseByAFixedShareOfTheEyeDistance()
    {
        foreach (var width in new[] { 0.05, 0.1, 0.2 })
        {
            var face = PeaceHandFixtures.Face(0.5, 0.4, width);
            var pose = MoustacheGeometry.FromFace(face, 1920, 1080);
            var eye = EyeDistance(face, 1920, 1080);
            var below = pose.Center.Y * 1080 - face.Nose.Y * 1080;
            Assert.InRange(below / eye, MoustacheGeometry.EyeAnchorFactor - MoustacheGeometry.MouthAnchorTolerance,
                MoustacheGeometry.EyeAnchorFactor + MoustacheGeometry.MouthAnchorTolerance);
            Assert.InRange(pose.WidthPx / eye, MoustacheGeometry.EyeWidthFactor * 0.9, MoustacheGeometry.EyeWidthFactor * 1.1);
        }
    }

    [Theory]
    [InlineData(25)]
    [InlineData(-25)]
    public void Tilted_AnchorLiesPerpendicularToTheEyeLine_AtTheSameDistance(double degrees)
    {
        var rad = degrees * Math.PI / 180;
        var upright = MoustacheGeometry.FromFace(Neutral(), W, H);
        var face = Rotated(Neutral(), rad, W, H);
        var pose = MoustacheGeometry.FromFace(face, W, H);

        var dx = (pose.Center.X - face.Nose.X) * W;
        var dy = (pose.Center.Y - face.Nose.Y) * H;
        var distance = (upright.Center.Y - 0.5) * H;
        // „Nach unten“ ist die Senkrechte zur Augenlinie: (−sin, cos).
        Assert.Equal(-Math.Sin(rad) * distance, dx, 4);
        Assert.Equal(Math.Cos(rad) * distance, dy, 4);
        Assert.Equal(upright.WidthPx, pose.WidthPx, 4);
    }

    [Fact]
    public void Mirrored_Neutral_AnchorFollowsTheFaceDown()
    {
        var tilted = Rotated(Neutral(), 25 * Math.PI / 180, W, H);
        var normal = MoustacheGeometry.FromFace(tilted, W, H);
        var flipped = MoustacheGeometry.FromFace(Flipped(tilted), W, H);

        Assert.Equal(1 - normal.Center.X, flipped.Center.X, 6);
        Assert.Equal(normal.Center.Y, flipped.Center.Y, 6);
        Assert.Equal(normal.WidthPx, flipped.WidthPx, 6);
    }

    [Fact]
    public void AsymmetricMouthCorners_TiltTheAngleOnlyAFractionOfTheMouthTilt()
    {
        // Linker Mundwinkel (Person) 0,1 · Augenabstand höher (schiefes Lächeln): Die Mundlinie kippt, die Augenlinie nicht.
        var face = Neutral() with { MouthLeft = new NormPoint(0.568, 0.618 - 0.1 * 200 / H) };
        var mouthTilt = Math.Atan2(-0.1 * 200, (0.568 - 0.432) * W);
        var pose = MoustacheGeometry.FromFace(face, W, H);

        Assert.True(Math.Abs(pose.AngleRad) < 0.4 * Math.Abs(mouthTilt), $"Winkel {pose.AngleRad}, Mundlinie {mouthTilt}");
        Assert.True(pose.AngleRad < 0, "Der Winkel folgt der Richtung der Mundlinie.");
    }

    [Fact]
    public void WithoutEyeLine_WidthAndCenterComeFromTheMouth()
    {
        var p = new NormPoint(0.5, 0.5);
        var face = new FaceLandmarks(new NormRect(0.4, 0.4, 0.2, 0.2), 0.9, p, p, p, new NormPoint(0.45, 0.6), new NormPoint(0.55, 0.6));
        var pose = MoustacheGeometry.FromFace(face, W, H);

        Assert.Equal(MoustacheGeometry.MouthWidthFactor * 100, pose.WidthPx, 6);
        Assert.Equal(0.5 + MoustacheGeometry.NoseToMouthFactor * 0.1, pose.Center.Y, 6);
    }

    [Fact]
    public void NonSquareImage_AngleIsComputedInPixels()
    {
        // 45° in Pixeln: Augen und Mundwinkel 100 px nach rechts und 100 px nach unten, im Bild 2000 × 1000 → Δx = 0,05, Δy = 0,1.
        var face = new FaceLandmarks(new NormRect(0.4, 0.3, 0.2, 0.4), 0.9,
            RightEye: new NormPoint(0.45, 0.4), LeftEye: new NormPoint(0.5, 0.5),
            Nose: new NormPoint(0.5, 0.55),
            MouthRight: new NormPoint(0.45, 0.6), MouthLeft: new NormPoint(0.5, 0.7));
        var pose = MoustacheGeometry.FromFace(face, 2000, 1000);

        Assert.Equal(Math.PI / 4, pose.AngleRad, 6);
    }

    [Fact]
    public void DegenerateLandmarks_FallBackToHalfTheBoxWidth_AndAngleZero()
    {
        var p = new NormPoint(0.5, 0.5);
        var face = new FaceLandmarks(new NormRect(0.4, 0.4, 0.2, 0.2), 0.9, p, p, p, p, p);
        var pose = MoustacheGeometry.FromFace(face, 1920, 1080);

        Assert.Equal(0.2 * 1920 * 0.5, pose.WidthPx, 6);
        Assert.Equal(0, pose.AngleRad, 6);
        Assert.Equal(0.5, pose.Center.X, 6);
        Assert.Equal(0.5, pose.Center.Y, 6);
    }

    [Fact]
    public void UnknownImageSize_FallsBackToTheNosePosition()
    {
        var pose = MoustacheGeometry.FromFace(Upright(), 0, 0);

        Assert.Equal(Upright().Nose, pose.Center);
    }
}
