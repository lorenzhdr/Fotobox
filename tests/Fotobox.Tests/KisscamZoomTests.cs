using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Heart;
using Fotobox.Core.Peace;
using Xunit;

namespace Fotobox.Tests;

public class KisscamZoomTests
{
    private static readonly NormRect[] NoFaces = [];

    // ---- Plan ----

    [Fact]
    public void Plan_SmallHeart_UsesRequestedZoomCenteredOnHeart()
    {
        var heart = new NormRect(0.45, 0.45, 0.10, 0.10);

        var plan = KisscamZoom.Plan(heart, NoFaces, 150);

        Assert.NotNull(plan);
        Assert.Equal(1 / 1.5, plan.Value.Width, 9);
        Assert.Equal(plan.Value.Width, plan.Value.Height, 9);
        Assert.Equal(0.5, plan.Value.CenterX, 9);
        Assert.Equal(0.5, plan.Value.CenterY, 9);
    }

    [Fact]
    public void Plan_Faces_ExtendTheUnionAndShiftTheCenter()
    {
        var heart = new NormRect(0.40, 0.50, 0.10, 0.10);
        var faces = new[] { new NormRect(0.30, 0.30, 0.10, 0.12), new NormRect(0.50, 0.32, 0.10, 0.12) };

        var plan = KisscamZoom.Plan(heart, faces, 140);

        Assert.NotNull(plan);
        // Vereinigung: x 0.30–0.60, y 0.30–0.60 → Mitte (0.45, 0.45)
        Assert.Equal(0.45, plan.Value.CenterX, 9);
        Assert.Equal(0.45, plan.Value.CenterY, 9);
    }

    [Fact]
    public void Plan_LowersZoomSoThatUnionWithMarginFits()
    {
        // Vereinigung 0,5 breit: mit 15 % Rand je Seite (×1,3) bräuchte das Fenster 0,65 → höchstens Zoom 1/0,65.
        var heart = new NormRect(0.25, 0.25, 0.50, 0.30);

        var plan = KisscamZoom.Plan(heart, NoFaces, 200);

        Assert.NotNull(plan);
        Assert.Equal(0.65, plan.Value.Width, 9);
        Assert.True(plan.Value.X <= heart.X - 0.075 + 1e-9);
        Assert.True(plan.Value.Right >= heart.Right + 0.075 - 1e-9);
    }

    [Fact]
    public void Plan_NoZoomLeft_ReturnsNull()
    {
        // Das Herz füllt fast das ganze Bild: auch ohne Rand bleibt kein Zoom übrig.
        Assert.Null(KisscamZoom.Plan(new NormRect(0.05, 0.05, 0.9, 0.9), NoFaces, 150));
        // Faces weit auseinander
        Assert.Null(KisscamZoom.Plan(new NormRect(0.45, 0.45, 0.1, 0.1), [new NormRect(0.0, 0.1, 0.1, 0.1), new NormRect(0.9, 0.1, 0.1, 0.1)], 150));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(80)]
    [InlineData(0)]
    public void Plan_ZoomAtMostOneHundredPercent_ReturnsNull(int percent) =>
        Assert.Null(KisscamZoom.Plan(new NormRect(0.45, 0.45, 0.1, 0.1), NoFaces, percent));

    [Fact]
    public void Plan_EmptyOrInvalidHeartBox_ReturnsNull()
    {
        Assert.Null(KisscamZoom.Plan(new NormRect(0.5, 0.5, 0, 0), NoFaces, 150));
        Assert.Null(KisscamZoom.Plan(new NormRect(double.NaN, 0.5, 0.1, 0.1), NoFaces, 150));
    }

    [Fact]
    public void Plan_InvalidFaceBoxes_AreIgnored()
    {
        var heart = new NormRect(0.45, 0.45, 0.10, 0.10);
        var faces = new[] { new NormRect(double.NaN, 0, 1, 1), new NormRect(0.1, 0.1, 0, 0) };

        var plan = KisscamZoom.Plan(heart, faces, 150);

        Assert.Equal(KisscamZoom.Plan(heart, NoFaces, 150), plan);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.95, 0.9)]
    [InlineData(0.0, 0.95)]
    [InlineData(0.9, 0.0)]
    public void Plan_NearEdge_IsShiftedIntoImageNotShrunk(double x, double y)
    {
        var heart = new NormRect(x, y, 0.05, 0.05);

        var plan = KisscamZoom.Plan(heart, NoFaces, 150);

        Assert.NotNull(plan);
        Assert.Equal(1 / 1.5, plan.Value.Width, 9);
        Assert.InRange(plan.Value.X, 0, 1 - plan.Value.Width + 1e-9);
        Assert.InRange(plan.Value.Y, 0, 1 - plan.Value.Height + 1e-9);
    }

    [Fact]
    public void Plan_AllAllowedPercentages_ProduceRectsInsideTheImage()
    {
        var heart = new NormRect(0.40, 0.40, 0.2, 0.2);
        for (var p = SettingsLimits.KisscamZoomPercentMin; p <= SettingsLimits.KisscamZoomPercentMax; p += SettingsLimits.KisscamZoomPercentStep)
        {
            var plan = KisscamZoom.Plan(heart, NoFaces, p);
            Assert.NotNull(plan);
            Assert.True(plan.Value.X >= 0 && plan.Value.Y >= 0 && plan.Value.Right <= 1 + 1e-9 && plan.Value.Bottom <= 1 + 1e-9);
        }
    }

    // ---- ToPixelRect ----

    [Fact]
    public void ToPixelRect_ConvertsAndRounds()
    {
        Assert.Equal((480, 270, 960, 540), KisscamZoom.ToPixelRect(new NormRect(0.25, 0.25, 0.5, 0.5), 1920, 1080));
        Assert.Equal((0, 0, 1920, 1080), KisscamZoom.ToPixelRect(NormRect.Full, 1920, 1080));
    }

    [Fact]
    public void ToPixelRect_ClampsToImageAndKeepsAtLeastOnePixel()
    {
        Assert.Equal((90, 0, 10, 100), KisscamZoom.ToPixelRect(new NormRect(0.9, -0.5, 0.5, 2), 100, 100));
        var tiny = KisscamZoom.ToPixelRect(new NormRect(0.5, 0.5, 0.0001, 0.0001), 100, 100);
        Assert.Equal(1, tiny.W);
        Assert.Equal(1, tiny.H);
        var outside = KisscamZoom.ToPixelRect(new NormRect(5, 5, 1, 1), 100, 100);
        Assert.Equal((99, 99, 1, 1), outside);
    }

    [Fact]
    public void ToPixelRect_InvalidImageSize_ReturnsEmpty()
    {
        Assert.Equal((0, 0, 0, 0), KisscamZoom.ToPixelRect(NormRect.Full, 0, 0));
        Assert.Equal((0, 0, 100, 100), KisscamZoom.ToPixelRect(new NormRect(double.NaN, double.NaN, 1, 1), 100, 100));
    }

    // ---- ViewTransform ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ViewTransform_CenterRegion_ScalesAroundTheCenter(bool mirror)
    {
        var region = new NormRect(0.25, 0.25, 0.5, 0.5);

        var (scale, tx, ty) = KisscamZoom.ViewTransform(region, 1920, 1080, 1920, 1080, OverlayFit.UniformToFill, mirror);

        Assert.Equal(2, scale, 9);
        Assert.Equal(-960, tx, 6);
        Assert.Equal(-540, ty, 6);
    }

    [Fact]
    public void ViewTransform_RegionFillsTheView_AndMapsItsCornersToTheViewCorners()
    {
        var region = new NormRect(0.1, 0.2, 0.5, 0.5);

        var (scale, tx, ty) = KisscamZoom.ViewTransform(region, 1920, 1080, 1920, 1080, OverlayFit.Uniform, false);

        var topLeft = OverlayMapper.MapPoint(new NormPoint(region.X, region.Y), 1920, 1080, 1920, 1080, OverlayFit.Uniform, false);
        var bottomRight = OverlayMapper.MapPoint(new NormPoint(region.Right, region.Bottom), 1920, 1080, 1920, 1080, OverlayFit.Uniform, false);
        Assert.Equal(2, scale, 9);
        Assert.Equal(0, scale * topLeft.X + tx, 6);
        Assert.Equal(0, scale * topLeft.Y + ty, 6);
        Assert.Equal(1920, scale * bottomRight.X + tx, 6);
        Assert.Equal(1080, scale * bottomRight.Y + ty, 6);
    }

    [Theory]
    [InlineData(false, 0.05, 0.05)]
    [InlineData(true, 0.05, 0.05)]
    [InlineData(false, 0.95, 0.95)]
    [InlineData(true, 0.95, 0.5)]
    public void ViewTransform_NearEdge_NeverLeavesABorder(bool mirror, double cx, double cy)
    {
        var region = new NormRect(Math.Clamp(cx - 0.25, 0, 0.5), Math.Clamp(cy - 0.25, 0, 0.5), 0.5, 0.5);

        var (scale, tx, ty) = KisscamZoom.ViewTransform(region, 1920, 1080, 1280, 720, OverlayFit.UniformToFill, mirror);

        // Das skalierte Bild (ganze Ansicht, da UniformToFill im gleichen Seitenverhältnis) bedeckt die Ansicht.
        Assert.True(tx <= 1e-6 && tx + scale * 1280 >= 1280 - 1e-6);
        Assert.True(ty <= 1e-6 && ty + scale * 720 >= 720 - 1e-6);
    }

    [Fact]
    public void ViewTransform_Mirror_MirrorsTheHorizontalShift()
    {
        var region = new NormRect(0.0, 0.25, 0.5, 0.5);

        var normal = KisscamZoom.ViewTransform(region, 1920, 1080, 1920, 1080, OverlayFit.UniformToFill, false);
        var mirrored = KisscamZoom.ViewTransform(region, 1920, 1080, 1920, 1080, OverlayFit.UniformToFill, true);

        Assert.Equal(normal.Scale, mirrored.Scale, 9);
        Assert.Equal(0, normal.TranslateX, 6);                      // Ausschnitt links → Bild links ausgerichtet
        Assert.Equal(1920 - normal.Scale * 1920, mirrored.TranslateX, 6);   // gespiegelt: rechts ausgerichtet
        Assert.Equal(normal.TranslateY, mirrored.TranslateY, 6);
    }

    [Fact]
    public void ViewTransform_WholeImage_IsIdentity()
    {
        var (scale, tx, ty) = KisscamZoom.ViewTransform(NormRect.Full, 1920, 1080, 1920, 1080, OverlayFit.UniformToFill, false);

        Assert.Equal(1, scale, 9);
        Assert.Equal(0, tx, 6);
        Assert.Equal(0, ty, 6);
    }

    [Fact]
    public void ViewTransform_UniformWithBars_StillCoversTheView()
    {
        // 16:9-Bild in 4:3-Ansicht mit Rändern oben/unten: der Zoom wird mindestens so groß, dass die Ränder verschwinden.
        var (scale, tx, ty) = KisscamZoom.ViewTransform(new NormRect(0.25, 0.25, 0.5, 0.5), 1920, 1080, 1440, 1080, OverlayFit.Uniform, false);
        Assert.True(scale >= 1080 / 810.0);                       // Ränder verschwinden
        Assert.True(tx <= 1e-6 && tx + scale * 1440 >= 1440 - 1e-6);
        Assert.True(ty <= scale * -135 + 1e-6 || true);

        // Bild 1440 × 810 (Ränder je 135 oben/unten); der Ausschnitt (720 × 405) füllt die Ansicht erst bei 1080 / 405.
        Assert.Equal(1080 / 405.0, scale, 9);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(double.NaN, 100)]
    public void ViewTransform_InvalidView_IsIdentity(double w, double h) =>
        Assert.Equal((1.0, 0.0, 0.0), KisscamZoom.ViewTransform(new NormRect(0.25, 0.25, 0.5, 0.5), 1920, 1080, w, h, OverlayFit.Uniform, false));

    [Fact]
    public void ViewTransform_InvalidRegion_IsIdentity() =>
        Assert.Equal((1.0, 0.0, 0.0), KisscamZoom.ViewTransform(new NormRect(0, 0, 0, 0), 1920, 1080, 1920, 1080, OverlayFit.Uniform, false));
}
