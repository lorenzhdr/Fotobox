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
using Fotobox.Core.Music;
using Fotobox.Core.Peace;
using Fotobox.ViewModels.Admin;
using Fotobox.ViewModels.Booth;
using Fotobox.Views.Admin;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Admin-Tab „Fun“, Seite „Herz“: Entwurf, Kisscam-Einstellungen, Musikordner und Songliste, Validierung, Kachel und Ansicht.</summary>
[Collection(WpfCollection.Name)]
public sealed class AdminHeartTabTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-heart-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AdminViewModelTests.FakeStore _store;
    private readonly AdminViewModelTests.FakePinService _pin;
    private readonly AdminViewModelTests.FakeUi _ui = new();
    private readonly FakeMusicPlayer _music = new();
    private readonly FakeMusicLibrary _library = new();
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
            music: withMusic ? _music : null, library: _library);
        _created.Add(vm);
        return vm;
    }

    private sealed class FakeMusicPlayer : IMusicPlayer
    {
        private EventHandler? _changed;
        public List<(string? Path, int Volume, bool Loop, TimeSpan StartAt)> Plays { get; } = [];
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

        public event EventHandler<MusicPlaybackFailedEventArgs>? PlaybackFailed;

        public event EventHandler<MusicPositionEventArgs>? PositionChanged;

        /// <summary>Jeder Start scheitert (Datei defekt, Öffnungsfrist verpasst …): kein PlaybackChanged, nur PlaybackFailed.</summary>
        public bool FailPlays { get; set; }

        public void Play(string? path, int volumePercent, bool loop, TimeSpan startAt = default)
        {
            Plays.Add((path, volumePercent, loop, startAt));
            if (FailPlays)
            {
                PlaybackFailed?.Invoke(this, new MusicPlaybackFailedEventArgs(path ?? string.Empty));
                return;
            }
            IsPlaying = true;
            _changed?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseFailed(string path) => PlaybackFailed?.Invoke(this, new MusicPlaybackFailedEventArgs(path));

        public void RaisePosition(string path, TimeSpan position) =>
            PositionChanged?.Invoke(this, new MusicPositionEventArgs(path, position, null));

        public List<TimeSpan> FadeOuts { get; } = [];

        public void FadeOut(TimeSpan duration) => FadeOuts.Add(duration);

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
        Assert.True(vm.IsDefaultKisscamMusicFolder);
        Assert.Equal(AppInfo.DefaultKisscamMusicFolder, vm.KisscamMusicFolderDisplay);
        Assert.Empty(vm.KisscamSongs);
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
        vm.Draft.Heart.KisscamMusicFolder = Path.Combine(_dir, "Musik");
        _ui.ConfirmResult = true;
        // Ohne Sync-Kontext feuert der nicht abgewartete Seitengrößen-Refresh PropertyChanged auf dem Threadpool nach: Liste sperren.
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => { lock (raised) raised.Add(e.PropertyName); };

        await vm.ResetToDefaultsCommand.ExecuteAsync(null);

        Assert.True(vm.HeartEnabled);
        Assert.Equal(new HeartSettings().KisscamSeconds, vm.KisscamSeconds);
        Assert.Equal(KisscamColor.PinkRed, vm.KisscamColor);
        Assert.Equal("An · Kisscam", vm.HeartStatusText);
        lock (raised)
            Assert.Contains(string.Empty, raised.Select(r => r ?? string.Empty)); // alle Eigenschaften neu gelesen
        Assert.True(vm.HeartActionOptions[0].IsSelected);
    }

    // ------------------------------------------------------------------ Musik: Ordner

    private static KisscamSong Song(string name, bool enabled = false, int start = 0, int volume = 100, TimeSpan? duration = null,
        bool playable = true) => FakeMusicLibrary.Song(name, enabled, start, volume, duration, playable);

    private AdminViewModel CreateViewModelWithSongs(params KisscamSong[] songs)
    {
        _library.Set(songs);
        return CreateViewModel();
    }

    [Fact]
    public void MusicFolder_DefaultsToTheStandardFolder_AndTheLibraryFollowsTheDraft()
    {
        NoContext();
        var vm = CreateViewModel();

        Assert.True(vm.IsDefaultKisscamMusicFolder);
        Assert.False(vm.HasCustomKisscamMusicFolder);
        Assert.Equal(AppInfo.DefaultKisscamMusicFolder, vm.KisscamMusicFolderDisplay);
        Assert.Null(Assert.Single(_library.Folders)); // der Admin lenkt die Bibliothek auf den Ordner des Entwurfs
    }

    [Fact]
    public void ChoosingAFolder_StoresIt_PointsTheLibrary_AndTheDefaultButtonResetsIt()
    {
        NoContext();
        var vm = CreateViewModel();
        var folder = Path.Combine(_dir, "Musik");
        _ui.FolderToPick = folder;
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ChooseKisscamMusicFolderCommand.Execute(null);

        Assert.Equal(folder, vm.Draft.Heart.KisscamMusicFolder);
        Assert.Equal(folder, vm.KisscamMusicFolderDisplay);
        Assert.False(vm.IsDefaultKisscamMusicFolder);
        Assert.True(vm.HasCustomKisscamMusicFolder);
        Assert.Equal(folder, _library.Folders[^1]);
        Assert.True(vm.HasUnsavedChanges);
        Assert.Contains(nameof(AdminViewModel.KisscamMusicFolderDisplay), raised);
        Assert.Contains("Musikordner", _ui.LastPickTitle);
        Assert.Equal(AppInfo.DefaultKisscamMusicFolder, _ui.LastPickInitial);

        _ui.FolderToPick = null; // Abbrechen ändert nichts
        vm.ChooseKisscamMusicFolderCommand.Execute(null);
        Assert.Equal(folder, vm.Draft.Heart.KisscamMusicFolder);

        vm.ResetKisscamMusicFolderCommand.Execute(null);

        Assert.Null(vm.Draft.Heart.KisscamMusicFolder);
        Assert.True(vm.IsDefaultKisscamMusicFolder);
        Assert.Null(_library.Folders[^1]);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void ChoosingAnOverlongFolder_IsRejectedWithAMessage()
    {
        NoContext();
        var vm = CreateViewModel();
        _ui.FolderToPick = "C:\\" + new string('a', SettingsLimits.KisscamMusicFolderMaxLength);

        vm.ChooseKisscamMusicFolderCommand.Execute(null);

        Assert.Null(vm.Draft.Heart.KisscamMusicFolder);
        Assert.NotNull(_ui.LastInfo);
        Assert.Single(_library.Folders);
    }

    [Fact]
    public void Dispose_ResetsTheLibraryToTheSavedFolder_EvenAfterCancelling()
    {
        NoContext();
        _store.Current.Heart.KisscamMusicFolder = Path.Combine(_dir, "Gespeichert");
        var vm = CreateViewModel();
        Assert.Equal(_store.Current.Heart.KisscamMusicFolder, _library.Folders[^1]);
        _ui.FolderToPick = Path.Combine(_dir, "Entwurf");
        vm.ChooseKisscamMusicFolderCommand.Execute(null);
        Assert.Equal(_ui.FolderToPick, _library.Folders[^1]);

        vm.Dispose(); // Abbrechen: Der ungespeicherte Entwurfsordner gilt nicht

        Assert.Equal(_store.Current.Heart.KisscamMusicFolder, _library.Folders[^1]);
    }

    [Fact]
    public async Task OpenFolder_OpensOnlyAnExistingFolder()
    {
        NoContext();
        var vm = CreateViewModel();
        var existing = Path.Combine(_dir, "Musik");
        Directory.CreateDirectory(existing);
        vm.Draft.Heart.KisscamMusicFolder = existing;

        await vm.OpenKisscamMusicFolderCommand.ExecuteAsync(null);

        Assert.Equal(existing, _ui.OpenedFolder);

        vm.Draft.Heart.KisscamMusicFolder = Path.Combine(_dir, "gibt-es-nicht");
        await vm.OpenKisscamMusicFolderCommand.ExecuteAsync(null);

        Assert.Equal(existing, _ui.OpenedFolder); // unverändert
        Assert.Contains("existiert noch nicht", vm.ValidationMessage);
    }

    [Fact]
    public void RefreshButton_ReadsTheFolderAgain()
    {
        NoContext();
        var vm = CreateViewModel();

        vm.RefreshKisscamMusicCommand.Execute(null);

        Assert.Equal(1, _library.Refreshes);
    }

    [Fact]
    public async Task ReplacingTheDraft_PointsTheLibraryAtTheNewFolder()
    {
        NoContext();
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.Draft.Heart.KisscamMusicFolder = Path.Combine(_dir, "Anders");
        _ui.ConfirmResult = true;

        await vm.ResetToDefaultsCommand.ExecuteAsync(null);

        Assert.Null(vm.Draft.Heart.KisscamMusicFolder);
        Assert.Null(_library.Folders[^1]);
    }

    // ------------------------------------------------------------------ Musik: Songliste

    [Fact]
    public void SongRows_FollowTheLibrary_ByFileName_WithoutRecreatingRows()
    {
        NoContext();
        var vm = CreateViewModelWithSongs(Song("a.mp3"), Song("b.mp3"));
        var a = vm.KisscamSongs[0];
        var b = vm.KisscamSongs[1];
        Assert.Equal(["a.mp3", "b.mp3"], vm.KisscamSongs.Select(r => r.FileName));

        // Ein neuer Song, ein geänderter Stand: dieselben Zeilen bleiben erhalten.
        _library.Set(Song("a.mp3", enabled: true, start: 5), Song("b.mp3"), Song("c.mp3"));

        Assert.Equal(3, vm.KisscamSongs.Count);
        Assert.Same(a, vm.KisscamSongs[0]);
        Assert.Same(b, vm.KisscamSongs[1]);
        Assert.True(a.Enabled);
        Assert.Equal(5, a.StartSeconds);

        // Ein Song verschwindet, die Reihenfolge ändert sich.
        _library.Set(Song("a.mp3", enabled: true, start: 5), Song("c.mp3"));
        Assert.Equal(["a.mp3", "c.mp3"], vm.KisscamSongs.Select(r => r.FileName));
        Assert.Same(a, vm.KisscamSongs[0]);

        _library.Set(Song("0.mp3"), Song("a.mp3", enabled: true, start: 5), Song("c.mp3"));
        Assert.Equal(["0.mp3", "a.mp3", "c.mp3"], vm.KisscamSongs.Select(r => r.FileName));
        Assert.Same(a, vm.KisscamSongs[1]);

        _library.Set();
        Assert.Empty(vm.KisscamSongs);
    }

    [Fact]
    public void RowEdits_GoIntoTheLibrary_NotIntoTheDraft_AndAreNoUnsavedChange()
    {
        NoContext();
        var vm = CreateViewModelWithSongs(Song("a.mp3", duration: TimeSpan.FromMinutes(3)));
        var row = vm.KisscamSongs[0];

        row.Enabled = true;
        row.StartLaterCommand.Execute(null);
        row.StartLaterCommand.Execute(null);
        row.StartEarlierCommand.Execute(null);
        row.VolumePercent = 60;

        Assert.Equal(
        [
            ("a.mp3", (bool?)true, (int?)null, (int?)null),
            ("a.mp3", (bool?)null, (int?)1, (int?)null),
            ("a.mp3", (bool?)null, (int?)2, (int?)null),
            ("a.mp3", (bool?)null, (int?)1, (int?)null),
            ("a.mp3", (bool?)null, (int?)null, (int?)60),
        ], _library.Updates);
        Assert.True(row.Enabled);
        Assert.Equal(1, row.StartSeconds);
        Assert.Equal("0:01", row.StartText);
        Assert.Equal(60, row.VolumePercent);
        Assert.False(vm.HasUnsavedChanges); // die Songwerte stehen in der Config, nicht in den Einstellungen
    }

    [Fact]
    public void StartTime_AcceptsMinutesAndSeconds_OrSeconds_AndRejectsGarbage()
    {
        NoContext();
        var vm = CreateViewModelWithSongs(Song("a.mp3", start: 7));
        var row = vm.KisscamSongs[0];
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.StartText = "1:30";
        Assert.Equal(90, row.StartSeconds);
        Assert.Equal("1:30", row.StartText);

        row.StartText = "45";
        Assert.Equal(45, row.StartSeconds);
        Assert.Equal("0:45", row.StartText);

        raised.Clear();
        row.StartText = "abc";
        Assert.Equal(45, row.StartSeconds);
        Assert.Contains(nameof(KisscamSongRowViewModel.StartText), raised); // die Anzeige springt auf den gültigen Wert zurück

        row.StartText = "99999:00";
        Assert.Equal(SettingsLimits.KisscamSongStartSecondsMax, row.StartSeconds);

        row.StartEarlierCommand.Execute(null);
        row.StartText = "0";
        row.StartEarlierCommand.Execute(null);
        Assert.Equal(0, row.StartSeconds); // nie unter 0:00
    }

    [Fact]
    public void Volume_IsClampedIntoTheSongLimits_InSteps()
    {
        NoContext();
        var vm = CreateViewModelWithSongs(Song("a.mp3"));
        var row = vm.KisscamSongs[0];

        row.VolumePercent = 3;
        Assert.Equal(SettingsLimits.KisscamSongVolumeMin, row.VolumePercent);
        row.VolumePercent = 500;
        Assert.Equal(SettingsLimits.KisscamSongVolumeMax, row.VolumePercent);
        row.VolumePercent = 47;
        Assert.Equal(45, row.VolumePercent);
    }

    [Fact]
    public void Warnings_ShowStartBeyondTheEnd_AndNotPlayable_AndTheLength()
    {
        NoContext();
        var vm = CreateViewModelWithSongs(
            Song("kurz.mp3", start: 200, duration: TimeSpan.FromSeconds(125)),
            Song("kaputt.mp3", playable: false),
            Song("ohne.mp3"));
        var kurz = vm.KisscamSongs[0];
        var kaputt = vm.KisscamSongs[1];
        var ohne = vm.KisscamSongs[2];

        Assert.True(kurz.StartBeyondEnd);
        Assert.False(kurz.NotPlayable);
        Assert.Equal("2:05", kurz.DurationText);
        Assert.True(kaputt.NotPlayable);
        Assert.False(kaputt.StartBeyondEnd);
        Assert.Equal("–", ohne.DurationText);
        Assert.Equal("Startzeit liegt hinter dem Songende – es wird ab 0:00 gespielt", KisscamSongRowViewModel.StartBeyondEndText);
        Assert.Equal("Datei lässt sich nicht abspielen", KisscamSongRowViewModel.NotPlayableText);

        kurz.StartText = "0:30"; // vor dem Ende: die Warnung verschwindet
        Assert.False(kurz.StartBeyondEnd);
    }

    [Fact]
    public void Hints_FollowTheLibraryState()
    {
        NoContext();
        var vm = CreateViewModel();

        _library.Set(KisscamLibraryStatus.Ready, false);
        Assert.Equal("Der Ordner ist leer – kopieren Sie Musikdateien (MP3, WAV, M4A, WMA) hinein.", AdminViewModel.KisscamMusicEmptyText);
        Assert.Equal([AdminViewModel.KisscamMusicEmptyText], vm.KisscamMusicHints);
        Assert.True(vm.HasKisscamMusicHints);

        _library.Set(KisscamLibraryStatus.Ready, false, Song("a.mp3"));
        Assert.Equal("Kein Song ausgewählt – die Kisscam läuft ohne Musik.", AdminViewModel.KisscamNoSongSelectedText);
        Assert.Equal([AdminViewModel.KisscamNoSongSelectedText], vm.KisscamMusicHints);

        _library.Set(KisscamLibraryStatus.Ready, false, Song("a.mp3", enabled: true, playable: false));
        Assert.Equal([AdminViewModel.KisscamNoPlayableSongText], vm.KisscamMusicHints);

        _library.Set(KisscamLibraryStatus.Ready, false, Song("a.mp3", enabled: true));
        Assert.Empty(vm.KisscamMusicHints);
        Assert.False(vm.HasKisscamMusicHints);
        Assert.Equal(string.Empty, vm.KisscamMusicHintsText);

        _library.Set(KisscamLibraryStatus.FolderUnavailable, false);
        Assert.Equal([AdminViewModel.KisscamMusicFolderUnavailableText], vm.KisscamMusicHints);

        _library.Set(KisscamLibraryStatus.ConfigBroken, false, Song("a.mp3"));
        Assert.Equal([AdminViewModel.KisscamMusicConfigBrokenText], vm.KisscamMusicHints);
        Assert.Contains("kisscam-musik.json.defekt", AdminViewModel.KisscamMusicConfigBrokenText);

        _library.Set(KisscamLibraryStatus.Ready, true, Song("a.mp3", enabled: true));
        Assert.Equal([AdminViewModel.KisscamMusicSaveFailedText], vm.KisscamMusicHints);
        Assert.Equal(KisscamLibraryStatus.Ready, vm.KisscamMusicStatus);
    }

    [Fact]
    public void LibraryChange_FromAnotherThread_IsMarshalledToTheUiContext()
    {
        var context = new RecordingContext();
        SynchronizationContext.SetSynchronizationContext(context);
        var vm = CreateViewModelWithSongs(Song("a.mp3"));
        context.RunAll();

        var change = Task.Run(() => _library.Set(Song("a.mp3"), Song("b.mp3")));
        Assert.True(SpinWait.SpinUntil(() => change.IsCompleted, TimeSpan.FromSeconds(10)));
        Assert.Single(vm.KisscamSongs); // wartet auf den UI-Kontext
        context.RunAll();

        Assert.Equal(2, vm.KisscamSongs.Count);

        vm.Dispose();
        _library.Set(Song("c.mp3"));
        context.RunAll(); // nach dem Schließen wirkungslos, wirft nicht
        Assert.Equal(2, vm.KisscamSongs.Count);
    }

    // ------------------------------------------------------------------ Musik: Anspielen

    [Fact]
    public void Preview_PlaysFromTheStartTime_WithTheEffectiveVolume_AndFadesOutAfterTheKisscamDuration()
    {
        NoContext();
        var vm = CreateViewModelWithSongs(Song("a.mp3", start: 12, volume: 50, duration: TimeSpan.FromMinutes(5)));
        var row = vm.KisscamSongs[0];
        vm.KisscamMusicVolumePercent = 80;
        vm.KisscamSeconds = 10;
        var baseline = _time.ActiveTimers;
        Assert.Equal("Anspielen", row.PreviewButtonText);

        row.PreviewCommand.Execute(null);

        var play = Assert.Single(_music.Plays);
        Assert.Equal((@"C:\Musik\a.mp3", 40, true, TimeSpan.FromSeconds(12)), play); // 80 % × 50 %
        Assert.True(row.IsPreviewing);
        Assert.Equal("Stopp", row.PreviewButtonText);

        AdvanceSeconds(9);
        Assert.Empty(_music.FadeOuts);
        AdvanceSeconds(1);
        Assert.Equal([TimeSpan.FromMilliseconds(800)], _music.FadeOuts);

        _music.EndPlayback(); // der Player meldet das Ende der Ausblendung
        Assert.False(row.IsPreviewing);
        Assert.Equal("Anspielen", row.PreviewButtonText);
        Assert.Equal(0, _music.StopCalls);
        Assert.Equal(baseline, _time.ActiveTimers);
    }

    [Fact]
    public void Preview_EndsOnItsOwn_EvenWhenThePlayerReportsNoEnd()
    {
        NoContext();
        var vm = CreateViewModelWithSongs(Song("a.mp3"));
        var row = vm.KisscamSongs[0];
        var baseline = _time.ActiveTimers;
        row.PreviewCommand.Execute(null);

        AdvanceSeconds(vm.KisscamSeconds);
        Assert.True(row.IsPreviewing);
        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.False(row.IsPreviewing);
        Assert.Equal(baseline, _time.ActiveTimers);
    }

    [Fact]
    public void Preview_StartBeyondTheEnd_PlaysFromZero()
    {
        NoContext();
        var vm = CreateViewModelWithSongs(Song("a.mp3", start: 200, duration: TimeSpan.FromSeconds(100)));

        vm.KisscamSongs[0].PreviewCommand.Execute(null);

        Assert.Equal(TimeSpan.Zero, Assert.Single(_music.Plays).StartAt);
    }

    [Fact]
    public void Preview_OnlyOneAtATime_AStopButtonEndsIt()
    {
        NoContext();
        var vm = CreateViewModelWithSongs(Song("a.mp3"), Song("b.mp3"));
        var a = vm.KisscamSongs[0];
        var b = vm.KisscamSongs[1];

        a.PreviewCommand.Execute(null);
        Assert.True(a.IsPreviewing);
        b.PreviewCommand.Execute(null);

        Assert.False(a.IsPreviewing);
        Assert.True(b.IsPreviewing);
        Assert.Equal(2, _music.Plays.Count);

        b.PreviewCommand.Execute(null); // „Stopp“
        Assert.False(b.IsPreviewing);
        Assert.Equal(2, _music.StopCalls); // beim Wechsel auf b und beim Stopp
    }

    [Fact]
    public void Preview_WithoutAPlayer_DoesNothing()
    {
        NoContext();
        _library.Set(Song("a.mp3"));
        var vm = CreateViewModel(withMusic: false);

        vm.KisscamSongs[0].PreviewCommand.Execute(null);

        Assert.False(vm.KisscamSongs[0].IsPreviewing);
        Assert.Empty(_music.Plays);
    }

    [Fact]
    public void TakePosition_UsesTheLastReportedPosition_AsTheStartTime()
    {
        NoContext();
        var vm = CreateViewModelWithSongs(Song("a.mp3", start: 3), Song("b.mp3"));
        var row = vm.KisscamSongs[0];
        row.PreviewCommand.Execute(null);

        _music.RaisePosition(@"C:\Musik\b.mp3", TimeSpan.FromSeconds(99)); // anderer Song: ignoriert
        Assert.Equal(string.Empty, row.PositionText);
        _music.RaisePosition(@"C:\Musik\a.mp3", TimeSpan.FromSeconds(41.8));
        Assert.Equal("0:41", row.PositionText);
        _music.RaisePosition(@"C:\Musik\a.mp3", TimeSpan.FromSeconds(72.2));
        row.TakePositionCommand.Execute(null);

        Assert.Equal(72, row.StartSeconds);
        Assert.Equal("1:12", row.StartText);
        Assert.Equal(72, _library.Updates[^1].Start);
        Assert.True(row.IsPreviewing); // das Anspielen läuft weiter
        Assert.Equal(0, _music.StopCalls);

        // Ohne Anspielen übernimmt der Knopf nichts.
        var other = vm.KisscamSongs[1];
        other.TakePositionCommand.Execute(null);
        Assert.Equal(0, other.StartSeconds);
    }

    [Fact]
    public void PositionText_IsClearedWhenThePreviewEnds()
    {
        NoContext();
        var vm = CreateViewModelWithSongs(Song("a.mp3"));
        var row = vm.KisscamSongs[0];
        row.PreviewCommand.Execute(null);
        _music.RaisePosition(@"C:\Musik\a.mp3", TimeSpan.FromSeconds(5));
        Assert.Equal("0:05", row.PositionText);

        row.PreviewCommand.Execute(null);

        Assert.Equal(string.Empty, row.PositionText);
    }

    [Fact]
    public void Preview_Stops_OnFolderChange_ReplacedDraft_HeartSwitchOff_AndRemovedSong()
    {
        NoContext();
        var vm = CreateViewModelWithSongs(Song("a.mp3"), Song("b.mp3"));
        vm.KisscamSongs[0].PreviewCommand.Execute(null);
        _ui.FolderToPick = Path.Combine(_dir, "Neu");
        vm.ChooseKisscamMusicFolderCommand.Execute(null);
        Assert.False(vm.KisscamSongs[0].IsPreviewing);
        Assert.Equal(1, _music.StopCalls);

        vm.KisscamSongs[0].PreviewCommand.Execute(null);
        vm.HeartEnabled = false;
        Assert.Equal(2, _music.StopCalls);

        vm.HeartEnabled = true;
        vm.KisscamSongs[1].PreviewCommand.Execute(null);
        _library.Set(Song("a.mp3")); // der angespielte Song verschwindet aus dem Ordner
        Assert.Equal(3, _music.StopCalls);
        Assert.Single(vm.KisscamSongs);
    }

    [Fact]
    public void Preview_Stops_OnPageChangeTabChangeAndDispose()
    {
        NoContext();
        _library.Set(Song("a.mp3"));
        var vm = CreateViewModel();
        vm.IsFunTabSelected = true;
        vm.FunPage = FunPage.Heart;
        var row = vm.KisscamSongs[0];
        row.PreviewCommand.Execute(null);
        Assert.True(row.IsPreviewing); // auf der Seite „Herz“ läuft sie weiter

        vm.FunPage = FunPage.Start;
        Assert.False(row.IsPreviewing);
        Assert.Equal(1, _music.StopCalls);

        vm.FunPage = FunPage.Heart;
        row.PreviewCommand.Execute(null);
        Assert.True(row.IsPreviewing);
        vm.IsFunTabSelected = false;
        Assert.False(row.IsPreviewing);
        Assert.Equal(2, _music.StopCalls);

        vm.IsFunTabSelected = true;
        row.PreviewCommand.Execute(null);
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
        var vm = CreateViewModelWithSongs(Song("a.mp3"));
        vm.IsFunTabSelected = true;
        vm.FunPage = FunPage.Heart;
        vm.FunPage = FunPage.Start;
        vm.IsFunTabSelected = false;
        vm.Dispose();

        Assert.Equal(0, _music.StopCalls); // der Player gehört auch der Gastansicht
    }

    [Fact]
    public void Preview_FailedStart_ResetsTheButton_AndLeavesTheMarkingToTheLibrary()
    {
        NoContext();
        var vm = CreateViewModelWithSongs(Song("a.mp3"));
        var row = vm.KisscamSongs[0];
        _music.FailPlays = true;

        row.PreviewCommand.Execute(null);

        Assert.False(row.IsPreviewing);
        Assert.Equal("Anspielen", row.PreviewButtonText);
        Assert.Empty(_library.Unplayable); // das tut die echte Bibliothek selbst über PlaybackFailed

        // Ein erneuter, diesmal erfolgreicher Versuch klappt.
        _music.FailPlays = false;
        row.PreviewCommand.Execute(null);
        Assert.True(row.IsPreviewing);
    }

    [Fact]
    public void Preview_FailureAfterTheAsyncPost_ResetsTheButton()
    {
        var context = new RecordingContext();
        SynchronizationContext.SetSynchronizationContext(context);
        var vm = CreateViewModelWithSongs(Song("a.mp3"));
        var row = vm.KisscamSongs[0];
        row.PreviewCommand.Execute(null);
        Assert.True(row.IsPreviewing); // der Player meldet den Fehlschlag erst später (Musik-Thread)

        // Kein „await“: Es würde den aufzeichnenden Kontext des Tests erfassen und dort nie fortgesetzt.
        var raise = Task.Run(() => _music.RaiseFailed(@"C:\Musik\a.mp3"));
        Assert.True(SpinWait.SpinUntil(() => raise.IsCompleted, TimeSpan.FromSeconds(10)));
        Assert.True(row.IsPreviewing); // wartet auf den UI-Kontext
        context.RunAll();

        Assert.False(row.IsPreviewing);
    }

    [Fact]
    public void PlaybackFailed_WhenThisViewDidNotStartTheMusic_IsIgnored()
    {
        NoContext();
        var vm = CreateViewModelWithSongs(Song("a.mp3"));
        var row = vm.KisscamSongs[0];
        row.PreviewCommand.Execute(null);

        _music.RaiseFailed(@"C:\Musik\anderer-song.mp3"); // der Player gehört auch der Gastansicht

        Assert.True(row.IsPreviewing);
    }

    [Fact]
    public void Preview_Ends_WhenThePlaybackEndsByItself()
    {
        NoContext();
        var vm = CreateViewModelWithSongs(Song("a.mp3"));
        var row = vm.KisscamSongs[0];
        row.PreviewCommand.Execute(null);

        _music.EndPlayback();

        Assert.False(row.IsPreviewing);
        Assert.Equal(0, _music.StopCalls);
    }

    [Fact]
    public async Task PlaybackChanged_FromAnotherThread_IsMarshalledToTheUiContext()
    {
        var context = new RecordingContext();
        SynchronizationContext.SetSynchronizationContext(context);
        var vm = CreateViewModelWithSongs(Song("a.mp3"));
        var row = vm.KisscamSongs[0];
        row.PreviewCommand.Execute(null);
        context.RunAll();
        Assert.True(row.IsPreviewing);

        _music.SetPlayingSilently(false);
        await RaiseMusicChangedOnAnotherThreadAsync();

        Assert.True(row.IsPreviewing); // noch nicht: das Ereignis wartet auf den UI-Kontext
        context.RunAll();
        Assert.False(row.IsPreviewing);

        vm.Dispose();
        _music.SetPlayingSilently(true);
        await RaiseMusicChangedOnAnotherThreadAsync();
        context.RunAll(); // nach dem Schließen wirkungslos, wirft nicht
    }

    // Löst das Ereignis auf dem Threadpool aus. Gewartet wird per Schleife und erst dann „await“ auf die fertige Aufgabe: Ein echtes
    // await würde den aufzeichnenden Kontext des Tests erfassen und dort nie fortgesetzt.
    private async Task RaiseMusicChangedOnAnotherThreadAsync()
    {
        var raise = Task.Run(() => _music.RaiseChanged());
        Assert.True(SpinWait.SpinUntil(() => raise.IsCompleted, TimeSpan.FromSeconds(10)));
        await raise;
    }

    [Fact]
    public async Task PreviewState_DoesNotCountAsAnUnsavedChange_AndKeepsTheSavedMessage()
    {
        NoContext();
        var vm = CreateViewModelWithSongs(Song("a.mp3"));
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.False(string.IsNullOrEmpty(vm.SaveStatusMessage));
        Assert.False(vm.HasUnsavedChanges);

        vm.KisscamSongs[0].PreviewCommand.Execute(null);
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(string.IsNullOrEmpty(vm.SaveStatusMessage));
        _library.Set(KisscamLibraryStatus.Ready, false, Song("a.mp3", enabled: true), Song("b.mp3"));
        Assert.False(string.IsNullOrEmpty(vm.SaveStatusMessage));
        vm.KisscamSongs[0].PreviewCommand.Execute(null);
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(string.IsNullOrEmpty(vm.SaveStatusMessage));
    }

    [Fact]
    public void TimerHint_AppearsOnlyForThreeSecondsOrLess_AndIsNoSettingsChange()
    {
        NoContext();
        var vm = CreateViewModel();
        Assert.False(vm.HasKisscamOnlyCountdownHint);
        Assert.Equal("Bei 3 Sekunden erscheint nur der Countdown 3 – 2 – 1, kein Text.", AdminViewModel.KisscamOnlyCountdownHintText);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.KisscamSeconds = SettingsLimits.KisscamFinalDigitsSeconds;
        Assert.True(vm.HasKisscamOnlyCountdownHint);
        Assert.Contains(nameof(AdminViewModel.HasKisscamOnlyCountdownHint), raised);

        vm.KisscamSeconds = SettingsLimits.KisscamFinalDigitsSeconds + 1;
        Assert.False(vm.HasKisscamOnlyCountdownHint);

        vm.KisscamSeconds = SettingsLimits.KisscamSecondsMin;
        vm.KisscamSeconds = new HeartSettings().KisscamSeconds;
        Assert.False(vm.HasUnsavedChanges);
    }

    [Theory]
    [InlineData(nameof(AdminViewModel.HasKisscamOnlyCountdownHint))]
    [InlineData(nameof(AdminViewModel.KisscamMusicStatus))]
    [InlineData(nameof(AdminViewModel.KisscamMusicHints))]
    [InlineData(nameof(AdminViewModel.KisscamMusicHintsText))]
    [InlineData(nameof(AdminViewModel.HasKisscamMusicHints))]
    public void HintProperties_AreViewState_NotSettings(string name)
    {
        var method = typeof(AdminViewModel).GetMethod("IsHeartViewStateProperty",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.True((bool)method.Invoke(null, [name])!);
    }

    // ------------------------------------------------------------------ Vorschau-Verhalten

    private static readonly IReadOnlyList<HandLandmarks> ClassicHeart = HeartHandFixtures.Classic(cx: 0.5, cy: 0.6);
    private static readonly IReadOnlyList<HandLandmarks> RoofHeart = HeartHandFixtures.Roof(cx: 0.5, cy: 0.6);
    private static readonly HandLandmarks Middle = PeaceHandFixtures.Hand(HandPose.MiddleOnly);
    private static readonly HandLandmarks PeaceHand = PeaceHandFixtures.Hand(HandPose.Peace);
    private static readonly FaceLandmarks Face = PeaceHandFixtures.Face(0.5, 0.3, 0.12);

    // Timer der Erkennung selbst (laufen, solange die Vorschau läuft); der Kisscam-Test darf darüber hinaus keine übrig lassen.
    private int _timerBaseline;

    private AdminViewModel OpenHeartPage(bool withMusic = true)
    {
        var vm = CreateViewModel(withMusic, withServices: true);
        vm.IsFunTabSelected = true;
        vm.FunPage = FunPage.Heart;
        _timerBaseline = _time.ActiveTimers;
        return vm;
    }

    // Eine Analyse alle 100 ms, die Zeit läuft mit (auch die des Kisscam-Tests).
    private void Run(double seconds, IReadOnlyList<HandLandmarks> hands)
    {
        var steps = (int)Math.Round(seconds * 10);
        for (var i = 0; i < steps; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            if (_gestures.Requests.Count > 0 && !_gestures.Last.IsDisposed)
                _gestures.Last.RaiseUpdated([Face], hands);
        }
    }

    // Hält das Herz, bis der Test läuft (höchstens 5 s); gibt zurück, ob er lief.
    private bool HoldHeartUntilTestRuns(AdminViewModel vm)
    {
        for (var i = 0; i < 50 && !vm.KisscamTestRunning; i++)
            Run(0.1, ClassicHeart);
        return vm.KisscamTestRunning;
    }

    private void AdvanceSeconds(double seconds) => _time.Advance(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void HeartPage_StartsThePreview_WithTheHeartDraft_AndWithoutPeaceGestures()
    {
        NoContext();
        var vm = CreateViewModel(withServices: true);
        vm.IsFunTabSelected = true;
        vm.FunPage = FunPage.Peace;
        Assert.Equal(PeaceMode.Watching, vm.PeacePreview.Mode);
        Assert.Equal(1, _gestures.OpenRequests);
        Assert.False(_gestures.Last.Options.PairedHands);

        vm.HeartEnabled = false; // die Vorschau zeigt die Erkennung trotzdem
        vm.FunPage = FunPage.Heart;

        Assert.Equal(PeaceMode.Watching, vm.PeacePreview.Mode);
        Assert.Equal(1, _gestures.OpenRequests);
        Assert.True(_gestures.Last.Options.PairedHands);
        Assert.False(vm.FunPreview.IsActive);

        Run(0.5, ClassicHeart);
        Assert.True(vm.PeacePreview.HeartDetectedNow);
        Assert.Equal(HeartShape.Classic, vm.PeacePreview.HeartDetectedShape);

        vm.FunPage = FunPage.MiddleFinger; // dort keine Herzerkennung
        Assert.Equal(PeaceMode.Watching, vm.PeacePreview.Mode);
        Assert.False(_gestures.Last.Options.PairedHands);

        vm.FunPage = FunPage.Start;
        Assert.Equal(PeaceMode.Off, vm.PeacePreview.Mode);
        Assert.Equal(0, _gestures.OpenRequests);
    }

    [Fact]
    public void HeartPage_PeaceGestures_AreOff_SoPeaceDoesNothingThere()
    {
        NoContext();
        var vm = OpenHeartPage();

        Run(1.6, [PeaceHand]);

        Assert.False(vm.PeaceTestSelfDestructVisible);
        Assert.False(vm.PeacePhotoBadgeVisible);
        Assert.False(vm.KisscamTestRunning);
    }

    [Fact]
    public void ChangedHeartSettings_UpdateThePreview()
    {
        NoContext();
        var vm = OpenHeartPage();
        Run(0.5, RoofHeart);
        Assert.True(vm.PeacePreview.HeartDetectedNow);
        Assert.Equal(HeartShape.Roof, vm.PeacePreview.HeartDetectedShape);
        Run(1.0, []);
        Assert.False(vm.PeacePreview.HeartDetectedNow);

        vm.HeartShapeRoof = false; // gilt sofort, ohne Neustart der Anforderung

        Assert.Single(_gestures.Requests);
        Run(0.5, RoofHeart);
        Assert.False(vm.PeacePreview.HeartDetectedNow);
        Run(0.5, ClassicHeart);
        Assert.True(vm.PeacePreview.HeartDetectedNow);
    }

    [Fact]
    public void DisplayOnlyHeartProperties_DoNotRestartThePreview()
    {
        NoContext();
        var vm = OpenHeartPage();
        var requests = _gestures.Requests.Count;
        var updates = _gestures.Last.Updates.Count;

        vm.KisscamSeconds = 20;
        vm.KisscamText = "Hallo";
        vm.KisscamColor = KisscamColor.Gold;
        vm.HeartShowHoldFeedback = false;

        Assert.Equal(PeaceMode.Watching, vm.PeacePreview.Mode);
        Assert.Equal(requests, _gestures.Requests.Count);
        Assert.Equal(updates, _gestures.Last.Updates.Count);
    }

    [Fact]
    public void LeavingThePageOrTheTab_StopsThePreview_AndTheTest()
    {
        NoContext();
        var vm = OpenHeartPage();
        vm.ToggleKisscamTestCommand.Execute(null);
        Assert.True(vm.KisscamTestRunning);

        vm.FunPage = FunPage.Start;
        Assert.False(vm.KisscamTestRunning);
        Assert.Equal(PeaceMode.Off, vm.PeacePreview.Mode);
        Assert.Equal(0, _gestures.OpenRequests);
        Assert.Equal(0, _time.ActiveTimers);

        vm.FunPage = FunPage.Heart;
        vm.ToggleKisscamTestCommand.Execute(null);
        Assert.True(vm.KisscamTestRunning);
        vm.IsFunTabSelected = false;
        Assert.False(vm.KisscamTestRunning);
        Assert.Equal(PeaceMode.Off, vm.PeacePreview.Mode);
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public void LeavingTheHeartPageForAnotherPreviewPage_StopsTheTest()
    {
        NoContext();
        var vm = OpenHeartPage();
        vm.ToggleKisscamTestCommand.Execute(null);

        vm.FunPage = FunPage.MiddleFinger;

        Assert.False(vm.KisscamTestRunning);
        Assert.False(vm.PeacePreview.MiddleFingerWatching);
        Assert.Equal(_timerBaseline, _time.ActiveTimers);
    }

    // ------------------------------------------------------------------ Kisscam-Test

    [Fact]
    public void HeartGesture_InThePreview_StartsTheTest_AndNeverAPhoto()
    {
        NoContext();
        var vm = OpenHeartPage();
        var triggers = 0;
        vm.PeacePreview.HeartTriggered += (_, _) => triggers++;

        Assert.False(vm.KisscamTestRunning);
        Assert.True(HoldHeartUntilTestRuns(vm));

        Assert.Equal(1, triggers);
        Assert.InRange(vm.KisscamTestSeconds, 1, vm.KisscamSeconds);
        Assert.NotNull(vm.KisscamTestFrame);
        Assert.Equal(PhotoFrameKind.Kisscam, vm.KisscamTestFrame!.Kind);
        Assert.Equal("Test abbrechen", vm.KisscamTestButtonText);
        Assert.Equal(0, _gestures.PhotoDetectCalls); // nie ein Foto
        Assert.Empty(_music.Plays);                   // ohne gewählte Datei bleibt es still
    }

    [Fact]
    public void HeartGesture_OnAnotherPage_DoesNotStartTheTest()
    {
        NoContext();
        var vm = OpenHeartPage();
        vm.FunPage = FunPage.MiddleFinger;

        Run(3.0, ClassicHeart);

        Assert.False(vm.KisscamTestRunning);
    }

    [Fact]
    public void Button_StartsWithoutAHeart_AndCancels()
    {
        NoContext();
        var vm = OpenHeartPage();
        Assert.Equal("Kisscam testen", vm.KisscamTestButtonText);
        Assert.True(vm.ToggleKisscamTestCommand.CanExecute(null));

        vm.ToggleKisscamTestCommand.Execute(null);

        Assert.True(vm.KisscamTestRunning);
        Assert.Equal(vm.KisscamSeconds, vm.KisscamTestSeconds);
        Assert.Equal("Test abbrechen", vm.KisscamTestButtonText);
        Assert.Equal(_timerBaseline + 1, _time.ActiveTimers);

        vm.ToggleKisscamTestCommand.Execute(null);

        Assert.False(vm.KisscamTestRunning);
        Assert.Null(vm.KisscamTestFrame);
        Assert.Equal("Kisscam testen", vm.KisscamTestButtonText);
        Assert.Equal(_timerBaseline, _time.ActiveTimers);
    }

    [Fact]
    public void Button_OnAnotherPage_StartsNothing()
    {
        NoContext();
        var vm = CreateViewModel(withServices: true);
        vm.IsFunTabSelected = true;
        vm.FunPage = FunPage.Peace;

        vm.ToggleKisscamTestCommand.Execute(null);

        Assert.False(vm.KisscamTestRunning);
    }

    [Fact]
    public void Test_Runs_TextThenDigitsThenBadge_ThenEnds()
    {
        NoContext();
        var vm = OpenHeartPage();
        vm.KisscamSeconds = 5;
        vm.KisscamText = "Noch {Sekunden} s";
        vm.KisscamBannerText = "UNSER BANNER";
        vm.KisscamColor = KisscamColor.Gold;

        vm.ToggleKisscamTestCommand.Execute(null);

        Assert.Equal(5, vm.KisscamTestSeconds);
        Assert.Equal("Noch 5 s", vm.KisscamTestText);
        Assert.True(vm.KisscamTestShowText);
        Assert.False(vm.KisscamTestShowDigits);
        Assert.Equal(string.Empty, vm.KisscamTestDigits);
        Assert.Equal(new PhotoFrame(PhotoFrameKind.Kisscam, "UNSER BANNER", KisscamColor.Gold), vm.KisscamTestFrame);
        Assert.False(vm.KisscamTestPhotoBadgeVisible);

        AdvanceSeconds(1); // 4
        Assert.Equal(4, vm.KisscamTestSeconds);
        Assert.Equal("Noch 4 s", vm.KisscamTestText);
        Assert.True(vm.KisscamTestShowText);

        AdvanceSeconds(1); // 3: ab hier die Ziffern
        Assert.False(vm.KisscamTestShowText);
        Assert.True(vm.KisscamTestShowDigits);
        Assert.Equal("3", vm.KisscamTestDigits);

        AdvanceSeconds(1);
        Assert.Equal("2", vm.KisscamTestDigits);
        AdvanceSeconds(1);
        Assert.Equal("1", vm.KisscamTestDigits);
        Assert.True(vm.KisscamTestRunning);

        AdvanceSeconds(1); // 0: Auslösemoment, nur das Abzeichen
        Assert.True(vm.KisscamTestRunning);
        Assert.Equal(0, vm.KisscamTestSeconds);
        Assert.True(vm.KisscamTestPhotoBadgeVisible);
        Assert.False(vm.KisscamTestShowDigits);
        Assert.False(vm.KisscamTestShowText);
        Assert.NotNull(vm.KisscamTestFrame);
        Assert.Equal(_timerBaseline + 1, _time.ActiveTimers); // nur noch die Abzeichen-Frist

        _time.Advance(TimeSpan.FromMilliseconds(1400));
        Assert.True(vm.KisscamTestRunning);
        _time.Advance(TimeSpan.FromMilliseconds(200));

        Assert.False(vm.KisscamTestRunning);
        Assert.False(vm.KisscamTestPhotoBadgeVisible);
        Assert.Null(vm.KisscamTestFrame);
        Assert.Equal(_timerBaseline, _time.ActiveTimers);
    }

    [Fact]
    public void Test_WithAShortTimer_ShowsDigitsFromTheStart()
    {
        NoContext();
        var vm = OpenHeartPage();
        vm.KisscamSeconds = SettingsLimits.KisscamSecondsMin; // = 3

        vm.ToggleKisscamTestCommand.Execute(null);

        Assert.False(vm.KisscamTestShowText);
        Assert.True(vm.KisscamTestShowDigits);
        Assert.Equal("3", vm.KisscamTestDigits);
    }

    [Fact]
    public void Test_UsesTheDefaultTexts_WhenTheDraftHasNone()
    {
        NoContext();
        var vm = OpenHeartPage();

        vm.ToggleKisscamTestCommand.Execute(null);

        Assert.Equal(HeartSettings.DefaultKisscamBannerText, vm.KisscamTestFrame!.BannerText);
        Assert.Contains(vm.KisscamSeconds.ToString(), vm.KisscamTestText);
    }

    [Fact]
    public void Test_FollowsTextAndFrameChanges_MadeWhileRunning()
    {
        NoContext();
        var vm = OpenHeartPage();
        vm.ToggleKisscamTestCommand.Execute(null);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.KisscamText = "Neu {Sekunden}";
        vm.KisscamBannerText = "NEU";
        vm.KisscamColor = KisscamColor.Purple;

        Assert.Equal($"Neu {vm.KisscamSeconds}", vm.KisscamTestText);
        Assert.Equal(new PhotoFrame(PhotoFrameKind.Kisscam, "NEU", KisscamColor.Purple), vm.KisscamTestFrame);
        Assert.Contains(nameof(AdminViewModel.KisscamTestText), changed);
        Assert.Contains(nameof(AdminViewModel.KisscamTestFrame), changed);
    }

    [Fact]
    public void MiddleFinger_EndsTheTest_WhenTheActionIsOn()
    {
        NoContext();
        var vm = OpenHeartPage();
        vm.ToggleKisscamTestCommand.Execute(null);
        Assert.True(vm.PeacePreview.MiddleFingerWatching); // der Test ist ein aufhebbares Ziel

        Run(0.5, []);
        Run(0.7, [Middle]);

        Assert.False(vm.KisscamTestRunning);
        Assert.False(vm.PeacePreview.MiddleFingerWatching);
        Assert.Equal(_timerBaseline, _time.ActiveTimers);
    }

    [Fact]
    public void MiddleFinger_LeavesTheTest_WhenTheActionIsOff()
    {
        NoContext();
        var vm = OpenHeartPage();
        vm.MiddleFingerActionCancelKisscam = false;
        vm.ToggleKisscamTestCommand.Execute(null);
        Assert.False(vm.PeacePreview.MiddleFingerWatching);

        Run(0.5, []);
        Run(0.9, [Middle]);

        Assert.True(vm.KisscamTestRunning);
    }

    [Fact]
    public void HeartSwitchOff_EndsTheTest()
    {
        NoContext();
        var vm = OpenHeartPage();
        vm.ToggleKisscamTestCommand.Execute(null);

        vm.HeartEnabled = false;

        Assert.False(vm.KisscamTestRunning);
        Assert.Equal(_timerBaseline, _time.ActiveTimers);
    }

    [Fact]
    public void Dispose_EndsTheTest_AndReleasesEverything()
    {
        NoContext();
        var vm = OpenHeartPage();
        vm.ToggleKisscamTestCommand.Execute(null);

        vm.Dispose();

        Assert.False(vm.KisscamTestRunning);
        Assert.Equal(0, _time.ActiveTimers);
        Assert.Equal(0, _gestures.OpenRequests);
        Run(2.0, ClassicHeart); // nach dem Schließen wirkungslos
        Assert.False(vm.KisscamTestRunning);
        vm.ToggleKisscamTestCommand.Execute(null);
        Assert.False(vm.KisscamTestRunning);
    }

    [Fact]
    public async Task ReplacingTheDraft_EndsTheTest()
    {
        NoContext();
        var vm = OpenHeartPage();
        vm.ToggleKisscamTestCommand.Execute(null);
        _ui.ConfirmResult = true;

        await vm.ResetToDefaultsCommand.ExecuteAsync(null);

        Assert.False(vm.KisscamTestRunning);
        Assert.Equal(PeaceMode.Watching, vm.PeacePreview.Mode); // die Vorschau selbst läuft weiter
    }

    [Fact]
    public async Task TestState_IsNoSettingsChange_AndKeepsTheSavedMessage()
    {
        NoContext();
        var vm = OpenHeartPage();
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.False(string.IsNullOrEmpty(vm.SaveStatusMessage));
        Assert.False(vm.HasUnsavedChanges);

        vm.ToggleKisscamTestCommand.Execute(null);
        AdvanceSeconds(1);
        Run(0.3, ClassicHeart); // Statuszeile und Abzeichen ändern sich
        vm.ToggleKisscamTestCommand.Execute(null);

        Assert.False(vm.HasUnsavedChanges);
        Assert.False(string.IsNullOrEmpty(vm.SaveStatusMessage));
    }

    [Theory]
    [InlineData(nameof(AdminViewModel.KisscamTestRunning))]
    [InlineData(nameof(AdminViewModel.KisscamTestSeconds))]
    [InlineData(nameof(AdminViewModel.KisscamTestText))]
    [InlineData(nameof(AdminViewModel.KisscamTestShowText))]
    [InlineData(nameof(AdminViewModel.KisscamTestShowDigits))]
    [InlineData(nameof(AdminViewModel.KisscamTestDigits))]
    [InlineData(nameof(AdminViewModel.KisscamTestFrame))]
    [InlineData(nameof(AdminViewModel.KisscamTestPhotoBadgeVisible))]
    [InlineData(nameof(AdminViewModel.KisscamTestButtonText))]
    [InlineData(nameof(AdminViewModel.HeartPreviewStatusText))]
    [InlineData(nameof(AdminViewModel.HeartPreviewWarningText))]
    [InlineData(nameof(AdminViewModel.HasHeartPreviewWarning))]
    [InlineData(nameof(AdminViewModel.HeartPreviewBadgeText))]
    public void TestAndPreviewProperties_AreViewState_NotSettings(string name)
    {
        var method = typeof(AdminViewModel).GetMethod("IsHeartViewStateProperty",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.True((bool)method.Invoke(null, [name])!);
        // … und keine davon ist eine Vorschau-Einstellung, die die Erkennung neu konfiguriert
        var affects = typeof(AdminViewModel).GetMethod("AffectsPeacePreview",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.False((bool)affects.Invoke(null, [name])!);
    }

    // ---- Musik im Test

    private AdminViewModel OpenHeartPageWithMusic(params KisscamSong[] songs)
    {
        _library.Set(songs.Length > 0 ? songs : [Song("kiss.mp3", enabled: true, start: 7, volume: 50)]);
        return OpenHeartPage();
    }

    [Fact]
    public void Music_PlaysAtTheStart_FadesOutAtTheEnd_AndIsNotStoppedBrutally()
    {
        NoContext();
        var vm = OpenHeartPageWithMusic();
        vm.KisscamSeconds = 4;
        vm.KisscamMusicVolumePercent = 70;

        vm.ToggleKisscamTestCommand.Execute(null);

        var play = Assert.Single(_music.Plays);
        Assert.Equal(@"C:\Musik\kiss.mp3", play.Path);
        Assert.Equal(35, play.Volume); // 70 % × 50 %
        Assert.True(play.Loop);
        Assert.Equal(TimeSpan.FromSeconds(7), play.StartAt);

        AdvanceSeconds(3);
        Assert.Empty(_music.FadeOuts);
        AdvanceSeconds(1); // Auslösemoment
        Assert.Equal([TimeSpan.FromMilliseconds(800)], _music.FadeOuts);

        _time.Advance(TimeSpan.FromMilliseconds(1600));
        Assert.False(vm.KisscamTestRunning);
        Assert.Equal(0, _music.StopCalls);
    }

    [Fact]
    public void Music_Test_PicksRandomlyAmongTheEnabledPlayableSongs()
    {
        NoContext();
        var vm = OpenHeartPageWithMusic(
            Song("a.mp3", enabled: true), Song("b.mp3"), Song("c.mp3", enabled: true), Song("d.mp3", enabled: true, playable: false));

        vm.KisscamRandom = new FixedRandom(1); // zweiter Kandidat: c.mp3 (b ist nicht angehakt, d nicht abspielbar)
        vm.ToggleKisscamTestCommand.Execute(null);
        Assert.Equal(@"C:\Musik\c.mp3", Assert.Single(_music.Plays).Path);

        vm.ToggleKisscamTestCommand.Execute(null);
        vm.KisscamRandom = new FixedRandom(0);
        vm.ToggleKisscamTestCommand.Execute(null);
        Assert.Equal(@"C:\Musik\a.mp3", _music.Plays[1].Path); // Wiederholung eines früheren Songs ist erlaubt
    }

    [Fact]
    public void Music_IsStopped_WhenTheTestIsAborted()
    {
        NoContext();
        var vm = OpenHeartPageWithMusic();
        vm.ToggleKisscamTestCommand.Execute(null);
        Assert.Single(_music.Plays);

        vm.ToggleKisscamTestCommand.Execute(null);

        Assert.Equal(1, _music.StopCalls);
        Assert.Empty(_music.FadeOuts);
        Assert.False(_music.IsPlaying);
    }

    [Fact]
    public void Music_IsStopped_WhenThePageIsLeft_OrTheWindowClosed()
    {
        NoContext();
        var vm = OpenHeartPageWithMusic();
        vm.ToggleKisscamTestCommand.Execute(null);
        vm.FunPage = FunPage.Start;
        Assert.Equal(1, _music.StopCalls);

        vm.FunPage = FunPage.Heart;
        vm.ToggleKisscamTestCommand.Execute(null);
        vm.Dispose();
        Assert.Equal(2, _music.StopCalls);
    }

    [Fact]
    public void Music_IsStopped_WhenAMiddleFingerEndsTheTest()
    {
        NoContext();
        var vm = OpenHeartPageWithMusic();
        vm.ToggleKisscamTestCommand.Execute(null);

        Run(0.5, []);
        Run(0.7, [Middle]);

        Assert.False(vm.KisscamTestRunning);
        Assert.Equal(1, _music.StopCalls);
    }

    [Fact]
    public void Test_StopsASongPreview_BeforePlayingItself()
    {
        NoContext();
        var vm = OpenHeartPageWithMusic();
        var row = vm.KisscamSongs[0];
        row.PreviewCommand.Execute(null);
        Assert.True(row.IsPreviewing);

        vm.ToggleKisscamTestCommand.Execute(null);

        Assert.False(row.IsPreviewing);
        Assert.Equal(2, _music.Plays.Count); // Anspielen, dann der Test
        Assert.True(_music.IsPlaying);
    }

    [Fact]
    public void Music_WithoutAnEnabledSong_StaysSilent_AndAbortDoesNotStopTheSharedPlayer()
    {
        NoContext();
        var vm = OpenHeartPageWithMusic(Song("a.mp3"), Song("b.mp3", enabled: true, playable: false));
        vm.ToggleKisscamTestCommand.Execute(null);
        Assert.True(vm.KisscamTestRunning); // der Test läuft trotzdem
        vm.ToggleKisscamTestCommand.Execute(null);

        Assert.Empty(_music.Plays);
        Assert.Equal(0, _music.StopCalls);
    }

    [Fact]
    public void Music_FailedPlay_InTheTest_LetsTheTestKeepRunning()
    {
        NoContext();
        var vm = OpenHeartPageWithMusic();
        _music.FailPlays = true;

        vm.ToggleKisscamTestCommand.Execute(null);

        Assert.True(vm.KisscamTestRunning);
        vm.ToggleKisscamTestCommand.Execute(null);
        Assert.Equal(0, _music.StopCalls); // der Test hat keine Musik mehr zu stoppen
    }

    // ---- Statuszeile

    [Fact]
    public void StatusLine_ShowsTheHeartAndItsShape_WhileOneIsDetected()
    {
        NoContext();
        var vm = OpenHeartPage();
        _gestures.Last.RaiseStatus(new GestureStatus(true, true, null, null, 10));
        Assert.DoesNotContain("Herz erkannt", vm.HeartPreviewStatusText);
        Assert.Equal(string.Empty, vm.HeartPreviewBadgeText);

        Run(0.5, ClassicHeart);

        Assert.Contains("· Herz erkannt ❤", vm.HeartPreviewStatusText);
        Assert.Equal("Herz erkannt ❤ (Klassisch)", vm.HeartPreviewBadgeText);

        vm.FunPage = FunPage.Start;
        Assert.Equal(string.Empty, vm.HeartPreviewStatusText);
        Assert.Equal(string.Empty, vm.HeartPreviewBadgeText);
    }

    [Theory]
    [InlineData(HeartShape.Classic, "Herz erkannt ❤ (Klassisch)")]
    [InlineData(HeartShape.Roof, "Herz erkannt ❤ (Dach)")]
    [InlineData(HeartShape.Inverted, "Herz erkannt ❤ (Umgedreht)")]
    [InlineData(HeartShape.FingerHeart, "Herz erkannt ❤ (Fingerherz)")]
    [InlineData(null, "Herz erkannt ❤")]
    public void Formatter_NamesTheHeartShape(HeartShape? shape, string expected) =>
        Assert.Equal(expected, PeacePreviewStatusFormatter.HeartBadge(shape));

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

    [StaTheory]
    [InlineData(AdminThemeMode.Light)]
    [InlineData(AdminThemeMode.Dark)]
    public void Page_ShowsThePreviewCard_OutsideTheDimmedArea_InBothThemes(AdminThemeMode theme)
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.IsAdminThemeLight = theme == AdminThemeMode.Light;
            vm.IsAdminThemeDark = theme == AdminThemeMode.Dark;
            vm.FunPage = FunPage.Heart;
            ShowFunTab(window);
            vm.HeartEnabled = false; // die Vorschau bleibt bedienbar
            var view = HeartView(window);

            var host = InView<Border>(view, "HeartPreviewHost");
            Assert.Equal(16.0 / 9.0, host.Width / host.Height, 3);
            Assert.Single(FindDescendants<Fotobox.Views.Controls.CameraPreview>(host));
            var overlay = Assert.Single(FindDescendants<Fotobox.Views.Booth.Peace.PeaceOverlay>(host));
            Assert.True(overlay.ShowLandmarks);
            Assert.Single(FindDescendants<Fotobox.Views.Booth.Heart.HeartFeedbackOverlay>(host));
            Assert.Single(FindDescendants<Fotobox.Views.Booth.Heart.KisscamOverlay>(host));
            Assert.Empty(FindDescendants<Fotobox.Views.Booth.Peace.SelfDestructOverlay>(host));

            var panel = InView<StackPanel>(view, "HeartSettingsPanel");
            Assert.False(panel.IsEnabled);
            Assert.False(panel.IsAncestorOf(host)); // außerhalb des ausgegrauten Bereichs
            var test = InView<Button>(view, "KisscamTestButton");
            Assert.True(test.IsEnabled);
            Assert.Equal("Kisscam testen", test.Content);
            Assert.Same(vm.ToggleKisscamTestCommand, test.Command);
            Assert.Equal(Visibility.Collapsed, InView<Border>(view, "HeartDetectedBadge").Visibility);
        });
    }

    [StaFact]
    public void Page_TestButton_ShowsTheKisscamInThePreview_AndCancels()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.FunPage = FunPage.Heart;
            ShowFunTab(window);
            var view = HeartView(window);
            var button = InView<Button>(view, "KisscamTestButton");
            var overlay = InView<Fotobox.Views.Booth.Heart.KisscamOverlay>(view, "HeartKisscamTestOverlay");
            var digits = InView<Border>(view, "HeartKisscamTestDigits");
            var photoBadge = InView<Border>(view, "HeartKisscamTestPhotoBadge");
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            Assert.Equal(Visibility.Collapsed, digits.Visibility);
            Assert.Equal(Visibility.Collapsed, photoBadge.Visibility);

            vm.KisscamSeconds = 5;
            vm.KisscamBannerText = "BANNER";
            button.Command.Execute(null);

            Assert.Equal("Test abbrechen", button.Content);
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.True(overlay.ShowText);
            Assert.Equal(vm.KisscamTestText, overlay.Text);
            Assert.Equal("BANNER", overlay.Frame!.BannerText);
            Assert.Equal(Visibility.Collapsed, digits.Visibility);
            ((FrameworkElement)window.Content).UpdateLayout();
            var guestText = (TextBlock)overlay.FindName("GuestText");
            Assert.Equal(vm.KisscamTestText, guestText.Text);
            Assert.True(guestText.ActualHeight > 0);                                  // der Text liegt im Layout, unten in der Vorschau
            Assert.True(guestText.TranslatePoint(new Point(0, 0), overlay).Y > overlay.ActualHeight / 2);

            AdvanceSeconds(2); // 3: Ziffern statt Text
            Assert.False(overlay.ShowText);
            Assert.Equal(Visibility.Visible, digits.Visibility);
            Assert.Equal("3", InView<TextBlock>(view, "HeartKisscamTestDigitsText").Text);

            AdvanceSeconds(3); // Auslösemoment
            Assert.Equal(Visibility.Collapsed, digits.Visibility);
            Assert.Equal(Visibility.Visible, photoBadge.Visibility);

            button.Command.Execute(null); // abbrechen
            Assert.Equal("Kisscam testen", button.Content);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            Assert.Equal(Visibility.Collapsed, photoBadge.Visibility);
        });
    }

    // Zählt helle (weiße) Bildpunkte im Textband-Bereich unten zwischen den Eckherzen (Frame und Lichterkette liegen außerhalb).
    private static int CountBrightPixelsInTextBand(BitmapSource bitmap)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var x0 = (int)(width * 0.22);
        var x1 = (int)(width * 0.78);
        var y0 = (int)(height * 0.70);
        var y1 = (int)(height * 0.90);
        var stride = (x1 - x0) * 4;
        var pixels = new byte[stride * (y1 - y0)];
        bitmap.CopyPixels(new Int32Rect(x0, y0, x1 - x0, y1 - y0), pixels, stride, 0);
        var bright = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 200 && pixels[i + 1] > 200 && pixels[i + 2] > 200)
                bright++;
        }
        return bright;
    }

    // Rendert die Vorschau (Maßstab 1:3 der Gastansicht, 640 × 360) bzw. verkleinert (~510 × 285, wie bei 80 % Admin-Zoom).
    private static BitmapSource RenderPreviewHost(FrameworkElement host, int width, int height)
    {
        host.UpdateLayout();
        var scaleX = width / host.ActualWidth;
        var scaleY = height / host.ActualHeight;
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scaleX, scaleY));
            dc.DrawRectangle(new VisualBrush(host) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, host.ActualWidth, host.ActualHeight));
            dc.Pop();
        }
        bitmap.Render(visual);
        return bitmap;
    }

    [StaFact]
    public void Page_KisscamTest_RendersTheGuestText_InThePreviewBand()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.FunPage = FunPage.Heart;
            ShowFunTab(window);
            var view = HeartView(window);
            var host = InView<Border>(view, "HeartPreviewHost");
            vm.KisscamSeconds = 10;
            var before = CountBrightPixelsInTextBand(RenderPreviewHost(host, 640, 360));

            InView<Button>(view, "KisscamTestButton").Command.Execute(null);
            ((FrameworkElement)window.Content).UpdateLayout();
            Assert.True(vm.KisscamTestShowText);

            // Die Vorschau zeigt den Text in voller Größe (640 × 360) und verkleinert (~510 × 285) als helle Pixel im Textband.
            var full = CountBrightPixelsInTextBand(RenderPreviewHost(host, 640, 360));
            var small = CountBrightPixelsInTextBand(RenderPreviewHost(host, 510, 285));
            Assert.True(full > before + 100, $"Kein Gasttext im Textband (vorher {before}, danach {full}).");
            Assert.True(small > before + 60, $"Kein Gasttext in der verkleinerten Vorschau (vorher {before}, danach {small}).");
        });
    }

    [StaFact]
    public void Page_HoldFeedback_FollowsTheSetting_AndTheStatusLineShowsTheWarning()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.FunPage = FunPage.Heart;
            ShowFunTab(window);
            var view = HeartView(window);
            var feedback = InView<Fotobox.Views.Booth.Heart.HeartFeedbackOverlay>(view, "HeartPreviewFeedback");
            Assert.Equal(Visibility.Visible, feedback.Visibility);

            vm.HeartShowHoldFeedback = false;
            Assert.Equal(Visibility.Collapsed, feedback.Visibility);

            Assert.Equal(vm.HeartPreviewStatusText, InView<TextBlock>(view, "HeartPreviewStatus").Text);
            Assert.Equal(vm.HeartPreviewWarningText, InView<TextBlock>(view, "HeartPreviewWarning").Text);
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
    public void Page_Music_ShowsTheFolderButtonsHintsAndTheSongList()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.FunPage = FunPage.Heart;
            ShowFunTab(window);
            var view = HeartView(window);
            var folder = InView<TextBlock>(view, "KisscamMusicFolderText");
            var pick = InView<Button>(view, "KisscamMusicFolderPickButton");
            var reset = InView<Button>(view, "KisscamMusicFolderResetButton");
            var open = InView<Button>(view, "KisscamMusicFolderOpenButton");
            var refresh = InView<Button>(view, "KisscamMusicRefreshButton");
            var hints = InView<TextBlock>(view, "KisscamMusicHintsText");
            var list = InView<ItemsControl>(view, "KisscamSongList");
            Assert.Same(vm.ChooseKisscamMusicFolderCommand, pick.Command);
            Assert.Same(vm.ResetKisscamMusicFolderCommand, reset.Command);
            Assert.Same(vm.OpenKisscamMusicFolderCommand, open.Command);
            Assert.Same(vm.RefreshKisscamMusicCommand, refresh.Command);
            Assert.Equal(AppInfo.DefaultKisscamMusicFolder, folder.Text);
            Assert.False(reset.IsEnabled); // schon der Standardordner

            // Leerer Ordner: Hinweis, keine Zeilen.
            _library.Set(KisscamLibraryStatus.Ready, false);
            view.UpdateLayout();
            Assert.Equal(Visibility.Visible, hints.Visibility);
            Assert.Equal(AdminViewModel.KisscamMusicEmptyText, hints.Text);
            Assert.Empty(list.Items);

            // Songs: Zeilen, Häkchen schreibt in die Bibliothek.
            _library.Set(KisscamLibraryStatus.Ready, false, Song("Titel.wav", start: 5, duration: TimeSpan.FromSeconds(185)), Song("Lied.mp3"));
            view.UpdateLayout();
            Assert.Equal(2, list.Items.Count);
            Assert.Equal(AdminViewModel.KisscamNoSongSelectedText, hints.Text);
            var first = FindDescendants<CheckBox>(list).First();
            Assert.False(first.IsChecked);
            first.IsChecked = true;
            Assert.Equal("Titel.wav", _library.Updates[^1].File);
            Assert.True(_library.Updates[^1].Enabled);
            Assert.Equal(Visibility.Collapsed, hints.Visibility); // jetzt ist ein Song gewählt

            _ui.FolderToPick = Path.Combine(_dir, "Eigener Ordner");
            pick.Command.Execute(null);
            Assert.Equal(_ui.FolderToPick, folder.Text);
            Assert.True(reset.IsEnabled);
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
