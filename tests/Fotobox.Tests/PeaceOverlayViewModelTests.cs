using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Fotobox.ViewModels.Booth;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Ansichtsmodell der Peaceerkennung: Moduswechsel, Updates, Aktionen, Bart-Zustand, Töne.</summary>
public sealed class PeaceOverlayViewModelTests
{
    private readonly FakeGestureService _service = new();
    private readonly FakeSoundPlayer _sound = new();
    private readonly TrackingTimeProvider _time = new();
    private readonly GuestMoustacheState _guest = new();

    public PeaceOverlayViewModelTests() => NoContext();

    // xunit setzt je Test einen eigenen Kontext; die Timer-Aufrufe sollen aber synchron im Test laufen.
    private static void NoContext() => SynchronizationContext.SetSynchronizationContext(null);

    private PeaceOverlayViewModel Create(GuestMoustacheState? guest = null, Func<int, int>? random = null)
    {
        NoContext(); // der Kontext des Testaufrufs würde Timer-Ticks auf einen anderen Thread verlegen
        return new(_service, _time, _sound, guest, random);
    }

    private static PeaceSettings Settings(Action<PeaceSettings>? configure = null)
    {
        var s = new PeaceSettings { HoldMilliseconds = 1000, CooldownSeconds = 0, SelfDestructSoundEnabled = true };
        configure?.Invoke(s);
        return s;
    }

    private static readonly HandLandmarks Peace = PeaceHandFixtures.Hand(HandPose.Peace);
    private static readonly FaceLandmarks Face = PeaceHandFixtures.Face(0.5, 0.3, 0.12);

    private void Run(double seconds, IReadOnlyList<FaceLandmarks>? faces = null, IReadOnlyList<HandLandmarks>? hands = null)
    {
        var steps = (int)Math.Round(seconds * 10);
        for (var i = 0; i < steps; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            if ((faces is not null || hands is not null) && !_service.Last.IsDisposed)
                _service.Last.RaiseUpdated(faces ?? [], hands ?? []);
        }
    }

    [Fact]
    public void Watching_OpensOneRequest_WithFacesAndHands_AndStartsTheTimer()
    {
        using var vm = Create();
        var settings = Settings(s => { s.AnalysesPerSecond = 7; s.SensitivityPercent = 80; });
        vm.SetMode(PeaceMode.Watching, settings, mirror: false);

        Assert.Equal(PeaceMode.Watching, vm.Mode);
        Assert.Equal(1, _service.OpenRequests);
        var options = _service.Last.Options;
        Assert.Equal(GestureNeeds.Faces | GestureNeeds.Hands, options.Needs);
        Assert.Equal(7, options.AnalysesPerSecond);
        Assert.Equal(0.6, options.MinFaceScore);
        Assert.Equal(settings.MinHandScore, options.MinHandScore);
        Assert.Equal(1, _time.ActiveTimers);
    }

    [Fact]
    public void ModeSwitch_OnlyUpdatesTheRequest_NoRebuild()
    {
        using var vm = Create();
        vm.SetMode(PeaceMode.Watching, Settings(), false);
        var request = _service.Last;

        vm.SetMode(PeaceMode.FollowMoustache, Settings(), false);
        Assert.Same(request, _service.Last);
        Assert.Single(_service.Requests);
        var follow = Assert.Single(request.Updates);
        Assert.Equal(GestureNeeds.Faces, follow.Needs);
        Assert.Equal(10, follow.AnalysesPerSecond);
        Assert.False(request.IsDisposed);

        vm.SetMode(PeaceMode.Watching, Settings(), false);
        Assert.Equal(2, request.Updates.Count);
        Assert.Equal(GestureNeeds.Faces | GestureNeeds.Hands, request.Updates[1].Needs);
        Assert.Single(_service.Requests);
        Assert.Equal(1, _time.TimersCreated);
    }

    [Fact]
    public void SameModeAndSettings_DoNotTouchTheRequestAgain()
    {
        using var vm = Create();
        vm.SetMode(PeaceMode.Watching, Settings(), false);
        vm.SetMode(PeaceMode.Watching, Settings(), false);
        Assert.Empty(_service.Last.Updates);
        Assert.Single(_service.Requests);
    }

    [Fact]
    public void ChangedSettings_UpdateTheRequest()
    {
        using var vm = Create();
        vm.SetMode(PeaceMode.Watching, Settings(), false);
        vm.SetMode(PeaceMode.Watching, Settings(s => s.AnalysesPerSecond = 9), false);
        Assert.Equal(9, Assert.Single(_service.Last.Updates).AnalysesPerSecond);
    }

    [Fact]
    public void Off_ClosesTheRequest_StopsTheTimer_AndClearsEverything()
    {
        using var vm = Create(_guest);
        vm.SetMode(PeaceMode.Watching, Settings(), false);
        _service.Last.RaiseUpdated([Face], [Peace]);
        vm.StartMoustache(new PeaceTrigger(PeaceAction.Moustache, 1, 1, new NormPoint(0.5, 0.5)));
        Assert.True(vm.IsShown);

        var request = _service.Last;
        vm.SetMode(PeaceMode.Off, Settings(), false);
        Assert.True(request.IsDisposed);
        Assert.Equal(0, _time.ActiveTimers);
        Assert.Equal(PeaceMode.Off, vm.Mode);
        Assert.Empty(vm.Hands);
        Assert.Empty(vm.Moustaches);
        Assert.False(vm.IsShown);
        Assert.False(vm.MoustacheActive);
        Assert.Null(_guest.Current);

        request.RaiseUpdated([Face], [Peace]); // nach dem Schließen wirkt nichts mehr
        Assert.Empty(vm.Hands);
    }

    [Fact]
    public void Off_AfterOff_ReopensAFreshRequest()
    {
        using var vm = Create();
        vm.SetMode(PeaceMode.Watching, Settings(), false);
        vm.SetMode(PeaceMode.Off, Settings(), false);
        vm.SetMode(PeaceMode.Watching, Settings(), false);
        Assert.Equal(2, _service.Requests.Count);
        Assert.Equal(1, _service.OpenRequests);
        Assert.Equal(1, _time.ActiveTimers);
    }

    [Fact]
    public void Update_FillsHands_RaisesPropertyChanges_AndTracksImageSize()
    {
        using var vm = Create();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.SetMode(PeaceMode.Watching, Settings(), false);

        _service.Last.RaiseUpdated([Face], [Peace], 1920, 1080);

        var hand = Assert.Single(vm.Hands);
        Assert.True(hand.IsPeace);
        Assert.True(vm.PeaceDetectedNow);
        Assert.True(vm.IsShown);
        Assert.Equal(1920, vm.ImageWidth);
        Assert.Equal(1080, vm.ImageHeight);
        Assert.Contains(nameof(PeaceOverlayViewModel.IsShown), changed);
        Assert.Contains(nameof(PeaceOverlayViewModel.Hands), changed);
        Assert.Contains(nameof(PeaceOverlayViewModel.PeaceDetectedNow), changed);

        changed.Clear();
        Run(1, [], []); // Hände weg, Zeit läuft: nach 400 ms verschwindet die Hand
        Assert.Empty(vm.Hands);
        Assert.False(vm.PeaceDetectedNow);
        Assert.False(vm.IsShown);
        Assert.Contains(nameof(PeaceOverlayViewModel.IsShown), changed);
    }

    [Fact]
    public void Status_IsTaken_AndMoustacheAvailabilityFollowsTheFaceReason()
    {
        using var vm = Create();
        vm.SetMode(PeaceMode.Watching, Settings(), false);
        Assert.True(vm.MoustacheAvailable);

        var status = new GestureStatus(false, true, "Modell fehlt.", null, 3);
        _service.Last.RaiseStatus(status);
        Assert.Equal(status, vm.Status);
        Assert.False(vm.MoustacheAvailable);

        _service.Last.RaiseStatus(new GestureStatus(true, true, null, null, 5));
        Assert.True(vm.MoustacheAvailable);
    }

    [Fact]
    public void ActionRequested_IsRaised_AfterTheHoldTime()
    {
        using var vm = Create();
        var actions = new List<PeaceTrigger>();
        vm.ActionRequested += (_, t) => actions.Add(t);
        vm.SetMode(PeaceMode.Watching, Settings(), false);

        _service.Last.RaiseUpdated([Face], [Peace]);
        Run(0.8, [Face], [Peace]);
        Assert.Empty(actions);
        Run(0.4, [Face], [Peace]);
        Assert.Equal(PeaceAction.SelfDestruct, Assert.Single(actions).Action);
        Assert.NotNull(actions[0].FaceTrackId);

        Run(3, [Face], [Peace]);
        Assert.Single(actions);
    }

    [Fact]
    public void NoAction_InFollowMode_EvenWithPeaceShown()
    {
        using var vm = Create();
        var actions = new List<PeaceTrigger>();
        vm.ActionRequested += (_, t) => actions.Add(t);
        vm.SetMode(PeaceMode.FollowMoustache, Settings(), false);

        _service.Last.RaiseUpdated([Face], [Peace]);
        Run(3, [Face], [Peace]);
        Assert.Empty(actions);
    }

    [Fact]
    public void MoustacheUnavailable_FiltersTheActionOut()
    {
        using var vm = Create(random: _ => 1);
        var actions = new List<PeaceTrigger>();
        vm.ActionRequested += (_, t) => actions.Add(t);
        vm.SetMode(PeaceMode.Watching, Settings(s => s.ActionMoustache = true), false);
        _service.Last.RaiseStatus(new GestureStatus(false, true, "Modell fehlt.", null, 3));

        Run(0.1, [], [Peace]);
        Run(1.2, [], [Peace]);
        Assert.Equal(PeaceAction.SelfDestruct, Assert.Single(actions).Action);
    }

    [Fact]
    public void StartMoustache_ShowsBeardsAndPublishesThePlanToTheGuestState()
    {
        using var vm = Create(_guest);
        vm.SetMode(PeaceMode.Watching, Settings(s => s.ActionMoustache = true), false);
        _service.Last.RaiseUpdated([Face], []);

        vm.StartMoustache(new PeaceTrigger(PeaceAction.Moustache, 5, 1, new NormPoint(0.5, 0.6)));
        Assert.True(vm.MoustacheActive);
        var view = Assert.Single(vm.Moustaches);
        Assert.NotEqual(MoustacheStyle.Random, view.Style);
        Assert.True(vm.IsShown);

        Assert.NotNull(_guest.Current);
        Assert.Equal(MoustacheTargets.Everyone, _guest.Current!.Targets);
        Assert.Single(_guest.Current.Tracks);

        vm.OnPhotoCompleted();
        Assert.False(vm.MoustacheActive);
        Assert.Empty(vm.Moustaches);
        Assert.Null(_guest.Current);
    }

    [Fact]
    public void Moustache_FollowsTheFace_AndSurvivesModeChange()
    {
        using var vm = Create(_guest);
        vm.SetMode(PeaceMode.Watching, Settings(), false);
        _service.Last.RaiseUpdated([PeaceHandFixtures.Face(0.4, 0.3, 0.12)], []);
        vm.StartMoustache(new PeaceTrigger(PeaceAction.Moustache, 1, 1, new NormPoint(0.5, 0.6)));
        var before = vm.Moustaches.Single().Pose.Center.X;

        vm.SetMode(PeaceMode.FollowMoustache, Settings(), false);
        Assert.Single(_service.Requests);
        _time.Advance(TimeSpan.FromMilliseconds(100));
        for (var i = 1; i <= 12; i++)
        {
            _service.Last.RaiseUpdated([PeaceHandFixtures.Face(0.4 + 0.02 * i, 0.3, 0.12)], []);
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.True(vm.MoustacheActive);
        Assert.True(vm.Moustaches.Single().Pose.Center.X > before + 0.15);
    }

    [Fact]
    public void FixedDuration_IsPausedInFollowMode()
    {
        using var vm = Create();
        var settings = Settings(s => { s.MoustacheVisibility = MoustacheVisibility.FixedDuration; s.MoustacheDurationSeconds = 5; });
        vm.SetMode(PeaceMode.Watching, settings, false);
        _service.Last.RaiseUpdated([Face], []);
        vm.StartMoustache(new PeaceTrigger(PeaceAction.Moustache, 1, 1, new NormPoint(0.5, 0.6)));

        vm.SetMode(PeaceMode.FollowMoustache, settings, false);
        Run(12, [Face], []); // länger als die Dauer, aber pausiert
        Assert.True(vm.MoustacheActive);

        vm.SetMode(PeaceMode.Watching, settings, false);
        Run(4, [Face], []);
        Assert.True(vm.MoustacheActive);
        Run(1.5, [Face], []);
        Assert.False(vm.MoustacheActive);
    }

    [Fact]
    public void TestMoustache_WithoutFaces_UsesAPlaceholder_NeverTheGuestState_AndEnds()
    {
        using var vm = Create(_guest);
        vm.SetMode(PeaceMode.Watching, Settings(), false);

        vm.StartTestMoustache();
        var view = Assert.Single(vm.Moustaches);
        Assert.Equal(-1, view.FaceId);
        Assert.True(vm.MoustacheActive);
        Assert.Null(_guest.Current);

        Run(5);
        Assert.Single(vm.Moustaches); // Platzhalter bleibt stehen
        Assert.Null(_guest.Current);

        Run(4);
        Assert.False(vm.MoustacheActive);
        Assert.Empty(vm.Moustaches);
        Assert.Null(_guest.Current);
    }

    [Fact]
    public void TestMoustache_WithRecognisedFace_UsesIt()
    {
        using var vm = Create(_guest);
        vm.SetMode(PeaceMode.Watching, Settings(s => s.MoustacheStyle = MoustacheStyle.Brush), false);
        _service.Last.RaiseUpdated([Face], []);

        vm.StartTestMoustache();
        var view = Assert.Single(vm.Moustaches);
        Assert.NotEqual(-1, view.FaceId);
        Assert.Equal(MoustacheStyle.Brush, view.Style);
        Assert.Null(_guest.Current);

        vm.OnPhotoCompleted(); // ein Test endet nicht durch ein Foto
        Assert.True(vm.MoustacheActive);
    }

    [Fact]
    public void TestMoustache_WorksWhileOff_AndReleasesTheTimerAfterwards()
    {
        using var vm = Create(_guest);
        vm.StartTestMoustache();
        Assert.Single(vm.Moustaches);
        Assert.Equal(1, _time.ActiveTimers);

        Run(9);
        Assert.Empty(vm.Moustaches);
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public void RealMoustache_AfterATest_DoesNotInheritThePlaceholder()
    {
        using var vm = Create(_guest);
        vm.SetMode(PeaceMode.Watching, Settings(), false);
        vm.StartTestMoustache();
        Run(0.5);

        vm.StartMoustache(new PeaceTrigger(PeaceAction.Moustache, 1, null, new NormPoint(0.5, 0.6)));
        Assert.Empty(vm.Moustaches);
        _service.Last.RaiseUpdated([Face], []);
        Assert.Single(vm.Moustaches);
        Assert.NotEqual(-1, vm.Moustaches[0].FaceId);
        Assert.NotNull(_guest.Current);
    }

    [Fact]
    public void SelfDestructSounds_OnlyWithSoundEnabled()
    {
        using var vm = Create();
        vm.SetMode(PeaceMode.Watching, Settings(s => s.SelfDestructSoundEnabled = false), false);
        vm.PlaySelfDestructTick(5);
        vm.PlaySelfDestructTick(2);
        vm.PlayExplosion();
        Assert.Empty(_sound.Played);

        vm.SetMode(PeaceMode.Watching, Settings(s => s.SelfDestructSoundEnabled = true), false);
        vm.PlaySelfDestructTick(10);
        vm.PlaySelfDestructTick(4);
        vm.PlaySelfDestructTick(3);
        vm.PlaySelfDestructTick(1);
        vm.PlayExplosion();
        Assert.Equal(
            [FunSoundCue.SelfDestructTick, FunSoundCue.SelfDestructTick, FunSoundCue.SelfDestructTickFast, FunSoundCue.SelfDestructTickFast, FunSoundCue.Explosion],
            _sound.Played);
    }

    [Fact]
    public void StopSounds_StopsThePlayer()
    {
        using var vm = Create();
        vm.StopSounds();
        Assert.Equal(1, _sound.Stops);
    }

    [Fact]
    public void Dispose_ClosesTheRequest_AndIgnoresLaterCalls()
    {
        var vm = Create(_guest);
        vm.SetMode(PeaceMode.Watching, Settings(), false);
        var request = _service.Last;
        vm.Dispose();

        Assert.True(request.IsDisposed);
        Assert.Equal(0, _time.ActiveTimers);
        vm.SetMode(PeaceMode.Watching, Settings(), false);
        Assert.Single(_service.Requests);
        Assert.Equal(0, _time.ActiveTimers);
        vm.Dispose();
    }

    [Fact]
    public void Inert_WorksWithoutErrors()
    {
        using var vm = PeaceOverlayViewModel.CreateInert();
        vm.SetMode(PeaceMode.Watching, new PeaceSettings(), false);
        vm.SetMode(PeaceMode.FollowMoustache, new PeaceSettings(), false);
        vm.PlaySelfDestructTick(3);
        vm.PlayExplosion();
        vm.StopSounds();
        vm.OnPhotoCompleted();
        Assert.False(vm.IsShown);
        vm.SetMode(PeaceMode.Off, new PeaceSettings(), false);
    }

    // ---- schneller Gesichtsmodus und Bildzeit

    [Fact]
    public void VisibleMoustache_SwitchesToFastFaceMode_AndBackWhenItEnds()
    {
        using var vm = Create();
        var settings = Settings(s => s.AnalysesPerSecond = 5);
        vm.SetMode(PeaceMode.Watching, settings, false);
        _service.Last.RaiseUpdated([Face], []);
        var request = _service.Last;

        vm.StartMoustache(new PeaceTrigger(PeaceAction.Moustache, 1, 1, new NormPoint(0.5, 0.6)));
        var fast = Assert.Single(request.Updates);
        Assert.Equal(GestureNeeds.Faces | GestureNeeds.Hands, fast.Needs);
        Assert.Equal(10, fast.AnalysesPerSecond);
        Assert.Equal(2, fast.HandCycleDivisor); // effektive Handrate 10 / 2 = 5/s, wie eingestellt

        vm.OnPhotoCompleted();
        Assert.Equal(2, request.Updates.Count);
        Assert.Equal(1, request.Updates[1].HandCycleDivisor);
        Assert.Equal(5, request.Updates[1].AnalysesPerSecond);
    }

    [Fact]
    public void FastFaceMode_KeepsTheConfiguredHandRate_ForHigherSettings()
    {
        using var vm = Create();
        vm.SetMode(PeaceMode.Watching, Settings(s => s.AnalysesPerSecond = 8), false);
        _service.Last.RaiseUpdated([Face], []);

        vm.StartMoustache(new PeaceTrigger(PeaceAction.Moustache, 1, 1, new NormPoint(0.5, 0.6)));

        var fast = Assert.Single(_service.Last.Updates);
        Assert.Equal(16, fast.AnalysesPerSecond);
        Assert.Equal(8, fast.AnalysesPerSecond / fast.HandCycleDivisor);
    }

    [Fact]
    public void FacesOnlyMode_StaysFacesOnlyEvenWithAMoustache()
    {
        using var vm = Create();
        vm.SetMode(PeaceMode.FollowMoustache, Settings(), false);
        _service.Last.RaiseUpdated([Face], []);
        vm.StartMoustache(new PeaceTrigger(PeaceAction.Moustache, 1, 1, new NormPoint(0.5, 0.6)));

        Assert.Empty(_service.Last.Updates);
        Assert.Equal(GestureNeeds.Faces, _service.Last.Options.Needs);
    }

    [Fact]
    public void FacesOnlyUpdate_DoesNotAffectTheHandsOrTheHold()
    {
        using var vm = Create();
        var actions = new List<PeaceTrigger>();
        vm.ActionRequested += (_, t) => actions.Add(t);
        vm.SetMode(PeaceMode.Watching, Settings(), false);
        var status = _service.Last.Status;

        // Hände nur alle 200 ms, dazwischen reine Gesichts-Updates mit leerer Handliste.
        for (var i = 0; i < 14; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            var withHands = i % 2 == 0;
            _service.Last.RaiseUpdated(new GestureUpdate([Face], withHands ? [Peace] : [], 1280, 720, status,
                HandsIncluded: withHands, FacesFresh: true, FrameTime: _time.GetUtcNow()));
        }

        Assert.Single(vm.Hands);
        Assert.Single(actions); // Peace wurde über 1 s gehalten
    }

    [Fact]
    public void RepeatedFaces_AreOnlyUsedToMatchHands_NotTrackedAgain()
    {
        using var vm = Create();
        vm.SetMode(PeaceMode.Watching, Settings(), false);
        var status = _service.Last.Status;
        var handNearFace = PeaceHandFixtures.Hand(HandPose.Peace, cx: 0.5, cy: 0.45);
        _service.Last.RaiseUpdated(new GestureUpdate([Face], [], 1280, 720, status, HandsIncluded: false, FacesFresh: true, FrameTime: _time.GetUtcNow()));
        Assert.Equal(1, vm.FaceCount);

        // Das Hand-Update wiederholt die Gesichter nur (hier bewusst leer): Zuordnung über die zuvor verfolgten Gesichter.
        _service.Last.RaiseUpdated(new GestureUpdate([], [handNearFace], 1280, 720, status, HandsIncluded: true, FacesFresh: false, FrameTime: _time.GetUtcNow()));

        Assert.Equal(1, vm.FaceCount);
        Assert.NotNull(Assert.Single(vm.Hands).FaceId);
    }

    [Fact]
    public void FrameTime_ShiftsTheShownBeardAheadOfTheFilteredPose()
    {
        using var vm = Create(_guest);
        vm.SetMode(PeaceMode.Watching, Settings(), false);
        var status = _service.Last.Status;
        _service.Last.RaiseUpdated([PeaceHandFixtures.Face(0.4, 0.3, 0.12)], []);
        vm.StartMoustache(new PeaceTrigger(PeaceAction.Moustache, 1, 1, new NormPoint(0.5, 0.6)));

        for (var i = 1; i <= 10; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            _service.Last.RaiseUpdated(new GestureUpdate([PeaceHandFixtures.Face(0.4 + 0.01 * i, 0.3, 0.12)], [], 1280, 720, status,
                HandsIncluded: false, FacesFresh: true, FrameTime: _time.GetUtcNow() - TimeSpan.FromMilliseconds(100)));
        }

        var shown = vm.Moustaches.Single().Pose.Center.X;
        var filtered = _guest.Current!.Tracks.Single().Pose.Center.X; // Foto-Pfad: ohne Vorhersage
        Assert.True(shown > filtered);
    }
}
