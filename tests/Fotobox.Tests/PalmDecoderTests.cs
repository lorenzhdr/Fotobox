using Fotobox.Core.Fun;
using Fotobox.Services.Detection.Peace;
using Xunit;

namespace Fotobox.Tests;

public class PalmDecoderTests
{
    private static float[] Row(float score, float x, float y, float size, float kp0x, float kp0y, float kp2x, float kp2y) =>
        [score, x, y, size, kp0x, kp0y, kp2x, kp2y];

    private static float[] Rows(params float[][] rows) => [.. rows.SelectMany(r => r)];

    [Fact]
    public void WindowFor_FullImage_IsLetterboxedSquare()
    {
        var w = PalmWindow.For(NormRect.Full, 1920, 1080);

        Assert.Equal(1920, w.Side, 6);
        Assert.Equal(0, w.X0, 6);
        Assert.Equal((1080 - 1920) / 2.0, w.Y0, 6); // schwarze Balken oben/unten
    }

    [Fact]
    public void WindowFor_Region_IsCenteredSquareOfLongerSide()
    {
        // 0,2 × 0,5 des 1000 × 500 Bilds = 200 × 250 px, Mitte (500, 250) → Quadrat 250.
        var w = PalmWindow.For(new NormRect(0.4, 0.25, 0.2, 0.5), 1000, 500);

        Assert.Equal(250, w.Side, 6);
        Assert.Equal(375, w.X0, 6);
        Assert.Equal(125, w.Y0, 6);
    }

    [Fact]
    public void Decode_MapsWindowCoordinatesBackToImage()
    {
        // Bild 1000 × 500, ganzes Bild: Fenster 1000 × 1000, Y0 = −250.
        var window = PalmWindow.For(NormRect.Full, 1000, 500);
        var data = Rows(Row(0.9f, 0.5f, 0.5f, 0.1f, 0.4f, 0.6f, 0.5f, 0.4f));

        var c = Assert.Single(PalmDecoder.Decode(data, 1, window, 1000, 500, 0.5, 5));

        Assert.Equal(0.9, c.Score, 6);
        Assert.Equal(0.5, c.Center.X, 6);          // 0,5 · 1000 / 1000
        Assert.Equal(0.5, c.Center.Y, 6);          // (−250 + 500) / 500
        Assert.Equal(0.1, c.Size, 6);              // 0,1 · 1000 px / Bildbreite 1000
        Assert.Equal(0.4, c.Kp0.X, 6);
        Assert.Equal((-250 + 0.6 * 1000) / 500, c.Kp0.Y, 6);
        Assert.Equal(0.5, c.Kp2.X, 6);
        Assert.Equal((-250 + 0.4 * 1000) / 500, c.Kp2.Y, 6);
    }

    [Fact]
    public void Decode_FiltersByScoreAndSize_SortsDescending_AndLimits()
    {
        var window = new PalmWindow(0, 0, 100);
        var data = Rows(
            Row(0.55f, 0.2f, 0.2f, 0.1f, 0, 0, 0, 0),
            Row(0.40f, 0.3f, 0.3f, 0.1f, 0, 0, 0, 0),   // unter 0,5
            Row(0.95f, 0.4f, 0.4f, 0.1f, 0, 0, 0, 0),
            Row(0.99f, 0.5f, 0.5f, 0.0f, 0, 0, 0, 0),   // Größe 0
            Row(0.80f, 0.6f, 0.6f, 0.1f, 0, 0, 0, 0));

        var all = PalmDecoder.Decode(data, 5, window, 100, 100, 0.5, 10);
        var two = PalmDecoder.Decode(data, 5, window, 100, 100, 0.5, 2);

        Assert.Equal(new[] { 0.95, 0.80, 0.55 }, all.Select(c => Math.Round(c.Score, 2)));
        Assert.Equal(2, two.Count);
        Assert.Equal(0.95, two[0].Score, 6);
    }

    [Fact]
    public void Decode_RejectsCandidatesWithCenterOutsideImage()
    {
        // Fenster ragt über den oberen Rand: Mitte y = −10 px.
        var window = new PalmWindow(0, -50, 100);
        var data = Rows(Row(0.9f, 0.5f, 0.4f, 0.1f, 0, 0, 0, 0), Row(0.9f, 0.5f, 0.6f, 0.1f, 0, 0, 0, 0));

        var list = PalmDecoder.Decode(data, 2, window, 100, 100, 0.5, 5);

        Assert.Single(list);
        Assert.Equal(0.1, list[0].Center.Y, 6);
    }

    [Fact]
    public void Decode_TooShortData_Throws()
    {
        Assert.Throws<ArgumentException>(() => PalmDecoder.Decode(new float[7], 1, new PalmWindow(0, 0, 10), 10, 10, 0.5, 1));
    }

    [Fact]
    public void Decode_EmptyOutput_GivesNothing()
    {
        Assert.Empty(PalmDecoder.Decode(ReadOnlySpan<float>.Empty, 0, new PalmWindow(0, 0, 10), 10, 10, 0.5, 3));
    }
}
