using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

public sealed class HandTrackerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void MovingHand_KeepsItsId()
    {
        var tracker = new HandTracker(_time);
        var first = tracker.Update([PeaceHandFixtures.Hand(HandPose.Peace, cx: 0.50)]).Single();
        _time.Advance(TimeSpan.FromMilliseconds(200));
        var second = tracker.Update([PeaceHandFixtures.Hand(HandPose.Peace, cx: 0.53, cy: 0.71)]).Single();
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public void TwoHands_GetDistinctStableIds_AndOrderDoesNotMatter()
    {
        var tracker = new HandTracker(_time);
        var a = PeaceHandFixtures.Hand(HandPose.Peace, cx: 0.2);
        var b = PeaceHandFixtures.Hand(HandPose.Fist, cx: 0.8);
        var first = tracker.Update([a, b]);
        Assert.NotEqual(first[0].Id, first[1].Id);

        _time.Advance(TimeSpan.FromMilliseconds(100));
        var second = tracker.Update([b, a]);
        Assert.Equal(first[1].Id, second[0].Id);
        Assert.Equal(first[0].Id, second[1].Id);
    }

    [Fact]
    public void Hand_IsLostAfter400ms_AndGetsNewId()
    {
        var tracker = new HandTracker(_time);
        var first = tracker.Update([PeaceHandFixtures.Hand(HandPose.Peace)]).Single();

        _time.Advance(TimeSpan.FromMilliseconds(300));
        tracker.Update([]);
        _time.Advance(TimeSpan.FromMilliseconds(90));
        Assert.Equal(first.Id, tracker.Update([PeaceHandFixtures.Hand(HandPose.Peace)]).Single().Id);

        _time.Advance(TimeSpan.FromMilliseconds(450));
        tracker.Update([]);
        var again = tracker.Update([PeaceHandFixtures.Hand(HandPose.Peace)]).Single();
        Assert.NotEqual(first.Id, again.Id);
    }

    [Fact]
    public void FarJump_IsANewHand()
    {
        var tracker = new HandTracker(_time);
        var first = tracker.Update([PeaceHandFixtures.Hand(HandPose.Peace, cx: 0.2)]).Single();
        _time.Advance(TimeSpan.FromMilliseconds(100));
        var second = tracker.Update([PeaceHandFixtures.Hand(HandPose.Peace, cx: 0.8)]).Single();
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void HandWithoutAll21Points_IsSkipped()
    {
        var tracker = new HandTracker(_time);
        var full = PeaceHandFixtures.Hand(HandPose.Peace);
        var broken = new HandLandmarks(full.Points.Take(5).ToList(), 0.9, full.Box);
        Assert.Empty(tracker.Update([broken]));
    }
}

public sealed class FaceTrackerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void JitteringFace_KeepsItsId()
    {
        var tracker = new FaceTracker(_time);
        var first = tracker.Update([PeaceHandFixtures.Face(0.5, 0.4)]).Single();
        _time.Advance(TimeSpan.FromMilliseconds(100));
        var second = tracker.Update([PeaceHandFixtures.Face(0.51, 0.41)]).Single();
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public void ResultKeepsInputOrder_AndIdsStayWhenOrderSwaps()
    {
        var tracker = new FaceTracker(_time);
        var left = PeaceHandFixtures.Face(0.25, 0.4);
        var right = PeaceHandFixtures.Face(0.75, 0.4);
        var first = tracker.Update([left, right]);
        Assert.Same(left, first[0].Face);
        Assert.NotEqual(first[0].Id, first[1].Id);

        _time.Advance(TimeSpan.FromMilliseconds(100));
        var second = tracker.Update([right, left]);
        Assert.Equal(first[1].Id, second[0].Id);
        Assert.Equal(first[0].Id, second[1].Id);
    }

    [Fact]
    public void NewFace_GetsNewId_AndLostFaceIsForgottenAfter700ms()
    {
        var tracker = new FaceTracker(_time);
        var first = tracker.Update([PeaceHandFixtures.Face(0.3, 0.4)]).Single();

        _time.Advance(TimeSpan.FromMilliseconds(500));
        var both = tracker.Update([PeaceHandFixtures.Face(0.3, 0.4), PeaceHandFixtures.Face(0.7, 0.4)]);
        Assert.Equal(first.Id, both[0].Id);
        Assert.NotEqual(first.Id, both[1].Id);

        _time.Advance(TimeSpan.FromMilliseconds(800));
        tracker.Update([]);
        var again = tracker.Update([PeaceHandFixtures.Face(0.3, 0.4)]).Single();
        Assert.NotEqual(first.Id, again.Id);
    }

    [Fact]
    public void ShortAbsence_KeepsTheId()
    {
        var tracker = new FaceTracker(_time);
        var first = tracker.Update([PeaceHandFixtures.Face()]).Single();
        _time.Advance(TimeSpan.FromMilliseconds(600));
        tracker.Update([]);
        _time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(first.Id, tracker.Update([PeaceHandFixtures.Face()]).Single().Id);
    }

    [Fact]
    public void CloseCenters_MatchEvenWithLowOverlap()
    {
        var tracker = new FaceTracker(_time);
        var big = Face(new NormRect(0.425, 0.3, 0.10, 0.10 * 16 / 9));
        var first = tracker.Update([big]).Single();
        _time.Advance(TimeSpan.FromMilliseconds(100));
        // Box deutlich kleiner (IoU < 0,3), aber nahezu gleiche Mitte.
        var nearlySame = Face(new NormRect(big.Box.CenterX - 0.02, big.Box.CenterY - 0.02, 0.04, 0.04 * 16 / 9));
        Assert.True(first.Face.Box.IoU(nearlySame.Box) < FaceTracker.MinIoU);
        Assert.Equal(first.Id, tracker.Update([nearlySame]).Single().Id);
    }

    private static FaceLandmarks FaceAt(double centerX, double width = 0.1, double centerY = 0.4) =>
        Face(new NormRect(centerX - width / 2, centerY - width * 16 / 9 / 2, width, width * 16 / 9));

    [Fact]
    public void LostFace_ReappearingWithinOneFaceWidth_KeepsItsId()
    {
        var tracker = new FaceTracker(_time);
        var first = tracker.Update([FaceAt(0.5)]).Single();

        _time.Advance(TimeSpan.FromMilliseconds(100));
        tracker.Update([]); // Aussetzer: die Spur ist jetzt „verloren“
        _time.Advance(TimeSpan.FromMilliseconds(100));
        // 0,8 Gesichtsbreiten daneben: mehr als die 0,5 der aktiven Zuordnung, weniger als 1,0.
        var again = tracker.Update([FaceAt(0.58)]).Single();

        Assert.Equal(first.Id, again.Id);
    }

    [Fact]
    public void LostFace_ReappearingBeyondOneFaceWidth_GetsANewId()
    {
        var tracker = new FaceTracker(_time);
        var first = tracker.Update([FaceAt(0.5)]).Single();

        _time.Advance(TimeSpan.FromMilliseconds(100));
        tracker.Update([]);
        _time.Advance(TimeSpan.FromMilliseconds(100));
        var other = tracker.Update([FaceAt(0.62)]).Single();

        Assert.NotEqual(first.Id, other.Id);
    }

    [Fact]
    public void LostFace_IsPredictedByItsLastVelocity()
    {
        var tracker = new FaceTracker(_time);
        var first = tracker.Update([FaceAt(0.30)]).Single();
        foreach (var x in new[] { 0.34, 0.38 })
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            Assert.Equal(first.Id, tracker.Update([FaceAt(x)]).Single().Id);
        }

        // 400 ms verloren: ohne Vorhersage läge das Gesicht 1,4 Gesichtsbreiten von der letzten Lage entfernt.
        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            tracker.Update([]);
        }
        _time.Advance(TimeSpan.FromMilliseconds(100));
        var again = tracker.Update([FaceAt(0.52)]).Single();

        Assert.Equal(first.Id, again.Id);
    }

    [Fact]
    public void ActiveTracks_AreMatchedBeforeLostOnes()
    {
        var tracker = new FaceTracker(_time);
        var both = tracker.Update([FaceAt(0.50), FaceAt(0.58)]);
        Assert.NotEqual(both[0].Id, both[1].Id);

        _time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(both[0].Id, tracker.Update([FaceAt(0.50)]).Single().Id); // die zweite Spur ist nun verloren

        _time.Advance(TimeSpan.FromMilliseconds(100));
        // Das Gesicht überdeckt die verlorene Spur stärker als die aktive; die aktive Spur hat trotzdem Vorrang.
        Assert.Equal(both[0].Id, tracker.Update([FaceAt(0.55)]).Single().Id);
    }

    [Fact]
    public void LostFace_IsStillForgottenAfterTheTimeout_EvenWhenNearby()
    {
        var tracker = new FaceTracker(_time);
        var first = tracker.Update([FaceAt(0.5)]).Single();

        _time.Advance(TimeSpan.FromMilliseconds(100));
        tracker.Update([]);
        _time.Advance(FaceTracker.LostTimeout);
        Assert.NotEqual(first.Id, tracker.Update([FaceAt(0.5)]).Single().Id);
    }

    private static FaceLandmarks Face(NormRect box) => new(box, 0.9,
        new NormPoint(box.X + box.Width * 0.3, box.Y + box.Height * 0.4), new NormPoint(box.X + box.Width * 0.7, box.Y + box.Height * 0.4),
        new NormPoint(box.CenterX, box.Y + box.Height * 0.6),
        new NormPoint(box.X + box.Width * 0.35, box.Y + box.Height * 0.8), new NormPoint(box.X + box.Width * 0.65, box.Y + box.Height * 0.8));
}

public sealed class HandFaceMatcherTests
{
    [Fact]
    public void PicksTheNearestFace_WithinLimit()
    {
        var near = new TrackedFace(1, PeaceHandFixtures.Face(0.5, 0.3, 0.1));
        var other = new TrackedFace(2, PeaceHandFixtures.Face(0.15, 0.3, 0.1));
        var hand = PeaceHandFixtures.Hand(HandPose.Peace, cx: 0.55, cy: 0.6);
        Assert.Equal(1, HandFaceMatcher.Match(hand, [other, near])!.Id);
    }

    [Fact]
    public void TooFar_ReturnsNull()
    {
        var face = new TrackedFace(1, PeaceHandFixtures.Face(0.05, 0.05, 0.03));
        var hand = PeaceHandFixtures.Hand(HandPose.Peace, cx: 0.9, cy: 0.9);
        Assert.Null(HandFaceMatcher.Match(hand, [face]));
        Assert.Null(HandFaceMatcher.Match(hand, []));
    }
}
