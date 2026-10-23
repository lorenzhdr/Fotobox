using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Fotobox.Core;

namespace Fotobox.ViewModels.Booth;

/// <summary>Bindbare Sicht auf <see cref="BoothController"/> und Kamera für die Gastansicht.</summary>
public sealed class BoothViewModel : ObservableObject
{
    private static readonly TimeSpan CameraRetryInterval = TimeSpan.FromSeconds(5);

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _retryTimer;
    private readonly SemaphoreSlim _cameraGate = new(1, 1);
    private int _lastCountdown;

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

        _retryTimer = new DispatcherTimer(CameraRetryInterval, DispatcherPriority.Background, OnRetryTick, _dispatcher);
        _retryTimer.Stop();
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
        CameraStatus.AccessDenied => "Kein Zugriff auf die Kamera",
        CameraStatus.NoDevice => "Keine Kamera gefunden",
        _ => "Die Kamera ist nicht verfügbar",
    };

    public string CameraProblemText
    {
        get
        {
            var message = Camera.StatusMessage;
            switch (Camera.Status)
            {
                case CameraStatus.AccessDenied:
                    const string hint = "Einstellungen → Datenschutz → Kamera → Desktop-Apps Zugriff erlauben";
                    var text = string.IsNullOrWhiteSpace(message)
                        ? "Die Fotobox darf nicht auf die Kamera zugreifen."
                        : message;
                    return text.Contains("Datenschutz", StringComparison.OrdinalIgnoreCase)
                        ? text
                        : $"{text}\n{hint}";
                case CameraStatus.NoDevice:
                    return string.IsNullOrWhiteSpace(message)
                        ? "Bitte die Kamera anschließen. Die Verbindung wird automatisch erneut versucht."
                        : message;
                case CameraStatus.Error:
                    return string.IsNullOrWhiteSpace(message)
                        ? "Beim Starten der Kamera ist ein Fehler aufgetreten. Es wird automatisch erneut versucht."
                        : message;
                default:
                    return "";
            }
        }
    }

    /// <summary>Hinweis „Drücke die Leertaste“ – nur wenn ein Foto auch wirklich möglich ist.</summary>
    public bool ShowHint => IsIdle && Camera.Status == CameraStatus.Running;

    /// <summary>Admin-Ansicht darf geöffnet werden (kein Ablauf läuft).</summary>
    public bool CanOpenAdmin => Controller.State is BoothState.Idle or BoothState.Starting;

    /// <summary>Beim Laden des Fensters: Controller freigeben, Kamera starten, Wiederholversuche aktivieren.</summary>
    public async Task InitializeAsync()
    {
        Controller.MarkReady();
        _retryTimer.Start();
        Refresh();
        await StartCameraAsync();
    }

    /// <summary>Startet die Kamera mit den aktuellen Einstellungen neu (z. B. nach Admin-Änderungen).</summary>
    public Task RestartCameraAsync() => StartCameraAsync();

    /// <summary>Stoppt Wiederholversuche, bricht einen Ablauf ab und stoppt die Kamera (blockiert max. 3 s).</summary>
    public void Shutdown()
    {
        _retryTimer.Stop();
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

    private async void OnRetryTick(object? sender, EventArgs e)
    {
        if (Camera.Status is not (CameraStatus.NoDevice or CameraStatus.Error or CameraStatus.AccessDenied))
            return;
        if (!CanOpenAdmin || _cameraGate.CurrentCount == 0)
            return;

        AppLog.Info($"Kamera-Neustartversuch (Status {Camera.Status}).");
        try { await StartCameraAsync(); }
        catch (Exception ex) { AppLog.Error("Kamera-Neustartversuch fehlgeschlagen.", ex); }
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
