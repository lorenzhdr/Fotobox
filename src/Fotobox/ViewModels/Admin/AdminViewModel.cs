using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fotobox.Core;

namespace Fotobox.ViewModels.Admin;

/// <summary>Eintrag der Auflösungs-Auswahl; <c>Resolution == null</c> bedeutet „Automatisch“.</summary>
public sealed record ResolutionOption(CameraResolution? Resolution, string Display);

/// <summary>Eintrag der Drucker-Auswahl; <c>Name == null</c> bedeutet Windows-Standarddrucker.</summary>
public sealed record PrinterOption(string? Name, string Display);

/// <summary>Eintrag der Papiergrößen-Auswahl; <c>Info == null</c> bedeutet Standard des Druckers.</summary>
public sealed record PaperOption(PaperSizeInfo? Info, string Display);

public sealed record OrientationOption(PageOrientation Orientation, string Display);

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
    private readonly SemaphoreSlim _cameraGate = new(1, 1);
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

    public AdminViewModel(ISettingsStore store, IPinService pin, ICameraService camera, IPrintService print, IAdminUi ui)
    {
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
        await LoadCameraListsAsync();
        await LoadPrintersAsync();
        UpdateOverlay();
        RaiseCameraStatus();
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
                _ = SelectDeviceAsync(value);
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
            _ = RestartCameraAsync();
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
                // Gespeicherte Auflösung gibt es für dieses Gerät nicht → Automatisch anzeigen und speichern.
                _draft.Camera.ResolutionWidth = null;
                _draft.Camera.ResolutionHeight = null;
                current = Resolutions[0];
            }

            _selectedResolution = current;
            OnPropertyChanged(nameof(SelectedResolution));
        }
        finally
        {
            _loading = false;
        }
    }

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
    }

    public int StripPhotoCount
    {
        get => _draft.Layout.StripPhotoCount;
        set => SetProperty(_draft.Layout.StripPhotoCount, value, v => _draft.Layout.StripPhotoCount = v);
    }

    public string CaptionText
    {
        get => _draft.Layout.CaptionText ?? string.Empty;
        set => SetProperty(_draft.Layout.CaptionText ?? string.Empty, value,
            v => _draft.Layout.CaptionText = string.IsNullOrWhiteSpace(v) ? null : v);
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
            var page = GetPageSpecSafe(LayoutKind.Overlay);
            var size = string.Create(CultureInfo.GetCultureInfo("de-DE"),
                $"Empfohlene Größe: {page.PixelWidth} × {page.PixelHeight} Pixel ({page.WidthMm:0.#} × {page.HeightMm:0.#} mm bei {page.Dpi} dpi). ");
            return size + "Transparente Bereiche lassen das Foto durchscheinen.";
        }
    }

    private PageSpec GetPageSpecSafe(LayoutKind kind)
    {
        try
        {
            return _print.GetPageSpec(_draft.Printer, kind);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Seitengröße konnte nicht ermittelt werden.", ex);
            return PageSpec.Default;
        }
    }

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

            Directory.CreateDirectory(_store.OverlayDirectory);
            var fileName = Path.GetFileName(sourcePath);
            var target = Path.Combine(_store.OverlayDirectory, fileName);
            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Copy(sourcePath, target, overwrite: true);

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
                _ = SelectPrinterAsync(value);
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
        OnPropertyChanged(nameof(OverlayHint));
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
            OnPropertyChanged(nameof(OverlayHint));
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
            OnPropertyChanged(nameof(OverlayHint));
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
        set => SetProperty(_draft.Printer.TimeoutSeconds, value, v => _draft.Printer.TimeoutSeconds = v);
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
        IsTestPrinting = true;
        TestPrintStatus = "Testseite wird vorbereitet …";
        try
        {
            // Eine Kopie genügt; Entwurf bleibt unverändert.
            var printer = new PrinterSettings
            {
                PrinterName = _draft.Printer.PrinterName,
                PaperSizeName = _draft.Printer.PaperSizeName,
                PaperWidthMm = _draft.Printer.PaperWidthMm,
                PaperHeightMm = _draft.Printer.PaperHeightMm,
                Orientation = _draft.Printer.Orientation,
                Copies = 1,
                TimeoutSeconds = _draft.Printer.TimeoutSeconds,
            };
            var page = _print.GetPageSpec(printer, _draft.Layout.Kind);
            var bitmap = _ui.RenderTestPage(page);
            var progress = new Progress<string>(s => TestPrintStatus = s);
            var result = await _print.PrintAsync(bitmap, printer, progress);
            TestPrintStatus = result.Outcome switch
            {
                PrintOutcome.Completed => "Testdruck abgeschlossen.",
                PrintOutcome.TimedOut => "Testdruck: Zeitüberschreitung. " + (result.Message ?? string.Empty),
                PrintOutcome.Cancelled => "Testdruck abgebrochen.",
                _ => "Testdruck fehlgeschlagen. " + (result.Message ?? string.Empty),
            };
            TestPrintStatus = TestPrintStatus.TrimEnd();
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

    // ---------------------------------------------------------------- Validierung / Speichern

    /// <summary>Prüft den Entwurf (und ggf. eine begonnene PIN-Änderung). Leer = alles in Ordnung.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        CheckRange(_draft.Flow.CountdownSeconds, SettingsLimits.CountdownMin, SettingsLimits.CountdownMax, "Der Countdown", errors);
        CheckRange(_draft.Flow.PreviewSeconds, SettingsLimits.PreviewMin, SettingsLimits.PreviewMax, "Die Vorschau-Dauer", errors);
        CheckRange(_draft.Flow.PauseBetweenShotsSeconds, SettingsLimits.PauseMin, SettingsLimits.PauseMax, "Die Pause zwischen den Fotos", errors);
        CheckRange(_draft.Flow.ErrorDisplaySeconds, SettingsLimits.ErrorDisplayMin, SettingsLimits.ErrorDisplayMax, "Die Fehleranzeige-Dauer", errors);

        if (_draft.Layout.Kind == LayoutKind.Strip && _draft.Layout.StripPhotoCount is < SettingsLimits.StripPhotosMin or > SettingsLimits.StripPhotosMax)
            errors.Add("Ein Fotostreifen besteht aus 2 bis 4 Fotos.");

        if (_draft.Layout.Kind == LayoutKind.Overlay)
        {
            var name = _draft.Layout.OverlayFileName;
            if (string.IsNullOrEmpty(name))
                errors.Add("Bitte wählen Sie für das Layout „Foto mit Overlay“ eine PNG-Datei aus.");
            else if (!File.Exists(Path.Combine(_store.OverlayDirectory, name)))
                errors.Add("Die gewählte Overlay-Datei wurde nicht gefunden. Bitte wählen Sie sie erneut aus.");
        }

        if ((_draft.Layout.CaptionText?.Length ?? 0) > 80)
            errors.Add("Die Bildunterschrift darf höchstens 80 Zeichen lang sein.");

        if (_draft.Printer.Copies is < SettingsLimits.CopiesMin or > SettingsLimits.CopiesMax)
            errors.Add($"Die Anzahl der Kopien muss zwischen {SettingsLimits.CopiesMin} und {SettingsLimits.CopiesMax} liegen.");
        if (_draft.Printer.TimeoutSeconds is < SettingsLimits.PrintTimeoutMin or > SettingsLimits.PrintTimeoutMax)
            errors.Add($"Das Druck-Timeout muss zwischen {SettingsLimits.PrintTimeoutMin} und {SettingsLimits.PrintTimeoutMax} Sekunden liegen.");

        if (_draft.Storage.JpegQuality is < SettingsLimits.JpegQualityMin or > SettingsLimits.JpegQualityMax)
            errors.Add("Die JPEG-Qualität muss zwischen 50 und 100 Prozent liegen.");

        var prefix = _draft.Storage.FilePrefix;
        if (string.IsNullOrWhiteSpace(prefix))
            errors.Add("Das Dateipräfix darf nicht leer sein.");
        else if (prefix.Length > 40 || prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            errors.Add("Das Dateipräfix darf höchstens 40 Zeichen lang sein und keine Zeichen wie \\ / : * ? \" < > | enthalten.");

        var folder = _draft.Storage.PhotoFolder;
        if (!string.IsNullOrWhiteSpace(folder) && !IsUsablePath(folder))
            errors.Add("Der Speicherordner ist kein gültiger Pfad. Bitte wählen Sie einen Ordner aus.");

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
        var errors = Validate();
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
    }
}
