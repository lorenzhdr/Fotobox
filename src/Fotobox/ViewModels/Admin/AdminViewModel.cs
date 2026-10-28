using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fotobox.Core;
using Fotobox.Services.Storage;
using Fotobox.ViewModels.Booth;

namespace Fotobox.ViewModels.Admin;

/// <summary>Eintrag der Auflösungs-Auswahl; <c>Resolution == null</c> bedeutet „Automatisch“.</summary>
public sealed record ResolutionOption(CameraResolution? Resolution, string Display)
{
    public override string ToString() => Display;
}

/// <summary>Eintrag der Drucker-Auswahl; <c>Name == null</c> bedeutet Windows-Standarddrucker.</summary>
public sealed record PrinterOption(string? Name, string Display)
{
    public override string ToString() => Display;
}

/// <summary>Eintrag der Monitor-Auswahl der Gastansicht; <c>Id == null</c> bedeutet „kein Monitor bekannt“ (Hauptbildschirm).</summary>
public sealed record MonitorOption(string? Id, string Display)
{
    public override string ToString() => Display;
}

/// <summary>Eintrag der Mikrofon-Auswahl; <c>Id == null</c> bedeutet Windows-Standardmikrofon.</summary>
public sealed record MicrophoneOption(string? Id, string Display)
{
    public override string ToString() => Display;
}

/// <summary>Eintrag der Hinweis-Stil-Auswahl (Fun-Text im Hinweis-Stil).</summary>
public sealed record HintStyleOption(HintStyle Style, string Display)
{
    public override string ToString() => Display;
}

/// <summary>Eintrag der Videoqualität-Auswahl.</summary>
public sealed record VideoQualityOption(VideoQuality Quality, string Display)
{
    public override string ToString() => Display;
}

/// <summary>Eintrag der Papiergrößen-Auswahl; <c>Info == null</c> bedeutet Standard des Druckers.</summary>
public sealed record PaperOption(PaperSizeInfo? Info, string Display)
{
    public override string ToString() => Display;
}

public sealed record OrientationOption(PageOrientation Orientation, string Display)
{
    public override string ToString() => Display;
}

/// <summary>Eintrag der Reihenfolge-Liste der Gastauswahl (Nummer, Name, ▲/▼).</summary>
public sealed class GuestLayoutItem
{
    internal GuestLayoutItem(GuestLayout layout, int position, string name, bool isDefault, bool canMoveUp, bool canMoveDown,
        Action makeDefault, Action moveUp, Action moveDown)
    {
        Layout = layout;
        Position = position;
        Name = name;
        IsDefault = isDefault;
        MakeDefaultCommand = new RelayCommand(makeDefault, () => !isDefault);
        MoveUpCommand = new RelayCommand(moveUp, () => canMoveUp);
        MoveDownCommand = new RelayCommand(moveDown, () => canMoveDown);
    }

    public GuestLayout Layout { get; }
    public int Position { get; }
    public string PositionText => Position + ".";
    public string Name { get; }

    /// <summary>Dieser Eintrag ist das Standard-Layout (★).</summary>
    public bool IsDefault { get; }

    public bool IsNotDefault => !IsDefault;
    public IRelayCommand MakeDefaultCommand { get; }
    public IRelayCommand MoveUpCommand { get; }
    public IRelayCommand MoveDownCommand { get; }
}

/// <summary>
/// Admin-Ansicht: bearbeitet einen Entwurf (Clone) der Einstellungen. Gespeichert wird erst über
/// <see cref="ApplyCommand"/> (Fenster bleibt offen) oder <see cref="SaveCommand"/> (speichern und schließen);
/// Schließen (<see cref="CloseCommand"/>) lässt den Store unberührt.
/// </summary>
public sealed partial class AdminViewModel : ObservableObject, IDisposable
{
    private const string DefaultPrinterDisplay = "Windows-Standarddrucker";
    private const string DefaultPaperDisplay = "Standard des Druckers";

    private readonly ISettingsStore _store;
    private readonly IPinService _pin;
    private readonly IPrintService _print;
    private readonly IDisplayService? _displays;
    private readonly IAdminUi _ui;
    private readonly SynchronizationContext? _sync = SynchronizationContext.Current;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _cameraGate = new(1, 1);
    // Wird beim Schließen der Admin-Ansicht (Dispose) abgebrochen, damit ein hängender Testdruck sie nicht überdauert.
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AppSettings _draft;

    // Zähler statt Flag: Kamera-, Drucker- und Schriftliste werden unabhängig voneinander geladen und angewendet.
    private int _loadingDepth;
    private bool _disposed;
    private int _cameraRestartVersion;
    // Mit diesen Kameraeinstellungen wurde die Kamera zuletzt wirklich gestartet (null = nie durch die Admin-Ansicht).
    private CameraSettings? _lastAppliedCamera;
    // Speicherordner beim Öffnen bzw. beim letzten Speichern: Nur ein geänderter Ordner wird per Schreibtest geprüft.
    private string? _initialPhotoFolder;
    private string? _initialVideoFolder;
    // Gespeicherter Stand des Entwurfs (normalisiertes JSON ohne PIN-Daten) zum Erkennen ungespeicherter Änderungen.
    private string? _baselineJson;
    private bool _isSaving;
    // Schließen wurde bereits angestoßen (Schließen, Speichern & schließen, Beenden): weiteres Speichern ist wirkungslos.
    private bool _closeRequested;
    private string _saveStatusMessage = string.Empty;

    private CameraDevice? _selectedDevice;
    private ResolutionOption? _selectedResolution;
    private PrinterOption? _selectedPrinter;
    private PaperOption? _selectedPaper;
    private OrientationOption _selectedOrientation;

    // Monitorwahl der Gastansicht: zuletzt geladene Liste, damit „Standardwerte wiederherstellen“ sie neu aufbauen kann.
    private IReadOnlyList<DisplayMonitor> _monitorList = [];
    private MonitorOption? _selectedMonitor;
    private bool _isMonitorSelectable;
    private string? _monitorHint;
    // Name des beim Öffnen gespeicherten, aber nicht angeschlossenen Monitors (für den Eintrag „… (nicht angeschlossen)“).
    private string? _missingMonitorName;

    // Mikrofonwahl (Tab Video): wie bei den Monitoren – zuletzt geladene Liste, damit „Standardwerte wiederherstellen“ sie neu aufbauen kann.
    private IReadOnlyList<MicrophoneDevice> _microphoneList = [];
    private MicrophoneOption? _selectedMicrophone;
    private string? _microphoneHint;
    // Name des beim Öffnen gespeicherten, aber nicht angeschlossenen Mikrofons (für den Eintrag „… (nicht angeschlossen)“).
    private string? _missingMicrophoneName;
    private VideoQualityOption _selectedVideoQuality;

    private string _validationMessage = string.Empty;
    private string _newPin = string.Empty;
    private string _repeatPin = string.Empty;
    private string _pinStatus = string.Empty;
    private string _testPrintStatus = string.Empty;
    private bool _isTestPrinting;
    private string _overlayInfo = string.Empty;
    private ImageSource? _overlayThumbnail;

    // Seitengröße für den Overlay-Hinweis: wird im Hintergrund ermittelt (der Drucker kann Sekunden brauchen).
    private PageSpec? _overlayPage;
    private int _pageSpecVersion;

    private IReadOnlyList<FontOption> _captionFonts = [];
    private IReadOnlyList<FontOption> _installedFonts = [];
    private FontOption _selectedCaptionFont;
    private bool _fontsLoaded;

    private IReadOnlyList<GuestLayoutItem> _guestOrder = [];

    private KeyActionEditor? _capturingEditor;
    private string _keyMessage = string.Empty;

    /// <summary>Länger darf die Abfrage der Seitengröße nicht dauern (der Druckdienst selbst gibt nach 5 s auf).</summary>
    private static readonly TimeSpan PageSpecTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Länger darf eine Prüfung des Speicherordners (Schreibtest, Existenz) nicht dauern: Ein hängender Netzpfad friert die
    /// Oberfläche sonst bis zum SMB-Timeout ein. Für Tests austauschbar.
    /// </summary>
    internal TimeSpan FolderProbeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Schreibtest des Speicherordners: Fehlertext oder null, wenn beschreibbar. Für Tests austauschbar.</summary>
    internal Func<string, string?> FolderWriteCheck { get; set; } =
        folder => FolderProbe.TryEnsureWritable(folder, out var error) ? null : error ?? "unbekannter Fehler";

    public AdminViewModel(ISettingsStore store, IPinService pin, ICameraService camera, IPrintService print, IAdminUi ui,
        TimeProvider? time = null, IDisplayService? displays = null,
        IPersonDetectionService? detection = null, IFunSoundPlayer? sound = null, IGestureDetectionService? gestures = null)
    {
        _time = time ?? TimeProvider.System;
        // Live-Vorschau im Tab „Fun“: eigene Zielerfassung (nicht aus dem Container, sonst hielte dieser jede Instanz). Ohne Dienste
        // (Tests) inert: Sie startet keine Erkennung, der Test-Knopf funktioniert trotzdem.
        FunPreview = detection is not null && sound is not null
            ? new FunOverlayViewModel(detection, sound, _time)
            : FunOverlayViewModel.CreateInert();
        FunPreview.PropertyChanged += OnFunPreviewPropertyChanged;
        // Live-Vorschau der Peaceerkennung (Fun → Peaceerkennung): eigene Instanz ohne Bart-Zustand der Gastansicht, damit ein Test
        // nie in ein echtes Foto gelangt. Ohne Dienste (Tests) inert.
        PeacePreview = gestures is not null && sound is not null
            ? new PeaceOverlayViewModel(gestures, _time, sound)
            : PeaceOverlayViewModel.CreateInert();
        PeacePreview.PropertyChanged += OnPeacePreviewPropertyChanged;
        PeacePreview.ActionRequested += OnPeacePreviewAction;
        _store = store;
        _pin = pin;
        Camera = camera;
        _print = print;
        _displays = displays;
        _ui = ui;
        _draft = store.Current.Clone();
        _initialPhotoFolder = _draft.Storage.PhotoFolder;
        _initialVideoFolder = _draft.Video.Folder;

        VideoQualities =
        [
            new(VideoQuality.High, "Hoch (beste Bildqualität, große Dateien)"),
            new(VideoQuality.Medium, "Mittel"),
            new(VideoQuality.Low, "Niedrig (kleine Dateien)"),
        ];
        _selectedVideoQuality = VideoQualities.First(q => q.Quality == _draft.Video.Quality);
        // Bis die Mikrofone geladen sind, zeigt die Auswahl das gespeicherte Mikrofon (bzw. das Standardmikrofon).
        RebuildMicrophoneOptions([]);

        Orientations =
        [
            new(PageOrientation.Auto, "Automatisch (Streifen hoch, sonst quer)"),
            new(PageOrientation.Portrait, "Hochformat"),
            new(PageOrientation.Landscape, "Querformat"),
        ];
        _selectedOrientation = Orientations.First(o => o.Orientation == _draft.Printer.Orientation);

        // Bis die Systemschriften geladen sind, zeigt die Auswahl die gespeicherte Schrift.
        _selectedCaptionFont = CreateMissingFontOption(SettingsNormalizer.SanitizeFontName(_draft.Layout.CaptionFontFamily), installed: true);
        _captionFonts = [_selectedCaptionFont];

        KeyEditors =
        [
            new KeyActionEditor(this, KeyAction.Trigger, "Foto auslösen",
                "Startet den Countdown und das Foto.", isRequired: true),
            new KeyActionEditor(this, KeyAction.Admin, "Admin-Ansicht öffnen",
                "Öffnet diese Einstellungen (nach PIN-Eingabe).", isRequired: true),
            new KeyActionEditor(this, KeyAction.Cancel, "Abbrechen",
                "Bricht einen laufenden Countdown oder Druck ab.", isRequired: false),
            new KeyActionEditor(this, KeyAction.VideoStart, "Video starten",
                "Startet die Videoaufnahme (nur bei aktivem Videomodus). Dieselbe Taste bei Start und Beenden schaltet die Aufnahme um.",
                isRequired: false),
            new KeyActionEditor(this, KeyAction.VideoStop, "Video beenden",
                "Beendet die laufende Videoaufnahme. Dieselbe Taste bei Start und Beenden schaltet die Aufnahme um.",
                isRequired: false),
        ];
        LoadKeysFromDraft();
        RebuildGuestOrder();

        SaveCommand = new AsyncRelayCommand(() => SaveCoreAsync(close: true), () => !IsSaving);
        ApplyCommand = new AsyncRelayCommand(() => SaveCoreAsync(close: false), () => !IsSaving);
        CloseCommand = new RelayCommand(Close);
        ExitCommand = new RelayCommand(RequestExit);
        ChooseOverlayCommand = new RelayCommand(ChooseOverlay);
        RemoveOverlayCommand = new RelayCommand(RemoveOverlay);
        ChooseFolderCommand = new RelayCommand(ChooseFolder);
        ResetFolderCommand = new RelayCommand(ResetFolder);
        OpenFolderCommand = new AsyncRelayCommand(OpenFolderAsync);
        ChooseVideoFolderCommand = new RelayCommand(ChooseVideoFolder);
        ResetVideoFolderCommand = new RelayCommand(ResetVideoFolder);
        OpenVideoFolderCommand = new AsyncRelayCommand(OpenVideoFolderAsync);
        ChangePinCommand = new RelayCommand(ChangePin);
        TestPrintCommand = new AsyncRelayCommand(TestPrintAsync, () => !IsTestPrinting);
        CancelKeyCaptureCommand = new RelayCommand(CancelKeyCapture);
        ResetKeysCommand = new RelayCommand(ResetKeys);
        ResetToDefaultsCommand = new AsyncRelayCommand(ResetToDefaultsAsync);
        TestFunPreviewCommand = new RelayCommand(TestFunPreview);
        ShowFunPageCommand = new RelayCommand<FunPage>(page => FunPage = page);
        TestSelfDestructCommand = new RelayCommand(StartPeaceTestSelfDestruct);
        TestMoustacheCommand = new RelayCommand(StartPeaceTestMoustache);

        Camera.StatusChanged += OnCameraStatusChanged;
        UpdateOverlay();
        _baselineJson = SnapshotJson();
    }

    // ---------------------------------------------------------------- Allgemein

    /// <summary>Der bearbeitete Entwurf (nicht der Store-Zustand).</summary>
    public AppSettings Draft => _draft;

    /// <summary>Kamera für die Live-Vorschau.</summary>
    public ICameraService Camera { get; }

    /// <summary>Speichern und die Admin-Ansicht schließen („Speichern &amp; schließen“).</summary>
    public IAsyncRelayCommand SaveCommand { get; }
    /// <summary>Speichern, die Admin-Ansicht bleibt offen („Übernehmen“).</summary>
    public IAsyncRelayCommand ApplyCommand { get; }
    /// <summary>Schließen ohne Speichern; bei ungespeicherten Änderungen mit Rückfrage („Schließen“).</summary>
    public IRelayCommand CloseCommand { get; }
    public IRelayCommand ExitCommand { get; }
    public IRelayCommand ChooseOverlayCommand { get; }
    public IRelayCommand RemoveOverlayCommand { get; }
    public IRelayCommand ChooseFolderCommand { get; }
    public IRelayCommand ResetFolderCommand { get; }
    public IAsyncRelayCommand OpenFolderCommand { get; }
    public IRelayCommand ChooseVideoFolderCommand { get; }
    public IRelayCommand ResetVideoFolderCommand { get; }
    public IAsyncRelayCommand OpenVideoFolderCommand { get; }
    public IRelayCommand ChangePinCommand { get; }
    public IAsyncRelayCommand TestPrintCommand { get; }
    public IRelayCommand CancelKeyCaptureCommand { get; }
    public IRelayCommand ResetKeysCommand { get; }
    public IAsyncRelayCommand ResetToDefaultsCommand { get; }

    /// <summary>Wird ausgelöst, wenn das Fenster geschlossen werden soll (Parameter: true = Speichern).</summary>
    public event Action<bool>? CloseRequested;

    /// <summary>Die PIN-Eingabefelder sollen geleert werden.</summary>
    public event Action? PinFieldsCleared;

    /// <summary>Die Einstellungen wurden mindestens einmal erfolgreich gespeichert (Übernehmen oder Speichern &amp; schließen).</summary>
    public bool SettingsSaved { get; private set; }

    public bool ExitRequested { get; private set; }

    /// <summary>Kamera wurde mit Entwurfs-Einstellungen neu gestartet (bei Schließen ohne Speichern zurücksetzen).</summary>
    public bool CameraTouched { get; private set; }

    /// <summary>Ein Speichervorgang läuft (Prüfung oder Schreiben): Übernehmen und Speichern sind dann gesperrt.</summary>
    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (!SetProperty(ref _isSaving, value))
                return;
            SaveCommand.NotifyCanExecuteChanged();
            ApplyCommand.NotifyCanExecuteChanged();
        }
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
                OnPropertyChanged(nameof(HasValidationMessage));
            // Ein neuer Fehler ersetzt die Erfolgsmeldung.
            if (value.Length > 0)
                SaveStatusMessage = string.Empty;
        }
    }

    public bool HasValidationMessage => _validationMessage.Length > 0;

    /// <summary>„Einstellungen gespeichert.“ nach Übernehmen; leer, sobald sich der Entwurf wieder ändert oder ein Fehler auftritt.</summary>
    public string SaveStatusMessage
    {
        get => _saveStatusMessage;
        private set
        {
            if (SetProperty(ref _saveStatusMessage, value))
                OnPropertyChanged(nameof(HasSaveStatus));
        }
    }

    public bool HasSaveStatus => _saveStatusMessage.Length > 0;

    // ---------------------------------------------------------------- Ungespeicherte Änderungen

    /// <summary>
    /// Unterscheidet sich der Entwurf vom zuletzt gespeicherten Stand (beim Öffnen bzw. nach dem letzten Speichern), oder ist
    /// eine neue PIN eingetragen? Verglichen wird das normalisierte JSON ohne PIN-Daten (die PIN ändert sich über den Dienst).
    /// </summary>
    public bool HasUnsavedChanges
    {
        get
        {
            if (_newPin.Length > 0 || _repeatPin.Length > 0)
                return true;
            var current = SnapshotJson();
            // Nicht serialisierbar: lieber einmal zu viel fragen.
            return current is null || !string.Equals(current, _baselineJson, StringComparison.Ordinal);
        }
    }

    /// <summary>JSON des normalisierten Entwurfs ohne <see cref="AppSettings.Security"/>; null, wenn es sich nicht erzeugen lässt.</summary>
    private string? SnapshotJson()
    {
        try
        {
            // Normalize arbeitet auf der Kopie (wie JsonSettingsStore.Save): Der Entwurf selbst bleibt unverändert.
            var copy = SettingsNormalizer.Normalize(_draft.Clone());
            copy.Security = new SecuritySettings();
            return JsonSerializer.Serialize(copy, AppSettings.JsonOptions);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Der Entwurf der Einstellungen konnte nicht verglichen werden.", ex);
            return null;
        }
    }

    /// <summary>Setzt den Vergleichsstand auf den aktuellen Entwurf (nach dem Laden der Listen und nach jedem Speichern).</summary>
    private void ResetBaseline() => _baselineJson = SnapshotJson();

    /// <summary>
    /// Fragt vor dem Verwerfen ungespeicherter Änderungen nach. true = Schließen ist in Ordnung (keine Änderungen oder bestätigt).
    /// Wirft nicht; scheitert die Rückfrage, wird nicht blockiert.
    /// </summary>
    public bool ConfirmDiscardChanges()
    {
        if (_disposed || _closeRequested || !HasUnsavedChanges)
            return true;

        try
        {
            return _ui.Confirm("Ungespeicherte Änderungen",
                "Es gibt ungespeicherte Änderungen. Möchten Sie sie verwerfen und den Admin schließen?");
        }
        catch (Exception ex)
        {
            AppLog.Error("Rückfrage zu ungespeicherten Änderungen fehlgeschlagen.", ex);
            return true;
        }
    }

    /// <summary>
    /// Blendet „Einstellungen gespeichert.“ aus, sobald der Entwurf vom gespeicherten Stand abweicht. Inhaltlich statt über
    /// einzelne Property-Namen, damit Änderungen ohne eigene Benachrichtigung ebenfalls erfasst werden und Hintergrundmeldungen
    /// (Kamerastatus, Seitengröße …) die Anzeige nicht grundlos löschen.
    /// </summary>
    private void HideSaveStatusIfDirty()
    {
        if (_saveStatusMessage.Length > 0 && HasUnsavedChanges)
            SaveStatusMessage = string.Empty;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // Läuft die Vorschau im Tab „Fun“, gelten geänderte Entwurfswerte sofort.
        if (_isFunTabSelected && !_disposed && AffectsFunPreview(e.PropertyName))
            UpdateFunPreview();
        if (_isFunTabSelected && !_disposed && AffectsPeacePreview(e.PropertyName))
            UpdatePeacePreview();
        if (_saveStatusMessage.Length > 0
            && e.PropertyName is not (nameof(SaveStatusMessage) or nameof(HasSaveStatus) or nameof(IsSaving)
                or nameof(FunPreviewStatusText) or nameof(FunPreviewWarningText) or nameof(HasFunPreviewWarning)
                or nameof(FunPreviewCameraRunning) or nameof(FunPreviewNoCamera) or nameof(IsFunTabSelected)
                or nameof(FunPage))
            && e.PropertyName?.StartsWith("PeacePreview", StringComparison.Ordinal) != true
            && e.PropertyName?.StartsWith("HasPeacePreview", StringComparison.Ordinal) != true)
            HideSaveStatusIfDirty();
    }

    // ---------------------------------------------------------------- Initialisierung

    /// <summary>Lädt Kamera-, Drucker-, Monitor-, Mikrofon- und Schriftenliste sowie die Seitengröße. Wirft nicht.</summary>
    public async Task InitializeAsync()
    {
        // Kamera-, Drucker- und Schriftenliste (die nur einmal pro Prozess geladen wird) laden und wenden sich unabhängig
        // voneinander an: Eine langsame Kamera- oder Druckerabfrage hält die anderen Listen nicht auf.
        // Die Seitengröße hängt nur vom Entwurf ab, nicht von den geladenen Listen.
        await Task.WhenAll(
            Guard(LoadCameraListsAsync(), "Kameraliste"),
            Guard(LoadPrintersAsync(), "Druckerliste"),
            Guard(LoadMonitorsAsync(), "Monitorliste"),
            Guard(LoadMicrophonesAsync(), "Mikrofonliste"),
            Guard(ApplyFontsAsync(FontCatalog.GetAsync()), "Schriftenliste"),
            Guard(RefreshPageSpecAsync(), "Seitengröße"));
        // Das Overlay-Vorschaubild hat bereits der Konstruktor geladen.
        RaiseCameraStatus();

        // Das Laden der Listen kann den Entwurf geringfügig angleichen (z. B. verschwundene Auflösung → „Automatisch“): Das ist
        // keine Änderung des Nutzers und soll beim Schließen nicht nachfragen lassen. Nach einem Speichern gilt dessen Stand.
        if (!SettingsSaved && !_disposed)
            ResetBaseline();
    }

    /// <summary>Protokolliert einen unerwarteten Fehler einer Ladeaufgabe, statt <see cref="InitializeAsync"/> scheitern zu lassen.</summary>
    private static async Task Guard(Task task, string what)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            AppLog.Error($"{what} der Admin-Ansicht konnte nicht geladen werden.", ex);
        }
    }

    private bool IsLoading => Volatile.Read(ref _loadingDepth) > 0;

    private void BeginLoading() => Interlocked.Increment(ref _loadingDepth);

    private void EndLoading() => Interlocked.Decrement(ref _loadingDepth);

    // ---------------------------------------------------------------- Kamera

    public ObservableCollection<CameraDevice> Devices { get; } = [];
    public ObservableCollection<ResolutionOption> Resolutions { get; } = [];

    public CameraDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (value is null || value == _selectedDevice)
                return;
            _selectedDevice = value;
            OnPropertyChanged();
            if (!IsLoading)
                Forget(SelectDeviceAsync(value));
        }
    }

    /// <summary>Gerätewechsel: Auflösung zurücksetzen, Auflösungen laden, Kamera mit Entwurf neu starten.</summary>
    public async Task SelectDeviceAsync(CameraDevice device)
    {
        _draft.Camera.DeviceId = device.Id;
        _draft.Camera.ResolutionWidth = null;
        _draft.Camera.ResolutionHeight = null;
        if (!Equals(_selectedDevice, device))
        {
            _selectedDevice = device;
            OnPropertyChanged(nameof(SelectedDevice));
        }
        HideSaveStatusIfDirty();   // OnPropertyChanged kam vor der Änderung des Entwurfs

        await LoadResolutionsAsync(device.Id);
        await RestartCameraAsync();
    }

    public ResolutionOption? SelectedResolution
    {
        get => _selectedResolution;
        set
        {
            if (value is null || value == _selectedResolution)
                return;
            _selectedResolution = value;
            OnPropertyChanged();
            if (IsLoading)
                return;
            _draft.Camera.ResolutionWidth = value.Resolution?.Width;
            _draft.Camera.ResolutionHeight = value.Resolution?.Height;
            HideSaveStatusIfDirty();   // OnPropertyChanged kam vor der Änderung des Entwurfs
            Forget(RestartCameraAsync());
        }
    }

    public bool MirrorPreview
    {
        get => _draft.Camera.MirrorPreview;
        set => SetProperty(_draft.Camera.MirrorPreview, value, v => _draft.Camera.MirrorPreview = v);
    }

    public bool MirrorPhoto
    {
        get => _draft.Camera.MirrorPhoto;
        set => SetProperty(_draft.Camera.MirrorPhoto, value, v => _draft.Camera.MirrorPhoto = v);
    }

    public string CameraStatusText
    {
        get
        {
            var message = Camera.StatusMessage;
            return Camera.Status switch
            {
                CameraStatus.Running => Camera.ActiveResolution is { } r
                    ? $"Kamera läuft ({r})."
                    : "Kamera läuft.",
                CameraStatus.Starting => "Kamera wird gestartet …",
                CameraStatus.Stopped => "Kamera ist angehalten.",
                CameraStatus.NoDevice => message ?? "Keine Kamera gefunden.",
                CameraStatus.AccessDenied => message ?? "Der Zugriff auf die Kamera wurde verweigert.",
                _ => message ?? "Die Kamera meldet einen Fehler.",
            };
        }
    }

    /// <summary>Startet die Kamera mit den Entwurfs-Einstellungen neu (nur die jeweils letzte Anforderung zählt).</summary>
    public async Task RestartCameraAsync()
    {
        if (_disposed)
            return;

        var version = Interlocked.Increment(ref _cameraRestartVersion);
        await _cameraGate.WaitAsync();
        try
        {
            if (version != Volatile.Read(ref _cameraRestartVersion) || _disposed)
                return;

            CameraTouched = true;
            var settings = _draft.Camera.Clone();
            // Festhalten, womit die Kamera wirklich läuft: Verwirft Speichern einen wartenden Neustart, kann sie noch auf einem
            // früheren Entwurfsstand laufen, obwohl die gespeicherten Werte denen beim Öffnen gleichen (siehe AdminLauncher).
            Volatile.Write(ref _lastAppliedCamera, settings.Clone());
            // Der Ton gilt ab dem nächsten Sessionstart: für die Vorschau der Stand des Entwurfs.
            Camera.ConfigureAudio(CameraServiceExtensions.AudioFor(_draft.Video));
            await Camera.StartAsync(settings);
        }
        catch (Exception ex)
        {
            AppLog.Error("Kamera-Neustart in den Einstellungen fehlgeschlagen.", ex);
        }
        finally
        {
            _cameraGate.Release();
            RaiseCameraStatus();
        }
    }

    /// <summary>
    /// Kameraeinstellungen, mit denen die Admin-Ansicht die Kamera zuletzt gestartet hat (Kopie); null, wenn sie sie nie
    /// angefasst hat. Läuft die Kamera damit anders als gespeichert, muss die Gastansicht sie nach dem Speichern neu starten.
    /// </summary>
    public CameraSettings? LastAppliedCamera => Volatile.Read(ref _lastAppliedCamera)?.Clone();

    /// <summary>
    /// Stellt nach „Schließen“ ohne Speichern die gespeicherten Einstellungen (Kamera und Ton) wieder her. Läuft über dasselbe Gate wie
    /// <see cref="RestartCameraAsync"/> und verwirft noch wartende Entwurfs-Neustarts.
    /// </summary>
    public async Task RestoreCameraAsync(AppSettings saved)
    {
        Interlocked.Increment(ref _cameraRestartVersion);
        await _cameraGate.WaitAsync();
        try
        {
            await Camera.StartWithAudioAsync(saved);
        }
        catch (Exception ex)
        {
            AppLog.Error("Kamera konnte nach dem Schließen der Einstellungen nicht neu gestartet werden.", ex);
        }
        finally
        {
            _cameraGate.Release();
        }
    }

    private async Task LoadCameraListsAsync()
    {
        IReadOnlyList<CameraDevice> devices;
        try
        {
            devices = await Camera.GetDevicesAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Kameraliste konnte nicht geladen werden.", ex);
            devices = [];
        }

        BeginLoading();
        try
        {
            Devices.Clear();
            foreach (var d in devices)
                Devices.Add(d);

            // Ohne gespeicherte Auswahl zeigt die Liste das erste Gerät (der Entwurf bleibt unverändert).
            _selectedDevice = Devices.FirstOrDefault(d => d.Id == _draft.Camera.DeviceId) ?? Devices.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedDevice));
        }
        finally
        {
            EndLoading();
        }

        if (_selectedDevice is not null)
            await LoadResolutionsAsync(_selectedDevice.Id);
    }

    private async Task LoadResolutionsAsync(string deviceId)
    {
        IReadOnlyList<CameraResolution> resolutions;
        try
        {
            resolutions = await Camera.GetResolutionsAsync(deviceId);
        }
        catch (Exception ex)
        {
            AppLog.Error("Auflösungen konnten nicht geladen werden.", ex);
            resolutions = [];
        }

        // Inzwischen wurde ein anderes Gerät gewählt: Die verspätete Antwort gehört nicht mehr zur Auswahl.
        if (_disposed || _selectedDevice?.Id != deviceId)
            return;

        // Nur eine vollständige Liste des eigenen Geräts darf die gespeicherte Auflösung für ungültig erklären. Fehlt die Liste
        // (Abfrage fehlgeschlagen, z. B. weil die Kamera gerade belegt ist) oder gehört sie zu einem anderen Gerät als dem
        // gespeicherten, bleibt der Entwurf unverändert – sonst würde „Speichern ohne Änderung“ die Auflösung löschen
        // und einen unnötigen Kamera-Neustart auslösen.
        var listIsAuthoritative = resolutions.Count > 0
            && (_draft.Camera.DeviceId is null || _draft.Camera.DeviceId == deviceId);

        BeginLoading();
        try
        {
            Resolutions.Clear();
            Resolutions.Add(new ResolutionOption(null, "Automatisch"));
            foreach (var r in resolutions.OrderByDescending(r => (long)r.Width * r.Height).Distinct())
                Resolutions.Add(new ResolutionOption(r, r.ToString()));

            var current = Resolutions.FirstOrDefault(o =>
                o.Resolution is { } r && r.Width == _draft.Camera.ResolutionWidth && r.Height == _draft.Camera.ResolutionHeight);
            if (current is null)
            {
                if (!listIsAuthoritative && _draft.Camera.ResolutionWidth is { } width && _draft.Camera.ResolutionHeight is { } height)
                {
                    // Liste nicht verfügbar → gespeicherte Auswahl erhalten (wie bei Drucker und Papier).
                    current = new ResolutionOption(new CameraResolution(width, height), $"{width} × {height} (nicht verfügbar)");
                    Resolutions.Add(current);
                }
                else
                {
                    // Gespeicherte Auflösung gibt es für dieses Gerät nicht → Automatisch anzeigen und speichern.
                    _draft.Camera.ResolutionWidth = null;
                    _draft.Camera.ResolutionHeight = null;
                    current = Resolutions[0];
                }
            }

            _selectedResolution = current;
            OnPropertyChanged(nameof(SelectedResolution));
        }
        finally
        {
            EndLoading();
        }
    }

    /// <summary>
    /// Für bewusst nicht abgewartete Aufgaben aus Property-Settern: Ein unerwarteter Fehler wird protokolliert,
    /// statt unbeobachtet zu bleiben.
    /// </summary>
    private static void Forget(Task task) =>
        _ = task.ContinueWith(t => AppLog.Error("Hintergrundaufgabe der Admin-Ansicht fehlgeschlagen.", t.Exception?.GetBaseException()),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private void OnCameraStatusChanged(object? sender, EventArgs e)
    {
        if (_sync is null)
            RaiseCameraStatus();
        else
            _sync.Post(_ => RaiseCameraStatus(), null);
    }

    private void RaiseCameraStatus()
    {
        OnPropertyChanged(nameof(CameraStatusText));
        OnPropertyChanged(nameof(FunPreviewCameraRunning));
        OnPropertyChanged(nameof(FunPreviewNoCamera));
        RaisePeacePreviewStatus();
    }

    // ---------------------------------------------------------------- Ablauf

    public int CountdownSeconds
    {
        get => _draft.Flow.CountdownSeconds;
        set => SetProperty(_draft.Flow.CountdownSeconds, value, v => _draft.Flow.CountdownSeconds = v);
    }

    public int PreviewSeconds
    {
        get => _draft.Flow.PreviewSeconds;
        set => SetProperty(_draft.Flow.PreviewSeconds, value, v => _draft.Flow.PreviewSeconds = v);
    }

    public int PauseBetweenShotsSeconds
    {
        get => _draft.Flow.PauseBetweenShotsSeconds;
        set => SetProperty(_draft.Flow.PauseBetweenShotsSeconds, value, v => _draft.Flow.PauseBetweenShotsSeconds = v);
    }

    public int ErrorDisplaySeconds
    {
        get => _draft.Flow.ErrorDisplaySeconds;
        set => SetProperty(_draft.Flow.ErrorDisplaySeconds, value, v => _draft.Flow.ErrorDisplaySeconds = v);
    }

    public bool PrintingEnabled
    {
        get => _draft.Flow.PrintingEnabled;
        set => SetProperty(_draft.Flow.PrintingEnabled, value, v => _draft.Flow.PrintingEnabled = v);
    }

    /// <summary>So lange läuft der Fortschrittsbalken in der Gastansicht.</summary>
    public int ExpectedPrintSeconds
    {
        get => _draft.Flow.ExpectedPrintSeconds;
        set
        {
            if (SetProperty(_draft.Flow.ExpectedPrintSeconds, value, v => _draft.Flow.ExpectedPrintSeconds = v))
                RaisePrintTimeoutHint();
        }
    }

    private void RaisePrintTimeoutHint()
    {
        OnPropertyChanged(nameof(PrintTimeoutHint));
        OnPropertyChanged(nameof(HasPrintTimeoutHint));
    }

    /// <summary>
    /// Hinweis, wenn das Druck-Timeout unter „erwartete Dauer + Reserve“ liegt (der Normalisierer hebt es beim
    /// Speichern an; ein Fehler ist das nicht). Leer = nichts anzuzeigen.
    /// </summary>
    public string PrintTimeoutHint
    {
        get
        {
            var minimum = _draft.Flow.ExpectedPrintSeconds + SettingsLimits.PrintTimeoutReserveSeconds;
            return _draft.Printer.TimeoutSeconds < minimum
                ? $"Das Zeitlimit ist kürzer als die erwartete Druckdauer plus {SettingsLimits.PrintTimeoutReserveSeconds} s. Es wird beim Speichern auf {minimum} s angehoben."
                : string.Empty;
        }
    }

    public bool HasPrintTimeoutHint => PrintTimeoutHint.Length > 0;

    // ---------------------------------------------------------------- Layout

    public bool IsSingleLayout
    {
        get => _draft.Layout.Kind == LayoutKind.SingleBorderless;
        set => SetLayoutKind(LayoutKind.SingleBorderless, value);
    }

    public bool IsStripLayout
    {
        get => _draft.Layout.Kind == LayoutKind.Strip;
        set => SetLayoutKind(LayoutKind.Strip, value);
    }

    public bool IsOverlayLayout
    {
        get => _draft.Layout.Kind == LayoutKind.Overlay;
        set => SetLayoutKind(LayoutKind.Overlay, value);
    }

    private void SetLayoutKind(LayoutKind kind, bool selected)
    {
        if (!selected || _draft.Layout.Kind == kind)
            return;
        _draft.Layout.Kind = kind;
        OnPropertyChanged(nameof(IsSingleLayout));
        OnPropertyChanged(nameof(IsStripLayout));
        OnPropertyChanged(nameof(IsOverlayLayout));
        EnsureDefaultInGuestChoices();
    }

    // ---- Auswahl für Gäste (Mausrad)
    // Die Wahlmöglichkeiten stehen im Entwurf als geordnete Liste; ihre Reihenfolge ist die Scroll-Reihenfolge der Gäste.
    // Leer = kein Wechsel. Ist sie nicht leer, enthält sie immer das Standard-Layout (Art + Streifenvariante, an beliebiger
    // Position); dessen Kästchen ist angehakt und nicht abwählbar. Neu angehakte Layouts kommen ans Ende.
    // In der Reihenfolge-Liste markiert ★ den Standard; ☆ macht einen Eintrag zum Standard (wie die Karten oben).

    /// <summary>Standard-Layout des Entwurfs (aus Art und Streifenvariante).</summary>
    private GuestLayout DefaultGuestLayout => GuestLayouts.From(_draft.Layout);

    private bool IsGuestChoice(GuestLayout layout) =>
        layout == DefaultGuestLayout || (_draft.Layout.GuestChoices?.Contains(layout) ?? false);

    private void SetGuestChoice(GuestLayout layout, bool selected)
    {
        var standard = DefaultGuestLayout;
        if (layout != standard)
        {
            var list = new List<GuestLayout>(_draft.Layout.GuestChoices ?? []);
            if (selected)
            {
                if (!list.Contains(standard))
                    list.Add(standard);
                if (!list.Contains(layout))
                    list.Add(layout);
            }
            else
            {
                list.Remove(layout);
            }

            // Nur der Standard übrig: kein Wechsel möglich → Liste leer.
            _draft.Layout.GuestChoices = list.Count <= 1 ? [] : list;
        }

        // Auch bei abgelehntem Abwählen des Standards: das Kästchen springt auf „angehakt“ zurück.
        RaiseGuestChoices();
    }

    /// <summary>Nach einem Wechsel des Standards: Ist ein Wechsel für Gäste aktiv, gehört der neue Standard dazu (hinten, falls er fehlt).</summary>
    private void EnsureDefaultInGuestChoices()
    {
        var choices = _draft.Layout.GuestChoices ?? [];
        if (choices.Count > 0 && !choices.Contains(DefaultGuestLayout))
            _draft.Layout.GuestChoices = [.. choices, DefaultGuestLayout];
        RaiseGuestChoices();
    }

    /// <summary>
    /// Macht ein Layout zum Standard (setzt Art und Streifenvariante wie die Karten oben). Die Reihenfolge bleibt,
    /// der bisherige Standard bleibt in der Gastauswahl.
    /// </summary>
    public void MakeGuestLayoutDefault(GuestLayout layout)
    {
        if (layout == DefaultGuestLayout)
            return;
        GuestLayouts.ApplyTo(_draft.Layout, layout);
        OnPropertyChanged(nameof(IsSingleLayout));
        OnPropertyChanged(nameof(IsStripLayout));
        OnPropertyChanged(nameof(IsOverlayLayout));
        OnPropertyChanged(nameof(IsStripDoubleCut));
        OnPropertyChanged(nameof(IsStripSingle));
        EnsureDefaultInGuestChoices();
    }

    /// <summary>Verschiebt ein Layout der Gastauswahl um eine Position nach oben (früher beim Scrollen).</summary>
    public void MoveGuestLayoutUp(GuestLayout layout) => MoveGuestLayout(layout, -1);

    /// <summary>Verschiebt ein Layout der Gastauswahl um eine Position nach unten (später beim Scrollen).</summary>
    public void MoveGuestLayoutDown(GuestLayout layout) => MoveGuestLayout(layout, +1);

    private void MoveGuestLayout(GuestLayout layout, int delta)
    {
        var list = _draft.Layout.GuestChoices;
        var index = list?.IndexOf(layout) ?? -1;
        var target = index + delta;
        if (list is null || index < 0 || target < 0 || target >= list.Count)
            return;

        var reordered = new List<GuestLayout>(list);
        (reordered[index], reordered[target]) = (reordered[target], reordered[index]);
        _draft.Layout.GuestChoices = reordered;
        RaiseGuestOrder();
    }

    /// <summary>Die Gastauswahl in Scroll-Reihenfolge (leer = kein Wechsel); jeder Eintrag mit Nummer, Name und ▲/▼-Befehlen.</summary>
    public IReadOnlyList<GuestLayoutItem> GuestOrder => _guestOrder;

    /// <summary>Die Reihenfolge-Liste ist relevant: mindestens zwei Layouts zur Auswahl.</summary>
    public bool HasGuestOrder => _guestOrder.Count >= 2;

    private void RebuildGuestOrder()
    {
        var list = _draft.Layout.GuestChoices ?? [];
        var photoCount = _draft.Layout.StripPhotoCount;
        var standard = DefaultGuestLayout;
        _guestOrder = list
            .Select((layout, i) => new GuestLayoutItem(
                layout, i + 1, GuestLayouts.DisplayName(layout, photoCount), isDefault: layout == standard,
                canMoveUp: i > 0, canMoveDown: i < list.Count - 1,
                makeDefault: () => MakeGuestLayoutDefault(layout),
                moveUp: () => MoveGuestLayoutUp(layout), moveDown: () => MoveGuestLayoutDown(layout)))
            .ToArray();
    }

    private void RaiseGuestOrder()
    {
        RebuildGuestOrder();
        OnPropertyChanged(nameof(GuestOrder));
        OnPropertyChanged(nameof(HasGuestOrder));
        // Die Vorschau der Gastansicht zeigt ab zwei Wahlmöglichkeiten die Layout-Zeile mit dem Namen des Standard-Layouts.
        RaiseWheelHintPreview();
    }

    private void RaiseGuestChoices()
    {
        OnPropertyChanged(nameof(IsGuestSingle));
        OnPropertyChanged(nameof(IsGuestStripDouble));
        OnPropertyChanged(nameof(IsGuestStripSingle));
        OnPropertyChanged(nameof(IsGuestOverlay));
        OnPropertyChanged(nameof(IsGuestSingleEnabled));
        OnPropertyChanged(nameof(IsGuestStripDoubleEnabled));
        OnPropertyChanged(nameof(IsGuestStripSingleEnabled));
        OnPropertyChanged(nameof(IsGuestOverlayEnabled));
        OnPropertyChanged(nameof(ShowStripOptions));
        OnPropertyChanged(nameof(ShowOverlayOptions));
        RaiseGuestOrder();
    }

    public bool IsGuestSingle
    {
        get => IsGuestChoice(GuestLayout.Single);
        set => SetGuestChoice(GuestLayout.Single, value);
    }

    public bool IsGuestStripDouble
    {
        get => IsGuestChoice(GuestLayout.StripDouble);
        set => SetGuestChoice(GuestLayout.StripDouble, value);
    }

    public bool IsGuestStripSingle
    {
        get => IsGuestChoice(GuestLayout.StripSingle);
        set => SetGuestChoice(GuestLayout.StripSingle, value);
    }

    public bool IsGuestOverlay
    {
        get => IsGuestChoice(GuestLayout.Overlay);
        set => SetGuestChoice(GuestLayout.Overlay, value);
    }

    // Das Kästchen des Standard-Layouts ist angehakt, aber nicht änderbar.
    public bool IsGuestSingleEnabled => DefaultGuestLayout != GuestLayout.Single;
    public bool IsGuestStripDoubleEnabled => DefaultGuestLayout != GuestLayout.StripDouble;
    public bool IsGuestStripSingleEnabled => DefaultGuestLayout != GuestLayout.StripSingle;
    public bool IsGuestOverlayEnabled => DefaultGuestLayout != GuestLayout.Overlay;

    /// <summary>Fotoanzahl des Streifens ist relevant: Standard ist ein Streifen oder Gäste können einen Streifen wählen.</summary>
    public bool ShowStripOptions =>
        _draft.Layout.Kind == LayoutKind.Strip || IsGuestStripDouble || IsGuestStripSingle;

    /// <summary>Overlay-Datei ist relevant: Standard ist Overlay oder Gäste können es wählen.</summary>
    public bool ShowOverlayOptions =>
        _draft.Layout.Kind == LayoutKind.Overlay || IsGuestOverlay;

    public int StripPhotoCount
    {
        get => _draft.Layout.StripPhotoCount;
        set
        {
            if (SetProperty(_draft.Layout.StripPhotoCount, value, v => _draft.Layout.StripPhotoCount = v))
                RaiseGuestOrder();
        }
    }

    /// <summary>Fotostreifen: zwei gleiche Streifen nebeneinander (zum Teilen).</summary>
    public bool IsStripDoubleCut
    {
        get => _draft.Layout.StripStyle == StripStyle.DoubleCut;
        set => SetStripStyle(StripStyle.DoubleCut, value);
    }

    /// <summary>Fotostreifen: ein Streifen über die ganze Seite.</summary>
    public bool IsStripSingle
    {
        get => _draft.Layout.StripStyle == StripStyle.Single;
        set => SetStripStyle(StripStyle.Single, value);
    }

    private void SetStripStyle(StripStyle style, bool selected)
    {
        if (!selected || _draft.Layout.StripStyle == style)
            return;
        _draft.Layout.StripStyle = style;
        OnPropertyChanged(nameof(IsStripDoubleCut));
        OnPropertyChanged(nameof(IsStripSingle));
        EnsureDefaultInGuestChoices();
    }

    public string CaptionText
    {
        get => _draft.Layout.CaptionText ?? string.Empty;
        set
        {
            if (SetProperty(_draft.Layout.CaptionText ?? string.Empty, value,
                    v => _draft.Layout.CaptionText = string.IsNullOrWhiteSpace(v) ? null : v))
                OnPropertyChanged(nameof(CaptionPreviewText));
        }
    }

    // ---- Schrift der Bildunterschrift

    public const string CaptionFontMissingHint = "Diese Schrift ist auf diesem PC nicht installiert – es wird Segoe UI verwendet.";

    /// <summary>Alle Systemschriften (nach Anzeigename sortiert); eine fehlende gespeicherte Schrift steht an erster Stelle.</summary>
    public IReadOnlyList<FontOption> CaptionFonts
    {
        get => _captionFonts;
        private set => SetProperty(ref _captionFonts, value);
    }

    public FontOption SelectedCaptionFont
    {
        get => _selectedCaptionFont;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedCaptionFont))
                return;
            _selectedCaptionFont = value;
            if (!IsLoading)
                _draft.Layout.CaptionFontFamily = value.Source;
            RaiseCaptionFont();
        }
    }

    /// <summary>Die gespeicherte Schrift ist nicht installiert (erst bekannt, wenn die Liste geladen ist).</summary>
    public bool IsCaptionFontMissing => _fontsLoaded && !_selectedCaptionFont.IsInstalled;

    /// <summary>Schrift für die Vorschau: die gewählte, bei fehlender Schrift Segoe UI.</summary>
    public FontFamily CaptionPreviewFont => _selectedCaptionFont.IsInstalled ? _selectedCaptionFont.Family : FontCatalog.Fallback;

    public string CaptionPreviewText =>
        string.IsNullOrWhiteSpace(_draft.Layout.CaptionText) ? "Vorschau der Bildunterschrift" : _draft.Layout.CaptionText;

    private void RaiseCaptionFont()
    {
        OnPropertyChanged(nameof(SelectedCaptionFont));
        OnPropertyChanged(nameof(IsCaptionFontMissing));
        OnPropertyChanged(nameof(CaptionPreviewFont));
    }

    private static FontOption CreateMissingFontOption(string name, bool installed) =>
        installed
            ? new FontOption(name, name, new FontFamily(name))
            : new FontOption(name, name + " (nicht installiert)", FontCatalog.Fallback, IsInstalled: false);

    private async Task ApplyFontsAsync(Task<IReadOnlyList<FontOption>> fontsTask)
    {
        IReadOnlyList<FontOption> fonts;
        try
        {
            fonts = await fontsTask;
        }
        catch (Exception ex)
        {
            AppLog.Error("Schriftenliste konnte nicht geladen werden.", ex);
            fonts = [];
        }

        _installedFonts = fonts;
        _fontsLoaded = true;
        SelectCaptionFontFromDraft();
    }

    /// <summary>Wählt in der geladenen Schriftenliste die Schrift des Entwurfs (fehlt sie, als „nicht installiert“ vorangestellt).</summary>
    private void SelectCaptionFontFromDraft()
    {
        var fonts = _installedFonts;
        var saved = SettingsNormalizer.SanitizeFontName(_draft.Layout.CaptionFontFamily);
        var match = fonts.FirstOrDefault(f => string.Equals(f.Source, saved, StringComparison.OrdinalIgnoreCase));
        var list = fonts;
        if (match is null)
        {
            // Nicht installiert: Eintrag trotzdem zeigen, damit nichts stillschweigend wechselt. Ist die Liste leer
            // (Aufzählung fehlgeschlagen), lässt sich "nicht installiert" nicht behaupten.
            match = CreateMissingFontOption(saved, installed: fonts.Count == 0);
            list = [match, .. fonts];
        }

        BeginLoading();
        try
        {
            CaptionFonts = list;
            _selectedCaptionFont = match;
            RaiseCaptionFont();
        }
        finally
        {
            EndLoading();
        }
    }

    public bool ShowDate
    {
        get => _draft.Layout.ShowDate;
        set => SetProperty(_draft.Layout.ShowDate, value, v => _draft.Layout.ShowDate = v);
    }

    public string? OverlayFileName => _draft.Layout.OverlayFileName;

    public bool HasOverlay => !string.IsNullOrEmpty(_draft.Layout.OverlayFileName);

    public ImageSource? OverlayThumbnail
    {
        get => _overlayThumbnail;
        private set => SetProperty(ref _overlayThumbnail, value);
    }

    public string OverlayInfo
    {
        get => _overlayInfo;
        private set => SetProperty(ref _overlayInfo, value);
    }

    /// <summary>Hinweis auf die empfohlene Pixelgröße des Overlays (aus der Seitengröße des Druckers).</summary>
    public string OverlayHint
    {
        get
        {
            // Nur der zwischengespeicherte Wert: Die Abfrage beim Drucker läuft im Hintergrund (RefreshPageSpecAsync).
            if (_overlayPage is not { } page)
                return "Seitengröße wird ermittelt …";
            var size = string.Create(CultureInfo.GetCultureInfo("de-DE"),
                $"Empfohlene Größe: {page.PixelWidth} × {page.PixelHeight} Pixel ({page.WidthMm:0.#} × {page.HeightMm:0.#} mm bei {page.Dpi} dpi). ");
            return size + "Transparente Bereiche lassen das Foto durchscheinen.";
        }
    }

    /// <summary>
    /// Ermittelt die Seitengröße für den Overlay-Hinweis im Hintergrund (mit Timeout; Fehler → Standardgröße).
    /// Ergebnisse, die von neueren Anfragen überholt wurden (Drucker, Papier oder Ausrichtung inzwischen geändert),
    /// werden verworfen. Wirft nicht.
    /// </summary>
    public async Task RefreshPageSpecAsync()
    {
        if (_disposed)
            return;

        var version = Interlocked.Increment(ref _pageSpecVersion);
        if (_overlayPage is not null)
        {
            _overlayPage = null;
            OnPropertyChanged(nameof(OverlayHint));
        }

        PageSpec page;
        try
        {
            page = await GetPageSpecAsync(SnapshotPrinter(_draft.Printer.Copies), LayoutKind.Overlay, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Seitengröße konnte nicht ermittelt werden.", ex);
            page = PageSpec.Default;
        }

        if (_disposed || version != Volatile.Read(ref _pageSpecVersion))
            return;

        _overlayPage = page;
        OnPropertyChanged(nameof(OverlayHint));
    }

    /// <summary>Fragt die Seitengröße auf dem Threadpool ab; wirft <see cref="TimeoutException"/> nach <see cref="PageSpecTimeout"/>.</summary>
    private async Task<PageSpec> GetPageSpecAsync(PrinterSettings printer, LayoutKind kind, CancellationToken ct)
    {
        var query = Task.Run(() => _print.GetPageSpec(printer, kind));
        // Hängt der Treiber, läuft die Abfrage ohne uns weiter; ihr Fehler soll nicht unbeobachtet bleiben.
        _ = query.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return await query.WaitAsync(PageSpecTimeout, _time, ct);
    }

    /// <summary>Kopie der Druckeinstellungen des Entwurfs (für Hintergrundabfragen und den Testdruck).</summary>
    private PrinterSettings SnapshotPrinter(int copies) => new()
    {
        PrinterName = _draft.Printer.PrinterName,
        PaperSizeName = _draft.Printer.PaperSizeName,
        PaperWidthMm = _draft.Printer.PaperWidthMm,
        PaperHeightMm = _draft.Printer.PaperHeightMm,
        Orientation = _draft.Printer.Orientation,
        Copies = copies,
        TimeoutSeconds = _draft.Printer.TimeoutSeconds,
    };

    /// <summary>Startet <see cref="RefreshPageSpecAsync"/> ohne zu warten.</summary>
    private void RequestPageSpec() => _ = RefreshPageSpecAsync();

    private void ChooseOverlay()
    {
        var path = _ui.PickOverlayFile();
        if (!string.IsNullOrEmpty(path))
            ImportOverlay(path);
    }

    /// <summary>Kopiert die PNG-Datei in den Overlay-Ordner und übernimmt sie in den Entwurf.</summary>
    public bool ImportOverlay(string sourcePath)
    {
        try
        {
            if (!string.Equals(Path.GetExtension(sourcePath), ".png", StringComparison.OrdinalIgnoreCase))
            {
                ValidationMessage = "Bitte wählen Sie eine PNG-Datei als Overlay aus.";
                return false;
            }

            // Vorab prüfen, ob die Datei ein gültiges Bild ist (die Vorschau wird dabei komplett gelesen, aber klein dekodiert).
            _ = LoadOverlayPreview(sourcePath);

            // Eine vorhandene Overlay-Datei (ggf. das aktive Overlay) wird nie überschrieben: „Schließen“ in der
            // Admin-Ansicht könnte das nicht rückgängig machen. Stattdessen eindeutiger Name bzw. identische Datei wiederverwenden.
            var fileName = CopyOverlayToStore(sourcePath);

            _draft.Layout.OverlayFileName = fileName;
            ValidationMessage = string.Empty;
            UpdateOverlay();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Overlay konnte nicht übernommen werden.", ex);
            ValidationMessage = "Das Overlay konnte nicht übernommen werden. Ist es eine gültige PNG-Datei?";
            return false;
        }
    }

    /// <summary>Legt die Datei im Overlay-Ordner ab, ohne Vorhandenes zu überschreiben, und liefert den Dateinamen.</summary>
    private string CopyOverlayToStore(string sourcePath)
    {
        var directory = _store.OverlayDirectory;
        Directory.CreateDirectory(directory);

        var original = Path.GetFileName(sourcePath);
        var stem = Path.GetFileNameWithoutExtension(original);
        var extension = Path.GetExtension(original);
        var sourceFull = Path.GetFullPath(sourcePath);

        for (var attempt = 1; attempt <= 1000; attempt++)
        {
            var name = attempt == 1 ? original : $"{stem}_{attempt}{extension}";
            var target = Path.Combine(directory, name);

            // Die Datei liegt bereits im Overlay-Ordner (z. B. erneut ausgewählt) → direkt verwenden.
            if (string.Equals(sourceFull, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                return name;

            if (File.Exists(target))
            {
                if (SameContent(sourcePath, target))
                    return name;
                continue;
            }

            try
            {
                File.Copy(sourcePath, target, overwrite: false);
                return name;
            }
            catch (IOException) when (File.Exists(target))
            {
                // Zwischenzeitlich angelegt → nächsten Namen versuchen (bzw. identische Datei erkennen).
                if (SameContent(sourcePath, target))
                    return name;
            }
        }

        throw new IOException("Für das Overlay konnte kein freier Dateiname gefunden werden.");
    }

    private static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length)
            return false;

        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        return SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }

    private void RemoveOverlay()
    {
        _draft.Layout.OverlayFileName = null;
        UpdateOverlay();
    }

    private void UpdateOverlay()
    {
        BitmapSource? bitmap = null;
        var info = string.Empty;
        var name = _draft.Layout.OverlayFileName;
        if (!string.IsNullOrEmpty(name))
        {
            var path = Path.Combine(_store.OverlayDirectory, name);
            try
            {
                var preview = LoadOverlayPreview(path);
                bitmap = preview.Thumbnail;
                info = $"{name} ({preview.PixelWidth} × {preview.PixelHeight} Pixel)";
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Overlay '{name}' konnte nicht geladen werden.", ex);
                info = $"{name} (Datei nicht gefunden oder ungültig)";
            }
        }

        OverlayThumbnail = bitmap;
        OverlayInfo = info;
        OnPropertyChanged(nameof(OverlayFileName));
        OnPropertyChanged(nameof(HasOverlay));
        OnPropertyChanged(nameof(OverlayHint));
    }

    /// <summary>Breite, auf die das Overlay für die Vorschau (90 × 120 DIPs im Admin) dekodiert wird.</summary>
    private const int OverlayPreviewDecodeWidth = 240;

    /// <summary>
    /// Lädt das Overlay als kleines Vorschaubild (ein druckgroßes PNG voll zu dekodieren kostet viel Speicher und Zeit) und liefert
    /// zusätzlich die echte Pixelgröße der Datei, die aus den Bildmetadaten gelesen wird. Die Datei bleibt nicht geöffnet.
    /// </summary>
    private static (BitmapSource Thumbnail, int PixelWidth, int PixelHeight) LoadOverlayPreview(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        int width = frame.PixelWidth, height = frame.PixelHeight;

        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;   // ohne UriSource gibt es keinen Bildcache, der veralten könnte
        if (width > OverlayPreviewDecodeWidth)
            image.DecodePixelWidth = OverlayPreviewDecodeWidth;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return (image, width, height);
    }

    // ---------------------------------------------------------------- Monitor der Gastansicht

    private const string FallbackMonitorDisplay = "Hauptbildschirm";
    private const string UnknownMonitorName = "Gewählter Bildschirm";

    /// <summary>Länger darf die Monitorabfrage nicht dauern (sie ist eigentlich sofort fertig).</summary>
    private static readonly TimeSpan MonitorListTimeout = TimeSpan.FromSeconds(5);

    public ObservableCollection<MonitorOption> Monitors { get; } = [];

    public MonitorOption? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (value is null || value == _selectedMonitor)
                return;
            _selectedMonitor = value;
            OnPropertyChanged();
            if (IsLoading)
                return;

            // Nur eine echte Nutzeränderung schreibt in den Entwurf. Ohne gespeicherte Wahl bleibt die Id also null.
            var monitor = value.Id is null ? null : _monitorList.FirstOrDefault(m => m.Id == value.Id);
            if (monitor is not null)
            {
                _draft.Appearance.GuestMonitorId = monitor.Id;
                _draft.Appearance.GuestMonitorName = monitor.Name;
            }
            else if (value.Id is not null)
            {
                // Zurück auf den nicht angeschlossenen Monitor: gespeicherte Wahl wiederherstellen.
                _draft.Appearance.GuestMonitorId = value.Id;
                _draft.Appearance.GuestMonitorName = _missingMonitorName;
            }
            HideSaveStatusIfDirty();   // OnPropertyChanged kam vor der Änderung des Entwurfs
            UpdateMonitorHint();
        }
    }

    /// <summary>Mehr als ein Monitor angeschlossen: Die Auswahl ist bedienbar, sonst ausgegraut.</summary>
    public bool IsMonitorSelectable
    {
        get => _isMonitorSelectable;
        private set => SetProperty(ref _isMonitorSelectable, value);
    }

    /// <summary>Hinweis unter der Monitor-Auswahl (null = keiner).</summary>
    public string? MonitorHint
    {
        get => _monitorHint;
        private set
        {
            if (SetProperty(ref _monitorHint, value))
                OnPropertyChanged(nameof(HasMonitorHint));
        }
    }

    public bool HasMonitorHint => !string.IsNullOrEmpty(_monitorHint);

    private async Task LoadMonitorsAsync()
    {
        IReadOnlyList<DisplayMonitor> monitors = [];
        try
        {
            if (_displays is { } displays)
                monitors = await Task.Run(displays.GetMonitors).WaitAsync(MonitorListTimeout, _time);
        }
        catch (Exception ex)
        {
            AppLog.Error("Monitorliste konnte nicht geladen werden.", ex);
            monitors = [];
        }

        if (_disposed)
            return;
        RebuildMonitorOptions(monitors);
    }

    /// <summary>
    /// Baut Liste und Auswahl aus den angeschlossenen Monitoren und der gespeicherten Wahl im Entwurf auf.
    /// Der Entwurf selbst bleibt dabei unverändert.
    /// </summary>
    private void RebuildMonitorOptions(IReadOnlyList<DisplayMonitor> monitors)
    {
        _monitorList = monitors;
        var savedId = _draft.Appearance.GuestMonitorId;
        _missingMonitorName = _draft.Appearance.GuestMonitorName;

        BeginLoading();
        try
        {
            Monitors.Clear();
            if (monitors.Count == 0)
            {
                // Keine Monitorliste (Fehler): Die Gastansicht bleibt auf dem Hauptbildschirm.
                var fallback = new MonitorOption(null, FallbackMonitorDisplay);
                Monitors.Add(fallback);
                _selectedMonitor = fallback;
            }
            else
            {
                foreach (var m in monitors)
                    Monitors.Add(new MonitorOption(m.Id, GuestMonitors.Label(m)));

                var missing = savedId is not null && monitors.All(m => m.Id != savedId);
                if (missing && monitors.Count > 1)
                {
                    var option = new MonitorOption(savedId, $"{_missingMonitorName ?? UnknownMonitorName} (nicht angeschlossen)");
                    Monitors.Add(option);
                    _selectedMonitor = option;
                }
                else
                {
                    // Gespeicherter Monitor, sonst Hauptbildschirm. Bei nur einem Monitor zeigt die ausgegraute Box diesen.
                    var target = GuestMonitors.Resolve(monitors, savedId)!;
                    _selectedMonitor = Monitors.First(o => o.Id == target.Id);
                }
            }

            IsMonitorSelectable = monitors.Count > 1;
            UpdateMonitorHint();
            OnPropertyChanged(nameof(SelectedMonitor));
        }
        finally
        {
            EndLoading();
        }
    }

    private void UpdateMonitorHint()
    {
        var savedId = _draft.Appearance.GuestMonitorId;
        if (_monitorList.Count == 0)
        {
            // Fehler oder keine Liste: Die Auswahl ist gesperrt, also sagen, warum – und was gespeichert bleibt.
            MonitorHint = "Die angeschlossenen Bildschirme konnten nicht ermittelt werden."
                + (savedId is null ? "" : $" Gespeichert ist „{_draft.Appearance.GuestMonitorName ?? UnknownMonitorName}“.");
        }
        else if (savedId is not null && _monitorList.All(m => m.Id != savedId))
        {
            var name = _draft.Appearance.GuestMonitorName ?? UnknownMonitorName;
            MonitorHint = $"Der gewählte Bildschirm „{name}“ ist nicht angeschlossen. Die Fotobox nutzt bis dahin den Hauptbildschirm.";
        }
        else if (_monitorList.Count == 1)
        {
            MonitorHint = "Schließen Sie einen weiteren Bildschirm an, um die Gastansicht dort anzuzeigen.";
        }
        else
        {
            MonitorHint = "Die Wahl wird mit „Übernehmen“ oder „Speichern & schließen“ gespeichert und wirkt erst nach dem Schließen der Einstellungen. Die Einstellungen und die PIN-Abfrage öffnen auf demselben Bildschirm.";
        }
    }

    // ---------------------------------------------------------------- Drucker

    public ObservableCollection<PrinterOption> Printers { get; } = [];
    public ObservableCollection<PaperOption> PaperSizes { get; } = [];
    public IReadOnlyList<OrientationOption> Orientations { get; }

    public PrinterOption? SelectedPrinter
    {
        get => _selectedPrinter;
        set
        {
            if (value is null || value == _selectedPrinter)
                return;
            _selectedPrinter = value;
            OnPropertyChanged();
            if (!IsLoading)
                Forget(SelectPrinterAsync(value));
        }
    }

    /// <summary>Druckerwechsel: Papiergrößen neu laden, Papierwahl zurücksetzen.</summary>
    public async Task SelectPrinterAsync(PrinterOption printer)
    {
        _draft.Printer.PrinterName = printer.Name;
        _draft.Printer.PaperSizeName = null;
        _draft.Printer.PaperWidthMm = null;
        _draft.Printer.PaperHeightMm = null;
        if (_selectedPrinter != printer)
        {
            _selectedPrinter = printer;
            OnPropertyChanged(nameof(SelectedPrinter));
        }

        await LoadPaperSizesAsync();
        RequestPageSpec();
    }

    public PaperOption? SelectedPaper
    {
        get => _selectedPaper;
        set
        {
            if (value is null || value == _selectedPaper)
                return;
            _selectedPaper = value;
            OnPropertyChanged();
            if (IsLoading)
                return;
            _draft.Printer.PaperSizeName = value.Info?.Name;
            _draft.Printer.PaperWidthMm = value.Info?.WidthMm;
            _draft.Printer.PaperHeightMm = value.Info?.HeightMm;
            RequestPageSpec();
        }
    }

    public OrientationOption SelectedOrientation
    {
        get => _selectedOrientation;
        set
        {
            if (value is null || value == _selectedOrientation)
                return;
            _selectedOrientation = value;
            _draft.Printer.Orientation = value.Orientation;
            OnPropertyChanged();
            RequestPageSpec();
        }
    }

    public int Copies
    {
        get => _draft.Printer.Copies;
        set => SetProperty(_draft.Printer.Copies, value, v => _draft.Printer.Copies = v);
    }

    public int PrintTimeoutSeconds
    {
        get => _draft.Printer.TimeoutSeconds;
        set
        {
            if (SetProperty(_draft.Printer.TimeoutSeconds, value, v => _draft.Printer.TimeoutSeconds = v))
                RaisePrintTimeoutHint();
        }
    }

    public bool IsTestPrinting
    {
        get => _isTestPrinting;
        private set
        {
            if (SetProperty(ref _isTestPrinting, value))
                TestPrintCommand.NotifyCanExecuteChanged();
        }
    }

    public string TestPrintStatus
    {
        get => _testPrintStatus;
        private set => SetProperty(ref _testPrintStatus, value);
    }

    private async Task LoadPrintersAsync()
    {
        IReadOnlyList<PrinterInfo> printers;
        try
        {
            printers = await Task.Run(_print.GetPrinters);
        }
        catch (Exception ex)
        {
            AppLog.Error("Druckerliste konnte nicht geladen werden.", ex);
            printers = [];
        }

        BeginLoading();
        try
        {
            Printers.Clear();
            Printers.Add(new PrinterOption(null, DefaultPrinterDisplay));
            foreach (var p in printers)
                Printers.Add(new PrinterOption(p.Name, p.IsDefault ? $"{p.Name} (Standard)" : p.Name));

            var name = _draft.Printer.PrinterName;
            var current = Printers.FirstOrDefault(p => p.Name == name);
            if (current is null && name is not null)
            {
                // Gespeicherter Drucker ist (gerade) nicht installiert → Auswahl erhalten.
                current = new PrinterOption(name, $"{name} (nicht verfügbar)");
                Printers.Add(current);
            }

            _selectedPrinter = current ?? Printers[0];
            OnPropertyChanged(nameof(SelectedPrinter));
        }
        finally
        {
            EndLoading();
        }

        await LoadPaperSizesAsync();
    }

    private async Task LoadPaperSizesAsync()
    {
        var printerName = _draft.Printer.PrinterName;
        IReadOnlyList<PaperSizeInfo> sizes;
        try
        {
            sizes = await Task.Run(() => _print.GetPaperSizes(printerName));
        }
        catch (Exception ex)
        {
            AppLog.Error("Papiergrößen konnten nicht geladen werden.", ex);
            sizes = [];
        }

        // Inzwischen wurde ein anderer Drucker gewählt: Die verspätete Antwort gehört nicht mehr zur Auswahl.
        if (_disposed || _draft.Printer.PrinterName != printerName)
            return;

        BeginLoading();
        try
        {
            PaperSizes.Clear();
            PaperSizes.Add(new PaperOption(null, DefaultPaperDisplay));
            foreach (var s in sizes)
                PaperSizes.Add(new PaperOption(s, s.DisplayName));

            var name = _draft.Printer.PaperSizeName;
            var current = PaperSizes.FirstOrDefault(p => p.Info?.Name == name && name is not null);
            if (current is null && name is not null)
            {
                var info = new PaperSizeInfo(name, name, _draft.Printer.PaperWidthMm ?? 0, _draft.Printer.PaperHeightMm ?? 0);
                current = new PaperOption(info, $"{name} (nicht verfügbar)");
                PaperSizes.Add(current);
            }

            _selectedPaper = current ?? PaperSizes[0];
            OnPropertyChanged(nameof(SelectedPaper));
        }
        finally
        {
            EndLoading();
        }
    }

    private async Task TestPrintAsync()
    {
        if (_disposed)
            return;

        var ct = _lifetime.Token;
        IsTestPrinting = true;
        TestPrintStatus = "Testseite wird vorbereitet …";
        try
        {
            // Eine Kopie genügt; Entwurf bleibt unverändert.
            var printer = SnapshotPrinter(copies: 1);
            // Nicht auf dem UI-Thread: Bei einem Offline-Drucker kann die Abfrage mehrere Sekunden dauern.
            var page = await GetPageSpecAsync(printer, _draft.Layout.Kind, ct);
            // Rendern muss auf dem UI-Thread laufen; die Fortsetzung nach dem await kommt dorthin zurück.
            var bitmap = _ui.RenderTestPage(page);
            var progress = new Progress<string>(s => TestPrintStatus = s);
            // Hartes Limit wie beim Druck im Fotobetrieb: Druck-Timeout plus Reserve; ein blockierendes Write
            // soll weder den Button dauerhaft sperren noch das Schließen der Admin-Ansicht überdauern.
            var printTask = _print.PrintAsync(bitmap, printer, progress, ct);
            _ = printTask.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            var result = await printTask.WaitAsync(TimeSpan.FromSeconds(printer.TimeoutSeconds + 30), _time, ct);
            TestPrintStatus = result.Outcome switch
            {
                PrintOutcome.Completed => "Testdruck abgeschlossen.",
                PrintOutcome.TimedOut => "Testdruck: Zeitüberschreitung. " + (result.Message ?? string.Empty),
                PrintOutcome.Cancelled => "Testdruck abgebrochen.",
                _ => "Testdruck fehlgeschlagen. " + (result.Message ?? string.Empty),
            };
            TestPrintStatus = TestPrintStatus.TrimEnd();
        }
        catch (TimeoutException)
        {
            AppLog.Warn("Testdruck: keine Antwort des Druckers innerhalb des Zeitlimits.");
            TestPrintStatus = "Testdruck: Zeitüberschreitung. Der Drucker hat nicht geantwortet. Bitte prüfen Sie Drucker und Verbindung.";
        }
        catch (OperationCanceledException)
        {
            TestPrintStatus = "Testdruck abgebrochen.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Testdruck fehlgeschlagen.", ex);
            TestPrintStatus = "Testdruck fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            IsTestPrinting = false;
        }
    }

    // ---------------------------------------------------------------- Speicher

    public bool DateSubfolder
    {
        get => _draft.Storage.DateSubfolder;
        set => SetProperty(_draft.Storage.DateSubfolder, value, v => _draft.Storage.DateSubfolder = v);
    }

    public bool SaveOriginals
    {
        get => _draft.Storage.SaveOriginals;
        set => SetProperty(_draft.Storage.SaveOriginals, value, v => _draft.Storage.SaveOriginals = v);
    }

    public int JpegQuality
    {
        get => _draft.Storage.JpegQuality;
        set
        {
            if (SetProperty(_draft.Storage.JpegQuality, value, v => _draft.Storage.JpegQuality = v))
                OnPropertyChanged(nameof(JpegQualityText));
        }
    }

    public string JpegQualityText => $"{_draft.Storage.JpegQuality} %";

    public string FilePrefix
    {
        get => _draft.Storage.FilePrefix;
        set => SetProperty(_draft.Storage.FilePrefix, value ?? string.Empty, v => _draft.Storage.FilePrefix = v);
    }

    /// <summary>Anzuzeigender Zielordner (Standard: Bilder\Fotobox+).</summary>
    /// <summary>Hinweis auf der Layout-Karte „Fotostreifen“.</summary>
    public string StripPhotosHint => $"{SettingsLimits.StripPhotosMin} bis {SettingsLimits.StripPhotosMax} Fotos untereinander.";

    /// <summary>Erklärung zur erwarteten Druckdauer.</summary>
    public string ExpectedPrintHint =>
        $"So lange läuft der Fortschrittsbalken in der Gastansicht. Das Druck-Timeout liegt automatisch mindestens {SettingsLimits.PrintTimeoutReserveSeconds} s darüber.";

    public string PhotoFolderDisplay => EffectivePhotoFolder;

    public bool IsDefaultPhotoFolder => string.IsNullOrWhiteSpace(_draft.Storage.PhotoFolder);

    private string EffectivePhotoFolder =>
        string.IsNullOrWhiteSpace(_draft.Storage.PhotoFolder)
            ? AppInfo.DefaultPhotoFolder
            : _draft.Storage.PhotoFolder;

    private void ChooseFolder() =>
        ChooseFolderCore(EffectivePhotoFolder, "Speicherordner für die Fotos auswählen",
            folder => _draft.Storage.PhotoFolder = folder, RaiseFolder);

    private void ResetFolder()
    {
        _draft.Storage.PhotoFolder = null;
        RaiseFolder();
    }

    private void RaiseFolder()
    {
        OnPropertyChanged(nameof(PhotoFolderDisplay));
        OnPropertyChanged(nameof(IsDefaultPhotoFolder));
    }

    private Task OpenFolderAsync() => OpenFolderCore(EffectivePhotoFolder, "Foto");

    /// <summary>Ordnerdialog für einen Speicherordner (Fotos oder Videos); ein gewählter Ordner geht an <paramref name="apply"/>.</summary>
    private void ChooseFolderCore(string current, string dialogTitle, Action<string> apply, Action raise)
    {
        var folder = _ui.PickFolder(current, dialogTitle);
        if (string.IsNullOrWhiteSpace(folder))
            return;
        apply(folder);
        raise();
    }

    /// <summary>
    /// Öffnet einen Speicherordner im Explorer. <paramref name="item"/> („Foto“, „Video“) steht im Hinweis, dass der Ordner erst
    /// beim ersten Eintrag angelegt wird.
    /// </summary>
    private async Task OpenFolderCore(string folder, string item)
    {
        // Nur öffnen, was existiert (PhotoStorage/VideoStorage legen den Ordner beim ersten Foto bzw. Video an). Die Abfrage hat ein Timeout.
        var exists = await FolderProbe.ExistsAsync(folder, FolderProbeTimeout);
        if (_disposed)
            return;
        if (exists is null)
        {
            ValidationMessage = "Der Ordner ist nicht erreichbar (Zeitüberschreitung).";
            return;
        }

        if (!exists.Value)
        {
            ValidationMessage = $"Der Ordner existiert noch nicht. Er wird beim ersten {item} angelegt.";
            return;
        }

        try
        {
            _ui.OpenFolder(folder);
        }
        catch (Exception ex)
        {
            AppLog.Error("Ordner konnte nicht geöffnet werden.", ex);
            ValidationMessage = "Der Ordner konnte nicht geöffnet werden.";
        }
    }

    // ---------------------------------------------------------------- Sicherheit

    public string NewPin
    {
        get => _newPin;
        set => SetProperty(ref _newPin, value ?? string.Empty);
    }

    public string RepeatPin
    {
        get => _repeatPin;
        set => SetProperty(ref _repeatPin, value ?? string.Empty);
    }

    public string PinStatus
    {
        get => _pinStatus;
        private set => SetProperty(ref _pinStatus, value);
    }

    private void ChangePin()
    {
        var error = PinRules.GetError(_newPin, _repeatPin);
        if (error is not null)
        {
            PinStatus = error;
            return;
        }

        if (TryApplyPin(out var failure))
            PinStatus = "Die PIN wurde geändert.";
        else
            PinStatus = failure;
    }

    private bool TryApplyPin(out string failure)
    {
        try
        {
            _pin.SetPin(_newPin);
            NewPin = string.Empty;
            RepeatPin = string.Empty;
            PinFieldsCleared?.Invoke();
            failure = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("PIN konnte nicht geändert werden.", ex);
            failure = "Die PIN konnte nicht geändert werden: " + ex.Message;
            return false;
        }
    }

    // ---------------------------------------------------------------- Tasten

    /// <summary>Foto auslösen, Admin-Ansicht öffnen, Abbrechen (in dieser Reihenfolge).</summary>
    public IReadOnlyList<KeyActionEditor> KeyEditors { get; }

    private KeyActionEditor Editor(KeyAction action) => KeyEditors.First(e => e.Action == action);

    /// <summary>Aktion, für die gerade eine Taste aufgenommen wird; null = kein Aufnahmemodus.</summary>
    public KeyActionEditor? CapturingEditor
    {
        get => _capturingEditor;
        private set
        {
            if (!SetProperty(ref _capturingEditor, value))
                return;
            OnPropertyChanged(nameof(IsKeyCaptureActive));
            OnPropertyChanged(nameof(ShowKeyMessageBelow));
            foreach (var editor in KeyEditors)
            {
                editor.IsCapturing = ReferenceEquals(editor, value);
                editor.RefreshCommands();
            }
        }
    }

    public bool IsKeyCaptureActive => _capturingEditor is not null;

    /// <summary>Meldung zur letzten Tastenaufnahme (Fehler oder Hinweis); leer = keine.</summary>
    public string KeyMessage
    {
        get => _keyMessage;
        private set
        {
            if (SetProperty(ref _keyMessage, value))
            {
                OnPropertyChanged(nameof(HasKeyMessage));
                OnPropertyChanged(nameof(ShowKeyMessageBelow));
            }
        }
    }

    public bool HasKeyMessage => _keyMessage.Length > 0;

    /// <summary>Meldung unter der Liste; im Aufnahmemodus steht sie stattdessen im Aufnahmehinweis der Aktion.</summary>
    public bool ShowKeyMessageBelow => HasKeyMessage && !IsKeyCaptureActive;

    /// <summary>Taste der Admin-Ansicht für Hinweistexte, z. B. „Strg+Alt+A“ (mehrere: „… oder …“).</summary>
    public string AdminKeysDisplay => string.Join(" oder ", Editor(KeyAction.Admin).Keys.Select(k => k.DisplayName));

    /// <summary>Die linke Maustaste löst ein Foto aus.</summary>
    public bool TriggerOnLeftClick
    {
        get => _draft.Input.TriggerOnLeftClick;
        set
        {
            if (SetProperty(_draft.Input.TriggerOnLeftClick, value, v => _draft.Input.TriggerOnLeftClick = v))
                RaiseHintPreview();
        }
    }

    /// <summary>Die rechte Maustaste löst ein Foto aus.</summary>
    public bool TriggerOnRightClick
    {
        get => _draft.Input.TriggerOnRightClick;
        set
        {
            if (SetProperty(_draft.Input.TriggerOnRightClick, value, v => _draft.Input.TriggerOnRightClick = v))
                RaiseHintPreview();
        }
    }

    /// <summary>Mausrad-Richtung der Gast-Layoutwahl umkehren (nach oben scrollen = nächstes Layout).</summary>
    public bool InvertMouseWheel
    {
        get => _draft.Input.InvertMouseWheel;
        set => SetProperty(_draft.Input.InvertMouseWheel, value, v => _draft.Input.InvertMouseWheel = v);
    }

    private void LoadKeysFromDraft()
    {
        // Wie beim Laden aus der Datei: ungültige, gesperrte und doppelte Einträge fallen weg, Pflicht-Aktionen behalten eine Taste.
        SettingsNormalizer.NormalizeKeys(_draft.Input);
        Fill(Editor(KeyAction.Trigger), _draft.Input.TriggerKeys);
        Fill(Editor(KeyAction.Admin), _draft.Input.AdminKeys);
        Fill(Editor(KeyAction.Cancel), _draft.Input.CancelCountdownKeys);
        Fill(Editor(KeyAction.VideoStart), _draft.Input.VideoStartKeys);
        Fill(Editor(KeyAction.VideoStop), _draft.Input.VideoStopKeys);

        static void Fill(KeyActionEditor editor, IEnumerable<string> entries)
        {
            editor.Keys.Clear();
            foreach (var entry in entries)
            {
                if (KeyChord.TryParse(entry, out var chord))
                    editor.Add(chord);
            }
        }
    }

    /// <summary>Schreibt die Tasten als kanonische Strings in den Entwurf.</summary>
    private void SyncKeysToDraft()
    {
        static List<string> Texts(KeyActionEditor e) => e.Keys.Select(k => k.Chord.ToString()).ToList();

        _draft.Input.TriggerKeys = Texts(Editor(KeyAction.Trigger));
        _draft.Input.AdminKeys = Texts(Editor(KeyAction.Admin));
        _draft.Input.CancelCountdownKeys = Texts(Editor(KeyAction.Cancel));
        _draft.Input.VideoStartKeys = Texts(Editor(KeyAction.VideoStart));
        _draft.Input.VideoStopKeys = Texts(Editor(KeyAction.VideoStop));
        OnPropertyChanged(nameof(AdminKeysDisplay));
        RaiseHintPreview();   // Die Hinweistexte der Gastansicht nennen die Auslöse- und Video-Tasten.
        OnPropertyChanged(nameof(VideoStartKeyHint));
        OnPropertyChanged(nameof(HasVideoStartKeyHint));
    }

    /// <summary>Startet den Aufnahmemodus für eine Aktion (die nächste Taste im Fenster wird übernommen).</summary>
    public void BeginKeyCapture(KeyActionEditor editor)
    {
        if (!editor.CanAddKey)
            return;
        KeyMessage = string.Empty;
        CapturingEditor = editor;
    }

    public void BeginKeyCapture(KeyAction action) => BeginKeyCapture(Editor(action));

    /// <summary>Beendet den Aufnahmemodus ohne Änderung.</summary>
    public void CancelKeyCapture()
    {
        KeyMessage = string.Empty;
        CapturingEditor = null;
    }

    /// <summary>
    /// Nimmt die gedrückte Taste für die Aktion im Aufnahmemodus auf. Bei Erfolg endet der Aufnahmemodus;
    /// bei Ablehnung bleibt er aktiv (Meldung in <see cref="KeyMessage"/>), damit eine andere Taste gewählt werden kann.
    /// </summary>
    public bool CaptureKey(KeyChord chord)
    {
        var target = _capturingEditor;
        if (target is null)
            return false;

        if (!chord.IsAllowed)
        {
            KeyMessage = "Diese Tastenkombination ist vom System belegt und kann nicht verwendet werden.";
            return false;
        }

        if (target.Contains(chord))
        {
            KeyMessage = $"Diese Taste ist bereits bei „{target.Title}“ vorhanden.";
            return false;
        }

        // Dieselbe Taste darf bei „Video starten“ und „Video beenden“ stehen (schaltet die Aufnahme um); sonst gehört sie einer Aktion.
        var other = KeyEditors.FirstOrDefault(e => !ReferenceEquals(e, target) && !IsVideoPair(e, target) && e.Contains(chord));
        if (other is not null)
        {
            KeyMessage = $"Diese Taste ist bereits ‚{other.Title}‘ zugeordnet.";
            return false;
        }

        if (target.Keys.Count >= SettingsLimits.KeysPerActionMax)
        {
            KeyMessage = $"Für „{target.Title}“ sind höchstens {SettingsLimits.KeysPerActionMax} Tasten möglich.";
            return false;
        }

        target.Add(chord);
        SyncKeysToDraft();
        KeyMessage = string.Empty;
        CapturingEditor = null;
        return true;
    }

    private static bool IsVideoPair(KeyActionEditor a, KeyActionEditor b) =>
        (a.Action is KeyAction.VideoStart or KeyAction.VideoStop) && (b.Action is KeyAction.VideoStart or KeyAction.VideoStop);

    internal void RemoveKey(KeyChordItem item)
    {
        var editor = KeyEditors.FirstOrDefault(e => e.Keys.Contains(item));
        if (editor is null || !editor.CanRemoveKey())
            return;

        editor.Keys.Remove(item);
        SyncKeysToDraft();
        KeyMessage = string.Empty;
    }

    private void ResetKeys()
    {
        CapturingEditor = null;
        // Die Mausrad-Richtung gehört zur Layoutwahl (Tab „Layout“) und bleibt beim Zurücksetzen der Tasten erhalten.
        _draft.Input = new InputSettings { InvertMouseWheel = _draft.Input.InvertMouseWheel };
        LoadKeysFromDraft();
        SyncKeysToDraft();
        OnPropertyChanged(nameof(TriggerOnLeftClick));
        OnPropertyChanged(nameof(TriggerOnRightClick));
        KeyMessage = "Die Tasten wurden auf die Standardbelegung zurückgesetzt. Sie gilt nach „Übernehmen“ oder „Speichern & schließen“.";
    }

    // ---------------------------------------------------------------- Gastansicht

    /// <summary>Eigener Hinweistext mit Platzhaltern; leer = automatischer Text aus den Auslöse-Tasten.</summary>
    public string HintText
    {
        get => _draft.GuestScreen.HintText ?? string.Empty;
        set
        {
            if (SetProperty(_draft.GuestScreen.HintText ?? string.Empty, value ?? string.Empty,
                    v => _draft.GuestScreen.HintText = string.IsNullOrWhiteSpace(v) ? null : v))
                RaiseHintPreview();
        }
    }

    /// <summary>Darstellung des Hinweises (Wahlkarten und Vorschau; gebunden über <c>EnumMatchConverter</c>).</summary>
    public HintStyle HintStyle
    {
        get => _draft.GuestScreen.HintStyle;
        set => SetProperty(_draft.GuestScreen.HintStyle, value, v => _draft.GuestScreen.HintStyle = v);
    }

    /// <summary>Mausradzeile („Mausrad: Layout wechseln · …“) in der Gastansicht anzeigen.</summary>
    public bool ShowWheelHint
    {
        get => _draft.GuestScreen.ShowWheelHint;
        set
        {
            if (SetProperty(_draft.GuestScreen.ShowWheelHint, value, v => _draft.GuestScreen.ShowWheelHint = v))
                RaiseWheelHintPreview();
        }
    }

    /// <summary>Eigener Text der Mausradzeile (wörtlich, ohne Platzhalter); leer = automatischer Text mit dem Layoutnamen.</summary>
    public string WheelHintText
    {
        get => _draft.GuestScreen.WheelHintText ?? string.Empty;
        set
        {
            if (SetProperty(_draft.GuestScreen.WheelHintText ?? string.Empty, value ?? string.Empty,
                    v => _draft.GuestScreen.WheelHintText = string.IsNullOrWhiteSpace(v) ? null : v))
                RaiseWheelHintPreview();
        }
    }

    /// <summary>Darstellung der Mausradzeile.</summary>
    public WheelHintStyle WheelHintStyle
    {
        get => _draft.GuestScreen.WheelHintStyle;
        set => SetProperty(_draft.GuestScreen.WheelHintStyle, value, v => _draft.GuestScreen.WheelHintStyle = v);
    }

    /// <summary>Die Mausradzeile erscheint in der Vorschau: mindestens zwei Layouts zur Auswahl und die Zeile ist eingeschaltet.</summary>
    public bool ShowWheelHintPreview => HasGuestOrder && ShowWheelHint;

    private void RaiseWheelHintPreview()
    {
        OnPropertyChanged(nameof(ShowWheelHintPreview));
        OnPropertyChanged(nameof(HintPreviewLayoutText));
    }

    // ---------------------------------------------------------------- Darstellung (Admin-Ansicht)

    /// <summary>Farbschema des Entwurfs; das Admin-Fenster wendet Änderungen sofort an (Vorschau).</summary>
    public AdminThemeMode AdminTheme => _draft.Appearance.AdminTheme;

    public bool IsAdminThemeSystem
    {
        get => _draft.Appearance.AdminTheme == AdminThemeMode.System;
        set => SetAdminTheme(AdminThemeMode.System, value);
    }

    public bool IsAdminThemeLight
    {
        get => _draft.Appearance.AdminTheme == AdminThemeMode.Light;
        set => SetAdminTheme(AdminThemeMode.Light, value);
    }

    public bool IsAdminThemeDark
    {
        get => _draft.Appearance.AdminTheme == AdminThemeMode.Dark;
        set => SetAdminTheme(AdminThemeMode.Dark, value);
    }

    private void SetAdminTheme(AdminThemeMode mode, bool selected)
    {
        if (!selected || _draft.Appearance.AdminTheme == mode)
            return;
        _draft.Appearance.AdminTheme = mode;
        OnPropertyChanged(nameof(AdminTheme));
        OnPropertyChanged(nameof(IsAdminThemeSystem));
        OnPropertyChanged(nameof(IsAdminThemeLight));
        OnPropertyChanged(nameof(IsAdminThemeDark));
    }

    /// <summary>
    /// Der Hinweistext, wie ihn die Gastansicht mit dem aktuellen Entwurf zeigt: Platzhalter ersetzt durch die im Entwurf
    /// belegten Auslöse-Tasten und die Maus-Einstellung.
    /// </summary>
    public string HintPreviewText =>
        HintTextFormatter.Format(_draft.GuestScreen.HintText, TriggerChords(), TriggerOnLeftClick || TriggerOnRightClick, VideoStartChords());

    /// <summary>Zusatzzeile der Vorschau mit dem Namen des Standard-Layouts (sichtbar bei <see cref="HasGuestOrder"/>).</summary>
    public string HintPreviewLayoutText =>
        GuestLayouts.WheelHintText(_draft.GuestScreen, DefaultGuestLayout, _draft.Layout.StripPhotoCount);

    /// <summary>Hinweise zu den Platzhaltern im Hinweistext (leer = alles in Ordnung).</summary>
    public IReadOnlyList<string> HintProblems =>
        HintTextFormatter.FindProblems(_draft.GuestScreen.HintText, Editor(KeyAction.Trigger).Keys.Count, Editor(KeyAction.VideoStart).Keys.Count);

    public string HintProblemsText => string.Join(Environment.NewLine, HintProblems);

    public bool HasHintProblems => HintProblems.Count > 0;

    private List<KeyChord> TriggerChords() => Editor(KeyAction.Trigger).Keys.Select(k => k.Chord).ToList();

    private List<KeyChord> VideoStartChords() => Editor(KeyAction.VideoStart).Keys.Select(k => k.Chord).ToList();

    // ---- Video-Hinweis (oben im Bild, nur bei aktivem Videomodus)

    /// <summary>Eigener Text der Video-Hinweiszeile mit Platzhaltern; leer = automatischer Text aus den Video-Starttasten.</summary>
    public string VideoHintText
    {
        get => _draft.GuestScreen.VideoHintText ?? string.Empty;
        set
        {
            if (SetProperty(_draft.GuestScreen.VideoHintText ?? string.Empty, value ?? string.Empty,
                    v => _draft.GuestScreen.VideoHintText = string.IsNullOrWhiteSpace(v) ? null : v))
                RaiseHintPreview();
        }
    }

    /// <summary>Darstellung des Video-Hinweises (Wahlkarten und Vorschau; gebunden über <c>EnumMatchConverter</c>).</summary>
    public HintStyle VideoHintStyle
    {
        get => _draft.GuestScreen.VideoHintStyle;
        set => SetProperty(_draft.GuestScreen.VideoHintStyle, value, v => _draft.GuestScreen.VideoHintStyle = v);
    }

    /// <summary>Die Video-Hinweiszeile, wie die Gastansicht sie mit dem aktuellen Entwurf zeigt (leer ohne Video-Starttaste).</summary>
    public string VideoHintPreviewText =>
        HintTextFormatter.FormatVideo(_draft.GuestScreen.VideoHintText, VideoStartChords(), click: false, TriggerChords());

    /// <summary>Die Vorschau zeigt die Video-Zeile: Videomodus aktiv und Text nicht leer (ohne Starttaste entfällt die Zeile).</summary>
    public bool ShowVideoHintPreview => _draft.Video.Enabled && VideoHintPreviewText.Length > 0;

    /// <summary>Hinweise zu den Platzhaltern im Video-Hinweistext (leer = alles in Ordnung).</summary>
    public IReadOnlyList<string> VideoHintProblems =>
        HintTextFormatter.FindVideoProblems(_draft.GuestScreen.VideoHintText, Editor(KeyAction.VideoStart).Keys.Count, Editor(KeyAction.Trigger).Keys.Count);

    public string VideoHintProblemsText => string.Join(Environment.NewLine, VideoHintProblems);

    public bool HasVideoHintProblems => VideoHintProblems.Count > 0;

    /// <summary>Text, Hinweise und Vorschau neu lesen (nach Änderung der Texte, der Auslöse- oder Video-Tasten, der Maus-Einstellung oder des Videomodus).</summary>
    private void RaiseHintPreview()
    {
        OnPropertyChanged(nameof(HintPreviewText));
        OnPropertyChanged(nameof(HintProblems));
        OnPropertyChanged(nameof(HintProblemsText));
        OnPropertyChanged(nameof(HasHintProblems));
        OnPropertyChanged(nameof(VideoHintPreviewText));
        OnPropertyChanged(nameof(ShowVideoHintPreview));
        OnPropertyChanged(nameof(VideoHintProblems));
        OnPropertyChanged(nameof(VideoHintProblemsText));
        OnPropertyChanged(nameof(HasVideoHintProblems));
    }

    // ---------------------------------------------------------------- Video

    private const string DefaultMicrophoneDisplay = "Windows-Standardmikrofon";
    private const string UnknownMicrophoneName = "Gewähltes Mikrofon";

    /// <summary>Länger darf die Mikrofonabfrage nicht dauern (der Kameradienst gibt intern ebenfalls nach 5 s auf).</summary>
    private static readonly TimeSpan MicrophoneListTimeout = TimeSpan.FromSeconds(5);

    private bool _microphonesLoaded;

    /// <summary>Der Videomodus ist aktiv; alle weiteren Einstellungen des Tabs „Video“ sind sonst ausgegraut.</summary>
    public bool VideoEnabled
    {
        get => _draft.Video.Enabled;
        set
        {
            if (!SetProperty(_draft.Video.Enabled, value, v => _draft.Video.Enabled = v))
                return;
            RaiseHintPreview();   // Die Video-Zeile der Vorschau hängt am Videomodus.
            OnPropertyChanged(nameof(VideoStartKeyHint));
            OnPropertyChanged(nameof(HasVideoStartKeyHint));
        }
    }

    /// <summary>Hinweis, wenn der Videomodus aktiv, aber keine Taste für „Video starten“ belegt ist (kein Fehler, nur Hinweis).</summary>
    public string VideoStartKeyHint =>
        _draft.Video.Enabled && Editor(KeyAction.VideoStart).Keys.Count == 0
            ? "Ohne Taste für „Video starten“ können Gäste kein Video aufnehmen."
            : string.Empty;

    public bool HasVideoStartKeyHint => VideoStartKeyHint.Length > 0;

    public int VideoMaxDurationSeconds
    {
        get => _draft.Video.MaxDurationSeconds;
        set
        {
            if (SetProperty(_draft.Video.MaxDurationSeconds, value, v => _draft.Video.MaxDurationSeconds = v))
                OnPropertyChanged(nameof(VideoMaxDurationText));
        }
    }

    /// <summary>Höchstdauer lesbar, z. B. „5:00 Minuten“ bzw. „45 Sekunden“.</summary>
    public string VideoMaxDurationText
    {
        get
        {
            var seconds = _draft.Video.MaxDurationSeconds;
            return seconds < 60
                ? $"Das sind {seconds} Sekunden."
                : $"Das sind {seconds / 60}:{seconds % 60:00} Minuten.";
        }
    }

    public int VideoCountdownSeconds
    {
        get => _draft.Video.CountdownSeconds;
        set => SetProperty(_draft.Video.CountdownSeconds, value, v => _draft.Video.CountdownSeconds = v);
    }

    public int VideoMinDurationSeconds
    {
        get => _draft.Video.MinDurationSeconds;
        set => SetProperty(_draft.Video.MinDurationSeconds, value, v => _draft.Video.MinDurationSeconds = v);
    }

    public bool VideoShowRecDot
    {
        get => _draft.Video.ShowRecDot;
        set => SetProperty(_draft.Video.ShowRecDot, value, v => _draft.Video.ShowRecDot = v);
    }

    public bool VideoShowElapsed
    {
        get => _draft.Video.ShowElapsed;
        set => SetProperty(_draft.Video.ShowElapsed, value, v => _draft.Video.ShowElapsed = v);
    }

    public bool VideoShowRemaining
    {
        get => _draft.Video.ShowRemaining;
        set => SetProperty(_draft.Video.ShowRemaining, value, v => _draft.Video.ShowRemaining = v);
    }

    public int VideoWarningSeconds
    {
        get => _draft.Video.WarningSeconds;
        set => SetProperty(_draft.Video.WarningSeconds, value, v => _draft.Video.WarningSeconds = v);
    }

    public int VideoSuccessDisplaySeconds
    {
        get => _draft.Video.SuccessDisplaySeconds;
        set => SetProperty(_draft.Video.SuccessDisplaySeconds, value, v => _draft.Video.SuccessDisplaySeconds = v);
    }

    /// <summary>Eigener Erfolgstext; leer = <see cref="VideoSuccessTextPlaceholder"/>.</summary>
    public string VideoSuccessText
    {
        get => _draft.Video.SuccessText ?? string.Empty;
        set => SetProperty(_draft.Video.SuccessText ?? string.Empty, value ?? string.Empty,
            v => _draft.Video.SuccessText = string.IsNullOrWhiteSpace(v) ? null : v);
    }

    /// <summary>Der Text, der bei leerem Feld gilt (Platzhalter im Eingabefeld).</summary>
    public string VideoSuccessTextPlaceholder => VideoSettings.DefaultSuccessText;

    public IReadOnlyList<VideoQualityOption> VideoQualities { get; }

    public VideoQualityOption SelectedVideoQuality
    {
        get => _selectedVideoQuality;
        set
        {
            if (value is null || value == _selectedVideoQuality)
                return;
            _selectedVideoQuality = value;
            _draft.Video.Quality = value.Quality;
            OnPropertyChanged();
        }
    }

    /// <summary>Ton aufnehmen (öffnet beim Start der Kamera-Session das Mikrofon).</summary>
    public bool VideoRecordAudio
    {
        get => _draft.Video.RecordAudio;
        set => SetProperty(_draft.Video.RecordAudio, value, v => _draft.Video.RecordAudio = v);
    }

    public bool VideoDateSubfolder
    {
        get => _draft.Video.DateSubfolder;
        set => SetProperty(_draft.Video.DateSubfolder, value, v => _draft.Video.DateSubfolder = v);
    }

    public string VideoFilePrefix
    {
        get => _draft.Video.FilePrefix;
        set
        {
            if (SetProperty(_draft.Video.FilePrefix, value ?? string.Empty, v => _draft.Video.FilePrefix = v))
                OnPropertyChanged(nameof(VideoFileNameExample));
        }
    }

    /// <summary>Beispiel für einen Dateinamen mit dem aktuellen Präfix (leeres Präfix: Standard).</summary>
    public string VideoFileNameExample
    {
        get
        {
            var prefix = string.IsNullOrWhiteSpace(_draft.Video.FilePrefix) ? VideoSettings.DefaultFilePrefix : _draft.Video.FilePrefix;
            return $"Dateien heißen zum Beispiel „{prefix}_20261028_153000.mp4“.";
        }
    }

    // ---- Speicherordner der Videos

    /// <summary>Anzuzeigender Zielordner (Standard: Videos\Fotobox+).</summary>
    public string VideoFolderDisplay => EffectiveVideoFolder;

    public bool IsDefaultVideoFolder => string.IsNullOrWhiteSpace(_draft.Video.Folder);

    private string EffectiveVideoFolder =>
        string.IsNullOrWhiteSpace(_draft.Video.Folder) ? AppInfo.DefaultVideoFolder : _draft.Video.Folder;

    private void ChooseVideoFolder() =>
        ChooseFolderCore(EffectiveVideoFolder, "Speicherordner für die Videos auswählen",
            folder => _draft.Video.Folder = folder, RaiseVideoFolder);

    private void ResetVideoFolder()
    {
        _draft.Video.Folder = null;
        RaiseVideoFolder();
    }

    private void RaiseVideoFolder()
    {
        OnPropertyChanged(nameof(VideoFolderDisplay));
        OnPropertyChanged(nameof(IsDefaultVideoFolder));
    }

    private Task OpenVideoFolderAsync() => OpenFolderCore(EffectiveVideoFolder, "Video");

    // ---- Mikrofon

    public ObservableCollection<MicrophoneOption> Microphones { get; } = [];

    public MicrophoneOption? SelectedMicrophone
    {
        get => _selectedMicrophone;
        set
        {
            if (value is null || value == _selectedMicrophone)
                return;
            _selectedMicrophone = value;
            OnPropertyChanged();
            if (IsLoading)
                return;

            // Nur eine echte Nutzeränderung schreibt in den Entwurf (Id und Name zusammen; Standardmikrofon löscht beide).
            if (value.Id is null)
            {
                _draft.Video.MicrophoneId = null;
                _draft.Video.MicrophoneName = null;
            }
            else
            {
                // Zurück auf das nicht angeschlossene Mikrofon: gespeicherte Wahl wiederherstellen.
                var device = _microphoneList.FirstOrDefault(m => m.Id == value.Id);
                _draft.Video.MicrophoneId = value.Id;
                _draft.Video.MicrophoneName = device?.Name ?? _missingMicrophoneName;
            }

            HideSaveStatusIfDirty();   // OnPropertyChanged kam vor der Änderung des Entwurfs
            UpdateMicrophoneHint();
        }
    }

    /// <summary>Hinweis unter der Mikrofon-Auswahl (null = keiner).</summary>
    public string? MicrophoneHint
    {
        get => _microphoneHint;
        private set
        {
            if (SetProperty(ref _microphoneHint, value))
                OnPropertyChanged(nameof(HasMicrophoneHint));
        }
    }

    public bool HasMicrophoneHint => !string.IsNullOrEmpty(_microphoneHint);

    private async Task LoadMicrophonesAsync()
    {
        IReadOnlyList<MicrophoneDevice> microphones;
        try
        {
            microphones = await Task.Run(() => Camera.GetMicrophonesAsync()).WaitAsync(MicrophoneListTimeout, _time);
        }
        catch (Exception ex)
        {
            AppLog.Error("Mikrofonliste konnte nicht geladen werden.", ex);
            microphones = [];
        }

        if (_disposed)
            return;
        _microphonesLoaded = true;
        RebuildMicrophoneOptions(microphones);
    }

    /// <summary>
    /// Baut Liste und Auswahl aus den gefundenen Mikrofonen und der gespeicherten Wahl im Entwurf auf. Der Entwurf selbst bleibt
    /// dabei unverändert. Erster Eintrag ist immer das Windows-Standardmikrofon (Id null).
    /// </summary>
    private void RebuildMicrophoneOptions(IReadOnlyList<MicrophoneDevice> microphones)
    {
        _microphoneList = microphones;
        var savedId = _draft.Video.MicrophoneId;
        _missingMicrophoneName = _draft.Video.MicrophoneName;

        BeginLoading();
        try
        {
            Microphones.Clear();
            Microphones.Add(new MicrophoneOption(null, DefaultMicrophoneDisplay));
            foreach (var m in microphones)
                Microphones.Add(new MicrophoneOption(m.Id, m.Name));

            var selected = savedId is null ? null : Microphones.FirstOrDefault(o => o.Id == savedId);
            if (selected is null && savedId is not null)
            {
                // Gespeichertes Mikrofon nicht gefunden: Eintrag trotzdem zeigen, damit nichts stillschweigend wechselt.
                // Solange die Liste noch nicht geladen ist, lässt sich „nicht angeschlossen“ nicht behaupten.
                var name = _missingMicrophoneName ?? UnknownMicrophoneName;
                selected = new MicrophoneOption(savedId, _microphonesLoaded ? $"{name} (nicht angeschlossen)" : name);
                Microphones.Add(selected);
            }

            _selectedMicrophone = selected ?? Microphones[0];
            UpdateMicrophoneHint();
            OnPropertyChanged(nameof(SelectedMicrophone));
        }
        finally
        {
            EndLoading();
        }
    }

    private void UpdateMicrophoneHint()
    {
        var savedId = _draft.Video.MicrophoneId;
        if (!_microphonesLoaded)
            MicrophoneHint = null;
        else if (savedId is not null && _microphoneList.All(m => m.Id != savedId))
            MicrophoneHint = $"Das gewählte Mikrofon „{_draft.Video.MicrophoneName ?? UnknownMicrophoneName}“ ist nicht angeschlossen. Bis dahin wird ohne Ton aufgenommen.";
        else if (_microphoneList.Count == 0)
            MicrophoneHint = "Es wurde kein Mikrofon gefunden. Es wird ohne Ton aufgenommen.";
        else
            MicrophoneHint = null;
    }

    // ---------------------------------------------------------------- Fun „Zielerfassung“

    /// <summary>Zielerfassung aktiv; alle weiteren Einstellungen des Tabs „Fun“ sind sonst ausgegraut.</summary>
    public bool FunEnabled
    {
        get => _draft.Fun.Enabled;
        set
        {
            if (SetProperty(_draft.Fun.Enabled, value, v => _draft.Fun.Enabled = v))
                OnPropertyChanged(nameof(FunPersonsStatusText));
        }
    }

    /// <summary>Erkennungsverfahren (Wahlkarten, gebunden über <c>EnumMatchConverter</c>).</summary>
    public DetectionMethod FunMethod
    {
        get => _draft.Fun.Method;
        set => SetProperty(_draft.Fun.Method, value, v => _draft.Fun.Method = v);
    }

    /// <summary>Was die Zielbox umrahmt (Kopf, Oberkörper, ganze Person).</summary>
    public TargetScope FunScope
    {
        get => _draft.Fun.Scope;
        set => SetProperty(_draft.Fun.Scope, value, v => _draft.Fun.Scope = v);
    }

    public TargetBoxStyle FunBoxStyle
    {
        get => _draft.Fun.BoxStyle;
        set => SetProperty(_draft.Fun.BoxStyle, value, v => _draft.Fun.BoxStyle = v);
    }

    public TargetColor FunColor
    {
        get => _draft.Fun.Color;
        set => SetProperty(_draft.Fun.Color, value, v => _draft.Fun.Color = v);
    }

    public LockOnAnimation FunAnimation
    {
        get => _draft.Fun.Animation;
        set => SetProperty(_draft.Fun.Animation, value, v => _draft.Fun.Animation = v);
    }

    public int FunAnalysesPerSecond
    {
        get => _draft.Fun.AnalysesPerSecond;
        set => SetProperty(_draft.Fun.AnalysesPerSecond, value, v => _draft.Fun.AnalysesPerSecond = v);
    }

    public int FunSensitivityPercent
    {
        get => _draft.Fun.SensitivityPercent;
        set => SetProperty(_draft.Fun.SensitivityPercent, value, v => _draft.Fun.SensitivityPercent = v);
    }

    public int FunMinSizePercent
    {
        get => _draft.Fun.MinSizePercent;
        set => SetProperty(_draft.Fun.MinSizePercent, value, v => _draft.Fun.MinSizePercent = v);
    }

    public int FunMaxTargets
    {
        get => _draft.Fun.MaxTargets;
        set => SetProperty(_draft.Fun.MaxTargets, value, v => _draft.Fun.MaxTargets = v);
    }

    public int FunDwellMilliseconds
    {
        get => _draft.Fun.DwellMilliseconds;
        set => SetProperty(_draft.Fun.DwellMilliseconds, value, v => _draft.Fun.DwellMilliseconds = v);
    }

    public int FunTextSeconds
    {
        get => _draft.Fun.TextSeconds;
        set => SetProperty(_draft.Fun.TextSeconds, value, v => _draft.Fun.TextSeconds = v);
    }

    public int FunCooldownSeconds
    {
        get => _draft.Fun.CooldownSeconds;
        set => SetProperty(_draft.Fun.CooldownSeconds, value, v => _draft.Fun.CooldownSeconds = v);
    }

    /// <summary>Ein Personentausch (neue Person bei gleicher Anzahl) löst die Erfassung aus.</summary>
    public bool FunSwapTriggers
    {
        get => _draft.Fun.SwapTriggers;
        set => SetProperty(_draft.Fun.SwapTriggers, value, v => _draft.Fun.SwapTriggers = v);
    }

    /// <summary>Bei gleichbleibender Gruppe nach <see cref="FunRepeatSeconds"/> erneut erfassen.</summary>
    public bool FunRepeatEnabled
    {
        get => _draft.Fun.RepeatEnabled;
        set => SetProperty(_draft.Fun.RepeatEnabled, value, v => _draft.Fun.RepeatEnabled = v);
    }

    public int FunRepeatSeconds
    {
        get => _draft.Fun.RepeatSeconds;
        set => SetProperty(_draft.Fun.RepeatSeconds, value, v => _draft.Fun.RepeatSeconds = v);
    }

    /// <summary>Text bei einer Person (Platzhalter <c>{Anzahl}</c>); leer = <see cref="FunSingularTextPlaceholder"/>.</summary>
    public string FunSingularText
    {
        get => _draft.Fun.SingularText ?? string.Empty;
        set => SetProperty(_draft.Fun.SingularText ?? string.Empty, value ?? string.Empty,
            v => _draft.Fun.SingularText = string.IsNullOrWhiteSpace(v) ? null : v);
    }

    /// <summary>Text bei mehreren Personen (Platzhalter <c>{Anzahl}</c>); leer = <see cref="FunPluralTextPlaceholder"/>.</summary>
    public string FunPluralText
    {
        get => _draft.Fun.PluralText ?? string.Empty;
        set => SetProperty(_draft.Fun.PluralText ?? string.Empty, value ?? string.Empty,
            v => _draft.Fun.PluralText = string.IsNullOrWhiteSpace(v) ? null : v);
    }

    /// <summary>Der Standardtext bei einer Person (Platzhalter im Eingabefeld, solange es leer ist).</summary>
    public string FunSingularTextPlaceholder => FunSettings.DefaultSingularText;

    /// <summary>Der Standardtext bei mehreren Personen.</summary>
    public string FunPluralTextPlaceholder => FunSettings.DefaultPluralText;

    public FunTextPosition FunTextPosition
    {
        get => _draft.Fun.TextPosition;
        set => SetProperty(_draft.Fun.TextPosition, value, v => _draft.Fun.TextPosition = v);
    }

    /// <summary>HUD (Tipp-Effekt) oder Hinweis-Stil.</summary>
    public FunTextStyle FunTextStyle
    {
        get => _draft.Fun.TextStyle;
        set
        {
            if (SetProperty(_draft.Fun.TextStyle, value, v => _draft.Fun.TextStyle = v))
                OnPropertyChanged(nameof(FunHintStyleVisible));
        }
    }

    /// <summary>Die Auswahl des Hinweis-Stils ist nur sichtbar, wenn der Fun-Text im Hinweis-Stil erscheint.</summary>
    public bool FunHintStyleVisible => _draft.Fun.TextStyle == FunTextStyle.Hint;

    /// <summary>Die neun Hinweis-Stile mit deutschen Namen (dieselben wie bei den Wahlkarten im Tab „Gastansicht“).</summary>
    public IReadOnlyList<HintStyleOption> FunHintStyles { get; } =
    [
        new(HintStyle.Pill, "Pille"),
        new(HintStyle.Plain, "Schlicht"),
        new(HintStyle.Banner, "Banner"),
        new(HintStyle.Neon, "Neon"),
        new(HintStyle.Bubble, "Sprechblase"),
        new(HintStyle.Ticker, "Laufschrift"),
        new(HintStyle.Glass, "Glas"),
        new(HintStyle.Note, "Zettel"),
        new(HintStyle.Cinema, "Kino"),
    ];

    /// <summary>Gewählter Hinweis-Stil des Fun-Texts (nur bei <see cref="FunTextStyle"/> = Hinweis-Stil wirksam).</summary>
    public HintStyleOption SelectedFunHintStyle
    {
        get => FunHintStyles.FirstOrDefault(o => o.Style == _draft.Fun.TextHintStyle) ?? FunHintStyles[0];
        set
        {
            if (value is null)
                return;
            SetProperty(_draft.Fun.TextHintStyle, value.Style, v => _draft.Fun.TextHintStyle = v);
        }
    }

    public bool FunSoundEnabled
    {
        get => _draft.Fun.SoundEnabled;
        set => SetProperty(_draft.Fun.SoundEnabled, value, v => _draft.Fun.SoundEnabled = v);
    }

    // ---------------------------------------------------------------- Fun: Live-Vorschau

    private bool _isFunTabSelected;

    /// <summary>Eigene Zielerfassung der Vorschau im Tab „Fun“ (Overlay und Text binden daran, wie die Gastansicht an ihre).</summary>
    public FunOverlayViewModel FunPreview { get; }

    /// <summary>
    /// Der Tab „Fun“ ist ausgewählt. Nur dann läuft die Erkennung der Vorschau (offene Anforderung, Timer); beim Wechsel zu einem
    /// anderen Tab und beim Schließen wird sie freigegeben.
    /// </summary>
    public bool IsFunTabSelected
    {
        get => _isFunTabSelected;
        set
        {
            if (SetProperty(ref _isFunTabSelected, value))
            {
                UpdateFunPreview();
                UpdatePeacePreview();
            }
        }
    }

    private FunPage _funPage = FunPage.Start;

    /// <summary>Welche Seite des Tabs „Fun“ zeigt (Startseite mit Kacheln, Personenerkennung, Peaceerkennung). Reine Ansicht, keine Einstellung.</summary>
    public FunPage FunPage
    {
        get => _funPage;
        set
        {
            if (SetProperty(ref _funPage, value))
            {
                // Personenerkennung und Peaceerkennung teilen sich die Kamera nur über getrennte Anforderungen: Je Seite läuft höchstens eine Vorschau.
                UpdateFunPreview();
                UpdatePeacePreview();
            }
        }
    }

    /// <summary>Wechselt die Seite des Tabs „Fun“ (Parameter: die Zielseite).</summary>
    public IRelayCommand<FunPage> ShowFunPageCommand { get; }

    /// <summary>Die Kamera liefert ein Livebild (sonst zeigt die Vorschau einen Hinweis).</summary>
    public bool FunPreviewCameraRunning => Camera.Status == CameraStatus.Running;

    public bool FunPreviewNoCamera => !FunPreviewCameraRunning;

    /// <summary>Test-Knopf: lässt ein simuliertes Ziel mit den aktuellen Entwurfswerten einrasten (auch ohne Kamera).</summary>
    public IRelayCommand TestFunPreviewCommand { get; }

    public string FunPreviewStatusText => FunPreviewStatus.Text;

    public string FunPreviewWarningText => FunPreviewStatus.Warning ?? string.Empty;

    public bool HasFunPreviewWarning => FunPreviewStatus.Warning is not null;

    private FunPreviewStatusLines FunPreviewStatus =>
        FunPreview.IsActive
            ? FunPreviewStatusFormatter.Format(FunPreview.Status, FunPreview.Snapshot.Count)
            : new FunPreviewStatusLines(string.Empty, null);

    private void TestFunPreview()
    {
        if (_disposed)
            return;
        FunPreview.Simulate(_draft.Fun.Clone(), MirrorPreview);
    }

    // Läuft die Vorschau-Erkennung, gelten die Entwurfswerte sofort (geänderte Einstellungen oder Spiegelung → SetActive erneut).
    private void UpdateFunPreview()
    {
        if (!_disposed)
            FunPreview.SetActive(_isFunTabSelected && _funPage == FunPage.Persons, _draft.Fun, MirrorPreview);
    }

    private static bool AffectsFunPreview(string? propertyName) =>
        string.IsNullOrEmpty(propertyName) // alle Eigenschaften (Standardwerte wiederhergestellt)
        || propertyName == nameof(MirrorPreview)
        || (propertyName.StartsWith("Fun", StringComparison.Ordinal) && !propertyName.StartsWith("FunPreview", StringComparison.Ordinal)
            && propertyName != nameof(FunPersonsStatusText))
        || propertyName.StartsWith("SelectedFun", StringComparison.Ordinal);

    private void OnFunPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FunOverlayViewModel.Status) or nameof(FunOverlayViewModel.Snapshot)
            or nameof(FunOverlayViewModel.IsActive))
        {
            OnPropertyChanged(nameof(FunPreviewStatusText));
            OnPropertyChanged(nameof(FunPreviewWarningText));
            OnPropertyChanged(nameof(HasFunPreviewWarning));
        }
    }

    // ---------------------------------------------------------------- Standardwerte

    /// <summary>
    /// Setzt alle Einstellungen im Entwurf auf die Standardwerte zurück (nach Bestätigung). Die PIN (<see cref="AppSettings.Security"/>)
    /// und die Version bleiben erhalten. Wie jede andere Änderung gilt das Zurücksetzen erst mit „Übernehmen“ bzw. „Speichern &amp; schließen“; „Schließen“ verwirft es.
    /// </summary>
    public async Task ResetToDefaultsAsync()
    {
        if (_disposed)
            return;

        // Wie bei Save darf kein Fehler als unbehandelte Ausnahme aus dem Befehl in den Dispatcher laufen (globaler Handler, Absturz).
        try
        {
            await ResetToDefaultsCoreAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Standardwerte konnten nicht wiederhergestellt werden.", ex);
            ValidationMessage = "Die Standardwerte konnten nicht vollständig wiederhergestellt werden: " + ex.Message;
        }
    }

    private async Task ResetToDefaultsCoreAsync()
    {
        if (!_ui.Confirm("Standardwerte wiederherstellen",
                "Alle Einstellungen außer der PIN werden auf die Standardwerte zurückgesetzt. " +
                "Die Änderung wird erst mit „Übernehmen“ oder „Speichern & schließen“ gespeichert.\nFortfahren?"))
            return;

        var cameraBefore = _draft.Camera;
        var defaults = new AppSettings();
        _draft.Camera = defaults.Camera;
        _draft.Flow = defaults.Flow;
        _draft.Layout = defaults.Layout;
        _draft.Printer = defaults.Printer;
        _draft.Storage = defaults.Storage;
        _draft.Input = defaults.Input;
        _draft.GuestScreen = defaults.GuestScreen;
        _draft.Appearance = defaults.Appearance;
        _draft.Video = defaults.Video;
        _draft.Fun = defaults.Fun;
        _draft.Peace = defaults.Peace;
        // Security (PIN) und Version bleiben bewusst unverändert.
        RebuildMonitorOptions(_monitorList);
        RebuildMicrophoneOptions(_microphoneList);
        _selectedVideoQuality = VideoQualities.First(q => q.Quality == _draft.Video.Quality);

        // UI-Zustand wie bei einer frisch geöffneten Admin-Ansicht mit Standard-Einstellungen.
        CapturingEditor = null;
        KeyMessage = string.Empty;
        ValidationMessage = string.Empty;
        LoadKeysFromDraft();
        SyncKeysToDraft();
        RaiseGuestChoices();
        _selectedOrientation = Orientations.First(o => o.Orientation == _draft.Printer.Orientation);
        SelectCaptionFontFromDraft();
        _selectedDevice = Devices.FirstOrDefault();
        _selectedResolution = Resolutions.FirstOrDefault();
        _selectedPrinter = Printers.FirstOrDefault(p => p.Name is null) ?? Printers.FirstOrDefault();
        _selectedPaper = PaperSizes.FirstOrDefault();
        UpdateOverlay();
        RaiseFolder();
        RaiseVideoFolder();
        RaisePrintTimeoutHint();
        // Alle gebundenen Eigenschaften neu lesen (Werte kommen direkt aus dem Entwurf).
        OnPropertyChanged(string.Empty);

        // Listen, die vom gewählten Gerät bzw. Drucker abhängen, neu laden; die Kamera nur bei geänderten Kameraeinstellungen neu starten.
        if (_selectedDevice is { } device)
            await LoadResolutionsAsync(device.Id);
        await LoadPaperSizesAsync();
        RequestPageSpec();

        if (cameraBefore.RequiresRestartComparedTo(_draft.Camera))
            await RestartCameraAsync();
    }

    // ---------------------------------------------------------------- Validierung / Speichern

    /// <summary>
    /// Prüft den Entwurf (und ggf. eine begonnene PIN-Änderung) ohne Zugriff auf den Speicherordner. Leer = alles in Ordnung.
    /// Der Schreibtest des Ordners gehört zu <see cref="ValidateAsync"/>.
    /// </summary>
    public IReadOnlyList<string> Validate() => ValidateCore(out _);

    /// <summary>
    /// Wie <see cref="Validate"/>, zusätzlich Schreibtest des Speicherordners (nur wenn er sich geändert hat), außerhalb des
    /// UI-Threads und mit Timeout (<see cref="FolderProbeTimeout"/>).
    /// </summary>
    public async Task<IReadOnlyList<string>> ValidateAsync()
    {
        var errors = ValidateCore(out var foldersToProbe);
        if (foldersToProbe.Count > 0)
        {
            // Parallel, damit der schlimmste Fall (zwei hängende Netzpfade) ein Timeout dauert, nicht zwei.
            var folderErrors = await Task.WhenAll(foldersToProbe.Select(f => ProbeFolderAsync(f.Folder, f.Name)));
            errors.AddRange(folderErrors.OfType<string>());
        }

        return errors;
    }

    /// <summary>Ein Speicherordner, der beim Speichern per Schreibtest geprüft wird; <c>Name</c> steht in der Fehlermeldung („Speicherordner“, „Videoordner“).</summary>
    private sealed record FolderToProbe(string Folder, string Name);

    private const string PhotoFolderName = "Speicherordner";
    private const string VideoFolderName = "Videoordner";

    /// <summary>Fehlermeldung zum Speicherordner oder null, wenn er beschreibbar ist.</summary>
    private async Task<string?> ProbeFolderAsync(string folder, string name)
    {
        var check = FolderWriteCheck;
        try
        {
            // Hängt der Zugriff, bleibt nur der Hintergrundthread hängen; die Oberfläche wartet höchstens das Timeout.
            var error = await Task.Run(() => check(folder)).WaitAsync(FolderProbeTimeout, _time);
            return error is null
                ? null
                : $"In den {name} kann nicht geschrieben werden ({error}). Bitte wählen Sie einen anderen Ordner aus.";
        }
        catch (TimeoutException)
        {
            AppLog.Warn($"Schreibtest des {name}s: Zeitüberschreitung.");
            return $"Der {name} ist nicht erreichbar (Zeitüberschreitung). Bitte wählen Sie einen anderen Ordner aus.";
        }
    }

    private List<string> ValidateCore(out List<FolderToProbe> foldersToProbe)
    {
        foldersToProbe = [];
        var errors = new List<string>();

        CheckRange(_draft.Flow.CountdownSeconds, SettingsLimits.CountdownMin, SettingsLimits.CountdownMax, "Der Countdown", errors);
        CheckRange(_draft.Flow.PreviewSeconds, SettingsLimits.PreviewMin, SettingsLimits.PreviewMax, "Die Vorschau-Dauer", errors);
        CheckRange(_draft.Flow.PauseBetweenShotsSeconds, SettingsLimits.PauseMin, SettingsLimits.PauseMax, "Die Pause zwischen den Fotos", errors);
        CheckRange(_draft.Flow.ErrorDisplaySeconds, SettingsLimits.ErrorDisplayMin, SettingsLimits.ErrorDisplayMax, "Die Fehleranzeige-Dauer", errors);
        CheckRange(_draft.Flow.ExpectedPrintSeconds, SettingsLimits.ExpectedPrintMin, SettingsLimits.ExpectedPrintMax, "Die erwartete Druckdauer", errors);

        // Streifen und Overlay zählen auch, wenn sie nur zur Auswahl für Gäste gehören.
        if (ShowStripOptions && _draft.Layout.StripPhotoCount is < SettingsLimits.StripPhotosMin or > SettingsLimits.StripPhotosMax)
            errors.Add($"Ein Fotostreifen besteht aus {SettingsLimits.StripPhotosMin} bis {SettingsLimits.StripPhotosMax} Fotos.");

        if (ShowOverlayOptions)
        {
            var name = _draft.Layout.OverlayFileName;
            if (string.IsNullOrEmpty(name))
                errors.Add(_draft.Layout.Kind == LayoutKind.Overlay
                    ? "Bitte wählen Sie für das Layout „Foto mit Overlay“ eine PNG-Datei aus."
                    : "Bitte wählen Sie für „Foto mit Overlay“ eine PNG-Datei aus oder entfernen Sie es aus der Gastauswahl.");
            else if (!File.Exists(Path.Combine(_store.OverlayDirectory, name)))
                errors.Add("Die gewählte Overlay-Datei wurde nicht gefunden. Bitte wählen Sie sie erneut aus.");
        }

        if ((_draft.Layout.CaptionText?.Length ?? 0) > SettingsLimits.CaptionMaxLength)
            errors.Add($"Die Bildunterschrift darf höchstens {SettingsLimits.CaptionMaxLength} Zeichen lang sein.");

        if ((_draft.GuestScreen.HintText?.Length ?? 0) > SettingsLimits.HintTextMaxLength)
            errors.Add($"Der Hinweistext darf höchstens {SettingsLimits.HintTextMaxLength} Zeichen lang sein.");

        if (_draft.Printer.Copies is < SettingsLimits.CopiesMin or > SettingsLimits.CopiesMax)
            errors.Add($"Die Anzahl der Kopien muss zwischen {SettingsLimits.CopiesMin} und {SettingsLimits.CopiesMax} liegen.");
        if (_draft.Printer.TimeoutSeconds is < SettingsLimits.PrintTimeoutMin or > SettingsLimits.PrintTimeoutMax)
            errors.Add($"Das Druck-Timeout muss zwischen {SettingsLimits.PrintTimeoutMin} und {SettingsLimits.PrintTimeoutMax} Sekunden liegen.");

        foreach (var editor in KeyEditors.Where(e => e.IsRequired && e.Keys.Count == 0))
            errors.Add($"Tasten: Für „{editor.Title}“ muss mindestens eine Taste festgelegt sein.");

        if (_draft.Storage.JpegQuality is < SettingsLimits.JpegQualityMin or > SettingsLimits.JpegQualityMax)
            errors.Add($"Die JPEG-Qualität muss zwischen {SettingsLimits.JpegQualityMin} und {SettingsLimits.JpegQualityMax} Prozent liegen.");

        var prefix = _draft.Storage.FilePrefix;
        if (string.IsNullOrWhiteSpace(prefix))
            errors.Add("Das Dateipräfix darf nicht leer sein.");
        else if (prefix.Length > SettingsLimits.FilePrefixMaxLength || prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            errors.Add($"Das Dateipräfix darf höchstens {SettingsLimits.FilePrefixMaxLength} Zeichen lang sein und keine Zeichen wie \\ / : * ? \" < > | enthalten.");

        // Echter Schreibtest (ValidateAsync), damit ein Fehler nicht erst bei der ersten Session auffällt (und dann nicht
        // gedruckt wird). Nur bei geändertem Ordner: Ein unveränderter, aber gerade nicht erreichbarer Netzpfad würde das
        // Speichern sonst verzögern (der Betrieb fällt dann ohnehin auf Bilder\Fotobox aus, siehe PhotoStorage).
        CheckFolder(_draft.Storage.PhotoFolder, _initialPhotoFolder, PhotoFolderName, errors, foldersToProbe);

        // Videoeinstellungen nur bei aktivem Videomodus: Im ausgegrauten Zustand lassen sich Fehler nicht beheben.
        if (_draft.Video.Enabled)
            ValidateVideo(errors, foldersToProbe);

        ValidatePeace(errors);

        if (_newPin.Length > 0 || _repeatPin.Length > 0)
        {
            var pinError = PinRules.GetError(_newPin, _repeatPin);
            if (pinError is not null)
                errors.Add("Sicherheit: " + pinError);
        }

        return errors;
    }

    /// <summary>Prüft einen gewählten Speicherordner: gültiger Pfad, und bei geändertem Ordner Schreibtest vormerken.</summary>
    private static void CheckFolder(string? folder, string? initialFolder, string name, List<string> errors, List<FolderToProbe> foldersToProbe)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return;

        var folderChanged = !string.Equals(folder, initialFolder, StringComparison.OrdinalIgnoreCase);
        if (!IsUsablePath(folder))
            errors.Add($"Der {name} ist kein gültiger Pfad. Bitte wählen Sie einen Ordner aus.");
        else if (folderChanged)
            foldersToProbe.Add(new FolderToProbe(folder, name));
    }

    private void ValidateVideo(List<string> errors, List<FolderToProbe> foldersToProbe)
    {
        var v = _draft.Video;
        CheckRange(v.MaxDurationSeconds, SettingsLimits.VideoMaxDurationMin, SettingsLimits.VideoMaxDurationMax, "Die Höchstdauer des Videos", errors);
        CheckRange(v.CountdownSeconds, SettingsLimits.VideoCountdownMin, SettingsLimits.VideoCountdownMax, "Der Countdown vor dem Video", errors);
        CheckRange(v.MinDurationSeconds, SettingsLimits.VideoMinDurationMin, SettingsLimits.VideoMinDurationMax, "Die Mindestlänge des Videos", errors);
        CheckRange(v.WarningSeconds, SettingsLimits.VideoWarningMin, SettingsLimits.VideoWarningMax, "Die Warnung vor dem Ende des Videos", errors);
        CheckRange(v.SuccessDisplaySeconds, SettingsLimits.VideoSuccessDisplayMin, SettingsLimits.VideoSuccessDisplayMax, "Die Anzeigedauer der Meldung nach dem Video", errors);

        // Der Normalisierer würde die Mindestlänge stillschweigend verkürzen; die Admin-Ansicht meldet es stattdessen.
        if (v.MinDurationSeconds >= v.MaxDurationSeconds)
            errors.Add($"Die Mindestlänge des Videos ({v.MinDurationSeconds} s) muss kürzer sein als die Höchstdauer ({v.MaxDurationSeconds} s).");

        if ((v.SuccessText?.Length ?? 0) > SettingsLimits.VideoSuccessTextMaxLength)
            errors.Add($"Der Text nach dem Video darf höchstens {SettingsLimits.VideoSuccessTextMaxLength} Zeichen lang sein.");

        if ((_draft.GuestScreen.VideoHintText?.Length ?? 0) > SettingsLimits.HintTextMaxLength)
            errors.Add($"Der Video-Hinweistext darf höchstens {SettingsLimits.HintTextMaxLength} Zeichen lang sein.");

        var prefix = v.FilePrefix;
        if (string.IsNullOrWhiteSpace(prefix))
            errors.Add("Das Dateipräfix der Videos darf nicht leer sein.");
        else if (prefix.Length > SettingsLimits.FilePrefixMaxLength || prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            errors.Add($"Das Dateipräfix der Videos darf höchstens {SettingsLimits.FilePrefixMaxLength} Zeichen lang sein und keine Zeichen wie \\ / : * ? \" < > | enthalten.");

        CheckFolder(v.Folder, _initialVideoFolder, VideoFolderName, errors, foldersToProbe);
    }

    /// <summary>Peaceerkennung: mindestens eine Aktion und alle Zahlen im Wertebereich.</summary>
    private void ValidatePeace(List<string> errors)
    {
        var p = _draft.Peace;
        // Nur bei eingeschalteter Erkennung, sonst ließe sich der Fehler bei ausgegrauten Aktionen nicht beheben.
        if (p.Enabled && !p.HasAnyAction)
            errors.Add("Wählen Sie für die Peaceerkennung mindestens eine Aktion aus.");

        CheckPeaceRange(p.HoldMilliseconds, SettingsLimits.PeaceHoldMillisecondsMin, SettingsLimits.PeaceHoldMillisecondsMax, "Die Haltedauer der Peaceerkennung", "Millisekunden", errors);
        CheckPeaceRange(p.CooldownSeconds, SettingsLimits.PeaceCooldownSecondsMin, SettingsLimits.PeaceCooldownSecondsMax, "Die Sperrzeit der Peaceerkennung", "Sekunden", errors);
        CheckPeaceRange(p.SensitivityPercent, SettingsLimits.PeaceSensitivityMin, SettingsLimits.PeaceSensitivityMax, "Die Empfindlichkeit der Peaceerkennung", "Prozent", errors);
        CheckPeaceRange(p.AnalysesPerSecond, SettingsLimits.PeaceAnalysesPerSecondMin, SettingsLimits.PeaceAnalysesPerSecondMax, "Die Analyserate der Peaceerkennung", "Analysen pro Sekunde", errors);
        CheckPeaceRange(p.SelfDestructSeconds, SettingsLimits.PeaceSelfDestructSecondsMin, SettingsLimits.PeaceSelfDestructSecondsMax, "Die Dauer der Selbstzerstörung", "Sekunden", errors);
        CheckPeaceRange(p.PhotoCountdownSeconds, SettingsLimits.PeacePhotoCountdownMin, SettingsLimits.PeacePhotoCountdownMax, "Der Countdown der Peace-Aktion Foto", "Sekunden", errors);
        CheckPeaceRange(p.MoustacheDurationSeconds, SettingsLimits.PeaceMoustacheDurationMin, SettingsLimits.PeaceMoustacheDurationMax, "Die Anzeigedauer des Schnurrbarts", "Sekunden", errors);
        CheckPeaceRange(p.MoustacheAutoPhotoCountdownSeconds, SettingsLimits.PeaceMoustacheAutoPhotoCountdownMin, SettingsLimits.PeaceMoustacheAutoPhotoCountdownMax, "Der Countdown des Fotos nach dem Schnurrbart", "Sekunden", errors);
    }

    private static void CheckPeaceRange(int value, int min, int max, string label, string unit, List<string> errors)
    {
        if (value < min || value > max)
            errors.Add($"{label} muss zwischen {min} und {max} {unit} liegen.");
    }

    private static void CheckRange(int value, int min, int max, string label, List<string> errors)
    {
        if (value < min || value > max)
            errors.Add($"{label} muss zwischen {min} und {max} Sekunden liegen.");
    }

    private static bool IsUsablePath(string path)
    {
        try
        {
            // Vollständig qualifiziert: „C:fotos“ (laufwerksrelativ) und „\fotos“ (laufwerkslos) hängen vom Arbeitsverzeichnis ab.
            return Path.IsPathFullyQualified(path) && Path.GetFullPath(path).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Prüft und speichert den Entwurf. <paramref name="close"/> = Speichern &amp; schließen, sonst Übernehmen (Fenster bleibt offen).
    /// Ein gemeinsames Flag sperrt gleichzeitige Aufrufe, damit Doppelklick bzw. Übernehmen + Speichern nur einmal schreiben.
    /// </summary>
    private async Task SaveCoreAsync(bool close)
    {
        if (_disposed || _isSaving || _closeRequested)
            return;

        IsSaving = true;
        try
        {
            await SaveCoreCoreAsync(close);
        }
        finally
        {
            IsSaving = false;
        }
    }

    private async Task SaveCoreCoreAsync(bool close)
    {
        // Kein Fehler darf als unbehandelte Ausnahme aus dem Befehl in den Dispatcher laufen (globaler Handler, Absturz).
        IReadOnlyList<string> errors;
        try
        {
            errors = await ValidateAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Einstellungen konnten nicht geprüft werden.", ex);
            ValidationMessage = "Die Einstellungen konnten nicht geprüft werden: " + ex.Message;
            return;
        }

        // Die Admin-Ansicht kann während des Schreibtests geschlossen worden sein.
        if (_disposed || _closeRequested)
            return;

        if (errors.Count > 0)
        {
            ValidationMessage = string.Join(Environment.NewLine, errors);
            return;
        }

        var pinApplied = false;
        try
        {
            // Eine begonnene PIN-Änderung zuerst übernehmen; der Store hält danach den neuen Hash.
            if (_newPin.Length > 0)
            {
                if (!TryApplyPin(out var failure))
                {
                    ValidationMessage = failure;
                    return;
                }

                pinApplied = true;
                PinStatus = "Die PIN wurde geändert.";
            }

            // Die PIN wird nicht im Entwurf bearbeitet → aktuellen Stand aus dem Store übernehmen,
            // sonst würde ein zwischenzeitlich geänderter PIN-Hash überschrieben.
            var security = _store.Current.Security;
            _draft.Security = new SecuritySettings
            {
                PinHash = security.PinHash,
                PinSalt = security.PinSalt,
                PinIterations = security.PinIterations,
            };

            _store.Save(_draft);
        }
        catch (Exception ex)
        {
            AppLog.Error("Einstellungen konnten nicht gespeichert werden.", ex);
            ValidationMessage = "Die Einstellungen konnten nicht gespeichert werden: " + ex.Message
                + (pinApplied ? " Die neue PIN gilt bereits." : string.Empty);
            return;
        }

        SettingsSaved = true;
        ValidationMessage = string.Empty;
        // Neuer Vergleichsstand für die Erkennung ungespeicherter Änderungen (gleich nach dem Speichern, ohne await dazwischen).
        ResetBaseline();

        if (close)
        {
            // Gespeichert: Aufnahmemodus beenden und wartende Entwurfs-Neustarts der Kamera verwerfen. Sonst startete die Kamera
            // noch mit Entwurfswerten, während das Hauptfenster sie schon mit den gespeicherten Werten neu startet.
            _capturingEditor = null;
            Interlocked.Increment(ref _cameraRestartVersion);
            _closeRequested = true;
            CloseRequested?.Invoke(true);
            return;
        }

        // Übernehmen: Das Fenster bleibt offen und die Kamera folgt dem Entwurf weiter (kein Verwerfen wartender Neustarts).
        // Der gespeicherte Ordner gilt als geprüft, damit er beim nächsten Speichern nicht erneut per Schreibtest geprüft wird.
        // Der Videoordner wird nur bei aktivem Videomodus geprüft (ValidateVideo) – sonst bleibt er ungeprüft.
        _initialPhotoFolder = _draft.Storage.PhotoFolder;
        if (_draft.Video.Enabled)
            _initialVideoFolder = _draft.Video.Folder;
        SaveStatusMessage = "Einstellungen gespeichert.";
    }

    private void Close()
    {
        if (_closeRequested || !ConfirmDiscardChanges())
            return;

        _closeRequested = true;
        CloseRequested?.Invoke(false);
    }

    private void RequestExit()
    {
        if (!_ui.Confirm("Fotobox+ beenden",
                "Soll Fotobox+ wirklich beendet werden?\nNicht gespeicherte Änderungen gehen verloren."))
            return;

        ExitRequested = true;
        _closeRequested = true;
        CloseRequested?.Invoke(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        FunPreview.PropertyChanged -= OnFunPreviewPropertyChanged;
        FunPreview.Dispose();
        DisposePeacePreview();
        Camera.StatusChanged -= OnCameraStatusChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
