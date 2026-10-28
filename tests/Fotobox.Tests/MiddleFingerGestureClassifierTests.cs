using Fotobox.Core;
using Fotobox.Core.Peace;
using Xunit;

namespace Fotobox.Tests;

public sealed class MiddleFingerGestureClassifierTests
{
    private static bool Is(HandLandmarks hand, int sensitivity = 50, double aspect = 16.0 / 9) =>
        MiddleFingerGestureClassifier.Classify(hand, sensitivity, aspect).IsMiddleFinger;

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(95)]
    public void MiddleOnly_IsRecognised_AtAllSensitivities(int sensitivity)
    {
        var verdict = MiddleFingerGestureClassifier.Classify(PeaceHandFixtures.Hand(HandPose.MiddleOnly), sensitivity);
        Assert.True(verdict.IsMiddleFinger, verdict.RejectReason);
        Assert.Null(verdict.RejectReason);
    }

    [Fact]
    public void MiddleOnly_IsRotationInvariant_AcrossFullCircle()
    {
        for (var rotation = 0; rotation < 360; rotation += 15)
        {
            Assert.True(Is(PeaceHandFixtures.Hand(HandPose.MiddleOnly, rotationDeg: rotation)), $"Drehung {rotation}");
            Assert.True(Is(PeaceHandFixtures.Hand(HandPose.MiddleOnly, rotationDeg: rotation, mirror: true)), $"Drehung {rotation} gespiegelt");
            Assert.False(Is(PeaceHandFixtures.Hand(HandPose.Fist, rotationDeg: rotation)), $"Faust {rotation}");
        }
    }

    [Fact]
    public void MiddleOnly_WithThumbSpreadOut_IsStillRecognised()
    {
        Assert.True(Is(PeaceHandFixtures.Hand(HandPose.MiddleOnly, thumbOut: true)));
        Assert.True(Is(PeaceHandFixtures.Hand(HandPose.MiddleOnly, thumbOut: true, mirror: true, rotationDeg: 90)));
    }

    [Theory]
    [InlineData(4.0 / 3)]
    [InlineData(1.0)]
    [InlineData(16.0 / 9)]
    [InlineData(21.0 / 9)]
    public void Aspect_IsTakenIntoAccount(double aspect)
    {
        foreach (var rotation in new[] { 0, 90, 37 })
        {
            var hand = PeaceHandFixtures.Hand(HandPose.MiddleOnly, aspect: aspect, rotationDeg: rotation);
            Assert.True(Is(hand, 50, aspect), $"aspect {aspect}, Drehung {rotation}");
        }
    }

    [Theory]
    [InlineData(HandPose.Peace)]
    [InlineData(HandPose.Fist)]
    [InlineData(HandPose.Open)]
    [InlineData(HandPose.IndexOnly)]
    [InlineData(HandPose.ThreeFingers)]
    public void OtherGestures_AreRejected(HandPose pose)
    {
        foreach (var sensitivity in new[] { 10, 50, 95 })
        {
            var verdict = MiddleFingerGestureClassifier.Classify(PeaceHandFixtures.Hand(pose), sensitivity);
            Assert.False(verdict.IsMiddleFinger, $"{pose}@{sensitivity}");
            Assert.False(string.IsNullOrEmpty(verdict.RejectReason));
        }
    }

    [Fact]
    public void MiddleOnly_IsNotPeace_AndPeaceIsNotMiddle()
    {
        var middle = PeaceHandFixtures.Hand(HandPose.MiddleOnly);
        var peace = PeaceHandFixtures.Hand(HandPose.Peace);
        Assert.False(PeaceGestureClassifier.Classify(middle, 95).IsPeace);
        Assert.False(Is(peace, 95));
    }

    [Fact]
    public void LowHandScore_IsRejected_AndDependsOnSensitivity()
    {
        var weak = PeaceHandFixtures.Hand(HandPose.MiddleOnly, score: 0.5);
        Assert.False(Is(weak, 95));

        var medium = PeaceHandFixtures.Hand(HandPose.MiddleOnly, score: 0.7);
        Assert.True(Is(medium, 95));
        Assert.False(Is(medium, 10));
        Assert.Equal("Handscore zu niedrig.", MiddleFingerGestureClassifier.Classify(weak, 95).RejectReason);
    }

    [Fact]
    public void Thresholds_MatchSettingsMinHandScore_AndLoosenWithSensitivity()
    {
        for (var s = SettingsLimits.MiddleFingerSensitivityMin; s <= SettingsLimits.MiddleFingerSensitivityMax; s += 5)
        {
            var th = MiddleFingerGestureClassifier.Thresholds.For(s);
            Assert.Equal(new MiddleFingerSettings { SensitivityPercent = s }.MinHandScore, th.MinScore, 9);
        }

        var strict = MiddleFingerGestureClassifier.Thresholds.For(SettingsLimits.MiddleFingerSensitivityMin);
        var loose = MiddleFingerGestureClassifier.Thresholds.For(SettingsLimits.MiddleFingerSensitivityMax);
        Assert.True(loose.ExtensionFactor < strict.ExtensionFactor);
        Assert.True(loose.PipAngleDeg < strict.PipAngleDeg);
        Assert.True(loose.MinScore < strict.MinScore);

        // Außerhalb der Grenzen wird geklemmt.
        Assert.Equal(strict, MiddleFingerGestureClassifier.Thresholds.For(-5));
        Assert.Equal(loose, MiddleFingerGestureClassifier.Thresholds.For(500));
    }

    [Fact]
    public void Sensitivity_ControlsHowStraightTheMiddleFingerMustBe()
    {
        // Mittelfinger am PIP um 30° gebeugt (Winkel 150°): erst bei hoher Empfindlichkeit noch Mittelfinger.
        var hand = BendMiddleFinger(PeaceHandFixtures.Hand(HandPose.MiddleOnly), 30);
        Assert.True(Is(hand, 95));
        Assert.False(Is(hand, 10));
    }

    [Fact]
    public void IncompleteOrDegenerateHand_IsRejected()
    {
        var full = PeaceHandFixtures.Hand(HandPose.MiddleOnly);
        var shortHand = new HandLandmarks(full.Points.Take(10).ToList(), 0.95, full.Box);
        Assert.False(Is(shortHand));

        var collapsed = new HandLandmarks(Enumerable.Repeat(new HandPoint(0.5, 0.5, 0), 21).ToList(), 0.95, full.Box);
        Assert.False(Is(collapsed));
    }

    // Dreht DIP und TIP des Mittelfingers um das PIP-Gelenk (in der Bildebene; isotrop gerechnet).
    private static HandLandmarks BendMiddleFinger(HandLandmarks hand, double degrees)
    {
        const double aspect = 16.0 / 9;
        var points = hand.Points.ToArray();
        var pip = points[10];
        var rad = degrees * Math.PI / 180;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        foreach (var i in new[] { 11, 12 })
        {
            var dx = (points[i].X - pip.X) * aspect;
            var dy = points[i].Y - pip.Y;
            var rx = dx * cos - dy * sin;
            var ry = dx * sin + dy * cos;
            points[i] = new HandPoint(pip.X + rx / aspect, pip.Y + ry, points[i].Z);
        }
        return hand with { Points = points };
    }
}
