using Fotobox.Core;
using Fotobox.Core.Peace;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Verschwinden der Bärte: Kurve <see cref="MoustacheVanish"/> und <see cref="MoustacheTracker.Dismiss"/>.</summary>
public sealed class MoustacheDismissTests
{
    private const double W = 1920, H = 1080;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    private static TrackedFace Face(int id, double x = 0.3) => new(id, PeaceHandFixtures.Face(x, 0.4, 0.12));

    private void Advance(int ms) => _time.Advance(TimeSpan.FromMilliseconds(ms));

    private MoustacheTracker Started(MoustacheTargets targets = MoustacheTargets.Everyone)
    {
        var tracker = new MoustacheTracker(_time);
        tracker.StartSession(targets, MoustacheStyle.Twirl, null, MoustacheVisibility.UntilPhotoOrGone, 15);
        tracker.OnFaces([Face(1, 0.3), Face(2, 0.7)], W, H);
        return tracker;
    }

    // ---- Kurve

    [Fact]
    public void Vanish_StartsAtOne_EndsAtZero_AndClampsOutside()
    {
        Assert.Equal(1, MoustacheVanish.Scale(0), 9);
        Assert.Equal(1, MoustacheVanish.Scale(-1), 9);
        Assert.Equal(0, MoustacheVanish.Scale(MoustacheVanish.DurationSeconds), 9);
        Assert.Equal(0, MoustacheVanish.Scale(10), 9);
        Assert.Equal(0.3, MoustacheVanish.DurationSeconds);
    }

    [Fact]
    public void Vanish_OvershootsToAboutOnePointZeroEight_InTheFirstQuarter()
    {
        var peakAt = MoustacheVanish.PeakAt * MoustacheVanish.DurationSeconds;
        Assert.Equal(1.08, MoustacheVanish.Scale(peakAt), 6);

        var max = 0.0;
        var argMax = 0.0;
        for (var i = 0; i <= 300; i++)
        {
            var t = i * MoustacheVanish.DurationSeconds / 300;
            var s = MoustacheVanish.Scale(t);
            if (s > max)
            {
                max = s;
                argMax = t;
            }
        }
        Assert.InRange(max, 1.07, 1.09);
        Assert.InRange(argMax, 0, 0.3 * 0.3);
    }

    [Fact]
    public void Vanish_AfterThePeak_ShrinksMonotonically_WithoutJumps()
    {
        var peakAt = MoustacheVanish.PeakAt * MoustacheVanish.DurationSeconds;
        var previous = MoustacheVanish.Scale(peakAt);
        for (var i = 1; i <= 100; i++)
        {
            var t = peakAt + i * (MoustacheVanish.DurationSeconds - peakAt) / 100;
            var s = MoustacheVanish.Scale(t);
            Assert.InRange(s, 0, previous + 1e-12);
            Assert.True(previous - s < 0.1, $"Sprung bei t={t}");
            previous = s;
        }

        // Ease-in: Die Hälfte der Restzeit schrumpft weniger als die Hälfte der Skalierung.
        var mid = peakAt + (MoustacheVanish.DurationSeconds - peakAt) / 2;
        Assert.True(MoustacheVanish.Scale(mid) > MoustacheVanish.Peak / 2);
    }

    [Fact]
    public void Vanish_IsRunning_UntilTheDuration()
    {
        Assert.True(MoustacheVanish.IsRunning(0));
        Assert.True(MoustacheVanish.IsRunning(0.29));
        Assert.False(MoustacheVanish.IsRunning(0.3));
    }

    // ---- Tracker

    [Fact]
    public void Dismiss_EndsTheSessionImmediately_AndFreezesTheViews()
    {
        var tracker = Started();
        var live = tracker.Views;
        Assert.Equal(2, live.Count);
        Assert.All(live, v => Assert.False(v.Vanishing));

        Assert.True(tracker.Dismiss());
        Assert.False(tracker.IsActive);
        Assert.Null(tracker.ExportPlanInput());
        Assert.True(tracker.IsVanishing);

        var vanishing = tracker.Views;
        Assert.Equal([1, 2], vanishing.Select(v => v.FaceId));
        Assert.All(vanishing, v =>
        {
            Assert.True(v.Vanishing);
            Assert.True(v.Wobble.IsRest);
        });
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(live[i].Pose, vanishing[i].Pose);
            Assert.Equal(live[i].Style, vanishing[i].Style);
            Assert.Equal(live[i].Color, vanishing[i].Color);
        }
    }

    [Fact]
    public void Dismiss_FreezesPoses_EvenWhenFacesMoveOrTimePasses()
    {
        var tracker = Started();
        Assert.True(tracker.Dismiss());
        var first = tracker.Views;

        Advance(100);
        tracker.OnFaces([Face(1, 0.5), Face(2, 0.9)], W, H); // keine Sitzung: ignoriert
        tracker.Tick();
        var later = tracker.Views;
        Assert.Equal(first.Select(v => v.Pose), later.Select(v => v.Pose));
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void Dismiss_ViewsStayForTheDuration_ThenAreEmpty()
    {
        var tracker = Started();
        tracker.Dismiss();

        Advance((int)(MoustacheVanish.DurationSeconds * 1000) - 10);
        Assert.True(tracker.IsVanishing);
        Assert.NotEmpty(tracker.Views);

        Advance(10);
        Assert.False(tracker.IsVanishing);
        Assert.Empty(tracker.Views);
    }

    [Fact]
    public void Dismiss_WithoutSessionOrWithoutVisibleMoustache_ReturnsFalse()
    {
        var tracker = new MoustacheTracker(_time);
        Assert.False(tracker.Dismiss());
        Assert.False(tracker.IsVanishing);

        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.UntilPhotoOrGone, 15);
        Assert.False(tracker.Dismiss()); // Sitzung ohne sichtbaren Bart
        Assert.False(tracker.IsActive);
        Assert.False(tracker.IsVanishing);
        Assert.Empty(tracker.Views);
    }

    [Fact]
    public void Dismiss_Twice_DoesNotRestartTheVanish()
    {
        var tracker = Started();
        Assert.True(tracker.Dismiss());
        Advance(200);
        Assert.False(tracker.Dismiss()); // Sitzung ist schon beendet
        Advance(100);
        Assert.False(tracker.IsVanishing); // 300 ms nach dem ersten Dismiss
        Assert.Empty(tracker.Views);
    }

    [Fact]
    public void Dismiss_DropsMoustachesThatAreNoLongerVisible()
    {
        var tracker = Started();
        Advance(400);
        tracker.OnFaces([Face(1, 0.3)], W, H); // Gesicht 2 nicht mehr gesehen
        Advance(500); // Gesicht 2: 900 ms > ViewTimeout, Gesicht 1: 500 ms
        Assert.Equal([1], tracker.Views.Select(v => v.FaceId));

        Assert.True(tracker.Dismiss());
        Assert.Equal([1], tracker.Views.Select(v => v.FaceId));
    }

    [Fact]
    public void StartSession_ClearsAVanishingSnapshot()
    {
        var tracker = Started();
        tracker.Dismiss();
        Assert.True(tracker.IsVanishing);

        tracker.StartSession(MoustacheTargets.Everyone, MoustacheStyle.Twirl, null, MoustacheVisibility.UntilPhotoOrGone, 15);
        Assert.False(tracker.IsVanishing);
        Assert.True(tracker.IsActive);
        Assert.All(tracker.Views, v => Assert.False(v.Vanishing));
    }

    [Fact]
    public void EndSession_AfterDismiss_KeepsTheVanishRunning()
    {
        var tracker = Started();
        tracker.Dismiss();
        tracker.EndSession();
        Assert.True(tracker.IsVanishing);
        Assert.NotEmpty(tracker.Views);
    }

    [Fact]
    public void MoustacheView_Vanishing_DefaultsToFalse()
    {
        var view = new MoustacheView(1, MoustacheStyle.Twirl, new MoustachePose(new NormPoint(0.5, 0.5), 100, 0));
        Assert.False(view.Vanishing);
        Assert.True((view with { Vanishing = true }).Vanishing);
    }
}
