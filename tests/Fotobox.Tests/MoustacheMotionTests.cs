using Fotobox.Core.Peace;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Plopp (<see cref="MoustachePop"/>) und Wippfeder (<see cref="MoustacheWobble"/>, <see cref="WobbleState"/>): rein, ohne Uhr.</summary>
public sealed class MoustacheMotionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // ---- Plopp

    [Fact]
    public void Pop_StartsAtZero_OvershootsSlightly_AndEndsExactlyAtOne()
    {
        Assert.Equal(0, MoustachePop.Scale(0));
        Assert.Equal(0, MoustachePop.Scale(-1));
        Assert.Equal(1, MoustachePop.Scale(MoustachePop.DurationSeconds));
        Assert.Equal(1, MoustachePop.Scale(5));

        var peak = Enumerable.Range(1, 349).Select(i => MoustachePop.Scale(i * MoustachePop.DurationSeconds / 350)).Max();
        Assert.InRange(peak, 1.05, 1.2); // leichter Überschwinger
        Assert.Equal(peak, MoustachePop.Peak, 3);
    }

    [Fact]
    public void Pop_GrowsMonotonicallyUntilTheOvershoot_ThenSettlesNearOne()
    {
        double previous = 0;
        for (var t = 0.005; t < 0.1; t += 0.005)
        {
            var value = MoustachePop.Scale(t);
            Assert.True(value > previous, $"t={t}");
            previous = value;
        }
        // Kurz vor dem Ende ist der Ausschlag klein (kein Sprung auf 1).
        Assert.InRange(MoustachePop.Scale(MoustachePop.DurationSeconds - 0.001), 0.97, 1.03);
        Assert.True(MoustachePop.IsRunning(0.2));
        Assert.False(MoustachePop.IsRunning(MoustachePop.DurationSeconds));
    }

    // ---- Wippen

    [Fact]
    public void Wobble_Rest_StaysRest()
    {
        Assert.True(WobbleState.Rest.IsRest);
        Assert.True(WobbleState.Rest.Advance(1).IsRest);
    }

    [Fact]
    public void Wobble_FirstSampleAndSmallNoise_StartNothing()
    {
        var wobble = new MoustacheWobble();
        wobble.OnSample(T0, 0.6, 0.55);
        Assert.True(wobble.At(T0).IsRest);

        // Rauschen unterhalb des Totbands (in Augenabständen)
        for (var i = 1; i <= 30; i++)
            wobble.OnSample(T0.AddMilliseconds(100 * i), 0.6 + (i % 2 == 0 ? 1 : -1) * 0.008, 0.55 + (i % 2 == 0 ? -1 : 1) * 0.008);
        Assert.True(wobble.At(T0.AddSeconds(3)).IsRest);
    }

    [Fact]
    public void Wobble_QuickMouthChange_StirsTheSpring_WithinTheLimits_AndSettles()
    {
        var wobble = new MoustacheWobble();
        wobble.OnSample(T0, 0.6, 0.55);
        wobble.OnSample(T0.AddMilliseconds(100), 0.6, 0.55);
        wobble.OnSample(T0.AddMilliseconds(200), 0.78, 0.80); // Mund geht schlagartig auf

        var seenOffset = 0.0;
        var seenAngle = 0.0;
        var now = T0.AddMilliseconds(200);
        for (var ms = 0; ms <= 3000; ms += 5)
        {
            var state = wobble.At(now.AddMilliseconds(ms));
            Assert.InRange(state.Offset, -MoustacheWobble.MaxOffset, MoustacheWobble.MaxOffset);
            Assert.InRange(state.Angle, -MoustacheWobble.MaxAngle, MoustacheWobble.MaxAngle);
            seenOffset = Math.Max(seenOffset, Math.Abs(state.Offset));
            seenAngle = Math.Max(seenAngle, Math.Abs(state.Angle));
        }

        Assert.True(seenOffset > 0.005, "Die Feder bewegt sich sichtbar");
        Assert.True(seenAngle > 0.002);
        Assert.True(wobble.At(T0.AddSeconds(5)).IsRest); // im Stillstand ausgelaufen
    }

    [Fact]
    public void Wobble_ExtremeInput_IsClamped()
    {
        var wobble = new MoustacheWobble();
        wobble.OnSample(T0, 0.6, 0.55);
        for (var i = 1; i <= 20; i++)
            wobble.OnSample(T0.AddMilliseconds(20 * i), 0.6 + i * 3, 0.55 - i * 3);

        for (var ms = 0; ms < 2000; ms += 4)
        {
            var state = wobble.At(T0.AddMilliseconds(400 + ms));
            Assert.InRange(state.Offset, -MoustacheWobble.MaxOffset, MoustacheWobble.MaxOffset);
            Assert.InRange(state.Angle, -MoustacheWobble.MaxAngle, MoustacheWobble.MaxAngle);
        }
    }

    [Fact]
    public void WobbleState_Advance_IsConsistentInSteps_AndDecays()
    {
        var start = new WobbleState(0.02, 0.5, 0.01, -0.3);
        var direct = start.Advance(0.3);
        var stepped = start.Advance(0.1).Advance(0.1).Advance(0.1);

        Assert.Equal(direct.Offset, stepped.Offset, 4);
        Assert.Equal(direct.Angle, stepped.Angle, 4);
        Assert.True(start.Advance(2).IsRest);
        // gedämpft: Der Betrag nimmt über die Schwingungen ab
        double Peak(double from, double to)
        {
            var max = 0.0;
            for (var t = from; t < to; t += 0.002)
                max = Math.Max(max, Math.Abs(start.Advance(t).Offset));
            return max;
        }
        Assert.True(Peak(0.3, 0.6) < Peak(0.0, 0.3));
    }

    [Fact]
    public void Wobble_Reset_ForgetsTheMouthAndSpring()
    {
        var wobble = new MoustacheWobble();
        wobble.OnSample(T0, 0.6, 0.55);
        wobble.OnSample(T0.AddMilliseconds(100), 0.9, 0.9);
        Assert.False(wobble.At(T0.AddMilliseconds(100)).IsRest);

        wobble.Reset();
        Assert.True(wobble.At(T0.AddMilliseconds(100)).IsRest);
        wobble.OnSample(T0.AddMilliseconds(200), 0.9, 0.9); // erste Beobachtung nach dem Reset: nichts
        Assert.True(wobble.At(T0.AddMilliseconds(200)).IsRest);
    }
}
