using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Fotobox.Core;
using Fotobox.Services.Camera;

namespace Fotobox.ViewModels.Booth;

/// <summary>Bindbare Sicht auf <see cref="BoothController"/> und Kamera für die Gastansicht.</summary>
public sealed class BoothViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _cameraGate = new(1, 1);
    private int _lastCountdown;
    private bool _isAdminOpen;

    public BoothViewModel(BoothController controller, ICameraService camera, ISettingsStore settings)
    {
        Controller = controller;
        Camera = camera;
        Settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;

        controller.StateChanged += (_, _) => OnUi(Refresh);
        controller.Changed += (_, _) => OnUi(Refresh);
        controller.FlashRequested += (_, _) => OnUi(() => FlashRequested?.Invoke(this, EventArgs.Empty));
        camera.StatusChanged += (_, _) => OnUi(Refresh);
        settings.SettingsChanged += (_, _) => OnUi(Refresh);
    }

    public BoothController Controller { get; }
    public ICameraService Camera { get; }
    public ISettingsStore Settings { get; }

    /// <summary>Wird bei jedem neuen Countdown-Wert (&gt; 0) ausgelöst – für die Zahlen-Animation.</summary>
    public event EventHandler? CountdownTicked;
    /// <summary>Weißer Blitz unmittelbar vor der Aufnahme (UI-Thread).</summary>
    public event EventHandler? FlashRequested;

    public bool MirrorPreview => Settings.Current.Camera.MirrorPreview;

    public bool IsIdle => Controller.State == BoothState.Idle;
    public bool IsCountdown => Controller.State == BoothState.Countdown;
    public string CountdownText => Controller.CountdownValue > 0 ? Controller.CountdownValue.ToString() : "";
    /// <summary>Zwischen zwei Streifenfotos: kein Countdown-Wert, nächste Aufnahme folgt.</summary>
    public bool IsPause => IsCountdown && Controller.CountdownValue == 0;
    public bool ShowShotInfo => Controller.ShotCount > 1 && Controller.State is BoothState.Countdown or BoothState.Capturing;
    public string ShotInfoText => ShowShotInfo ? $"Foto {Controller.ShotIndex} von {Controller.ShotCount}" : "";

    /// <summary>Layout-Vorschau bzw. Druckanzeige sichtbar.</summary>
    public bool ShowResult => IsPreview || IsPrinting;
    public bool IsPreview => Controller.State == BoothState.Preview;
    public BitmapSource? PreviewImage => Controller.PreviewImage;

    public bool IsPrinting => Controller.State == BoothState.Printing;
    /// <summary>Foto wird zusammengestellt/gespeichert (nach der letzten Aufnahme).</summary>
    public bool IsProcessing => Controller.State == BoothState.Capturing && Controller.ShotIndex >= Controller.ShotCount;
    public string StatusText => IsPrinting
        ? Controller.StatusMessage ?? "Wird gedruckt…"
        : IsProcessing ? "Einen Moment, dein Foto wird erstellt…" : "";
    public bool ShowStatusBanner => IsPrinting || IsProcessing;

    public bool IsError => Controller.State == BoothState.Error;
    public string ErrorText => Controller.ErrorMessage ?? "";

    public bool HasCameraProblem =>
        Controller.State is BoothState.Idle or BoothState.Starting &&
        Camera.Status is CameraStatus.NoDevice or CameraStatus.AccessDenied or CameraStatus.Error;

    public bool IsCameraStarting =>
        Controller.State is BoothState.Idle or BoothState.Starting &&
        Camera.Status is CameraStatus.Starting or CameraStatus.Stopped;

    public string CameraProblemTitle => Camera.Status switch
    {
        CameraStatus.AccessDenied => CameraMessages.AccessDeniedTitle,
        CameraStatus.NoDevice => CameraMessages.NoDeviceTitle,
        _ => CameraMessages.UnavailableTitle,
    };

    /// <summary>
    /// Handlungsorientierter Text unter dem Titel: die Dienstmeldung, soweit sie mehr sagt als der Titel,
    /// sonst ein fester Hinweis. Der Wiederhol-Hinweis steht nur im XAML.
    /// </summary>
    public string CameraProblemText
    {
        get
        {
            var detail = DetailBeyondTitle(Camera.StatusMessage, CameraProblemTitle);
            if (detail is not null)
                return detail;

            return Camera.Status switch
            {
                CameraStatus.AccessDenied => CameraMessages.AccessDeniedHint,
                CameraStatus.NoDevice => CameraMessages.NoDeviceHint,
                CameraStatus.Error => CameraMessages.UnavailableHint,
                _ => "",
            };
        }
    }

    /// <summary>Hinweis „Drücke die Leertaste“ – nur wenn ein Foto auch wirklich möglich ist.</summary>
    public bool ShowHint => IsIdle && Camera.Status == CameraStatus.Running;

    /// <summary>Die modale Admin-Ansicht (inklusive PIN-Abfrage) ist offen; das Hauptfenster setzt das Flag.</summary>
    public bool IsAdminOpen
    {
        get => _isAdminOpen;
        set
        {
            if (_isAdminOpen == value) return;
            _isAdminOpen = value;
            OnPropertyChanged(nameof(IsAdminOpen));
            OnPropertyChanged(nameof(CanOpenAdmin));
        }
    }

    /// <summary>Admin-Ansicht darf geöffnet werden: im Leerlauf, beim Start und im Fehlerzustand, nicht mitten im Ablauf.</summary>
    public bool CanOpenAdmin =>
        !IsAdminOpen && Controller.State is BoothState.Idle or BoothState.Starting or BoothState.Error;

    /// <summary>Ein Kamerastart/-neustart dieser Ansicht läuft gerade.</summary>
    public bool IsCameraBusy => _cameraGate.CurrentCount == 0;

    /// <summary>
    /// Löst einen Durchgang aus. Gesperrt, solange die Admin-Ansicht offen ist oder die Kamera gerade (neu) startet;
    /// alles Weitere (Idle, Kamera läuft) prüft der <see cref="BoothController"/>.
    /// </summary>
    public bool TryTrigger() => !IsAdminOpen && !IsCameraBusy && Controller.TryTrigger();

    /// <summary>Beim Laden des Fensters: Controller freigeben und Kamera starten.</summary>
    public async Task InitializeAsync()
    {
        Controller.MarkReady();
        Refresh();
        await StartCameraAsync();
    }

    /// <summary>Startet die Kamera mit den aktuellen Einstellungen neu (z. B. nach Admin-Änderungen).</summary>
    public Task RestartCameraAsync() => StartCameraAsync();

    /// <summary>Bricht einen Ablauf ab und stoppt die Kamera (blockiert max. 3 s).</summary>
    public void Shutdown()
    {
        Controller.Cancel();
        try
        {
            // Auf dem Threadpool ausführen, damit kein UI-Thread-Deadlock entsteht.
            if (!Task.Run(() => Camera.StopAsync()).Wait(TimeSpan.FromSeconds(3)))
                AppLog.Warn("Kamera konnte nicht rechtzeitig gestoppt werden.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Fehler beim Stoppen der Kamera.", ex);
        }
    }

    private async Task StartCameraAsync()
    {
        await _cameraGate.WaitAsync();
        try
        {
            await Camera.StartAsync(Settings.Current.Camera);
        }
        catch (Exception ex)
        {
            AppLog.Error("Kamera konnte nicht gestartet werden.", ex);
        }
        finally
        {
            _cameraGate.Release();
            Refresh();
        }
    }

    /// <summary>Dienstmeldung ohne den (ggf. vorangestellten) Titel; null, wenn nichts darüber hinaus gesagt wird.</summary>
    private static string? DetailBeyondTitle(string? message, string title)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var text = message.Trim();
        var heading = title.TrimEnd('.', ' ');
        if (text.StartsWith(heading, StringComparison.OrdinalIgnoreCase))
            text = text[heading.Length..].TrimStart('.', ':', ' ');

        return text.Length == 0 ? null : text;
    }

    private void Refresh()
    {
        OnPropertyChanged(string.Empty);

        var value = Controller.CountdownValue;
        if (value != _lastCountdown)
        {
            _lastCountdown = value;
            if (value > 0)
                CountdownTicked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
            action();
        else
            _dispatcher.BeginInvoke(action);
    }
}
