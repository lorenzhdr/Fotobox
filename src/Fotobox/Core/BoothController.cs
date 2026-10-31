using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;
using Fotobox.Core.Fun;
using Fotobox.Core.Heart;
using Fotobox.Core.Peace;

namespace Fotobox.Core;

/// <summary>
/// Printed: Druck fertig, Gast wird kurz aufgefordert, das Foto aus dem Drucker zu nehmen.
/// PrintCancelled: Gast hat den Druck abgebrochen (bzw. der Abbruch ging nicht mehr), kurze ruhige Meldung statt Fehler.
/// VideoCountdown → Recording → VideoSaving → VideoSaved: Videodurchgang (Fehler wie beim Foto über Error zurück nach Idle).
/// PrinterBlocked: Druckersperre – der Drucker meldet ein Problem (z. B. Papier leer); die Box bleibt gesperrt, bis der Auftrag gedruckt ist
/// oder die Sperre aufgehoben wird (<see cref="BoothController.ReleasePrinterBlock"/>). Neue Werte nur hinten anhängen.
/// </summary>
public enum BoothState { Starting, Idle, Countdown, Capturing, Preview, Printing, Printed, Error, PrintCancelled, VideoCountdown, Recording, VideoSaving, VideoSaved, PrinterBlocked }

/// <summary>
/// Ablaufsteuerung eines Foto-Durchgangs (Countdown → Aufnahme → Layout → Speichern → Vorschau/Druck → „Fertig“).
/// Ohne UI testbar; Zeit läuft über <see cref="TimeProvider"/>. Zustandsänderungen werden auf dem
/// SynchronizationContext des Aufrufers von <see cref="TryTrigger"/> ausgelöst (kein ConfigureAwait(false),
/// damit auch Compose auf dem UI-Thread läuft).
/// <para>
/// Daneben gibt es den Videodurchgang (<see cref="TryStartVideo"/>): Countdown → Aufnahme → Speichern → „gespeichert“.
/// Foto- und Videodurchgang teilen sich dasselbe Gate, es läuft nie beides zugleich. Ohne <c>videoStorage</c> ist Video nicht startbar.
/// Für die Taste „Video“ (gleiche Taste für Start und Stopp) bietet <see cref="TryToggleVideo"/> die Umschaltung an.
/// </para>
/// </summary>
public sealed class BoothController(
    ICameraService camera,
    ILayoutComposer composer,
    IPrintService print,
    IPhotoStorage storage,
    ISettingsStore settings,
    TimeProvider time,
    IVideoStorage? videoStorage = null,
    IPhotoDecorator? photoDecorator = null)
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);
    /// <summary>Ein Foto-Verzierer ist angebunden (für den DI-Test).</summary>
    internal bool HasPhotoDecorator => photoDecorator is not null;
    /// <summary>Obergrenze für die (blockierende) Seitengrößen-Abfrage des Druckers.</summary>
    private static readonly TimeSpan PageSpecTimeout = TimeSpan.FromSeconds(5);
    /// <summary>Obergrenze für das Speichern (z. B. hängende Netzwerkfreigabe, abgezogener USB-Stick).</summary>
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(30);
    /// <summary>Reserve des harten Druck-Limits über dem Druck-Timeout der Einstellungen (der Druckdienst bricht vorher von selbst ab).</summary>
    private static readonly TimeSpan PrintHardLimitReserve = TimeSpan.FromSeconds(30);
    /// <summary>Obergrenze für <see cref="IPhotoDecorator.DecorateAsync"/> je Aufnahme (danach gilt das unverzierte Foto).</summary>
    public static readonly TimeSpan PhotoDecorateTimeout = TimeSpan.FromSeconds(3);
    /// <summary>So lange bleibt die „Fertig“-Meldung nach erfolgreichem Druck stehen.</summary>
    public static readonly TimeSpan PrintedDisplay = TimeSpan.FromSeconds(5);
    /// <summary>So lange bleibt die Meldung „Druck abgebrochen“ (bzw. „Abbrechen nicht möglich“) stehen.</summary>
    public static readonly TimeSpan PrintCancelledDisplay = TimeSpan.FromSeconds(3);
    /// <summary>So lange wartet der Controller nach einem Abbruchwunsch auf den Druckdienst; danach gilt der Abbruch als gescheitert.</summary>
    public static readonly TimeSpan PrintCancelTimeout = TimeSpan.FromSeconds(10);
    /// <summary>Takt, in dem <see cref="Changed"/> während des Drucks den Fortschritt meldet.</summary>
    private static readonly TimeSpan ProgressTick = TimeSpan.FromMilliseconds(250);
    /// <summary>Obergrenze für <see cref="IVideoStorage.PrepareAsync"/> (Ordner anlegen, Speicherplatz prüfen).</summary>
    public static readonly TimeSpan VideoPrepareTimeout = TimeSpan.FromSeconds(10);
    /// <summary>Obergrenze für <see cref="ICameraService.StartRecordingAsync"/>.</summary>
    public static readonly TimeSpan VideoStartTimeout = TimeSpan.FromSeconds(15);
    /// <summary>Obergrenze für <see cref="IVideoRecording.StopAsync"/> (Datei abschließen).</summary>
    public static readonly TimeSpan VideoStopTimeout = TimeSpan.FromSeconds(30);
    /// <summary>Obergrenze für <see cref="IVideoStorage.CommitAsync"/> (Umbenennen).</summary>
    public static readonly TimeSpan VideoCommitTimeout = TimeSpan.FromSeconds(30);
    // Beim App-Ende sind die Fristen kürzer (Teilvideo retten, aber nie lange hängen).
    private static readonly TimeSpan ShutdownStopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownCommitTimeout = TimeSpan.FromSeconds(5);
    /// <summary>
    /// So lange blockiert <see cref="Cancel"/> bei laufender Aufnahme höchstens (echte Zeit), um das Video zu speichern.
    /// Danach kehrt es zurück; das Speichern läuft ggf. im Hintergrund weiter.
    /// </summary>
    public TimeSpan ShutdownVideoWait { get; init; } = TimeSpan.FromSeconds(12);

    private const string VideoStartError = "Das Video konnte nicht gestartet werden. Bitte versuche es noch einmal.";
    private const string VideoSaveError = "Das Video konnte nicht gespeichert werden. Bitte versuche es noch einmal.";
    private const string VideoDiskSpaceError = "Nicht genug Speicherplatz für ein Video. Bitte sag dem Gastgeber Bescheid.";

    private volatile BoothState _state = BoothState.Starting;
    private int _busy;
    private int _settingsHooked;
    private CancellationTokenSource? _sessionCts;

    // Druckfortschritt: wird bei jedem Lesen aus der Zeit berechnet, der Takt löst nur Changed aus.
    private DateTimeOffset _printStart;
    private volatile bool _printActive;
    private volatile bool _printDone;
    private int _expectedPrintSeconds = new FlowSettings().ExpectedPrintSeconds;
    // Druck-Timeout (Sekunden) des laufenden Drucks, Grundlage des Ladekreises (Untergrenze wie im Druckdienst).
    private volatile int _printTimeoutSeconds = SettingsLimits.PrintTimeoutMin;
    // Druckersperre des laufenden Drucks (null = kein Druck bzw. keine Sperrüberwachung).
    private volatile PrintBlockRun? _printBlock;
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
    private CountdownStyle _countdownStyle;
    private int _shotIndex;
    private int _shotCount;
    private BitmapSource? _previewImage;
    private string? _statusMessage;
    private string? _errorMessage;
    private string? _printCancelTitle;
    private string? _printCancelText;
    private SavedPhotoSet? _lastSaved;

    // Video: laufende Aufnahme und ihre Anzeigewerte. Die Laufzeit wird bei jedem Lesen aus der Zeit berechnet,
    // der Sekundentakt der Aufnahme löst nur Changed aus.
    private volatile VideoRun? _video;
    private long _recStart;
    private long _recFrozenTicks;
    private volatile bool _recActive;
    private volatile int _recMaxSeconds;
    private volatile int _recMinSeconds;
    private volatile int _recWarnSeconds;
    private bool _recHasAudio;
    private string _videoSavedText = VideoSettings.DefaultSuccessText;
    private string? _lastSavedVideo;

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
    /// <summary>
    /// Darstellung des Countdowns im laufenden Durchgang (aus <see cref="TriggerOptions.Style"/>); außerhalb eines Durchgangs
    /// <see cref="CountdownStyle.Normal"/>. Bleibt bis nach <see cref="FlashRequested"/> gesetzt. Änderungen kommen über <see cref="Changed"/>.
    /// </summary>
    public CountdownStyle CountdownStyle { get => _countdownStyle; private set => Set(ref _countdownStyle, value); }
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
    /// Ein Videodurchgang läuft (Countdown, Aufnahme, Speichern oder „gespeichert“-Meldung).
    /// </summary>
    public bool IsVideoState => _state is BoothState.VideoCountdown or BoothState.Recording or BoothState.VideoSaving or BoothState.VideoSaved;
    /// <summary>
    /// Laufzeit der Aufnahme (ab dem Start, auf <c>MaxDurationSeconds</c> begrenzt); nach dem Ende der Aufnahme der Endwert,
    /// bis der nächste Videodurchgang beginnt. Die UI aktualisiert sich über <see cref="Changed"/> (Sekundentakt).
    /// </summary>
    public TimeSpan RecordingElapsed
    {
        get
        {
            var max = TimeSpan.FromSeconds(_recMaxSeconds);
            var elapsed = _recActive
                ? time.GetElapsedTime(Volatile.Read(ref _recStart))
                : TimeSpan.FromTicks(Volatile.Read(ref _recFrozenTicks));
            return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed > max ? max : elapsed;
        }
    }
    /// <summary>Restzeit bis zum automatischen Ende (Maximaldauer − <see cref="RecordingElapsed"/>).</summary>
    public TimeSpan RecordingRemaining => TimeSpan.FromSeconds(_recMaxSeconds) - RecordingElapsed;
    /// <summary>Während der Aufnahme: <c>WarningSeconds</c> &gt; 0 und die Restzeit ist kleiner oder gleich diesem Wert.</summary>
    public bool IsRecordingWarning =>
        _recActive && _recWarnSeconds > 0 && RecordingRemaining <= TimeSpan.FromSeconds(_recWarnSeconds);
    /// <summary>Die laufende Aufnahme enthält Ton (<see cref="IVideoRecording.HasAudio"/>).</summary>
    public bool RecordingHasAudio { get => _recHasAudio; private set => Set(ref _recHasAudio, value); }
    /// <summary>
    /// Im Zustand <see cref="BoothState.Recording"/> hat die Aufnahme die Mindestlänge erreicht; erst dann wirkt
    /// <see cref="TryStopVideo"/> (die Abbrechen-Taste beendet eine Aufnahme nie).
    /// </summary>
    public bool CanStopVideo =>
        _state == BoothState.Recording && _recActive && RecordingElapsed >= TimeSpan.FromSeconds(_recMinSeconds);
    /// <summary>Meldung im Zustand <see cref="BoothState.VideoSaved"/> (<c>SuccessText</c> oder <see cref="VideoSettings.DefaultSuccessText"/>).</summary>
    public string VideoSavedText { get => _videoSavedText; private set => Set(ref _videoSavedText, value); }
    /// <summary>Pfad des zuletzt gespeicherten Videos.</summary>
    public string? LastSavedVideo { get => _lastSavedVideo; private set => Set(ref _lastSavedVideo, value); }

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

    /// <summary>
    /// Der Ladekreis unter dem Fortschrittsbalken ist sichtbar: im Zustand <see cref="BoothState.Printing"/>, solange
    /// <see cref="PrintOverdue"/> gilt. Er füllt sich (<see cref="PrintRingProgress"/>) von der erwarteten Druckzeit bis zum Druck-Timeout.
    /// </summary>
    public bool ShowPrintRing => _state == BoothState.Printing && PrintOverdue;

    /// <summary>
    /// Füllstand des Ladekreises 0..1: (verstrichene Zeit − erwartete Dauer) / (Druck-Timeout − erwartete Dauer), gedeckelt;
    /// 0 außerhalb eines laufenden Drucks. Die UI aktualisiert sich über <see cref="Changed"/> (Takt während des Drucks).
    /// </summary>
    public double PrintRingProgress
    {
        get
        {
            if (_printDone || !_printActive) return 0;
            var elapsed = (time.GetUtcNow() - _printStart).TotalSeconds;
            var span = Math.Max(1, _printTimeoutSeconds - _expectedPrintSeconds);
            return Math.Clamp((elapsed - _expectedPrintSeconds) / span, 0, 1);
        }
    }

    /// <summary>
    /// Ursache der Druckersperre im Zustand <see cref="BoothState.PrinterBlocked"/>; <see cref="PrinterAttention.None"/> = das Problem ist
    /// behoben, der Auftrag wartet noch auf den Druck. Außerhalb der Sperre <see cref="PrinterAttention.None"/>. Änderungen kommen über <see cref="Changed"/>.
    /// </summary>
    public PrinterAttention BlockedReason => _printBlock is { } run ? run.Reason : PrinterAttention.None;

    /// <summary>
    /// Zuletzt vom Druck ermittelte Seitengröße für <paramref name="kind"/> mit den aktuellen Druckereinstellungen (inklusive aufgelöster
    /// Ausrichtung); <c>null</c>, solange noch keine echte Abfrage gelungen ist. Reiner Lesezugriff, blockiert nie und fragt den Drucker
    /// nicht ab (für die Anzeige des Druckbereichs in der Gastansicht).
    /// </summary>
    public PageSpec? GetLastKnownPageSpec(LayoutKind kind) =>
        _lastGoodPage.TryGetValue(PageSpecKey.From(settings.Current.Printer, kind), out var known) ? known : null;

    public event EventHandler? StateChanged;
    /// <summary>Alle übrigen Eigenschaften (Countdown, Fotonummer, Status, Druckfortschritt …) haben sich geändert.</summary>
    public event EventHandler? Changed;
    /// <summary>
    /// Eine Seitengröße wurde neu ermittelt und weicht vom zuletzt bekannten Wert ab (<see cref="GetLastKnownPageSpec"/>). Kann auf einem
    /// Hintergrundthread kommen. Bewusst getrennt von <see cref="Changed"/>.
    /// </summary>
    public event EventHandler? PageSpecUpdated;
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
    public bool TryTrigger(GuestLayout? layout = null) => TryTrigger(new TriggerOptions { Layout = layout });

    /// <summary>
    /// Startet einen Durchgang mit <see cref="TriggerOptions"/> (gleiche Sperren wie <see cref="TryTrigger(GuestLayout?)"/>).
    /// <see cref="TriggerOptions.SingleShotOnly"/> ersetzt ein Streifen-Layout durch das Einzelfoto (Overlay und Einzelfoto bleiben),
    /// <see cref="TriggerOptions.CountdownSeconds"/> überschreibt den Countdown (0 = sofort, auf 0–30 begrenzt; beim Stil
    /// <see cref="CountdownStyle.Kisscam"/> auf 3–60). <see cref="TriggerOptions.Frame"/> erzwingt das Einzelfoto (auch bei Streifen oder
    /// Overlay), <see cref="TriggerOptions.FilePrefix"/> ersetzt das Dateipräfix, <see cref="TriggerOptions.CropRegion"/> schneidet die
    /// Aufnahme zu. Auch dann arbeitet der Durchgang nur auf einer Kopie der Einstellungen.
    /// </summary>
    public bool TryTrigger(TriggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_state != BoothState.Idle || camera.Status != CameraStatus.Running)
            return false;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return false;

        var cts = new CancellationTokenSource();
        try
        {
            _sessionCts = cts;
            var cfg = settings.Current;
            var defaultLayout = GuestLayouts.From(cfg.Layout);
            var effective = options.Layout ?? defaultLayout;
            if (options.SingleShotOnly && effective is GuestLayout.StripDouble or GuestLayout.StripSingle)
                effective = GuestLayout.Single;
            // Ein Rahmen (Kisscam) gibt es nur um ein einzelnes Foto: kein Streifen, kein Overlay-PNG.
            if (options.Frame is not null)
                effective = GuestLayout.Single;
            var layoutChanged = effective != defaultLayout;
            var kisscam = options.Style == CountdownStyle.Kisscam;
            var countdownMin = kisscam ? SettingsLimits.KisscamSecondsMin : SettingsLimits.PeacePhotoCountdownMin;
            var countdownMax = kisscam ? SettingsLimits.KisscamSecondsMax : SettingsLimits.PeacePhotoCountdownMax;
            var countdown = options.CountdownSeconds is { } c ? Math.Clamp(c, countdownMin, countdownMax) : (int?)null;
            var prefix = string.IsNullOrWhiteSpace(options.FilePrefix) ? null : options.FilePrefix;
            if (layoutChanged || (countdown is { } cd && cd != cfg.Flow.CountdownSeconds) || (prefix is not null && prefix != cfg.Storage.FilePrefix))
            {
                cfg = cfg.Clone();
                if (layoutChanged)
                    GuestLayouts.ApplyTo(cfg.Layout, effective);
                if (countdown is { } value)
                    cfg.Flow.CountdownSeconds = value;
                if (prefix is not null)
                    cfg.Storage.FilePrefix = prefix;
            }

            CountdownStyle = options.Style;
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

            _ = RunSessionAsync(cfg, new SessionExtras(options.CropRegion, options.Frame), cts);
            return true;
        }
        catch (Exception ex)
        {
            // Der Durchgang läuft noch nicht (RunSessionAsync wirft nicht synchron): Sperre freigeben, sonst bliebe die Box dauerhaft belegt.
            AppLog.Error("Durchgang konnte nicht gestartet werden.", ex);
            CountdownStyle = CountdownStyle.Normal;
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
        // Ab der Druckersperre ist Esc wirkungslos (der Auftrag bleibt in der Warteschlange; Ausweg ist nur die Admin-Ansicht).
        if (_printBlock is { Blocked: true })
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

    /// <summary>
    /// Hebt die Druckersperre auf (Ausweg über die Admin-Ansicht): Das Warten auf den Druck endet, die Box geht ohne Fehleranzeige
    /// zurück zu Idle. Der Auftrag bleibt in der Warteschlange und druckt nach dem Beheben des Problems von selbst. Nur im Zustand
    /// <see cref="BoothState.PrinterBlocked"/> wirksam, sonst <c>false</c>.
    /// </summary>
    public bool ReleasePrinterBlock()
    {
        if (_state != BoothState.PrinterBlocked || _printBlock is not { Blocked: true, Over: false } run)
            return false;

        var cts = run.PrintCts;
        if (cts is null)
            return false;

        run.Released = true;
        try { cts.Cancel(); }
        catch (ObjectDisposedException)
        {
            run.Released = false;
            return false;
        }

        AppLog.Info("Druckersperre aufgehoben (Admin-Ansicht); der Auftrag bleibt in der Warteschlange.");
        return true;
    }

    /// <summary>
    /// Taste „Abbrechen“: bricht den Foto- oder Video-Countdown ab (still, nichts wird aufgenommen), sonst einen laufenden
    /// Druck; sonst wirkungslos. Eine laufende Videoaufnahme beendet sie nie (das tun die Videotaste, die Maximaldauer
    /// und das App-Ende).
    /// </summary>
    public bool TryCancel() => TryCancelCountdown() || TryCancelVideo() || TryCancelPrint();

    /// <summary>
    /// Bricht einen laufenden Durchgang ab (z. B. beim Beenden der App). Läuft gerade eine Videoaufnahme (oder wird sie
    /// gespeichert), wird sie beendet und gespeichert (Teilvideo); der Aufruf blockiert dafür höchstens
    /// <see cref="ShutdownVideoWait"/> und braucht dazu nicht den UI-Thread.
    /// </summary>
    public void Cancel()
    {
        if (_video is { } run && _state is BoothState.Recording or BoothState.VideoSaving)
            FinishVideoForShutdown(run);

        try { _sessionCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Startet einen Videodurchgang. Nur im Zustand Idle, bei laufender Kamera, aktivem Video
    /// (<see cref="VideoSettings.Enabled"/>) und vorhandenem Video-Speicher; sonst (auch bei laufendem Foto- oder Videodurchgang)
    /// <c>false</c>. Foto und Video teilen sich dasselbe Gate. Der Zustand wechselt sofort zu <see cref="BoothState.VideoCountdown"/>
    /// (bei <c>CountdownSeconds</c> = 0 mit <see cref="CountdownValue"/> 0, bis die Aufnahme beginnt).
    /// </summary>
    public bool TryStartVideo()
    {
        if (videoStorage is null || _state != BoothState.Idle || camera.Status != CameraStatus.Running)
            return false;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return false;

        var cts = new CancellationTokenSource();
        try
        {
            var cfg = settings.Current;
            if (!cfg.Video.Enabled)
            {
                cts.Dispose();
                Volatile.Write(ref _busy, 0);
                return false;
            }

            _sessionCts = cts;
            var video = cfg.Video;
            var max = Math.Max(1, video.MaxDurationSeconds);

            ErrorMessage = null;
            PrintCancelTitle = null;
            PrintCancelText = null;
            StatusMessage = null;
            PreviewImage = null;
            CountdownValue = 0;
            ShotIndex = 0;
            ShotCount = 0;
            ResetPrintProgress(cfg.Flow.ExpectedPrintSeconds);
            _recActive = false;
            Volatile.Write(ref _recFrozenTicks, 0);
            _recMaxSeconds = max;
            _recMinSeconds = Math.Clamp(video.MinDurationSeconds, 0, max);
            _recWarnSeconds = Math.Max(0, video.WarningSeconds);
            RecordingHasAudio = false;
            VideoSavedText = string.IsNullOrWhiteSpace(video.SuccessText) ? VideoSettings.DefaultSuccessText : video.SuccessText;
            State = BoothState.VideoCountdown;

            AppLog.Info($"Videodurchgang gestartet (Countdown {Math.Max(0, video.CountdownSeconds)} s, Höchstdauer {max} s).");
            _ = RunVideoSessionAsync(cfg, cts);
            return true;
        }
        catch (Exception ex)
        {
            // Der Durchgang läuft noch nicht: Sperre freigeben, sonst bliebe die Box dauerhaft belegt.
            AppLog.Error("Videodurchgang konnte nicht gestartet werden.", ex);
            _sessionCts = null;
            cts.Dispose();
            if (_state == BoothState.VideoCountdown)
                State = BoothState.Idle;
            Volatile.Write(ref _busy, 0);
            return false;
        }
    }

    /// <summary>
    /// Beendet die laufende Aufnahme und speichert sie. Wirkt nur im Zustand <see cref="BoothState.Recording"/> und erst ab
    /// <c>MinDurationSeconds</c> Laufzeit (<see cref="CanStopVideo"/>); vorher, im Countdown und sonst <c>false</c>.
    /// </summary>
    public bool TryStopVideo()
    {
        if (_state != BoothState.Recording || _video is not { } run || !CanStopVideo)
            return false;
        if (!run.RequestStop(VideoStopReason.User))
            return false;
        AppLog.Info("Videoaufnahme vom Gast beendet.");
        return true;
    }

    /// <summary>
    /// Taste „Video“ mit gleicher Belegung für Start und Stopp: In Idle <see cref="TryStartVideo"/>, in Recording
    /// <see cref="TryStopVideo"/>, sonst (Countdown, Speichern …) wirkungslos. Rückgabe: es wurde etwas ausgelöst.
    /// </summary>
    public bool TryToggleVideo() => _state switch
    {
        BoothState.Idle => TryStartVideo(),
        BoothState.Recording => TryStopVideo(),
        _ => false,
    };

    /// <summary>Abbrechen im Video-Countdown: still zurück zu Idle. In Recording (und sonst) wirkungslos.</summary>
    private bool TryCancelVideo()
    {
        if (_state != BoothState.VideoCountdown)
            return false;

        var cts = _sessionCts;
        if (cts is null)
            return false;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { return false; }
        return true;
    }

    /// <summary>App-Ende bei laufender Aufnahme: beenden, speichern (kurze Fristen) und höchstens <see cref="ShutdownVideoWait"/> warten.</summary>
    private void FinishVideoForShutdown(VideoRun run)
    {
        try
        {
            // Das Speichern zuerst anmelden und auf dem Threadpool ausführen: Der Aufrufer (UI-Thread) blockiert gleich, und
            // Fortsetzungen des Aufnahmedienstes, die auf seinen Synchronisationskontext warten, würden sonst nie laufen.
            // Es muss vor dem Stopp-Signal angemeldet sein, weil der Ablauf auf dem UI-Thread inline weiterläuft und sonst
            // selbst (mit den langen Fristen, auf dem UI-Thread) das Speichern beginnen würde.
            var save = BeginFinalize(run, ShutdownStopTimeout, ShutdownCommitTimeout, onThreadPool: true);
            run.RequestStop(VideoStopReason.AppExit);
            if (!save.Wait(ShutdownVideoWait))
                AppLog.Warn("Videoaufnahme wurde beim Beenden nicht rechtzeitig gespeichert.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Videoaufnahme konnte beim Beenden nicht gespeichert werden.", ex);
        }
    }

    private async Task RunVideoSessionAsync(AppSettings cfg, CancellationTokenSource cts)
    {
        try
        {
            string? error;
            try
            {
                error = await RunVideoPhasesAsync(cfg, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                AppLog.Info("Videodurchgang abgebrochen.");
                error = null;
            }
            catch (Exception ex)
            {
                AppLog.Error("Unerwarteter Fehler im Video-Durchgang.", ex);
                error = "Es ist ein unerwarteter Fehler aufgetreten. Bitte versuche es noch einmal.";
            }

            if (error is not null)
            {
                AppLog.Warn($"Videodurchgang mit Fehler beendet: {error}");
                ErrorMessage = error;
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
                // Ausnahme mitten in der Aufnahme: nie eine laufende Aufnahme zurücklassen (stoppen und speichern bzw. verwerfen).
                if (_video is { } run && !run.FinalizeStarted)
                {
                    _recActive = false;
                    _ = BeginFinalize(run, VideoStopTimeout, VideoCommitTimeout);
                }
                _video = null;
                _recActive = false;
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

    /// <summary>Führt alle Video-Phasen aus. Rückgabe: Fehlermeldung für den Gast oder null bei Erfolg (oder stillem Abbruch).</summary>
    private async Task<string?> RunVideoPhasesAsync(AppSettings cfg, CancellationToken ct)
    {
        var video = cfg.Video;   // Snapshot: spätere Änderungen der Einstellungen wirken erst im nächsten Durchgang
        var maxDuration = TimeSpan.FromSeconds(_recMaxSeconds);

        // 1. Countdown (0 = sofort). Abbrechen wirft hier und verwirft alles (noch nichts vorbereitet).
        for (var v = Math.Max(0, video.CountdownSeconds); v >= 1; v--)
        {
            CountdownValue = v;
            await Task.Delay(Second, time, ct);
        }
        CountdownValue = 0;
        ct.ThrowIfCancellationRequested();

        // 2. Ziel vorbereiten (Ordner, Speicherplatz, temporäre Datei).
        var timestamp = time.GetLocalNow().DateTime;
        VideoTarget target;
        using (var prepareCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            Task<VideoTarget>? prepare = null;
            try
            {
                prepare = videoStorage!.PrepareAsync(video, camera.ActiveResolution, maxDuration, timestamp, prepareCts.Token);
                ObserveFault(prepare);
                target = await prepare.WaitAsync(VideoPrepareTimeout, time, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                prepareCts.Cancel();
                DiscardLateTarget(prepare);
                throw;
            }
            catch (TimeoutException)
            {
                AppLog.Error("Vorbereiten des Videos hat das Zeitlimit überschritten.");
                prepareCts.Cancel();
                DiscardLateTarget(prepare);
                return VideoStartError;
            }
            catch (InsufficientDiskSpaceException ex)
            {
                AppLog.Error("Nicht genug Speicherplatz für ein Video.", ex);
                return VideoDiskSpaceError;
            }
            catch (Exception ex)
            {
                AppLog.Error("Video konnte nicht vorbereitet werden.", ex);
                return VideoStartError;
            }
        }

        // 3. Aufnahme starten.
        IVideoRecording recording;
        using (var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            Task<IVideoRecording>? start = null;
            try
            {
                start = camera.StartRecordingAsync(new VideoRecordingRequest(target.TempPath, video.Quality), startCts.Token);
                ObserveFault(start);
                recording = await start.WaitAsync(VideoStartTimeout, time, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                startCts.Cancel();
                _ = CleanUpUnusedRecordingAsync(start, target);
                throw;
            }
            catch (TimeoutException)
            {
                AppLog.Error("Starten der Videoaufnahme hat das Zeitlimit überschritten.");
                startCts.Cancel();
                _ = CleanUpUnusedRecordingAsync(start, target);
                return VideoStartError;
            }
            catch (Exception ex)
            {
                AppLog.Error("Videoaufnahme konnte nicht gestartet werden.", ex);
                videoStorage.Discard(target);
                return VideoStartError;
            }
        }

        // Abgebrochen, während die Kamera gerade startete: die (leere) Aufnahme wieder beenden und verwerfen.
        if (ct.IsCancellationRequested)
        {
            _ = CleanUpUnusedRecordingAsync(Task.FromResult(recording), target);
            ct.ThrowIfCancellationRequested();
        }

        // 4. Aufnahme läuft. Faulted kommt auf beliebigem Thread und geht über das Stopp-Signal in den Ablauf.
        var run = new VideoRun(recording, target);
        recording.Faulted += (_, reason) =>
        {
            if (run.RequestStop(VideoStopReason.Faulted, reason))
                AppLog.Warn($"Videoaufnahme vom Gerät beendet: {reason}");
        };
        _video = run;
        var startTimestamp = time.GetTimestamp();
        Volatile.Write(ref _recStart, startTimestamp);
        _recActive = true;
        RecordingHasAudio = recording.HasAudio;
        AppLog.Info($"Videoaufnahme gestartet (Ton: {(recording.HasAudio ? "ja" : "nein")}, Höchstdauer {_recMaxSeconds} s).");
        Raise(Changed);
        State = BoothState.Recording;

        while (true)
        {
            var elapsed = time.GetElapsedTime(startTimestamp);
            if (elapsed >= maxDuration)
                run.RequestStop(VideoStopReason.MaxDuration);
            if (run.StopSignal.IsCompleted)
                break;

            using var tickCts = new CancellationTokenSource();
            var tick = Task.Delay(Min(Second, maxDuration - elapsed), time, tickCts.Token);
            await Task.WhenAny(run.StopSignal, tick);
            tickCts.Cancel();
            if (!run.StopSignal.IsCompleted)
                Raise(Changed);
        }

        var finalElapsed = RecordingElapsed;
        Volatile.Write(ref _recFrozenTicks, finalElapsed.Ticks);
        _recActive = false;
        var stopReason = run.StopSignal.Result;
        AppLog.Info($"Videoaufnahme beendet ({DescribeStopReason(stopReason, run.FaultDetail)}), Dauer {finalElapsed.TotalSeconds:0.0} s.");

        // 5. Speichern.
        Raise(Changed);
        State = BoothState.VideoSaving;
        var result = await BeginFinalize(run, VideoStopTimeout, VideoCommitTimeout);
        if (result.FilePath is null)
            return VideoSaveError;

        AppLog.Info($"Video gespeichert: {result.FilePath} (Dauer {finalElapsed.TotalSeconds:0.0} s, Ton: {(recording.HasAudio ? "ja" : "nein")}).");
        LastSavedVideo = result.FilePath;
        State = BoothState.VideoSaved;
        await DelayAsync(Math.Max(0, video.SuccessDisplaySeconds), ct);
        return null;
    }

    /// <summary>
    /// Beendet die Aufnahme und übernimmt die Datei, genau einmal je Aufnahme: Wer zuerst kommt (Ablauf oder App-Ende),
    /// bestimmt die Fristen, alle anderen warten auf dasselbe Ergebnis. Läuft ohne Synchronisationskontext und wirft nie.
    /// </summary>
    private Task<VideoSaveResult> BeginFinalize(VideoRun run, TimeSpan stopTimeout, TimeSpan commitTimeout, bool onThreadPool = false)
    {
        TaskCompletionSource<VideoSaveResult> tcs;
        lock (run.Gate)
        {
            if (run.Finalizing is { } existing)
                return existing.Task;
            tcs = run.Finalizing = new TaskCompletionSource<VideoSaveResult>();
        }

        if (onThreadPool)
            _ = Task.Run(() => FinalizeVideoAsync(run, tcs, stopTimeout, commitTimeout));
        else
            _ = FinalizeVideoAsync(run, tcs, stopTimeout, commitTimeout);
        return tcs.Task;
    }

    private async Task FinalizeVideoAsync(VideoRun run, TaskCompletionSource<VideoSaveResult> tcs, TimeSpan stopTimeout, TimeSpan commitTimeout)
    {
        VideoSaveResult result;
        try
        {
            // Aufnahme abschließen. Scheitert das (z. B. nach Faulted), wird trotzdem versucht, das Teilvideo zu übernehmen;
            // hängt es, ist die Datei nicht verlässlich: verwerfen.
            var stopHung = false;
            using (var stopCts = new CancellationTokenSource())
            {
                try
                {
                    var stop = run.Recording.StopAsync(stopCts.Token);
                    ObserveFault(stop);
                    await stop.WaitAsync(stopTimeout, time).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    AppLog.Error("Beenden der Videoaufnahme hat das Zeitlimit überschritten.");
                    stopHung = true;
                    stopCts.Cancel();
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Videoaufnahme ließ sich nicht sauber beenden, versuche das Teilvideo zu übernehmen.", ex);
                }
            }

            if (stopHung)
            {
                videoStorage!.Discard(run.Target);
                result = new VideoSaveResult(null);
            }
            else
            {
                using var commitCts = new CancellationTokenSource();
                Task<string>? commit = null;
                try
                {
                    commit = videoStorage!.CommitAsync(run.Target, commitCts.Token);
                    ObserveFault(commit);
                    result = new VideoSaveResult(await commit.WaitAsync(commitTimeout, time).ConfigureAwait(false));
                }
                catch (TimeoutException)
                {
                    AppLog.Error("Übernehmen des Videos hat das Zeitlimit überschritten.");
                    commitCts.Cancel();
                    LogLateCommit(commit);
                    videoStorage!.Discard(run.Target);
                    result = new VideoSaveResult(null);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Video konnte nicht gespeichert werden.", ex);
                    videoStorage!.Discard(run.Target);
                    result = new VideoSaveResult(null);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Unerwarteter Fehler beim Speichern des Videos.", ex);
            try { videoStorage!.Discard(run.Target); } catch (Exception) { /* Discard wirft nie; Sicherheitsnetz */ }
            result = new VideoSaveResult(null);
        }

        _ = DisposeRecordingAsync(run.Recording);
        tcs.TrySetResult(result);
    }

    /// <summary>Beendet eine Aufnahme, die nach Abbruch/Zeitlimit des Starts doch noch zustande kam, und verwirft die Datei.</summary>
    private async Task CleanUpUnusedRecordingAsync(Task<IVideoRecording>? start, VideoTarget target)
    {
        try
        {
            if (start is not null)
            {
                IVideoRecording? recording = null;
                try { recording = await start.ConfigureAwait(false); }
                catch (Exception) { /* Start gescheitert: nichts zu beenden */ }

                if (recording is not null)
                {
                    try { await recording.StopAsync().WaitAsync(VideoStopTimeout, time).ConfigureAwait(false); }
                    catch (Exception ex) { AppLog.Warn("Nicht benötigte Videoaufnahme ließ sich nicht beenden.", ex); }
                    await DisposeRecordingAsync(recording).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Aufräumen einer nicht benötigten Videoaufnahme fehlgeschlagen.", ex);
        }
        finally
        {
            videoStorage?.Discard(target);
        }
    }

    private void DiscardLateTarget(Task<VideoTarget>? prepare)
    {
        if (prepare is null)
            return;
        _ = prepare.ContinueWith(t => videoStorage?.Discard(t.Result),
            CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private static void LogLateCommit(Task<string>? commit)
    {
        if (commit is null)
            return;
        _ = commit.ContinueWith(static t => AppLog.Warn($"Übernehmen des Videos war nach dem Zeitlimit doch noch erfolgreich: {t.Result}"),
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private static async Task DisposeRecordingAsync(IVideoRecording recording)
    {
        try { await recording.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Warn("Videoaufnahme ließ sich nicht freigeben.", ex); }
    }

    private static string DescribeStopReason(VideoStopReason reason, string? detail) => reason switch
    {
        VideoStopReason.User => "vom Gast",
        VideoStopReason.MaxDuration => "Höchstdauer erreicht",
        VideoStopReason.Faulted => $"Gerätefehler: {detail}",
        VideoStopReason.AppExit => "Programmende",
        _ => reason.ToString(),
    };

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;

    private enum VideoStopReason { User, MaxDuration, Faulted, AppExit }

    /// <summary>Pfad der gespeicherten Datei oder <c>null</c>, wenn das Video verworfen wurde.</summary>
    private sealed record VideoSaveResult(string? FilePath);

    /// <summary>Eine laufende Aufnahme samt Stopp-Signal und gemeinsamem Speicher-Ergebnis.</summary>
    private sealed class VideoRun(IVideoRecording recording, VideoTarget target)
    {
        public IVideoRecording Recording { get; } = recording;
        public VideoTarget Target { get; } = target;
        public object Gate { get; } = new();
        public TaskCompletionSource<VideoSaveResult>? Finalizing { get; set; }
        public bool FinalizeStarted { get { lock (Gate) return Finalizing is not null; } }
        public string? FaultDetail { get; private set; }

        private readonly TaskCompletionSource<VideoStopReason> _stop = new();   // bewusst ohne RunContinuationsAsynchronously
        private bool _stopRequested;

        public Task<VideoStopReason> StopSignal => _stop.Task;

        /// <summary>Fordert das Ende an; nur die erste Anforderung zählt. Beliebiger Thread.</summary>
        public bool RequestStop(VideoStopReason reason, string? detail = null)
        {
            lock (Gate)
            {
                if (_stopRequested)
                    return false;
                _stopRequested = true;
                FaultDetail = detail;
            }
            _stop.TrySetResult(reason);
            return true;
        }
    }

    private async Task RunSessionAsync(AppSettings cfg, SessionExtras extras, CancellationTokenSource cts)
    {
        try
        {
            string? error;
            try
            {
                error = await RunPhasesAsync(cfg, extras, cts.Token);
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
                CountdownStyle = CountdownStyle.Normal;
                StatusMessage = null;
                ResetPrintProgress(_expectedPrintSeconds);
                State = BoothState.Idle;
            }
            finally
            {
                _sessionCts = null;
                _printTask = null;
                RetirePrintBlock();
                Interlocked.Exchange(ref _printCancelCts, null)?.Dispose();
                cts.Dispose();
                Volatile.Write(ref _busy, 0);
            }
        }
    }

    /// <summary>Führt alle Phasen aus. Rückgabe: Fehlermeldung für den Gast oder null bei Erfolg.</summary>
    private async Task<string?> RunPhasesAsync(AppSettings cfg, SessionExtras extras, CancellationToken ct)
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
                photo = await DecorateAsync(photo, i + 1, ct);
                // Zuschnitt erst nach dem Verzieren (der Bartplaner rechnet mit den Koordinaten des ganzen Bildes), vor dem Spiegeln
                // (der Ausschnitt gilt für das ungespiegelte Rohfoto).
                if (extras.Crop is { } crop)
                    photo = CropPhoto(photo, crop);
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
            layout = extras.Frame is { } frame
                ? composer.Compose(photos, cfg.Layout, page, settings.OverlayDirectory, timestamp, frame)
                : composer.Compose(photos, cfg.Layout, page, settings.OverlayDirectory, timestamp);
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
        PrintBlockRun? blockRun = null;
        var userCancel = CancellationToken.None;
        if (cfg.Flow.PrintingEnabled)
        {
            ResetPrintProgress(cfg.Flow.ExpectedPrintSeconds);
            var printCancel = new CancellationTokenSource();
            _printCancelCts = printCancel;
            userCancel = printCancel.Token;
            _printTimeoutSeconds = Math.Max(SettingsLimits.PrintTimeoutMin, cfg.Printer.TimeoutSeconds);
            blockRun = new PrintBlockRun();
            _printBlock = blockRun;
            _printStart = time.GetUtcNow();
            _printActive = true;
            printTask = PrintGuardedAsync(layout, cfg.Printer, userCancel, ct, blockRun);
            _printTask = printTask;
        }
        State = BoothState.Preview;

        // Der Abbruchwunsch des Gastes und eine Druckersperre beenden nur die Vorschau; das Sitzungs-Token bleibt unberührt.
        try
        {
            using var previewCts = CancellationTokenSource.CreateLinkedTokenSource(ct, userCancel,
                blockRun?.PreviewSignal.Token ?? CancellationToken.None);
            await DelayAsync(cfg.Flow.PreviewSeconds, previewCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && userCancel.IsCancellationRequested)
        {
            AppLog.Info("Vorschau auf Wunsch des Gastes beendet (Druck abgebrochen).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && blockRun is { Blocked: true })
        {
            AppLog.Info("Vorschau wegen Druckersperre beendet.");
        }

        if (printTask is null)
            return null;

        if (!printTask.IsCompleted)
        {
            StatusMessage ??= "Wird gedruckt…";
            State = blockRun is { Blocked: true } ? BoothState.PrinterBlocked : BoothState.Printing;
            await TrackPrintProgressAsync(printTask, blockRun, ct);
        }

        var result = await printTask;
        ct.ThrowIfCancellationRequested();
        _printActive = false;

        // Sperre über die Admin-Ansicht aufgehoben: ohne Fehleranzeige zurück zu Idle, der Auftrag bleibt in der Warteschlange.
        if (blockRun is { Released: true } && result.Outcome == PrintOutcome.Cancelled)
        {
            AppLog.Info("Druckersperre aufgehoben: Durchgang endet ohne Fehleranzeige, der Auftrag bleibt in der Warteschlange.");
            StatusMessage = null;
            return null;
        }

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
            if (blockRun is { Blocked: true })
                AppLog.Info($"Druck nach Druckersperre abgeschlossen (Sperre dauerte {time.GetElapsedTime(blockRun.BlockedSince).TotalSeconds:0} s).");
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
    private async Task TrackPrintProgressAsync(Task printTask, PrintBlockRun? run, CancellationToken ct)
    {
        while (!printTask.IsCompleted && !ct.IsCancellationRequested)
        {
            using var tickCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tick = Task.Delay(ProgressTick, time, tickCts.Token);
            await Task.WhenAny(printTask, tick);
            tickCts.Cancel();
            if (!printTask.IsCompleted)
            {
                // Fängt eine Sperre ab, die zwischen Zustandswahl und Takt gemeldet wurde (die Meldung selbst kommt über den UI-Kontext).
                SyncPrintBlockState(run);
                Raise(Changed);
            }
        }
    }

    /// <summary>Wechselt von <see cref="BoothState.Printing"/> nach <see cref="BoothState.PrinterBlocked"/>, sobald eine Sperre gemeldet wurde (UI-Kontext).</summary>
    private void SyncPrintBlockState(PrintBlockRun? run)
    {
        if (run is { Blocked: true } && ReferenceEquals(_printBlock, run) && _state == BoothState.Printing)
            State = BoothState.PrinterBlocked;
    }

    /// <summary>
    /// Handler der Sperrmeldungen des Druckdienstes (beliebiger Thread). Setzt Sperre und Ursache sofort (damit z. B. Esc und das harte
    /// Limit sie schon sehen); der Zustandswechsel und das Ende der Vorschau laufen danach auf dem Kontext des Durchgangs.
    /// </summary>
    private void OnPrinterAttention(PrintBlockRun run, PrinterAttention attention, SynchronizationContext? context)
    {
        try
        {
            if (run.Over || (attention == PrinterAttention.None && !run.Blocked))
                return;

            var previous = run.Reason;
            run.Reason = attention;
            if (!run.Blocked)
            {
                run.BlockedSince = time.GetTimestamp();
                run.Blocked = true;
                AppLog.Warn($"Druckersperre: {attention}.");
            }
            else if (previous != attention)
            {
                AppLog.Info(attention == PrinterAttention.None
                    ? "Druckersperre: Problem behoben, der Auftrag wartet auf den Druck."
                    : $"Druckersperre: Ursache {attention}.");
            }

            void Apply()
            {
                if (run.Over)
                    return;
                try { run.PreviewSignal.Cancel(); }
                catch (ObjectDisposedException) { }
                SyncPrintBlockState(run);
                Raise(Changed);
            }

            if (context is not null)
                context.Post(static state => ((Action)state!)(), (Action)Apply);
            else
                _ = Task.Run(Apply);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Druckersperre konnte nicht gemeldet werden.", ex);
        }
    }

    /// <summary>Beendet die Sperrüberwachung des Durchgangs: spätere Meldungen des Druckdienstes bleiben ohne Wirkung.</summary>
    private void RetirePrintBlock()
    {
        var run = Interlocked.Exchange(ref _printBlock, null);
        if (run is null)
            return;
        run.Over = true;
        run.PreviewSignal.Dispose();
    }

    /// <summary>Zustand der Druckersperre eines Drucks (Meldungen kommen vom Druck-Thread, Lesen auf dem UI-Thread).</summary>
    private sealed class PrintBlockRun
    {
        private int _reason;

        /// <summary>Die Sperre wurde gemeldet (bleibt bis zum Ende des Drucks gesetzt, auch wenn die Ursache wieder wegfällt).</summary>
        public volatile bool Blocked;
        /// <summary>Der Admin hat die Sperre aufgehoben.</summary>
        public volatile bool Released;
        /// <summary>Der Druck ist vorbei bzw. der Durchgang beendet: Meldungen werden ignoriert.</summary>
        public volatile bool Over;
        /// <summary>Zeitstempel (<see cref="TimeProvider.GetTimestamp"/>) der ersten Sperrmeldung.</summary>
        public long BlockedSince;
        /// <summary>Beendet die Vorschau, sobald die Sperre gemeldet wurde.</summary>
        public CancellationTokenSource PreviewSignal { get; } = new();
        /// <summary>Quelle, die das Warten auf den Druck beendet (Aufheben der Sperre); gesetzt, solange <c>PrintGuardedAsync</c> läuft.</summary>
        public volatile CancellationTokenSource? PrintCts;

        public PrinterAttention Reason
        {
            get => (PrinterAttention)Volatile.Read(ref _reason);
            set => Volatile.Write(ref _reason, (int)value);
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
                    var specChanged = !_lastGoodPage.TryGetValue(key, out var previousSpec) || previousSpec != spec;
                    _lastGoodPage[key] = spec;
                    _pageQueries.TryRemove(key, out _);
                    tcs.TrySetResult(spec);
                    if (specChanged)
                        Raise(PageSpecUpdated);
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
    private async Task<PrintResult> PrintGuardedAsync(BitmapSource page, PrinterSettings printer, CancellationToken userCancel, CancellationToken ct,
        PrintBlockRun run)
    {
        var limit = TimeSpan.FromSeconds(Math.Max(SettingsLimits.PrintTimeoutMin, printer.TimeoutSeconds)) + PrintHardLimitReserve;
        using var printCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        run.PrintCts = printCts;
        // Der Druck kann nach hartem Timeout noch weiterlaufen und später Status melden; sobald dieser Durchgang
        // vorbei ist, dürfen solche Meldungen den Status des nächsten Durchgangs nicht mehr überschreiben.
        var sessionOver = false;
        var progress = new Progress<string>(message =>
        {
            // Nach dem Abbruchwunsch bleibt „Druck wird abgebrochen…“ stehen.
            if (!Volatile.Read(ref sessionOver) && !userCancel.IsCancellationRequested)
                StatusMessage = message;
        });
        // Die Sperrmeldung setzt ihr Flag sofort (nicht erst über den Kontext wie Progress<T>), siehe OnPrinterAttention.
        var context = SynchronizationContext.Current;
        var blockOptions = new PrintBlockOptions(new ImmediateProgress<PrinterAttention>(a => OnPrinterAttention(run, a, context)));
        Task<PrintResult>? task = null;
        try
        {
            task = print.PrintAsync(page, printer, progress, printCts.Token, userCancel, blockOptions);
            var work = AwaitPrintAsync(task, limit, run, printCts.Token);

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

            // Zwischen Abbruchwunsch und Sperre geraten: In der Sperre wirkt der Abbruch nicht, der Auftrag bleibt – weiter warten.
            if (run.Blocked)
            {
                AppLog.Info("Druckabbruch ignoriert: Der Druck ist inzwischen gesperrt (Drucker meldet ein Problem).");
                return await work;
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
            run.Over = true;
            run.PrintCts = null;
        }
    }

    /// <summary>
    /// Wartet auf den Druck mit hartem Limit. Ist das Limit abgelaufen, während eine Druckersperre aktiv ist, wird ohne Limit weiter
    /// gewartet (Ausweg nur über <paramref name="token"/>: App-Ende bzw. Aufheben der Sperre); ein hängender Spooler ohne Sperre läuft weiter in <see cref="TimeoutException"/>.
    /// </summary>
    private async Task<PrintResult> AwaitPrintAsync(Task<PrintResult> task, TimeSpan limit, PrintBlockRun run, CancellationToken token)
    {
        try
        {
            return await task.WaitAsync(limit, time, token);
        }
        catch (TimeoutException) when (run.Blocked)
        {
            AppLog.Info("Hartes Druck-Limit erreicht, aber die Druckersperre ist aktiv – das Warten läuft ohne Frist weiter.");
            return await task.WaitAsync(token);
        }
    }

    /// <summary>Fortschrittsmeldung, die ihren Handler sofort auf dem meldenden Thread ausführt (anders als <see cref="Progress{T}"/>).</summary>
    private sealed class ImmediateProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
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

    /// <summary>
    /// Verziert das rohe (ungespiegelte) Foto, falls ein aktiver <see cref="IPhotoDecorator"/> vorhanden ist. Frist
    /// <see cref="PhotoDecorateTimeout"/>; Abbruch wird weitergereicht, jeder andere Fehler und die Zeitüberschreitung ergeben
    /// (mit Warnung im Log) das unverzierte Foto, nie einen Fehlerzustand.
    /// </summary>
    private async Task<BitmapSource> DecorateAsync(BitmapSource photo, int shot, CancellationToken ct)
    {
        var decorator = photoDecorator;
        if (decorator is null)
            return photo;

        Task<BitmapSource>? task = null;
        try
        {
            if (!decorator.IsActive)
                return photo;
            task = decorator.DecorateAsync(photo, shot, ct);
            ObserveFault(task);
            return await task.WaitAsync(PhotoDecorateTimeout, time, ct) ?? photo;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            AppLog.Warn($"Foto {shot} konnte nicht rechtzeitig verziert werden; es wird unverziert verwendet.");
            return photo;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Foto {shot} konnte nicht verziert werden; es wird unverziert verwendet ({ex.GetType().Name}: {ex.Message}).");
            return photo;
        }
    }

    /// <summary>Zusätzliche Angaben eines Durchgangs aus <see cref="TriggerOptions"/> (Zuschnitt des Rohfotos, Rahmen um das Layout).</summary>
    private sealed record SessionExtras(NormRect? Crop, PhotoFrame? Frame);

    /// <summary>
    /// Schneidet das (ungespiegelte) Foto auf den normierten Ausschnitt zu (<see cref="KisscamZoom.ToPixelRect"/>). Bei jedem Fehler
    /// gilt das unbeschnittene Foto (mit Warnung im Log), nie ein Fehlerzustand.
    /// </summary>
    internal static BitmapSource CropPhoto(BitmapSource source, NormRect region)
    {
        try
        {
            var (x, y, w, h) = KisscamZoom.ToPixelRect(region, source.PixelWidth, source.PixelHeight);
            if (w <= 0 || h <= 0 || (x == 0 && y == 0 && w == source.PixelWidth && h == source.PixelHeight))
                return source;
            if (!source.IsFrozen && source.CanFreeze)
                source.Freeze();
            var cropped = new CroppedBitmap(source, new Int32Rect(x, y, w, h));
            if (cropped.CanFreeze)
                cropped.Freeze();
            return cropped;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Foto konnte nicht zugeschnitten werden; es wird unbeschnitten verwendet ({ex.GetType().Name}: {ex.Message}).");
            return source;
        }
    }

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
