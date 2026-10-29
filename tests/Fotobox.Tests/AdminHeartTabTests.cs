using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fotobox.Core;
using Fotobox.Core.Heart;
using Fotobox.Core.Peace;
using Fotobox.ViewModels.Admin;
using Fotobox.ViewModels.Booth;
using Fotobox.Views.Admin;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Admin-Tab „Fun“, Seite „Herz“: Entwurf, Kisscam-Einstellungen, Musik-Anhören, Validierung, Kachel und Ansicht.</summary>
[Collection(WpfCollection.Name)]
public sealed class AdminHeartTabTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-heart-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AdminViewModelTests.FakeStore _store;
    private readonly AdminViewModelTests.FakePinService _pin;
    private readonly AdminViewModelTests.FakeUi _ui = new();
    private readonly FakeMusicPlayer _music = new();
    private readonly FakeGestureService _gestures = new();
    private readonly FakeSoundPlayer _sound = new();
    private readonly TrackingTimeProvider _time = new();
    private readonly List<AdminViewModel> _created = [];

    public AdminHeartTabTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "overlays"));
        _store = new AdminViewModelTests.FakeStore(_dir);
        _pin = new AdminViewModelTests.FakePinService(_store);
    }

    public void Dispose()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        foreach (var vm in _created)
            vm.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void NoContext() => SynchronizationContext.SetSynchronizationContext(null);

    private AdminViewModel CreateViewModel(bool withMusic = true, bool withServices = false)
    {
        var vm = new AdminViewModel(_store, _pin, new AdminViewModelTests.FakeCamera(), new AdminViewModelTests.FakePrint(), _ui, _time,
            new AdminViewModelTests.FakeDisplays(AdminViewModelTests.AdminMonitors.Primary),
            detection: null, sound: withServices ? _sound : null, gestures: withServices ? _gestures : null,
            music: withMusic ? _music : null);
        _created.Add(vm);
        return vm;
    }

    // Eine echte (leere) Datei als Musikdatei – nur ihre Existenz zählt.
    private string CreateMusicFile(string name = "kiss.mp3")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private sealed class FakeMusicPlayer : IMusicPlayer
    {
        private EventHandler? _changed;
        public List<(string? Path, int Volume, bool Loop)> Plays { get; } = [];
        public int StopCalls { get; private set; }
        public int Subscribers { get; private set; }
        public bool IsPlaying { get; private set; }

        public event EventHandler? PlaybackChanged
        {
            add
            {
                _changed += value;
                Subscribers++;
            }
            remove
            {
                _changed -= value;
                Subscribers--;
            }
        }

        public void Play(string? path, int volumePercent, bool loop)
        {
            Plays.Add((path, volumePercent, loop));
            IsPlaying = true;
            _changed?.Invoke(this, EventArgs.Empty);
        }

        public void FadeOut(TimeSpan duration)
        {
        }

        public void Stop()
        {
            StopCalls++;
            if (!IsPlaying)
                return;
            IsPlaying = false;
            _changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Die Wiedergabe endet von selbst (oder von anderer Stelle gestoppt).</summary>
        public void EndPlayback()
        {
            IsPlaying = false;
            _changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Löst das Ereignis aus, ohne den Zustand zu ändern (z. B. auf einem anderen Thread).</summary>
        public void RaiseChanged() => _changed?.Invoke(this, EventArgs.Empty);

        public void SetPlayingSilently(bool playing) => IsPlaying = playing;
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        public List<(SendOrPostCallback Callback, object? State)> Posted { get; } = [];

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (Posted)
                Posted.Add((d, state));
        }

        public void RunAll()
        {
            List<(SendOrPostCallback Callback, object? State)> items;
            lock (Posted)
            {
                items = [.. Posted];
                Posted.Clear();
            }
            foreach (var (callback, state) in items)
                callback(state);
        }
    }

    // ------------------------------------------------------------------ Entwurf

    [Fact]
    public void Defaults_AreReadFromSettings()
    {
        NoContext();
        var vm = CreateViewModel();
        var d = new HeartSettings();

        Assert.True(vm.HeartEnabled);
        Assert.True(vm.HeartShapeClassic);
        Assert.True(vm.HeartShapeRoof);
        Assert.False(vm.HeartShapeInverted);
        Assert.False(vm.HeartShapeFingerHeart);
        Assert.False(vm.HeartHasNoShape);
        Assert.Equal(d.HoldMilliseconds, vm.HeartHoldMilliseconds);
        Assert.Equal(d.SensitivityPercent, vm.HeartSensitivityPercent);
        Assert.True(vm.HeartShowHoldFeedback);
        Assert.Equal(HeartAction.Kisscam, vm.HeartSelectedAction);
        Assert.True(vm.IsKisscamActionSelected);
        Assert.Equal(d.KisscamSeconds, vm.KisscamSeconds);
        Assert.Equal(string.Empty, vm.KisscamText);
        Assert.Equal(HeartSettings.DefaultKisscamText, vm.KisscamTextPlaceholder);
        Assert.Equal(string.Empty, vm.KisscamBannerText);
        Assert.Equal(HeartSettings.DefaultKisscamBannerText, vm.KisscamBannerTextPlaceholder);
        Assert.Equal(KisscamColor.PinkRed, vm.KisscamColor);
        Assert.False(vm.KisscamZoomEnabled);
        Assert.Equal(d.KisscamZoomPercent, vm.KisscamZoomPercent);
        Assert.Equal("Kisscam", vm.KisscamFilePrefix);
        Assert.Equal(d.KisscamMusicVolumePercent, vm.KisscamMusicVolumePercent);
        Assert.False(vm.HasKisscamMusic);
        Assert.Equal(AdminViewModel.KisscamNoMusicText, vm.KisscamMusicFileName);
        Assert.False(vm.KisscamMusicMissing);
        Assert.False(vm.KisscamMusicPreviewPlaying);
        Assert.Equal("An · Kisscam", vm.HeartStatusText);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void Setters_WriteIntoTheDraft_AndCountAsUnsavedChange_AndRevert()
    {
        NoContext();
        var vm = CreateViewModel();

        vm.HeartHoldMilliseconds = 1500;
        vm.HeartSensitivityPercent = 70;
        vm.HeartShowHoldFeedback = false;
        vm.HeartShapeInverted = true;
        vm.KisscamSeconds = 25;
        vm.KisscamText = "Küsst euch, {Sekunden}!";
        vm.KisscamBannerText = "LIEBE";
        vm.KisscamColor = KisscamColor.Gold;
        vm.KisscamZoomEnabled = true;
        vm.KisscamZoomPercent = 160;
        vm.KisscamFilePrefix = "Kuss";
        vm.KisscamMusicVolumePercent = 40;

        var h = vm.Draft.Heart;
        Assert.Equal(1500, h.HoldMilliseconds);
        Assert.Equal(70, h.SensitivityPercent);
        Assert.False(h.ShowHoldFeedback);
        Assert.True(h.ShapeInverted);
        Assert.Equal(25, h.KisscamSeconds);
        Assert.Equal("Küsst euch, {Sekunden}!", h.KisscamText);
        Assert.Equal("LIEBE", h.KisscamBannerText);
        Assert.Equal(KisscamColor.Gold, h.KisscamColor);
        Assert.True(h.KisscamZoomEnabled);
        Assert.Equal(160, h.KisscamZoomPercent);
        Assert.Equal("Kuss", h.KisscamFilePrefix);
        Assert.Equal(40, h.KisscamMusicVolumePercent);
        Assert.True(vm.HasUnsavedChanges);

        var d = new HeartSettings();
        vm.HeartHoldMilliseconds = d.HoldMilliseconds;
        vm.HeartSensitivityPercent = d.SensitivityPercent;
        vm.HeartShowHoldFeedback = true;
        vm.HeartShapeInverted = false;
        vm.KisscamSeconds = d.KisscamSeconds;
        vm.KisscamText = string.Empty;
        vm.KisscamBannerText = "  ";
        vm.KisscamColor = KisscamColor.PinkRed;
        vm.KisscamZoomEnabled = false;
        vm.KisscamZoomPercent = d.KisscamZoomPercent;
        vm.KisscamFilePrefix = d.KisscamFilePrefix;
        vm.KisscamMusicVolumePercent = d.KisscamMusicVolumePercent;
        Assert.Null(h.KisscamText);
        Assert.Null(h.KisscamBannerText);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void Setters_ClampIntoTheLimits()
    {
        NoContext();
        var vm = CreateViewModel();

        vm.HeartHoldMilliseconds = 10;
        Assert.Equal(SettingsLimits.HeartHoldMillisecondsMin, vm.HeartHoldMilliseconds);
        vm.HeartHoldMilliseconds = 99999;
        Assert.Equal(SettingsLimits.HeartHoldMillisecondsMax, vm.Draft.Heart.HoldMilliseconds);
        vm.HeartSensitivityPercent = 0;
        Assert.Equal(SettingsLimits.HeartSensitivityMin, vm.HeartSensitivityPercent);
        vm.HeartSensitivityPercent = 500;
        Assert.Equal(SettingsLimits.HeartSensitivityMax, vm.Draft.Heart.SensitivityPercent);
        vm.KisscamSeconds = 0;
        Assert.Equal(SettingsLimits.KisscamSecondsMin, vm.KisscamSeconds);
        vm.KisscamSeconds = 1000;
        Assert.Equal(SettingsLimits.KisscamSecondsMax, vm.Draft.Heart.KisscamSeconds);
        vm.KisscamZoomPercent = 100;
        Assert.Equal(SettingsLimits.KisscamZoomPercentMin, vm.KisscamZoomPercent);
        vm.KisscamZoomPercent = 999;
        Assert.Equal(SettingsLimits.KisscamZoomPercentMax, vm.Draft.Heart.KisscamZoomPercent);
        vm.KisscamMusicVolumePercent = -5;
        Assert.Equal(SettingsLimits.KisscamMusicVolumeMin, vm.KisscamMusicVolumePercent);
        vm.KisscamMusicVolumePercent = 500;
        Assert.Equal(SettingsLimits.KisscamMusicVolumeMax, vm.Draft.Heart.KisscamMusicVolumePercent);
    }

    [Fact]
    public void StatusText_FollowsSwitchAndAction_AndRaisesTheChange()
    {
        NoContext();
        var vm = CreateViewModel();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.HeartEnabled = false;

        Assert.Equal("Aus", vm.HeartStatusText);
        Assert.Contains(nameof(AdminViewModel.HeartStatusText), raised);
        vm.HeartEnabled = true;
        Assert.Equal("An · Kisscam", vm.HeartStatusText);
    }

    // ------------------------------------------------------------------ Formen

    [Fact]
    public void TheLastShape_IsLocked_AndNoShape_WhileEnabled_BlocksSaving()
    {
        NoContext();
        var vm = CreateViewModel();
        Assert.True(vm.HeartShapeClassicEnabled);
        Assert.True(vm.HeartShapeRoofEnabled);
        Assert.True(vm.HeartShapeInvertedEnabled);
        Assert.True(vm.HeartShapeFingerHeartEnabled);

        bool? closed = null;
        vm.CloseRequested += save => closed = save;
        vm.HeartShapeRoof = false;
        Assert.False(vm.HeartShapeClassicEnabled); // Klassisch ist jetzt die letzte Form
        Assert.True(vm.HeartShapeInvertedEnabled);
        vm.HeartShapeFingerHeart = true;
        Assert.True(vm.HeartShapeClassicEnabled);
        Assert.False(vm.HeartHasNoShape);

        vm.HeartShapeClassic = false; // (nur programmatisch möglich, die Ansicht sperrt den Haken)
        vm.HeartShapeFingerHeart = false;
        Assert.True(vm.HeartHasNoShape);
        Assert.True(vm.HeartShapeClassicEnabled);
        vm.SaveCommand.Execute(null);

        Assert.Null(closed);
        Assert.Equal(0, _store.SaveCount);
        Assert.Contains("Herzerkennung mindestens eine Herzform", vm.ValidationMessage);

        vm.HeartEnabled = false; // ausgegraut: kein Fehler, sonst wäre er nicht behebbar
        Assert.False(vm.HeartHasNoShape);
        Assert.DoesNotContain("Herzform", string.Join('\n', vm.Validate()));
    }

    [Fact]
    public void ShapeToggles_RaiseAllShapeEnabledProperties()
    {
        NoContext();
        var vm = CreateViewModel();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.HeartShapeRoof = false;

        Assert.Contains(nameof(AdminViewModel.HeartShapeClassicEnabled), raised);
        Assert.Contains(nameof(AdminViewModel.HeartShapeRoofEnabled), raised);
        Assert.Contains(nameof(AdminViewModel.HeartShapeInvertedEnabled), raised);
        Assert.Contains(nameof(AdminViewModel.HeartShapeFingerHeartEnabled), raised);
        Assert.Contains(nameof(AdminViewModel.HeartHasNoShape), raised);
    }

    // ------------------------------------------------------------------ Aktion

    [Fact]
    public void ActionOptions_ComeFromTheCatalog_AndFollowTheSelection()
    {
        NoContext();
        var vm = CreateViewModel();

        var option = Assert.Single(vm.HeartActionOptions);
        Assert.Equal(HeartAction.Kisscam, option.Action);
        Assert.Equal(HeartActions.All[0].DisplayName, option.DisplayName);
        Assert.Equal(HeartActions.All[0].Description, option.Description);
        Assert.True(option.IsSelected);
        Assert.True(vm.IsKisscamActionSelected);

        var raised = new List<string?>();
        option.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        option.IsSelected = false; // Abwählen ist nicht möglich: Gewählt wird immer durch eine andere Aktion
        Assert.Equal(HeartAction.Kisscam, vm.HeartSelectedAction);
        option.IsSelected = true;
        Assert.Equal(HeartAction.Kisscam, vm.Draft.Heart.Action);
        Assert.False(vm.HasUnsavedChanges);

        vm.HeartSelectedAction = (HeartAction)99; // unbekannt: ignoriert
        Assert.Equal(HeartAction.Kisscam, vm.HeartSelectedAction);
    }

    // ------------------------------------------------------------------ Texte

    [Fact]
    public void Texts_EmptyBecomesNull_AndUnknownPlaceholdersAreReportedWithoutBlocking()
    {
        NoContext();
        var vm = CreateViewModel();
        Assert.False(vm.HasKisscamTextProblems);
        Assert.Empty(vm.KisscamTextProblems);

        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.KisscamText = "Hallo {Name}, noch {sekunden} s {Name} {Ort}";

        Assert.True(vm.HasKisscamTextProblems);
        Assert.Equal(2, vm.KisscamTextProblems.Count);
        Assert.Contains("{Name}", vm.KisscamTextProblems[0]);
        Assert.Contains("{Ort}", vm.KisscamTextProblems[1]);
        Assert.Contains("{Sekunden}", vm.KisscamTextProblemsText);
        Assert.Contains(nameof(AdminViewModel.KisscamTextProblemsText), raised);
        Assert.Empty(vm.Validate()); // nicht blockierend

        vm.KisscamText = "   ";
        Assert.Null(vm.Draft.Heart.KisscamText);
        Assert.False(vm.HasKisscamTextProblems);
        Assert.Equal(string.Empty, vm.KisscamTextProblemsText);

        vm.KisscamBannerText = "";
        Assert.Null(vm.Draft.Heart.KisscamBannerText);
    }

    [Fact]
    public void TooLongTexts_BlockSaving()
    {
        NoContext();
        var vm = CreateViewModel();

        vm.Draft.Heart.KisscamText = new string('x', SettingsLimits.HintTextMaxLength + 1);
        vm.Draft.Heart.KisscamBannerText = new string('y', SettingsLimits.KisscamBannerMaxLength + 1);
        var errors = vm.Validate();

        Assert.Contains(errors, e => e.Contains("Kisscam-Text"));
        Assert.Contains(errors, e => e.Contains("Banner-Text"));

        vm.HeartEnabled = false;
        Assert.Empty(vm.Validate());
    }

    // ------------------------------------------------------------------ Dateipräfix

    [Fact]
    public void Prefix_ShowsAnExampleName_WithTheSanitizedPrefix()
    {
        NoContext();
        var vm = CreateViewModel();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.Equal("Dateien heißen zum Beispiel „Kisscam_20261029_153000.jpg“.", vm.KisscamFileNameExample);

        vm.KisscamFilePrefix = "Kuss";
        Assert.Equal("Dateien heißen zum Beispiel „Kuss_20261029_153000.jpg“.", vm.KisscamFileNameExample);
        Assert.Contains(nameof(AdminViewModel.KisscamFileNameExample), raised);

        vm.KisscamFilePrefix = "A/B:C";
        Assert.Equal("Dateien heißen zum Beispiel „ABC_20261029_153000.jpg“.", vm.KisscamFileNameExample);

        vm.KisscamFilePrefix = "";
        Assert.Equal("Dateien heißen zum Beispiel „Kisscam_20261029_153000.jpg“.", vm.KisscamFileNameExample);
    }

    [Fact]
    public void InvalidPrefix_BlocksSaving_AndIsIgnoredWhileTheDetectionIsOff()
    {
        NoContext();
        var vm = CreateViewModel();

        vm.KisscamFilePrefix = "";
        Assert.Contains(vm.Validate(), e => e.Contains("Dateipräfix der Kisscam darf nicht leer"));
        vm.KisscamFilePrefix = "a/b";
        Assert.Contains(vm.Validate(), e => e.Contains("Dateipräfix der Kisscam darf höchstens"));
        vm.KisscamFilePrefix = new string('a', SettingsLimits.FilePrefixMaxLength + 1);
        Assert.Contains(vm.Validate(), e => e.Contains("Dateipräfix der Kisscam darf höchstens"));

        vm.HeartEnabled = false;
        Assert.Empty(vm.Validate());
    }

    // ------------------------------------------------------------------ Speichern, Zurücksetzen

    [Fact]
    public void DefaultSettings_PassTheValidation_AndSaveKeepsTheHeartSettings()
    {
        NoContext();
        var vm = CreateViewModel();
        Assert.Empty(vm.Validate());

        vm.HeartHoldMilliseconds = 1400;
        vm.KisscamSeconds = 15;
        vm.KisscamText = "Los, {Sekunden}!";
        vm.KisscamColor = KisscamColor.Purple;
        vm.KisscamFilePrefix = "Kuss";
        vm.SaveCommand.Execute(null);

        Assert.Equal(1, _store.SaveCount);
        var saved = _store.Current.Heart;
        Assert.Equal(1400, saved.HoldMilliseconds);
        Assert.Equal(15, saved.KisscamSeconds);
        Assert.Equal("Los, {Sekunden}!", saved.KisscamText);
        Assert.Equal(KisscamColor.Purple, saved.KisscamColor);
        Assert.Equal("Kuss", saved.KisscamFilePrefix);
    }

    [Fact]
    public async Task ResetToDefaults_RestoresTheHeartSettings_AndRefreshesTheProperties()
    {
        NoContext();
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.HeartEnabled = false;
        vm.KisscamSeconds = 40;
        vm.KisscamColor = KisscamColor.Red;
        vm.Draft.Heart.KisscamMusicPath = CreateMusicFile();
        _ui.ConfirmResult = true;
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await vm.ResetToDefaultsCommand.ExecuteAsync(null);

        Assert.True(vm.HeartEnabled);
        Assert.Equal(new HeartSettings().KisscamSeconds, vm.KisscamSeconds);
        Assert.Equal(KisscamColor.PinkRed, vm.KisscamColor);
        Assert.Equal("An · Kisscam", vm.HeartStatusText);
        Assert.Contains(string.Empty, raised.Select(r => r ?? string.Empty)); // alle Eigenschaften neu gelesen
        Assert.True(vm.HeartActionOptions[0].IsSelected);
    }

    // ------------------------------------------------------------------ Musik

    [Fact]
    public async Task PickingMusic_StoresThePath_ShowsTheFileName_AndRemovingClearsIt()
    {
        NoContext();
        var vm = CreateViewModel();
        var path = CreateMusicFile("Song Eins.mp3");
        _ui.AudioFileToPick = path;
        Assert.False(vm.RemoveKisscamMusicCommand.CanExecute(null));

        vm.PickKisscamMusicCommand.Execute(null);
        await vm.KisscamMusicCheck;

        Assert.Equal(path, vm.Draft.Heart.KisscamMusicPath);
        Assert.Equal("Song Eins.mp3", vm.KisscamMusicFileName);
        Assert.Equal(path, vm.KisscamMusicPath);
        Assert.True(vm.HasKisscamMusic);
        Assert.False(vm.KisscamMusicMissing);
        Assert.True(vm.RemoveKisscamMusicCommand.CanExecute(null));
        Assert.True(vm.HasUnsavedChanges);

        _ui.AudioFileToPick = null; // Abbrechen ändert nichts
        vm.PickKisscamMusicCommand.Execute(null);
        Assert.Equal(path, vm.Draft.Heart.KisscamMusicPath);

        vm.RemoveKisscamMusicCommand.Execute(null);
        Assert.Null(vm.Draft.Heart.KisscamMusicPath);
        Assert.Equal(AdminViewModel.KisscamNoMusicText, vm.KisscamMusicFileName);
        Assert.False(vm.HasKisscamMusic);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void PickingAnOverlongPath_IsRejectedWithAMessage()
    {
        NoContext();
        var vm = CreateViewModel();
        _ui.AudioFileToPick = "C:\\" + new string('a', SettingsLimits.KisscamMusicPathMaxLength) + ".mp3";

        vm.PickKisscamMusicCommand.Execute(null);

        Assert.Null(vm.Draft.Heart.KisscamMusicPath);
        var info = Assert.Single(_ui.InfoCalls);
        Assert.Equal("Musikdatei", info.Title);
    }

    [Fact]
    public async Task MissingMusicFile_ShowsAWarning_WithoutBlockingSaving()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.KisscamMusicFileExists = _ => false;
        _ui.AudioFileToPick = Path.Combine(_dir, "gibt-es-nicht.mp3");

        vm.PickKisscamMusicCommand.Execute(null);
        await vm.KisscamMusicCheck;

        Assert.True(vm.KisscamMusicMissing);
        Assert.False(vm.ToggleKisscamMusicPreviewCommand.CanExecute(null));
        Assert.Empty(vm.Validate());
        vm.SaveCommand.Execute(null);
        Assert.Equal(1, _store.SaveCount);

        vm.RemoveKisscamMusicCommand.Execute(null);
        Assert.False(vm.KisscamMusicMissing);
    }

    [Fact]
    public async Task MusicCheck_IsMarkedMissing_WhenTheProbeHangs_AndTheTimeoutExpires()
    {
        NoContext();
        var vm = CreateViewModel();
        using var gate = new ManualResetEventSlim();
        vm.KisscamMusicFileExists = _ =>
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            return true;
        };
        _ui.AudioFileToPick = Path.Combine(_dir, "netz.mp3");

        vm.PickKisscamMusicCommand.Execute(null);
        Assert.False(vm.KisscamMusicMissing); // die Prüfung läuft noch, die Oberfläche wartet nicht
        _time.Advance(vm.FolderProbeTimeout + TimeSpan.FromSeconds(1));
        await vm.KisscamMusicCheck;

        Assert.True(vm.KisscamMusicMissing);
        gate.Set();
    }

    [Fact]
    public async Task StaleMusicCheck_DoesNotOverwriteTheNewerResult()
    {
        NoContext();
        var vm = CreateViewModel();
        using var gate = new ManualResetEventSlim();
        vm.KisscamMusicFileExists = _ =>
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            return false;
        };
        _ui.AudioFileToPick = Path.Combine(_dir, "langsam.mp3");
        vm.PickKisscamMusicCommand.Execute(null);
        var slow = vm.KisscamMusicCheck;

        vm.RemoveKisscamMusicCommand.Execute(null); // neuere Auswahl: keine Datei
        gate.Set();
        await slow;

        Assert.False(vm.KisscamMusicMissing);
    }

    [Fact]
    public async Task MusicFileFromTheSettings_IsCheckedWhenTheViewModelIsCreated()
    {
        NoContext();
        _store.Current.Heart.KisscamMusicPath = Path.Combine(_dir, "weg.mp3");
        var vm = CreateViewModel();
        await vm.KisscamMusicCheck;
        Assert.True(vm.KisscamMusicMissing);

        _store.Current.Heart.KisscamMusicPath = CreateMusicFile();
        var ok = CreateViewModel();
        await ok.KisscamMusicCheck;
        Assert.False(ok.KisscamMusicMissing);
    }

    [Fact]
    public async Task Preview_TogglesPlayback_WithTheDraftValues_AndStopsOnDemand()
    {
        NoContext();
        var vm = CreateViewModel();
        Assert.False(vm.ToggleKisscamMusicPreviewCommand.CanExecute(null)); // keine Datei
        var path = CreateMusicFile();
        _ui.AudioFileToPick = path;
        vm.PickKisscamMusicCommand.Execute(null);
        await vm.KisscamMusicCheck;
        vm.KisscamMusicVolumePercent = 35;
        Assert.True(vm.ToggleKisscamMusicPreviewCommand.CanExecute(null));
        Assert.Equal("Anhören", vm.KisscamMusicPreviewButtonText);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ToggleKisscamMusicPreviewCommand.Execute(null);

        Assert.True(vm.KisscamMusicPreviewPlaying);
        Assert.Equal("Stopp", vm.KisscamMusicPreviewButtonText);
        Assert.Equal((path, 35, true), Assert.Single(_music.Plays));
        Assert.Contains(nameof(AdminViewModel.KisscamMusicPreviewPlaying), raised);

        vm.ToggleKisscamMusicPreviewCommand.Execute(null);

        Assert.False(vm.KisscamMusicPreviewPlaying);
        Assert.Equal(1, _music.StopCalls);
        Assert.Equal("Anhören", vm.KisscamMusicPreviewButtonText);
    }

    [Fact]
    public async Task Preview_WithoutAPlayer_IsDisabled()
    {
        NoContext();
        var vm = CreateViewModel(withMusic: false);
        _ui.AudioFileToPick = CreateMusicFile();
        vm.PickKisscamMusicCommand.Execute(null);
        await vm.KisscamMusicCheck;

        Assert.False(vm.ToggleKisscamMusicPreviewCommand.CanExecute(null));
        vm.ToggleKisscamMusicPreviewCommand.Execute(null);
        Assert.False(vm.KisscamMusicPreviewPlaying);
        Assert.Empty(_music.Plays);
    }

    private async Task<AdminViewModel> StartPreviewAsync()
    {
        var vm = CreateViewModel();
        _ui.AudioFileToPick = CreateMusicFile();
        vm.PickKisscamMusicCommand.Execute(null);
        await vm.KisscamMusicCheck;
        vm.ToggleKisscamMusicPreviewCommand.Execute(null);
        Assert.True(vm.KisscamMusicPreviewPlaying);
        return vm;
    }

    [Fact]
    public async Task Preview_Stops_WhenTheFileChanges_IsRemoved_OrTheDetectionIsSwitchedOff()
    {
        NoContext();
        var vm = await StartPreviewAsync();
        vm.RemoveKisscamMusicCommand.Execute(null);
        Assert.False(vm.KisscamMusicPreviewPlaying);
        Assert.Equal(1, _music.StopCalls);

        vm.PickKisscamMusicCommand.Execute(null);
        await vm.KisscamMusicCheck;
        vm.ToggleKisscamMusicPreviewCommand.Execute(null);
        Assert.True(vm.KisscamMusicPreviewPlaying);
        vm.HeartEnabled = false;
        Assert.False(vm.KisscamMusicPreviewPlaying);
        Assert.Equal(2, _music.StopCalls);
    }

    [Fact]
    public async Task Preview_Stops_OnPageChangeTabChangeAndDispose()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.IsFunTabSelected = true;
        vm.FunPage = FunPage.Heart;
        _ui.AudioFileToPick = CreateMusicFile();
        vm.PickKisscamMusicCommand.Execute(null);
        await vm.KisscamMusicCheck;
        vm.ToggleKisscamMusicPreviewCommand.Execute(null);
        Assert.True(vm.KisscamMusicPreviewPlaying); // auf der Seite „Herz“ läuft sie weiter

        vm.FunPage = FunPage.Start;
        Assert.False(vm.KisscamMusicPreviewPlaying);
        Assert.Equal(1, _music.StopCalls);

        vm.FunPage = FunPage.Heart;
        vm.ToggleKisscamMusicPreviewCommand.Execute(null);
        Assert.True(vm.KisscamMusicPreviewPlaying);
        vm.IsFunTabSelected = false;
        Assert.False(vm.KisscamMusicPreviewPlaying);
        Assert.Equal(2, _music.StopCalls);

        vm.IsFunTabSelected = true;
        vm.ToggleKisscamMusicPreviewCommand.Execute(null);
        Assert.True(_music.IsPlaying);
        vm.Dispose();
        Assert.False(_music.IsPlaying);
        Assert.Equal(3, _music.StopCalls);
        Assert.Equal(0, _music.Subscribers); // abgemeldet
    }

    [Fact]
    public void NeverStartedPreview_DoesNotStopTheSharedPlayer()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.IsFunTabSelected = true;
        vm.FunPage = FunPage.Heart;
        vm.FunPage = FunPage.Start;
        vm.IsFunTabSelected = false;
        vm.Dispose();

        Assert.Equal(0, _music.StopCalls); // der Player gehört auch der Gastansicht
    }

    [Fact]
    public async Task Preview_Ends_WhenThePlaybackEndsByItself()
    {
        NoContext();
        var vm = await StartPreviewAsync();

        _music.EndPlayback();

        Assert.False(vm.KisscamMusicPreviewPlaying);
        Assert.Equal("Anhören", vm.KisscamMusicPreviewButtonText);
        Assert.Equal(0, _music.StopCalls);
    }

    [Fact]
    public void PlaybackChanged_FromAnotherThread_IsMarshalledToTheUiContext()
    {
        var context = new RecordingContext();
        SynchronizationContext.SetSynchronizationContext(context);
        var vm = CreateViewModel();
        _ui.AudioFileToPick = CreateMusicFile();
        vm.PickKisscamMusicCommand.Execute(null);
        // Die Prüfung der Datei meldet ihr Ergebnis ebenfalls über den (hier aufzeichnenden) UI-Kontext.
        Assert.True(SpinWait.SpinUntil(() =>
        {
            context.RunAll();
            return vm.KisscamMusicCheck.IsCompleted;
        }, TimeSpan.FromSeconds(10)));
        context.RunAll();
        vm.ToggleKisscamMusicPreviewCommand.Execute(null);
        context.RunAll();
        Assert.True(vm.KisscamMusicPreviewPlaying);

        _music.SetPlayingSilently(false);
        Task.Run(() => _music.RaiseChanged()).GetAwaiter().GetResult();

        Assert.True(vm.KisscamMusicPreviewPlaying); // noch nicht: das Ereignis wartet auf den UI-Kontext
        context.RunAll();
        Assert.False(vm.KisscamMusicPreviewPlaying);

        vm.Dispose();
        _music.SetPlayingSilently(true);
        Task.Run(() => _music.RaiseChanged()).GetAwaiter().GetResult();
        context.RunAll(); // nach dem Schließen wirkungslos, wirft nicht
    }

    [Fact]
    public async Task PreviewState_DoesNotCountAsAnUnsavedChange_AndKeepsTheSavedMessage()
    {
        NoContext();
        var vm = CreateViewModel();
        _ui.AudioFileToPick = CreateMusicFile();
        vm.PickKisscamMusicCommand.Execute(null);
        await vm.KisscamMusicCheck;
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.False(string.IsNullOrEmpty(vm.SaveStatusMessage));
        Assert.False(vm.HasUnsavedChanges);

        vm.ToggleKisscamMusicPreviewCommand.Execute(null);
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(string.IsNullOrEmpty(vm.SaveStatusMessage));
        vm.ToggleKisscamMusicPreviewCommand.Execute(null);
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(string.IsNullOrEmpty(vm.SaveStatusMessage));
    }

    // ------------------------------------------------------------------ Vorschau-Verhalten (vorerst keine Herz-Vorschau)

    [Fact]
    public void HeartPage_StartsNoPreview_AndStopsThePeacePreview()
    {
        NoContext();
        var vm = CreateViewModel(withServices: true);
        vm.IsFunTabSelected = true;
        vm.FunPage = FunPage.Peace;
        Assert.Equal(PeaceMode.Watching, vm.PeacePreview.Mode);
        Assert.Equal(1, _gestures.OpenRequests);

        vm.FunPage = FunPage.Heart;

        Assert.Equal(PeaceMode.Off, vm.PeacePreview.Mode);
        Assert.Equal(0, _gestures.OpenRequests);
        Assert.False(vm.FunPreview.IsActive);

        vm.HeartHoldMilliseconds = 1200; // Änderungen auf der Herz-Seite lösen nichts aus
        vm.KisscamSeconds = 20;
        Assert.Equal(PeaceMode.Off, vm.PeacePreview.Mode);
        Assert.Equal(0, _gestures.OpenRequests);

        vm.FunPage = FunPage.MiddleFinger;
        Assert.Equal(PeaceMode.Watching, vm.PeacePreview.Mode);
    }

    [Fact]
    public void MiddleFingerKisscamTick_DoesNotRestartThePreview()
    {
        NoContext();
        var vm = CreateViewModel(withServices: true);
        vm.IsFunTabSelected = true;
        vm.FunPage = FunPage.MiddleFinger;
        var requests = _gestures.Requests.Count;

        vm.MiddleFingerActionCancelKisscam = false; // ändert die Sperre der Haken, aber nicht die Erkennung der Vorschau

        Assert.Equal(PeaceMode.Watching, vm.PeacePreview.Mode);
        Assert.Equal(requests, _gestures.Requests.Count);
    }

    // ------------------------------------------------------------------ Ansicht

    private void WithWindow(Action<AdminWindow, AdminViewModel> body)
    {
        var vm = CreateViewModel();
        var window = new AdminWindow(vm) { ShowInTaskbar = false };
        body(window, vm);
    }

    private static TabControl FindTabControl(DependencyObject parent) =>
        FindTabControlOrNull(parent) ?? throw new InvalidOperationException("Kein TabControl gefunden.");

    private static TabControl? FindTabControlOrNull(DependencyObject parent)
    {
        if (parent is TabControl tabs)
            return tabs;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (FindTabControlOrNull(child) is { } found)
                return found;
        }
        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                yield return match;
            foreach (var nested in FindDescendants<T>(child))
                yield return nested;
        }
    }

    private static FrameworkElement ShowFunTab(AdminWindow window)
    {
        var tabs = FindTabControl(window);
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header as string == "Fun");
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1100, 750));
        root.Arrange(new Rect(0, 0, 1100, 750));
        root.UpdateLayout();
        return root;
    }

    // Wartet auf die Aufgabe und arbeitet dabei die Warteschlange des Dispatchers ab (Fortsetzungen und Bindungen laufen dort).
    private static void WaitFor(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(5);
        }
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        Assert.True(task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static HeartSettingsView HeartView(AdminWindow window) => (HeartSettingsView)window.FindName("HeartPage");

    private static T InView<T>(HeartSettingsView view, string name) where T : class =>
        view.FindName(name) as T ?? throw new InvalidOperationException(name + " nicht gefunden.");

    [StaFact]
    public void StartPage_ShowsTheHeartTile_WithItsStatus()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            ShowFunTab(window);
            var start = (UIElement)window.FindName("FunStartPage");
            var tile = (Button)window.FindName("FunTileHeart");

            Assert.Equal(Visibility.Visible, start.Visibility);
            Assert.Equal(Visibility.Collapsed, HeartView(window).Visibility);
            Assert.Equal("Herz", AutomationProperties.GetName(tile));
            Assert.Same(window.TryFindResource("FunTile"), tile.Style);
            Assert.Contains(FindDescendants<TextBlock>(tile), t => t.Text == "Verstecktes Extra: Ein Herz aus zwei Händen startet die Kisscam.");
            Assert.Contains(FindDescendants<TextBlock>(tile), t => t.Text == "An · Kisscam");

            vm.HeartEnabled = false;
            Assert.Contains(FindDescendants<TextBlock>(tile), t => t.Text == "Aus");
        });
    }

    [StaFact]
    public void Tile_NavigatesToThePage_AndTheBackButtonReturns()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            ShowFunTab(window);
            var tile = (Button)window.FindName("FunTileHeart");

            Assert.True(tile.Command.CanExecute(tile.CommandParameter));
            tile.Command.Execute(tile.CommandParameter);

            Assert.Equal(FunPage.Heart, vm.FunPage);
            Assert.Equal(Visibility.Visible, HeartView(window).Visibility);
            Assert.Equal(Visibility.Collapsed, ((UIElement)window.FindName("FunStartPage")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((UIElement)window.FindName("PeacePage")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((UIElement)window.FindName("MiddleFingerPage")).Visibility);

            var back = InView<Button>(HeartView(window), "HeartBackButton");
            back.Command.Execute(back.CommandParameter);
            Assert.Equal(FunPage.Start, vm.FunPage);
        });
    }

    [StaTheory]
    [InlineData(AdminThemeMode.Light)]
    [InlineData(AdminThemeMode.Dark)]
    public void Page_LaysOutInBothThemes_WithAllCards(AdminThemeMode theme)
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.IsAdminThemeLight = theme == AdminThemeMode.Light;
            vm.IsAdminThemeDark = theme == AdminThemeMode.Dark;
            vm.FunPage = FunPage.Heart;
            ShowFunTab(window);

            var view = HeartView(window);
            Assert.True(view.ActualHeight > 0);
            Assert.Contains(FindDescendants<ToggleButton>(view), c => AutomationProperties.GetName(c) == "Herzerkennung aktiv");
            foreach (var name in new[] { "HeartShapeClassicCheck", "HeartShapeRoofCheck", "HeartShapeInvertedCheck", "HeartShapeFingerHeartCheck" })
                Assert.True(InView<CheckBox>(view, name).IsEnabled);
            Assert.Equal(Visibility.Collapsed, InView<TextBlock>(view, "HeartNoShapeWarning").Visibility);
            Assert.NotEmpty(FindDescendants<NumberStepper>(view));
            Assert.Equal(Visibility.Visible, InView<Border>(view, "HeartKisscamCard").Visibility);
            Assert.Equal(5, FindDescendants<KisscamFrameSketch>(view).Count());
            Assert.True(InView<RadioButton>(view, "KisscamColorPinkRed").IsChecked);
        });
    }

    [StaFact]
    public void Page_GreysOutTheSettings_WhenSwitchedOff_ButNotTheSwitch()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.FunPage = FunPage.Heart;
            ShowFunTab(window);
            var view = HeartView(window);
            var panel = InView<StackPanel>(view, "HeartSettingsPanel");
            var check = InView<CheckBox>(view, "HeartEnabledCheck");
            Assert.True(panel.IsEnabled);

            vm.HeartEnabled = false;

            Assert.False(panel.IsEnabled);
            Assert.True(check.IsEnabled);
            Assert.False(InView<CheckBox>(view, "HeartShapeClassicCheck").IsEnabled);
        });
    }

    [StaFact]
    public void Page_ShapeChecks_LockTheLastOne_AndShowTheWarning()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.FunPage = FunPage.Heart;
            ShowFunTab(window);
            var view = HeartView(window);
            var classic = InView<CheckBox>(view, "HeartShapeClassicCheck");
            var roof = InView<CheckBox>(view, "HeartShapeRoofCheck");

            roof.IsChecked = false;
            Assert.False(classic.IsEnabled);

            vm.HeartShapeClassic = false; // programmatisch (die Ansicht sperrt den Haken)
            Assert.Equal(Visibility.Visible, InView<TextBlock>(view, "HeartNoShapeWarning").Visibility);
        });
    }

    [StaFact]
    public void Page_CardsWriteIntoTheViewModel_AndFollowIt()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.FunPage = FunPage.Heart;
            ShowFunTab(window);
            var view = HeartView(window);

            InView<NumberStepper>(view, "HeartHoldStepper").Value = 1700;
            InView<NumberStepper>(view, "HeartSensitivityStepper").Value = 80;
            InView<NumberStepper>(view, "KisscamSecondsStepper").Value = 30;
            InView<NumberStepper>(view, "KisscamMusicVolumeStepper").Value = 25;
            Assert.Equal(1700, vm.Draft.Heart.HoldMilliseconds);
            Assert.Equal(80, vm.Draft.Heart.SensitivityPercent);
            Assert.Equal(30, vm.Draft.Heart.KisscamSeconds);
            Assert.Equal(25, vm.Draft.Heart.KisscamMusicVolumePercent);

            vm.KisscamSeconds = 12;
            Assert.Equal(12, InView<NumberStepper>(view, "KisscamSecondsStepper").Value);

            InView<RadioButton>(view, "KisscamColorGold").IsChecked = true;
            Assert.Equal(KisscamColor.Gold, vm.KisscamColor);
            Assert.False(InView<RadioButton>(view, "KisscamColorPinkRed").IsChecked);
            vm.KisscamColor = KisscamColor.BlackWhite;
            Assert.True(InView<RadioButton>(view, "KisscamColorBlackWhite").IsChecked);

            InView<TextBox>(view, "KisscamPrefixBox").Text = "Kuss";
            Assert.Equal("Kuss", vm.Draft.Heart.KisscamFilePrefix);
            Assert.Equal(vm.KisscamFileNameExample, InView<TextBlock>(view, "KisscamFileNameExampleText").Text);
        });
    }

    [StaFact]
    public void Page_TextPlaceholders_ShowTheDefaults_WhileEmpty_AndTheProblems_WhenUnknown()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.FunPage = FunPage.Heart;
            ShowFunTab(window);
            var view = HeartView(window);
            var placeholder = InView<TextBlock>(view, "KisscamTextPlaceholder");
            var bannerPlaceholder = InView<TextBlock>(view, "KisscamBannerPlaceholder");
            var problems = InView<TextBlock>(view, "KisscamTextProblems");
            Assert.Equal(HeartSettings.DefaultKisscamText, placeholder.Text);
            Assert.Equal(Visibility.Visible, placeholder.Visibility);
            Assert.Equal(HeartSettings.DefaultKisscamBannerText, bannerPlaceholder.Text);
            Assert.Equal(Visibility.Collapsed, problems.Visibility);
            Assert.Equal(SettingsLimits.HintTextMaxLength, InView<TextBox>(view, "KisscamTextBox").MaxLength);
            Assert.Equal(SettingsLimits.KisscamBannerMaxLength, InView<TextBox>(view, "KisscamBannerTextBox").MaxLength);

            InView<TextBox>(view, "KisscamTextBox").Text = "Hallo {Name}";

            Assert.Equal(Visibility.Collapsed, placeholder.Visibility);
            Assert.Equal(Visibility.Visible, problems.Visibility);
            Assert.Contains("{Name}", problems.Text);
            Assert.Equal("Hallo {Name}", vm.Draft.Heart.KisscamText);
        });
    }

    [StaFact]
    public void Page_Zoom_StepperIsGreyedOutUntilEnabled()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.FunPage = FunPage.Heart;
            ShowFunTab(window);
            var view = HeartView(window);
            var panel = InView<StackPanel>(view, "KisscamZoomPanel");
            Assert.False(panel.IsEnabled);
            Assert.Contains(FindDescendants<TextBlock>(panel), t => t.Text == "Stärkerer Zoom kostet Auflösung im Druck.");

            InView<CheckBox>(view, "KisscamZoomCheck").IsChecked = true;

            Assert.True(vm.Draft.Heart.KisscamZoomEnabled);
            Assert.True(panel.IsEnabled);
            InView<NumberStepper>(view, "KisscamZoomStepper").Value = 170;
            Assert.Equal(170, vm.Draft.Heart.KisscamZoomPercent);
        });
    }

    [StaFact]
    public void Page_Music_ButtonsFollowTheState_AndShowTheMissingWarning()
    {
        // Wie in der Anwendung meldet die Dateiprüfung ihr Ergebnis über den Dispatcher (sonst ändert ein Thread des Pools Befehle der Knöpfe).
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        WithWindow((window, vm) =>
        {
            vm.FunPage = FunPage.Heart;
            ShowFunTab(window);
            var view = HeartView(window);
            var pick = InView<Button>(view, "KisscamMusicPickButton");
            var remove = InView<Button>(view, "KisscamMusicRemoveButton");
            var preview = InView<Button>(view, "KisscamMusicPreviewButton");
            var name = InView<TextBlock>(view, "KisscamMusicFileNameText");
            var warning = InView<TextBlock>(view, "KisscamMusicMissingWarning");
            Assert.Same(vm.PickKisscamMusicCommand, pick.Command);
            Assert.Equal(AdminViewModel.KisscamNoMusicText, name.Text);
            Assert.False(remove.IsEnabled);
            Assert.False(preview.IsEnabled);
            Assert.Equal(Visibility.Collapsed, warning.Visibility);
            Assert.Equal(AdminViewModel.KisscamMusicMissingText, warning.Text);

            _ui.AudioFileToPick = CreateMusicFile("Titel.wav");
            vm.KisscamMusicFileExists = _ => true;
            pick.Command.Execute(null);
            WaitFor(vm.KisscamMusicCheck);

            Assert.Equal("Titel.wav", name.Text);
            Assert.True(remove.IsEnabled);
            Assert.True(preview.IsEnabled);
            Assert.Equal("Anhören", preview.Content);
            preview.Command.Execute(null);
            Assert.Equal("Stopp", preview.Content);

            vm.KisscamMusicFileExists = _ => false;
            vm.RemoveKisscamMusicCommand.Execute(null);
            _ui.AudioFileToPick = Path.Combine(_dir, "fehlt.mp3");
            pick.Command.Execute(null);
            WaitFor(vm.KisscamMusicCheck);
            Assert.Equal(Visibility.Visible, warning.Visibility);
            Assert.False(preview.IsEnabled);
        });
    }

    [StaFact]
    public void Page_ActionCards_ListTheCatalog_AndHideTheKisscamCardForOtherActions()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.FunPage = FunPage.Heart;
            ShowFunTab(window);
            var view = HeartView(window);
            var list = InView<ItemsControl>(view, "HeartActionList");

            var radio = Assert.Single(FindDescendants<RadioButton>(list));
            Assert.True(radio.IsChecked);
            Assert.Equal("Aktion: Kisscam", AutomationProperties.GetName(radio));
            Assert.Contains(FindDescendants<TextBlock>(radio), t => t.Text == HeartActions.All[0].Description);
            Assert.Equal(Visibility.Visible, InView<Border>(view, "HeartKisscamCard").Visibility);
        });
    }

    [StaFact]
    public void Page_AnalysesStepper_IsBoundToTheSharedPeaceValue_AndHasAnInfoBadge()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.FunPage = FunPage.Heart;
            ShowFunTab(window);
            var view = HeartView(window);
            var stepper = InView<NumberStepper>(view, "HeartAnalysesStepper");
            Assert.Equal(5, stepper.Value);
            Assert.Equal(SettingsLimits.PeaceAnalysesPerSecondMin, stepper.Minimum);
            Assert.Equal(SettingsLimits.PeaceAnalysesPerSecondMax, stepper.Maximum);

            stepper.Value = 8;
            Assert.Equal(8, vm.Draft.Peace.AnalysesPerSecond);
            vm.PeaceAnalysesPerSecond = 3;
            Assert.Equal(3, stepper.Value);

            var badge = InView<Button>(view, "HeartAnalysesInfo");
            Assert.Equal(AdminInfoTexts.HeartAnalysesPerSecond, badge.ToolTip);
            Assert.False(string.IsNullOrWhiteSpace(AdminInfoTexts.HeartAnalysesPerSecond));
            Assert.Equal(AdminInfoTexts.AnalysesPerSecondName, AutomationProperties.GetName(badge));
            Assert.Equal(AdminInfoTexts.HeartAnalysesPerSecond, AutomationProperties.GetHelpText(badge));
            Assert.Same(window.TryFindResource("InfoBadge"), badge.Style);
            Assert.Equal(60000, ToolTipService.GetShowDuration(badge));
            Assert.True(ToolTipService.GetShowOnDisabled(badge));
            Assert.False(badge.Focusable);
            Assert.Contains("Herz", AdminInfoTexts.PeaceAnalysesPerSecond);
            Assert.Contains("Herz", AdminInfoTexts.MiddleFingerAnalysesPerSecond);
        });
    }

    [StaFact]
    public void KisscamFrameSketch_DrawsADistinctFrame_PerColor()
    {
        NoContext();
        var hashes = new HashSet<string>();
        foreach (var color in Enum.GetValues<KisscamColor>())
        {
            var sketch = new KisscamFrameSketch { Color = color };
            sketch.Measure(new Size(200, 120));
            sketch.Arrange(new Rect(0, 0, sketch.DesiredSize.Width, sketch.DesiredSize.Height));
            Assert.Equal(144, sketch.ActualWidth);
            var bitmap = new RenderTargetBitmap(144, 81, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(sketch);
            var pixels = new byte[144 * 81 * 4];
            bitmap.CopyPixels(pixels, 144 * 4, 0);
            Assert.Contains(pixels, b => b != 0);
            hashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels)));
        }

        Assert.Equal(Enum.GetValues<KisscamColor>().Length, hashes.Count);
    }
}
