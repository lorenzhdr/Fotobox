using System.Windows.Threading;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Fotobox.ViewModels.Booth;
using Fotobox.Views.Booth;
using Fotobox.Views.Booth.Peace;
using Xunit;

namespace Fotobox.Tests;

// Peaceerkennung (Easter Egg) in der Gastansicht: Betriebsart je Zustand, Aktionen, Explosion statt Blitz, Töne.
public partial class BoothViewModelTests
{
    private sealed class PeaceRig
    {
        public Harness H { get; } = new();
        public FakeGestureService Gestures { get; } = new();
        public FakeSoundPlayer Sound { get; } = new();
        public TrackingTimeProvider PeaceTime { get; } = new();
        public GuestMoustacheState Guest { get; } = new();
        public PeaceOverlayViewModel Peace { get; }
        public FunOverlayViewModel? Fun { get; }
        public FakeDetectionService Detection { get; } = new();
        public BoothViewModel ViewModel { get; }

        public PeaceRig(bool enabled = true, Action<PeaceSettings>? configure = null, bool withFun = false)
        {
            H.Settings.Current.Peace = new PeaceSettings
            {
                Enabled = enabled,
                HoldMilliseconds = 1000,
                SelfDestructSeconds = 5,
                SelfDestructSoundEnabled = true,
                PhotoCountdownSeconds = 4,
                MoustacheAutoPhotoCountdownSeconds = 2,
            };
            configure?.Invoke(H.Settings.Current.Peace);
            // Die Herzerkennung ist standardmäßig an und hielte die Erkennung auch bei ausgeschaltetem Peace am Laufen: hier aus.
            H.Settings.Current.Heart.Enabled = false;
            H.Camera.Photo = CreateBitmap();
            H.Composer.Result = CreateBitmap();
            H.Storage.Works = true;

            // Deterministisch: immer die erste Möglichkeit; Kontext null, damit Timer-Ticks synchron im Test laufen.
            SynchronizationContext.SetSynchronizationContext(null);
            Peace = new PeaceOverlayViewModel(Gestures, PeaceTime, Sound, Guest, _ => 0);
            if (withFun)
            {
                H.Settings.Current.Fun = new FunSettings { Enabled = true, Scope = TargetScope.FullBody };
                Fun = new FunOverlayViewModel(Detection, Sound, new TrackingTimeProvider());
            }
            ViewModel = new BoothViewModel(H.Controller, H.Camera, H.Settings, Fun, Peace);
            // Erste Entscheidung wie beim Fensterstart.
            H.Settings.RaiseSettingsChanged();
        }

        public static PeaceTrigger Trigger(PeaceAction action) => new(action, 1, null, new NormPoint(0.5, 0.5));

        public bool Do(PeaceAction action) => ViewModel.OnPeaceAction(Trigger(action));

        /// <summary>Die Hintergrund-Sitzung hat Ereignisse in die Warteschlange des Dispatchers gelegt: abarbeiten.</summary>
        public static void Pump() => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () => { });

        /// <summary>Das Ende eines abgebrochenen Durchgangs läuft asynchron: auf Idle warten und die gemeldeten Änderungen abarbeiten.</summary>
        public void WaitForIdle()
        {
            Assert.True(SpinWait.SpinUntil(() => H.Controller.State == BoothState.Idle, TimeSpan.FromSeconds(10)));
            Pump();
        }

        public void CancelAndWait()
        {
            Assert.True(H.Controller.TryCancel());
            WaitForIdle();
        }

        /// <summary>Zeigt erst <paramref name="quiet"/> Sekunden lang keine Hand, dann <paramref name="hold"/> Sekunden lang den Mittelfinger.</summary>
        public void ShowMiddleFinger(double quiet, double hold)
        {
            var face = PeaceHandFixtures.Face(0.5, 0.3, 0.12);
            var middle = PeaceHandFixtures.Hand(HandPose.MiddleOnly);
            for (var i = 0; i < (int)Math.Round((quiet + hold) * 10); i++)
            {
                PeaceTime.Advance(TimeSpan.FromMilliseconds(100));
                if (Gestures.Requests.Count > 0 && !Gestures.Last.IsDisposed)
                    Gestures.Last.RaiseUpdated([face], i < (int)Math.Round(quiet * 10) ? [] : [middle]);
            }
        }

        /// <summary>Hält ein Peace-Zeichen mit einem Gesicht ins Bild, bis die Engine auslöst (oder die Zeit um ist).</summary>
        public void ShowPeaceFor(double seconds)
        {
            var face = PeaceHandFixtures.Face(0.5, 0.3, 0.12);
            var hand = PeaceHandFixtures.Hand(HandPose.Peace);
            for (var i = 0; i < (int)Math.Round(seconds * 10); i++)
            {
                PeaceTime.Advance(TimeSpan.FromMilliseconds(100));
                if (Gestures.Requests.Count > 0 && !Gestures.Last.IsDisposed)
                    Gestures.Last.RaiseUpdated([face], [hand]);
            }
        }
    }

    // ---- Betriebsart

    [Fact]
    public void Peace_Watches_InIdleWithRunningCamera_AndStopsWhileAdminIsOpen()
    {
        var rig = new PeaceRig();
        Assert.Equal(PeaceMode.Watching, rig.Peace.Mode);
        Assert.Equal(1, rig.Gestures.OpenRequests);
        Assert.Equal(GestureNeeds.Faces | GestureNeeds.Hands, rig.Gestures.Last.Options.Needs);

        rig.ViewModel.IsAdminOpen = true;
        Assert.Equal(PeaceMode.Off, rig.Peace.Mode);
        Assert.Equal(0, rig.Gestures.OpenRequests);

        rig.ViewModel.IsAdminOpen = false;
        Assert.Equal(PeaceMode.Watching, rig.Peace.Mode);
        Assert.Equal(1, rig.Gestures.OpenRequests);
    }

    [Fact]
    public void Peace_StaysOff_WhenDisabled_OrCameraNotRunning_AndTurnsOnLater()
    {
        var rig = new PeaceRig(enabled: false);
        Assert.Equal(PeaceMode.Off, rig.Peace.Mode);
        Assert.Empty(rig.Gestures.Requests);

        rig.H.Settings.Current.Peace.Enabled = true;
        rig.H.Camera.Status = CameraStatus.Starting;
        rig.H.Settings.RaiseSettingsChanged();
        Assert.Equal(PeaceMode.Off, rig.Peace.Mode);

        rig.H.Camera.Status = CameraStatus.Running;
        rig.H.Settings.RaiseSettingsChanged();
        Assert.Equal(PeaceMode.Watching, rig.Peace.Mode);

        rig.H.Settings.Current.Peace.Enabled = false;
        rig.H.Settings.RaiseSettingsChanged();
        Assert.Equal(PeaceMode.Off, rig.Peace.Mode);
        Assert.Equal(0, rig.Gestures.OpenRequests);
    }

    [Fact]
    public void Peace_IsOff_DuringAPlainCountdown_AndWhileVideoOrError()
    {
        var rig = new PeaceRig();
        rig.H.Settings.Current.Flow.CountdownSeconds = 3;
        Assert.True(rig.ViewModel.TryTrigger());
        Assert.Equal(BoothState.Countdown, rig.H.Controller.State);
        Assert.Equal(PeaceMode.Off, rig.Peace.Mode);
        Assert.Equal(0, rig.Gestures.OpenRequests);
        rig.CancelAndWait();
        Assert.Equal(BoothState.Idle, rig.H.Controller.State);
        Assert.Equal(PeaceMode.Watching, rig.Peace.Mode);

        Assert.True(rig.ViewModel.TryStartVideo());
        Assert.Equal(BoothState.VideoCountdown, rig.H.Controller.State);
        Assert.Equal(PeaceMode.Off, rig.Peace.Mode);
        rig.CancelAndWait();
        Assert.Equal(PeaceMode.Watching, rig.Peace.Mode);

        rig.H.Settings.Current.Flow.CountdownSeconds = 0;
        rig.H.Camera.CaptureFailure = new InvalidOperationException("Kamera getrennt");
        Assert.True(rig.ViewModel.TryTrigger());
        Assert.Equal(BoothState.Error, rig.H.Controller.State);
        Assert.Equal(PeaceMode.Off, rig.Peace.Mode);
    }

    [Fact]
    public void Peace_Shutdown_TurnsItOff()
    {
        var rig = new PeaceRig();
        Assert.Equal(PeaceMode.Watching, rig.Peace.Mode);

        rig.ViewModel.Shutdown();

        Assert.Equal(PeaceMode.Off, rig.Peace.Mode);
        Assert.Equal(0, rig.Gestures.OpenRequests);
    }

    // ---- Aktionen

    [Fact]
    public void SelfDestruct_StartsASingleShotAlarmCountdown_WithTheConfiguredSeconds()
    {
        var rig = new PeaceRig();
        rig.H.Settings.Current.Layout.GuestChoices = [GuestLayout.StripDouble, GuestLayout.Single];
        rig.H.Settings.RaiseSettingsChanged();

        Assert.True(rig.Do(PeaceAction.SelfDestruct));

        var c = rig.H.Controller;
        Assert.Equal(BoothState.Countdown, c.State);
        Assert.Equal(CountdownStyle.SelfDestruct, c.CountdownStyle);
        Assert.Equal(5, c.CountdownValue);
        var vm = rig.ViewModel;
        Assert.True(vm.IsSelfDestructCountdown);
        Assert.False(vm.IsPeaceCountdown);
        Assert.False(vm.ShowNormalCountdownDigits);
        Assert.True(vm.ShowSelfDestruct);
        Assert.Equal(5, vm.SelfDestructSecondsRemaining);
        Assert.False(vm.IsPause);
        Assert.False(vm.ShowHint);
        // Gewählt war der Streifen (Standard ist der erste Eintrag): Peace-Fotos sind immer ein Einzelfoto.
        Assert.Equal(LayoutKind.SingleBorderless, rig.H.Composer.RequestedLayout!.Kind);
        // Mit Mittelfingererkennung (Standard) bleibt Peace im Selbstzerstörungs-Countdown an, um ihn aufheben zu können.
        Assert.Equal(PeaceMode.FollowMoustache, rig.Peace.Mode);
    }

    [Fact]
    public void SelfDestruct_WithMiddleFingerDisabled_KeepsPeaceOffDuringTheCountdown()
    {
        var rig = new PeaceRig();
        rig.H.Settings.Current.MiddleFinger.Enabled = false;
        rig.H.Settings.RaiseSettingsChanged();

        Assert.True(rig.Do(PeaceAction.SelfDestruct));

        Assert.Equal(BoothState.Countdown, rig.H.Controller.State);
        Assert.Equal(PeaceMode.Off, rig.Peace.Mode);
        Assert.Equal(0, rig.Gestures.OpenRequests);
    }

    [Fact]
    public void PhotoAction_StartsANormalCountdownWithPeaceSign()
    {
        var rig = new PeaceRig(configure: s => { s.ActionPhoto = true; });

        Assert.True(rig.Do(PeaceAction.Photo));

        var vm = rig.ViewModel;
        Assert.Equal(CountdownStyle.Peace, vm.CountdownStyle);
        Assert.Equal(4, rig.H.Controller.CountdownValue);
        Assert.True(vm.IsPeaceCountdown);
        Assert.True(vm.ShowNormalCountdownDigits);
        Assert.False(vm.IsSelfDestructCountdown);
        Assert.False(vm.ShowSelfDestruct);
        Assert.Equal("4", vm.CountdownText);
    }

    [Fact]
    public void PhotoAction_WithCountdownZero_CapturesImmediately_WithTheNormalFlash()
    {
        var rig = new PeaceRig(configure: s => s.PhotoCountdownSeconds = 0);
        var flashes = 0;
        var explosions = 0;
        rig.ViewModel.FlashRequested += (_, _) => flashes++;
        rig.ViewModel.ExplosionRequested += (_, _) => explosions++;

        Assert.True(rig.Do(PeaceAction.Photo));

        Assert.Equal(1, flashes);
        Assert.Equal(0, explosions);
        Assert.False(rig.ViewModel.IsPeaceCountdown); // kein Zählwert, kein ✌
    }

    [Fact]
    public void PeaceActions_AreBlocked_WhileAdminIsOpenOrTheCameraRestarts()
    {
        var rig = new PeaceRig();
        rig.ViewModel.IsAdminOpen = true;
        Assert.False(rig.Do(PeaceAction.SelfDestruct));
        Assert.False(rig.Do(PeaceAction.Photo));
        Assert.False(rig.Do(PeaceAction.Moustache));
        Assert.Equal(BoothState.Idle, rig.H.Controller.State);
        Assert.False(rig.Peace.MoustacheActive);
        rig.ViewModel.IsAdminOpen = false;
        Assert.True(rig.Do(PeaceAction.Photo));
    }

    [Fact]
    public async Task PeaceActions_AreBlocked_WhileTheCameraRestarts()
    {
        var rig = new PeaceRig();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.H.Camera.StartTask = start.Task;
        var restart = rig.ViewModel.RestartCameraAsync();
        Assert.True(rig.ViewModel.IsCameraBusy);

        Assert.False(rig.Do(PeaceAction.SelfDestruct));
        Assert.Equal(BoothState.Idle, rig.H.Controller.State);

        start.SetResult();
        await restart;
    }

    [Fact]
    public void PeaceActions_UseTheGuestLayoutChoice_AndResetItAfterwards()
    {
        var rig = new PeaceRig();
        rig.H.Settings.Current.Layout.GuestChoices = [GuestLayout.Single, GuestLayout.Overlay];
        rig.H.Settings.RaiseSettingsChanged();
        rig.ViewModel.CycleLayout(1);
        Assert.Equal(GuestLayout.Overlay, rig.ViewModel.SelectedLayout);

        Assert.True(rig.Do(PeaceAction.Photo));

        // Das Overlay bleibt erhalten (nur Streifen werden zum Einzelfoto), danach gilt wieder der Standard.
        Assert.Equal(LayoutKind.Overlay, rig.H.Composer.RequestedLayout!.Kind);
        Assert.Equal(GuestLayout.Single, rig.ViewModel.SelectedLayout);
    }

    [Fact]
    public void MoustacheAction_StartsTheMoustache_WithoutAPhoto()
    {
        var rig = new PeaceRig();

        Assert.True(rig.Do(PeaceAction.Moustache));

        Assert.True(rig.Peace.MoustacheActive);
        Assert.Equal(BoothState.Idle, rig.H.Controller.State);
        Assert.Equal(PeaceMode.Watching, rig.Peace.Mode);
    }

    [Fact]
    public void MoustacheAction_WithAutoPhoto_StartsAPeaceCountdown_AndKeepsFollowingTheFaces()
    {
        var rig = new PeaceRig(configure: s => s.MoustacheAutoPhoto = true);
        var request = rig.Gestures.Last;

        Assert.True(rig.Do(PeaceAction.Moustache));

        var c = rig.H.Controller;
        Assert.Equal(BoothState.Countdown, c.State);
        Assert.Equal(CountdownStyle.Peace, c.CountdownStyle);
        Assert.Equal(2, c.CountdownValue);
        Assert.True(rig.ViewModel.IsPeaceCountdown);
        // Der Bart überlebt den Countdown: gleiche Anforderung, nur Gesichter mit 10 Analysen/s.
        Assert.True(rig.Peace.MoustacheActive);
        Assert.Equal(PeaceMode.FollowMoustache, rig.Peace.Mode);
        Assert.Same(request, rig.Gestures.Last);
        Assert.False(request.IsDisposed);
        // Mit Mittelfingererkennung (Standard) kommen im Countdown Hände dazu (schneller Gesichtsmodus: Handstufe jeden 2. Zyklus).
        Assert.Equal(GestureNeeds.Faces | GestureNeeds.Hands, request.Options.Needs);
        Assert.Equal(10, request.Options.AnalysesPerSecond);
        Assert.Equal(2, request.Options.HandCycleDivisor);
    }

    [Fact]
    public void MoustacheAction_WithAutoPhoto_AndMiddleFingerDisabled_FollowsOnlyTheFaces()
    {
        var rig = new PeaceRig(configure: s => s.MoustacheAutoPhoto = true);
        rig.H.Settings.Current.MiddleFinger.Enabled = false;
        rig.H.Settings.RaiseSettingsChanged();
        var request = rig.Gestures.Last;

        Assert.True(rig.Do(PeaceAction.Moustache));

        Assert.Equal(PeaceMode.FollowMoustache, rig.Peace.Mode);
        Assert.Same(request, rig.Gestures.Last);
        Assert.Equal(GestureNeeds.Faces, request.Options.Needs);
        Assert.Equal(10, request.Options.AnalysesPerSecond);
    }

    [Fact]
    public void Moustache_EndsWithThePhoto_AndWatchingContinuesAfterwards()
    {
        var rig = new PeaceRig(configure: s => s.MoustacheAutoPhoto = true);
        rig.H.Settings.Current.Flow.PreviewSeconds = 30;
        rig.H.Settings.Current.Flow.PrintingEnabled = false;
        Assert.True(rig.Do(PeaceAction.Moustache));
        Assert.Equal(PeaceMode.FollowMoustache, rig.Peace.Mode);

        // Zwei Sekunden Countdown abwarten; danach läuft die Aufnahme im Hintergrund bis zur Vorschau.
        rig.H.Time.Advance(TimeSpan.FromSeconds(1));
        rig.H.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(SpinUntil(rig.H, () => rig.H.Controller.State == BoothState.Preview, advanceInPreview: false));
        PeaceRig.Pump();

        Assert.False(rig.Peace.MoustacheActive); // „bis zum nächsten Foto“ endet mit dem fertigen Foto
        Assert.Equal(PeaceMode.Off, rig.Peace.Mode);

        Assert.True(SpinUntil(rig.H, () => rig.H.Controller.State == BoothState.Idle, advanceInPreview: true));
        PeaceRig.Pump();
        Assert.Equal(PeaceMode.Watching, rig.Peace.Mode);
    }

    [Fact]
    public void Moustache_InPreview_FollowsOnlyTheFaces_NoMiddleFingerHands()
    {
        // Feste Dauer: Der Bart überlebt das Foto, damit die Vorschau mit aktivem Bart geprüft werden kann.
        var rig = new PeaceRig(configure: s =>
        {
            s.MoustacheAutoPhoto = true;
            s.MoustacheVisibility = MoustacheVisibility.FixedDuration;
            s.MoustacheDurationSeconds = 120;
        });
        rig.H.Settings.Current.Flow.PreviewSeconds = 30;
        rig.H.Settings.Current.Flow.PrintingEnabled = false;
        var request = rig.Gestures.Last;
        Assert.True(rig.Do(PeaceAction.Moustache));
        // Im Countdown (Bart sichtbar) sind Hände für den Mittelfinger erlaubt.
        Assert.True(rig.Peace.MiddleFingerWatching);
        Assert.Equal(GestureNeeds.Faces | GestureNeeds.Hands, request.Options.Needs);

        rig.H.Time.Advance(TimeSpan.FromSeconds(1));
        rig.H.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(SpinUntil(rig.H, () => rig.H.Controller.State == BoothState.Preview, advanceInPreview: false));
        PeaceRig.Pump();

        Assert.True(rig.Peace.MoustacheActive);
        Assert.Equal(PeaceMode.FollowMoustache, rig.Peace.Mode);
        Assert.Same(request, rig.Gestures.Last);
        Assert.False(rig.Peace.MiddleFingerWatching);
        Assert.Equal(GestureNeeds.Faces, request.Options.Needs);
    }

    [Fact]
    public void Moustache_EndsWhenPeaceIsSwitchedOff()
    {
        var rig = new PeaceRig();
        Assert.True(rig.Do(PeaceAction.Moustache));
        Assert.True(rig.Peace.MoustacheActive);

        rig.H.Settings.Current.Peace.Enabled = false;
        rig.H.Settings.RaiseSettingsChanged();

        Assert.False(rig.Peace.MoustacheActive);
        Assert.Equal(PeaceMode.Off, rig.Peace.Mode);
    }

    [Fact]
    public void Moustache_KeepsTheTargetTrackingRunning_InIdle()
    {
        var rig = new PeaceRig(withFun: true);
        Assert.True(rig.Fun!.IsActive);

        Assert.True(rig.Do(PeaceAction.Moustache));

        Assert.True(rig.Peace.MoustacheActive);
        Assert.True(rig.Fun.IsActive); // beim Bart darf die Zielerfassung im Leerlauf weiterlaufen
        Assert.Equal(1, rig.Detection.OpenRequests);
    }

    [Fact]
    public void CancelKey_InIdle_VanishesTheBeard()
    {
        var rig = new PeaceRig();
        Assert.False(rig.ViewModel.TryCancel()); // ohne Bart nichts zu tun

        Assert.True(rig.Do(PeaceAction.Moustache));
        rig.Gestures.Last.RaiseUpdated([PeaceHandFixtures.Face(0.5, 0.3, 0.12)], []);
        Assert.True(rig.Peace.MoustacheActive);

        Assert.True(rig.ViewModel.TryCancel());

        Assert.False(rig.Peace.MoustacheActive);
        Assert.Null(rig.Guest.Current); // ein Foto danach hat keinen Bart
        Assert.Equal(BoothState.Idle, rig.H.Controller.State);
        Assert.False(rig.ViewModel.TryCancel());
    }

    [Fact]
    public void CancelKey_InTheCountdown_CancelsOnlyTheCountdown_TheBeardStays()
    {
        var rig = new PeaceRig(configure: s => s.MoustacheAutoPhoto = true);
        Assert.True(rig.Do(PeaceAction.Moustache));
        Assert.Equal(BoothState.Countdown, rig.H.Controller.State);

        Assert.True(rig.ViewModel.TryCancel());
        rig.WaitForIdle();
        Assert.True(rig.Peace.MoustacheActive);

        // Erst der nächste Druck im Leerlauf entfernt den Bart.
        Assert.True(rig.ViewModel.TryCancel());
        Assert.False(rig.Peace.MoustacheActive);
    }

    [Fact]
    public void CancelKey_WhileAdminIsOpen_DoesNothing()
    {
        var rig = new PeaceRig();
        Assert.True(rig.Do(PeaceAction.Moustache));

        rig.ViewModel.IsAdminOpen = true;

        Assert.False(rig.ViewModel.TryCancel());
    }

    // ---- Ende-zu-Ende über die Engine

    [Fact]
    public void HoldingThePeaceSign_TriggersTheSelfDestruct_ThroughTheEngine()
    {
        var rig = new PeaceRig();

        rig.ShowPeaceFor(0.5);
        Assert.Equal(BoothState.Idle, rig.H.Controller.State);

        rig.ShowPeaceFor(1.5);

        Assert.Equal(BoothState.Countdown, rig.H.Controller.State);
        Assert.Equal(CountdownStyle.SelfDestruct, rig.H.Controller.CountdownStyle);
        Assert.Equal(PeaceMode.FollowMoustache, rig.Peace.Mode); // wegen der Mittelfingererkennung (siehe SelfDestruct_WithMiddleFingerDisabled…)
    }

    // ---- Explosion

    [Fact]
    public void Explosion_ReplacesTheFlash_AndTheOverlayIsStillShownWhenItStarts()
    {
        var rig = new PeaceRig(configure: s => s.SelfDestructSeconds = 2);
        var flashes = 0;
        var explosions = 0;
        var shownAtExplosion = false;
        var stateAtExplosion = BoothState.Idle;
        rig.ViewModel.FlashRequested += (_, _) => flashes++;
        rig.ViewModel.ExplosionRequested += (_, _) =>
        {
            explosions++;
            shownAtExplosion = rig.ViewModel.ShowSelfDestruct;
            stateAtExplosion = rig.H.Controller.State;
        };
        Assert.True(rig.Do(PeaceAction.SelfDestruct));

        rig.H.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, explosions);
        rig.H.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0, flashes);
        Assert.Equal(1, explosions);
        Assert.True(shownAtExplosion);
        // Der Zustand ist beim Blitz schon Aufnahme: Die Alarm-Ansicht bleibt deshalb auch im Zustand Capturing sichtbar.
        Assert.Equal(BoothState.Capturing, stateAtExplosion);
    }

    [Fact]
    public void NormalTrigger_StillRaisesTheNormalFlash()
    {
        var rig = new PeaceRig();
        rig.H.Settings.Current.Flow.CountdownSeconds = 0;
        var flashes = 0;
        var explosions = 0;
        rig.ViewModel.FlashRequested += (_, _) => flashes++;
        rig.ViewModel.ExplosionRequested += (_, _) => explosions++;

        Assert.True(rig.ViewModel.TryTrigger());

        Assert.Equal(1, flashes);
        Assert.Equal(0, explosions);
    }

    // ---- Töne

    [Fact]
    public void SelfDestruct_PlaysTicksAndExplosion_OnlyWithTheSoundSwitch()
    {
        var rig = new PeaceRig(configure: s => s.SelfDestructSeconds = 5);
        Assert.True(rig.Do(PeaceAction.SelfDestruct));
        for (var i = 0; i < 5; i++)
            rig.H.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(
            [FunSoundCue.SelfDestructTick, FunSoundCue.SelfDestructTick, FunSoundCue.SelfDestructTickFast,
             FunSoundCue.SelfDestructTickFast, FunSoundCue.SelfDestructTickFast, FunSoundCue.Explosion],
            rig.Sound.Played);
    }

    [Fact]
    public void SelfDestruct_IsSilent_WithoutTheSoundSwitch()
    {
        var rig = new PeaceRig(configure: s => { s.SelfDestructSeconds = 3; s.SelfDestructSoundEnabled = false; });
        Assert.True(rig.Do(PeaceAction.SelfDestruct));
        for (var i = 0; i < 3; i++)
            rig.H.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(rig.Sound.Played);
    }

    [Fact]
    public void Cancel_DuringTheSelfDestruct_SilencesTheSiren_AndShowsNothingMore()
    {
        var rig = new PeaceRig();
        Assert.True(rig.Do(PeaceAction.SelfDestruct));
        rig.H.Time.Advance(TimeSpan.FromSeconds(1));
        var stops = rig.Sound.Stops;
        var played = rig.Sound.Played.Count;

        Assert.True(rig.ViewModel.TryCancel());
        rig.WaitForIdle();

        Assert.Equal(BoothState.Idle, rig.H.Controller.State);
        Assert.True(rig.Sound.Stops > stops);
        Assert.Equal(played, rig.Sound.Played.Count); // keine Explosion
        Assert.False(rig.ViewModel.ShowSelfDestruct);
        Assert.False(rig.ViewModel.IsSelfDestructCountdown);
        Assert.Equal(PeaceMode.Watching, rig.Peace.Mode);
    }

    [Fact]
    public void ReachingTheExplosion_DoesNotStopTheSound()
    {
        var rig = new PeaceRig(configure: s => s.SelfDestructSeconds = 2);
        Assert.True(rig.Do(PeaceAction.SelfDestruct));
        rig.H.Time.Advance(TimeSpan.FromSeconds(1));
        var stops = rig.Sound.Stops;

        rig.H.Time.Advance(TimeSpan.FromSeconds(1)); // Blitz, Aufnahme

        Assert.Equal(FunSoundCue.Explosion, rig.Sound.Played[^1]);
        Assert.Equal(stops, rig.Sound.Stops);
    }

    [Fact]
    public void FunTargeting_StoppingAtTheStart_DoesNotCutOffTheSiren()
    {
        var rig = new PeaceRig(withFun: true);
        Assert.True(rig.Fun!.IsActive);

        Assert.True(rig.Do(PeaceAction.SelfDestruct));

        // Beim Verlassen von Idle schaltet die Zielerfassung ab und stoppt den gemeinsamen Spieler einmal – vor dem ersten Ton.
        Assert.False(rig.Fun.IsActive);
        var stopsAfterStart = rig.Sound.Stops;
        Assert.Equal(FunSoundCue.SelfDestructTick, rig.Sound.Played[0]);

        rig.H.Time.Advance(TimeSpan.FromSeconds(1));
        rig.H.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(stopsAfterStart, rig.Sound.Stops);
        Assert.Equal(3, rig.Sound.Played.Count);
    }

    // ---- BoothWindow

    [StaFact]
    public void BoothWindow_PeaceBindings_ShowAlarmOnlyForTheSelfDestruct_AndThePeaceSignOnlyForPeaceCountdowns()
    {
        var rig = new PeaceRig(configure: s => s.SelfDestructSeconds = 2);
        var window = new BoothWindow(rig.ViewModel, new FakeAdminLauncher(), rig.H.Settings, new NoDisplays());
        var alarm = (SelfDestructOverlay)window.FindName("SelfDestructView");
        var digits = (System.Windows.FrameworkElement)window.FindName("CountdownBlock");
        var sign = (System.Windows.FrameworkElement)window.FindName("PeaceSign");
        var layer = (PeaceOverlay)window.FindName("PeaceLayer");
        Assert.NotNull(alarm);
        Assert.NotNull(layer);
        var content = (System.Windows.FrameworkElement)window.Content;
        void Layout()
        {
            content.Measure(new System.Windows.Size(1920, 1080));
            content.Arrange(new System.Windows.Rect(0, 0, 1920, 1080));
            content.UpdateLayout();
        }

        Layout();
        Assert.Equal(System.Windows.Visibility.Collapsed, alarm.Visibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, sign.Visibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, layer.Visibility); // nichts zu zeichnen: die Gastansicht verrät nichts

        // Peace-Foto: normale Ziffern mit ✌, kein Alarm.
        Assert.True(rig.Do(PeaceAction.Photo));
        Layout();
        Assert.Equal(System.Windows.Visibility.Visible, digits.Visibility);
        Assert.Equal(System.Windows.Visibility.Visible, sign.Visibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, alarm.Visibility);
        rig.CancelAndWait();

        // Selbstzerstörung: Alarm statt Ziffern, kein ✌; die Explosion trifft ein sichtbares Overlay.
        var visibleAtExplosion = false;
        rig.ViewModel.ExplosionRequested += (_, _) => visibleAtExplosion = alarm.Visibility == System.Windows.Visibility.Visible;
        Assert.True(rig.Do(PeaceAction.SelfDestruct));
        Layout();
        Assert.Equal(System.Windows.Visibility.Visible, alarm.Visibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, digits.Visibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, sign.Visibility);
        Assert.Equal(2, alarm.SecondsRemaining);

        rig.H.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, alarm.SecondsRemaining);
        rig.H.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(visibleAtExplosion);
    }

    [StaFact]
    public void BoothWindow_PeaceLayer_ShowsMoustachesOverFaces()
    {
        var rig = new PeaceRig();
        var window = new BoothWindow(rig.ViewModel, new FakeAdminLauncher(), rig.H.Settings, new NoDisplays());
        var layer = (PeaceOverlay)window.FindName("PeaceLayer");
        Assert.True(rig.Do(PeaceAction.Moustache));

        rig.Gestures.Last.RaiseUpdated([PeaceHandFixtures.Face(0.5, 0.3, 0.12)], []);

        Assert.NotEmpty(rig.Peace.Moustaches);
        Assert.True(rig.Peace.IsShown);
        var content = (System.Windows.FrameworkElement)window.Content;
        content.Measure(new System.Windows.Size(1920, 1080));
        content.Arrange(new System.Windows.Rect(0, 0, 1920, 1080));
        content.UpdateLayout();
        Assert.Equal(System.Windows.Visibility.Visible, layer.Visibility);
        Assert.False(layer.ShowLandmarks);
        Assert.Same(rig.Peace.Moustaches, layer.Moustaches);
    }

    // ---- Mittelfinger

    [Fact]
    public void MiddleFinger_SelfDestructCountdown_KeepsPeaceOnWithHands_OnlyWhenTheFeatureIsOn()
    {
        var rig = new PeaceRig();
        Assert.True(rig.Do(PeaceAction.SelfDestruct));
        Assert.Equal(PeaceMode.FollowMoustache, rig.Peace.Mode);
        Assert.True(rig.Peace.MiddleFingerWatching);
        Assert.Equal(GestureNeeds.Faces | GestureNeeds.Hands, rig.Gestures.Last.Options.Needs);
        Assert.Equal(5, rig.Gestures.Last.Options.AnalysesPerSecond); // eingestellte Peace-Rate
        rig.CancelAndWait();
        Assert.Equal(PeaceMode.Watching, rig.Peace.Mode);
        Assert.False(rig.Peace.MiddleFingerWatching);

        // Zwilling mit ausgeschalteter Erkennung: wie bisher aus, keine Hände.
        var off = new PeaceRig();
        off.H.Settings.Current.MiddleFinger.Enabled = false;
        off.H.Settings.RaiseSettingsChanged();
        Assert.True(off.Do(PeaceAction.SelfDestruct));
        Assert.Equal(PeaceMode.Off, off.Peace.Mode);
        Assert.Equal(0, off.Gestures.OpenRequests);
    }

    [Fact]
    public void MiddleFinger_CancelsTheSelfDestruct_Silently_LikeEsc()
    {
        var rig = new PeaceRig();
        Assert.True(rig.Do(PeaceAction.SelfDestruct));
        rig.H.Time.Advance(TimeSpan.FromSeconds(1));
        var stops = rig.Sound.Stops;
        var played = rig.Sound.Played.Count;
        var explosions = 0;
        rig.ViewModel.ExplosionRequested += (_, _) => explosions++;

        rig.ShowMiddleFinger(quiet: 1.0, hold: 1.0);
        rig.WaitForIdle();

        Assert.Equal(BoothState.Idle, rig.H.Controller.State); // kein Fehlerzustand
        Assert.True(rig.Sound.Stops > stops);
        Assert.Equal(played, rig.Sound.Played.Count); // keine Explosion
        Assert.Equal(0, explosions);
        Assert.False(rig.ViewModel.ShowSelfDestruct);
        Assert.False(rig.ViewModel.IsSelfDestructCountdown);
        Assert.Equal(PeaceMode.Watching, rig.Peace.Mode);
    }

    [Fact]
    public void MiddleFinger_DoesNotCancelANormalCountdown_OrPeaceCountdown()
    {
        var rig = new PeaceRig();
        rig.H.Settings.Current.Flow.CountdownSeconds = 3;
        Assert.True(rig.ViewModel.TryTrigger());
        Assert.Equal(PeaceMode.Off, rig.Peace.Mode);
        Assert.Equal(0, rig.Gestures.OpenRequests); // nichts aufzuheben: keine Hände
        Assert.False(rig.ViewModel.OnMiddleFinger());
        Assert.Equal(BoothState.Countdown, rig.H.Controller.State);
        rig.CancelAndWait();

        Assert.True(rig.Do(PeaceAction.Photo));
        Assert.Equal(CountdownStyle.Peace, rig.H.Controller.CountdownStyle);
        Assert.Equal(PeaceMode.Off, rig.Peace.Mode);
        Assert.Equal(0, rig.Gestures.OpenRequests);
        Assert.False(rig.ViewModel.OnMiddleFinger());
        Assert.Equal(BoothState.Countdown, rig.H.Controller.State);
    }

    [Fact]
    public void MiddleFinger_InIdleWithoutABeard_DoesNothing()
    {
        var rig = new PeaceRig();
        Assert.False(rig.ViewModel.OnMiddleFinger());

        rig.ShowMiddleFinger(quiet: 1.0, hold: 2.0);

        Assert.Equal(BoothState.Idle, rig.H.Controller.State);
        Assert.False(rig.Peace.MiddleFingerWatching);
    }

    [Fact]
    public void MiddleFinger_VanishesTheBeard_ButTheAutoPhotoCountdownContinuesWithoutIt()
    {
        var rig = new PeaceRig(configure: s => s.MoustacheAutoPhoto = true);
        rig.H.Settings.Current.Flow.PreviewSeconds = 30;
        rig.H.Settings.Current.Flow.PrintingEnabled = false;
        Assert.True(rig.Do(PeaceAction.Moustache));
        rig.Gestures.Last.RaiseUpdated([PeaceHandFixtures.Face(0.5, 0.3, 0.12)], []);
        Assert.True(rig.Peace.MoustacheActive);
        Assert.NotNull(rig.Guest.Current);
        Assert.Equal(BoothState.Countdown, rig.H.Controller.State);

        rig.ShowMiddleFinger(quiet: 1.0, hold: 1.0);

        Assert.False(rig.Peace.MoustacheActive);
        Assert.Null(rig.Guest.Current); // ein Foto jetzt hat keinen Bart
        Assert.Equal(BoothState.Countdown, rig.H.Controller.State); // Peace-Countdown läuft weiter

        rig.H.Time.Advance(TimeSpan.FromSeconds(1));
        rig.H.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(SpinUntil(rig.H, () => rig.H.Controller.State == BoothState.Preview, advanceInPreview: false));
        PeaceRig.Pump();
        Assert.Null(rig.Guest.Current);
    }

    [Fact]
    public void MiddleFinger_VanishesTheBeardInIdle_ThroughTheEngine_AndTheAnimationEnds()
    {
        var rig = new PeaceRig();
        Assert.True(rig.Do(PeaceAction.Moustache));
        rig.Gestures.Last.RaiseUpdated([PeaceHandFixtures.Face(0.5, 0.3, 0.12)], []);
        Assert.True(rig.Peace.MoustacheActive);
        Assert.True(rig.Peace.MiddleFingerWatching);

        rig.ShowMiddleFinger(quiet: 1.0, hold: 0.8);

        Assert.False(rig.Peace.MoustacheActive);
        Assert.Null(rig.Guest.Current);
        Assert.False(rig.Peace.MiddleFingerWatching);
        Assert.Equal(PeaceMode.Watching, rig.Peace.Mode);

        rig.PeaceTime.Advance(TimeSpan.FromSeconds(0.5));
        Assert.False(rig.Peace.HasMoustacheVisuals);
        Assert.Empty(rig.Peace.Moustaches);
    }

    [Fact]
    public void MiddleFinger_RightAfterTheExplosionStarted_DoesNotCancelThePhoto()
    {
        var rig = new PeaceRig(configure: s => s.SelfDestructSeconds = 2);
        rig.H.Settings.Current.Flow.PreviewSeconds = 30;
        rig.H.Settings.Current.Flow.PrintingEnabled = false;
        var stateAtExplosion = BoothState.Idle;
        var result = true;
        rig.ViewModel.ExplosionRequested += (_, _) =>
        {
            stateAtExplosion = rig.H.Controller.State;
            result = rig.ViewModel.OnMiddleFinger();
        };
        Assert.True(rig.Do(PeaceAction.SelfDestruct));

        rig.H.Time.Advance(TimeSpan.FromSeconds(1));
        rig.H.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(BoothState.Capturing, stateAtExplosion);
        Assert.False(result);
        Assert.True(SpinUntil(rig.H, () => rig.H.Controller.State == BoothState.Preview, advanceInPreview: false));
    }

    [Fact]
    public void MiddleFinger_IsIgnored_WhileAdminIsOpen_OrWhenSwitchedOff()
    {
        var rig = new PeaceRig();
        Assert.True(rig.Do(PeaceAction.SelfDestruct));

        rig.ViewModel.IsAdminOpen = true;
        Assert.False(rig.ViewModel.OnMiddleFinger());
        Assert.Equal(BoothState.Countdown, rig.H.Controller.State);
        rig.ViewModel.IsAdminOpen = false;

        rig.H.Settings.Current.MiddleFinger.Enabled = false;
        Assert.False(rig.ViewModel.OnMiddleFinger());
        rig.H.Settings.Current.MiddleFinger.Enabled = true;
        rig.H.Settings.Current.MiddleFinger.ActionCancelPeace = false;
        rig.H.Settings.Current.MiddleFinger.ActionCancelKisscam = false;
        Assert.False(rig.ViewModel.OnMiddleFinger());
        Assert.Equal(BoothState.Countdown, rig.H.Controller.State);

        rig.H.Settings.Current.MiddleFinger.ActionCancelPeace = true;
        rig.H.Settings.Current.MiddleFinger.ActionCancelKisscam = true;
        Assert.True(rig.ViewModel.OnMiddleFinger());
        rig.WaitForIdle();
        Assert.Equal(BoothState.Idle, rig.H.Controller.State);
    }

    // ---- Video: Bart endet mit dem Videostart, Peace- und Herz-Aktionen sind gesperrt

    [Fact]
    public void Moustache_EndsOnVideoStart_AndDoesNotReturn()
    {
        var rig = new PeaceRig();
        var video = rig.H.Settings.Current.Video;
        video.CountdownSeconds = 2;
        video.MinDurationSeconds = 0;
        video.SuccessDisplaySeconds = 3;
        Assert.True(rig.Do(PeaceAction.Moustache));
        rig.Gestures.Last.RaiseUpdated([PeaceHandFixtures.Face(0.5, 0.3, 0.12)], []);
        Assert.True(rig.Peace.MoustacheActive);
        Assert.NotEmpty(rig.Peace.Moustaches);
        Assert.NotNull(rig.Guest.Current);

        Assert.True(rig.ViewModel.TryStartVideo());

        // Sofort weg (kein Verschwinden-Effekt): im Video-Countdown ist die Erkennung aus.
        Assert.Equal(BoothState.VideoCountdown, rig.H.Controller.State);
        AssertMoustacheGone(rig);

        // Auch über Aufnahme, Speichern und Anzeige des Erfolgstexts bleibt er weg.
        rig.H.Time.Advance(TimeSpan.FromSeconds(1));
        rig.H.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(SpinWait.SpinUntil(() => rig.H.Controller.State == BoothState.Recording, TimeSpan.FromSeconds(10)));
        AssertMoustacheGone(rig);

        Assert.True(rig.ViewModel.TryStopVideo());
        Assert.Equal(BoothState.VideoSaved, rig.H.Controller.State);
        AssertMoustacheGone(rig);

        rig.H.Time.Advance(TimeSpan.FromSeconds(3));
        rig.WaitForIdle();
        Assert.Equal(PeaceMode.Watching, rig.Peace.Mode);
        rig.Gestures.Last.RaiseUpdated([PeaceHandFixtures.Face(0.5, 0.3, 0.12)], []);
        Assert.False(rig.Peace.MoustacheActive);
        Assert.Empty(rig.Peace.Moustaches);
        Assert.Null(rig.Guest.Current);
    }

    private static void AssertMoustacheGone(PeaceRig rig)
    {
        Assert.Equal(PeaceMode.Off, rig.Peace.Mode);
        Assert.False(rig.Peace.MoustacheActive);
        Assert.False(rig.Peace.HasMoustacheVisuals);
        Assert.Empty(rig.Peace.Moustaches);
        Assert.Null(rig.Guest.Current);
        Assert.Equal(0, rig.Gestures.OpenRequests);
    }

    public enum NonIdleStage { VideoCountdown, Recording, StripPause }

    [Theory]
    [InlineData(NonIdleStage.VideoCountdown)]
    [InlineData(NonIdleStage.Recording)]
    [InlineData(NonIdleStage.StripPause)]
    public void PeaceAndHeart_Ignored_InVideoCountdown_Recording_StripCountdown(NonIdleStage stage)
    {
        var rig = new PeaceRig();
        var settings = rig.H.Settings.Current;
        BoothState expected;
        switch (stage)
        {
            case NonIdleStage.VideoCountdown:
                settings.Video.CountdownSeconds = 3;
                Assert.True(rig.ViewModel.TryStartVideo());
                expected = BoothState.VideoCountdown;
                break;
            case NonIdleStage.Recording:
                settings.Video.CountdownSeconds = 0;
                settings.Video.MinDurationSeconds = 0;
                Assert.True(rig.ViewModel.TryStartVideo());
                expected = BoothState.Recording;
                break;
            default:
                // Zwei Streifenfotos ohne Countdown: Nach dem ersten wartet der Ablauf in der Pause (Countdown, Wert 0).
                rig.H.Composer.CountFor = _ => 2;
                settings.Flow.CountdownSeconds = 0;
                settings.Flow.PauseBetweenShotsSeconds = 5;
                Assert.True(rig.ViewModel.TryTrigger());
                Assert.True(SpinWait.SpinUntil(
                    () => rig.H.Controller.State == BoothState.Countdown && rig.H.Controller.ShotIndex == 2, TimeSpan.FromSeconds(10)));
                Assert.Equal(0, rig.H.Controller.CountdownValue);
                expected = BoothState.Countdown;
                break;
        }
        Assert.Equal(expected, rig.H.Controller.State);

        Assert.False(rig.Do(PeaceAction.SelfDestruct));
        Assert.False(rig.Do(PeaceAction.Photo));
        Assert.False(rig.Do(PeaceAction.Moustache));
        Assert.False(rig.ViewModel.OnHeartAction(KisscamRig.Trigger()));

        Assert.Equal(expected, rig.H.Controller.State);
        Assert.Equal(CountdownStyle.Normal, rig.H.Controller.CountdownStyle);
        Assert.False(rig.Peace.MoustacheActive);
        Assert.False(rig.ViewModel.IsSelfDestructCountdown);
        Assert.False(rig.ViewModel.IsPeaceCountdown);
        Assert.Null(rig.ViewModel.KisscamFrame);

        // Aufräumen: den Durchgang beenden, damit keine Sitzung im Hintergrund weiterläuft.
        if (stage == NonIdleStage.Recording)
        {
            Assert.True(rig.ViewModel.TryStopVideo());
            rig.H.Time.Advance(TimeSpan.FromSeconds(60));
        }
        else
        {
            rig.CancelAndWait();
        }
        rig.WaitForIdle();
    }

    [Fact]
    public void MiddleFinger_DoesNotTouchVideoOrPrint()
    {
        var rig = new PeaceRig();
        Assert.True(rig.ViewModel.TryStartVideo());
        Assert.Equal(BoothState.VideoCountdown, rig.H.Controller.State);

        Assert.False(rig.ViewModel.OnMiddleFinger());

        Assert.Equal(BoothState.VideoCountdown, rig.H.Controller.State);
        rig.CancelAndWait();
    }
}
