using System.IO;
using System.Windows.Input;
using Fotobox.Core;
using Fotobox.Services.Camera;
using Fotobox.ViewModels.Booth;
using Fotobox.Views.Booth;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Gastansicht im Videomodus: Tasten, Sperren, Hinweiszeile, Overlay, Kameraton (teilt Harness und Fakes mit den Foto-Tests).</summary>
public partial class BoothViewModelTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);
    private static readonly KeyChord F5 = new(Key.F5, ModifierKeys.None);
    private static readonly KeyChord F6 = new(Key.F6, ModifierKeys.None);

    /// <summary>Video ohne Countdown, Mindestdauer 0; <paramref name="configure"/> ändert die Videoeinstellungen.</summary>
    private static Harness VideoHarness(Action<VideoSettings>? configure = null)
    {
        var h = new Harness();
        h.Settings.Current.Video.CountdownSeconds = 0;
        h.Settings.Current.Video.MinDurationSeconds = 0;
        configure?.Invoke(h.Settings.Current.Video);
        return h;
    }

    private static Harness RecordingHarness(Action<VideoSettings>? configure = null)
    {
        var h = VideoHarness(configure);
        Assert.True(h.ViewModel.TryStartVideo());
        Assert.Equal(BoothState.Recording, h.Controller.State);
        return h;
    }

    // ---- Tastenzuordnung

    [Fact]
    public void HandleKey_SameKeyInStartAndStop_IsToggle()
    {
        var h = new Harness();

        // Standard: dieselbe Taste (Eingabetaste) startet und beendet.
        Assert.Equal(BoothKeyAction.VideoToggle, h.ViewModel.HandleKey(Enter));
        Assert.Equal(BoothKeyAction.Trigger, h.ViewModel.HandleKey(Space));
    }

    [Fact]
    public void Toggle_StartsInIdle_AndStopsInRecording_OncePerKeyPress()
    {
        var h = VideoHarness();

        Assert.True(h.ViewModel.ExecuteVideoAction(h.ViewModel.HandleKey(Enter)));
        Assert.Equal(BoothState.Recording, h.Controller.State);

        // Ein weiterer Druck beendet; nie Start und Stopp für denselben Druck (kein Neustart direkt danach).
        Assert.True(h.ViewModel.ExecuteVideoAction(h.ViewModel.HandleKey(Enter)));
        Assert.Contains(h.Controller.State, new[] { BoothState.VideoSaving, BoothState.VideoSaved });
        Assert.Equal(1, h.Camera.StartRecordingCalls);
        Assert.Equal(1, h.Camera.LastRecording!.StopCalls);
    }

    [Fact]
    public void SeparateKeys_StartOnlyStarts_StopOnlyStops()
    {
        var h = VideoHarness();
        h.Settings.Current.Input.VideoStartKeys = ["F5"];
        h.Settings.Current.Input.VideoStopKeys = ["F6"];
        h.Settings.RaiseSettingsChanged();

        Assert.Equal(BoothKeyAction.VideoStart, h.ViewModel.HandleKey(F5));
        Assert.Equal(BoothKeyAction.VideoStop, h.ViewModel.HandleKey(F6));
        Assert.Equal(BoothKeyAction.None, h.ViewModel.HandleKey(Enter));

        // Stopp-Taste im Leerlauf: nichts. Start-Taste in der Aufnahme: kein zweiter Start.
        Assert.False(h.ViewModel.ExecuteVideoAction(BoothKeyAction.VideoStop));
        Assert.Equal(BoothState.Idle, h.Controller.State);
        Assert.True(h.ViewModel.ExecuteVideoAction(BoothKeyAction.VideoStart));
        Assert.Equal(BoothState.Recording, h.Controller.State);
        Assert.False(h.ViewModel.ExecuteVideoAction(BoothKeyAction.VideoStart));
        Assert.Equal(1, h.Camera.StartRecordingCalls);

        Assert.True(h.ViewModel.ExecuteVideoAction(BoothKeyAction.VideoStop));
        Assert.NotEqual(BoothState.Recording, h.Controller.State);
    }

    [Fact]
    public void VideoDisabled_IgnoresVideoKeys()
    {
        var h = VideoHarness(v => v.Enabled = false);

        Assert.Equal(BoothKeyAction.None, h.ViewModel.HandleKey(Enter));
        Assert.False(h.ViewModel.ExecuteVideoAction(BoothKeyAction.VideoToggle));
        Assert.False(h.ViewModel.TryStartVideo());
        Assert.Equal(BoothState.Idle, h.Controller.State);
        Assert.False(h.ViewModel.ShowVideoHint);
    }

    [Fact]
    public void NoVideoKeysAssigned_MapsNothing_AndHidesVideoHint()
    {
        var h = VideoHarness();
        h.Settings.Current.Input.VideoStartKeys = [];
        h.Settings.Current.Input.VideoStopKeys = [];
        h.Settings.RaiseSettingsChanged();

        Assert.Equal(BoothKeyAction.None, h.ViewModel.HandleKey(Enter));
        Assert.Equal("", h.ViewModel.VideoHintText);
        Assert.False(h.ViewModel.ShowVideoHint);
    }

    [Fact]
    public void ExecuteVideoAction_IgnoresOtherActions()
    {
        var h = VideoHarness();

        Assert.False(h.ViewModel.ExecuteVideoAction(BoothKeyAction.Trigger));
        Assert.False(h.ViewModel.ExecuteVideoAction(BoothKeyAction.Cancel));
        Assert.False(h.ViewModel.ExecuteVideoAction(BoothKeyAction.None));
        Assert.Equal(BoothState.Idle, h.Controller.State);
    }

    [Fact]
    public void Video_IsBlocked_WhileAdminIsOpen_OrCameraIsNotRunning()
    {
        var h = VideoHarness();
        h.ViewModel.IsAdminOpen = true;
        Assert.False(h.ViewModel.TryStartVideo());
        Assert.False(h.ViewModel.TryToggleVideo());
        h.ViewModel.IsAdminOpen = false;

        h.Camera.Status = CameraStatus.NoDevice;
        Assert.False(h.ViewModel.TryStartVideo());
        Assert.Equal(BoothState.Idle, h.Controller.State);
    }

    [StaFact]
    public void BoothWindow_VideoKey_TogglesOncePerKeyDownEvent()
    {
        var h = VideoHarness();
        var window = new BoothWindow(h.ViewModel, new FakeAdminLauncher(), h.Settings, new NoDisplays());
        using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters());

        RaiseKeyDown(window, source, Key.Enter);
        Assert.Equal(BoothState.Recording, h.Controller.State);

        RaiseKeyDown(window, source, Key.Enter);
        Assert.NotEqual(BoothState.Recording, h.Controller.State);
        Assert.Equal(1, h.Camera.StartRecordingCalls);
    }

    [StaFact]
    public void BoothWindow_HeldVideoKey_RepeatDoesNotToggle()
    {
        var h = VideoHarness();
        var window = new BoothWindow(h.ViewModel, new FakeAdminLauncher(), h.Settings, new NoDisplays());
        using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters());

        // Taste gehalten: erster Druck startet, alle Wiederholungen (IsRepeat) bleiben ohne Wirkung.
        RaiseKeyDown(window, source, Key.Enter);
        Assert.Equal(BoothState.Recording, h.Controller.State);
        for (var i = 0; i < 5; i++)
        {
            var repeat = RaiseKeyDown(window, source, Key.Enter, isRepeat: true);
            Assert.True(repeat.IsRepeat);
            Assert.True(repeat.Handled);
        }

        Assert.Equal(BoothState.Recording, h.Controller.State);
        Assert.Equal(1, h.Camera.StartRecordingCalls);
        Assert.Equal(0, h.Camera.LastRecording!.StopCalls);
    }

    [StaFact]
    public void BoothWindow_RepeatVideoKey_InIdleStartsNothing()
    {
        var h = VideoHarness();
        var window = new BoothWindow(h.ViewModel, new FakeAdminLauncher(), h.Settings, new NoDisplays());
        using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters());

        RaiseKeyDown(window, source, Key.Enter, isRepeat: true);

        Assert.Equal(BoothState.Idle, h.Controller.State);
        Assert.Equal(0, h.Camera.StartRecordingCalls);
    }

    [StaFact]
    public void BoothWindow_PhotoKey_DoesNothingDuringRecording()
    {
        var h = RecordingHarness();
        var window = new BoothWindow(h.ViewModel, new FakeAdminLauncher(), h.Settings, new NoDisplays());
        using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters());

        var args = RaiseKeyDown(window, source, Key.Space);

        Assert.True(args.Handled);
        Assert.Equal(BoothState.Recording, h.Controller.State);
    }

    private static KeyEventArgs RaiseKeyDown(BoothWindow window, System.Windows.PresentationSource source, Key key,
        bool isRepeat = false)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        if (isRepeat)
        {
            // IsRepeat setzt WPF nur intern (SetRepeat, vom Eingabesystem bei gehaltener Taste); per Reflection nachgestellt.
            var setRepeat = typeof(KeyEventArgs).GetMethod("SetRepeat",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(setRepeat);
            setRepeat.Invoke(args, [true]);
        }
        window.RaiseEvent(args);
        return args;
    }

    // ---- Sperren in Video-Zuständen

    /// <summary>Bringt die Box in den Zustand <paramref name="target"/> eines Videodurchgangs und hält ihn dort.</summary>
    private static Harness HarnessInVideoState(BoothState target)
    {
        var h = VideoHarness(v =>
        {
            v.SuccessDisplaySeconds = 30;
            if (target == BoothState.VideoCountdown)
                v.CountdownSeconds = 5;
        });
        h.Settings.Current.Layout.GuestChoices = [GuestLayout.Single, GuestLayout.StripDouble];
        h.Settings.RaiseSettingsChanged();
        if (target == BoothState.VideoSaving)
            h.VideoStorage.CommitHang = true;

        Assert.True(h.ViewModel.TryStartVideo());
        if (target != BoothState.VideoCountdown)
        {
            Assert.Equal(BoothState.Recording, h.Controller.State);
            if (target != BoothState.Recording)
                Assert.True(h.ViewModel.TryStopVideo());
        }
        Assert.Equal(target, h.Controller.State);
        return h;
    }

    [Theory]
    [InlineData(BoothState.VideoCountdown)]
    [InlineData(BoothState.Recording)]
    [InlineData(BoothState.VideoSaving)]
    [InlineData(BoothState.VideoSaved)]
    public void VideoStates_BlockPhotoClickWheelAdminAndWindowMove(BoothState state)
    {
        var h = HarnessInVideoState(state);
        var vm = h.ViewModel;

        // Foto (Taste und Mausklick laufen beide über TryTrigger)
        Assert.False(vm.TryTrigger());
        Assert.Equal(BoothKeyAction.Trigger, vm.HandleMouseButton(MouseButton.Left));
        Assert.False(vm.TryTrigger());
        Assert.Equal(state, h.Controller.State);

        // Mausrad (Layoutwahl)
        var selected = vm.SelectedLayout;
        vm.ScrollLayout(wheelUp: false);
        vm.CycleLayout(1);
        Assert.False(vm.CanChooseLayout);
        Assert.False(vm.IsLayoutPreviewVisible);
        Assert.Equal(selected, vm.SelectedLayout);

        // Admin-Taste und Monitorwechsel
        Assert.False(vm.CanOpenAdmin);
        Assert.False(vm.CanMoveWindow);
        Assert.False(vm.ShowHint);
        Assert.False(vm.ShowVideoHint);
    }

    [Fact]
    public void CanMoveWindow_IsTrueAgainAfterVideoEnds()
    {
        var h = RecordingHarness(v => v.SuccessDisplaySeconds = 3);
        Assert.False(h.ViewModel.CanMoveWindow);

        Assert.True(h.ViewModel.TryStopVideo());
        Assert.Equal(BoothState.VideoSaved, h.Controller.State);
        Assert.False(h.ViewModel.CanMoveWindow);

        h.Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(BoothState.Idle, h.Controller.State);
        Assert.True(h.ViewModel.CanMoveWindow);
        Assert.True(h.ViewModel.CanOpenAdmin);
    }

    [Fact]
    public void Escape_InVideoCountdown_CancelsSilently_ViaViewModel()
    {
        var h = HarnessInVideoState(BoothState.VideoCountdown);

        Assert.True(h.ViewModel.TryCancel());

        // Der Abbruch läuft über das Token der Sitzung und kann auf dem Threadpool abschließen.
        Assert.True(SpinWait.SpinUntil(() => h.Controller.State == BoothState.Idle, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, h.Camera.StartRecordingCalls);
    }

    [Fact]
    public void Escape_InRecording_HasNoEffect_ViaViewModel()
    {
        var h = RecordingHarness();   // Mindestlänge 0: Stopp wäre sofort möglich

        Assert.False(h.ViewModel.TryCancel());
        Assert.Equal(BoothState.Recording, h.Controller.State);
        Assert.Equal(0, h.Camera.LastRecording!.StopCalls);

        // Die Videotaste beendet weiterhin.
        Assert.True(h.ViewModel.ExecuteVideoAction(h.ViewModel.HandleKey(Enter)));
        Assert.NotEqual(BoothState.Recording, h.Controller.State);
        Assert.Equal(1, h.Camera.LastRecording!.StopCalls);
    }

    // ---- Video-Hinweiszeile

    [Fact]
    public void VideoHint_IsShownInIdle_WithAutomaticText()
    {
        var h = new Harness();

        Assert.True(h.ViewModel.ShowVideoHint);
        Assert.Equal("Drücke die Eingabetaste für ein Video 🎬", h.ViewModel.VideoHintText);
        Assert.Equal(HintStyle.Pill, h.ViewModel.VideoHintStyle);
    }

    [Fact]
    public void VideoHint_UsesCustomTextAndStyle()
    {
        var h = new Harness();
        h.Settings.Current.GuestScreen.VideoHintText = "Film ab mit {Videotaste_1}!";
        h.Settings.Current.GuestScreen.VideoHintStyle = HintStyle.Banner;

        Assert.Equal($"Film ab mit {Enter.DisplayName}!", h.ViewModel.VideoHintText);
        Assert.Equal(HintStyle.Banner, h.ViewModel.VideoHintStyle);
    }

    [Fact]
    public void PhotoHint_ResolvesVideoPlaceholders_OnlyWithVideoEnabled()
    {
        var h = new Harness();
        h.Settings.Current.GuestScreen.HintText = "Foto: {Fototaste_1} – Video: {Videotaste_1}";

        Assert.Equal($"Foto: {Space.DisplayName} – Video: {Enter.DisplayName}", h.ViewModel.HintText);

        h.Settings.Current.Video.Enabled = false;
        Assert.Equal($"Foto: {Space.DisplayName} – Video:", h.ViewModel.HintText);
    }

    [Fact]
    public void VideoHint_ClickPlaceholder_FollowsMouseTrigger_LikePhotoHint()
    {
        var h = new Harness();
        h.Settings.Current.GuestScreen.HintText = "Foto: {Fototaste_1} {Klick}";
        h.Settings.Current.GuestScreen.VideoHintText = "Video mit {Videotaste_1} {Klick}";

        Assert.Equal($"Video mit {Enter.DisplayName} oder klicke", h.ViewModel.VideoHintText);
        Assert.Equal($"Foto: {Space.DisplayName} oder klicke", h.ViewModel.HintText);

        h.Settings.Current.Input.TriggerOnLeftClick = false;
        h.Settings.Current.Input.TriggerOnRightClick = false;
        Assert.Equal($"Video mit {Enter.DisplayName}", h.ViewModel.VideoHintText);
        Assert.Equal($"Foto: {Space.DisplayName}", h.ViewModel.HintText);
    }

    [Fact]
    public void VideoHint_FollowsSameConditionsAsPhotoHint()
    {
        var h = new Harness();
        Assert.True(h.ViewModel.ShowHint);
        Assert.True(h.ViewModel.ShowVideoHint);

        h.Camera.Status = CameraStatus.Starting;
        Assert.False(h.ViewModel.ShowHint);
        Assert.False(h.ViewModel.ShowVideoHint);

        h.Camera.Status = CameraStatus.Running;
        h.Settings.Current.Video.Enabled = false;
        Assert.True(h.ViewModel.ShowHint);
        Assert.False(h.ViewModel.ShowVideoHint);
    }

    [Fact]
    public void VideoHint_HidesWhileLayoutPreviewIsVisible_AndNotifies()
    {
        var h = new Harness();
        h.Settings.Current.Layout.GuestChoices = [GuestLayout.Single, GuestLayout.StripDouble];
        h.Settings.RaiseSettingsChanged();
        var changed = new List<string?>();
        h.ViewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.True(h.ViewModel.ShowVideoHint);

        h.ViewModel.CycleLayout(1);

        Assert.True(h.ViewModel.IsLayoutPreviewVisible);
        Assert.False(h.ViewModel.ShowVideoHint);
        Assert.Contains(nameof(BoothViewModel.ShowVideoHint), changed);
    }

    // ---- Stopp-Hinweis während der Aufnahme

    [Fact]
    public void StopHint_IsHiddenBeforeMinLength_ShownFromThenOn_AndHiddenAfterStop()
    {
        var h = RecordingHarness(v => v.MinDurationSeconds = 2);
        Assert.False(h.ViewModel.ShowVideoStopHint);   // gleich nach dem Start: Stopp wirkt noch nicht

        h.Time.Advance(OneSecond);
        Assert.False(h.Controller.CanStopVideo);
        Assert.False(h.ViewModel.ShowVideoStopHint);

        h.Time.Advance(OneSecond);
        Assert.True(h.Controller.CanStopVideo);
        Assert.True(h.ViewModel.ShowVideoStopHint);
        Assert.Equal("Drücke die Eingabetaste, um die Videoaufnahme zu beenden", h.ViewModel.VideoStopHintText);

        Assert.True(h.ViewModel.TryStopVideo());
        Assert.False(h.ViewModel.ShowVideoStopHint);
    }

    [Fact]
    public void StopHint_NotifiesWhenStopBecomesPossible_ViaSecondTick()
    {
        var h = RecordingHarness(v => v.MinDurationSeconds = 2);
        var changed = new List<string?>();
        h.ViewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        h.Time.Advance(OneSecond);
        h.Time.Advance(OneSecond);

        // Der Sekundentakt der Aufnahme löst Refresh() aus (alle Eigenschaften neu lesen), auch für den Stopp-Hinweis.
        Assert.Contains(string.Empty, changed);
        Assert.True(h.ViewModel.ShowVideoStopHint);
    }

    [Fact]
    public void StopHint_IsHiddenInIdle_AndDuringCountdown_AndAfterTheRecording()
    {
        var h = VideoHarness(v => v.CountdownSeconds = 3);
        Assert.False(h.ViewModel.ShowVideoStopHint);   // Leerlauf

        Assert.True(h.ViewModel.TryStartVideo());
        Assert.Equal(BoothState.VideoCountdown, h.Controller.State);
        Assert.False(h.ViewModel.ShowVideoStopHint);   // Countdown

        for (var i = 0; i < 3; i++)
            h.Time.Advance(OneSecond);
        Assert.True(SpinWait.SpinUntil(() => h.Controller.State == BoothState.Recording, TimeSpan.FromSeconds(5)));
        Assert.True(h.ViewModel.ShowVideoStopHint);    // Mindestlänge 0

        Assert.True(h.ViewModel.TryStopVideo());
        Assert.NotEqual(BoothState.Recording, h.Controller.State);
        Assert.False(h.ViewModel.ShowVideoStopHint);   // VideoSaving/VideoSaved
    }

    [Fact]
    public void StopHint_IsHidden_WhenSwitchOff_WithoutStopKeys_OrWithVideoModeOff()
    {
        var h = RecordingHarness();
        Assert.True(h.ViewModel.ShowVideoStopHint);

        h.Settings.Current.Video.ShowStopHint = false;
        Assert.False(h.ViewModel.ShowVideoStopHint);

        h.Settings.Current.Video.ShowStopHint = true;
        Assert.True(h.ViewModel.ShowVideoStopHint);

        h.Settings.Current.Input.VideoStopKeys = [];
        h.Settings.RaiseSettingsChanged();
        Assert.Equal("", h.ViewModel.VideoStopHintText);
        Assert.False(h.ViewModel.ShowVideoStopHint);

        h.Settings.Current.Input.VideoStopKeys = ["Enter"];
        h.Settings.RaiseSettingsChanged();
        Assert.True(h.ViewModel.ShowVideoStopHint);

        h.Settings.Current.Video.Enabled = false;
        Assert.False(h.ViewModel.ShowVideoStopHint);
    }

    [Fact]
    public void StopHint_UsesVideoHintStyle_AndCustomText_WithStopKeyPlaceholders()
    {
        var h = RecordingHarness();
        h.Settings.Current.GuestScreen.VideoHintStyle = HintStyle.Neon;
        h.Settings.Current.Input.VideoStopKeys = ["F6", "Space"];
        h.Settings.Current.Video.StopHintText = "Zum Beenden {Taste} ({Stopptaste_1}, {Stopptasten})";
        h.Settings.RaiseSettingsChanged();

        Assert.Equal(HintStyle.Neon, h.ViewModel.VideoStopHintStyle);
        Assert.Equal("Zum Beenden F6 oder Leertaste (F6, F6 oder Leertaste)", h.ViewModel.VideoStopHintText);
        Assert.True(h.ViewModel.ShowVideoStopHint);
    }

    [Fact]
    public void StopHint_WithSingleSpaceKey_UsesArticle_AndEmptyTextMeansDefault()
    {
        var h = RecordingHarness();
        h.Settings.Current.Input.VideoStopKeys = ["Space"];
        h.Settings.Current.Video.StopHintText = null;
        h.Settings.RaiseSettingsChanged();

        Assert.Equal("Drücke die Leertaste, um die Videoaufnahme zu beenden", h.ViewModel.VideoStopHintText);
    }

    // ---- Countdown

    [Fact]
    public void VideoCountdown_ShowsCountdown_WithNeutralTextAtZero()
    {
        var h = VideoHarness(v => v.CountdownSeconds = 3);
        Assert.True(h.ViewModel.TryStartVideo());

        Assert.True(h.ViewModel.IsVideoCountdown);
        Assert.True(h.ViewModel.ShowCountdown);
        Assert.False(h.ViewModel.IsCountdown);
        Assert.Equal("3", h.ViewModel.CountdownText);
        Assert.False(h.ViewModel.IsVideoStarting);

        h.Time.Advance(OneSecond);
        Assert.Equal("2", h.ViewModel.CountdownText);
    }

    [Fact]
    public void VideoCountdown_AtZero_ShowsStartingInsteadOfNumber()
    {
        var h = VideoHarness();
        h.VideoStorage.PrepareHang = true;   // Vorbereitung hängt: Zustand bleibt Countdown mit Wert 0

        Assert.True(h.ViewModel.TryStartVideo());

        Assert.Equal(BoothState.VideoCountdown, h.Controller.State);
        Assert.Equal("", h.ViewModel.CountdownText);
        Assert.True(h.ViewModel.IsVideoStarting);
        Assert.False(h.ViewModel.IsPause);   // „Gleich geht es weiter“ gehört zum Foto
    }

    // ---- Overlay

    [Fact]
    public void FormatClock_FormatsMinutesAndSeconds()
    {
        Assert.Equal("00:00", BoothViewModel.FormatClock(TimeSpan.Zero, roundUp: false));
        Assert.Equal("01:05", BoothViewModel.FormatClock(TimeSpan.FromSeconds(65.9), roundUp: false));
        Assert.Equal("01:06", BoothViewModel.FormatClock(TimeSpan.FromSeconds(65.1), roundUp: true));
        Assert.Equal("30:00", BoothViewModel.FormatClock(TimeSpan.FromMinutes(30), roundUp: true));
        Assert.Equal("00:00", BoothViewModel.FormatClock(TimeSpan.FromSeconds(-3), roundUp: true));
    }

    [Fact]
    public void Recording_OverlayShowsElapsedAndRemaining()
    {
        var h = RecordingHarness(v => v.MaxDurationSeconds = 300);

        Assert.True(h.ViewModel.IsRecording);
        Assert.Equal("00:00", h.ViewModel.ElapsedText);
        Assert.Equal("noch 05:00", h.ViewModel.RemainingText);

        h.Time.Advance(TimeSpan.FromSeconds(65));

        Assert.Equal("01:05", h.ViewModel.ElapsedText);
        Assert.Equal("noch 03:55", h.ViewModel.RemainingText);
        Assert.True(h.ViewModel.ShowRecordingOverlay);
        Assert.True(h.ViewModel.ShowRecDot);
        Assert.True(h.ViewModel.ShowElapsed);
        Assert.True(h.ViewModel.ShowRemaining);
        Assert.False(h.ViewModel.IsRecordingWarning);
    }

    [Fact]
    public void Recording_WarningReplacesRemaining_InLastSeconds()
    {
        var h = RecordingHarness(v =>
        {
            v.MaxDurationSeconds = 60;
            v.WarningSeconds = 10;
        });

        h.Time.Advance(TimeSpan.FromSeconds(49));   // 11 s Rest
        Assert.False(h.ViewModel.IsRecordingWarning);
        Assert.True(h.ViewModel.ShowRemaining);

        h.Time.Advance(OneSecond);                   // 10 s Rest
        Assert.True(h.ViewModel.IsRecordingWarning);
        Assert.Equal("Noch 10 s", h.ViewModel.RecordingWarningText);
        Assert.False(h.ViewModel.ShowRemaining);     // die große Warnung ersetzt die kleine Restzeit

        h.Time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal("Noch 6 s", h.ViewModel.RecordingWarningText);
        Assert.Equal("00:54", h.ViewModel.ElapsedText);
    }

    [Fact]
    public void Recording_WarningTextForLongerWarning_UsesMinutes()
    {
        var h = RecordingHarness(v =>
        {
            v.MaxDurationSeconds = 300;
            v.WarningSeconds = 120;
        });

        h.Time.Advance(TimeSpan.FromSeconds(180));   // 120 s Rest

        Assert.True(h.ViewModel.IsRecordingWarning);
        Assert.Equal("Noch 02:00 min", h.ViewModel.RecordingWarningText);
    }

    [Theory]
    [InlineData(true, false, false, 0, true)]
    [InlineData(false, true, false, 0, true)]
    [InlineData(false, false, true, 0, true)]
    [InlineData(false, false, false, 10, false)]   // Warnung noch nicht aktiv (Anfang der Aufnahme)
    [InlineData(false, false, false, 0, false)]
    public void Recording_ElementsAreSwitchedIndividually(bool dot, bool elapsed, bool remaining, int warning, bool expectInfo)
    {
        var h = RecordingHarness(v =>
        {
            v.ShowRecDot = dot;
            v.ShowElapsed = elapsed;
            v.ShowRemaining = remaining;
            v.WarningSeconds = warning;
            v.RecordAudio = false;   // kein „ohne Ton“-Symbol
        });

        Assert.Equal(dot, h.ViewModel.ShowRecDot);
        Assert.Equal(elapsed, h.ViewModel.ShowElapsed);
        Assert.Equal(remaining, h.ViewModel.ShowRemaining);
        Assert.Equal(expectInfo, h.ViewModel.ShowRecordingInfo);
        Assert.Equal(expectInfo, h.ViewModel.ShowRecordingOverlay);
        Assert.False(h.ViewModel.ShowNoAudioHint);
    }

    [Fact]
    public void Recording_AllElementsOff_WarningStillShowsInLastSeconds()
    {
        var h = RecordingHarness(v =>
        {
            v.MaxDurationSeconds = 30;
            v.WarningSeconds = 10;
            v.ShowRecDot = false;
            v.ShowElapsed = false;
            v.ShowRemaining = false;
            v.RecordAudio = false;
        });
        Assert.False(h.ViewModel.ShowRecordingOverlay);

        h.Time.Advance(TimeSpan.FromSeconds(21));

        Assert.True(h.ViewModel.IsRecordingWarning);
        Assert.True(h.ViewModel.ShowRecordingOverlay);
        Assert.False(h.ViewModel.ShowRecordingInfo);
    }

    [Fact]
    public void Recording_OverlayIsHiddenOutsideRecording()
    {
        var h = new Harness();

        Assert.False(h.ViewModel.ShowRecordingOverlay);
        Assert.False(h.ViewModel.ShowRecDot);
        Assert.False(h.ViewModel.IsRecordingWarning);
        Assert.False(h.ViewModel.ShowNoAudioHint);
    }

    [Theory]
    [InlineData(true, true, false)]    // Ton gewünscht und vorhanden
    [InlineData(true, false, true)]    // Ton gewünscht, Aufnahme ohne Ton -> Symbol
    [InlineData(false, false, false)]  // kein Ton gewünscht
    public void Recording_ShowsMutedHint_OnlyWhenAudioWantedButMissing(bool recordAudio, bool hasAudio, bool expected)
    {
        var h = VideoHarness(v => v.RecordAudio = recordAudio);
        h.Camera.RecordingHasAudio = hasAudio;

        Assert.True(h.ViewModel.TryStartVideo());

        Assert.Equal(expected, h.ViewModel.ShowNoAudioHint);
    }

    // ---- Speichern, Gespeichert, Fehler

    [Fact]
    public void VideoSaving_ShowsStatusBanner()
    {
        var h = HarnessInVideoState(BoothState.VideoSaving);

        Assert.True(h.ViewModel.IsVideoSaving);
        Assert.True(h.ViewModel.ShowProcessingBanner);
        Assert.Equal("Video wird gespeichert …", h.ViewModel.StatusText);
        Assert.False(h.ViewModel.ShowRecordingOverlay);
    }

    [Fact]
    public void VideoSaved_ShowsDefaultOrCustomText()
    {
        var h = HarnessInVideoState(BoothState.VideoSaved);
        Assert.True(h.ViewModel.ShowVideoSaved);
        Assert.Equal(VideoSettings.DefaultSuccessText, h.ViewModel.VideoSavedText);
        Assert.False(h.ViewModel.ShowProcessingBanner);

        var custom = VideoHarness(v => v.SuccessText = "Danke fürs Filmen!");
        Assert.True(custom.ViewModel.TryStartVideo());
        Assert.True(custom.ViewModel.TryStopVideo());
        Assert.Equal("Danke fürs Filmen!", custom.ViewModel.VideoSavedText);
    }

    [Fact]
    public void VideoFailure_UsesExistingErrorScreen()
    {
        var h = VideoHarness();
        h.VideoStorage.PrepareFailure = new IOException("kein Zugriff");

        Assert.True(h.ViewModel.TryStartVideo());

        Assert.True(h.ViewModel.IsError);
        Assert.Contains("Video", h.ViewModel.ErrorText);
        Assert.True(h.ViewModel.CanOpenAdmin);
        Assert.True(h.ViewModel.CanMoveWindow);
        Assert.False(h.ViewModel.ShowVideoSaved);
    }

    // ---- Kameraton

    [StaFact]
    public async Task Initialize_ConfiguresAudio_BeforeStartingCamera()
    {
        var h = new Harness(markReady: false);

        await h.ViewModel.InitializeAsync();

        Assert.Equal(["audio:default", "start"], h.Camera.Calls);
    }

    [StaFact]
    public async Task Restart_ConfiguresAudioAgain_WithCurrentMicrophone_BeforeEveryStart()
    {
        var h = new Harness();
        h.Settings.Current.Video.MicrophoneId = "mic-1";
        await h.ViewModel.RestartCameraAsync();

        h.Settings.Current.Video.MicrophoneId = "mic-2";
        await h.ViewModel.RestartCameraAsync();

        h.Settings.Current.Video.RecordAudio = false;
        await h.ViewModel.RestartCameraAsync();

        h.Settings.Current.Video.RecordAudio = true;
        h.Settings.Current.Video.Enabled = false;
        await h.ViewModel.RestartCameraAsync();

        Assert.Equal(
            ["audio:mic-1", "start", "audio:mic-2", "start", "audio:none", "start", "audio:none", "start"],
            h.Camera.Calls);
    }

    [Fact]
    public void AudioFor_MapsVideoSettings()
    {
        Assert.Null(CameraServiceExtensions.AudioFor(null));
        Assert.Null(CameraServiceExtensions.AudioFor(new VideoSettings { Enabled = false }));
        Assert.Null(CameraServiceExtensions.AudioFor(new VideoSettings { RecordAudio = false }));
        Assert.Equal(new CameraAudio(null), CameraServiceExtensions.AudioFor(new VideoSettings()));
        Assert.Equal(new CameraAudio("m"), CameraServiceExtensions.AudioFor(new VideoSettings { MicrophoneId = "m" }));
    }

    // ---- Fakes für Video

    private sealed class FakeRecording(bool hasAudio) : IVideoRecording
    {
        private int _stopCalls;

        public bool HasAudio { get; } = hasAudio;
        public event EventHandler<string>? Faulted { add { } remove { } }
        public int StopCalls => Volatile.Read(ref _stopCalls);

        public Task StopAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _stopCalls);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeVideoStorage : IVideoStorage
    {
        private static readonly VideoTarget Target =
            new("fake", "fake/tmp.mp4", VideoSettings.DefaultFilePrefix, new DateTime(2026, 1, 2, 3, 4, 5));

        public Exception? PrepareFailure { get; set; }
        public bool PrepareHang { get; set; }
        public bool CommitHang { get; set; }

        public string ResolveTargetFolder(VideoSettings video, DateTime timestamp) => "fake";

        public Task<VideoTarget> PrepareAsync(VideoSettings video, CameraResolution? resolution, TimeSpan maxDuration,
            DateTime timestamp, CancellationToken ct = default)
        {
            if (PrepareFailure is not null)
                return Task.FromException<VideoTarget>(PrepareFailure);
            return PrepareHang ? new TaskCompletionSource<VideoTarget>().Task : Task.FromResult(Target);
        }

        public Task<string> CommitAsync(VideoTarget target, CancellationToken ct = default) =>
            CommitHang ? new TaskCompletionSource<string>().Task : Task.FromResult("fake/Video.mp4");

        public void Discard(VideoTarget target) { }
    }
}
