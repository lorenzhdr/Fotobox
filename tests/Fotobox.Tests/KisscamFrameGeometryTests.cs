using Fotobox.Core.Heart;
using Xunit;

namespace Fotobox.Tests;

public class KisscamFrameGeometryTests
{
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1200, 1800)]
    [InlineData(1800, 1200)]
    [InlineData(640, 360)]
    public void For_DerivesMeasuresFromTheShorterEdge(double w, double h)
    {
        var m = Math.Min(w, h);

        var g = KisscamFrameGeometry.For(w, h);

        Assert.Equal(w, g.Width);
        Assert.Equal(h, g.Height);
        Assert.Equal(0.03 * m, g.Thickness, 9);
        Assert.Equal(0.13 * m, g.Banner.Height, 9);
        Assert.Equal(0.03 * m, g.BannerCornerRadius, 9);
        Assert.Equal(0.075 * m, g.FontEm, 9);
        Assert.Equal(4, g.CornerHearts.Count);
        Assert.All(g.CornerHearts, r => Assert.Equal(0.11 * m, r.Width, 9));
        Assert.All(g.CornerHearts, r => Assert.Equal(r.Width, r.Height, 9));
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1200, 1800)]
    public void For_BannerIsCenteredAtTheTopAndReachesIntoTheFrame(double w, double h)
    {
        var g = KisscamFrameGeometry.For(w, h);

        Assert.Equal(w / 2, g.Banner.CenterX, 6);
        Assert.Equal(0.46 * w, g.Banner.Width, 6);
        Assert.True(g.Banner.Y >= 0 && g.Banner.Y < g.Thickness);       // beginnt im Rahmenband
        Assert.True(g.Banner.Bottom > g.Thickness);                     // ragt darüber hinaus ins Bild
    }

    [Fact]
    public void For_BannerGrowsWithTheTextUpToTheLimit()
    {
        var narrow = KisscamFrameGeometry.For(1920, 1080);
        var wide = KisscamFrameGeometry.For(1920, 1080, 1000);
        var huge = KisscamFrameGeometry.For(1920, 1080, 100000);

        Assert.Equal(1000, wide.Banner.Width, 6);
        Assert.True(wide.Banner.Width > narrow.Banner.Width);
        Assert.True(huge.Banner.Width <= 0.9 * 1920 + 1e-6);
        Assert.Equal(1920 / 2.0, huge.Banner.CenterX, 6);
        // Mindestbreite kleiner als der Standard: Standardbreite bleibt
        Assert.Equal(narrow.Banner.Width, KisscamFrameGeometry.For(1920, 1080, 10).Banner.Width, 6);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1200, 1800)]
    [InlineData(1800, 1200)]
    public void For_BannerNeverTouchesTheTopCornerHearts(double w, double h)
    {
        var g = KisscamFrameGeometry.For(w, h, 100000);

        var topLeft = g.CornerHearts[0];
        var topRight = g.CornerHearts[1];
        Assert.True(g.Banner.X > topLeft.Right);
        Assert.True(g.Banner.Right < topRight.X);
    }

    [Fact]
    public void For_CornerHeartsSitInsideTheFrameWithGap()
    {
        var g = KisscamFrameGeometry.For(1920, 1080);
        var gap = 0.02 * 1080;

        var tl = g.CornerHearts[0];
        var br = g.CornerHearts[3];
        Assert.Equal(g.Thickness + gap, tl.X, 6);
        Assert.Equal(g.Thickness + gap, tl.Y, 6);
        Assert.Equal(1920 - g.Thickness - gap, br.Right, 6);
        Assert.Equal(1080 - g.Thickness - gap, br.Bottom, 6);
        Assert.Equal(g.CornerHearts[0].Y, g.CornerHearts[1].Y, 6);
        Assert.Equal(g.CornerHearts[2].Y, g.CornerHearts[3].Y, 6);
    }

    [Fact]
    public void For_LightsRunAlongTheFrameMidline_ButNotUnderTheBanner()
    {
        var g = KisscamFrameGeometry.For(1920, 1080);
        var half = g.Thickness / 2;

        Assert.NotEmpty(g.Lights);
        Assert.All(g.Lights, p =>
        {
            var onVertical = Math.Abs(p.X - half) < 1e-6 || Math.Abs(p.X - (1920 - half)) < 1e-6;
            var onHorizontal = Math.Abs(p.Y - half) < 1e-6 || Math.Abs(p.Y - (1080 - half)) < 1e-6;
            Assert.True(onVertical || onHorizontal);
            Assert.False(g.Banner.Contains(p.X, p.Y));
        });
        // Abstand ungefähr 0,05 · m (gleichmäßige Teilung des Umfangs, im Mittel etwas darüber wegen der Lücke unter dem Banner)
        var spacing = 2 * ((1920 - g.Thickness) + (1080 - g.Thickness)) / g.Lights.Count;
        Assert.InRange(spacing, 0.04 * 1080, 0.08 * 1080);
        Assert.Equal(0.0075 * 1080, g.LightRadius, 9);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-5, -5)]
    [InlineData(double.NaN, 100)]
    public void For_InvalidSize_ReturnsEmptyGeometry(double w, double h)
    {
        var g = KisscamFrameGeometry.For(w, h);

        Assert.Equal(0, g.Thickness);
        Assert.Empty(g.CornerHearts);
        Assert.Empty(g.Lights);
        Assert.Equal(0, g.Banner.Width);
    }

    [Fact]
    public void For_SameAspectScalesLinearly()
    {
        var small = KisscamFrameGeometry.For(640, 360);
        var large = KisscamFrameGeometry.For(1920, 1080);

        Assert.Equal(small.Thickness * 3, large.Thickness, 6);
        Assert.Equal(small.Banner.Width * 3, large.Banner.Width, 6);
        Assert.Equal(small.CornerHearts[3].X * 3, large.CornerHearts[3].X, 6);
    }
}
