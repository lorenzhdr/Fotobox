using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.ViewModels.Booth;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Ansichtsmodell der Zielerfassung: Aktivierung, Einstellungswechsel, Tick-Timer, Ton, Simulation.</summary>
public sealed class FunOverlayViewModelTests
{
    private readonly FakeDetectionService _detection = new();
    private readonly FakeSoundPlayer _sound = new();
    private readonly TrackingTimeProvider _time = new();
    private readonly FunOverlayViewModel _vm;

    public FunOverlayViewModelTests()
    {
        NoContext();
        _vm = new FunOverlayViewModel(_detection, _sound, _time);
    }

    // xunit setzt je Test einen eigenen Kontext; die Timer-Aufrufe sollen aber synchron im Test laufen.
    private static void NoContext() => SynchronizationContext.SetSynchronizationContext(null);

    private static FunSettings Settings(Action<FunSettings>? configure = null)
    {
        var settings = new FunSettings
        {
            Enabled = true,
            SoundEnabled = true,
            Scope = TargetScope.FullBody,
            DwellMilliseconds = 0,
            MinSizePercent = 5,
            TextSeconds = 6,
            CooldownSeconds = 20,
        };
        configure?.Invoke(settings);
        return settings;
    }

    private static Detection Person(double x = 0.1) => new(new NormRect(x, 0.1, 0.12, 0.7), 0.9, DetectionKind.Body);

    /// <summary>Läuft in 100-ms-Schritten (Timer-Tick der Engine); pro Schritt kommt, wenn angegeben, eine Analyse.</summary>
    private void Run(double seconds, params Detection[] people)
    {
        var steps = (int)Math.Round(seconds * 10);
        for (var i = 0; i < steps; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            if (people.Length > 0 && !_detection.Last.IsDisposed)
                _detection.Last.RaiseUpdated(people);
        }
    }

    [Fact]
    public void SetActive_OpensOneRequest_AndStartsTheTickTimer()
    {
        NoContext();
        _vm.SetActive(true, Settings(), mirror: false);

        Assert.True(_vm.IsActive);
        Assert.True(_vm.IsShown);
        Assert.Single(_detection.Requests);
        Assert.Equal(1, _time.ActiveTimers);
    }

    [Fact]
    public void SetActive_SameSettingsAgain_DoesNothing()
    {
        NoContext();
        _vm.SetActive(true, Settings(), mirror: false);
        _vm.SetActive(true, Settings(), mirror: false);
        _vm.SetActive(true, Settings(), mirror: false);

        Assert.Single(_detection.Requests);
        Assert.Empty(_detection.Last.Updates);
        Assert.Equal(1, _time.TimersCreated);
    }

    [Fact]
    public void SetActive_ChangedSettings_UpdatesTheRequest_AndKeepsTheTarget()
    {
        NoContext();
        _vm.SetActive(true, Settings(), mirror: false);
        Run(0.5, Person());
        Assert.Single(_vm.Snapshot.Targets);

        _vm.SetActive(true, Settings(s => s.Color = TargetColor.Cyan), mirror: false);

        Assert.Single(_detection.Last.Updates);
        Assert.Equal(TargetColor.Cyan, _detection.Last.Settings.Color);
        Assert.Equal(TargetColor.Cyan, _vm.Color);
        Assert.Single(_vm.Snapshot.Targets); // kein Reset
        Assert.Single(_detection.Requests);
    }

    [Fact]
    public void SetActive_ScopeChange_ResetsTheEngine()
    {
        NoContext();
        _vm.SetActive(true, Settings(), mirror: false);
        Run(0.5, Person());
        Assert.Single(_vm.Snapshot.Targets);

        _vm.SetActive(true, Settings(s => s.Scope = TargetScope.Head), mirror: false);

        Assert.Empty(_vm.Snapshot.Targets);
        Assert.Equal(FunPhase.Watching, _vm.Snapshot.Phase);
        Assert.Single(_detection.Last.Updates);
    }

    [Fact]
    public void SetActive_MethodChange_ResetsTheEngine()
    {
        NoContext();
        _vm.SetActive(true, Settings(), mirror: false);
        Run(0.5, Person());
        Assert.Single(_vm.Snapshot.Targets);

        _vm.SetActive(true, Settings(s => s.Method = DetectionMethod.Face), mirror: false);

        Assert.Empty(_vm.Snapshot.Targets);
    }

    [Fact]
    public void SetActive_MirrorOnlyChange_ConfiguresWithoutUpdatingTheRequest()
    {
        NoContext();
        _vm.SetActive(true, Settings(), mirror: false);
        Run(0.5, Person());

        _vm.SetActive(true, Settings(), mirror: true);

        Assert.True(_vm.Mirror);
        Assert.Empty(_detection.Last.Updates);
        Assert.Single(_vm.Snapshot.Targets);
    }

    [Fact]
    public void Deactivate_DisposesRequest_ClearsSnapshot_StopsSound_AndStopsTimer()
    {
        NoContext();
        _vm.SetActive(true, Settings(), mirror: false);
        Run(0.5, Person());
        var request = _detection.Last;
        Assert.Single(_vm.Snapshot.Targets);

        _vm.SetActive(false, Settings(), mirror: false);

        Assert.False(_vm.IsActive);
        Assert.False(_vm.IsShown);
        Assert.True(request.IsDisposed);
        Assert.Equal(0, _detection.OpenRequests);
        Assert.Empty(_vm.Snapshot.Targets);
        Assert.False(_vm.TextVisible);
        Assert.Equal("", _vm.Text);
        Assert.Equal(1, _sound.Stops);
        Assert.Equal(0, _time.ActiveTimers);

        // Späte Ereignisse einer abgemeldeten Anforderung wirken nicht mehr.
        request.RaiseUpdated([Person()]);
        Run(1);
        Assert.Empty(_vm.Snapshot.Targets);
    }

    [Fact]
    public void Deactivate_WhenNeverActive_IsHarmless()
    {
        NoContext();
        _vm.SetActive(false, Settings(), mirror: false);

        Assert.Empty(_detection.Requests);
        Assert.Equal(0, _sound.Stops);
    }

    [Fact]
    public void Reactivation_OpensANewRequest_WithACleanEngine()
    {
        NoContext();
        _vm.SetActive(true, Settings(), mirror: false);
        Run(0.5, Person());
        _vm.SetActive(false, Settings(), mirror: false);

        _vm.SetActive(true, Settings(), mirror: false);

        Assert.Equal(2, _detection.Requests.Count);
        Assert.Equal(1, _detection.OpenRequests);
        Assert.Empty(_vm.Snapshot.Targets);
    }

    [Fact]
    public void Detections_BecomeTargets_RememberImageSize_AndPlayTheAcquireTone()
    {
        NoContext();
        _vm.SetActive(true, Settings(), mirror: false);
        var changed = new List<string?>();
        _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Run(0.2, Person());
        _detection.Last.RaiseUpdated([Person()], 1920, 1080);

        Assert.Equal(1920, _vm.ImageWidth);
        Assert.Equal(1080, _vm.ImageHeight);
        Assert.Contains(nameof(FunOverlayViewModel.ImageWidth), changed);
        Assert.Contains(nameof(FunOverlayViewModel.Snapshot), changed);
        var target = Assert.Single(_vm.Snapshot.Targets);
        Assert.Equal("TARGET 1", target.Label);
        Assert.Contains(FunSoundCue.Acquire, _sound.Played);
    }

    [Fact]
    public void SoundCues_AreSilent_WhenSoundIsDisabled()
    {
        NoContext();
        _vm.SetActive(true, Settings(s => s.SoundEnabled = false), mirror: false);

        _detection.Last.RaiseUpdated([Person()]);
        Run(2, Person());

        Assert.Empty(_sound.Played);
        Assert.True(_vm.TextVisible); // der Ablauf läuft trotzdem
    }

    [Fact]
    public void TickTimer_DrivesLockOnAndText_WithoutFurtherDetectionsBeyondPresence()
    {
        NoContext();
        _vm.SetActive(true, Settings(), mirror: false);
        var changed = new List<string?>();
        _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _detection.Last.RaiseUpdated([Person()]);
        Assert.True(_vm.Snapshot.Targets[0].IsLockingOn);
        Assert.False(_vm.TextVisible);

        Run(1.4, Person());

        Assert.True(_vm.TextVisible);
        Assert.Equal(FunTextFormatter.Resolve(Settings(), 1), _vm.Text);
        Assert.Contains(nameof(FunOverlayViewModel.TextVisible), changed);
        Assert.Contains(FunSoundCue.Lock, _sound.Played);
        Assert.Contains(FunSoundCue.Text, _sound.Played);

        // Text nach der eingestellten Dauer wieder weg.
        Run(6.2, Person());
        Assert.False(_vm.TextVisible);
        Assert.Equal("", _vm.Text);
    }

    [Fact]
    public void Simulate_WithoutRequest_RunsThroughAndStopsItsOwnTimer()
    {
        NoContext();
        var settings = Settings();
        _vm.Simulate(settings, mirror: false);

        Assert.Empty(_detection.Requests);
        Assert.True(_vm.IsShown);
        Assert.False(_vm.IsActive);
        Assert.Equal(1, _time.ActiveTimers);
        var target = Assert.Single(_vm.Snapshot.Targets);
        Assert.Equal(FunEngine.SimulatedTargetId, target.Id);
        Assert.Contains(FunSoundCue.Acquire, _sound.Played);

        Run(1.5);
        Assert.True(_vm.TextVisible);

        Run(7);
        Assert.Empty(_vm.Snapshot.Targets);
        Assert.False(_vm.TextVisible);
        Assert.False(_vm.IsShown);
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public void Simulate_WhileActive_KeepsTheRequest_AndIgnoresDetectionsUntilDone()
    {
        NoContext();
        _vm.SetActive(true, Settings(), mirror: false);

        _vm.Simulate();
        _detection.Last.RaiseUpdated([Person(0.6), Person(0.1)]);

        var target = Assert.Single(_vm.Snapshot.Targets);
        Assert.Equal(FunEngine.SimulatedTargetId, target.Id);
        Assert.False(_detection.Last.IsDisposed);

        Run(8);
        Assert.True(_vm.IsActive);
        Assert.Equal(1, _time.ActiveTimers); // der Timer gehört weiter zur aktiven Erkennung
    }

    [Fact]
    public void Dispose_ReleasesEverything()
    {
        NoContext();
        _vm.SetActive(true, Settings(), mirror: false);

        _vm.Dispose();

        Assert.True(_detection.Requests[0].IsDisposed);
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public void PresentationProperties_FollowTheSettings_AndNotify()
    {
        NoContext();
        var changed = new List<string?>();
        _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _vm.SetActive(true, Settings(s =>
        {
            s.BoxStyle = TargetBoxStyle.Rectangle;
            s.Color = TargetColor.Yellow;
            s.Animation = LockOnAnimation.ScanPulse;
            s.TextPosition = FunTextPosition.Bottom;
            s.TextStyle = FunTextStyle.Hint;
            s.TextHintStyle = HintStyle.Neon;
        }), mirror: true);

        Assert.Equal(TargetBoxStyle.Rectangle, _vm.BoxStyle);
        Assert.Equal(TargetColor.Yellow, _vm.Color);
        Assert.Equal(LockOnAnimation.ScanPulse, _vm.Animation);
        Assert.Equal(FunTextPosition.Bottom, _vm.TextPosition);
        Assert.Equal(FunTextStyle.Hint, _vm.TextStyle);
        Assert.Equal(HintStyle.Neon, _vm.TextHintStyle);
        Assert.True(_vm.Mirror);
        Assert.Contains(nameof(FunOverlayViewModel.BoxStyle), changed);
        Assert.Contains(nameof(FunOverlayViewModel.TextHintStyle), changed);
    }

    [Fact]
    public void LockOnSettings_FollowTheSettings_AndNotify()
    {
        NoContext();
        Assert.Equal(1200, _vm.LockOnMilliseconds);
        Assert.Equal(50, _vm.LockOnIntensityPercent);
        var changed = new List<string?>();
        _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _vm.SetActive(true, Settings(s =>
        {
            s.LockOnMilliseconds = 3000;
            s.LockOnIntensityPercent = 80;
        }), mirror: false);

        Assert.Equal(3000, _vm.LockOnMilliseconds);
        Assert.Equal(80, _vm.LockOnIntensityPercent);
        Assert.Contains(nameof(FunOverlayViewModel.LockOnMilliseconds), changed);
        Assert.Contains(nameof(FunOverlayViewModel.LockOnIntensityPercent), changed);

        // Geänderte Werte gelten sofort (SameSettings erkennt sie), ohne die Erkennung neu anzufordern.
        _vm.SetActive(true, Settings(s => s.LockOnIntensityPercent = 20), mirror: false);
        Assert.Equal(20, _vm.LockOnIntensityPercent);
        Assert.Equal(1200, _vm.LockOnMilliseconds);
        Assert.Single(_detection.Requests);
    }

    [Fact]
    public void SoundCues_CarryTheLockOnIntensity()
    {
        NoContext();
        _vm.SetActive(true, Settings(s => s.LockOnIntensityPercent = 85), mirror: false);

        Run(1.6, Person());

        Assert.Contains(FunSoundCue.Acquire, _sound.Played);
        Assert.Contains(FunSoundCue.Lock, _sound.Played);
        Assert.NotEmpty(_sound.Intensities);
        Assert.All(_sound.Intensities, i => Assert.Equal(85, i));
    }

    [Fact]
    public void LockOnDuration_ChangedViaConfigure_AppliesToTheSimulation()
    {
        NoContext();
        var settings = Settings(s => s.LockOnMilliseconds = 3000);
        _vm.Simulate(settings, mirror: false);

        Run(2.0);
        Assert.Equal(FunPhase.LockingOn, _vm.Snapshot.Phase);
        Run(1.1);
        Assert.Equal(FunPhase.ShowingText, _vm.Snapshot.Phase);
    }

    [Fact]
    public void VisibleRegion_IsPassedToTheEngine_SoPeopleOutsideAreIgnored()
    {
        NoContext();
        _vm.SetActive(true, Settings(), mirror: false);
        _vm.VisibleRegion = new NormRect(0.4, 0, 0.2, 1);

        _detection.Last.RaiseUpdated([Person(0.0)]); // Mitte bei x = 0,06 → außerhalb
        Run(0.5, Person(0.0));

        Assert.Empty(_vm.Snapshot.Targets);
    }

    [Fact]
    public void Status_FollowsTheRequest()
    {
        NoContext();
        _vm.SetActive(true, Settings(), mirror: false);
        Assert.NotNull(_vm.Status);

        var status = new DetectionStatus(DetectionMethod.Body, DetectionKind.Face, "Ausweichen", null, 5);
        _detection.Last.RaiseStatus(status);

        Assert.Equal(status, _vm.Status);

        _vm.SetActive(false, Settings(), mirror: false);
        Assert.Null(_vm.Status);
    }
}
