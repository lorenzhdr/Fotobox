using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fotobox.Core;

/// <summary>
/// Printed: Druck fertig, Gast wird kurz aufgefordert, das Foto aus dem Drucker zu nehmen.
/// PrintCancelled: Gast hat den Druck abgebrochen (bzw. der Abbruch ging nicht mehr), kurze ruhige Meldung statt Fehler.
/// VideoCountdown → Recording → VideoSaving → VideoSaved: Videodurchgang (Fehler wie beim Foto über Error zurück nach Idle).
/// </summary>
public enum BoothState { Starting, Idle, Countdown, Capturing, Preview, Printing, Printed, Error, PrintCancelled, VideoCountdown, Recording, VideoSaving, VideoSaved }

/// <summary>
/// Ablaufsteuerung eines Foto-Durchgangs (Countdown → Aufnahme → Layout → Speichern → Vorschau/Druck → „Fertig“).
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
    /// <summary>Reserve des harten Druck-Limits über dem Druck-Timeout der Einstellungen (der Druckdienst bricht vorher von selbst ab).</summary>
    private static readonly TimeSpan PrintHardLimitReserve = TimeSpan.FromSeconds(30);
    /// <summary>So lange bleibt die „Fertig“-Meldung nach erfolgreichem Druck stehen.</summary>
    public static readonly TimeSpan PrintedDisplay = TimeSpan.FromSeconds(5);
    /// <summary>So lange bleibt die Meldung „Druck abgebrochen“ (bzw. „Abbrechen nicht möglich“) stehen.</summary>
    public static readonly TimeSpan PrintCancelledDisplay = TimeSpan.FromSeconds(3);
    /// <summary>So lange wartet der Controller nach einem Abbruchwunsch auf den Druckdienst; danach gilt der Abbruch als gescheitert.</summary>
    public static readonly TimeSpan PrintCancelTimeout = TimeSpan.FromSeconds(10);
    /// <summary>Takt, in dem <see cref="Changed"/> während des Drucks den Fortschritt meldet.</summary>
    private static readonly TimeSpan ProgressTick = TimeSpan.FromMilliseconds(250);

    private volatile BoothState _state = BoothState.Starting;
    private int _busy;
    private int _settingsHooked;
    private CancellationTokenSource? _sessionCts;

    // Druckfortschritt: wird bei jedem Lesen aus der Zeit berechnet, der Takt löst nur Changed aus.
    private DateTimeOffset _printStart;
    private volatile bool _printActive;
    private volatile bool _printDone;
    private int _expectedPrintSeconds = new FlowSettings().ExpectedPrintSeconds;
    // Abbruchwunsch des Gastes für den Druck des laufenden Durchgangs (getrennt vom Sitzungs-Token, das den ganzen
    // Durchgang beendet) und der zugehörige Druck, damit ein bereits fertiger Druck nicht mehr „abgebrochen“ wird.
    private CancellationTokenSource? _printCancelCts;
    private volatile Task<PrintResult>? _printTask;

    // Seitengröße: zuletzt erfolgreich ermittelter Wert je Schlüssel (Rückfall bei Zeitüberschreitung)
    // und die laufende Abfrage je Schlüssel. Vorabruf und Durchgang teilen sich dieselbe Abfrage, damit sich bei
    // hängendem Spooler keine blockierten Threads stapeln (je Schlüssel höchstens eine Abfrage zugleich).
    private readonly ConcurrentDictionary<PageSpecKey, PageSpec> _lastGoodPage = new();
    private readonly ConcurrentDictionary<PageSpecKey, Task<PageSpec>> _pageQueries = new();

    private int _countdownValue;
    private int _shotIndex;
    private int _shotCount;
    private BitmapSource? _previewImage;
    private string? _statusMessage;
    private string? _errorMessage;
    private string? _printCancelTitle;
    private string? _printCancelText;
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
    /// <summary>Überschrift der Meldung im Zustand <see cref="BoothState.PrintCancelled"/>.</summary>
    public string? PrintCancelTitle { get => _printCancelTitle; private set => Set(ref _printCancelTitle, value); }
    /// <summary>Text der Meldung im Zustand <see cref="BoothState.PrintCancelled"/>.</summary>
    public string? PrintCancelText { get => _printCancelText; private set => Set(ref _printCancelText, value); }
    /// <summary>Der Gast hat den Druck abbrechen wollen; der Druckdienst führt den Abbruch gerade aus.</summary>
    public bool PrintCancelRequested => _printCancelCts is { IsCancellationRequested: true };
    public SavedPhotoSet? LastSaved { get => _lastSaved; private set => Set(ref _lastSaved, value); }

    /// <summary>
    /// Vorabruf der Seitengröße im Hintergrund (bei Start, Auslösen und geänderten Einstellungen), damit die
    /// Komposition nicht auf einen langsamen Drucker warten muss. Nur für Tests abschaltbar.
    /// </summary>
    public bool PrefetchPageSpec { get; init; } = true;

    /// <summary>Erwartete Druckdauer der laufenden bzw. letzten Sitzung in Sekunden.</summary>
    public int ExpectedPrintSeconds => _expectedPrintSeconds;

    /// <summary>Druckfortschritt 0..1: verstrichene Zeit seit Druckstart / erwartete Dauer, gedeckelt auf 1; nach erfolgreichem Druck 1.</summary>
    public double PrintProgress
    {
        get
        {
            if (_printDone) return 1;
            if (!_printActive) return 0;
            var elapsed = (time.GetUtcNow() - _printStart).TotalSeconds;
            return Math.Clamp(elapsed / _expectedPrintSeconds, 0, 1);
        }
    }

    /// <summary>Die erwartete Druckzeit ist abgelaufen, der Druck aber noch nicht fertig.</summary>
    public bool PrintOverdue =>
        _printActive && !_printDone && (time.GetUtcNow() - _printStart).TotalSeconds >= _expectedPrintSeconds;

    public event EventHandler? StateChanged;
    /// <summary>Alle übrigen Eigenschaften (Countdown, Fotonummer, Status, Druckfortschritt …) haben sich geändert.</summary>
    public event EventHandler? Changed;
    /// <summary>Unmittelbar vor der Aufnahme: weißen Blitz anzeigen.</summary>
    public event EventHandler? FlashRequested;

    /// <summary>Beendet die Startphase (Starting → Idle). Ob die Kamera läuft, prüft <see cref="TryTrigger"/> separat.</summary>
    public void MarkReady()
    {
        if (Interlocked.CompareExchange(ref _settingsHooked, 1, 0) == 0)
            settings.SettingsChanged += (_, _) => StartPageSpecPrefetch();

        if (_state == BoothState.Starting)
            State = BoothState.Idle;
        StartPageSpecPrefetch();
    }

    /// <summary>
    /// Startet einen Durchgang. Nur im Zustand Idle und bei laufender Kamera.
    /// <paramref name="layout"/>: vom Gast gewählte Variante; <c>null</c> oder gleich dem Standard = Einstellungen unverändert.
    /// Die gespeicherten Einstellungen werden nie verändert; nur bei einem vom Standard abweichenden Gast-Layout arbeitet
    /// der Durchgang auf einer angepassten Kopie.
    /// </summary>
    public bool TryTrigger(GuestLayout? layout = null)
    {
        if (_state != BoothState.Idle || camera.Status != CameraStatus.Running)
            return false;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return false;

        var cts = new CancellationTokenSource();
        try
        {
            _sessionCts = cts;
            var cfg = settings.Current;
            if (layout is { } chosen && chosen != GuestLayouts.From(cfg.Layout))
            {
                cfg = cfg.Clone();
                GuestLayouts.ApplyTo(cfg.Layout, chosen);
            }

            ErrorMessage = null;
            PrintCancelTitle = null;
            PrintCancelText = null;
            StatusMessage = null;
            PreviewImage = null;
            CountdownValue = 0;
            ShotIndex = 0;
            ShotCount = 0;
            ResetPrintProgress(cfg.Flow.ExpectedPrintSeconds);
            State = BoothState.Countdown;

            // Bis zur Komposition vergeht der Countdown: genug Zeit, die Seitengröße vorab zu holen.
            StartPageSpecPrefetch(cfg.Layout.Kind);

            _ = RunSessionAsync(cfg, cts);
            return true;
        }
        catch (Exception ex)
        {
            // Der Durchgang läuft noch nicht (RunSessionAsync wirft nicht synchron): Sperre freigeben, sonst bliebe die Box dauerhaft belegt.
            AppLog.Error("Durchgang konnte nicht gestartet werden.", ex);
            _sessionCts = null;
            cts.Dispose();
            if (_state == BoothState.Countdown)
                State = BoothState.Idle;
            Volatile.Write(ref _busy, 0);
            return false;
        }
    }

    /// <summary>
    /// Bricht den Countdown ab (auch die Pause zwischen zwei Streifenfotos): Fotos werden verworfen, es geht
    /// ohne Fehleranzeige zurück zu Idle. In allen anderen Zuständen wirkungslos.
    /// </summary>
    public bool TryCancelCountdown()
    {
        if (_state != BoothState.Countdown)
            return false;

        var cts = _sessionCts;
        if (cts is null)
            return false;

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { return false; }
        return true;
    }

    /// <summary>
    /// Bricht den laufenden Druck ab (Vorschau oder Druckanzeige): Die Vorschau endet sofort, der Druckdienst
    /// storniert den Auftrag, danach zeigt <see cref="BoothState.PrintCancelled"/> kurz eine ruhige Meldung. Der Durchgang
    /// (Foto ist gespeichert) wird nicht abgebrochen. Wirkungslos, solange der Druck noch nicht gestartet ist
    /// (Aufnahme, Zusammenstellen, Speichern), schon fertig ist oder nicht läuft.
    /// </summary>
    public bool TryCancelPrint()
    {
        if (!_printActive || _state is not (BoothState.Preview or BoothState.Printing))
            return false;

        var task = _printTask;
        var cts = _printCancelCts;
        if (task is null || task.IsCompleted || cts is null || cts.IsCancellationRequested)
            return false;

        try
        {
            // Zuerst die Meldung, dann das Signal: Fortschrittsmeldungen des Dienstes werden ab dem Signal ignoriert.
            StatusMessage = "Druck wird abgebrochen…";
            cts.Cancel();
        }
        catch (ObjectDisposedException) { return false; }
        AppLog.Info("Druck vom Gast abgebrochen (Abbruchwunsch).");
        return true;
    }

    /// <summary>Taste „Abbrechen“: bricht den Countdown ab, sonst einen laufenden Druck; sonst wirkungslos.</summary>
    public bool TryCancel() => TryCancelCountdown() || TryCancelPrint();

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
                ResetPrintProgress(_expectedPrintSeconds);
                State = BoothState.Idle;
            }
            finally
            {
                _sessionCts = null;
                _printTask = null;
                Interlocked.Exchange(ref _printCancelCts, null)?.Dispose();
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
        // Die Seitengröße bestimmt auch bei deaktiviertem Druck die Auflösung des Layouts. Bei Zeitüberschreitung
        // oder Fehler gilt der zuletzt ermittelte Wert für dieselben Druckereinstellungen, sonst der Standard.
        var pageKey = PageSpecKey.From(cfg.Printer, cfg.Layout.Kind);
        PageSpec page;
        try
        {
            // Läuft für denselben Schlüssel schon eine Abfrage (Vorabruf, vorheriger Durchgang), wird auf diese
            // gewartet statt eine weitere zu starten.
            var pageTask = GetPageSpecQuery(cfg.Printer, cfg.Layout.Kind, pageKey);
            page = await pageTask.WaitAsync(PageSpecTimeout, time, ct);
            _lastGoodPage[pageKey] = page;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            page = FallbackPage(pageKey, "Seitengröße wurde nicht rechtzeitig ermittelt");
        }
        catch (Exception ex)
        {
            page = FallbackPage(pageKey, "Seitengröße konnte nicht ermittelt werden", ex);
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

        // Bei Zeitüberschreitung wird das Speichern über das Token abgebrochen (SaveCore prüft es zwischen den
        // Dateien), sonst erschiene die Datei womöglich noch nach der Meldung „konnte nicht gespeichert werden“.
        using var saveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<SavedPhotoSet>? saveTask = null;
        try
        {
            saveTask = storage.SaveAsync(layout, photos, cfg.Storage, timestamp, saveCts.Token);
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
            try { saveCts.Cancel(); }
            catch (Exception ex) { AppLog.Warn("Speichern konnte nicht abgebrochen werden.", ex); }
            LogLateSave(saveTask);
            return "Das Foto konnte nicht gespeichert werden. Bitte wende dich an das Personal.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Foto konnte nicht gespeichert werden.", ex);
            return "Das Foto konnte nicht gespeichert werden. Bitte wende dich an das Personal.";
        }

        // Vorschau; der Druck läuft parallel dazu.
        PreviewImage = layout;
        Task<PrintResult>? printTask = null;
        var userCancel = CancellationToken.None;
        if (cfg.Flow.PrintingEnabled)
        {
            ResetPrintProgress(cfg.Flow.ExpectedPrintSeconds);
            var printCancel = new CancellationTokenSource();
            _printCancelCts = printCancel;
            userCancel = printCancel.Token;
            _printStart = time.GetUtcNow();
            _printActive = true;
            printTask = PrintGuardedAsync(layout, cfg.Printer, userCancel, ct);
            _printTask = printTask;
        }
        State = BoothState.Preview;

        // Der Abbruchwunsch des Gastes beendet nur die Vorschau; das Sitzungs-Token bleibt unberührt.
        try
        {
            using var previewCts = CancellationTokenSource.CreateLinkedTokenSource(ct, userCancel);
            await DelayAsync(cfg.Flow.PreviewSeconds, previewCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && userCancel.IsCancellationRequested)
        {
            AppLog.Info("Vorschau auf Wunsch des Gastes beendet (Druck abgebrochen).");
        }

        if (printTask is null)
            return null;

        if (!printTask.IsCompleted)
        {
            StatusMessage ??= "Wird gedruckt…";
            State = BoothState.Printing;
            await TrackPrintProgressAsync(printTask, ct);
        }

        var result = await printTask;
        ct.ThrowIfCancellationRequested();
        _printActive = false;

        if (result.Outcome is PrintOutcome.UserCancelled or PrintOutcome.UserCancelFailed)
        {
            var cancelled = result.Outcome == PrintOutcome.UserCancelled;
            AppLog.Info(cancelled ? "Druck vom Gast abgebrochen." : "Abbruch des Drucks nicht mehr möglich.");
            PrintCancelTitle = cancelled ? "Druck abgebrochen" : "Abbrechen nicht möglich";
            PrintCancelText = cancelled
                ? "Dein Foto ist trotzdem gespeichert."
                : "Der Druck ließ sich nicht mehr abbrechen – dein Foto kommt gleich.";
            StatusMessage = null;
            Raise(Changed);
            State = BoothState.PrintCancelled;
            await Task.Delay(PrintCancelledDisplay, time, ct);
            return null;
        }

        if (result.Success)
        {
            // Auch wenn der Druck schon während der Vorschau fertig wurde: Gast zum Abholen auffordern.
            _printDone = true;
            StatusMessage = null;
            Raise(Changed);
            State = BoothState.Printed;
            await Task.Delay(PrintedDisplay, time, ct);
            return null;
        }

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

    /// <summary>Meldet während des Drucks im festen Takt den Fortschritt (<see cref="Changed"/>), bis der Druck endet.</summary>
    private async Task TrackPrintProgressAsync(Task printTask, CancellationToken ct)
    {
        while (!printTask.IsCompleted && !ct.IsCancellationRequested)
        {
            using var tickCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tick = Task.Delay(ProgressTick, time, tickCts.Token);
            await Task.WhenAny(printTask, tick);
            tickCts.Cancel();
            if (!printTask.IsCompleted)
                Raise(Changed);
        }
    }

    private void ResetPrintProgress(int expectedSeconds)
    {
        _printActive = false;
        _printDone = false;
        _expectedPrintSeconds = Math.Max(1, expectedSeconds);
    }

    /// <summary>Seitengröße nach Zeitüberschreitung/Fehler: zuletzt guter Wert desselben Schlüssels, sonst Standard.</summary>
    private PageSpec FallbackPage(PageSpecKey key, string reason, Exception? ex = null)
    {
        if (_lastGoodPage.TryGetValue(key, out var lastGood))
        {
            AppLog.Warn($"{reason}, verwende den zuletzt ermittelten Wert.", ex);
            return lastGood;
        }

        AppLog.Warn($"{reason}, verwende Standard.", ex);
        return PageSpec.Default;
    }

    /// <summary>
    /// Liefert die laufende Seitengrößen-Abfrage für den Schlüssel oder startet (auf dem Threadpool) genau eine neue.
    /// Die Abfrage selbst ist nicht abbrechbar (blockierender Treiberaufruf); Aufrufer begrenzen nur ihr Warten darauf.
    /// Ein Erfolg wird als letzter guter Wert gemerkt.
    /// </summary>
    private Task<PageSpec> GetPageSpecQuery(PrinterSettings printer, LayoutKind kind, PageSpecKey key)
    {
        while (true)
        {
            if (_pageQueries.TryGetValue(key, out var running))
                return running;

            var tcs = new TaskCompletionSource<PageSpec>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pageQueries.TryAdd(key, tcs.Task))
                continue;

            ObserveFault(tcs.Task);
            _ = Task.Run(() =>
            {
                try
                {
                    var spec = print.GetPageSpec(printer, kind);
                    _lastGoodPage[key] = spec;
                    _pageQueries.TryRemove(key, out _);
                    tcs.TrySetResult(spec);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Abfrage der Seitengröße fehlgeschlagen.", ex);
                    _pageQueries.TryRemove(key, out _);
                    tcs.TrySetException(ex);
                }
            });
            return tcs.Task;
        }
    }

    /// <summary>Holt die Seitengröße im Hintergrund (füllt den Zwischenspeicher des Druckdienstes und den letzten guten Wert).</summary>
    private void StartPageSpecPrefetch(LayoutKind? extraKind = null)
    {
        if (!PrefetchPageSpec)
            return;

        try
        {
            var cfg = settings.Current;
            var printer = cfg.Printer;

            // Standard, alle per Mausrad wählbaren Layouts und ein ggf. gerade gewähltes.
            var kinds = new List<LayoutKind> { cfg.Layout.Kind };
            foreach (var choice in cfg.Layout.GuestChoices ?? [])
            {
                var choiceLayout = new LayoutSettings();
                GuestLayouts.ApplyTo(choiceLayout, choice);
                kinds.Add(choiceLayout.Kind);
            }
            if (extraKind is { } extra)
                kinds.Add(extra);

            foreach (var kind in kinds.Distinct())
            {
                // Läuft schon eine Abfrage für diesen Schlüssel, wird keine zweite gestapelt.
                _ = GetPageSpecQuery(printer, kind, PageSpecKey.From(printer, kind));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Vorabruf der Seitengröße konnte nicht gestartet werden.", ex);
        }
    }

    /// <summary>
    /// Druckt mit hartem Timeout (Druckerzeitlimit + Reserve); wirft nie. <paramref name="userCancel"/> (Wunsch des Gastes)
    /// geht an den Druckdienst; bestätigt der ihn nicht binnen <see cref="PrintCancelTimeout"/>, gilt
    /// <see cref="PrintOutcome.UserCancelFailed"/>. <paramref name="ct"/> beendet nur das Warten (der Auftrag bleibt).
    /// </summary>
    private async Task<PrintResult> PrintGuardedAsync(BitmapSource page, PrinterSettings printer, CancellationToken userCancel, CancellationToken ct)
    {
        var limit = TimeSpan.FromSeconds(Math.Max(SettingsLimits.PrintTimeoutMin, printer.TimeoutSeconds)) + PrintHardLimitReserve;
        using var printCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Der Druck kann nach hartem Timeout noch weiterlaufen und später Status melden; sobald dieser Durchgang
        // vorbei ist, dürfen solche Meldungen den Status des nächsten Durchgangs nicht mehr überschreiben.
        var sessionOver = false;
        var progress = new Progress<string>(message =>
        {
            // Nach dem Abbruchwunsch bleibt „Druck wird abgebrochen…“ stehen.
            if (!Volatile.Read(ref sessionOver) && !userCancel.IsCancellationRequested)
                StatusMessage = message;
        });
        Task<PrintResult>? task = null;
        try
        {
            task = print.PrintAsync(page, printer, progress, printCts.Token, userCancel);
            var work = task.WaitAsync(limit, time, ct);

            // Bewusst ohne RunContinuationsAsynchronously: Die Frist unten startet im selben Zug wie der Abbruchwunsch.
            var cancelRequested = new TaskCompletionSource();
            using var registration = userCancel.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), cancelRequested);
            if (await Task.WhenAny(work, cancelRequested.Task) == work)
                return await work;

            // Abbruch angefordert: Der Dienst bekommt begrenzt Zeit, den Auftrag zu stornieren. Das harte Limit
            // (in work) gilt weiter; endet es früher, wirft „await work“ wie sonst auch.
            using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                if (await Task.WhenAny(work, Task.Delay(PrintCancelTimeout, time, graceCts.Token)) == work)
                    return await work;
            }
            finally
            {
                graceCts.Cancel();
            }

            // Der Dienst hat sich nicht gemeldet: nicht länger warten (der Auftrag läuft evtl. weiter).
            AppLog.Warn($"Druckabbruch nicht innerhalb von {PrintCancelTimeout.TotalSeconds:0} s bestätigt.");
            printCts.Cancel();
            ObserveFault(work);
            ObserveFault(task);
            return new PrintResult(PrintOutcome.UserCancelFailed);
        }
        catch (TimeoutException)
        {
            printCts.Cancel();
            if (task is not null)
                ObserveFault(task);
            return new PrintResult(PrintOutcome.TimedOut);
        }
        catch (OperationCanceledException)
        {
            // Wirft der Dienst auf den Abbruchwunsch des Gastes eine OCE (statt UserCancelled zu melden), ist es dennoch dessen Abbruch.
            return new PrintResult(userCancel.IsCancellationRequested && !ct.IsCancellationRequested
                ? PrintOutcome.UserCancelled
                : PrintOutcome.Cancelled);
        }
        catch (Exception ex)
        {
            AppLog.Error("Druck wirft eine Ausnahme.", ex);
            return new PrintResult(PrintOutcome.Failed, ex.Message);
        }
        finally
        {
            Volatile.Write(ref sessionOver, true);
        }
    }

    /// <summary>Loggt, wenn ein per Zeitlimit aufgegebenes Speichern doch noch fertig wurde (die Datei erscheint dann nachträglich).</summary>
    private static void LogLateSave(Task<SavedPhotoSet>? saveTask)
    {
        if (saveTask is null)
            return;
        _ = saveTask.ContinueWith(static t => AppLog.Warn($"Speichern war nach dem Zeitlimit doch noch erfolgreich: {t.Result.LayoutPath}"),
            TaskContinuationOptions.OnlyOnRanToCompletion);
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

/// <summary>Druckereinstellungen, von denen die ermittelte Seitengröße abhängt (Schlüssel für Zwischenspeicher).</summary>
public readonly record struct PageSpecKey(
    string? PrinterName, string? PaperSizeName, double? PaperWidthMm, double? PaperHeightMm,
    PageOrientation Orientation, LayoutKind Kind)
{
    public static PageSpecKey From(PrinterSettings printer, LayoutKind kind) =>
        new(printer.PrinterName, printer.PaperSizeName, printer.PaperWidthMm, printer.PaperHeightMm, printer.Orientation, kind);
}
