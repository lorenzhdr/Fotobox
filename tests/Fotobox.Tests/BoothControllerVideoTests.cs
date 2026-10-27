using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Fotobox.Core;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Videoablauf des <see cref="BoothController"/> (teilt Harness und Fakes mit den Foto-Tests).</summary>
public partial class BoothControllerTests
{
    private const string StartErrorText = "Das Video konnte nicht gestartet werden. Bitte versuche es noch einmal.";
    private const string SaveErrorText = "Das Video konnte nicht gespeichert werden. Bitte versuche es noch einmal.";
    private const string DiskErrorText = "Nicht genug Speicherplatz für ein Video. Bitte sag dem Gastgeber Bescheid.";

    private static Harness VideoHarness(int countdown = 0, Action<VideoSettings>? configure = null)
    {
        var h = new Harness();
        h.Settings.Current.Video.CountdownSeconds = countdown;
        configure?.Invoke(h.Settings.Current.Video);
        return h;
    }

    /// <summary>Startet ein Video (Countdown 0) und läuft bis zum Zustand Recording.</summary>
    private static Harness RecordingHarness(Action<VideoSettings>? configure = null)
    {
        var h = VideoHarness(0, configure);
        Assert.True(h.Controller.TryStartVideo());
        Assert.Equal(BoothState.Recording, h.Controller.State);
        return h;
    }

    // ---- Start-Bedingungen ----

    [Fact]
    public void TryStartVideo_InStartingState_ReturnsFalse()
    {
        var h = new Harness(markReady: false);

        Assert.False(h.Controller.TryStartVideo());
        Assert.Equal(BoothState.Starting, h.Controller.State);
        Assert.Equal(0, h.VideoStorage.PrepareCalls);
    }

    [Theory]
    [InlineData(CameraStatus.Stopped)]
    [InlineData(CameraStatus.Starting)]
    [InlineData(CameraStatus.NoDevice)]
    [InlineData(CameraStatus.AccessDenied)]
    [InlineData(CameraStatus.Error)]
    public void TryStartVideo_CameraNotRunning_ReturnsFalse(CameraStatus status)
    {
        var h = VideoHarness();
        h.Camera.Status = status;

        Assert.False(h.Controller.TryStartVideo());
        Assert.Equal(BoothState.Idle, h.Controller.State);
    }

    [Fact]
    public void TryStartVideo_VideoDisabled_ReturnsFalse_AndKeepsGateFree()
    {
        var h = VideoHarness(configure: v => v.Enabled = false);

        Assert.False(h.Controller.TryStartVideo());
        Assert.Equal(BoothState.Idle, h.Controller.State);
        Assert.Equal(0, h.VideoStorage.PrepareCalls);

        // Das Gate bleibt frei: Foto geht, und nach dem Aktivieren auch Video.
        Assert.True(h.Controller.TryTrigger());
        h.RunToIdle();
        h.Settings.Current.Video.Enabled = true;
        Assert.True(h.Controller.TryStartVideo());
    }

    [Fact]
    public void TryStartVideo_WithoutVideoStorage_ReturnsFalse()
    {
        var h = new Harness();
        var controller = new BoothController(h.Camera, h.Composer, h.Printer, h.Storage, h.Settings, h.Time);
        controller.MarkReady();

        Assert.False(controller.TryStartVideo());
        Assert.False(controller.TryToggleVideo());
        Assert.Equal(BoothState.Idle, controller.State);
    }

    [Fact]
    public void TryStartVideo_OnlyFromIdle()
    {
        var h = VideoHarness(countdown: 3, v => v.MinDurationSeconds = 0);

        Assert.True(h.Controller.TryStartVideo());
        Assert.Equal(BoothState.VideoCountdown, h.Controller.State);
        Assert.False(h.Controller.TryStartVideo());   // nicht im Countdown
        h.AdvanceUntil(() => h.Controller.State == BoothState.Recording);
        Assert.False(h.Controller.TryStartVideo());   // nicht während der Aufnahme

        Assert.True(h.Controller.TryStopVideo());
        h.AdvanceUntil(() => h.Controller.State == BoothState.VideoSaved);
        Assert.False(h.Controller.TryStartVideo());   // nicht in der Erfolgsmeldung
        h.RunToIdle();
        Assert.True(h.Controller.TryStartVideo());    // danach wieder
    }

    // ---- Countdown ----

    [Fact]
    public void Countdown0_StartsRecordingImmediately_WithPreparedTarget()
    {
        var h = VideoHarness(countdown: 0, v => { v.MaxDurationSeconds = 120; v.Quality = VideoQuality.Low; });
        var states = h.RecordStates();

        Assert.True(h.Controller.TryStartVideo());

        Assert.Equal(BoothState.Recording, h.Controller.State);
        Assert.Equal(0, h.Controller.CountdownValue);
        Assert.Equal(1, h.VideoStorage.PrepareCalls);
        Assert.Equal(TimeSpan.FromSeconds(120), h.VideoStorage.LastMaxDuration);
        Assert.Equal(h.Camera.ActiveResolution, h.VideoStorage.LastResolution);
        Assert.Equal(h.Time.GetLocalNow().DateTime, h.VideoStorage.LastTimestamp);
        Assert.Equal(1, h.Camera.StartRecordingCalls);
        Assert.Equal(h.VideoStorage.Target.TempPath, h.Camera.LastRecordingRequest!.FilePath);
        Assert.Equal(VideoQuality.Low, h.Camera.LastRecordingRequest.Quality);
        Assert.Equal([BoothState.VideoCountdown, BoothState.Recording], states);
    }

    [Fact]
    public void Countdown3_TicksOncePerSecond_ThenPreparesAndRecords()
    {
        var h = VideoHarness(countdown: 3);

        Assert.True(h.Controller.TryStartVideo());
        Assert.Equal(BoothState.VideoCountdown, h.Controller.State);
        Assert.Equal(3, h.Controller.CountdownValue);

        h.Time.Advance(Second);
        Assert.Equal(2, h.Controller.CountdownValue);
        h.Time.Advance(Second);
        Assert.Equal(1, h.Controller.CountdownValue);
        Assert.Equal(0, h.VideoStorage.PrepareCalls);
        Assert.Equal(0, h.Camera.StartRecordingCalls);

        h.Time.Advance(Second);
        h.Drain();
        Assert.Equal(BoothState.Recording, h.Controller.State);
        Assert.Equal(0, h.Controller.CountdownValue);
        Assert.Equal(1, h.VideoStorage.PrepareCalls);
        Assert.Equal(1, h.Camera.StartRecordingCalls);
    }

    [Fact]
    public void Escape_InCountdown_CancelsSilently_NothingPrepared()
    {
        var h = VideoHarness(countdown: 3);
        var states = h.RecordStates();
        h.Controller.TryStartVideo();
        h.Time.Advance(Second);

        Assert.True(h.Controller.TryCancel());
        h.RunToIdle();

        Assert.Equal(0, h.VideoStorage.PrepareCalls);
        Assert.Equal(0, h.Camera.StartRecordingCalls);
        Assert.Empty(h.VideoStorage.Discarded);
        Assert.Null(h.Controller.ErrorMessage);
        Assert.Equal([BoothState.VideoCountdown, BoothState.Idle], states);
        Assert.True(h.Controller.TryStartVideo());   // Gate wieder frei
    }

    [Fact]
    public void Stop_InCountdown_IsIgnored()
    {
        var h = VideoHarness(countdown: 3);
        h.Controller.TryStartVideo();

        Assert.False(h.Controller.TryStopVideo());
        Assert.False(h.Controller.TryToggleVideo());
        Assert.Equal(BoothState.VideoCountdown, h.Controller.State);
    }

    [Fact]
    public void Escape_WhilePreparing_CancelsAndDiscardsLateTarget()
    {
        var h = VideoHarness();
        h.VideoStorage.PrepareHang = true;
        h.Controller.TryStartVideo();
        Assert.Equal(BoothState.VideoCountdown, h.Controller.State);

        Assert.True(h.Controller.TryCancel());
        h.WaitUntil(() => h.Controller.State == BoothState.Idle);
        Assert.Equal(0, h.Camera.StartRecordingCalls);

        // Kommt das Ziel nachträglich doch noch, wird es verworfen.
        h.VideoStorage.PrepareSource.SetResult(h.VideoStorage.Target);
        h.WaitUntil(() => h.VideoStorage.Discarded.Count == 1);
    }

    [Fact]
    public void Escape_WhileStartingRecording_CancelsAndCleansUpLateRecording()
    {
        var h = VideoHarness();
        var late = new TaskCompletionSource<IVideoRecording>();
        h.Camera.RecordingStarter = (_, _) => late.Task;
        h.Controller.TryStartVideo();
        Assert.Equal(BoothState.VideoCountdown, h.Controller.State);
        Assert.Equal(1, h.Camera.StartRecordingCalls);

        Assert.True(h.Controller.TryCancel());
        h.WaitUntil(() => h.Controller.State == BoothState.Idle);

        var recording = new FakeRecording();
        late.SetResult(recording);
        h.WaitUntil(() => recording.DisposeCalls == 1 && h.VideoStorage.Discarded.Count == 1);
        Assert.Equal(1, recording.StopCalls);
        Assert.Equal(0, h.VideoStorage.CommitCalls);
    }

    // ---- Stopp / Mindestlänge ----

    [Fact]
    public void Stop_BeforeMinDuration_IsIgnored_ThenSaves()
    {
        var h = RecordingHarness();   // Mindestlänge 2 s
        var states = h.RecordStates();

        h.Time.Advance(Second);
        h.Drain();
        Assert.False(h.Controller.CanStopVideo);
        Assert.False(h.Controller.TryStopVideo());
        Assert.False(h.Controller.TryCancel());
        Assert.False(h.Controller.TryToggleVideo());
        Assert.Equal(BoothState.Recording, h.Controller.State);
        Assert.Equal(0, h.Camera.LastRecording!.StopCalls);

        h.Time.Advance(Second);
        h.Drain();
        Assert.True(h.Controller.CanStopVideo);
        Assert.True(h.Controller.TryToggleVideo());
        Assert.False(h.Controller.TryStopVideo());   // nur einmal
        h.Drain();

        Assert.Equal(BoothState.VideoSaved, h.Controller.State);
        Assert.Equal(1, h.Camera.LastRecording!.StopCalls);
        Assert.Equal(1, h.VideoStorage.CommitCalls);
        Assert.Equal(h.VideoStorage.CommitPath, h.Controller.LastSavedVideo);
        Assert.Equal(VideoSettings.DefaultSuccessText, h.Controller.VideoSavedText);
        Assert.Equal(TimeSpan.FromSeconds(2), h.Controller.RecordingElapsed);

        h.RunToIdle();
        Assert.Equal([BoothState.VideoSaving, BoothState.VideoSaved, BoothState.Idle], states);
        Assert.Null(h.Controller.ErrorMessage);
        Assert.Equal(1, h.Camera.LastRecording!.DisposeCalls);
    }

    [Fact]
    public void MinDurationZero_AllowsStopImmediately()
    {
        var h = RecordingHarness(v => v.MinDurationSeconds = 0);

        Assert.True(h.Controller.TryStopVideo());
        h.Drain();
        Assert.Equal(BoothState.VideoSaved, h.Controller.State);
    }

    [Fact]
    public void Escape_InRecording_Saves_OnlyAfterMinDuration()
    {
        var h = RecordingHarness();
        h.AdvanceBy(Second, Second);
        Assert.False(h.Controller.TryCancel());   // zu kurz

        h.AdvanceBy(Second, Second);
        Assert.True(h.Controller.TryCancel());
        h.Drain();

        Assert.Equal(BoothState.VideoSaved, h.Controller.State);
        Assert.Equal(1, h.VideoStorage.CommitCalls);
        Assert.Empty(h.VideoStorage.Discarded);
    }

    [Fact]
    public void CustomSuccessText_IsShown_WhitespaceFallsBackToDefault()
    {
        var h = VideoHarness(configure: v => v.SuccessText = "Fertig, danke!");
        Assert.True(h.Controller.TryStartVideo());
        Assert.Equal("Fertig, danke!", h.Controller.VideoSavedText);

        var h2 = VideoHarness(configure: v => v.SuccessText = "   ");
        Assert.True(h2.Controller.TryStartVideo());
        Assert.Equal(VideoSettings.DefaultSuccessText, h2.Controller.VideoSavedText);
    }

    [Fact]
    public void SavedMessage_StaysForSuccessDisplaySeconds()
    {
        var h = RecordingHarness(v => { v.MinDurationSeconds = 0; v.SuccessDisplaySeconds = 5; });
        h.Controller.TryStopVideo();
        h.Drain();
        Assert.Equal(BoothState.VideoSaved, h.Controller.State);

        h.AdvanceBy(TimeSpan.FromSeconds(4), Second);
        Assert.Equal(BoothState.VideoSaved, h.Controller.State);
        h.AdvanceBy(Second, Second);
        Assert.Equal(BoothState.Idle, h.Controller.State);
    }

    // ---- Maximaldauer, Anzeigewerte ----

    [Fact]
    public void AutoStop_AtMaxDuration_SavesWithoutMinCheck()
    {
        var h = RecordingHarness(v => { v.MaxDurationSeconds = 10; v.MinDurationSeconds = 30; });
        var states = h.RecordStates();

        h.AdvanceUntil(() => h.Controller.State == BoothState.VideoSaved);

        Assert.Equal(TimeSpan.FromSeconds(10), h.Controller.RecordingElapsed);
        Assert.Equal(TimeSpan.Zero, h.Controller.RecordingRemaining);
        Assert.Equal(1, h.Camera.LastRecording!.StopCalls);
        Assert.Equal(1, h.VideoStorage.CommitCalls);
        h.RunToIdle();
        Assert.Equal([BoothState.VideoSaving, BoothState.VideoSaved, BoothState.Idle], states);
    }

    [Fact]
    public void Recording_ElapsedRemainingAndChanged_UpdateEverySecond()
    {
        var h = RecordingHarness(v => { v.MaxDurationSeconds = 60; v.WarningSeconds = 0; });
        var changed = 0;
        h.Controller.Changed += (_, _) => changed++;

        Assert.Equal(TimeSpan.Zero, h.Controller.RecordingElapsed);
        Assert.Equal(TimeSpan.FromSeconds(60), h.Controller.RecordingRemaining);

        for (var i = 1; i <= 5; i++)
        {
            var before = changed;
            h.Time.Advance(Second);
            h.Drain();
            Assert.Equal(TimeSpan.FromSeconds(i), h.Controller.RecordingElapsed);
            Assert.Equal(TimeSpan.FromSeconds(60 - i), h.Controller.RecordingRemaining);
            Assert.True(changed > before, "Changed muss jede Sekunde feuern.");
        }

        Assert.False(h.Controller.IsRecordingWarning);   // Warnung aus
    }

    [Fact]
    public void WarningFlag_SetFromWarningSecondsRemaining()
    {
        var h = RecordingHarness(v => { v.MaxDurationSeconds = 10; v.WarningSeconds = 3; });

        h.AdvanceBy(TimeSpan.FromSeconds(6), Second);
        Assert.Equal(TimeSpan.FromSeconds(4), h.Controller.RecordingRemaining);
        Assert.False(h.Controller.IsRecordingWarning);

        h.AdvanceBy(Second, Second);
        Assert.Equal(TimeSpan.FromSeconds(3), h.Controller.RecordingRemaining);
        Assert.True(h.Controller.IsRecordingWarning);

        h.AdvanceUntil(() => h.Controller.State == BoothState.VideoSaved);
        Assert.False(h.Controller.IsRecordingWarning);   // nur während der Aufnahme
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecordingHasAudio_FollowsRecording(bool hasAudio)
    {
        var h = VideoHarness();
        h.Camera.RecordingToReturn = new FakeRecording { HasAudio = hasAudio };

        h.Controller.TryStartVideo();

        Assert.Equal(hasAudio, h.Controller.RecordingHasAudio);
    }

    // ---- Fehlerpfade ----

    [Fact]
    public void Faulted_RescuesPartialVideo_OnSynchronizationContext()
    {
        var h = RecordingHarness();
        var recording = h.Camera.LastRecording!;
        var threadIds = new List<int>();
        h.Controller.StateChanged += (_, _) => threadIds.Add(Environment.CurrentManagedThreadId);
        h.AdvanceBy(TimeSpan.FromSeconds(4), Second);

        var worker = new Thread(() => recording.RaiseFaulted("Kamera getrennt"));
        worker.Start();
        worker.Join();
        h.WaitUntil(() => h.Controller.State == BoothState.VideoSaved);

        Assert.Equal(1, recording.StopCalls);
        Assert.Equal(1, h.VideoStorage.CommitCalls);
        Assert.Null(h.Controller.ErrorMessage);
        Assert.Equal(TimeSpan.FromSeconds(4), h.Controller.RecordingElapsed);
        Assert.All(threadIds, id => Assert.Equal(Environment.CurrentManagedThreadId, id));
        h.RunToIdle();
    }

    [Fact]
    public void Faulted_AndStopThrows_StillTriesCommit()
    {
        var h = VideoHarness();
        var recording = new FakeRecording { StopFailure = new IOException("Datei defekt") };
        h.Camera.RecordingToReturn = recording;
        h.Controller.TryStartVideo();

        recording.RaiseFaulted("Mikrofon weg");
        h.Drain();

        Assert.Equal(BoothState.VideoSaved, h.Controller.State);
        Assert.Equal(1, h.VideoStorage.CommitCalls);
        Assert.Empty(h.VideoStorage.Discarded);
    }

    [Fact]
    public void StopThrows_CommitStillAttempted_AndSucceeds()
    {
        var h = VideoHarness(configure: v => v.MinDurationSeconds = 0);
        h.Camera.RecordingToReturn = new FakeRecording { StopFailure = new InvalidOperationException("kaputt") };
        h.Controller.TryStartVideo();

        h.Controller.TryStopVideo();
        h.Drain();

        Assert.Equal(BoothState.VideoSaved, h.Controller.State);
        Assert.Equal(1, h.VideoStorage.CommitCalls);
    }

    [Fact]
    public void StopAndCommitFail_ShowsSaveError_Discards_ThenIdle()
    {
        var h = VideoHarness(configure: v => v.MinDurationSeconds = 0);
        h.Camera.RecordingToReturn = new FakeRecording { StopFailure = new InvalidOperationException("kaputt") };
        h.VideoStorage.CommitFailure = new IOException("leer");
        var states = h.RecordStates();
        h.Controller.TryStartVideo();

        h.Controller.TryStopVideo();
        h.Drain();

        Assert.Equal(BoothState.Error, h.Controller.State);
        Assert.Equal(SaveErrorText, h.Controller.ErrorMessage);
        Assert.Equal(1, h.VideoStorage.CommitCalls);
        Assert.Single(h.VideoStorage.Discarded);
        Assert.Null(h.Controller.LastSavedVideo);

        h.RunToIdle();
        Assert.Equal([BoothState.VideoCountdown, BoothState.Recording, BoothState.VideoSaving, BoothState.Error, BoothState.Idle], states);
        Assert.True(h.Controller.TryTrigger());   // wieder nutzbar
    }

    [Fact]
    public void InsufficientDiskSpace_ShowsGuestText_NoRecording()
    {
        var h = VideoHarness();
        h.VideoStorage.PrepareFailure = new InsufficientDiskSpaceException("zu wenig Platz");

        h.Controller.TryStartVideo();
        h.Drain();

        Assert.Equal(BoothState.Error, h.Controller.State);
        Assert.Equal(DiskErrorText, h.Controller.ErrorMessage);
        Assert.Equal(0, h.Camera.StartRecordingCalls);

        h.RunToIdle();
        Assert.Equal(BoothState.Idle, h.Controller.State);
    }

    [Fact]
    public void PrepareFails_ShowsStartError()
    {
        var h = VideoHarness();
        h.VideoStorage.PrepareFailure = new IOException("Ordner nicht beschreibbar");

        h.Controller.TryStartVideo();
        h.Drain();

        Assert.Equal(BoothState.Error, h.Controller.State);
        Assert.Equal(StartErrorText, h.Controller.ErrorMessage);
        Assert.Equal(0, h.Camera.StartRecordingCalls);
        h.RunToIdle();
    }

    [Fact]
    public void StartRecordingFails_DiscardsTarget_ShowsStartError()
    {
        var h = VideoHarness();
        h.Camera.RecordingStarter = (_, _) => Task.FromException<IVideoRecording>(new InvalidOperationException("Kamera belegt"));

        h.Controller.TryStartVideo();
        h.Drain();

        Assert.Equal(BoothState.Error, h.Controller.State);
        Assert.Equal(StartErrorText, h.Controller.ErrorMessage);
        Assert.Single(h.VideoStorage.Discarded);
        Assert.Equal(0, h.VideoStorage.CommitCalls);

        h.RunToIdle();
        Assert.True(h.Controller.TryStartVideo());
    }

    [Fact]
    public void ErrorScreen_StaysForErrorDisplaySeconds()
    {
        var h = VideoHarness();
        h.VideoStorage.PrepareFailure = new IOException("x");
        h.Controller.TryStartVideo();
        h.Drain();

        h.AdvanceBy(TimeSpan.FromSeconds(3), Second);
        Assert.Equal(BoothState.Error, h.Controller.State);
        h.AdvanceBy(Second, Second);
        Assert.Equal(BoothState.Idle, h.Controller.State);
    }

    // ---- Foto und Video schließen sich aus ----

    [Fact]
    public void PhotoTrigger_IsBlocked_DuringEveryVideoState()
    {
        var h = VideoHarness(countdown: 2, v => v.MinDurationSeconds = 0);
        Assert.True(h.Controller.TryStartVideo());
        Assert.False(h.Controller.TryTrigger());   // VideoCountdown

        h.AdvanceUntil(() => h.Controller.State == BoothState.Recording);
        Assert.False(h.Controller.TryTrigger());   // Recording

        h.Controller.TryStopVideo();
        h.Drain();
        Assert.Equal(BoothState.VideoSaved, h.Controller.State);
        Assert.False(h.Controller.TryTrigger());   // VideoSaved

        h.RunToIdle();
        Assert.Equal(0, h.Camera.CaptureCount);
        Assert.True(h.Controller.TryTrigger());    // danach wieder Foto
    }

    [Fact]
    public void PhotoTrigger_IsBlocked_WhileVideoSaves()
    {
        var h = RecordingHarness(v => v.MinDurationSeconds = 0);
        h.VideoStorage.CommitHang = true;
        h.Controller.TryStopVideo();
        h.Drain();

        Assert.Equal(BoothState.VideoSaving, h.Controller.State);
        Assert.False(h.Controller.TryTrigger());
        Assert.False(h.Controller.TryStartVideo());
    }

    [Fact]
    public void StartVideo_IsBlocked_DuringPhotoSession_AndDoesNotDisturbIt()
    {
        var h = VideoHarness();
        h.Settings.Current.Flow.PrintingEnabled = false;
        Assert.True(h.Controller.TryTrigger());

        Assert.False(h.Controller.TryStartVideo());   // Countdown
        h.AdvanceUntil(() => h.Controller.State == BoothState.Preview);
        Assert.False(h.Controller.TryStartVideo());   // Vorschau
        Assert.False(h.Controller.TryToggleVideo());

        h.RunToIdle();
        Assert.Single(h.Storage.Saved);
        Assert.Equal(0, h.VideoStorage.PrepareCalls);
        Assert.True(h.Controller.TryStartVideo());
    }

    [Fact]
    public void EscapeInPhotoCountdown_StillCancelsPhoto_NotAffectedByVideo()
    {
        var h = VideoHarness();
        h.Controller.TryTrigger();

        Assert.True(h.Controller.TryCancel());
        h.RunToIdle();
        Assert.Equal(0, h.Camera.CaptureCount);
    }

    // ---- Zeitüberschreitungen ----

    [Fact]
    public void PrepareHangs_TimesOut_StartError_ThenIdle()
    {
        var h = VideoHarness();
        h.VideoStorage.PrepareHang = true;
        h.Controller.TryStartVideo();
        Assert.Equal(BoothState.VideoCountdown, h.Controller.State);

        h.Time.Advance(BoothController.VideoPrepareTimeout);
        h.WaitUntil(() => h.Controller.State == BoothState.Error);

        Assert.Equal(StartErrorText, h.Controller.ErrorMessage);
        Assert.Equal(0, h.Camera.StartRecordingCalls);
        h.RunToIdle();
    }

    [Fact]
    public void StartRecordingHangs_TimesOut_StartError_DiscardsTarget()
    {
        var h = VideoHarness();
        h.Camera.RecordingStarter = (_, _) => new TaskCompletionSource<IVideoRecording>().Task;
        h.Controller.TryStartVideo();

        h.Time.Advance(BoothController.VideoStartTimeout);
        h.WaitUntil(() => h.Controller.State == BoothState.Error);

        Assert.Equal(StartErrorText, h.Controller.ErrorMessage);
        Assert.Equal(0, h.VideoStorage.CommitCalls);
        h.RunToIdle();
    }

    [Fact]
    public void StopHangs_TimesOut_SaveError_DiscardsWithoutCommit_ThenIdle()
    {
        var h = VideoHarness(configure: v => v.MinDurationSeconds = 0);
        h.Camera.RecordingToReturn = new FakeRecording { StopHang = true };
        h.Controller.TryStartVideo();

        h.Controller.TryStopVideo();
        h.Drain();
        Assert.Equal(BoothState.VideoSaving, h.Controller.State);

        h.Time.Advance(BoothController.VideoStopTimeout);
        h.WaitUntil(() => h.Controller.State == BoothState.Error);

        Assert.Equal(SaveErrorText, h.Controller.ErrorMessage);
        Assert.Equal(0, h.VideoStorage.CommitCalls);
        Assert.Single(h.VideoStorage.Discarded);
        h.RunToIdle();
        Assert.Equal(BoothState.Idle, h.Controller.State);
    }

    [Fact]
    public void CommitHangs_TimesOut_SaveError_Discards_ThenIdle()
    {
        var h = RecordingHarness(v => v.MinDurationSeconds = 0);
        h.VideoStorage.CommitHang = true;

        h.Controller.TryStopVideo();
        h.Drain();
        Assert.Equal(BoothState.VideoSaving, h.Controller.State);

        h.Time.Advance(BoothController.VideoCommitTimeout);
        h.WaitUntil(() => h.Controller.State == BoothState.Error);

        Assert.Equal(SaveErrorText, h.Controller.ErrorMessage);
        Assert.Single(h.VideoStorage.Discarded);
        h.RunToIdle();
        Assert.True(h.Controller.TryStartVideo());
    }

    // ---- Cancel() (App-Ende) ----

    [Fact]
    public void Cancel_DuringRecording_SavesPartialVideo_EvenBeforeMinDuration()
    {
        var h = RecordingHarness(v => v.MinDurationSeconds = 10);
        h.AdvanceBy(Second, Second);

        h.Controller.Cancel();

        Assert.Equal(1, h.Camera.LastRecording!.StopCalls);
        Assert.Equal(1, h.VideoStorage.CommitCalls);
        h.WaitUntil(() => h.Controller.State == BoothState.Idle);
        Assert.Equal(1, h.VideoStorage.CommitCalls);   // nicht doppelt
        Assert.Empty(h.VideoStorage.Discarded);
    }

    [Fact]
    public void Cancel_DuringRecording_WithStopThatNeedsTheUiContext_StillSaves()
    {
        // Cancel() blockiert den (Pump-)Thread; ein StopAsync, dessen Fortsetzung auf dessen Kontext wartet,
        // darf das Speichern nicht verhindern.
        var h = VideoHarness();
        h.Camera.RecordingToReturn = new FakeRecording { StopYields = true };
        h.Controller.TryStartVideo();
        h.AdvanceBy(Second, Second);

        h.Controller.Cancel();

        Assert.Equal(1, h.Camera.LastRecording!.StopCalls);
        Assert.Equal(1, h.VideoStorage.CommitCalls);
        h.WaitUntil(() => h.Controller.State == BoothState.Idle);
        Assert.Empty(h.VideoStorage.Discarded);
    }

    [Fact]
    public void Cancel_DuringRecording_WithHangingStop_ReturnsAfterDeadline()
    {
        var h = RecordingHarness();
        h.Camera.LastRecording!.StopHang = true;

        var watch = Stopwatch.StartNew();
        h.Controller.Cancel();
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Cancel darf nicht hängen bleiben.");
        Assert.Equal(1, h.Camera.LastRecording.StopCalls);
        Assert.Equal(0, h.VideoStorage.CommitCalls);
    }

    [Fact]
    public void Cancel_DuringSaving_WaitsForSameSave_NoSecondCommit()
    {
        var h = RecordingHarness(v => v.MinDurationSeconds = 0);
        h.VideoStorage.CommitHang = true;
        h.Controller.TryStopVideo();
        h.Drain();
        Assert.Equal(BoothState.VideoSaving, h.Controller.State);

        var watch = Stopwatch.StartNew();
        h.Controller.Cancel();   // wartet höchstens ShutdownVideoWait

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(1, h.VideoStorage.CommitCalls);
        Assert.Equal(1, h.Camera.LastRecording!.StopCalls);
    }

    [Fact]
    public void Cancel_InVideoCountdown_CancelsSilently()
    {
        var h = VideoHarness(countdown: 3);
        h.Controller.TryStartVideo();

        h.Controller.Cancel();
        h.WaitUntil(() => h.Controller.State == BoothState.Idle);

        Assert.Equal(0, h.VideoStorage.PrepareCalls);
        Assert.Null(h.Controller.ErrorMessage);
    }

    // ---- Umschalten ----

    [Fact]
    public void ToggleVideo_StartsInIdle_StopsInRecording_IgnoredElsewhere()
    {
        var h = VideoHarness(countdown: 1);

        Assert.True(h.Controller.TryToggleVideo());               // Idle → Start
        Assert.False(h.Controller.TryToggleVideo());              // Countdown: ignoriert
        h.AdvanceUntil(() => h.Controller.State == BoothState.Recording);
        Assert.False(h.Controller.TryToggleVideo());              // Mindestlänge noch nicht erreicht
        h.AdvanceBy(TimeSpan.FromSeconds(2), Second);
        Assert.True(h.Controller.TryToggleVideo());               // Recording → Stopp
        h.Drain();
        Assert.Equal(BoothState.VideoSaved, h.Controller.State);
        Assert.False(h.Controller.TryToggleVideo());              // Erfolgsmeldung: ignoriert
        Assert.True(h.Controller.IsVideoState);
        h.RunToIdle();
        Assert.False(h.Controller.IsVideoState);
    }

    // ---- Fakes ----

    private sealed class FakeRecording : IVideoRecording
    {
        private int _stopCalls;
        private int _disposeCalls;

        public bool HasAudio { get; set; } = true;
        public event EventHandler<string>? Faulted;
        public Exception? StopFailure { get; set; }
        /// <summary>StopAsync endet nie (hängender Medienabschluss).</summary>
        public bool StopHang { get; set; }
        public int StopCalls => Volatile.Read(ref _stopCalls);
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public void RaiseFaulted(string reason) => Faulted?.Invoke(this, reason);

        /// <summary>StopAsync wartet erst (ohne ConfigureAwait(false)) auf den Kontext des Aufrufers, wie ein echter Dienst.</summary>
        public bool StopYields { get; set; }

        public Task StopAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _stopCalls);
            if (StopFailure is not null)
                return Task.FromException(StopFailure);
            if (StopYields)
                return YieldAsync();
            return StopHang ? new TaskCompletionSource().Task : Task.CompletedTask;
        }

        private static async Task YieldAsync() => await Task.Yield();

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeVideoStorage : IVideoStorage
    {
        private int _prepareCalls;
        private int _commitCalls;

        public VideoTarget Target { get; } = new("fake", "fake/tmp.mp4", VideoSettings.DefaultFilePrefix, new DateTime(2026, 1, 2, 3, 4, 5));
        public string CommitPath { get; } = "fake/Fotobox_Video.mp4";

        public int PrepareCalls => Volatile.Read(ref _prepareCalls);
        public TimeSpan LastMaxDuration { get; private set; }
        public CameraResolution? LastResolution { get; private set; }
        public DateTime LastTimestamp { get; private set; }
        public Exception? PrepareFailure { get; set; }
        /// <summary>PrepareAsync endet erst, wenn <see cref="PrepareSource"/> gesetzt wird.</summary>
        public bool PrepareHang { get; set; }
        public TaskCompletionSource<VideoTarget> PrepareSource { get; } = new();

        public int CommitCalls => Volatile.Read(ref _commitCalls);
        public Exception? CommitFailure { get; set; }
        public bool CommitHang { get; set; }

        public ConcurrentQueue<VideoTarget> Discarded { get; } = new();

        public string ResolveTargetFolder(VideoSettings video, DateTime timestamp) => "fake";

        public Task<VideoTarget> PrepareAsync(VideoSettings video, CameraResolution? resolution, TimeSpan maxDuration,
            DateTime timestamp, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _prepareCalls);
            LastMaxDuration = maxDuration;
            LastResolution = resolution;
            LastTimestamp = timestamp;
            if (PrepareFailure is not null)
                return Task.FromException<VideoTarget>(PrepareFailure);
            return PrepareHang ? PrepareSource.Task : Task.FromResult(Target);
        }

        public Task<string> CommitAsync(VideoTarget target, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _commitCalls);
            if (CommitFailure is not null)
                return Task.FromException<string>(CommitFailure);
            return CommitHang ? new TaskCompletionSource<string>().Task : Task.FromResult(CommitPath);
        }

        public void Discard(VideoTarget target) => Discarded.Enqueue(target);
    }
}
