using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fotobox.Core;

/// <summary>Printed: Druck fertig, Gast wird kurz aufgefordert, das Foto aus dem Drucker zu nehmen.</summary>
public enum BoothState { Starting, Idle, Countdown, Capturing, Preview, Printing, Printed, Error }

/// <summary>
/// Ablaufsteuerung eines Foto-Durchgangs (Countdown → Aufnahme → Layout → Speichern → Vorschau/Druck).
/// Ohne UI testbar; Zeit läuft über <see cref="TimeProvider"/>. Zustandsänderungen werden auf dem
/// SynchronizationContext des Aufrufers von <see cref="TryTrigger"/> ausgelöst (kein ConfigureAwait(false),
/// damit auch Compose auf dem UI-Thread läuft).
/// </summary>
public sealed class BoothController(
    ICameraService camera,
    ILayoutComposer composer,
    IPrintService print,
    IPhotoStorage storage,
    ISettingsStore settings,
    TimeProvider time)
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);
    /// <summary>Obergrenze für die (blockierende) Seitengrößen-Abfrage des Druckers.</summary>
    private static readonly TimeSpan PageSpecTimeout = TimeSpan.FromSeconds(5);
    /// <summary>Obergrenze für das Speichern (z. B. hängende Netzwerkfreigabe, abgezogener USB-Stick).</summary>
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(30);

    private volatile BoothState _state = BoothState.Starting;
    private int _busy;
    private CancellationTokenSource? _sessionCts;

    private int _countdownValue;
    private int _shotIndex;
    private int _shotCount;
    private BitmapSource? _previewImage;
    private string? _statusMessage;
    private string? _errorMessage;
    private SavedPhotoSet? _lastSaved;

    public BoothState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            Raise(StateChanged);
        }
    }

    /// <summary>Aktueller Countdown-Wert (0 = kein Countdown / Pause zwischen Fotos).</summary>
    public int CountdownValue { get => _countdownValue; private set => Set(ref _countdownValue, value); }
    /// <summary>1-basierte Nummer der aktuellen Aufnahme.</summary>
    public int ShotIndex { get => _shotIndex; private set => Set(ref _shotIndex, value); }
    public int ShotCount { get => _shotCount; private set => Set(ref _shotCount, value); }
    public BitmapSource? PreviewImage { get => _previewImage; private set => Set(ref _previewImage, value); }
    public string? StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public string? ErrorMessage { get => _errorMessage; private set => Set(ref _errorMessage, value); }
    public SavedPhotoSet? LastSaved { get => _lastSaved; private set => Set(ref _lastSaved, value); }

    public event EventHandler? StateChanged;
    /// <summary>Alle übrigen Eigenschaften (Countdown, Fotonummer, Status …) haben sich geändert.</summary>
    public event EventHandler? Changed;
    /// <summary>Unmittelbar vor der Aufnahme: weißen Blitz anzeigen.</summary>
    public event EventHandler? FlashRequested;

    /// <summary>Beendet die Startphase (Starting → Idle). Ob die Kamera läuft, prüft <see cref="TryTrigger"/> separat.</summary>
    public void MarkReady()
    {
        if (_state == BoothState.Starting)
            State = BoothState.Idle;
    }

    /// <summary>Startet einen Durchgang. Nur im Zustand Idle und bei laufender Kamera.</summary>
    public bool TryTrigger()
    {
        if (_state != BoothState.Idle || camera.Status != CameraStatus.Running)
            return false;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return false;

        var cts = new CancellationTokenSource();
        _sessionCts = cts;
        var cfg = settings.Current;

        ErrorMessage = null;
        StatusMessage = null;
        PreviewImage = null;
        CountdownValue = 0;
        ShotIndex = 0;
        ShotCount = 0;
        State = BoothState.Countdown;

        _ = RunSessionAsync(cfg, cts);
        return true;
    }

    /// <summary>Bricht einen laufenden Durchgang ab (z. B. beim Beenden der App).</summary>
    public void Cancel()
    {
        try { _sessionCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task RunSessionAsync(AppSettings cfg, CancellationTokenSource cts)
    {
        try
        {
            string? error;
            try
            {
                error = await RunPhasesAsync(cfg, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                AppLog.Info("Durchgang abgebrochen.");
                error = null;
            }
            catch (Exception ex)
            {
                AppLog.Error("Unerwarteter Fehler im Foto-Durchgang.", ex);
                error = "Es ist ein unerwarteter Fehler aufgetreten. Bitte versuche es noch einmal.";
            }

            if (error is not null)
            {
                AppLog.Warn($"Durchgang mit Fehler beendet: {error}");
                ErrorMessage = error;
                PreviewImage = null;
                CountdownValue = 0;
                State = BoothState.Error;
                try { await DelayAsync(cfg.Flow.ErrorDisplaySeconds, cts.Token); }
                catch (Exception) { /* Abbruch/Fehler beim Warten: trotzdem zurück zu Idle */ }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Fehler bei der Fehleranzeige.", ex);
        }
        finally
        {
            try
            {
                PreviewImage = null;
                CountdownValue = 0;
                StatusMessage = null;
                State = BoothState.Idle;
            }
            finally
            {
                _sessionCts = null;
                cts.Dispose();
                Volatile.Write(ref _busy, 0);
            }
        }
    }

    /// <summary>Führt alle Phasen aus. Rückgabe: Fehlermeldung für den Gast oder null bei Erfolg.</summary>
    private async Task<string?> RunPhasesAsync(AppSettings cfg, CancellationToken ct)
    {
        var count = Math.Max(1, composer.GetRequiredPhotoCount(cfg.Layout));
        ShotCount = count;
        var photos = new List<BitmapSource>(count);

        for (var i = 0; i < count; i++)
        {
            ShotIndex = i + 1;

            if (i > 0)
            {
                CountdownValue = 0;
                State = BoothState.Countdown;
                await DelayAsync(cfg.Flow.PauseBetweenShotsSeconds, ct);
            }

            State = BoothState.Countdown;
            for (var v = Math.Max(0, cfg.Flow.CountdownSeconds); v >= 1; v--)
            {
                CountdownValue = v;
                await Task.Delay(Second, time, ct);
            }

            CountdownValue = 0;
            State = BoothState.Capturing;
            Raise(FlashRequested);

            try
            {
                var photo = await camera.CapturePhotoAsync(ct);
                photos.Add(cfg.Camera.MirrorPhoto ? MirrorPhoto(photo) : photo);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Error("Aufnahme fehlgeschlagen.", ex);
                return "Die Kamera hat kein Bild geliefert. Bitte versuche es noch einmal.";
            }
        }

        var timestamp = time.GetLocalNow().DateTime;

        // Die Abfrage blockiert intern (Druckserver/Treiber) und läuft deshalb abseits des UI-Threads mit Timeout.
        // Die Seitengröße bestimmt auch bei deaktiviertem Druck die Auflösung des Layouts.
        PageSpec page;
        try
        {
            var pageTask = Task.Run(() => print.GetPageSpec(cfg.Printer, cfg.Layout.Kind));
            ObserveFault(pageTask);
            page = await pageTask.WaitAsync(PageSpecTimeout, time, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            AppLog.Warn("Seitengröße wurde nicht rechtzeitig ermittelt, verwende Standard.");
            page = PageSpec.Default;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Seitengröße konnte nicht ermittelt werden, verwende Standard.", ex);
            page = PageSpec.Default;
        }

        BitmapSource layout;
        try
        {
            layout = composer.Compose(photos, cfg.Layout, page, settings.OverlayDirectory, timestamp);
        }
        catch (Exception ex)
        {
            AppLog.Error("Layout konnte nicht erstellt werden.", ex);
            return "Das Foto konnte nicht zusammengestellt werden. Bitte versuche es noch einmal.";
        }

        try
        {
            var saveTask = storage.SaveAsync(layout, photos, cfg.Storage, timestamp, ct);
            ObserveFault(saveTask);
            LastSaved = await saveTask.WaitAsync(SaveTimeout, time, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            AppLog.Error("Speichern hat das Zeitlimit überschritten.");
            return "Das Foto konnte nicht gespeichert werden. Bitte wende dich an das Personal.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Foto konnte nicht gespeichert werden.", ex);
            return "Das Foto konnte nicht gespeichert werden. Bitte wende dich an das Personal.";
        }

        // Vorschau; der Druck läuft parallel dazu.
        PreviewImage = layout;
        Task<PrintResult>? printTask = cfg.Flow.PrintingEnabled
            ? PrintGuardedAsync(layout, cfg.Printer, ct)
            : null;
        State = BoothState.Preview;

        await DelayAsync(cfg.Flow.PreviewSeconds, ct);

        if (printTask is null)
            return null;

        if (!printTask.IsCompleted)
        {
            StatusMessage ??= "Wird gedruckt…";
            State = BoothState.Printing;
        }

        var result = await printTask;
        ct.ThrowIfCancellationRequested();
        if (result.Success)
            return null;

        var detail = !string.IsNullOrWhiteSpace(result.Message)
            ? result.Message
            : result.Outcome switch
            {
                PrintOutcome.TimedOut => "Zeitüberschreitung beim Drucken.",
                PrintOutcome.Cancelled => "Der Druck wurde abgebrochen.",
                _ => "Unbekannter Fehler.",
            };
        return $"Das Foto wurde gespeichert, aber der Druck ist fehlgeschlagen: {detail}";
    }

    /// <summary>Druckt mit hartem Timeout (Druckerzeitlimit + Reserve); wirft nie.</summary>
    private async Task<PrintResult> PrintGuardedAsync(BitmapSource page, PrinterSettings printer, CancellationToken ct)
    {
        var limit = TimeSpan.FromSeconds(Math.Max(SettingsLimits.PrintTimeoutMin, printer.TimeoutSeconds) + 30);
        using var printCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progress = new Progress<string>(message => StatusMessage = message);
        Task<PrintResult>? task = null;
        try
        {
            task = print.PrintAsync(page, printer, progress, printCts.Token);
            return await task.WaitAsync(limit, time, ct);
        }
        catch (TimeoutException)
        {
            printCts.Cancel();
            _ = task?.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return new PrintResult(PrintOutcome.TimedOut);
        }
        catch (OperationCanceledException)
        {
            return new PrintResult(PrintOutcome.Cancelled);
        }
        catch (Exception ex)
        {
            AppLog.Error("Druck wirft eine Ausnahme.", ex);
            return new PrintResult(PrintOutcome.Failed, ex.Message);
        }
    }

    /// <summary>Beobachtet eine ggf. verwaiste Aufgabe, damit ihre Ausnahme nie als UnobservedTaskException endet.</summary>
    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

    private Task DelayAsync(int seconds, CancellationToken ct) =>
        seconds > 0 ? Task.Delay(TimeSpan.FromSeconds(seconds), time, ct) : Task.CompletedTask;

    private static BitmapSource MirrorPhoto(BitmapSource source)
    {
        if (!source.IsFrozen && source.CanFreeze)
            source.Freeze();
        var mirrored = new TransformedBitmap(source, new ScaleTransform(-1, 1));
        if (mirrored.CanFreeze)
            mirrored.Freeze();
        return mirrored;
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(Changed);
    }

    private void Raise(EventHandler? handler)
    {
        try { handler?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { AppLog.Error("Ausnahme in einem Ereignis-Handler des BoothController.", ex); }
    }
}
