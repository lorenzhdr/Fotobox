using Fotobox.Core;
using Fotobox.Core.Peace;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

public sealed class MiddleFingerEngineTests
{
    // Handrate 10/s: Aussetzer-Toleranz 250 ms, Wiederscharf-Ruhe 400 ms (die Mindestwerte).
    private const int Rate = 10;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private int _triggers;
    private int _changes;

    private static MiddleFingerSettings Settings(Action<MiddleFingerSettings>? configure = null)
    {
        var s = new MiddleFingerSettings { Enabled = true, ActionCancelPeace = true, HoldMilliseconds = 500, SensitivityPercent = 50 };
        configure?.Invoke(s);
        return s;
    }

    /// <summary>Engine mit gesetzten Einstellungen; <paramref name="applicable"/> = true schaltet sie ein und wartet bis zum Scharfwerden.</summary>
    private MiddleFingerEngine Create(MiddleFingerSettings? settings = null, bool applicable = true, int rate = Rate)
    {
        var engine = new MiddleFingerEngine(_time);
        engine.Configure(settings ?? Settings(), rate);
        engine.Triggered += (_, _) => _triggers++;
        engine.Changed += (_, _) => _changes++;
        if (applicable)
        {
            engine.Applicable = true;
            _time.Advance(TimeSpan.FromMilliseconds(3000));
            engine.Tick();
            Assert.True(engine.IsArmed);
        }
        return engine;
    }

    private static readonly HandLandmarks Middle = PeaceHandFixtures.Hand(HandPose.MiddleOnly);
    private static readonly HandLandmarks Peace = PeaceHandFixtures.Hand(HandPose.Peace);
    private static readonly HandLandmarks Fist = PeaceHandFixtures.Hand(HandPose.Fist);

    private void Step(MiddleFingerEngine engine, int ms, HandLandmarks? hand)
    {
        _time.Advance(TimeSpan.FromMilliseconds(ms));
        engine.OnUpdate(hand is null ? [] : [hand]);
    }

    /// <summary>Hält die Hand über <paramref name="totalMs"/> in Schritten à 100 ms.</summary>
    private void Hold(MiddleFingerEngine engine, int totalMs, HandLandmarks? hand)
    {
        for (var elapsed = 0; elapsed < totalMs; elapsed += 100)
            Step(engine, 100, hand);
    }

    [Fact]
    public void Fires_AfterHold_ExactlyOnce()
    {
        var engine = Create();
        Step(engine, 0, Middle); // Halten beginnt
        Hold(engine, 400, Middle);
        Assert.Equal(0, _triggers);

        Step(engine, 100, Middle); // 500 ms
        Assert.Equal(1, _triggers);
        Assert.False(engine.IsArmed);

        Hold(engine, 5000, Middle); // Hand bleibt oben: kein zweites Auslösen
        Assert.Equal(1, _triggers);
    }

    [Fact]
    public void Tick_FiresWhenHoldCompletesWithoutNewUpdate()
    {
        var engine = Create(Settings(s => s.HoldMilliseconds = 300), rate: 2); // Toleranz 1,25 s
        Step(engine, 0, Middle);
        _time.Advance(TimeSpan.FromMilliseconds(200));
        engine.Tick();
        Assert.Equal(0, _triggers);

        _time.Advance(TimeSpan.FromMilliseconds(100));
        engine.Tick();
        Assert.Equal(1, _triggers);
    }

    [Fact]
    public void ShortGaps_AreTolerated()
    {
        var engine = Create();
        Step(engine, 0, Middle);
        Hold(engine, 300, Middle);
        Step(engine, 100, null); // 100 ms ohne Zeichen
        Step(engine, 100, Middle); // Lücke 200 ms ≤ 250 ms: Halten läuft weiter, insgesamt 500 ms
        Assert.Equal(1, _triggers);
    }

    [Fact]
    public void LongGap_ResetsTheHold()
    {
        var engine = Create();
        Step(engine, 0, Middle);
        Hold(engine, 200, Middle);
        Hold(engine, 300, null); // 300 ms Lücke > 250 ms
        Step(engine, 100, Middle); // Halten beginnt neu
        Assert.Equal(0, _triggers);
        Hold(engine, 300, Middle);
        Assert.Equal(0, _triggers); // seit dem Neubeginn erst 300 ms
        Hold(engine, 200, Middle);
        Assert.Equal(1, _triggers);
    }

    [Fact]
    public void Rearm_RequiresHandDownForQuietTime()
    {
        var engine = Create();
        Step(engine, 0, Middle);
        Hold(engine, 500, Middle);
        Assert.Equal(1, _triggers);

        // Nur 300 ms ohne Zeichen (< 400 ms): wieder zeigen und halten löst nicht erneut aus.
        Hold(engine, 300, null);
        Hold(engine, 1500, Middle);
        Assert.Equal(1, _triggers);
        Assert.False(engine.IsArmed);

        // Hand 500 ms unten (≥ 400 ms): scharf, das nächste Halten löst aus.
        Hold(engine, 500, null);
        Assert.True(engine.IsArmed);
        Step(engine, 100, Middle);
        Hold(engine, 500, Middle);
        Assert.Equal(2, _triggers);
    }

    [Fact]
    public void Rearm_AlsoWorksWithTickOnly()
    {
        var engine = Create();
        Step(engine, 0, Middle);
        Hold(engine, 500, Middle);
        Assert.False(engine.IsArmed);

        _time.Advance(TimeSpan.FromMilliseconds(500));
        engine.Tick();
        Assert.True(engine.IsArmed);
    }

    [Fact]
    public void NoAccumulation_WhileNotApplicable()
    {
        var engine = Create(applicable: false);
        Step(engine, 0, Middle);
        Hold(engine, 3000, Middle);
        Assert.Equal(0, _triggers);
    }

    [Fact]
    public void RisingEdge_StartsDisarmed_VisibleFingerMustDisappearFirst()
    {
        var engine = Create(applicable: false);
        Step(engine, 0, Middle);
        Hold(engine, 1000, Middle);

        engine.Applicable = true; // Mittelfinger ist schon oben
        Assert.False(engine.IsArmed);
        Hold(engine, 2000, Middle);
        Assert.Equal(0, _triggers);
        Assert.False(engine.IsArmed);

        Hold(engine, 500, null); // verschwindet für die Ruhezeit
        Assert.True(engine.IsArmed);
        Step(engine, 100, Middle);
        Hold(engine, 500, Middle);
        Assert.Equal(1, _triggers);
    }

    [Fact]
    public void RisingEdge_NeedsQuietTimeEvenWithoutAnyFinger()
    {
        var engine = Create(applicable: false);
        engine.Applicable = true;
        Assert.False(engine.IsArmed);

        _time.Advance(TimeSpan.FromMilliseconds(300)); // < 400 ms
        engine.Tick();
        Assert.False(engine.IsArmed);

        _time.Advance(TimeSpan.FromMilliseconds(100));
        engine.Tick();
        Assert.True(engine.IsArmed);
    }

    [Fact]
    public void SettingApplicableFalse_ResetsTheHold_AndTurningItOnAgainDisarms()
    {
        var engine = Create();
        Step(engine, 0, Middle);
        Hold(engine, 300, Middle);

        engine.Applicable = false;
        Hold(engine, 1000, Middle);
        Assert.Equal(0, _triggers);

        engine.Applicable = true;
        Hold(engine, 1000, Middle);
        Assert.Equal(0, _triggers); // Mittelfinger blieb sichtbar: entwaffnet
    }

    [Fact]
    public void PeaceThenMiddleFinger_ApplicableFromTheStart_BriefTransitionDoesNotFire_ButAHeldFingerDoes()
    {
        // Peace zählt nie als Mittelfinger (der Zeigefinger ist gestreckt).
        var engine = Create();
        Hold(engine, 2000, Peace);
        Assert.Equal(0, _triggers);
        Assert.False(engine.DetectedNow);

        // Beim Senken des Peace-Zeichens bleibt kurz nur der Mittelfinger oben (< Haltedauer): kein Auslösen.
        Hold(engine, 300, Middle);
        Hold(engine, 1000, Fist);
        Assert.Equal(0, _triggers);

        // Hält jemand dagegen nach Peace den Mittelfinger (Applicable war schon vorher an, ohne Mittelfinger), zählt er.
        Hold(engine, 500, Peace);
        Step(engine, 100, Middle);
        Hold(engine, 500, Middle);
        Assert.Equal(1, _triggers);
    }

    [Fact]
    public void DisabledSettings_NeverFire()
    {
        foreach (var settings in new[]
                 {
                     Settings(s => s.Enabled = false),
                     Settings(s => s.ActionCancelPeace = false),
                     Settings(s => { s.Enabled = false; s.ActionCancelPeace = false; }),
                 })
        {
            var engine = Create(settings);
            Step(engine, 0, Middle);
            Hold(engine, 3000, Middle);
            Assert.Equal(0, _triggers);
        }
    }

    [Fact]
    public void ReconfigureToDisabled_StopsARunningHold()
    {
        var engine = Create();
        Step(engine, 0, Middle);
        Hold(engine, 300, Middle);
        engine.Configure(Settings(s => s.Enabled = false), Rate);
        Hold(engine, 1000, Middle);
        Assert.Equal(0, _triggers);
    }

    [Fact]
    public void AnyHandCounts_AlsoAmongSeveral()
    {
        var engine = Create();
        _time.Advance(TimeSpan.FromMilliseconds(0));
        engine.OnUpdate([Fist, Peace, Middle]);
        for (var i = 0; i < 5; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            engine.OnUpdate([Fist, Middle]);
        }
        Assert.Equal(1, _triggers);
    }

    [Fact]
    public void DetectedNow_AndChanged_FollowTheUpdates()
    {
        var engine = Create();
        var before = _changes;
        Assert.False(engine.DetectedNow);

        Step(engine, 100, Middle);
        Assert.True(engine.DetectedNow);
        Assert.Equal(before + 1, _changes);

        Step(engine, 100, Middle); // unverändert
        Assert.Equal(before + 1, _changes);

        Step(engine, 100, Fist);
        Assert.False(engine.DetectedNow);
        Assert.Equal(before + 2, _changes);
    }

    [Fact]
    public void DetectedNow_ExpiresViaTick_WhenNoFurtherUpdatesArrive()
    {
        var engine = Create();
        Step(engine, 100, Middle);
        Assert.True(engine.DetectedNow);

        _time.Advance(TimeSpan.FromMilliseconds(200)); // < 250 ms Toleranz
        engine.Tick();
        Assert.True(engine.DetectedNow);

        _time.Advance(TimeSpan.FromMilliseconds(100));
        engine.Tick();
        Assert.False(engine.DetectedNow);
    }

    [Fact]
    public void DetectedNow_IsReportedEvenWhileNotApplicable()
    {
        var engine = Create(applicable: false);
        Step(engine, 100, Middle);
        Assert.True(engine.DetectedNow);
        Assert.Equal(0, _triggers);
    }

    [Fact]
    public void Reset_ClearsHoldAndDetection_ButKeepsWaitingForHandDown()
    {
        var engine = Create();
        Step(engine, 0, Middle);
        Hold(engine, 300, Middle);
        engine.Reset();
        Assert.False(engine.DetectedNow);

        Step(engine, 100, Middle); // neu beginnen
        Hold(engine, 300, Middle);
        Assert.Equal(0, _triggers);
        Hold(engine, 200, Middle);
        Assert.Equal(1, _triggers);

        engine.Reset(); // nach dem Auslösen: weiter entwaffnet
        Assert.False(engine.IsArmed);
        Hold(engine, 2000, Middle);
        Assert.Equal(1, _triggers);
    }

    [Theory]
    [InlineData(1, 2500, 2500)]
    [InlineData(5, 500, 500)]
    [InlineData(10, 250, 400)]
    [InlineData(30, 250, 400)]
    [InlineData(0, 2500, 2500)] // 0 wird wie 1 behandelt
    public void Configure_DerivesGapAndQuietTime_LikePeaceEngine(int rate, int gapMs, int quietMs)
    {
        var engine = new MiddleFingerEngine(_time);
        engine.Configure(Settings(), rate);
        Assert.Equal(TimeSpan.FromMilliseconds(gapMs), engine.GapTolerance);
        Assert.Equal(TimeSpan.FromMilliseconds(quietMs), engine.RearmQuietTime);
    }

    [Fact]
    public void LongerHoldSetting_IsRespected()
    {
        var engine = Create(Settings(s => s.HoldMilliseconds = SettingsLimits.MiddleFingerHoldMillisecondsMax));
        Step(engine, 0, Middle);
        Hold(engine, 2900, Middle);
        Assert.Equal(0, _triggers);
        Hold(engine, 100, Middle);
        Assert.Equal(1, _triggers);
    }
}
