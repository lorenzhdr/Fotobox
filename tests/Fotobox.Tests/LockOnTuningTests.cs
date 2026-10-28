using Fotobox.Core;
using Fotobox.Core.Fun;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Kennlinien der Einrast-Intensität: 50 % ist das bisherige Aussehen, 0 % dezent, 100 % deutlich stärker.</summary>
public sealed class LockOnTuningTests
{
    [Fact]
    public void FiftyPercent_IsExactlyTheFormerLook()
    {
        Assert.Equal(1.8, LockOnTuning.ShrinkStartScale(50));
        Assert.Equal(1.0, LockOnTuning.PulseAmplitude(50));
        Assert.Equal(1.0, LockOnTuning.LineFactor(50));
        Assert.Equal(180.0, LockOnTuning.BlinkPeriodMilliseconds(50));
        Assert.Equal(0.3, LockOnTuning.BlinkMinOpacity(50));
        Assert.Equal(1.0, LockOnTuning.SoundGain(50));
        Assert.Equal(1.0, LockOnTuning.TimeScale(1200));
    }

    [Fact]
    public void ZeroAndHundredPercent_AreTheEnds()
    {
        Assert.Equal(1.2, LockOnTuning.ShrinkStartScale(0));
        Assert.Equal(2.4, LockOnTuning.ShrinkStartScale(100));
        Assert.Equal(0.4, LockOnTuning.PulseAmplitude(0));
        Assert.Equal(1.6, LockOnTuning.PulseAmplitude(100));
        Assert.Equal(0.6, LockOnTuning.LineFactor(0));
        Assert.Equal(1.4, LockOnTuning.LineFactor(100));
        Assert.Equal(300.0, LockOnTuning.BlinkPeriodMilliseconds(0));
        Assert.Equal(120.0, LockOnTuning.BlinkPeriodMilliseconds(100));
        Assert.Equal(0.6, LockOnTuning.BlinkMinOpacity(0));
        Assert.Equal(0.1, LockOnTuning.BlinkMinOpacity(100));
        Assert.Equal(0.35, LockOnTuning.SoundGain(0));
        Assert.Equal(1.6, LockOnTuning.SoundGain(100));
    }

    [Fact]
    public void Curves_AreMonotonic_AndStayVisibleAtZero()
    {
        for (var p = 5; p <= 100; p += 5)
        {
            Assert.True(LockOnTuning.ShrinkStartScale(p) > LockOnTuning.ShrinkStartScale(p - 5));
            Assert.True(LockOnTuning.PulseAmplitude(p) > LockOnTuning.PulseAmplitude(p - 5));
            Assert.True(LockOnTuning.LineFactor(p) > LockOnTuning.LineFactor(p - 5));
            Assert.True(LockOnTuning.BlinkPeriodMilliseconds(p) < LockOnTuning.BlinkPeriodMilliseconds(p - 5)); // schneller
            Assert.True(LockOnTuning.BlinkMinOpacity(p) < LockOnTuning.BlinkMinOpacity(p - 5));                 // kontrastreicher
            Assert.True(LockOnTuning.SoundGain(p) > LockOnTuning.SoundGain(p - 5));
        }
        Assert.True(LockOnTuning.ShrinkStartScale(0) > 1.0);   // auch bei 0 % bewegt sich die Box
        Assert.True(LockOnTuning.PulseAmplitude(0) > 0.0);
        Assert.True(LockOnTuning.SoundGain(0) > 0.0);
    }

    [Fact]
    public void OutOfRangeInput_IsClamped()
    {
        Assert.Equal(LockOnTuning.PulseAmplitude(0), LockOnTuning.PulseAmplitude(-40));
        Assert.Equal(LockOnTuning.PulseAmplitude(100), LockOnTuning.PulseAmplitude(400));
        Assert.Equal(LockOnTuning.TimeScale(500), LockOnTuning.TimeScale(10));
        Assert.Equal(LockOnTuning.TimeScale(5000), LockOnTuning.TimeScale(90_000));
        Assert.True(LockOnTuning.PulseAmplitude(100) <= LockOnTuning.MaxPulse);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 5)]
    [InlineData(50, 50)]
    [InlineData(52, 50)]
    [InlineData(53, 55)]
    [InlineData(100, 100)]
    [InlineData(250, 100)]
    [InlineData(-9, 0)]
    public void SoundStep_RoundsToFivePercent(int percent, int expected) =>
        Assert.Equal(expected, LockOnTuning.SoundStep(percent));

    [Fact]
    public void TimeScale_IsDurationOverReference()
    {
        Assert.Equal(500 / 1200.0, LockOnTuning.TimeScale(500), 9);
        Assert.Equal(5000 / 1200.0, LockOnTuning.TimeScale(5000), 9);
    }
}
