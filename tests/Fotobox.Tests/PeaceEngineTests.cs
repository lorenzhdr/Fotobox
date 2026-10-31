using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Fotobox.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

public sealed class PeaceEngineTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly List<PeaceTrigger> _triggers = [];

    private static PeaceSettings Settings(Action<PeaceSettings>? configure = null)
    {
        var s = new PeaceSettings { HoldMilliseconds = 1000, CooldownSeconds = 0, SensitivityPercent = 50, ActionSelfDestruct = true };
        configure?.Invoke(s);
        return s;
    }

    private PeaceEngine Create(PeaceSettings? settings = null, bool moustache = true, Func<int, int>? random = null, bool enabled = true)
    {
        var engine = new PeaceEngine(_time, random);
        engine.Configure(settings ?? Settings(), moustache);
        engine.GesturesEnabled = enabled;
        engine.Triggered += (_, t) => _triggers.Add(t);
        return engine;
    }

    private static readonly HandLandmarks Peace = PeaceHandFixtures.Hand(HandPose.Peace);
    private static readonly HandLandmarks Fist = PeaceHandFixtures.Hand(HandPose.Fist);

    /// <summary>Zeit vorrücken, dann eine Analyse mit der Hand (oder ohne).</summary>
    private void Step(PeaceEngine engine, int ms, HandLandmarks? hand, IReadOnlyList<TrackedFace>? faces = null)
    {
        _time.Advance(TimeSpan.FromMilliseconds(ms));
        engine.OnUpdate(hand is null ? [] : [hand], faces ?? []);
    }

    /// <summary>Hält die Hand über <paramref name="totalMs"/> in Schritten à 100 ms.</summary>
    private void Hold(PeaceEngine engine, int totalMs, HandLandmarks? hand)
    {
        for (var elapsed = 0; elapsed < totalMs; elapsed += 100)
            Step(engine, 100, hand);
    }

    [Fact]
    public void TriggersExactlyOnce_AfterHoldDuration()
    {
        var engine = Create();
        Step(engine, 0, Peace);
        Hold(engine, 800, Peace);
        Assert.Empty(_triggers);

        Hold(engine, 200, Peace);
        Assert.Single(_triggers);
        Assert.Equal(PeaceAction.SelfDestruct, _triggers[0].Action);

        Hold(engine, 5000, Peace); // Hand bleibt oben: kein zweites Auslösen
        Assert.Single(_triggers);
        Assert.False(engine.IsArmed);
    }

    [Fact]
    public void Progress_GrowsWithHoldTime_AndIsZeroWithoutPeace()
    {
        var engine = Create();
        Step(engine, 0, Peace);
        Assert.Equal(0, engine.Snapshot.Hands.Single().Progress, 3);

        Hold(engine, 500, Peace);
        var hand = engine.Snapshot.Hands.Single();
        Assert.True(hand.IsPeace);
        Assert.Equal(0.5, hand.Progress, 2);

        Step(engine, 100, Fist);
        Assert.False(engine.Snapshot.Hands.Single().IsPeace);
    }

    [Fact]
    public void ShortGap_UpTo250ms_IsTolerated()
    {
        var engine = Create();
        Step(engine, 0, Peace);
        Step(engine, 100, Fist);
        Step(engine, 100, Peace); // 200 ms Abstand zur letzten Peace-Analyse
        Hold(engine, 800, Peace);
        Assert.Single(_triggers);
    }

    [Fact]
    public void LongerGap_ResetsTheHold()
    {
        var engine = Create(Settings(s => s.AnalysesPerSecond = 10)); // 10/s → Mindestfrist 250 ms
        Step(engine, 0, Peace);
        Hold(engine, 400, Peace);
        Step(engine, 300, Fist);
        Assert.Equal(0, engine.Snapshot.Hands.Single().Progress, 3);
        Step(engine, 100, Peace); // Halten beginnt hier von vorn
        Hold(engine, 800, Peace);
        Assert.Empty(_triggers);
        Hold(engine, 300, Peace);
        Assert.Single(_triggers);
    }

    [Fact]
    public void Tick_AloneCompletesTheHold_AndResetsOnGap()
    {
        var engine = Create(Settings(s => s.AnalysesPerSecond = 10));
        Step(engine, 0, Peace);
        _time.Advance(TimeSpan.FromMilliseconds(300));
        engine.Tick(); // Aussetzer > 250 ms
        Assert.Equal(0, engine.Snapshot.Hands.Single().Progress, 3);
        Assert.Empty(_triggers);
    }

    [Theory]
    [InlineData(10, 250)]
    [InlineData(5, 500)]
    [InlineData(1, 2500)]
    public void GapTolerance_GrowsWithAnalysisInterval(int analysesPerSecond, int expectedMs)
    {
        var engine = Create(Settings(s => s.AnalysesPerSecond = analysesPerSecond));
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), engine.GapTolerance);
    }

    [Theory]
    [InlineData(1, 2500)]
    [InlineData(5, 500)]
    [InlineData(10, 250)]
    [InlineData(20, 125)]
    [InlineData(30, 125)]
    [InlineData(50, 125)]
    public void LostAfterFor_IsTwoAndAHalfIntervals_ButNeverBelow125ms(int analysesPerSecond, int expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), PeaceEngine.LostAfterFor(analysesPerSecond));
    }

    [Fact]
    public void HandTrack_AtTheMaximumRate_SurvivesAGapBeyondOneAndAHalfCameraFrames()
    {
        // 2,5 / 50 = 50 ms läge knapp über einem Kamerabild; die Handspur lebt mindestens so lange wie der Aussetzer-Toleranz (250 ms).
        var engine = Create(Settings(s => s.AnalysesPerSecond = SettingsLimits.PeaceAnalysesPerSecondMax));
        Step(engine, 0, Peace);
        var id = engine.Snapshot.Hands.Single().TrackId;

        Step(engine, 100, null);
        Step(engine, 20, Peace);

        Assert.Equal(id, engine.Snapshot.Hands.Single().TrackId);
    }

    [Fact]
    public void SlowAnalyses_OneSecondApart_StillCompleteTheHold()
    {
        var engine = Create(Settings(s => { s.AnalysesPerSecond = 1; s.HoldMilliseconds = 2000; }));
        Step(engine, 0, Peace);
        Step(engine, 1000, Peace);
        Assert.Empty(_triggers);
        Step(engine, 1000, Peace);
        Assert.Single(_triggers);
    }

    [Fact]
    public void HandDown_ArmsAgain_AfterQuietTime()
    {
        var engine = Create();
        Step(engine, 0, Peace);
        Hold(engine, 1000, Peace);
        Assert.Single(_triggers);

        Hold(engine, 300, Fist); // noch keine 400 ms ohne Peace
        engine.Tick();
        Assert.False(engine.IsArmed);
        Hold(engine, 200, null);
        engine.Tick();
        Assert.True(engine.IsArmed);

        Step(engine, 100, Peace);
        Hold(engine, 1000, Peace);
        Assert.Equal(2, _triggers.Count);
    }

    [Fact]
    public void TickWithoutHandUpdates_DoesNotRearm_AndPeaceAfterwardsDoesNotTrigger()
    {
        var engine = Create();
        Step(engine, 0, Peace);
        Hold(engine, 1000, Peace);
        Assert.Single(_triggers);

        // Foto/Vorschau: keine Hand-Updates, nur die Uhr läuft (und Ticks).
        for (var i = 0; i < 50; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            engine.Tick();
        }
        Assert.False(engine.IsArmed);

        // Zurück im Leerlauf: Die Hand ist weiterhin oben.
        Hold(engine, 3000, Peace);
        Assert.Single(_triggers);
        Assert.False(engine.IsArmed);

        // Erst beobachtete Ruhe (Hand-Updates ohne Peace über die Ruhezeit) macht wieder scharf.
        Hold(engine, 300, Fist);
        Assert.False(engine.IsArmed);
        Hold(engine, 300, Fist);
        Assert.True(engine.IsArmed);

        Step(engine, 100, Peace);
        Hold(engine, 1000, Peace);
        Assert.Equal(2, _triggers.Count);
    }

    [Fact]
    public void GapBetweenHandUpdates_RestartsTheObservedQuietTime()
    {
        var engine = Create();
        Step(engine, 0, Peace);
        Hold(engine, 1000, Peace);
        Assert.Single(_triggers);

        Hold(engine, 200, Fist);          // 200 ms beobachtet, noch nicht genug
        _time.Advance(TimeSpan.FromSeconds(5));
        engine.Tick();                    // Lücke ohne Updates zählt nicht als Ruhe
        Assert.False(engine.IsArmed);
        Step(engine, 100, Fist);          // erstes Update nach der Lücke: Beobachtung beginnt neu
        Assert.False(engine.IsArmed);
        Hold(engine, 500, Fist); // Ruhefrist bei 5 Analysen/s = 500 ms
        Assert.True(engine.IsArmed);
    }

    [Fact]
    public void Cooldown_BlocksUntilItHasPassed_AndHandMustGoDown()
    {
        var engine = Create(Settings(s => s.CooldownSeconds = 10));
        Step(engine, 0, Peace);
        Hold(engine, 1000, Peace);
        Assert.Single(_triggers);

        Hold(engine, 500, null);
        Hold(engine, 3000, Peace); // Hand wieder oben, aber Sperrzeit
        Assert.Single(_triggers);

        Hold(engine, 1000, Fist); // runter
        _time.Advance(TimeSpan.FromSeconds(10));
        engine.Tick();
        Assert.True(engine.IsArmed);
        Hold(engine, 1100, Peace);
        Assert.Equal(2, _triggers.Count);
    }

    [Fact]
    public void GesturesDisabled_NeverTriggers_ButRearmingContinues()
    {
        var engine = Create();
        Step(engine, 0, Peace);
        Hold(engine, 1000, Peace);
        Assert.Single(_triggers);

        engine.GesturesEnabled = false;
        Hold(engine, 3000, Peace);
        Assert.Single(_triggers);
        Assert.False(engine.IsArmed);

        Hold(engine, 600, null); // Hand runter, während gesperrt
        engine.Tick();
        Assert.True(engine.IsArmed);

        Hold(engine, 3000, Peace); // weiterhin gesperrt
        Assert.Single(_triggers);
        Assert.Equal(0, engine.Snapshot.Hands.Single().Progress, 3);

        engine.GesturesEnabled = true; // Hand ist oben, aber noch nicht heruntergenommen: Halten beginnt jetzt
        Hold(engine, 100, Peace);
        Hold(engine, 1000, Peace);
        Assert.Equal(2, _triggers.Count);
    }

    [Theory]
    [InlineData(0, PeaceAction.SelfDestruct)]
    [InlineData(1, PeaceAction.Photo)]
    [InlineData(2, PeaceAction.Moustache)]
    public void SeveralActions_AreChosenRandomly(int pick, PeaceAction expected)
    {
        var asked = new List<int>();
        var engine = Create(Settings(s => { s.ActionPhoto = true; s.ActionMoustache = true; }), random: n =>
        {
            asked.Add(n);
            return pick;
        });
        Step(engine, 0, Peace);
        Hold(engine, 1000, Peace);
        Assert.Equal(expected, Assert.Single(_triggers).Action);
        Assert.Equal([3], asked);
    }

    [Fact]
    public void SingleAction_IsUsed_WithoutAskingTheRandom()
    {
        var engine = Create(Settings(s => { s.ActionSelfDestruct = false; s.ActionPhoto = true; }),
            random: _ => throw new InvalidOperationException("kein Zufall nötig"));
        Step(engine, 0, Peace);
        Hold(engine, 1000, Peace);
        Assert.Equal(PeaceAction.Photo, Assert.Single(_triggers).Action);
    }

    [Fact]
    public void Moustache_IsFilteredOut_WithoutFaceModel()
    {
        var engine = Create(Settings(s => s.ActionMoustache = true), moustache: false, random: _ => 1);
        Step(engine, 0, Peace);
        Hold(engine, 1000, Peace);
        Assert.Equal(PeaceAction.SelfDestruct, Assert.Single(_triggers).Action);
    }

    [Fact]
    public void OnlyMoustache_WithoutFaceModel_TriggersNothing_ButConsumesTheGesture()
    {
        var engine = Create(Settings(s => { s.ActionSelfDestruct = false; s.ActionMoustache = true; }), moustache: false);
        Step(engine, 0, Peace);
        Hold(engine, 3000, Peace);
        Assert.Empty(_triggers);
        Assert.False(engine.IsArmed);
    }

    [Fact]
    public void Trigger_CarriesHandIdFaceIdAndHandCenter()
    {
        var engine = Create();
        var face = new TrackedFace(7, PeaceHandFixtures.Face(centerX: 0.5, centerY: 0.3, width: 0.15));
        Step(engine, 0, Peace, [face]);
        for (var i = 0; i < 10; i++)
            Step(engine, 100, Peace, [face]);

        var trigger = Assert.Single(_triggers);
        Assert.Equal(7, trigger.FaceTrackId);
        Assert.True(trigger.HandTrackId > 0);
        Assert.InRange(trigger.HandCenter.X, 0.3, 0.7);
        Assert.InRange(trigger.HandCenter.Y, 0.4, 0.8);
        Assert.Equal(7, engine.Snapshot.Hands.Single().FaceId);
        Assert.Single(engine.Snapshot.Faces);
    }

    [Fact]
    public void HandFarFromAnyFace_HasNoFaceId()
    {
        var engine = Create();
        var farFace = new TrackedFace(1, PeaceHandFixtures.Face(centerX: 0.05, centerY: 0.05, width: 0.04));
        Step(engine, 0, Peace, [farFace]);
        Assert.Null(engine.Snapshot.Hands.Single().FaceId);
    }

    [Fact]
    public void Changed_IsRaisedOnUpdates()
    {
        var engine = Create();
        var changes = 0;
        engine.Changed += (_, _) => changes++;
        Step(engine, 0, Peace);
        Assert.True(changes > 0);
    }

    [Fact]
    public void Reset_ClearsHands_ButKeepsTheHandDownRule()
    {
        var engine = Create();
        Step(engine, 0, Peace);
        Hold(engine, 1000, Peace);
        Assert.Single(_triggers);

        engine.Reset();
        Assert.Empty(engine.Snapshot.Hands);

        // Hand ist unverändert oben: kein sofortiges zweites Auslösen.
        Hold(engine, 2500, Peace);
        Assert.Single(_triggers);
    }

    [Fact]
    public void LostHand_DisappearsAfter400ms()
    {
        var engine = Create(Settings(s => s.AnalysesPerSecond = 10)); // bei 10/s gilt die Mindestfrist
        Step(engine, 0, Peace);
        Assert.Single(engine.Snapshot.Hands);
        _time.Advance(TimeSpan.FromMilliseconds(450));
        engine.Tick();
        Assert.Empty(engine.Snapshot.Hands);
    }
}
