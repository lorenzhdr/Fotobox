using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fotobox.Core;
using Fotobox.Services.Storage;

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
    internal GuestLayoutItem(GuestLayout layout, int position, string name, bool canMoveUp, bool canMoveDown,
        Action moveUp, Action moveDown)
    {
        Layout = layout;
        Position = position;
        Name = name;
        MoveUpCommand = new RelayCommand(moveUp, () => canMoveUp);
        MoveDownCommand = new RelayCommand(moveDown, () => canMoveDown);
    }

    public GuestLayout Layout { get; }
    public int Position { get; }
    public string PositionText => Position + ".";
    public string Name { get; }
    public IRelayCommand MoveUpCommand { get; }
    public IRelayCommand MoveDownCommand { get; }
}

/// <summary>
/// Admin-Ansicht: bearbeitet einen Entwurf (Clone) der Einstellungen. Gespeichert wird erst über
/// <see cref="SaveCommand"/>; Abbrechen lässt den Store unberührt.
/// </summary>
public sealed class AdminViewModel : ObservableObject, IDisposable
{
    private const string DefaultPrinterDisplay = "Windows-Standarddrucker";
    private const string DefaultPaperDisplay = "Standard des Druckers";

    private readonly ISettingsStore _store;
    private readonly IPinService _pin;
    private readonly IPrintService _print;
    private readonly IAdminUi _ui;
    private readonly SynchronizationContext? _sync = SynchronizationContext.Current;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _cameraGate = new(1, 1);
    // Wird beim Schließen der Admin-Ansicht (Dispose) abgebrochen, damit ein hängender Testdruck sie nicht überdauert.
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AppSettings _draft;

    private bool _loading;
    private bool _disposed;
    private int _cameraRestartVersion;

    private CameraDevice? _selectedDevice;
    private ResolutionOption? _selectedResolution;
    private PrinterOption? _selectedPrinter;
    private PaperOption? _selectedPaper;
    private OrientationOption _selectedOrientation;

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

    public AdminViewModel(ISettingsStore store, IPinService pin, ICameraService camera, IPrintService print, IAdminUi ui,
        TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _store = store;
        _pin = pin;
        Camera = camera;
        _print = print;
        _ui = ui;
        _draft = store.Current.Clone();

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
            new KeyActionEditor(this, KeyAction.CancelCountdown, "Countdown abbrechen",
                "Bricht einen laufenden Countdown ab.", isRequired: false),
        ];
        LoadKeysFromDraft();
        RebuildGuestOrder();

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(Cancel);
        ExitCommand = new RelayCommand(RequestExit);
        ChooseOverlayCommand = new RelayCommand(ChooseOverlay);
        RemoveOverlayCommand = new RelayCommand(RemoveOverlay);
        ChooseFolderCommand = new RelayCommand(ChooseFolder);
        ResetFolderCommand = new RelayCommand(ResetFolder);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        ChangePinCommand = new RelayCommand(ChangePin);
        TestPrintCommand = new AsyncRelayCommand(TestPrintAsync, () => !IsTestPrinting);
        CancelKeyCaptureCommand = new RelayCommand(CancelKeyCapture);
        ResetKeysCommand = new RelayCommand(ResetKeys);
        ResetToDefaultsCommand = new AsyncRelayCommand(ResetToDefaultsAsync);

        Camera.StatusChanged += OnCameraStatusChanged;
        UpdateOverlay();
    }

    // ---------------------------------------------------------------- Allgemein

    /// <summary>Der bearbeitete Entwurf (nicht der Store-Zustand).</summary>
    public AppSettings Draft => _draft;

    /// <summary>Kamera für die Live-Vorschau.</summary>
    public ICameraService Camera { get; }

    public IRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand ExitCommand { get; }
    public IRelayCommand ChooseOverlayCommand { get; }
    public IRelayCommand RemoveOverlayCommand { get; }
    public IRelayCommand ChooseFolderCommand { get; }
    public IRelayCommand ResetFolderCommand { get; }
    public IRelayCommand OpenFolderCommand { get; }
    public IRelayCommand ChangePinCommand { get; }
    public IAsyncRelayCommand TestPrintCommand { get; }
    public IRelayCommand CancelKeyCaptureCommand { get; }
    public IRelayCommand ResetKeysCommand { get; }
    public IAsyncRelayCommand ResetToDefaultsCommand { get; }

    /// <summary>Wird ausgelöst, wenn das Fenster geschlossen werden soll (Parameter: true = Speichern).</summary>
    public event Action<bool>? CloseRequested;

    /// <summary>Die PIN-Eingabefelder sollen geleert werden.</summary>
    public event Action? PinFieldsCleared;

    public bool SettingsSaved { get; private set; }

    public bool ExitRequested { get; private set; }

    /// <summary>Kamera wurde mit Entwurfs-Einstellungen neu gestartet (bei Abbrechen zurücksetzen).</summary>
    public bool CameraTouched { get; private set; }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
                OnPropertyChanged(nameof(HasValidationMessage));
        }
    }

    public bool HasValidationMessage => _validationMessage.Length > 0;

    // ---------------------------------------------------------------- Initialisierung

    /// <summary>Lädt Geräte, Auflösungen, Drucker und Papiergrößen. Wirft nicht.</summary>
    public async Task InitializeAsync()
    {
        // Die Schriftenliste lädt parallel zu Kamera und Drucker (und nur einmal pro Prozess).
        var fontsTask = FontCatalog.GetAsync();
        // Seitengröße ebenfalls im Hintergrund; sie hängt nur vom Entwurf ab, nicht von den geladenen Listen.
        var pageTask = RefreshPageSpecAsync();

        await LoadCameraListsAsync();
        await LoadPrintersAsync();
        await ApplyFontsAsync(fontsTask);
        UpdateOverlay();
        RaiseCameraStatus();
        await pageTask;
    }

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
            if (!_loading)
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
            if (_loading)
                return;
            _draft.Camera.ResolutionWidth = value.Resolution?.Width;
            _draft.Camera.ResolutionHeight = value.Resolution?.Height;
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
            var settings = new CameraSettings
            {
                DeviceId = _draft.Camera.DeviceId,
                ResolutionWidth = _draft.Camera.ResolutionWidth,
                ResolutionHeight = _draft.Camera.ResolutionHeight,
                MirrorPreview = _draft.Camera.MirrorPreview,
                MirrorPhoto = _draft.Camera.MirrorPhoto,
            };
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
    /// Stellt nach „Abbrechen“ die gespeicherten Kamera-Einstellungen wieder her. Läuft über dasselbe Gate wie
    /// <see cref="RestartCameraAsync"/> und verwirft noch wartende Entwurfs-Neustarts.
    /// </summary>
    public async Task RestoreCameraAsync(CameraSettings saved)
    {
        Interlocked.Increment(ref _cameraRestartVersion);
        await _cameraGate.WaitAsync();
        try
        {
            await Camera.StartAsync(saved);
        }
        catch (Exception ex)
        {
            AppLog.Error("Kamera konnte nach dem Abbrechen nicht neu gestartet werden.", ex);
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

        _loading = true;
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
            _loading = false;
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

        // Nur eine vollständige Liste des eigenen Geräts darf die gespeicherte Auflösung für ungültig erklären. Fehlt die Liste
        // (Abfrage fehlgeschlagen, z. B. weil die Kamera gerade belegt ist) oder gehört sie zu einem anderen Gerät als dem
        // gespeicherten, bleibt der Entwurf unverändert – sonst würde „Speichern ohne Änderung“ die Auflösung löschen
        // und einen unnötigen Kamera-Neustart auslösen.
        var listIsAuthoritative = resolutions.Count > 0
            && (_draft.Camera.DeviceId is null || _draft.Camera.DeviceId == deviceId);

        _loading = true;
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
            _loading = false;
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

    private void RaiseCameraStatus() => OnPropertyChanged(nameof(CameraStatusText));

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
        _guestOrder = list
            .Select((layout, i) => new GuestLayoutItem(
                layout, i + 1, GuestLayouts.DisplayName(layout, photoCount),
                canMoveUp: i > 0, canMoveDown: i < list.Count - 1,
                moveUp: () => MoveGuestLayoutUp(layout), moveDown: () => MoveGuestLayoutDown(layout)))
            .ToArray();
    }

    private void RaiseGuestOrder()
    {
        RebuildGuestOrder();
        OnPropertyChanged(nameof(GuestOrder));
        OnPropertyChanged(nameof(HasGuestOrder));
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
            if (!_loading)
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

        _loading = true;
        try
        {
            CaptionFonts = list;
            _selectedCaptionFont = match;
            RaiseCaptionFont();
        }
        finally
        {
            _loading = false;
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

            // Vorab prüfen, ob die Datei ein gültiges Bild ist.
            _ = LoadBitmap(sourcePath);

            // Eine vorhandene Overlay-Datei (ggf. das aktive Overlay) wird nie überschrieben: „Abbrechen“ in der
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
                bitmap = LoadBitmap(path);
                info = $"{name} ({bitmap.PixelWidth} × {bitmap.PixelHeight} Pixel)";
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

    private static BitmapImage LoadBitmap(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
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
            if (!_loading)
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
            if (_loading)
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

        _loading = true;
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
            _loading = false;
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

        _loading = true;
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
            _loading = false;
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
            _ = printTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
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

    /// <summary>Anzuzeigender Zielordner (Standard: Bilder\Fotobox).</summary>
    public string PhotoFolderDisplay => EffectivePhotoFolder;

    public bool IsDefaultPhotoFolder => string.IsNullOrWhiteSpace(_draft.Storage.PhotoFolder);

    private string EffectivePhotoFolder =>
        string.IsNullOrWhiteSpace(_draft.Storage.PhotoFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Fotobox")
            : _draft.Storage.PhotoFolder;

    private void ChooseFolder()
    {
        var folder = _ui.PickFolder(EffectivePhotoFolder);
        if (string.IsNullOrWhiteSpace(folder))
            return;
        _draft.Storage.PhotoFolder = folder;
        RaiseFolder();
    }

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

    private void OpenFolder()
    {
        try
        {
            _ui.OpenFolder(EffectivePhotoFolder);
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

    /// <summary>Foto auslösen, Admin-Ansicht öffnen, Countdown abbrechen (in dieser Reihenfolge).</summary>
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
            OnPropertyChanged(nameof(KeyCaptureHint));
            foreach (var editor in KeyEditors)
            {
                editor.IsCapturing = ReferenceEquals(editor, value);
                editor.RefreshCommands();
            }
        }
    }

    public bool IsKeyCaptureActive => _capturingEditor is not null;

    public string KeyCaptureHint => _capturingEditor is null
        ? string.Empty
        : $"Drücken Sie jetzt die gewünschte Taste für „{_capturingEditor.Title}“ (auch mit Strg/Alt/Umschalt) …";

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
        set => SetProperty(_draft.Input.TriggerOnLeftClick, value, v => _draft.Input.TriggerOnLeftClick = v);
    }

    /// <summary>Die rechte Maustaste löst ein Foto aus.</summary>
    public bool TriggerOnRightClick
    {
        get => _draft.Input.TriggerOnRightClick;
        set => SetProperty(_draft.Input.TriggerOnRightClick, value, v => _draft.Input.TriggerOnRightClick = v);
    }

    private void LoadKeysFromDraft()
    {
        // Wie beim Laden aus der Datei: ungültige, gesperrte und doppelte Einträge fallen weg, Pflicht-Aktionen behalten eine Taste.
        SettingsNormalizer.NormalizeKeys(_draft.Input);
        Fill(Editor(KeyAction.Trigger), _draft.Input.TriggerKeys);
        Fill(Editor(KeyAction.Admin), _draft.Input.AdminKeys);
        Fill(Editor(KeyAction.CancelCountdown), _draft.Input.CancelCountdownKeys);

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
        _draft.Input.CancelCountdownKeys = Texts(Editor(KeyAction.CancelCountdown));
        OnPropertyChanged(nameof(AdminKeysDisplay));
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

        var other = KeyEditors.FirstOrDefault(e => !ReferenceEquals(e, target) && e.Contains(chord));
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
        _draft.Input = new InputSettings();
        LoadKeysFromDraft();
        SyncKeysToDraft();
        OnPropertyChanged(nameof(TriggerOnLeftClick));
        OnPropertyChanged(nameof(TriggerOnRightClick));
        KeyMessage = "Die Tasten wurden auf die Standardbelegung zurückgesetzt. Sie gilt nach „Speichern“.";
    }

    // ---------------------------------------------------------------- Standardwerte

    /// <summary>
    /// Setzt alle Einstellungen im Entwurf auf die Standardwerte zurück (nach Bestätigung). Die PIN (<see cref="AppSettings.Security"/>)
    /// und die Version bleiben erhalten. Wie jede andere Änderung gilt das Zurücksetzen erst mit „Speichern“; „Abbrechen“ verwirft es.
    /// </summary>
    public async Task ResetToDefaultsAsync()
    {
        if (_disposed)
            return;

        if (!_ui.Confirm("Standardwerte wiederherstellen",
                "Alle Einstellungen außer der PIN werden auf die Standardwerte zurückgesetzt. " +
                "Die Änderung wird erst mit „Speichern“ übernommen.\nFortfahren?"))
            return;

        var cameraBefore = _draft.Camera;
        var defaults = new AppSettings();
        _draft.Camera = defaults.Camera;
        _draft.Flow = defaults.Flow;
        _draft.Layout = defaults.Layout;
        _draft.Printer = defaults.Printer;
        _draft.Storage = defaults.Storage;
        _draft.Input = defaults.Input;
        // Security (PIN) und Version bleiben bewusst unverändert.

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
        RaisePrintTimeoutHint();
        // Alle gebundenen Eigenschaften neu lesen (Werte kommen direkt aus dem Entwurf).
        OnPropertyChanged(string.Empty);

        // Listen, die vom gewählten Gerät bzw. Drucker abhängen, neu laden; die Kamera nur bei geänderten Kameraeinstellungen neu starten.
        if (_selectedDevice is { } device)
            await LoadResolutionsAsync(device.Id);
        await LoadPaperSizesAsync();
        RequestPageSpec();

        var cameraChanged = cameraBefore.DeviceId != _draft.Camera.DeviceId
            || cameraBefore.ResolutionWidth != _draft.Camera.ResolutionWidth
            || cameraBefore.ResolutionHeight != _draft.Camera.ResolutionHeight
            || cameraBefore.MirrorPreview != _draft.Camera.MirrorPreview
            || cameraBefore.MirrorPhoto != _draft.Camera.MirrorPhoto;
        if (cameraChanged)
            await RestartCameraAsync();
    }

    // ---------------------------------------------------------------- Validierung / Speichern

    /// <summary>Prüft den Entwurf (und ggf. eine begonnene PIN-Änderung). Leer = alles in Ordnung.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        CheckRange(_draft.Flow.CountdownSeconds, SettingsLimits.CountdownMin, SettingsLimits.CountdownMax, "Der Countdown", errors);
        CheckRange(_draft.Flow.PreviewSeconds, SettingsLimits.PreviewMin, SettingsLimits.PreviewMax, "Die Vorschau-Dauer", errors);
        CheckRange(_draft.Flow.PauseBetweenShotsSeconds, SettingsLimits.PauseMin, SettingsLimits.PauseMax, "Die Pause zwischen den Fotos", errors);
        CheckRange(_draft.Flow.ErrorDisplaySeconds, SettingsLimits.ErrorDisplayMin, SettingsLimits.ErrorDisplayMax, "Die Fehleranzeige-Dauer", errors);
        CheckRange(_draft.Flow.ExpectedPrintSeconds, SettingsLimits.ExpectedPrintMin, SettingsLimits.ExpectedPrintMax, "Die erwartete Druckdauer", errors);

        // Streifen und Overlay zählen auch, wenn sie nur zur Auswahl für Gäste gehören.
        if (ShowStripOptions && _draft.Layout.StripPhotoCount is < SettingsLimits.StripPhotosMin or > SettingsLimits.StripPhotosMax)
            errors.Add("Ein Fotostreifen besteht aus 2 bis 4 Fotos.");

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

        if ((_draft.Layout.CaptionText?.Length ?? 0) > 80)
            errors.Add("Die Bildunterschrift darf höchstens 80 Zeichen lang sein.");

        if (_draft.Printer.Copies is < SettingsLimits.CopiesMin or > SettingsLimits.CopiesMax)
            errors.Add($"Die Anzahl der Kopien muss zwischen {SettingsLimits.CopiesMin} und {SettingsLimits.CopiesMax} liegen.");
        if (_draft.Printer.TimeoutSeconds is < SettingsLimits.PrintTimeoutMin or > SettingsLimits.PrintTimeoutMax)
            errors.Add($"Das Druck-Timeout muss zwischen {SettingsLimits.PrintTimeoutMin} und {SettingsLimits.PrintTimeoutMax} Sekunden liegen.");

        foreach (var editor in KeyEditors.Where(e => e.IsRequired && e.Keys.Count == 0))
            errors.Add($"Tasten: Für „{editor.Title}“ muss mindestens eine Taste festgelegt sein.");

        if (_draft.Storage.JpegQuality is < SettingsLimits.JpegQualityMin or > SettingsLimits.JpegQualityMax)
            errors.Add("Die JPEG-Qualität muss zwischen 50 und 100 Prozent liegen.");

        var prefix = _draft.Storage.FilePrefix;
        if (string.IsNullOrWhiteSpace(prefix))
            errors.Add("Das Dateipräfix darf nicht leer sein.");
        else if (prefix.Length > 40 || prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            errors.Add("Das Dateipräfix darf höchstens 40 Zeichen lang sein und keine Zeichen wie \\ / : * ? \" < > | enthalten.");

        var folder = _draft.Storage.PhotoFolder;
        if (!string.IsNullOrWhiteSpace(folder))
        {
            // Echter Schreibtest, damit ein Fehler nicht erst bei der ersten Session auffällt (und dann nicht gedruckt wird).
            if (!IsUsablePath(folder))
                errors.Add("Der Speicherordner ist kein gültiger Pfad. Bitte wählen Sie einen Ordner aus.");
            else if (!FolderProbe.TryEnsureWritable(folder, out var folderError))
                errors.Add($"In den Speicherordner kann nicht geschrieben werden ({folderError}). Bitte wählen Sie einen anderen Ordner aus.");
        }

        if (_newPin.Length > 0 || _repeatPin.Length > 0)
        {
            var pinError = PinRules.GetError(_newPin, _repeatPin);
            if (pinError is not null)
                errors.Add("Sicherheit: " + pinError);
        }

        return errors;
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
            return Path.IsPathRooted(path) && Path.GetFullPath(path).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Save()
    {
        // Kein Fehler darf als unbehandelte Ausnahme aus dem Befehl in den Dispatcher laufen (globaler Handler, Absturz).
        if (_disposed || SettingsSaved)
            return;

        IReadOnlyList<string> errors;
        try
        {
            errors = Validate();
        }
        catch (Exception ex)
        {
            AppLog.Error("Einstellungen konnten nicht geprüft werden.", ex);
            ValidationMessage = "Die Einstellungen konnten nicht geprüft werden: " + ex.Message;
            return;
        }

        if (errors.Count > 0)
        {
            ValidationMessage = string.Join(Environment.NewLine, errors);
            return;
        }

        try
        {
            // Eine begonnene PIN-Änderung zuerst übernehmen; der Store hält danach den neuen Hash.
            if (_newPin.Length > 0 && !TryApplyPin(out var failure))
            {
                ValidationMessage = failure;
                return;
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
            ValidationMessage = "Die Einstellungen konnten nicht gespeichert werden: " + ex.Message;
            return;
        }

        SettingsSaved = true;
        ValidationMessage = string.Empty;

        // Gespeichert: Aufnahmemodus beenden und wartende Entwurfs-Neustarts der Kamera verwerfen. Sonst startete die Kamera
        // noch mit Entwurfswerten, während das Hauptfenster sie schon mit den gespeicherten Werten neu startet.
        _capturingEditor = null;
        Interlocked.Increment(ref _cameraRestartVersion);
        CloseRequested?.Invoke(true);
    }

    private void Cancel() => CloseRequested?.Invoke(false);

    private void RequestExit()
    {
        if (!_ui.Confirm("Fotobox beenden",
                "Soll die Fotobox wirklich beendet werden?\nNicht gespeicherte Änderungen gehen verloren."))
            return;

        ExitRequested = true;
        CloseRequested?.Invoke(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Camera.StatusChanged -= OnCameraStatusChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
