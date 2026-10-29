using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Heart;
using Fotobox.Core.Peace;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

public sealed class HeartEngineTests
{
    // Handrate 10/s: Aussetzer-Toleranz 250 ms, Wiederscharf-Ruhe 400 ms (die Mindestwerte).
    private const int Rate = 10;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly List<HeartTrigger> _triggers = [];
    private int _changes;

    private static HeartSettings Settings(Action<HeartSettings>? configure = null)
    {
        var s = new HeartSettings { Enabled = true, ShapeClassic = true, ShapeRoof = true, HoldMilliseconds = 1000, SensitivityPercent = 50 };
        configure?.Invoke(s);
        return s;
    }

    private HeartEngine Create(HeartSettings? settings = null, bool enabled = true, int rate = Rate)
    {
        var engine = new HeartEngine(_time);
        engine.Configure(settings ?? Settings(), rate);
        engine.GesturesEnabled = enabled;
        engine.Triggered += (_, t) => _triggers.Add(t);
        engine.Changed += (_, _) => _changes++;
        return engine;
    }

    private static readonly IReadOnlyList<HandLandmarks> Heart = HeartHandFixtures.Classic(cx: 0.5, cy: 0.6);
    private static readonly IReadOnlyList<HandLandmarks> Nothing = [];

    private void Step(HeartEngine engine, int ms, IReadOnlyList<HandLandmarks> hands, IReadOnlyList<TrackedFace>? faces = null)
    {
        _time.Advance(TimeSpan.FromMilliseconds(ms));
        engine.OnUpdate(hands, faces ?? []);
    }

    /// <summary>Hält die Hände über <paramref name="totalMs"/> in Schritten à 100 ms.</summary>
    private void Hold(HeartEngine engine, int totalMs, IReadOnlyList<HandLandmarks> hands, IReadOnlyList<TrackedFace>? faces = null)
    {
        for (var elapsed = 0; elapsed < totalMs; elapsed += 100)
            Step(engine, 100, hands, faces);
    }

    [Fact]
    public void TriggersExactlyOnce_AfterHoldDuration()
    {
        var engine = Create();
        Step(engine, 0, Heart);
        Hold(engine, 800, Heart);
        Assert.Empty(_triggers);

        Hold(engine, 200, Heart);
        Assert.Single(_triggers);

        Hold(engine, 5000, Heart); // Herz bleibt stehen: kein zweites Auslösen
        Assert.Single(_triggers);
        Assert.False(engine.Snapshot.Armed);
        Assert.False(engine.IsArmed);
    }

    [Fact]
    public void Tick_CompletesTheHold_WithoutNewUpdate()
    {
        var engine = Create();
        Step(engine, 0, Heart);
        Hold(engine, 900, Heart);
        Assert.Empty(_triggers);

        _time.Advance(TimeSpan.FromMilliseconds(100));
        engine.Tick();
        Assert.Single(_triggers);
    }

    [Fact]
    public void Trigger_ContainsActionShapeCenterAndBox()
    {
        var engine = Create();
        Hold(engine, 1500, Heart);

        var trigger = Assert.Single(_triggers);
        Assert.Equal(HeartAction.Kisscam, trigger.Action);
        Assert.Equal(HeartShape.Classic, trigger.Shape);
        Assert.Equal(0.5, trigger.Center.X, 2);
        Assert.Equal(0.6, trigger.Center.Y, 2);
        Assert.Equal(Math.Min(Heart[0].Box.X, Heart[1].Box.X), trigger.HeartBox.X, 6);
        Assert.Equal(Math.Max(Heart[0].Box.Right, Heart[1].Box.Right), trigger.HeartBox.Right, 6);
        Assert.Equal(Math.Min(Heart[0].Box.Y, Heart[1].Box.Y), trigger.HeartBox.Y, 6);
        Assert.Equal(Math.Max(Heart[0].Box.Bottom, Heart[1].Box.Bottom), trigger.HeartBox.Bottom, 6);
        Assert.Empty(trigger.FaceBoxes);
    }

    [Fact]
    public void Trigger_ContainsFaceOfTheHeartHands_OncePerFace()
    {
        var engine = Create();
        var face = PeaceHandFixtures.Face(centerX: 0.5, centerY: 0.25, width: 0.12);
        var far = PeaceHandFixtures.Face(centerX: 0.02, centerY: 0.02, width: 0.02); // zu weit weg von den Händen
        IReadOnlyList<TrackedFace> faces = [new TrackedFace(1, face), new TrackedFace(2, far)];

        Hold(engine, 1500, Heart, faces);

        var trigger = Assert.Single(_triggers);
        var box = Assert.Single(trigger.FaceBoxes); // beide Hände gehören zu demselben Gesicht
        Assert.Equal(face.Box, box);
    }

    [Fact]
    public void Trigger_UsesConfiguredAction()
    {
        var engine = Create(Settings(s => s.Action = HeartAction.Kisscam));
        Hold(engine, 1500, Heart);
        Assert.Equal(HeartAction.Kisscam, Assert.Single(_triggers).Action);
    }

    [Fact]
    public void Progress_GrowsWithHoldTime_AndShapeIsReported()
    {
        var engine = Create();
        Step(engine, 0, Heart);
        Assert.True(engine.DetectedNow);
        Assert.Equal(HeartShape.Classic, engine.DetectedShape);
        var view = Assert.Single(engine.Snapshot.Hearts);
        Assert.Equal(0, view.Progress, 3);
        Assert.True(engine.Snapshot.Armed);

        Hold(engine, 500, Heart);
        view = Assert.Single(engine.Snapshot.Hearts);
        Assert.Equal(0.5, view.Progress, 2);
        Assert.Equal(HeartShape.Classic, view.Shape);
        Assert.Equal(0.5, view.Center.X, 2);
        Assert.True(view.SizeNorm > 0);
    }

    [Fact]
    public void SnapshotEmpties_AfterTheGapTolerance()
    {
        var engine = Create();
        Step(engine, 0, Heart);
        Assert.True(engine.DetectedNow);

        Step(engine, 200, Nothing);
        Assert.True(engine.DetectedNow); // innerhalb der Toleranz

        _time.Advance(TimeSpan.FromMilliseconds(100));
        engine.Tick();
        Assert.False(engine.DetectedNow);
        Assert.Null(engine.DetectedShape);
        Assert.Empty(engine.Snapshot.Hearts);
    }

    [Fact]
    public void ShortDropout_KeepsTheHold()
    {
        var engine = Create();
        Step(engine, 0, Heart);
        Hold(engine, 500, Heart);
        Step(engine, 100, Nothing); // ein verpasstes Bild: Lücke zwischen zwei Treffern 200 ms < 250 ms
        Hold(engine, 500, Heart);
        Assert.Single(_triggers);
    }

    [Fact]
    public void LongDropout_RestartsTheHold()
    {
        var engine = Create();
        Step(engine, 0, Heart);
        Hold(engine, 700, Heart);
        Step(engine, 400, Nothing); // länger als die Toleranz
        Hold(engine, 700, Heart);
        Assert.Empty(_triggers);

        Hold(engine, 400, Heart);
        Assert.Single(_triggers);
    }

    [Fact]
    public void Tolerance_FollowsTheAnalysisRate()
    {
        var engine = Create(rate: 10);
        Assert.Equal(TimeSpan.FromMilliseconds(250), engine.GapTolerance);
        Assert.Equal(TimeSpan.FromMilliseconds(400), engine.RearmQuietTime);

        var slow = Create(rate: 2);
        Assert.Equal(TimeSpan.FromSeconds(1.25), slow.GapTolerance);
        Assert.Equal(TimeSpan.FromSeconds(1.25), slow.RearmQuietTime);
    }

    [Fact]
    public void GesturesDisabled_NeverTriggers_ButStillDetects()
    {
        var engine = Create(enabled: false);
        Step(engine, 0, Heart);
        Hold(engine, 5000, Heart);
        Assert.Empty(_triggers);
        Assert.True(engine.DetectedNow);
        Assert.Equal(0, Assert.Single(engine.Snapshot.Hearts).Progress);

        // Wieder erlaubt und das Herz steht noch: Das Halten beginnt jetzt von vorn, die Engine war die ganze Zeit scharf.
        engine.GesturesEnabled = true;
        Hold(engine, 900, Heart);
        Assert.Empty(_triggers);
        Hold(engine, 200, Heart);
        Assert.Single(_triggers);
    }

    [Fact]
    public void DisablingGestures_AbortsARunningHold()
    {
        var engine = Create();
        Step(engine, 0, Heart);
        Hold(engine, 700, Heart);
        engine.GesturesEnabled = false;
        Hold(engine, 1000, Heart);
        Assert.Empty(_triggers);
    }

    [Fact]
    public void SettingsDisabled_NeverTriggersAndDetectsNothing()
    {
        var engine = Create(Settings(s => s.Enabled = false));
        Hold(engine, 5000, Heart);
        Assert.Empty(_triggers);
        Assert.False(engine.DetectedNow);
        Assert.Empty(engine.Snapshot.Hearts);
    }

    [Fact]
    public void NoShapeSelected_NeverTriggers()
    {
        var engine = Create(Settings(s => s.ShapeClassic = s.ShapeRoof = s.ShapeInverted = s.ShapeFingerHeart = false));
        Hold(engine, 5000, Heart);
        Assert.Empty(_triggers);
        Assert.False(engine.DetectedNow);
    }

    [Fact]
    public void OnlyEnabledShapesCount()
    {
        var engine = Create(Settings(s => { s.ShapeClassic = false; s.ShapeRoof = true; }));
        Hold(engine, 3000, Heart); // Klassisch ist nicht gewählt
        Assert.Empty(_triggers);

        var roof = HeartHandFixtures.Roof(cy: 0.6);
        Hold(engine, 1500, roof);
        var trigger = Assert.Single(_triggers);
        Assert.Equal(HeartShape.Roof, trigger.Shape);
    }

    [Fact]
    public void Configure_DisablingDiscardsTheHold()
    {
        var engine = Create();
        Step(engine, 0, Heart);
        Hold(engine, 700, Heart);
        engine.Configure(Settings(s => s.Enabled = false), Rate);
        Assert.False(engine.DetectedNow);
        engine.Configure(Settings(), Rate);
        Hold(engine, 700, Heart);
        Assert.Empty(_triggers); // Halten begann nach dem Wiedereinschalten von vorn
        Hold(engine, 400, Heart);
        Assert.Single(_triggers);
    }

    // ---- Wiederscharf

    [Fact]
    public void RearmsOnlyAfterObservedQuiet()
    {
        var engine = Create();
        Hold(engine, 1500, Heart);
        Assert.Single(_triggers);

        // Zu kurze Ruhe (300 ms < 400 ms), dann steht das Herz wieder: nicht scharf, kein zweites Auslösen.
        Hold(engine, 300, Nothing);
        Hold(engine, 5000, Heart);
        Assert.Single(_triggers);
        Assert.False(engine.IsArmed);

        // Ruhe lang genug: scharf, nächstes Halten löst erneut aus.
        Hold(engine, 600, Nothing);
        Assert.True(engine.IsArmed);
        Assert.True(engine.Snapshot.Armed);
        Hold(engine, 1500, Heart);
        Assert.Equal(2, _triggers.Count);
    }

    [Fact]
    public void GapWithoutHandUpdates_DoesNotCountAsQuiet()
    {
        var engine = Create();
        Hold(engine, 1500, Heart);
        Assert.Single(_triggers);

        // Lange keine Hand-Updates (z. B. während der Kisscam): Das Herz kann unbemerkt stehen geblieben sein.
        for (var i = 0; i < 50; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(200));
            engine.Tick();
        }
        Assert.False(engine.IsArmed);

        // Erst beobachtete Ruhe macht wieder scharf: ein Update ohne Herz startet sie, 400 ms später ist die Frist erfüllt.
        Step(engine, 100, Nothing);
        Assert.False(engine.IsArmed);
        Hold(engine, 300, Nothing);
        Assert.False(engine.IsArmed);
        Hold(engine, 200, Nothing);
        Assert.True(engine.IsArmed);
    }

    [Fact]
    public void GapWithoutHandUpdates_ThenHeartStillThere_StaysDisarmed()
    {
        var engine = Create();
        Hold(engine, 1500, Heart);
        Assert.Single(_triggers);

        _time.Advance(TimeSpan.FromSeconds(30));
        engine.Tick();
        Hold(engine, 5000, Heart);
        Assert.Single(_triggers);
        Assert.False(engine.IsArmed);
    }

    [Fact]
    public void Reset_KeepsTheDisarmedState()
    {
        var engine = Create();
        Hold(engine, 1500, Heart);
        Assert.Single(_triggers);

        engine.Reset();
        Assert.False(engine.IsArmed);
        Assert.Empty(engine.Snapshot.Hearts);
        Assert.False(engine.DetectedNow);

        Hold(engine, 5000, Heart); // das Herz steht weiter: bleibt gesperrt
        Assert.Single(_triggers);

        Hold(engine, 600, Nothing);
        Assert.True(engine.IsArmed);
        Hold(engine, 1500, Heart);
        Assert.Equal(2, _triggers.Count);
    }

    [Fact]
    public void Reset_DiscardsHoldWhenArmed_AndNotifies()
    {
        var engine = Create();
        Step(engine, 0, Heart);
        Hold(engine, 700, Heart);
        var before = _changes;

        engine.Reset();
        Assert.True(_changes > before);
        Assert.Empty(engine.Snapshot.Hearts);
        Assert.True(engine.IsArmed);

        Hold(engine, 700, Heart); // Halten beginnt von vorn
        Assert.Empty(_triggers);
        Hold(engine, 400, Heart);
        Assert.Single(_triggers);
    }

    [Fact]
    public void Reset_WhileDisarmed_RequiresQuietAfterTheReset()
    {
        var engine = Create();
        Hold(engine, 1500, Heart);
        engine.Reset();

        // Ein einzelnes Update ohne Herz direkt nach dem Reset genügt nicht, auch wenn die Uhr weit vorgerückt ist.
        _time.Advance(TimeSpan.FromSeconds(10));
        engine.Tick();
        Step(engine, 0, Nothing);
        Assert.False(engine.IsArmed);
    }

    [Fact]
    public void DisabledPeriod_DoesNotCountAsObservedQuiet()
    {
        var engine = Create();
        Hold(engine, 1500, Heart);
        Assert.Single(_triggers);

        // Ausgeschaltet: Das Herz steht weiter, die Updates dürfen nicht als Ruhe zählen.
        engine.Configure(Settings(s => s.Enabled = false), Rate);
        Hold(engine, 3000, Heart);
        engine.Configure(Settings(), Rate);
        Hold(engine, 5000, Heart);
        Assert.Single(_triggers);
        Assert.False(engine.IsArmed);
    }

    [Fact]
    public void Changed_IsRaisedOnUpdates()
    {
        var engine = Create();
        Step(engine, 0, Nothing);
        var before = _changes;
        Step(engine, 100, Heart);
        Assert.True(_changes > before);
    }
}
