using Fotobox.Core.Peace;
using Xunit;

namespace Fotobox.Tests;

public sealed class PeaceGestureClassifierTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(95)]
    public void Peace_IsRecognised_AtAllSensitivities(int sensitivity)
    {
        var verdict = PeaceGestureClassifier.Classify(PeaceHandFixtures.Hand(HandPose.Peace), sensitivity);
        Assert.True(verdict.IsPeace, verdict.RejectReason);
        Assert.Null(verdict.RejectReason);
    }

    [Theory]
    [InlineData(HandPose.Fist)]
    [InlineData(HandPose.Open)]
    [InlineData(HandPose.IndexOnly)]
    [InlineData(HandPose.ThreeFingers)]
    public void OtherGestures_AreRejected(HandPose pose)
    {
        foreach (var sensitivity in new[] { 10, 50, 95 })
        {
            var verdict = PeaceGestureClassifier.Classify(PeaceHandFixtures.Hand(pose), sensitivity);
            Assert.False(verdict.IsPeace, $"{pose}@{sensitivity}");
            Assert.False(string.IsNullOrEmpty(verdict.RejectReason));
        }
    }

    [Fact]
    public void Peace_WithThumbSpreadOut_IsStillPeace()
    {
        Assert.True(PeaceGestureClassifier.Classify(PeaceHandFixtures.Hand(HandPose.Peace, thumbOut: true), 50).IsPeace);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(-90)]
    [InlineData(37)]
    public void Peace_IsRotationInvariant(double rotation)
    {
        Assert.True(PeaceGestureClassifier.Classify(PeaceHandFixtures.Hand(HandPose.Peace, rotationDeg: rotation), 50).IsPeace);
        Assert.False(PeaceGestureClassifier.Classify(PeaceHandFixtures.Hand(HandPose.Fist, rotationDeg: rotation), 50).IsPeace);
    }

    [Fact]
    public void Peace_IsMirrorInvariant()
    {
        Assert.True(PeaceGestureClassifier.Classify(PeaceHandFixtures.Hand(HandPose.Peace, mirror: true), 50).IsPeace);
        Assert.True(PeaceGestureClassifier.Classify(PeaceHandFixtures.Hand(HandPose.Peace, mirror: true, rotationDeg: 90), 50).IsPeace);
    }

    [Fact]
    public void Aspect_IsTakenIntoAccount()
    {
        var hand = PeaceHandFixtures.Hand(HandPose.Peace, aspect: 4.0 / 3, rotationDeg: 90);
        Assert.True(PeaceGestureClassifier.Classify(hand, 50, 4.0 / 3).IsPeace);
    }

    [Fact]
    public void Sensitivity_ControlsHowStraightFingersMustBe()
    {
        // Zeigefinger am PIP um 30° gebeugt (Winkel 150°): erst bei hoher Empfindlichkeit noch Peace.
        var hand = PeaceHandFixtures.Hand(HandPose.Peace, indexBendDeg: 30);
        Assert.True(PeaceGestureClassifier.Classify(hand, 95).IsPeace);
        Assert.False(PeaceGestureClassifier.Classify(hand, 10).IsPeace);
    }

    [Fact]
    public void LowHandScore_IsRejected_AndDependsOnSensitivity()
    {
        var weak = PeaceHandFixtures.Hand(HandPose.Peace, score: 0.5);
        Assert.False(PeaceGestureClassifier.Classify(weak, 95).IsPeace);

        var medium = PeaceHandFixtures.Hand(HandPose.Peace, score: 0.7);
        Assert.True(PeaceGestureClassifier.Classify(medium, 95).IsPeace);
        Assert.False(PeaceGestureClassifier.Classify(medium, 10).IsPeace);
    }

    [Fact]
    public void IncompleteOrDegenerateHand_IsRejected()
    {
        var full = PeaceHandFixtures.Hand(HandPose.Peace);
        var shortHand = new HandLandmarks(full.Points.Take(10).ToList(), 0.95, full.Box);
        Assert.False(PeaceGestureClassifier.Classify(shortHand, 50).IsPeace);

        var collapsed = new HandLandmarks(Enumerable.Repeat(new HandPoint(0.5, 0.5, 0), 21).ToList(), 0.95, full.Box);
        Assert.False(PeaceGestureClassifier.Classify(collapsed, 50).IsPeace);
    }
}
