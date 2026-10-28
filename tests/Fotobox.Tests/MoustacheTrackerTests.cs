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
        Assert.InRange(smoothed, start + 0.05 * (raw - start), start + 0.95 * (raw - start));
    }

    [Fact]
    public void Pose_AtRest_FiltersOutJitter()
    {
        var tracker = Create();
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.FixedDuration, 60);
        // Abwechselnd ±2 px (bei 1920 px Breite) um 0,3.
        var jitter = 2 / W;
        var center = MoustacheGeometry.FromFace(Face(1, 0.3).Face, W, H).Center.X;
        double maxDeviation = 0;
        for (var i = 0; i < 40; i++)
        {
            tracker.OnFaces([Face(1, 0.3 + (i % 2 == 0 ? jitter : -jitter))], W, H);
            if (i >= 10)
                maxDeviation = Math.Max(maxDeviation, Math.Abs(tracker.ExportPlanInput()!.Tracks.Single().Pose.Center.X - center));
            Advance(100);
        }

        // Das Rauschen (±2 px) wird auf unter 1 px gedämpft.
        Assert.True(maxDeviation * W < 1.0, $"Restabweichung {maxDeviation * W:0.00} px");
    }

    [Fact]
    public void ShownPose_AtRest_IsCalmerThanTheRawDetections_EvenWithPredictionAge()
    {
        var tracker = Create();
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.FixedDuration, 600);
        var random = new Random(1234);
        var truth = MoustacheGeometry.FromFace(Face(1, 0.3).Face, W, H).Center;
        double rawSquares = 0, shownSquares = 0, filteredSquares = 0;
        var n = 0;
        for (var i = 0; i < 120; i++)
        {
            // Gleichverteiltes Rauschen von ±4 px in x und y; das Bild ist beim Eintreffen der Auswertung 80 ms alt.
            var dx = (random.NextDouble() * 2 - 1) * 4 / W;
            var dy = (random.NextDouble() * 2 - 1) * 4 / H;
            var noisy = PeaceHandFixtures.Face(0.3 + dx, 0.4 + dy, 0.12);
            tracker.OnFaces([new TrackedFace(1, noisy)], W, H, _time.GetUtcNow() - TimeSpan.FromMilliseconds(80));
            if (i >= 20)
            {
                var raw = MoustacheGeometry.FromFace(noisy, W, H).Center;
                var shown = tracker.Views.Single().Pose.Center;
                var filtered = tracker.ExportPlanInput()!.Tracks.Single().Pose.Center;
                filteredSquares += Math.Pow((filtered.X - truth.X) * W, 2) + Math.Pow((filtered.Y - truth.Y) * H, 2);
                rawSquares += Math.Pow((raw.X - truth.X) * W, 2) + Math.Pow((raw.Y - truth.Y) * H, 2);
                shownSquares += Math.Pow((shown.X - truth.X) * W, 2) + Math.Pow((shown.Y - truth.Y) * H, 2);
                n++;
            }
            Advance(100);
        }

        var rawRms = Math.Sqrt(rawSquares / n);
        var shownRms = Math.Sqrt(shownSquares / n);
        Assert.True(shownRms < 0.6 * rawRms, $"Angezeigt {shownRms:0.00} px, gefiltert {Math.Sqrt(filteredSquares / n):0.00} px, roh {rawRms:0.00} px");
    }

    [Fact]
    public void Pose_DuringFastMotion_FollowsWithSmallLag()
    {
        var tracker = Create();
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.FixedDuration, 60);
        // 0,01 pro 100 ms = rund 0,8 Gesichtsbreiten pro Sekunde.
        double x = 0.2;
        for (var i = 0; i < 20; i++)
        {
            tracker.OnFaces([Face(1, x)], W, H);
            Advance(100);
            x += 0.01;
        }
        var observed = MoustacheGeometry.FromFace(Face(1, x - 0.01).Face, W, H).Center.X;
        var filtered = tracker.ExportPlanInput()!.Tracks.Single().Pose.Center.X;

        var lagFaceWidths = (observed - filtered) / 0.12;
        Assert.InRange(lagFaceWidths, 0, 0.35);
    }

    [Fact]
    public void Views_ExtrapolateByTheAgeOfTheObservation_ButExportDoesNot()
    {
        var tracker = Create();
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.FixedDuration, 60);
        double x = 0.2;
        for (var i = 0; i < 12; i++)
        {
            tracker.OnFaces([Face(1, x)], W, H, _time.GetUtcNow() - TimeSpan.FromMilliseconds(100));
            Advance(100);
            x += 0.01;
        }
        // Letzte Beobachtung trifft mit Bildzeit „vor 100 ms“ ein.
        tracker.OnFaces([Face(1, x)], W, H, _time.GetUtcNow() - TimeSpan.FromMilliseconds(100));

        var exported = tracker.ExportPlanInput()!.Tracks.Single().Pose.Center.X;
        var shown = tracker.Views.Single().Pose.Center.X;
        Assert.True(shown > exported, "Die Anzeige soll der Bewegung vorausgehen.");
        // Rund 100 ms bei 0,01 je 100 ms.
        Assert.InRange(shown - exported, 0.004, 0.012);
    }

    [Fact]
    public void Prediction_IsCappedAt150ms_AndNotExtendedWhileNoNewObservationArrives()
    {
        var tracker = Create();
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.FixedDuration, 60);
        double x = 0.2;
        for (var i = 0; i < 12; i++)
        {
            tracker.OnFaces([Face(1, x)], W, H);
            Advance(100);
            x += 0.005;
        }
        // 150 ms nach der letzten Beobachtung (bereits gedeckelt) und 250 ms später: gleiche Anzeige.
        tracker.OnFaces([Face(1, x)], W, H);
        Advance(150);
        var atCap = tracker.Views.Single().Pose.Center.X;
        Advance(250);
        var later = tracker.Views.Single().Pose.Center.X;

        Assert.Equal(atCap, later, 9);
    }

    [Fact]
    public void Prediction_IsLimitedToAThirdOfAFaceWidth()
    {
        var tracker = Create();
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.FixedDuration, 60);
        // Sehr schnell: 0,06 pro 100 ms (rund 5 Gesichtsbreiten pro Sekunde).
        double x = 0.1;
        for (var i = 0; i < 10; i++)
        {
            tracker.OnFaces([Face(1, x)], W, H);
            Advance(100);
            x += 0.06;
        }
        tracker.OnFaces([Face(1, x)], W, H);
        Advance(150);

        var exported = tracker.ExportPlanInput()!.Tracks.Single().Pose.Center.X;
        var shown = tracker.Views.Single().Pose.Center.X;
        Assert.True(shown - exported <= 0.3 * 0.12 + 1e-9, $"Vorhersage {(shown - exported) / 0.12:0.00} Gesichtsbreiten");
        Assert.True(shown - exported >= 0.25 * 0.12);
    }

    [Fact]
    public void Prediction_DoesNotExtrapolateAngleOrWidth()
    {
        var tracker = Create();
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.FixedDuration, 60);
        for (var i = 0; i < 6; i++)
        {
            tracker.OnFaces([Face(1, 0.4 + 0.01 * i)], W, H);
            Advance(100);
        }
        tracker.OnFaces([Face(1, 0.46)], W, H);
        Advance(100);

        var exported = tracker.ExportPlanInput()!.Tracks.Single().Pose;
        var shown = tracker.Views.Single().Pose;
        Assert.Equal(exported.AngleRad, shown.AngleRad, 12);
        Assert.Equal(exported.WidthPx, shown.WidthPx, 12);
    }

    [Fact]
    public void NewFaceId_GetsTheRawPoseImmediately()
    {
        var tracker = Create();
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.FixedDuration, 60);
        tracker.OnFaces([Face(1, 0.3)], W, H);
        Advance(100);
        tracker.OnFaces([Face(1, 0.3), Face(2, 0.7)], W, H);

        var raw = MoustacheGeometry.FromFace(Face(2, 0.7).Face, W, H);
        Assert.Equal(raw, tracker.Views.Single(v => v.FaceId == 2).Pose);
    }

    [Fact]
    public void LongGapBetweenObservations_RestartsTheSmoothing()
    {
        var tracker = Create();
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.FixedDuration, 60);
        tracker.OnFaces([Face(1, 0.3)], W, H);
        Advance(900);
        tracker.OnFaces([Face(1, 0.5)], W, H);

        var raw = MoustacheGeometry.FromFace(Face(1, 0.5).Face, W, H);
        Assert.Equal(raw.Center.X, tracker.ExportPlanInput()!.Tracks.Single().Pose.Center.X, 9);
    }

    [Fact]
    public void Angle_WrapsAroundPi_WithoutJumping()
    {
        var tracker = Create();
        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.FixedDuration, 60);
        TrackedFace Tilted(double degrees)
        {
            var f = PeaceHandFixtures.Face(0.5, 0.4, 0.12);
            var rad = degrees * Math.PI / 180;
            var dx = Math.Cos(rad) * 0.05;
            var dy = Math.Sin(rad) * 0.05 * W / H;
            return new TrackedFace(1, f with
            {
                RightEye = new NormPoint(0.5 - dx, 0.4 - dy), LeftEye = new NormPoint(0.5 + dx, 0.4 + dy),
                MouthRight = new NormPoint(0.5 - dx / 2, 0.45 - dy / 2), MouthLeft = new NormPoint(0.5 + dx / 2, 0.45 + dy / 2),
            });
        }

        tracker.OnFaces([Tilted(175)], W, H);
        Advance(100);
        tracker.OnFaces([Tilted(-175)], W, H); // 10° weiter, über ±180° hinweg

        var angle = tracker.Views.Single().Pose.AngleRad * 180 / Math.PI;
        Assert.True(Math.Abs(angle) > 170, $"Winkel {angle:0}° sprang durch die Mitte");
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
