using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Views.Booth;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Ansichten der Zielerfassung: <see cref="TargetOverlay"/> (Zielboxen) und <see cref="FunText"/> (Text).</summary>
[Collection(WpfCollection.Name)]
public class FunViewsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static FunTarget Target(int id, double x, double y = 0.3, double w = 0.2, double h = 0.4, string? label = null,
        bool locking = false, DateTimeOffset? lockStart = null) =>
        new(id, new NormRect(x, y, w, h), label ?? $"TARGET {id}", locking, lockStart ?? default);

    private static FunSnapshot Snapshot(params FunTarget[] targets) =>
        new(targets, FunPhase.Watching, false, "", targets.Length);

    private static TargetOverlay Overlay(double width = 1920, double height = 1080, double imageW = 1920, double imageH = 1080)
    {
        var overlay = new TargetOverlay { ImageWidth = imageW, ImageHeight = imageH };
        overlay.Measure(new Size(width, height));
        overlay.Arrange(new Rect(0, 0, width, height));
        overlay.UpdateLayout();
        return overlay;
    }

    private static void Relayout(FrameworkElement element, double width = 1920, double height = 1080)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    // ---- TargetOverlay

    [StaFact]
    public void TargetOverlay_CreatesOneElementPerTarget_AtTheMappedPosition()
    {
        var overlay = Overlay();

        overlay.Snapshot = Snapshot(Target(1, 0.1), Target(2, 0.6));

        Assert.Equal(2, overlay.Visuals.Count);
        var first = overlay.Visuals[1];
        Assert.Equal(192, first.Target.X, 3);
        Assert.Equal(324, first.Target.Y, 3);
        Assert.Equal(384, first.Target.Width, 3);
        Assert.Equal(432, first.Target.Height, 3);
        Assert.Equal(192, Canvas.GetLeft(first), 3); // erstes Platzieren sofort, ohne Weg von 0,0
        Assert.Equal(384, first.Width, 3);
        Assert.Equal(1152, overlay.Visuals[2].Target.X, 3);
    }

    [StaFact]
    public void TargetOverlay_ReusesElements_AndRemovesVanishedTargets()
    {
        var overlay = Overlay();
        overlay.Snapshot = Snapshot(Target(1, 0.1), Target(2, 0.6));
        var first = overlay.Visuals[1];

        overlay.Snapshot = Snapshot(Target(1, 0.12));

        Assert.Single(overlay.Visuals);
        Assert.Same(first, overlay.Visuals[1]);
        Assert.False(overlay.Visuals.ContainsKey(2));
        Assert.Equal(0.12 * 1920, first.Target.X, 3); // Ziel ist das neue Rechteck, die Bewegung läuft weich dorthin

        overlay.Snapshot = FunSnapshot.Empty;
        Assert.Empty(overlay.Visuals);
    }

    [StaFact]
    public void TargetOverlay_ReappearingId_GetsAFreshElement()
    {
        var overlay = Overlay();
        overlay.Snapshot = Snapshot(Target(1, 0.1));
        var old = overlay.Visuals[1];

        overlay.Snapshot = FunSnapshot.Empty;
        overlay.Snapshot = Snapshot(Target(1, 0.1));

        Assert.NotSame(old, overlay.Visuals[1]);
    }

    [StaFact]
    public void TargetOverlay_LabelFoldsInside_WhenThereIsNoRoomAbove()
    {
        var overlay = Overlay();

        overlay.Snapshot = Snapshot(Target(1, 0.1, y: 0.01), Target(2, 0.6, y: 0.4));

        Assert.True(overlay.Visuals[1].LabelInside);
        Assert.False(overlay.Visuals[2].LabelInside);
        Assert.Equal("TARGET 1", overlay.Visuals[1].DisplayedLabel);
    }

    [StaFact]
    public void TargetOverlay_Mirror_SwapsThePositions()
    {
        var overlay = Overlay();
        overlay.Snapshot = Snapshot(Target(1, 0.1, w: 0.1));
        Assert.Equal(192, overlay.Visuals[1].Target.X, 3);

        overlay.Mirror = true;

        Assert.Equal((1 - 0.2) * 1920, overlay.Visuals[1].Target.X, 3);
        Assert.Equal((1 - 0.2) * 1920, Canvas.GetLeft(overlay.Visuals[1]), 3); // ohne Weichzeichnen sofort
    }

    [StaFact]
    public void TargetOverlay_FollowsAUniformToFillCrop()
    {
        // 16:9-Bild in quadratiger Ansicht: füllend, links/rechts beschnitten.
        var overlay = Overlay(1000, 1000);
        overlay.Snapshot = Snapshot(Target(1, 0.25, y: 0, w: 0.5, h: 0.5));

        var drawWidth = 1000 * (16.0 / 9);
        var rect = overlay.Visuals[1].Target;
        Assert.Equal(drawWidth * 0.5, rect.Width, 1);
        Assert.Equal((1000 - drawWidth) / 2 + drawWidth * 0.25, rect.X, 1);

        Assert.Equal(0.5625, overlay.VisibleRegion.Width, 2);
        Assert.Equal(1.0, overlay.VisibleRegion.Height, 3);
    }

    [StaFact]
    public void TargetOverlay_Uniform_ShowsTheWholeImage()
    {
        var overlay = new TargetOverlay { Fit = OverlayFit.Uniform, ImageWidth = 1600, ImageHeight = 900 };
        Relayout(overlay, 1000, 1000);

        Assert.Equal(NormRect.Full, overlay.VisibleRegion);
    }

    [StaFact]
    public void TargetOverlay_ReportsTheVisibleRegionAgain_WhenTheImageSizeBecomesKnown()
    {
        var overlay = new TargetOverlay { Fit = OverlayFit.UniformToFill };
        Relayout(overlay, 1000, 1000);
        Assert.Equal(0.5625, overlay.VisibleRegion.Width, 2); // unbekannte Größe: 16:9

        overlay.ImageWidth = 1000;
        overlay.ImageHeight = 1000;

        Assert.Equal(NormRect.Full, overlay.VisibleRegion);
    }

    [StaFact]
    public void TargetOverlay_LockOn_StartsOncePerLockStart_AndShowsLocking()
    {
        var overlay = Overlay();
        var start = T0;

        overlay.Snapshot = Snapshot(Target(1, 0.1, locking: true, lockStart: start));
        var visual = overlay.Visuals[1];
        Assert.Equal(start.UtcTicks, visual.LockKey);
        Assert.True(visual.IsLocking);
        Assert.Equal("LOCKING…", visual.DisplayedLabel);

        // Gleicher Beginn: kein Neustart (Schlüssel bleibt, Zustand bleibt).
        overlay.Snapshot = Snapshot(Target(1, 0.11, locking: true, lockStart: start));
        Assert.Equal(start.UtcTicks, visual.LockKey);
        Assert.True(visual.IsLocking);

        // Einrasten vorbei: Beschriftung zeigt TARGET n.
        overlay.Snapshot = Snapshot(Target(1, 0.11, locking: false, lockStart: start));
        Assert.False(visual.IsLocking);
        Assert.Equal("TARGET 1", visual.DisplayedLabel);

        // Neues Einrasten (Zuwachs-Fall) beginnt später: neuer Schlüssel.
        var later = start.AddSeconds(10);
        overlay.Snapshot = Snapshot(Target(1, 0.11, locking: true, lockStart: later));
        Assert.Equal(later.UtcTicks, visual.LockKey);
        Assert.True(visual.IsLocking);
    }

    [StaTheory]
    [InlineData(LockOnAnimation.ShrinkPulse)]
    [InlineData(LockOnAnimation.Pulse)]
    [InlineData(LockOnAnimation.ScanPulse)]
    public void TargetOverlay_LockOn_RunsEachAnimationWithoutErrors(LockOnAnimation animation)
    {
        var overlay = Overlay();
        overlay.Animation = animation;

        overlay.Snapshot = Snapshot(Target(1, 0.1, locking: true, lockStart: T0));
        overlay.Snapshot = Snapshot(Target(1, 0.3, locking: true, lockStart: T0)); // bewegt sich währenddessen
        overlay.Snapshot = Snapshot(Target(1, 0.3, locking: false, lockStart: T0));

        Assert.Single(overlay.Visuals);
        overlay.Visuals[1].StopAnimations();
        Assert.Equal(0, overlay.Visuals[1].Pulse);
    }

    // ---- Einrast-Dauer und -Intensität

    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    [StaTheory]
    [InlineData(LockOnAnimation.ShrinkPulse, 500)]
    [InlineData(LockOnAnimation.ShrinkPulse, 1200)]
    [InlineData(LockOnAnimation.ShrinkPulse, 5000)]
    [InlineData(LockOnAnimation.Pulse, 500)]
    [InlineData(LockOnAnimation.Pulse, 1200)]
    [InlineData(LockOnAnimation.Pulse, 5000)]
    [InlineData(LockOnAnimation.ScanPulse, 500)]
    [InlineData(LockOnAnimation.ScanPulse, 1200)]
    [InlineData(LockOnAnimation.ScanPulse, 5000)]
    public void LockOnPlan_EndsExactlyAtTheConfiguredDuration_ForEveryAnimationAndIntensity(LockOnAnimation animation, int milliseconds)
    {
        foreach (var intensity in new[] { 0, 50, 100 })
        {
            var plan = TargetVisual.CreatePlan(animation, TimeSpan.FromMilliseconds(milliseconds), intensity);
            Assert.InRange(plan.End.TotalMilliseconds, milliseconds - 1.0, milliseconds + 1.0);
        }
    }

    [StaFact]
    public void LockOnPlan_At1200msAnd50Percent_IsIdenticalToTheFormerHandWrittenTimes()
    {
        var shrink = TargetVisual.CreatePlan(LockOnAnimation.ShrinkPulse, Seconds(1.2), 50);
        Assert.Equal(1.8, shrink.Shrink!.From);
        Assert.Equal(1.0, shrink.Shrink.To);
        Assert.Equal(Seconds(0.4), shrink.Shrink.Duration.TimeSpan);
        Assert.Equal(Seconds(0.3), shrink.Fade!.Duration.TimeSpan);
        Assert.Equal(Seconds(0.4), shrink.Pulse.BeginTime);
        Assert.Equal(Seconds(0.8 / 6), shrink.Pulse.Duration.TimeSpan);
        Assert.Equal(1.0, shrink.Pulse.To);
        Assert.Equal(0.0, shrink.Pulse.From);
        Assert.Equal(3, shrink.Pulse.RepeatBehavior.Count);
        Assert.True(shrink.Pulse.AutoReverse);

        var pulse = TargetVisual.CreatePlan(LockOnAnimation.Pulse, Seconds(1.2), 50);
        Assert.Equal(Seconds(0), pulse.Pulse.BeginTime);
        Assert.Equal(Seconds(0.2), pulse.Pulse.Duration.TimeSpan);
        Assert.Equal(1.0, pulse.Pulse.To);
        Assert.Null(pulse.Shrink);
        Assert.Null(pulse.Scan);

        var scan = TargetVisual.CreatePlan(LockOnAnimation.ScanPulse, Seconds(1.2), 50);
        Assert.Equal(Seconds(0.5), scan.Scan!.KeyFrames[1].KeyTime.TimeSpan);
        Assert.Equal(Seconds(0.5), scan.Pulse.BeginTime);
        Assert.Equal(Seconds(0.7 / 6), scan.Pulse.Duration.TimeSpan);
        Assert.Equal(1.0, scan.Pulse.To);
    }

    [StaFact]
    public void LockOnPlan_TimesScaleWithTheDuration()
    {
        var plan = TargetVisual.CreatePlan(LockOnAnimation.ShrinkPulse, Seconds(2.4), 50);
        Assert.Equal(Seconds(0.8), plan.Shrink!.Duration.TimeSpan);
        Assert.Equal(Seconds(0.6), plan.Fade!.Duration.TimeSpan);
        Assert.Equal(Seconds(0.8), plan.Pulse.BeginTime);

        var scan = TargetVisual.CreatePlan(LockOnAnimation.ScanPulse, Seconds(0.6), 50);
        Assert.Equal(Seconds(0.25), scan.Scan!.KeyFrames[1].KeyTime.TimeSpan);
        Assert.Equal(Seconds(0.25), scan.Pulse.BeginTime);
    }

    [StaFact]
    public void LockOnPlan_Intensity_ControlsMovementAndPulseHeight()
    {
        double Start(int p) => TargetVisual.CreatePlan(LockOnAnimation.ShrinkPulse, Seconds(1.2), p).Shrink!.From!.Value;
        double Peak(int p) => TargetVisual.CreatePlan(LockOnAnimation.Pulse, Seconds(1.2), p).Pulse.To!.Value;

        Assert.Equal(1.2, Start(0));
        Assert.Equal(1.8, Start(50));
        Assert.Equal(2.4, Start(100));
        Assert.Equal(0.4, Peak(0));
        Assert.Equal(1.0, Peak(50));
        Assert.Equal(1.6, Peak(100));
    }

    [StaFact]
    public void Blink_Intensity_ControlsTempoAndContrast_FiftyPercentIsTheFormerBlink()
    {
        var former = TargetVisual.CreateBlink(50);
        Assert.Equal(1.0, former.From);
        Assert.Equal(0.3, former.To);
        Assert.Equal(TimeSpan.FromMilliseconds(180), former.Duration.TimeSpan);
        Assert.True(former.AutoReverse);
        Assert.Equal(RepeatBehavior.Forever, former.RepeatBehavior);

        var calm = TargetVisual.CreateBlink(0);
        var wild = TargetVisual.CreateBlink(100);
        Assert.Equal(TimeSpan.FromMilliseconds(300), calm.Duration.TimeSpan);
        Assert.Equal(TimeSpan.FromMilliseconds(120), wild.Duration.TimeSpan);
        Assert.Equal(0.6, calm.To);
        Assert.Equal(0.1, wild.To);
    }

    [StaFact]
    public void TargetOverlay_PassesDurationAndIntensityToTheElements()
    {
        var overlay = Overlay();
        Assert.Equal(1200, overlay.LockOnMilliseconds);
        Assert.Equal(50, overlay.LockOnIntensity);

        overlay.LockOnMilliseconds = 500;
        overlay.LockOnIntensity = 100;
        overlay.Snapshot = Snapshot(Target(1, 0.1, locking: true, lockStart: T0));

        var visual = overlay.Visuals[1];
        Assert.Equal(100, visual.Intensity);
        Assert.True(visual.IsLocking);
        overlay.Snapshot = Snapshot(Target(1, 0.1, locking: false, lockStart: T0));
        visual.StopAnimations();
    }

    [StaFact]
    public void TargetOverlay_StyleAndColor_ReachTheElements()
    {
        var overlay = Overlay();
        overlay.Snapshot = Snapshot(Target(1, 0.1));

        overlay.BoxStyle = TargetBoxStyle.CornersCrosshair;
        overlay.BoxColor = TargetColor.Cyan;

        Assert.Equal(TargetBoxStyle.CornersCrosshair, overlay.Visuals[1].BoxStyle);
        Assert.Equal(TargetColor.Cyan, overlay.Visuals[1].ColorKind);
    }

    [StaFact]
    public void TargetOverlay_DoesNothing_WhileNotLaidOut_AndShowsTargetsAfterwards()
    {
        var overlay = new TargetOverlay { ImageWidth = 1920, ImageHeight = 1080 };
        overlay.Snapshot = Snapshot(Target(1, 0.1));
        Assert.Empty(overlay.Visuals);

        Relayout(overlay);

        Assert.Single(overlay.Visuals);
    }

    [StaFact]
    public void TargetOverlay_IsNotHitTestable()
    {
        var overlay = new TargetOverlay();

        Assert.False(overlay.IsHitTestVisible);
        Assert.False(overlay.Focusable);
    }

    // ---- FunText

    [StaFact]
    public void FunText_TypesOnlyWhenFadingIn_AndSwapsTextWithoutTypingAgain()
    {
        var text = new FunText { Text = "" };

        text.IsShown = true;
        text.Text = "Hey du, mach mit!";
        Assert.True(text.IsTyping);
        Assert.Equal("", text.DisplayedText);

        text.FinishTyping();
        Assert.False(text.IsTyping);
        Assert.Equal("Hey du, mach mit!", text.DisplayedText);

        // du ↔ ihr bei sichtbarem Feld: sofort ausgetauscht, kein neuer Tipp-Effekt.
        text.Text = "Hey ihr, macht mit!";
        Assert.False(text.IsTyping);
        Assert.Equal("Hey ihr, macht mit!", text.DisplayedText);
    }

    [StaFact]
    public void FunText_TextChangeWhileStillTyping_KeepsTyping()
    {
        var text = new FunText { Text = "Hey du!" };
        text.IsShown = true;
        Assert.True(text.IsTyping);

        text.Text = "Hey ihr alle zusammen!";

        Assert.True(text.IsTyping);
        Assert.Equal("", text.DisplayedText);
    }

    [StaFact]
    public void FunText_TypesAgain_WhenShownAnewButNotWhenHiding()
    {
        var text = new FunText { Text = "Hey du!" };
        text.IsShown = true;
        text.FinishTyping();

        // Ausblenden: Text bleibt stehen, auch wenn das Ansichtsmodell ihn schon leert.
        text.IsShown = false;
        text.Text = "";
        Assert.Equal("Hey du!", text.DisplayedText);
        Assert.False(text.IsTyping);

        text.Text = "Hey ihr!";
        text.IsShown = true;
        Assert.True(text.IsTyping);
        Assert.Equal("", text.DisplayedText);
    }

    [StaFact]
    public void FunText_StartsHidden_AndFollowsIsShown()
    {
        var text = new FunText();
        Assert.Equal(Visibility.Collapsed, text.Visibility);
        Assert.False(text.IsHitTestVisible);

        text.IsShown = true;
        Assert.Equal(Visibility.Visible, text.Visibility);

        text.IsShown = false;
        text.FinishFade();
        Assert.Equal(Visibility.Collapsed, text.Visibility);
        Assert.Equal(0, text.Opacity);
    }

    [StaFact]
    public void FunText_StyleSelectsHudOrHint()
    {
        var text = new FunText { Text = "Hey du!", HintStyle = HintStyle.Neon };
        Assert.True(text.HudVisible);
        Assert.False(text.HintVisible);

        text.TextStyle = FunTextStyle.Hint;
        text.IsShown = true;

        Assert.False(text.HudVisible);
        Assert.True(text.HintVisible);
        Assert.Equal("Hey du!", text.Hint.Text);
        Assert.Equal(HintStyle.Neon, text.Hint.HintStyle);
        Assert.False(text.Hint.ShowLayoutHint);
    }

    [StaTheory]
    [InlineData(FunTextPosition.Bottom, HintPosition.Bottom, VerticalAlignment.Stretch)]
    [InlineData(FunTextPosition.Top, HintPosition.Top, VerticalAlignment.Stretch)]
    [InlineData(FunTextPosition.Center, HintPosition.Bottom, VerticalAlignment.Center)]
    public void FunText_PositionsTheHintLikeTheExistingHints(FunTextPosition position, HintPosition hint, VerticalAlignment alignment)
    {
        var text = new FunText { TextStyle = FunTextStyle.Hint, Position = position };

        Assert.Equal(hint, text.Hint.Position);
        Assert.Equal(alignment, text.Hint.VerticalAlignment);
    }
}
