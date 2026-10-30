using System.IO;
using System.Windows.Input;
using Fotobox.Core;
using Fotobox.ViewModels.Admin;
using Fotobox.Views.Admin;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Admin-Ansicht für den Videomodus: Tasten, Tab „Video“, Hinweis-Vorschau, Standardwerte, Kamera-Neustart.</summary>
[Collection(WpfCollection.Name)]
public sealed class AdminVideoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-video-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AdminViewModelTests.FakeStore _store;
    private readonly AdminViewModelTests.FakePinService _pin;
    private readonly AdminViewModelTests.FakeCamera _camera = new();
    private readonly AdminViewModelTests.FakePrint _print = new();
    private readonly AdminViewModelTests.FakeUi _ui = new();
    private readonly AdminViewModelTests.FakeDisplays _displays = new(AdminViewModelTests.AdminMonitors.Primary);

    public AdminVideoTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "overlays"));
        _store = new AdminViewModelTests.FakeStore(_dir);
        _pin = new AdminViewModelTests.FakePinService(_store);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AdminViewModel CreateViewModel(TimeProvider? time = null) =>
        new(_store, _pin, _camera, _print, _ui, time, _displays);

    private static KeyActionEditor Keys(AdminViewModel vm, KeyAction action) => vm.KeyEditors.Single(e => e.Action == action);

    private static KeyChord Chord(Key key, ModifierKeys modifiers = ModifierKeys.None) => new(key, modifiers);

    // ------------------------------------------------------------------ Tasten

    [Fact]
    public void VideoKeyEditors_ExistWithDefaultEnter_AreNotRequired_AndLoadFromSettings()
    {
        var vm = CreateViewModel();

        var start = Keys(vm, KeyAction.VideoStart);
        var stop = Keys(vm, KeyAction.VideoStop);
        Assert.Equal("Video starten", start.Title);
        Assert.Equal("Video beenden", stop.Title);
        Assert.False(start.IsRequired);
        Assert.False(stop.IsRequired);
        Assert.Contains("schaltet", start.Description);
        Assert.Equal(["Eingabetaste"], start.Keys.Select(k => k.DisplayName));
        Assert.Equal(["Eingabetaste"], stop.Keys.Select(k => k.DisplayName));
        Assert.Equal(["Enter"], vm.Draft.Input.VideoStartKeys);
    }

    [Fact]
    public void VideoKeys_SameKeyForStartAndStop_IsAllowed_AndSaved()
    {
        var vm = CreateViewModel();
        vm.BeginKeyCapture(KeyAction.VideoStart);
        Assert.True(vm.CaptureKey(Chord(Key.F8)));

        vm.BeginKeyCapture(KeyAction.VideoStop);
        Assert.True(vm.CaptureKey(Chord(Key.F8)));   // dieselbe Taste wie bei „Video starten“: Umschalten

        Assert.False(vm.HasKeyMessage);
        Assert.Equal(["Enter", "F8"], vm.Draft.Input.VideoStartKeys);
        Assert.Equal(["Enter", "F8"], vm.Draft.Input.VideoStopKeys);

        vm.SaveCommand.Execute(null);
        Assert.True(vm.SettingsSaved);
        Assert.Equal(["Enter", "F8"], _store.Current.Input.VideoStartKeys);
        Assert.Equal(["Enter", "F8"], _store.Current.Input.VideoStopKeys);
    }

    [Fact]
    public void VideoKeys_SameKeyTwiceInOneAction_IsRejected()
    {
        var vm = CreateViewModel();
        vm.BeginKeyCapture(KeyAction.VideoStart);

        Assert.False(vm.CaptureKey(Chord(Key.Enter)));

        Assert.Contains("bereits bei „Video starten“", vm.KeyMessage);
        Assert.True(vm.IsKeyCaptureActive);
    }

    [Theory]
    [InlineData(KeyAction.VideoStart, Key.Space, "Foto auslösen")]
    [InlineData(KeyAction.VideoStop, Key.Space, "Foto auslösen")]
    [InlineData(KeyAction.VideoStart, Key.Escape, "Abbrechen")]
    [InlineData(KeyAction.VideoStop, Key.Escape, "Abbrechen")]
    public void VideoKeys_KeyOfOtherAction_IsRejected(KeyAction target, Key key, string ownerTitle)
    {
        var vm = CreateViewModel();
        vm.BeginKeyCapture(target);

        Assert.False(vm.CaptureKey(Chord(key)));

        Assert.Contains($"‚{ownerTitle}‘", vm.KeyMessage);
    }

    [Fact]
    public void VideoKeys_AdminChord_IsRejected()
    {
        var vm = CreateViewModel();
        vm.BeginKeyCapture(KeyAction.VideoStart);

        Assert.False(vm.CaptureKey(Chord(Key.A, ModifierKeys.Control | ModifierKeys.Alt)));

        Assert.Contains("Admin-Ansicht öffnen", vm.KeyMessage);
    }

    [Theory]
    [InlineData(KeyAction.Trigger)]
    [InlineData(KeyAction.Cancel)]
    public void PhotoKeys_CannotTakeVideoKey(KeyAction target)
    {
        var vm = CreateViewModel();
        vm.BeginKeyCapture(target);

        Assert.False(vm.CaptureKey(Chord(Key.Enter)));   // Standard der Video-Tasten

        Assert.Contains("Video starten", vm.KeyMessage);
    }

    [Fact]
    public void VideoKeys_CanBeRemoved_AndStartKeyHintAppearsOnlyWithActiveVideoMode()
    {
        var vm = CreateViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.False(vm.HasVideoStartKeyHint);

        var start = Keys(vm, KeyAction.VideoStart);
        Assert.True(start.Keys[0].RemoveCommand.CanExecute(null));
        start.Keys[0].RemoveCommand.Execute(null);

        Assert.Empty(vm.Draft.Input.VideoStartKeys);
        Assert.True(vm.HasVideoStartKeyHint);
        Assert.Equal("Ohne Taste für „Video starten“ können Gäste kein Video aufnehmen.", vm.VideoStartKeyHint);
        Assert.Contains(nameof(AdminViewModel.HasVideoStartKeyHint), raised);

        vm.VideoEnabled = false;
        Assert.False(vm.HasVideoStartKeyHint);
        vm.VideoEnabled = true;
        Assert.True(vm.HasVideoStartKeyHint);

        // Kein Blocker: Speichern funktioniert.
        vm.SaveCommand.Execute(null);
        Assert.True(vm.SettingsSaved);
        Assert.Empty(_store.Current.Input.VideoStartKeys);
    }

    [Fact]
    public void ResetKeys_RestoresVideoKeysToEnter_AndKeepsMouseOptionsWithTrigger()
    {
        var vm = CreateViewModel();
        vm.BeginKeyCapture(KeyAction.VideoStart);
        vm.CaptureKey(Chord(Key.F8));
        Keys(vm, KeyAction.VideoStop).Keys[0].RemoveCommand.Execute(null);
        vm.TriggerOnLeftClick = false;

        vm.ResetKeysCommand.Execute(null);

        Assert.Equal(["Enter"], vm.Draft.Input.VideoStartKeys);
        Assert.Equal(["Enter"], vm.Draft.Input.VideoStopKeys);
        Assert.Equal(["Eingabetaste"], Keys(vm, KeyAction.VideoStop).Keys.Select(k => k.DisplayName));
        Assert.True(vm.TriggerOnLeftClick);
    }

    // ------------------------------------------------------------------ Felder laden, ändern, speichern

    [Fact]
    public void VideoFields_LoadFromSettings()
    {
        _store.Current.Video.Enabled = false;
        _store.Current.Video.MaxDurationSeconds = 120;
        _store.Current.Video.CountdownSeconds = 0;
        _store.Current.Video.MinDurationSeconds = 5;
        _store.Current.Video.WarningSeconds = 20;
        _store.Current.Video.ShowRecDot = false;
        _store.Current.Video.SuccessText = "Top!";
        _store.Current.Video.Quality = VideoQuality.Low;
        _store.Current.Video.RecordAudio = false;
        _store.Current.Video.FilePrefix = "Clip";

        var vm = CreateViewModel();

        Assert.False(vm.VideoEnabled);
        Assert.Equal(120, vm.VideoMaxDurationSeconds);
        Assert.Equal("Das sind 2:00 Minuten.", vm.VideoMaxDurationText);
        Assert.Equal(0, vm.VideoCountdownSeconds);
        Assert.Equal(5, vm.VideoMinDurationSeconds);
        Assert.Equal(20, vm.VideoWarningSeconds);
        Assert.False(vm.VideoShowRecDot);
        Assert.Equal("Top!", vm.VideoSuccessText);
        Assert.Equal(VideoQuality.Low, vm.SelectedVideoQuality.Quality);
        Assert.False(vm.VideoRecordAudio);
        Assert.Equal("Clip", vm.VideoFilePrefix);
        Assert.Contains("Clip_20261028_153000.mp4", vm.VideoFileNameExample);
    }

    [Fact]
    public void VideoFields_Defaults_ShowEffectiveValues()
    {
        var vm = CreateViewModel();

        Assert.True(vm.VideoEnabled);
        Assert.Equal("Das sind 5:00 Minuten.", vm.VideoMaxDurationText);
        Assert.Equal(string.Empty, vm.VideoSuccessText);
        Assert.Equal(VideoSettings.DefaultSuccessText, vm.VideoSuccessTextPlaceholder);
        Assert.Equal(VideoQuality.High, vm.SelectedVideoQuality.Quality);
        Assert.Equal(3, vm.VideoQualities.Count);
        Assert.True(vm.IsDefaultVideoFolder);
        Assert.Equal(AppInfo.DefaultVideoFolder, vm.VideoFolderDisplay);
        Assert.Contains($"{VideoSettings.DefaultFilePrefix}_20261028_153000.mp4", vm.VideoFileNameExample);
    }

    [Fact]
    public void VideoMaxDurationText_ShowsSecondsBelowOneMinute()
    {
        var vm = CreateViewModel();

        vm.VideoMaxDurationSeconds = 45;
        Assert.Equal("Das sind 45 Sekunden.", vm.VideoMaxDurationText);

        vm.VideoMaxDurationSeconds = 605;
        Assert.Equal("Das sind 10:05 Minuten.", vm.VideoMaxDurationText);
    }

    [Fact]
    public void VideoFields_EditsGoToDraftFirst_AndAreSavedWithSave()
    {
        var vm = CreateViewModel();

        vm.VideoEnabled = true;
        vm.VideoMaxDurationSeconds = 90;
        vm.VideoCountdownSeconds = 5;
        vm.VideoMinDurationSeconds = 3;
        vm.VideoShowRecDot = false;
        vm.VideoShowElapsed = false;
        vm.VideoShowRemaining = false;
        vm.VideoWarningSeconds = 0;
        vm.VideoSuccessText = "Danke!";
        vm.VideoSuccessDisplaySeconds = 7;
        vm.SelectedVideoQuality = vm.VideoQualities.Single(q => q.Quality == VideoQuality.Medium);
        vm.VideoRecordAudio = false;
        vm.VideoDateSubfolder = false;
        vm.VideoFilePrefix = "Hochzeit_Video";

        Assert.Equal(300, _store.Current.Video.MaxDurationSeconds);   // Store unberührt
        Assert.Equal(0, _store.SaveCount);

        vm.SaveCommand.Execute(null);

        Assert.True(vm.SettingsSaved);
        var saved = _store.Current.Video;
        Assert.Equal(90, saved.MaxDurationSeconds);
        Assert.Equal(5, saved.CountdownSeconds);
        Assert.Equal(3, saved.MinDurationSeconds);
        Assert.False(saved.ShowRecDot);
        Assert.False(saved.ShowElapsed);
        Assert.False(saved.ShowRemaining);
        Assert.Equal(0, saved.WarningSeconds);
        Assert.Equal("Danke!", saved.SuccessText);
        Assert.Equal(7, saved.SuccessDisplaySeconds);
        Assert.Equal(VideoQuality.Medium, saved.Quality);
        Assert.False(saved.RecordAudio);
        Assert.False(saved.DateSubfolder);
        Assert.Equal("Hochzeit_Video", saved.FilePrefix);
    }

    [Fact]
    public void VideoSuccessText_BlankMeansDefault_AndTooLongIsRejected()
    {
        var vm = CreateViewModel();
        vm.VideoSuccessText = "Hallo";
        Assert.Equal("Hallo", vm.Draft.Video.SuccessText);

        vm.VideoSuccessText = "   ";
        Assert.Null(vm.Draft.Video.SuccessText);

        vm.Draft.Video.SuccessText = new string('x', SettingsLimits.VideoSuccessTextMaxLength + 1);
        Assert.Contains(vm.Validate(), e => e.Contains("Text nach dem Video") && e.Contains(SettingsLimits.VideoSuccessTextMaxLength.ToString()));
    }

    // ------------------------------------------------------------------ Validierung

    [Fact]
    public void Validate_MinDurationNotBelowMaxDuration_ReportsClearMessage_AndBlocksSave()
    {
        var vm = CreateViewModel();
        vm.VideoMaxDurationSeconds = 20;
        vm.VideoMinDurationSeconds = 20;

        var errors = vm.Validate();

        Assert.Contains("Die Mindestlänge des Videos (20 s) muss kürzer sein als die Höchstdauer (20 s).", errors);
        vm.SaveCommand.Execute(null);
        Assert.False(vm.SettingsSaved);
        Assert.Equal(0, _store.SaveCount);
        Assert.Contains("Mindestlänge", vm.ValidationMessage);
    }

    [Fact]
    public void Validate_MinDurationBelowMaxDuration_IsFine()
    {
        var vm = CreateViewModel();
        vm.VideoMaxDurationSeconds = SettingsLimits.VideoMaxDurationMin;
        vm.VideoMinDurationSeconds = SettingsLimits.VideoMaxDurationMin - 1;

        Assert.Empty(vm.Validate());
    }

    [Fact]
    public void Validate_OutOfRangeValues_AreReportedWithLimits()
    {
        var vm = CreateViewModel();
        vm.Draft.Video.MaxDurationSeconds = SettingsLimits.VideoMaxDurationMax + 1;
        vm.Draft.Video.CountdownSeconds = SettingsLimits.VideoCountdownMax + 1;
        vm.Draft.Video.WarningSeconds = SettingsLimits.VideoWarningMax + 1;
        vm.Draft.Video.SuccessDisplaySeconds = 0;

        var errors = vm.Validate();

        Assert.Contains(errors, e => e.StartsWith("Die Höchstdauer des Videos muss zwischen 10 und 1800"));
        Assert.Contains(errors, e => e.StartsWith("Der Countdown vor dem Video muss zwischen 0 und 30"));
        Assert.Contains(errors, e => e.StartsWith("Die Warnung vor dem Ende des Videos muss zwischen 0 und 120"));
        Assert.Contains(errors, e => e.StartsWith("Die Anzeigedauer der Meldung nach dem Video muss zwischen 1 und 60"));
    }

    [Fact]
    public void Validate_VideoPrefix_EmptyOrInvalidOrTooLong()
    {
        var vm = CreateViewModel();

        vm.VideoFilePrefix = " ";
        Assert.Contains(vm.Validate(), e => e.Contains("Dateipräfix der Videos darf nicht leer"));

        vm.VideoFilePrefix = "a/b";
        Assert.Contains(vm.Validate(), e => e.Contains("Dateipräfix der Videos darf höchstens"));

        vm.VideoFilePrefix = new string('a', SettingsLimits.FilePrefixMaxLength + 1);
        Assert.Contains(vm.Validate(), e => e.Contains("Dateipräfix der Videos darf höchstens"));

        vm.VideoFilePrefix = new string('a', SettingsLimits.FilePrefixMaxLength);
        Assert.Empty(vm.Validate());
    }

    [Fact]
    public void Validate_VideoModeOff_SkipsVideoChecks()
    {
        var vm = CreateViewModel();
        vm.VideoMaxDurationSeconds = 10;
        vm.VideoMinDurationSeconds = 10;
        Assert.NotEmpty(vm.Validate());

        vm.VideoEnabled = false;

        Assert.Empty(vm.Validate());
    }

    // ------------------------------------------------------------------ Speicherordner

    [Fact]
    public void ChooseVideoFolder_UsesVideoDialogTitle_AndStartsInEffectiveFolder()
    {
        var vm = CreateViewModel();
        var chosen = Path.Combine(_dir, "Clips");
        _ui.FolderToPick = chosen;

        vm.ChooseVideoFolderCommand.Execute(null);

        Assert.Equal("Speicherordner für die Videos auswählen", _ui.LastPickTitle);
        Assert.Equal(AppInfo.DefaultVideoFolder, _ui.LastPickInitial);
        Assert.Equal(chosen, vm.Draft.Video.Folder);
        Assert.Equal(chosen, vm.VideoFolderDisplay);
        Assert.False(vm.IsDefaultVideoFolder);
        Assert.Null(vm.Draft.Storage.PhotoFolder);   // der Fotoordner bleibt unberührt
    }

    [Fact]
    public void ChoosePhotoFolder_StillUsesPhotoDialogTitle()
    {
        var vm = CreateViewModel();
        _ui.FolderToPick = Path.Combine(_dir, "Bilder");

        vm.ChooseFolderCommand.Execute(null);

        Assert.Equal("Speicherordner für die Fotos auswählen", _ui.LastPickTitle);
        Assert.Null(vm.Draft.Video.Folder);
    }

    [Fact]
    public void ChooseVideoFolder_Cancelled_KeepsFolder()
    {
        var vm = CreateViewModel();
        _ui.FolderToPick = null;

        vm.ChooseVideoFolderCommand.Execute(null);

        Assert.Null(vm.Draft.Video.Folder);
    }

    [Fact]
    public void ResetVideoFolder_RestoresDefault()
    {
        var vm = CreateViewModel();
        vm.Draft.Video.Folder = Path.Combine(_dir, "Clips");

        vm.ResetVideoFolderCommand.Execute(null);

        Assert.Null(vm.Draft.Video.Folder);
        Assert.True(vm.IsDefaultVideoFolder);
        Assert.Equal(AppInfo.DefaultVideoFolder, vm.VideoFolderDisplay);
    }

    [Fact]
    public async Task OpenVideoFolder_Existing_OpensIt()
    {
        var vm = CreateViewModel();
        vm.Draft.Video.Folder = _dir;

        await vm.OpenVideoFolderCommand.ExecuteAsync(null);

        Assert.Equal(_dir, _ui.OpenedFolder);
    }

    [Fact]
    public async Task OpenVideoFolder_Missing_ShowsHintForFirstVideo()
    {
        var vm = CreateViewModel();
        var folder = Path.Combine(_dir, "NochNichtDa");
        vm.Draft.Video.Folder = folder;

        await vm.OpenVideoFolderCommand.ExecuteAsync(null);

        Assert.Null(_ui.OpenedFolder);
        Assert.False(Directory.Exists(folder));
        Assert.Contains("ersten Video", vm.ValidationMessage);
    }

    [Fact]
    public async Task Save_ChangedVideoFolder_IsWriteTested_UnchangedIsNot()
    {
        var checkedFolders = new List<string>();
        var vm = CreateViewModel();
        vm.FolderWriteCheck = f => { checkedFolders.Add(f); return null; };
        vm.Draft.Video.Folder = Path.Combine(_dir, "Neu");

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.SettingsSaved);
        Assert.Equal([Path.Combine(_dir, "Neu")], checkedFolders);
        Assert.Equal(Path.Combine(_dir, "Neu"), _store.Current.Video.Folder);

        // Zweites Öffnen mit gespeichertem Ordner: unverändert → kein Schreibtest.
        checkedFolders.Clear();
        var again = CreateViewModel();
        again.FolderWriteCheck = f => { checkedFolders.Add(f); return null; };
        await again.SaveCommand.ExecuteAsync(null);
        Assert.True(again.SettingsSaved);
        Assert.Empty(checkedFolders);
    }

    [Fact]
    public async Task Save_VideoFolderNotWritable_ShowsMessage_AndDoesNotSave()
    {
        var vm = CreateViewModel();
        vm.FolderWriteCheck = _ => "Zugriff verweigert";
        vm.Draft.Video.Folder = Path.Combine(_dir, "Gesperrt");

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.SettingsSaved);
        Assert.Equal(0, _store.SaveCount);
        Assert.Contains("In den Videoordner kann nicht geschrieben werden (Zugriff verweigert)", vm.ValidationMessage);
    }

    [Fact]
    public async Task Save_VideoFolderCheckHangs_TimesOutWithGermanMessage()
    {
        var vm = CreateViewModel();
        using var release = new ManualResetEventSlim();
        vm.FolderProbeTimeout = TimeSpan.FromMilliseconds(100);
        vm.FolderWriteCheck = _ => { release.Wait(TimeSpan.FromSeconds(30)); return null; };
        vm.Draft.Video.Folder = Path.Combine(_dir, "Netz");

        try
        {
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.False(vm.SettingsSaved);
            Assert.Contains("Der Videoordner ist nicht erreichbar (Zeitüberschreitung)", vm.ValidationMessage);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task Save_BothFoldersChanged_BothAreTested_AndBothErrorsReported()
    {
        var vm = CreateViewModel();
        vm.FolderWriteCheck = f => "kaputt:" + Path.GetFileName(f);
        vm.Draft.Storage.PhotoFolder = Path.Combine(_dir, "Fotos");
        vm.Draft.Video.Folder = Path.Combine(_dir, "Clips");

        var errors = await vm.ValidateAsync();

        Assert.Contains(errors, e => e.Contains("In den Speicherordner kann nicht geschrieben werden (kaputt:Fotos)"));
        Assert.Contains(errors, e => e.Contains("In den Videoordner kann nicht geschrieben werden (kaputt:Clips)"));
    }

    [Fact]
    public async Task Save_VideoModeOff_DoesNotProbeVideoFolder()
    {
        var checkedFolders = new List<string>();
        var vm = CreateViewModel();
        vm.FolderWriteCheck = f => { checkedFolders.Add(f); return null; };
        vm.Draft.Video.Folder = Path.Combine(_dir, "Neu");
        vm.VideoEnabled = false;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.SettingsSaved);
        Assert.Empty(checkedFolders);
    }

    [Fact]
    public void Validate_VideoFolderNotAbsolute_IsRejected()
    {
        var vm = CreateViewModel();
        vm.Draft.Video.Folder = "relativ\\pfad";

        Assert.Contains(vm.Validate(), e => e.Contains("Der Videoordner ist kein gültiger Pfad"));
    }

    // ------------------------------------------------------------------ Mikrofon

    [Fact]
    public async Task Microphones_ListStartsWithDefault_AndShowsDevices()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        Assert.Equal(["Windows-Standardmikrofon", "Testmikrofon A", "Testmikrofon B"], vm.Microphones.Select(m => m.Display));
        Assert.Null(vm.Microphones[0].Id);
        Assert.Same(vm.Microphones[0], vm.SelectedMicrophone);
        Assert.False(vm.HasMicrophoneHint);
        Assert.Null(vm.Draft.Video.MicrophoneId);
    }

    [Fact]
    public async Task Microphones_SelectionWritesIdAndName_AndDefaultClearsBoth()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        vm.SelectedMicrophone = vm.Microphones[2];
        Assert.Equal("mic-b", vm.Draft.Video.MicrophoneId);
        Assert.Equal("Testmikrofon B", vm.Draft.Video.MicrophoneName);
        Assert.Null(_store.Current.Video.MicrophoneId);   // erst mit „Speichern“

        vm.SaveCommand.Execute(null);
        Assert.Equal("mic-b", _store.Current.Video.MicrophoneId);
        Assert.Equal("Testmikrofon B", _store.Current.Video.MicrophoneName);

        var again = CreateViewModel();
        await again.InitializeAsync();
        Assert.Equal("mic-b", again.SelectedMicrophone!.Id);

        again.SelectedMicrophone = again.Microphones[0];
        Assert.Null(again.Draft.Video.MicrophoneId);
        Assert.Null(again.Draft.Video.MicrophoneName);
    }

    [Fact]
    public async Task Microphones_SavedButMissing_ShowsDisconnectedEntryWithHint_AndKeepsDraft()
    {
        _store.Current.Video.MicrophoneId = "mic-weg";
        _store.Current.Video.MicrophoneName = "Altes Mikrofon";
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        Assert.Equal("Altes Mikrofon (nicht angeschlossen)", vm.SelectedMicrophone!.Display);
        Assert.Equal("mic-weg", vm.SelectedMicrophone.Id);
        Assert.Equal(4, vm.Microphones.Count);
        Assert.True(vm.HasMicrophoneHint);
        Assert.Contains("Altes Mikrofon", vm.MicrophoneHint);
        Assert.Contains("nicht angeschlossen", vm.MicrophoneHint);
        Assert.Equal("mic-weg", vm.Draft.Video.MicrophoneId);   // Laden verändert den Entwurf nicht
        Assert.Equal("Altes Mikrofon", vm.Draft.Video.MicrophoneName);

        // Weg und zurück: die gespeicherte Wahl samt Name kommt wieder.
        var missing = vm.SelectedMicrophone;
        vm.SelectedMicrophone = vm.Microphones[1];
        Assert.Equal("mic-a", vm.Draft.Video.MicrophoneId);
        Assert.False(vm.HasMicrophoneHint);
        vm.SelectedMicrophone = missing;
        Assert.Equal("mic-weg", vm.Draft.Video.MicrophoneId);
        Assert.Equal("Altes Mikrofon", vm.Draft.Video.MicrophoneName);
        Assert.True(vm.HasMicrophoneHint);
    }

    [Fact]
    public async Task Microphones_SavedMissingWithoutName_UsesGenericName()
    {
        _store.Current.Video.MicrophoneId = "mic-weg";
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        Assert.Equal("Gewähltes Mikrofon (nicht angeschlossen)", vm.SelectedMicrophone!.Display);
    }

    [Fact]
    public async Task Microphones_SavedAndConnected_IsSelected_WithoutHint()
    {
        _store.Current.Video.MicrophoneId = "mic-a";
        _store.Current.Video.MicrophoneName = "Testmikrofon A";
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        Assert.Equal("mic-a", vm.SelectedMicrophone!.Id);
        Assert.Equal(3, vm.Microphones.Count);
        Assert.False(vm.HasMicrophoneHint);
    }

    [Fact]
    public void Microphones_BeforeListIsLoaded_ShowSavedMicrophoneWithoutDisconnectedClaim()
    {
        _store.Current.Video.MicrophoneId = "mic-a";
        _store.Current.Video.MicrophoneName = "Testmikrofon A";

        var vm = CreateViewModel();

        Assert.Equal("Testmikrofon A", vm.SelectedMicrophone!.Display);
        Assert.False(vm.HasMicrophoneHint);
    }

    [Fact]
    public async Task Microphones_NoDevices_OnlyDefault_WithHint_AndNoCrashOnError()
    {
        _camera.Microphones = [];
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        Assert.Single(vm.Microphones);
        Assert.Contains("kein Mikrofon", vm.MicrophoneHint);

        _camera.MicrophonesError = new InvalidOperationException("Dienst weg");
        var failing = CreateViewModel();
        await failing.InitializeAsync();

        Assert.Single(failing.Microphones);
        Assert.True(failing.HasMicrophoneHint);
    }

    [Fact]
    public async Task Microphones_HangingQuery_TimesOutAfterFiveSeconds_WithoutBlockingTheRest()
    {
        var time = new FakeTimeProvider();
        _camera.MicrophonesGate = new TaskCompletionSource().Task;   // antwortet nie
        var vm = CreateViewModel(time);

        var init = vm.InitializeAsync();
        while (!init.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(10);
        }
        await init.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(vm.Microphones);
        Assert.Equal(2, vm.Devices.Count);   // Kameraliste ist trotzdem da
    }

    // ------------------------------------------------------------------ Kamera-Neustart (Ton)

    private static VideoSettings Video(bool enabled = true, bool audio = true, string? mic = null, int max = 300) =>
        new() { Enabled = enabled, RecordAudio = audio, MicrophoneId = mic, MaxDurationSeconds = max };

    [Fact]
    public void VideoNeedsCameraRestart_MicrophoneChange_IsTrue()
    {
        Assert.True(Video(mic: null).RequiresCameraRestartComparedTo(Video(mic: "mic-a")));
        Assert.True(Video(mic: "mic-a").RequiresCameraRestartComparedTo(Video(mic: "mic-b")));
    }

    [Fact]
    public void VideoNeedsCameraRestart_AudioOnOff_IsTrue()
    {
        Assert.True(Video(audio: true).RequiresCameraRestartComparedTo(Video(audio: false)));
        Assert.True(Video(audio: false).RequiresCameraRestartComparedTo(Video(audio: true)));
        Assert.True(Video(enabled: true).RequiresCameraRestartComparedTo(Video(enabled: false)));
    }

    [Fact]
    public void VideoNeedsCameraRestart_DurationOrDisplayChange_IsFalse()
    {
        var before = Video();
        var after = Video(max: 60);
        after.ShowRecDot = false;
        after.Folder = "X";

        Assert.False(before.RequiresCameraRestartComparedTo(after));
    }

    [Fact]
    public void VideoNeedsCameraRestart_MicrophoneChangeWithAudioOff_IsFalse()
    {
        Assert.False(Video(audio: false, mic: "mic-a").RequiresCameraRestartComparedTo(Video(audio: false, mic: "mic-b")));
    }

    [Fact]
    public async Task SavedSettings_FromViewModel_DriveTheRestartDecision()
    {
        var before = _store.Current.Clone().Video;
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        vm.VideoMaxDurationSeconds = 120;
        vm.SaveCommand.Execute(null);
        Assert.False(before.RequiresCameraRestartComparedTo(_store.Current.Video));

        var second = CreateViewModel();
        await second.InitializeAsync();
        second.SelectedMicrophone = second.Microphones[1];
        second.SaveCommand.Execute(null);
        Assert.True(before.RequiresCameraRestartComparedTo(_store.Current.Video));
    }

    // ------------------------------------------------------------------ Video-Hinweis (Gastansicht)

    [Fact]
    public void VideoHint_Preview_DefaultIsAutomaticText_AndFollowsKeys()
    {
        var vm = CreateViewModel();
        Assert.Equal("Drücke die Eingabetaste für ein Video 🎬", vm.VideoHintPreviewText);
        Assert.True(vm.ShowVideoHintPreview);
        Assert.Equal(HintStyle.Pill, vm.VideoHintStyle);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.BeginKeyCapture(KeyAction.VideoStart);
        vm.CaptureKey(Chord(Key.F8));
        Assert.Equal("Drücke Eingabetaste oder F8 für ein Video 🎬", vm.VideoHintPreviewText);
        Assert.Contains(nameof(AdminViewModel.VideoHintPreviewText), raised);

        Keys(vm, KeyAction.VideoStart).Keys.ToList().ForEach(k => k.RemoveCommand.Execute(null));
        Assert.Equal(string.Empty, vm.VideoHintPreviewText);
        Assert.False(vm.ShowVideoHintPreview);   // ohne Starttaste entfällt die Zeile
    }

    [Fact]
    public void VideoHint_CustomText_UsesVideoAndPhotoPlaceholders()
    {
        var vm = CreateViewModel();
        vm.VideoHintText = "Video: {Videotasten} · Foto: {Fototaste_1}";

        Assert.Equal("Video: Eingabetaste · Foto: Leertaste", vm.VideoHintPreviewText);
        Assert.False(vm.HasVideoHintProblems);
        Assert.Equal("Video: {Videotasten} · Foto: {Fototaste_1}", vm.Draft.GuestScreen.VideoHintText);

        vm.VideoHintText = "";
        Assert.Null(vm.Draft.GuestScreen.VideoHintText);
    }

    [Fact]
    public void VideoHint_Problems_ReportUnassignedAndUnknownPlaceholders_AndUpdateWithKeys()
    {
        var vm = CreateViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.VideoHintText = "{Videotaste_2} {Fototaste_3} {Foo}";

        Assert.True(vm.HasVideoHintProblems);
        Assert.Equal(3, vm.VideoHintProblems.Count);
        Assert.Contains(vm.VideoHintProblems, p => p.StartsWith("{Videotaste_2} ist nicht belegt"));
        Assert.Contains(vm.VideoHintProblems, p => p.StartsWith("{Fototaste_3} ist nicht belegt"));
        Assert.Contains(vm.VideoHintProblems, p => p.Contains("{Foo}"));
        Assert.Equal(string.Join(Environment.NewLine, vm.VideoHintProblems), vm.VideoHintProblemsText);

        raised.Clear();
        vm.BeginKeyCapture(KeyAction.VideoStart);
        vm.CaptureKey(Chord(Key.F8));
        Assert.Contains(nameof(AdminViewModel.HasVideoHintProblems), raised);
        Assert.Equal(2, vm.VideoHintProblems.Count);
    }

    [Fact]
    public void VideoHint_Style_WritesDraft_AndIsSaved()
    {
        var vm = CreateViewModel();
        Assert.Equal(HintStyle.Pill, vm.VideoHintStyle);

        vm.VideoHintStyle = HintStyle.Banner;
        Assert.Equal(HintStyle.Banner, vm.VideoHintStyle);
        Assert.Equal(HintStyle.Pill, vm.HintStyle);   // der Foto-Hinweis bleibt unberührt

        vm.VideoHintStyle = HintStyle.Plain;
        Assert.Equal(HintStyle.Plain, vm.VideoHintStyle);

        vm.VideoHintText = "Los!";
        vm.SaveCommand.Execute(null);
        Assert.Equal(HintStyle.Plain, _store.Current.GuestScreen.VideoHintStyle);
        Assert.Equal("Los!", _store.Current.GuestScreen.VideoHintText);
    }

    [Fact]
    public void VideoHint_VideoModeOff_HidesPreviewLine()
    {
        var vm = CreateViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.VideoEnabled = false;

        Assert.False(vm.ShowVideoHintPreview);
        Assert.Contains(nameof(AdminViewModel.ShowVideoHintPreview), raised);
        vm.VideoEnabled = true;
        Assert.True(vm.ShowVideoHintPreview);
    }

    [Fact]
    public void VideoHint_TooLongText_IsRejected()
    {
        var vm = CreateViewModel();
        vm.Draft.GuestScreen.VideoHintText = new string('x', SettingsLimits.HintTextMaxLength + 1);

        Assert.Contains(vm.Validate(), e => e.Contains("Video-Hinweistext"));
    }

    [Fact]
    public void PhotoHint_KnowsVideoKeys_ForPlaceholdersAndProblems()
    {
        var vm = CreateViewModel();
        vm.HintText = "Foto {Fototaste_1}, Video {Videotaste_1}";

        Assert.Equal("Foto Leertaste, Video Eingabetaste", vm.HintPreviewText);
        Assert.False(vm.HasHintProblems);

        Keys(vm, KeyAction.VideoStart).Keys[0].RemoveCommand.Execute(null);

        Assert.True(vm.HasHintProblems);
        Assert.Contains(vm.HintProblems, p => p.StartsWith("{Videotaste_1} ist nicht belegt"));
        Assert.Equal("Foto Leertaste, Video", vm.HintPreviewText);
    }

    [Fact]
    public void PhotoHint_Preview_IgnoresVideoKeys_WhenVideoIsOff_AndFollowsToggle()
    {
        var vm = CreateViewModel();
        vm.HintText = "Foto {Fototaste_1}, Video {Videotaste_1}";
        Assert.Equal("Foto Leertaste, Video Eingabetaste", vm.HintPreviewText);
        Assert.False(vm.HasHintProblems);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.VideoEnabled = false;

        Assert.Equal("Foto Leertaste, Video", vm.HintPreviewText);
        Assert.True(vm.HasHintProblems);
        Assert.Contains(vm.HintProblems, p => p.StartsWith("{Videotaste_1} ist nicht belegt"));
        Assert.Contains(nameof(AdminViewModel.HintPreviewText), raised);
        Assert.Contains(nameof(AdminViewModel.HintProblemsText), raised);

        vm.VideoEnabled = true;
        Assert.Equal("Foto Leertaste, Video Eingabetaste", vm.HintPreviewText);
        Assert.False(vm.HasHintProblems);
    }

    [Fact]
    public void VideoHint_Preview_ClickPlaceholder_FollowsMouseCheckboxes()
    {
        var vm = CreateViewModel();
        vm.VideoHintText = "Video {Videotasten} {Klick}";
        Assert.Equal("Video Eingabetaste oder klicke", vm.VideoHintPreviewText);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.TriggerOnLeftClick = false;
        Assert.Equal("Video Eingabetaste oder klicke", vm.VideoHintPreviewText);   // rechts löst noch aus
        Assert.Contains(nameof(AdminViewModel.VideoHintPreviewText), raised);

        vm.TriggerOnRightClick = false;
        Assert.Equal("Video Eingabetaste", vm.VideoHintPreviewText);

        vm.TriggerOnLeftClick = true;
        Assert.Equal("Video Eingabetaste oder klicke", vm.VideoHintPreviewText);
    }

    // ------------------------------------------------------------------ Stopp-Hinweis

    [Fact]
    public void StopHint_Defaults_ShowSwitchOn_EmptyField_AndStandardTextInPreview()
    {
        var vm = CreateViewModel();

        Assert.True(vm.VideoShowStopHint);
        Assert.Equal(string.Empty, vm.VideoStopHintText);
        Assert.Equal(VideoSettings.DefaultStopHintText, vm.VideoStopHintTextPlaceholder);
        Assert.Equal("Drücke die Eingabetaste, um die Videoaufnahme zu beenden", vm.VideoStopHintPreviewText);
        Assert.True(vm.ShowVideoStopHintPreview);
        Assert.False(vm.HasVideoStopHintProblems);
    }

    [Fact]
    public void StopHint_EmptyText_IsStoredAsNull_AndWhitespaceToo()
    {
        var vm = CreateViewModel();

        vm.VideoStopHintText = "Stopp: {Taste}";
        Assert.Equal("Stopp: {Taste}", vm.Draft.Video.StopHintText);
        Assert.Equal("Stopp: die Eingabetaste", vm.VideoStopHintPreviewText);

        vm.VideoStopHintText = "";
        Assert.Null(vm.Draft.Video.StopHintText);
        vm.VideoStopHintText = "   ";
        Assert.Null(vm.Draft.Video.StopHintText);
        Assert.Equal("Drücke die Eingabetaste, um die Videoaufnahme zu beenden", vm.VideoStopHintPreviewText);
    }

    [Fact]
    public void StopHint_Preview_FollowsStopKeys_Switch_AndVideoMode_AndNotifies()
    {
        var vm = CreateViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Keys(vm, KeyAction.VideoStop).Keys[0].RemoveCommand.Execute(null);
        Assert.Equal(string.Empty, vm.VideoStopHintPreviewText);   // ohne Stopptaste entfällt der Hinweis
        Assert.False(vm.ShowVideoStopHintPreview);
        Assert.Contains(nameof(AdminViewModel.VideoStopHintPreviewText), raised);
        Assert.Contains(nameof(AdminViewModel.ShowVideoStopHintPreview), raised);

        vm.BeginKeyCapture(KeyAction.VideoStop);
        vm.CaptureKey(Chord(Key.F7));
        Assert.Equal("Drücke F7, um die Videoaufnahme zu beenden", vm.VideoStopHintPreviewText);
        Assert.True(vm.ShowVideoStopHintPreview);

        vm.VideoShowStopHint = false;
        Assert.False(vm.ShowVideoStopHintPreview);
        vm.VideoShowStopHint = true;
        Assert.True(vm.ShowVideoStopHintPreview);

        vm.VideoEnabled = false;
        Assert.False(vm.ShowVideoStopHintPreview);
    }

    [Fact]
    public void StopHint_Problems_ReportClick_PhotoVideoKeys_Unknown_AndTooLargeNumber()
    {
        var vm = CreateViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.VideoStopHintText = "{Klick} {Videotaste_1} {Foo} {Stopptaste_2}";

        Assert.True(vm.HasVideoStopHintProblems);
        Assert.Equal(4, vm.VideoStopHintProblems.Count);
        Assert.Contains(vm.VideoStopHintProblems, p => p.StartsWith("{Klick} gibt es in dieser Zeile nicht"));
        Assert.Contains(vm.VideoStopHintProblems, p => p.StartsWith("{Videotaste_1} gibt es in dieser Zeile nicht"));
        Assert.Contains(vm.VideoStopHintProblems, p => p.StartsWith("Unbekannter Platzhalter {Foo}"));
        Assert.Contains(vm.VideoStopHintProblems, p => p.StartsWith("{Stopptaste_2} ist nicht belegt"));
        Assert.Contains(nameof(AdminViewModel.VideoStopHintProblemsText), raised);
        Assert.Contains(nameof(AdminViewModel.HasVideoStopHintProblems), raised);
        Assert.Equal(string.Join(Environment.NewLine, vm.VideoStopHintProblems), vm.VideoStopHintProblemsText);

        vm.VideoStopHintText = "{Taste}";
        Assert.False(vm.HasVideoStopHintProblems);
    }

    [Fact]
    public void StopHint_TooLongText_IsRejected()
    {
        var vm = CreateViewModel();
        vm.Draft.Video.StopHintText = new string('x', SettingsLimits.HintTextMaxLength + 1);

        Assert.Contains(vm.Validate(), e => e.Contains("Stopp-Hinweistext"));

        vm.Draft.Video.StopHintText = new string('x', SettingsLimits.HintTextMaxLength);
        Assert.DoesNotContain(vm.Validate(), e => e.Contains("Stopp-Hinweistext"));
    }

    [Fact]
    public void StopHint_IsSaved_AndEmptyFieldSavesNull()
    {
        var vm = CreateViewModel();
        vm.VideoShowStopHint = false;
        vm.VideoStopHintText = "Beenden: {Taste}";
        vm.SaveCommand.Execute(null);

        Assert.False(_store.Current.Video.ShowStopHint);
        Assert.Equal("Beenden: {Taste}", _store.Current.Video.StopHintText);

        var again = CreateViewModel();   // neue Sitzung mit dem gespeicherten Text
        Assert.Equal("Beenden: {Taste}", again.VideoStopHintText);
        again.VideoStopHintText = "";
        again.SaveCommand.Execute(null);
        Assert.Null(_store.Current.Video.StopHintText);
    }

    // ------------------------------------------------------------------ Standardwerte wiederherstellen

    [Fact]
    public async Task ResetToDefaults_RestoresVideoSettingsHintAndUiState()
    {
        _store.Current.Video.MicrophoneId = "mic-b";
        _store.Current.Video.MicrophoneName = "Testmikrofon B";
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.VideoEnabled = false;
        vm.VideoMaxDurationSeconds = 60;
        vm.VideoSuccessText = "Anders";
        vm.SelectedVideoQuality = vm.VideoQualities.Single(q => q.Quality == VideoQuality.Low);
        vm.VideoRecordAudio = false;
        vm.Draft.Video.Folder = Path.Combine(_dir, "Clips");
        vm.VideoFilePrefix = "Clip";
        vm.VideoHintText = "Eigener Text";
        vm.VideoHintStyle = HintStyle.Banner;
        vm.VideoShowStopHint = false;
        vm.VideoStopHintText = "Eigener Stopp";
        Keys(vm, KeyAction.VideoStart).Keys[0].RemoveCommand.Execute(null);
        _ui.ConfirmResult = true;

        await vm.ResetToDefaultsCommand.ExecuteAsync(null);

        var video = vm.Draft.Video;
        Assert.True(video.Enabled);
        Assert.Equal(300, video.MaxDurationSeconds);
        Assert.Null(video.SuccessText);
        Assert.Equal(VideoQuality.High, video.Quality);
        Assert.True(video.RecordAudio);
        Assert.Null(video.Folder);
        Assert.Null(video.MicrophoneId);
        Assert.Equal(VideoSettings.DefaultFilePrefix, video.FilePrefix);
        Assert.Null(vm.Draft.GuestScreen.VideoHintText);
        Assert.Equal(HintStyle.Pill, vm.Draft.GuestScreen.VideoHintStyle);
        Assert.Equal(["Enter"], vm.Draft.Input.VideoStartKeys);

        // UI-Zustand wurde neu aufgebaut.
        Assert.True(vm.VideoEnabled);
        Assert.Equal(string.Empty, vm.VideoSuccessText);
        Assert.Equal(VideoQuality.High, vm.SelectedVideoQuality.Quality);
        Assert.Equal(VideoSettings.DefaultFilePrefix, vm.VideoFilePrefix);
        Assert.Same(vm.Microphones[0], vm.SelectedMicrophone);
        Assert.Equal(3, vm.Microphones.Count);
        Assert.False(vm.HasMicrophoneHint);
        Assert.True(vm.IsDefaultVideoFolder);
        Assert.Equal(AppInfo.DefaultVideoFolder, vm.VideoFolderDisplay);
        Assert.Equal(["Eingabetaste"], Keys(vm, KeyAction.VideoStart).Keys.Select(k => k.DisplayName));
        Assert.Equal(HintStyle.Pill, vm.VideoHintStyle);
        Assert.Equal(string.Empty, vm.VideoHintText);
        Assert.Equal("Drücke die Eingabetaste für ein Video 🎬", vm.VideoHintPreviewText);
        Assert.True(video.ShowStopHint);
        Assert.Null(video.StopHintText);
        Assert.True(vm.VideoShowStopHint);
        Assert.Equal(string.Empty, vm.VideoStopHintText);
        Assert.Equal("Drücke die Eingabetaste, um die Videoaufnahme zu beenden", vm.VideoStopHintPreviewText);
    }

    [Fact]
    public async Task ResetToDefaults_WithMissingSavedMicrophone_DropsTheDisconnectedEntry()
    {
        _store.Current.Video.MicrophoneId = "mic-weg";
        _store.Current.Video.MicrophoneName = "Altes Mikrofon";
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        Assert.Equal(4, vm.Microphones.Count);
        _ui.ConfirmResult = true;

        await vm.ResetToDefaultsCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.Microphones.Count);
        Assert.Same(vm.Microphones[0], vm.SelectedMicrophone);
        Assert.False(vm.HasMicrophoneHint);
    }

    [Fact]
    public async Task ResetToDefaults_Declined_KeepsVideoSettings()
    {
        var vm = CreateViewModel();
        vm.VideoMaxDurationSeconds = 60;
        _ui.ConfirmResult = false;

        await vm.ResetToDefaultsCommand.ExecuteAsync(null);

        Assert.Equal(60, vm.Draft.Video.MaxDurationSeconds);
    }

    [Fact]
    public async Task ResetToDefaults_VideoOnlyChange_DoesNotRestartCamera()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.VideoMaxDurationSeconds = 60;
        _ui.ConfirmResult = true;
        var startsBefore = _camera.StartCalls.Count;

        await vm.ResetToDefaultsCommand.ExecuteAsync(null);

        Assert.Equal(startsBefore, _camera.StartCalls.Count);
    }

    // ------------------------------------------------------------------ Ton bei Kamera-Neustart in der Admin-Ansicht

    [Fact]
    public async Task RestartCamera_ConfiguresAudioFromDraft_BeforeStart()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.Draft.Video.MicrophoneId = "mic-a";

        await vm.RestartCameraAsync();

        Assert.Equal("mic-a", _camera.AudioAtStart.Last()?.MicrophoneId);

        vm.VideoRecordAudio = false;
        await vm.RestartCameraAsync();
        Assert.Null(_camera.AudioAtStart.Last());   // ohne Ton: kein Mikrofon

        vm.VideoRecordAudio = true;
        vm.VideoEnabled = false;
        await vm.RestartCameraAsync();
        Assert.Null(_camera.AudioAtStart.Last());   // Videomodus aus: ebenfalls ohne Mikrofon
    }

    [Fact]
    public async Task RestoreCamera_ConfiguresAudioFromSavedSettings_NotFromDraft()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.Draft.Video.MicrophoneId = "mic-b";
        await vm.RestartCameraAsync();
        Assert.Equal("mic-b", _camera.AudioAtStart.Last()?.MicrophoneId);

        var saved = new AppSettings();
        saved.Video.MicrophoneId = "mic-a";
        await vm.RestoreCameraAsync(saved);

        Assert.Equal("mic-a", _camera.AudioAtStart.Last()?.MicrophoneId);
    }
}
