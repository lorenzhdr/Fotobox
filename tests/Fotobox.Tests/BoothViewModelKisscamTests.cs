using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Heart;
using Fotobox.Core.Music;
using Fotobox.Core.Peace;
using Fotobox.ViewModels.Booth;
using Fotobox.Views.Booth;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Musikspieler-Attrappe: zeichnet Aufrufe auf.</summary>
internal sealed class FakeMusicPlayer : IMusicPlayer
{
    public List<(string? Path, int Volume, bool Loop, TimeSpan StartAt)> Played { get; } = [];
    public List<TimeSpan> FadeOuts { get; } = [];
    public int Stops { get; private set; }
    public bool IsPlaying { get; private set; }

    public event EventHandler? PlaybackChanged { add { } remove { } }
    public event EventHandler<MusicPlaybackFailedEventArgs>? PlaybackFailed { add { } remove { } }
    public event EventHandler<MusicPositionEventArgs>? PositionChanged { add { } remove { } }

    public void Play(string? path, int volumePercent, bool loop, TimeSpan startAt = default)
    {
        Played.Add((path, volumePercent, loop, startAt));
        IsPlaying = true;
    }

    public void FadeOut(TimeSpan duration) => FadeOuts.Add(duration);

    public void Stop()
    {
        Stops++;
        IsPlaying = false;
    }
}

// Herzerkennung und Kisscam in der Gastansicht: Auslösen mit allen Optionen, Musik, Abbruch (Esc/Mittelfinger), Anzeige-Eigenschaften.
public partial class BoothViewModelTests
{
    private sealed class KisscamRig
    {
        public Harness H { get; } = new();
        public FakeGestureService Gestures { get; } = new();
        public FakeSoundPlayer Sound { get; } = new();
        public TrackingTimeProvider PeaceTime { get; } = new();
        public FakeMusicPlayer Music { get; } = new();
        public FakeMusicLibrary Library { get; } = new();
        public PeaceOverlayViewModel Peace { get; }
        public BoothViewModel ViewModel { get; }
        public HeartSettings Heart => H.Settings.Current.Heart;

        /// <param name="music">Die Bibliothek enthält einen angehakten Song „musik.mp3“.</param>
        public KisscamRig(bool peaceEnabled = true, bool heartEnabled = true, Action<HeartSettings>? configure = null, int photoSize = 1,
            bool music = false)
        {
            if (music)
                Library.Set(FakeMusicLibrary.Song("musik.mp3", enabled: true));
            H.Settings.Current.Peace = new PeaceSettings { Enabled = peaceEnabled, HoldMilliseconds = 1000, AnalysesPerSecond = 5 };
            H.Settings.Current.Heart = new HeartSettings { Enabled = heartEnabled, HoldMilliseconds = 1000, KisscamSeconds = 10 };
            configure?.Invoke(H.Settings.Current.Heart);
            H.Settings.Current.Flow.PreviewSeconds = 30;
            H.Settings.Current.Flow.PrintingEnabled = false;
            H.Camera.Photo = Bitmap(photoSize);
            H.Composer.Result = CreateBitmap();
            H.Storage.Works = true;

            SynchronizationContext.SetSynchronizationContext(null);
            Peace = new PeaceOverlayViewModel(Gestures, PeaceTime, Sound, new GuestMoustacheState(), _ => 0);
            ViewModel = new BoothViewModel(H.Controller, H.Camera, H.Settings, null, Peace, Music, Library);
            H.Settings.RaiseSettingsChanged();
        }

        private static BitmapSource Bitmap(int size)
        {
            var bmp = BitmapSource.Create(size, size, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[size * size * 4], size * 4);
            bmp.Freeze();
            return bmp;
        }

        public static HeartTrigger Trigger(HeartAction action = HeartAction.Kisscam) => new(action, HeartShape.Classic, new NormPoint(0.5, 0.5),
            new NormRect(0.4, 0.4, 0.2, 0.2), [new NormRect(0.35, 0.1, 0.12, 0.2), new NormRect(0.53, 0.1, 0.12, 0.2)]);

        public bool Do(HeartAction action = HeartAction.Kisscam) => ViewModel.OnHeartAction(Trigger(action));

        public static void Pump() => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () => { });

        public void WaitForIdle()
        {
            Assert.True(SpinWait.SpinUntil(() => H.Controller.State == BoothState.Idle, TimeSpan.FromSeconds(10)));
            Pump();
        }

        public void Seconds(int count)
        {
            for (var i = 0; i < count; i++)
                H.Time.Advance(TimeSpan.FromSeconds(1));
        }

        /// <summary>Hält ein Herz mit einem Gesicht ins Bild (100-ms-Schritte der Peace-Zeit).</summary>
        public void ShowHeartFor(double seconds, IReadOnlyList<HandLandmarks>? hands = null)
        {
            var face = PeaceHandFixtures.Face(0.5, 0.3, 0.12);
            hands ??= HeartHandFixtures.Classic(cx: 0.5, cy: 0.6);
            for (var i = 0; i < (int)Math.Round(seconds * 10); i++)
            {
                PeaceTime.Advance(TimeSpan.FromMilliseconds(100));
                if (Gestures.Requests.Count > 0 && !Gestures.Last.IsDisposed)
                    Gestures.Last.RaiseUpdated([face], hands);
            }
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
    }

    // ---- Auslösen

    [Fact]
    public void Kisscam_StartsASingleShotCountdown_WithFrame_Prefix_AndMusic()
    {
        var rig = new KisscamRig(configure: h =>
        {
            h.KisscamSeconds = 12;
            h.KisscamBannerText = "  ";
            h.KisscamColor = KisscamColor.Gold;
            h.KisscamFilePrefix = "Kiss-Party";
            h.KisscamMusicVolumePercent = 55;
        }, music: true);
        rig.Library.Set(FakeMusicLibrary.Song("musik.mp3", enabled: true, start: 12, volume: 50));
        rig.H.Settings.Current.Layout.GuestChoices = [GuestLayout.StripDouble, GuestLayout.Single];
        rig.H.Settings.RaiseSettingsChanged();

        Assert.True(rig.Do());

        var c = rig.H.Controller;
        var vm = rig.ViewModel;
        Assert.Equal(BoothState.Countdown, c.State);
        Assert.Equal(CountdownStyle.Kisscam, c.CountdownStyle);
        Assert.Equal(12, c.CountdownValue);
        Assert.Equal(new PhotoFrame(PhotoFrameKind.Kisscam, HeartSettings.DefaultKisscamBannerText, KisscamColor.Gold), vm.KisscamFrame);
        Assert.Null(vm.KisscamZoomRegion); // Zoom aus
        Assert.Equal((@"C:\Musik\musik.mp3", 28, true, TimeSpan.FromSeconds(12)), Assert.Single(rig.Music.Played)); // 55 % × 50 % = 27,5 → 28
        // Einzelfoto trotz gewähltem Streifen.
        Assert.Equal(LayoutKind.SingleBorderless, rig.H.Composer.RequestedLayout!.Kind);

        // Das Foto entsteht mit Rahmen und eigenem Präfix.
        rig.Seconds(12);
        Assert.True(SpinUntil(rig.H, () => rig.H.Composer.LastFrame is not null && rig.H.Storage.LastStorage is not null, advanceInPreview: false));
        Assert.Equal(PhotoFrameKind.Kisscam, rig.H.Composer.LastFrame!.Kind);
        Assert.Equal(KisscamColor.Gold, rig.H.Composer.LastFrame.Color);
        Assert.Equal("Kiss-Party", rig.H.Storage.LastStorage!.FilePrefix);
    }

    [Fact]
    public void Kisscam_UsesTheConfiguredBannerText()
    {
        var rig = new KisscamRig(configure: h => h.KisscamBannerText = "Küss dich!");

        Assert.True(rig.Do());

        Assert.Equal("Küss dich!", rig.ViewModel.KisscamFrame!.BannerText);
    }

    [Fact]
    public void Kisscam_WithoutAnEnabledSong_DoesNotPlay()
    {
        var rig = new KisscamRig();
        rig.Library.Set(FakeMusicLibrary.Song("musik.mp3"), FakeMusicLibrary.Song("zwei.mp3", enabled: true, playable: false));

        Assert.True(rig.Do());

        Assert.Empty(rig.Music.Played);
    }

    [Fact]
    public void Kisscam_PicksRandomlyAmongTheEnabledPlayableSongs_AndRepetitionIsAllowed()
    {
        var rig = new KisscamRig(configure: h => h.KisscamMusicVolumePercent = 80);
        rig.Library.Set(
            FakeMusicLibrary.Song("a.mp3", enabled: true, start: 5),
            FakeMusicLibrary.Song("b.mp3", enabled: false),
            FakeMusicLibrary.Song("c.mp3", enabled: true, start: 20, volume: 50),
            FakeMusicLibrary.Song("d.mp3", enabled: true, playable: false));
        rig.ViewModel.MusicRandom = new FixedRandom(1); // zweiter Kandidat: c.mp3

        Assert.True(rig.Do());
        Assert.Equal((@"C:\Musik\c.mp3", 40, true, TimeSpan.FromSeconds(20)), Assert.Single(rig.Music.Played)); // 80 % × 50 %

        Assert.True(rig.ViewModel.TryCancel());
        rig.WaitForIdle();
        rig.ViewModel.MusicRandom = new FixedRandom(1);
        Assert.True(rig.Do()); // derselbe Song noch einmal
        Assert.Equal(@"C:\Musik\c.mp3", rig.Music.Played[1].Path);

        Assert.True(rig.ViewModel.TryCancel());
        rig.WaitForIdle();
        rig.ViewModel.MusicRandom = new FixedRandom(0);
        Assert.True(rig.Do());
        Assert.Equal((@"C:\Musik\a.mp3", 80, true, TimeSpan.FromSeconds(5)), rig.Music.Played[2]);
    }

    [Fact]
    public void Kisscam_StartBeyondTheSongEnd_PlaysFromZero()
    {
        var rig = new KisscamRig();
        rig.Library.Set(FakeMusicLibrary.Song("kurz.mp3", enabled: true, start: 100, duration: TimeSpan.FromSeconds(30)));

        Assert.True(rig.Do());

        Assert.Equal(TimeSpan.Zero, Assert.Single(rig.Music.Played).StartAt);
    }

    [Theory]
    [InlineData(KisscamLibraryStatus.ConfigBroken)]
    [InlineData(KisscamLibraryStatus.FolderUnavailable)]
    [InlineData(KisscamLibraryStatus.Loading)]
    public void Kisscam_RunsSilent_WhenTheLibraryIsNotReady(KisscamLibraryStatus status)
    {
        var rig = new KisscamRig();
        rig.Library.Set(status, false, FakeMusicLibrary.Song("a.mp3")); // nichts angehakt (defekte Config) bzw. noch nichts bekannt

        Assert.True(rig.Do()); // die Kisscam selbst läuft

        Assert.Equal(BoothState.Countdown, rig.H.Controller.State);
        Assert.Empty(rig.Music.Played);
    }

    [Fact]
    public void Kisscam_WithoutALibrary_RunsSilent()
    {
        var rig = new KisscamRig(music: true);
        var viewModel = new BoothViewModel(rig.H.Controller, rig.H.Camera, rig.H.Settings, null, rig.Peace, rig.Music, null);

        Assert.False(viewModel.HasMusicLibrary);
        Assert.True(viewModel.OnHeartAction(KisscamRig.Trigger()));

        Assert.Empty(rig.Music.Played);
    }

    [Fact]
    public void Kisscam_ReadsOnlyTheSnapshot_NeverBlocksOnTheFileSystem()
    {
        var rig = new KisscamRig(music: true);

        Assert.True(rig.Do());

        // Der Start liest ausschließlich Current (die Attrappe kennt kein Dateisystem) – und blockiert nicht.
        Assert.Single(rig.Music.Played);
        Assert.Empty(rig.Library.Folders);
    }

    [Fact]
    public void Kisscam_WithZoom_CropsThePhoto_AndExposesTheRegion()
    {
        var rig = new KisscamRig(configure: h => { h.KisscamZoomEnabled = true; h.KisscamZoomPercent = 200; }, photoSize: 8);

        Assert.True(rig.Do());

        // Herzbox und Gesichter ergeben den geplanten Ausschnitt (hier durch den Rand begrenzt: weniger als 200 %).
        var trigger = KisscamRig.Trigger();
        var expected = KisscamZoom.Plan(trigger.HeartBox, trigger.FaceBoxes, 200);
        Assert.NotNull(expected);
        Assert.Equal(expected, rig.ViewModel.KisscamZoomRegion);
        var (_, _, w, h) = KisscamZoom.ToPixelRect(expected!.Value, 8, 8);
        Assert.InRange(w, 1, 7);
        rig.Seconds(10);
        Assert.True(SpinUntil(rig.H, () => rig.H.Composer.LastPhotos is not null, advanceInPreview: false));
        Assert.Equal(w, rig.H.Composer.LastPhotos![0].PixelWidth);
        Assert.Equal(h, rig.H.Composer.LastPhotos![0].PixelHeight);
    }

    [Fact]
    public void Kisscam_WithoutZoom_KeepsThePhotoUncropped()
    {
        var rig = new KisscamRig(photoSize: 8);

        Assert.True(rig.Do());
        rig.Seconds(10);
        Assert.True(SpinUntil(rig.H, () => rig.H.Composer.LastPhotos is not null, advanceInPreview: false));

        Assert.Equal(8, rig.H.Composer.LastPhotos![0].PixelWidth);
    }

    [Fact]
    public void Kisscam_ClampsTheSecondsToThreeToSixty()
    {
        var rig = new KisscamRig(configure: h => h.KisscamSeconds = 1);

        Assert.True(rig.Do());

        Assert.Equal(3, rig.H.Controller.CountdownValue);
    }

    [Fact]
    public void HeartAction_IsBlocked_WhileAdminIsOpen_OrAnotherRunIsActive_OrTheCameraRestarts()
    {
        var rig = new KisscamRig();
        rig.ViewModel.IsAdminOpen = true;
        Assert.False(rig.Do());
        Assert.Equal(BoothState.Idle, rig.H.Controller.State);
        Assert.Empty(rig.Music.Played);
        rig.ViewModel.IsAdminOpen = false;

        Assert.True(rig.Do());
        Assert.False(rig.Do()); // läuft schon
        Assert.Equal(CountdownStyle.Kisscam, rig.H.Controller.CountdownStyle);
    }

    [Fact]
    public async Task HeartAction_IsBlocked_WhileTheCameraRestarts()
    {
        var rig = new KisscamRig();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.H.Camera.StartTask = start.Task;
        var restart = rig.ViewModel.RestartCameraAsync();
        Assert.True(rig.ViewModel.IsCameraBusy);

        Assert.False(rig.Do());
        Assert.Equal(BoothState.Idle, rig.H.Controller.State);

        start.SetResult();
        await restart;
    }

    [Fact]
    public void HeartAction_AfterShutdown_DoesNothing()
    {
        var rig = new KisscamRig(music: true);

        rig.ViewModel.Shutdown();

        Assert.False(rig.Do());
        Assert.Empty(rig.Music.Played);
        Assert.True(rig.Music.Stops >= 1); // Shutdown beendet eine laufende Wiedergabe
    }

    [Fact]
    public void HeartAction_FailedStart_LeavesNoFrameBehind()
    {
        var rig = new KisscamRig(music: true);
        rig.H.Camera.Status = CameraStatus.Starting;

        Assert.False(rig.Do());

        Assert.Null(rig.ViewModel.KisscamFrame);
        Assert.Empty(rig.Music.Played);
    }

    // ---- Musik und Abbruch

    [Fact]
    public void Music_FadesOutAtTheFlash_AndIsNotStopped()
    {
        var rig = new KisscamRig(configure: h => h.KisscamSeconds = 3, music: true);
        var flashes = 0;
        rig.ViewModel.FlashRequested += (_, _) => flashes++;
        Assert.True(rig.Do());
        Assert.Single(rig.Music.Played);

        rig.Seconds(3);
        Assert.True(SpinUntil(rig.H, () => rig.H.Controller.State == BoothState.Preview, advanceInPreview: false));
        KisscamRig.Pump();

        Assert.Equal(1, flashes); // der normale weiße Blitz
        Assert.Equal(TimeSpan.FromMilliseconds(800), Assert.Single(rig.Music.FadeOuts));
        Assert.Equal(0, rig.Music.Stops);
    }

    [Fact]
    public void Music_StopsWhenTheCountdownIsCancelledWithEsc()
    {
        var rig = new KisscamRig(music: true);
        Assert.True(rig.Do());
        rig.Seconds(2);
        Assert.Equal(0, rig.Music.Stops);

        Assert.True(rig.ViewModel.TryCancel());
        rig.WaitForIdle();

        Assert.True(rig.Music.Stops >= 1);
        Assert.Empty(rig.Music.FadeOuts);
        Assert.False(rig.ViewModel.ShowKisscamFrame);
        Assert.Null(rig.ViewModel.KisscamFrame);
        Assert.Null(rig.ViewModel.KisscamZoomRegion);
    }

    // ---- Mittelfinger

    [Fact]
    public void MiddleFinger_CancelsTheKisscam_WhenConfigured()
    {
        var rig = new KisscamRig(music: true);
        Assert.True(rig.Do());

        Assert.True(rig.ViewModel.OnMiddleFinger());
        rig.WaitForIdle();

        Assert.Equal(BoothState.Idle, rig.H.Controller.State);
        Assert.True(rig.Music.Stops >= 1);
    }

    [Fact]
    public void MiddleFinger_DoesNotCancelTheKisscam_WithActionOff_AndLeavesMoustachesAlone()
    {
        var rig = new KisscamRig();
        rig.H.Settings.Current.MiddleFinger.ActionCancelKisscam = false;
        rig.H.Settings.RaiseSettingsChanged();
        Assert.True(rig.Do());

        Assert.False(rig.ViewModel.OnMiddleFinger());

        Assert.Equal(BoothState.Countdown, rig.H.Controller.State);
        Assert.Equal(CountdownStyle.Kisscam, rig.H.Controller.CountdownStyle);
        Assert.False(rig.Peace.MiddleFingerWatching); // nichts aufzuheben: keine Hände im Countdown
    }

    [Fact]
    public void MiddleFinger_OnlyKisscamAction_DoesNotRemoveMoustaches()
    {
        var rig = new KisscamRig();
        rig.H.Settings.Current.MiddleFinger.ActionCancelPeace = false;
        rig.H.Settings.RaiseSettingsChanged();
        rig.Peace.StartMoustache(new PeaceTrigger(PeaceAction.Moustache, 1, null, new NormPoint(0.5, 0.5)));
        Assert.True(rig.Peace.MoustacheActive);

        Assert.False(rig.ViewModel.OnMiddleFinger()); // keine Kisscam, Peace-Aktion aus

        Assert.True(rig.Peace.MoustacheActive);
    }

    [Fact]
    public void MiddleFinger_HeldDuringTheKisscam_CancelsIt()
    {
        var rig = new KisscamRig();
        Assert.True(rig.Do());
        Assert.True(rig.Peace.MiddleFingerWatching);
        Assert.Equal(PeaceMode.FollowMoustache, rig.Peace.Mode);
        Assert.Equal(GestureNeeds.Faces | GestureNeeds.Hands, rig.Gestures.Last.Options.Needs);

        rig.ShowMiddleFinger(quiet: 0.5, hold: 2.0);
        rig.WaitForIdle();

        Assert.Equal(BoothState.Idle, rig.H.Controller.State);
        Assert.Equal(PeaceMode.Watching, rig.Peace.Mode);
    }

    // ---- Erkennung

    [Fact]
    public void Peace_Off_HeartOn_StillWatchesWithPairedHands_AndFiresOnlyTheHeart()
    {
        var rig = new KisscamRig(peaceEnabled: false);
        Assert.Equal(PeaceMode.Watching, rig.Peace.Mode);
        var options = rig.Gestures.Last.Options;
        Assert.Equal(GestureNeeds.Faces | GestureNeeds.Hands, options.Needs);
        Assert.True(options.PairedHands);

        // Ein gehaltenes Peace-Zeichen löst nichts aus.
        var face = PeaceHandFixtures.Face(0.5, 0.3, 0.12);
        for (var i = 0; i < 20; i++)
        {
            rig.PeaceTime.Advance(TimeSpan.FromMilliseconds(100));
            rig.Gestures.Last.RaiseUpdated([face], [PeaceHandFixtures.Hand(HandPose.Peace)]);
        }
        Assert.Equal(BoothState.Idle, rig.H.Controller.State);

        rig.ShowHeartFor(1.5);
        Assert.Equal(BoothState.Countdown, rig.H.Controller.State);
        Assert.Equal(CountdownStyle.Kisscam, rig.H.Controller.CountdownStyle);
    }

    [Fact]
    public void Heart_Off_Peace_Off_MeansNoDetection()
    {
        var rig = new KisscamRig(peaceEnabled: false, heartEnabled: false);

        Assert.Equal(PeaceMode.Off, rig.Peace.Mode);
        Assert.Empty(rig.Gestures.Requests);
    }

    [Fact]
    public void Heart_Off_Peace_On_DoesNotAskForPairedHands()
    {
        var rig = new KisscamRig(heartEnabled: false);

        Assert.Equal(PeaceMode.Watching, rig.Peace.Mode);
        Assert.False(rig.Gestures.Last.Options.PairedHands);
    }

    [Fact]
    public void Heart_Held_InIdle_StartsTheKisscam_AndDoesNotRetriggerAfterCancelWhileStillHeld()
    {
        var rig = new KisscamRig();

        rig.ShowHeartFor(1.5);

        Assert.Equal(BoothState.Countdown, rig.H.Controller.State);
        Assert.Equal(CountdownStyle.Kisscam, rig.H.Controller.CountdownStyle);

        // Abbrechen, die Hände bleiben oben: erst nach dem Lösen des Herzens wieder scharf.
        Assert.True(rig.ViewModel.TryCancel());
        rig.WaitForIdle();
        rig.ShowHeartFor(2.0);
        Assert.Equal(BoothState.Idle, rig.H.Controller.State);
    }

    // ---- Anzeige

    [Fact]
    public void Display_ShowsTheGuestTextAndTheDigitsThroughoutTheWholeCountdown_WithoutShade()
    {
        var rig = new KisscamRig(configure: h => h.KisscamSeconds = 6);
        var vm = rig.ViewModel;
        Assert.False(vm.ShowKisscamFrame);
        Assert.False(vm.ShowKisscamText);
        Assert.False(vm.ShowCountdown);
        Assert.False(vm.ShowCountdownShade);

        Assert.True(rig.Do());
        Assert.True(vm.IsKisscamCountdown);
        Assert.True(vm.ShowKisscamFrame);
        Assert.False(vm.IsPeaceCountdown);
        Assert.False(vm.IsSelfDestructCountdown);
        Assert.False(vm.IsPause);
        Assert.False(vm.ShowShotInfo);

        // Von der ersten bis zur letzten Sekunde: Text unten, große Ziffern, nie die Abdunklung.
        for (var expected = 6; expected >= 1; expected--)
        {
            Assert.Equal(expected, rig.H.Controller.CountdownValue);
            Assert.True(vm.ShowKisscamText, $"Text bei {expected}");
            Assert.True(vm.ShowCountdown, $"Countdown bei {expected}");
            Assert.True(vm.ShowNormalCountdownDigits, $"Ziffern bei {expected}");
            Assert.False(vm.ShowCountdownShade, $"keine Abdunklung bei {expected}");
            Assert.Equal(expected.ToString(), vm.CountdownText);
            Assert.Contains(expected.ToString(), vm.KisscamText);
            Assert.Contains("Sekunden", vm.KisscamText);
            Assert.True(vm.ShowKisscamFrame); // Rahmen bleibt
            if (expected > 1)
                rig.Seconds(1);
        }
    }

    [Fact]
    public void Display_ShortKisscam_ShowsTheTextToo()
    {
        var rig = new KisscamRig(configure: h => h.KisscamSeconds = 3);
        var vm = rig.ViewModel;

        Assert.True(rig.Do());

        Assert.Equal(3, rig.H.Controller.CountdownValue);
        Assert.True(vm.ShowKisscamText);
        Assert.True(vm.ShowNormalCountdownDigits);
        Assert.False(vm.ShowCountdownShade);
    }

    [Fact]
    public void Display_NormalCountdown_StillHasTheShade_AndNoKisscamText()
    {
        var h = new Harness();
        h.Settings.Current.Flow.CountdownSeconds = 3;

        Assert.True(h.ViewModel.TryTrigger());

        var vm = h.ViewModel;
        Assert.True(vm.IsCountdown);
        Assert.True(vm.ShowCountdown);
        Assert.True(vm.ShowCountdownShade);
        Assert.True(vm.ShowNormalCountdownDigits);
        Assert.False(vm.ShowKisscamText);
        Assert.False(vm.ShowKisscamFrame);
    }

    [Fact]
    public void Display_UsesTheCustomGuestText()
    {
        var rig = new KisscamRig(configure: h => h.KisscamText = "Noch {sekunden} s!");

        Assert.True(rig.Do());

        Assert.Equal("Noch 10 s!", rig.ViewModel.KisscamText);
    }

    [Fact]
    public void Display_NoPauseText_ForTheKisscam()
    {
        var rig = new KisscamRig();

        Assert.True(rig.Do());

        Assert.False(rig.ViewModel.IsPause);
    }

    [Fact]
    public void Display_FrameStaysUntilThePhotoIsDone_ThenGoesAway()
    {
        var rig = new KisscamRig(configure: h => h.KisscamSeconds = 3);
        Assert.True(rig.Do());
        rig.Seconds(3);
        Assert.True(SpinUntil(rig.H, () => rig.H.Controller.State == BoothState.Preview, advanceInPreview: false));
        KisscamRig.Pump();

        Assert.False(rig.ViewModel.ShowKisscamFrame);
        Assert.Null(rig.ViewModel.KisscamFrame);
        Assert.Null(rig.ViewModel.KisscamZoomRegion);
    }

    [Fact]
    public void Display_ZoomRegionOnlyWhileTheFrameIsShown()
    {
        var rig = new KisscamRig(configure: h => { h.KisscamZoomEnabled = true; h.KisscamZoomPercent = 200; });
        Assert.Null(rig.ViewModel.KisscamZoomRegion);
        Assert.True(rig.Do());
        Assert.NotNull(rig.ViewModel.KisscamZoomRegion);

        Assert.True(rig.ViewModel.TryCancel());
        rig.WaitForIdle();

        Assert.Null(rig.ViewModel.KisscamZoomRegion);
    }

    [Fact]
    public void HeartFeedback_FollowsTheHeartInIdle_AndCanBeSwitchedOff()
    {
        var rig = new KisscamRig();
        Assert.False(rig.ViewModel.ShowHeartFeedback);
        var changes = new List<string?>();
        rig.ViewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        rig.ShowHeartFor(0.5);

        Assert.True(rig.ViewModel.ShowHeartFeedback);
        Assert.NotNull(rig.ViewModel.HeartFeedback);
        Assert.InRange(rig.ViewModel.HeartFeedback!.Progress, 0.01, 1.0);
        Assert.Contains(nameof(BoothViewModel.HeartFeedback), changes);

        rig.H.Settings.Current.Heart.ShowHoldFeedback = false;
        rig.H.Settings.RaiseSettingsChanged();
        Assert.False(rig.ViewModel.ShowHeartFeedback);
    }

    [Fact]
    public void HeartFeedback_Hidden_WhenHeartIsOff_OrAdminIsOpen()
    {
        var rig = new KisscamRig();
        rig.ShowHeartFor(0.5);
        Assert.True(rig.ViewModel.ShowHeartFeedback);

        rig.ViewModel.IsAdminOpen = true;
        Assert.False(rig.ViewModel.ShowHeartFeedback);
        Assert.Null(rig.Peace.Heart);
    }

    // ---- BoothWindow

    [StaFact]
    public void BoothWindow_KisscamBindings_ShowFrameAndText_ThenTheDigits()
    {
        WpfCollection.EnsurePackUris();
        var rig = new KisscamRig(configure: h => h.KisscamSeconds = 5);
        var window = new BoothWindow(rig.ViewModel, new FakeAdminLauncher(), rig.H.Settings, new NoDisplays());
        var overlay = (Fotobox.Views.Booth.Heart.KisscamOverlay)FindLogical<Fotobox.Views.Booth.Heart.KisscamOverlay>(window)!;
        var digits = (System.Windows.FrameworkElement)window.FindName("CountdownBlock");
        var content = (System.Windows.FrameworkElement)window.Content;
        void Layout()
        {
            content.Measure(new System.Windows.Size(1920, 1080));
            content.Arrange(new System.Windows.Rect(0, 0, 1920, 1080));
            content.UpdateLayout();
        }

        Layout();
        Assert.Equal(System.Windows.Visibility.Collapsed, overlay.Visibility);

        Assert.True(rig.Do());
        Layout();
        Assert.Equal(System.Windows.Visibility.Visible, overlay.Visibility);
        Assert.NotNull(overlay.Frame);
        Assert.True(overlay.ShowText);
        Assert.Contains("5", overlay.Text);
        Assert.Equal(System.Windows.Visibility.Visible, digits.Visibility); // Ziffern von Anfang an
        Assert.Equal(360, ((System.Windows.Controls.TextBlock)digits).FontSize); // etwas kleiner als beim normalen Countdown (460)

        rig.Seconds(2); // 3: Text und Ziffern bleiben
        Layout();
        Assert.True(overlay.ShowText);
        Assert.Contains("3", overlay.Text);
        Assert.Equal(System.Windows.Visibility.Visible, overlay.Visibility);
        Assert.Equal(System.Windows.Visibility.Visible, digits.Visibility);
    }

    [StaFact]
    public void BoothWindow_Kisscam_HasNoShade()
    {
        WpfCollection.EnsurePackUris();
        var rig = new KisscamRig(configure: h => h.KisscamSeconds = 5);
        var window = new BoothWindow(rig.ViewModel, new FakeAdminLauncher(), rig.H.Settings, new NoDisplays());
        var digits = (System.Windows.Controls.TextBlock)window.FindName("CountdownBlock");
        var content = (System.Windows.FrameworkElement)window.Content;
        // Die Abdunklung ist das erste Border-Kind des Countdown-Rasters (Geschwister des Ziffern-Rasters).
        var countdownGrid = (System.Windows.Controls.Grid)System.Windows.Media.VisualTreeHelper.GetParent(
            System.Windows.Media.VisualTreeHelper.GetParent(digits));
        var shade = countdownGrid.Children.OfType<System.Windows.Controls.Border>().First();

        Assert.True(rig.Do());
        content.Measure(new System.Windows.Size(1920, 1080));
        content.Arrange(new System.Windows.Rect(0, 0, 1920, 1080));
        content.UpdateLayout();

        Assert.Equal(System.Windows.Visibility.Visible, countdownGrid.Visibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, shade.Visibility);
        Assert.Equal(360, digits.FontSize);
        Assert.IsType<System.Windows.Media.RadialGradientBrush>(shade.Background);
    }

    [StaFact]
    public void BoothWindow_NormalCountdown_ShowsTheShade_AndFullSizeDigits()
    {
        WpfCollection.EnsurePackUris();
        var h = new Harness();
        h.Settings.Current.Flow.CountdownSeconds = 3;
        var window = new BoothWindow(h.ViewModel, new FakeAdminLauncher(), h.Settings, new NoDisplays());
        var digits = (System.Windows.Controls.TextBlock)window.FindName("CountdownBlock");
        var content = (System.Windows.FrameworkElement)window.Content;
        var countdownGrid = (System.Windows.Controls.Grid)System.Windows.Media.VisualTreeHelper.GetParent(
            System.Windows.Media.VisualTreeHelper.GetParent(digits));
        var shade = countdownGrid.Children.OfType<System.Windows.Controls.Border>().First();

        Assert.True(h.ViewModel.TryTrigger());
        content.Measure(new System.Windows.Size(1920, 1080));
        content.Arrange(new System.Windows.Rect(0, 0, 1920, 1080));
        content.UpdateLayout();

        Assert.Equal(System.Windows.Visibility.Visible, countdownGrid.Visibility);
        Assert.Equal(System.Windows.Visibility.Visible, shade.Visibility);
        Assert.Equal(460, digits.FontSize);
    }

    /// <summary>
    /// Sichtprüfung per Bilddatei, kein Test im engeren Sinn: Läuft nur mit <c>FOTOBOX_RENDER_DIR</c> (sonst sofort beendet). Rendert die
    /// Gastansicht im Leerlauf mit Herz-Fortschritt, im Kisscam-Countdown (Text und Ziffern von Anfang an) und in den letzten Sekunden auf einem
    /// synthetischen Livebild.
    /// </summary>
    [StaFact]
    [Trait("Category", "Visual")]
    public void Render_BoothWindow_HeartAndKisscam()
    {
        var dir = Environment.GetEnvironmentVariable("FOTOBOX_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            return;
        System.IO.Directory.CreateDirectory(dir);
        WpfCollection.EnsurePackUris();

        var rig = new KisscamRig(configure: h => h.KisscamSeconds = 10);
        var window = new BoothWindow(rig.ViewModel, new FakeAdminLauncher(), rig.H.Settings, new NoDisplays());
        var content = (System.Windows.Controls.Grid)window.Content;
        content.Measure(new System.Windows.Size(1920, 1080));
        content.Arrange(new System.Windows.Rect(0, 0, 1920, 1080));

        // Szene statt schwarzem Livebild: Verlauf direkt über dem (leeren, schwarzen) Kamerabild-Steuerelement, unter allen Overlays.
        var scene = new System.Windows.Controls.Border
        {
            Background = new System.Windows.Media.LinearGradientBrush(
                System.Windows.Media.Color.FromRgb(0x5B, 0x8D, 0xC9), System.Windows.Media.Color.FromRgb(0x2E, 0x3A, 0x4E), 90),
            IsHitTestVisible = false,
        };
        ((System.Windows.Controls.Panel)FindLogical<Fotobox.Views.Booth.Heart.LiveZoomHost>(window)!.Child).Children.Insert(1, scene);

        void Save(string name)
        {
            content.Measure(new System.Windows.Size(1920, 1080));
            content.Arrange(new System.Windows.Rect(0, 0, 1920, 1080));
            content.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1920, 1080, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = System.IO.File.Create(System.IO.Path.Combine(dir, name));
            encoder.Save(stream);
        }

        rig.ShowHeartFor(0.6);
        Save("booth-heart-feedback.png");

        rig.PeaceTime.Advance(TimeSpan.FromSeconds(2));
        Assert.True(rig.Do());
        Save("booth-kisscam-text.png");

        rig.Seconds(7);
        Save("booth-kisscam-digits.png");
    }
}
