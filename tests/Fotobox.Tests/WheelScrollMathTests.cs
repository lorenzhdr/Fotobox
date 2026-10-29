using Fotobox.Core;
using Xunit;

namespace Fotobox.Tests;

public class WheelScrollMathTests
{
    [Fact]
    public void PixelsFor_FullNotch_ThreeLines_Is48Pixels() =>
        Assert.Equal(48, WheelScrollMath.PixelsFor(120, 3), 6);

    [Fact]
    public void PixelsFor_SmallTrackpadDelta_IsProportional() =>
        Assert.Equal(12, WheelScrollMath.PixelsFor(30, 3), 6);

    [Theory]
    [InlineData(-120, -48)]
    [InlineData(-30, -12)]
    [InlineData(240, 96)]
    [InlineData(0, 0)]
    public void PixelsFor_NegativeAndMultipleDeltas(int delta, double expected) =>
        Assert.Equal(expected, WheelScrollMath.PixelsFor(delta, 3), 6);

    [Fact]
    public void PixelsFor_UsesLineHeightAndLineCount() =>
        Assert.Equal(120 / 120.0 * 5 * 20, WheelScrollMath.PixelsFor(120, 5, 20), 6);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PixelsFor_PageMode_UsesViewportHeight(int lines)
    {
        Assert.Equal(300, WheelScrollMath.PixelsFor(120, lines, viewportHeight: 300), 6);
        Assert.Equal(-75, WheelScrollMath.PixelsFor(-30, lines, viewportHeight: 300), 6);
    }

    [Fact]
    public void Accumulator_FourSmallDeltas_ProduceOneNotch()
    {
        var acc = new WheelDeltaAccumulator();

        Assert.Equal(0, acc.Add(30));
        Assert.Equal(0, acc.Add(30));
        Assert.Equal(0, acc.Add(30));
        Assert.Equal(1, acc.Add(30));
        Assert.Equal(0, acc.Rest);
    }

    [Fact]
    public void Accumulator_KeepsRemainder()
    {
        var acc = new WheelDeltaAccumulator();

        Assert.Equal(1, acc.Add(150));
        Assert.Equal(30, acc.Rest);
        Assert.Equal(1, acc.Add(90));
        Assert.Equal(0, acc.Rest);
    }

    [Fact]
    public void Accumulator_NegativeDirection_ReturnsNegativeNotches()
    {
        var acc = new WheelDeltaAccumulator();

        Assert.Equal(-2, acc.Add(-240));
        Assert.Equal(0, acc.Add(-60));
        Assert.Equal(-1, acc.Add(-60));
    }

    [Fact]
    public void Accumulator_DirectionChange_DiscardsRemainder()
    {
        var acc = new WheelDeltaAccumulator();

        Assert.Equal(0, acc.Add(100));
        // Ohne Richtungswechsel wäre 100 - 30 = 70 Rest, hier zählt nur das neue Delta.
        Assert.Equal(0, acc.Add(-30));
        Assert.Equal(-30, acc.Rest);
        Assert.Equal(-1, acc.Add(-90));
    }

    [Fact]
    public void Accumulator_Reset_ClearsRest()
    {
        var acc = new WheelDeltaAccumulator();
        acc.Add(50);

        acc.Reset();

        Assert.Equal(0, acc.Rest);
    }
}
