using Fotobox.Core;
using Fotobox.Core.Peace;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

public sealed class MoustacheTrackerTests
{
    private const double W = 1920, H = 1080;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    private static TrackedFace Face(int id, double x = 0.3) => new(id, PeaceHandFixtures.Face(x, 0.4, 0.12));

    private MoustacheTracker Create(Func<int, int>? random = null) => new(_time, random);

    private void Advance(int ms) => _time.Advance(TimeSpan.FromMilliseconds(ms));

    [Fact]
    public void NotActive_UntilStarted()
    {
        var tracker = Create();
        tracker.OnFaces([Face(1)], W, H);
        Assert.False(tracker.IsActive);
        Assert.Empty(tracker.Views);
        Assert.Null(tracker.ExportPlanInput());
    }

    [Fact]
    public void Everyone_GivesEveryFaceAMoustache_AlsoNewlyArrivingOnes()
    {
        var tracker = Create();
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.UntilPhotoOrGone, 15);
        tracker.OnFaces([Face(1, 0.3), Face(2, 0.7)], W, H);
        Assert.Equal([1, 2], tracker.Views.Select(v => v.FaceId));
        Assert.All(tracker.Views, v => Assert.Equal(MoustacheStyle.Twirl, v.Style));

        Advance(500);
        tracker.OnFaces([Face(1, 0.3), Face(2, 0.7), Face(3, 0.5)], W, H);
        Assert.Equal([1, 2, 3], tracker.Views.Select(v => v.FaceId));
    }

    [Fact]
    public void PeaceShower_OnlyTheOwnerGetsOne()
    {
        var tracker = Create();
        tracker.StartSession(MoustacheTargets.PeaceShower, MoustacheStyle.Walrus, 2, MoustacheVisibility.UntilPhotoOrGone, 15);
        tracker.OnFaces([Face(1, 0.3), Face(2, 0.7)], W, H);
        Assert.Equal(2, tracker.Views.Single().FaceId);

        tracker.StartSession(MoustacheTargets.PeaceShower, MoustacheStyle.Walrus, null, MoustacheVisibility.UntilPhotoOrGone, 15);
        tracker.OnFaces([Face(1, 0.3), Face(2, 0.7)], W, H);
        Assert.Empty(tracker.Views);
    }

    [Fact]
    public void RandomStyle_IsStablePerFace_NeverRandomItself_AndRerolledOnNewSession()
    {
        var calls = 0;
        var tracker = Create(n => calls++ % n);
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Random, null, MoustacheVisibility.UntilPhotoOrGone, 15);
        tracker.OnFaces([Face(1, 0.3), Face(2, 0.7)], W, H);
        var first = tracker.Views.ToDictionary(v => v.FaceId, v => v.Style);
        Assert.DoesNotContain(MoustacheStyle.Random, first.Values);
        Assert.NotEqual(first[1], first[2]);

        for (var i = 0; i < 5; i++)
        {
            Advance(100);
            tracker.OnFaces([Face(1, 0.3), Face(2, 0.7)], W, H);
        }
        Assert.Equal(first, tracker.Views.ToDictionary(v => v.FaceId, v => v.Style));
        Assert.Equal(2, calls);

        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Random, null, MoustacheVisibility.UntilPhotoOrGone, 15);
        tracker.OnFaces([Face(1, 0.3), Face(2, 0.7)], W, H);
        var second = tracker.Views.ToDictionary(v => v.FaceId, v => v.Style);
        Assert.NotEqual(first[1], second[1]); // Würfel liefert beim zweiten Mal andere Werte (Zähler läuft weiter)
    }

    [Fact]
    public void UntilPhotoOrGone_EndsAfter1500msWithoutFaces()
    {
        var tracker = Create();
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.UntilPhotoOrGone, 15);
        tracker.OnFaces([Face(1)], W, H);

        Advance(1400);
        tracker.Tick();
        Assert.True(tracker.IsActive);

        Advance(200);
        tracker.Tick();
        Assert.False(tracker.IsActive);
        Assert.Empty(tracker.Views);
    }

    [Fact]
    public void UntilPhotoOrGone_StaysWhileAFaceIsSeen()
    {
        var tracker = Create();
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.UntilPhotoOrGone, 15);
        for (var i = 0; i < 40; i++)
        {
            tracker.OnFaces([Face(1)], W, H);
            Advance(200);
            tracker.Tick();
        }
        Assert.True(tracker.IsActive);
    }

    [Fact]
    public void SessionWithoutAnyFace_EndsAfter1500ms()
    {
        var tracker = Create();
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.UntilPhotoOrGone, 15);
        Advance(1600);
        tracker.Tick();
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void OnPhotoCompleted_EndsUntilPhoto_ButNotFixedDuration()
    {
        var tracker = Create();
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.UntilPhotoOrGone, 15);
        tracker.OnFaces([Face(1)], W, H);
        tracker.OnPhotoCompleted();
        Assert.False(tracker.IsActive);

        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.FixedDuration, 15);
        tracker.OnFaces([Face(1)], W, H);
        tracker.OnPhotoCompleted();
        Assert.True(tracker.IsActive);
        Assert.Single(tracker.Views);
    }

    [Fact]
    public void FixedDuration_EndsAfterTheDuration_AndPausesWhileHeld()
    {
        var tracker = Create();
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.FixedDuration, 5);
        tracker.OnFaces([Face(1)], W, H);

        Advance(3000);
        tracker.Tick();
        tracker.SetHoldExpiry(true);
        for (var i = 0; i < 10; i++)
        {
            Advance(1000);
            tracker.OnFaces([Face(1)], W, H);
            tracker.Tick();
        }
        Assert.True(tracker.IsActive);

        tracker.SetHoldExpiry(false);
        Advance(1900);
        tracker.OnFaces([Face(1)], W, H);
        tracker.Tick();
        Assert.True(tracker.IsActive);

        Advance(200);
        tracker.Tick();
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void FixedDuration_SurvivesAbsenceOfFaces_ButViewsFade()
    {
        var tracker = Create();
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.FixedDuration, 10);
        tracker.OnFaces([Face(1)], W, H);
        Advance(5000);
        tracker.Tick();
        Assert.True(tracker.IsActive);
        Assert.Empty(tracker.Views);

        tracker.OnFaces([Face(1)], W, H);
        Assert.Single(tracker.Views);
    }

    [Fact]
    public void NewSession_RestartsTheDuration()
    {
        var tracker = Create();
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.FixedDuration, 5);
        Advance(4000);
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.FixedDuration, 5);
        Advance(4000);
        tracker.Tick();
        Assert.True(tracker.IsActive);
        Advance(1100);
        tracker.Tick();
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void Pose_IsSmoothed_TowardsTheNewObservation()
    {
        var tracker = Create();
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.FixedDuration, 15);
        var a = Face(1, 0.30);
        var b = Face(1, 0.34);
        tracker.OnFaces([a], W, H);
        var start = tracker.Views.Single().Pose.Center.X;
        Advance(100);
        tracker.OnFaces([b], W, H);
        var raw = MoustacheGeometry.FromFace(b.Face, W, H).Center.X;
        var smoothed = tracker.Views.Single().Pose.Center.X;
        Assert.Equal((start + raw) / 2, smoothed, 6);
    }

    [Fact]
    public void FacesSeenJustBeforeStart_GetAMoustacheImmediately()
    {
        var tracker = Create();
        tracker.OnFaces([Face(1), Face(2, 0.7)], W, H);
        Advance(300);
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Brush, null, MoustacheVisibility.UntilPhotoOrGone, 15);
        Assert.Equal(2, tracker.Views.Count);

        Advance(2000);
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Brush, null, MoustacheVisibility.UntilPhotoOrGone, 15);
        Assert.Empty(tracker.Views); // zu alt
    }

    [Fact]
    public void Export_ContainsTargetsOwnerAndTracks_AndIsNullAfterEnd()
    {
        var tracker = Create();
        tracker.StartSession(MoustacheTargets.PeaceShower, MoustacheStyle.Chevron, 2, MoustacheVisibility.UntilPhotoOrGone, 15);
        tracker.OnFaces([Face(1), Face(2, 0.7)], W, H);
        Advance(300);

        var export = tracker.ExportPlanInput();
        Assert.NotNull(export);
        Assert.Equal(MoustacheTargets.PeaceShower, export.Targets);
        Assert.Equal(2, export.OwnerFaceId);
        var track = Assert.Single(export.Tracks);
        Assert.Equal(2, track.FaceId);
        Assert.Equal(MoustacheStyle.Chevron, track.Style);
        Assert.Equal(TimeSpan.FromMilliseconds(300), track.Age);
        Assert.Equal(0.7 - 0.06, track.FaceBox.X, 6);

        tracker.EndSession();
        Assert.Null(tracker.ExportPlanInput());
    }
}
