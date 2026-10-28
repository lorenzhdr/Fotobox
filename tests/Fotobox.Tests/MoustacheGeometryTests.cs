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
    public void Upright_CenterBetweenNoseAndMouth_WidthFromEyes_AngleZero()
    {
        var pose = MoustacheGeometry.FromFace(Upright(), W, H);

        Assert.Equal(0.5, pose.Center.X, 6);
        // Nase y = 0,5, Mundmitte y = 0,6 → 55 % des Wegs.
        Assert.Equal(0.5 + MoustacheGeometry.NoseToMouthFactor * 0.1, pose.Center.Y, 6);
        Assert.Equal(200, pose.WidthPx, 6);
        Assert.Equal(0, pose.AngleRad, 6);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(-35)]
    [InlineData(70)]
    public void Tilted_AngleFollowsTheFace_WidthStaysTheSame(double degrees)
    {
        var rad = degrees * Math.PI / 180;
        var pose = MoustacheGeometry.FromFace(Rotated(Upright(), rad, W, H), W, H);

        Assert.Equal(rad, pose.AngleRad, 6);
        Assert.Equal(200, pose.WidthPx, 4);
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
    public void WideMouth_WidthComesFromTheMouth_WhenWiderThanTheEyes()
    {
        var face = Upright() with { MouthRight = new NormPoint(0.425, 0.6), MouthLeft = new NormPoint(0.575, 0.6) };
        var pose = MoustacheGeometry.FromFace(face, W, H);

        // Mundbreite 150 px × 1,7 = 255 px > Augenabstand 200 px.
        Assert.Equal(MoustacheGeometry.MouthWidthFactor * 150, pose.WidthPx, 6);
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
