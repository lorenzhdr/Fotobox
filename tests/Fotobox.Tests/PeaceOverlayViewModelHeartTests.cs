using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Heart;
using Fotobox.Core.Peace;
using Fotobox.ViewModels.Booth;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Herzerkennung im Ansichtsmodell der Peaceerkennung: gemeinsame Anforderung, Betriebsarten, Rücksetzen, Fortschrittsanzeige.</summary>
public sealed class PeaceOverlayViewModelHeartTests
{
    private readonly FakeGestureService _service = new();
    private readonly FakeSoundPlayer _sound = new();
    private readonly TrackingTimeProvider _time = new();

    public PeaceOverlayViewModelHeartTests() => SynchronizationContext.SetSynchronizationContext(null);

    private PeaceOverlayViewModel Create()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        return new(_service, _time, _sound, new GuestMoustacheState(), _ => 0);
    }

    private static PeaceSettings PeaceConfig(Action<PeaceSettings>? configure = null)
    {
        var s = new PeaceSettings { HoldMilliseconds = 1000, CooldownSeconds = 0 };
        configure?.Invoke(s);
        return s;
    }

    private static HeartSettings HeartConfig(Action<HeartSettings>? configure = null)
    {
        var s = new HeartSettings { Enabled = true, HoldMilliseconds = 1000 };
        configure?.Invoke(s);
        return s;
    }

    private static readonly IReadOnlyList<HandLandmarks> HeartHands = HeartHandFixtures.Classic(cx: 0.5, cy: 0.6);
    private static readonly FaceLandmarks Face = PeaceHandFixtures.Face(0.5, 0.3, 0.12);

    private void Run(double seconds, IReadOnlyList<HandLandmarks> hands, IReadOnlyList<FaceLandmarks>? faces = null)
    {
        for (var i = 0; i < (int)Math.Round(seconds * 10); i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            if (!_service.Last.IsDisposed)
                _service.Last.RaiseUpdated(faces ?? [Face], hands);
        }
    }

    [Fact]
    public void Heart_Fires_OnceInWatching_AfterTheHoldTime()
    {
        using var vm = Create();
        var triggers = new List<HeartTrigger>();
        vm.HeartTriggered += (_, t) => triggers.Add(t);
        vm.SetMode(PeaceMode.Watching, PeaceConfig(), false, heart: HeartConfig());

        Run(0.7, HeartHands);
        Assert.Empty(triggers);
        Run(1.0, HeartHands);

        var trigger = Assert.Single(triggers);
        Assert.Equal(HeartAction.Kisscam, trigger.Action);
        Assert.Equal(HeartShape.Classic, trigger.Shape);
        Assert.False(trigger.HeartBox.IsEmpty);

        // Gehalten bleibt es bei einer Auslösung.
        Run(2.0, HeartHands);
        Assert.Single(triggers);
    }

    [Fact]
    public void SetMode_NeverFiresTheHeart_EvenWhenTheNewHoldTimeIsAlreadyReached()
    {
        using var vm = Create();
        var triggers = new List<HeartTrigger>();
        vm.HeartTriggered += (_, t) => triggers.Add(t);
        vm.SetMode(PeaceMode.Watching, PeaceConfig(), false, heart: HeartConfig());
        Run(0.7, HeartHands);
        Assert.Empty(triggers);

        // Kürzere Haltedauer, die schon erreicht ist: Das Umstellen löst nie synchron einen Durchgang aus (der Aufrufer wäre mitten
        // in SetMode), erst das nächste Update.
        vm.SetMode(PeaceMode.Watching, PeaceConfig(), false, heart: HeartConfig(h => h.HoldMilliseconds = 300));

        Assert.Empty(triggers);
        Run(0.1, HeartHands);
        Assert.Single(triggers);
    }

    [Fact]
    public void SetMode_ReentrantOffDuringConfigure_IsNotOverwrittenByTheOuterCall()
    {
        using var vm = Create();
        vm.SetMode(PeaceMode.Watching, PeaceConfig(), false, heart: HeartConfig());
        Run(0.3, HeartHands);
        Assert.NotNull(vm.Heart);
        var nested = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PeaceOverlayViewModel.Heart) || nested)
                return;
            nested = true;
            vm.SetMode(PeaceMode.Off, PeaceConfig(), false);
        };

        // Das Ausschalten der Herzerkennung leert das Herz schon beim Konfigurieren: Ein Beobachter schaltet verschachtelt ab.
        vm.SetMode(PeaceMode.Watching, PeaceConfig(), false, heart: HeartConfig(h => h.Enabled = false));

        Assert.True(nested);
        Assert.Equal(PeaceMode.Off, vm.Mode);
        Assert.True(_service.Last.IsDisposed); // der veraltete äußere Aufruf hat die Anforderung nicht wieder geöffnet
    }

    [Fact]
    public void Heart_DoesNotFire_InFollowMoustache_OrWhenDisabled_OrWithoutShapes()
    {
        var triggers = new List<HeartTrigger>();

        // Follow: Hände nur wegen des Mittelfingers angefordert, das Herz sieht sie, löst aber nie aus.
        using (var vm = Create())
        {
            vm.HeartTriggered += (_, t) => triggers.Add(t);
            vm.SetMode(PeaceMode.FollowMoustache, PeaceConfig(), false, new MiddleFingerSettings(), MiddleFingerTargets.SelfDestruct, HeartConfig());
            Assert.True(vm.MiddleFingerWatching);
            Run(2.0, HeartHands);
        }
        Assert.Empty(triggers);

        using (var vm = Create())
        {
            vm.HeartTriggered += (_, t) => triggers.Add(t);
            vm.SetMode(PeaceMode.Watching, PeaceConfig(), false, heart: HeartConfig(h => h.Enabled = false));
            Run(2.0, HeartHands);
            vm.SetMode(PeaceMode.Watching, PeaceConfig(), false, heart: null);
            Run(2.0, HeartHands);
        }
        Assert.Empty(triggers);

        using (var vm = Create())
        {
            vm.HeartTriggered += (_, t) => triggers.Add(t);
            vm.SetMode(PeaceMode.Watching, PeaceConfig(), false, heart: HeartConfig(h =>
            {
                h.ShapeClassic = false;
                h.ShapeRoof = false;
            }));
            Run(2.0, HeartHands);
        }
        Assert.Empty(triggers);
    }

    [Fact]
    public void PeaceGesturesOff_PeaceDoesNotFire_ButTheHeartDoes()
    {
        using var vm = Create();
        var peaceActions = new List<PeaceTrigger>();
        var hearts = new List<HeartTrigger>();
        vm.ActionRequested += (_, t) => peaceActions.Add(t);
        vm.HeartTriggered += (_, t) => hearts.Add(t);
        vm.SetMode(PeaceMode.Watching, PeaceConfig(), false, heart: HeartConfig(), peaceGestures: false);
        Assert.Equal(GestureNeeds.Faces | GestureNeeds.Hands, _service.Last.Options.Needs);

        Run(2.0, [PeaceHandFixtures.Hand(HandPose.Peace)]);
        Assert.Empty(peaceActions);
        Assert.False(vm.PeaceDetectedNow);

        Run(1.5, HeartHands);
        Assert.Single(hearts);
        Assert.Empty(peaceActions);
    }

    [Fact]
    public void PeaceGesturesOn_StillFirePeace()
    {
        using var vm = Create();
        var peaceActions = new List<PeaceTrigger>();
        vm.ActionRequested += (_, t) => peaceActions.Add(t);
        vm.SetMode(PeaceMode.Watching, PeaceConfig(), false, heart: HeartConfig());

        Run(1.5, [PeaceHandFixtures.Hand(HandPose.Peace)]);

        Assert.Single(peaceActions);
    }

    [Fact]
    public void Request_AsksForPairedHands_OnlyWithHeart_AndUsesTheLowestMinHandScore()
    {
        using var vm = Create();
        var peace = PeaceConfig(p => p.SensitivityPercent = 20);
        var heart = HeartConfig(h => h.SensitivityPercent = 90);
        Assert.NotEqual(peace.MinHandScore, heart.MinHandScore);

        vm.SetMode(PeaceMode.Watching, peace, false, heart: heart);
        var options = _service.Last.Options;
        Assert.True(options.PairedHands);
        Assert.Equal(Math.Min(peace.MinHandScore, heart.MinHandScore), options.MinHandScore);
        Assert.Equal(peace.AnalysesPerSecond, options.AnalysesPerSecond);

        // Ohne Herz wie bisher.
        vm.SetMode(PeaceMode.Watching, peace, false);
        options = _service.Last.Options;
        Assert.False(options.PairedHands);
        Assert.Equal(peace.MinHandScore, options.MinHandScore);
        Assert.Equal(GestureNeeds.Faces | GestureNeeds.Hands, options.Needs);
        Assert.Single(_service.Requests); // immer dieselbe Anforderung
    }

    [Fact]
    public void Request_WithHeartOnly_NeedsHands_WithoutAnyFunction_OnlyFaces()
    {
        using var vm = Create();
        vm.SetMode(PeaceMode.Watching, PeaceConfig(), false, heart: HeartConfig(h => h.SensitivityPercent = 70), peaceGestures: false);
        var heartOnly = _service.Last.Options;
        Assert.Equal(GestureNeeds.Faces | GestureNeeds.Hands, heartOnly.Needs);
        Assert.Equal(HeartConfig(h => h.SensitivityPercent = 70).MinHandScore, heartOnly.MinHandScore);

        vm.SetMode(PeaceMode.Watching, PeaceConfig(), false, heart: HeartConfig(h => h.Enabled = false), peaceGestures: false);
        Assert.Equal(GestureNeeds.Faces, _service.Last.Options.Needs);
        Assert.False(_service.Last.Options.PairedHands);
    }

    [Fact]
    public void OffAndDeactivate_ResetTheHeart_AndKeepItDisarmedUntilItWasReleased()
    {
        using var vm = Create();
        var triggers = new List<HeartTrigger>();
        vm.HeartTriggered += (_, t) => triggers.Add(t);
        var heart = HeartConfig();
        vm.SetMode(PeaceMode.Watching, PeaceConfig(), false, heart: heart);

        Run(0.5, HeartHands);
        Assert.NotNull(vm.Heart);
        Assert.True(vm.HeartDetectedNow);
        Assert.Equal(HeartShape.Classic, vm.HeartDetectedShape);

        vm.SetMode(PeaceMode.Off, PeaceConfig(), false, heart: heart);

        Assert.Null(vm.Heart);
        Assert.False(vm.HeartDetectedNow);
        Assert.Null(vm.HeartDetectedShape);
        Assert.Equal(0, _service.OpenRequests);

        // Wieder an: Die Haltezeit beginnt von vorn (0,5 s Halten davor zählen nicht).
        vm.SetMode(PeaceMode.Watching, PeaceConfig(), false, heart: heart);
        Run(0.7, HeartHands);
        Assert.Empty(triggers);
        Run(1.0, HeartHands);
        Assert.Single(triggers);

        // Nach der Auslösung: aus und wieder an mit noch gehaltenem Herz löst nicht erneut aus.
        vm.SetMode(PeaceMode.Off, PeaceConfig(), false, heart: heart);
        vm.SetMode(PeaceMode.Watching, PeaceConfig(), false, heart: heart);
        Run(3.0, HeartHands);
        Assert.Single(triggers);

        // Erst nach dem Lösen ist die Erkennung wieder scharf.
        Run(1.0, []);
        Run(1.5, HeartHands);
        Assert.Equal(2, triggers.Count);

        vm.Dispose();
        Assert.Null(vm.Heart);
    }

    [Fact]
    public void HeartProperty_ReportsProgress_AndRaisesPropertyChanged()
    {
        using var vm = Create();
        vm.SetMode(PeaceMode.Watching, PeaceConfig(), false, heart: HeartConfig());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Null(vm.Heart);

        Run(0.3, HeartHands);
        var early = vm.Heart;
        Assert.NotNull(early);
        Assert.Contains(nameof(PeaceOverlayViewModel.Heart), changed);
        Assert.Contains(nameof(PeaceOverlayViewModel.HeartDetectedNow), changed);

        Run(0.4, HeartHands);
        Assert.True(vm.Heart!.Progress > early!.Progress);
        Assert.InRange(vm.Heart.Progress, 0.0, 1.0);

        // Herz weg: nach der Aussetzer-Toleranz verschwindet die Anzeige.
        Run(1.0, []);
        Assert.Null(vm.Heart);
        Assert.False(vm.HeartDetectedNow);
    }

    [Fact]
    public void HeartTrigger_ExceptionInHandler_IsLoggedNotThrown()
    {
        using var vm = Create();
        vm.HeartTriggered += (_, _) => throw new InvalidOperationException("Test");
        vm.SetMode(PeaceMode.Watching, PeaceConfig(), false, heart: HeartConfig());

        var ex = Record.Exception(() => Run(2.0, HeartHands));

        Assert.Null(ex);
    }

    [Fact]
    public void HeartTrigger_CarriesTheFaceOfTheHeartPerson()
    {
        using var vm = Create();
        HeartTrigger? trigger = null;
        vm.HeartTriggered += (_, t) => trigger = t;
        vm.SetMode(PeaceMode.Watching, PeaceConfig(), false, heart: HeartConfig());

        Run(2.0, HeartHands);

        Assert.NotNull(trigger);
        Assert.NotEmpty(trigger!.FaceBoxes);
    }
}
