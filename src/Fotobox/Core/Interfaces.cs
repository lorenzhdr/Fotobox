using System.Windows;
using System.Windows.Media.Imaging;
namespace Fotobox.Core;

public interface ISettingsStore
{
    string ConfigDirectory { get; }      // %LOCALAPPDATA%\Fotobox (existiert nach ctor)
    string OverlayDirectory { get; }     // ConfigDirectory\overlays (existiert nach ctor)
    AppSettings Current { get; }         // nie null; bei fehlender/defekter Datei Defaults (defekte Datei → .bak sichern + loggen)
    event EventHandler? SettingsChanged;
    void Save(AppSettings settings);     // speichert Clone, atomar (tmp + File.Replace/Move), ersetzt Current, feuert SettingsChanged; wirft IOException, wenn LoadFailed
    /// <summary>true = settings.json existiert, war aber nicht lesbar (fail-closed: Current sind nur Standardwerte, Save verweigert).</summary>
    bool LoadFailed => false;
    /// <summary>Liest die Datei nach einem Ladefehler erneut. true = einsatzbereit (LoadFailed dann false).</summary>
    bool TryReload() => true;
}

public interface IPinService
{
    bool HasPin { get; }
    void SetPin(string pin);             // 4–12 Ziffern, sonst ArgumentException; PBKDF2-SHA256 + 16-Byte-Salt, speichert via ISettingsStore
    bool Verify(string pin);             // konstante Vergleichszeit (CryptographicOperations.FixedTimeEquals)
}

public interface ICameraService : IAsyncDisposable
{
    CameraStatus Status { get; }
    string? StatusMessage { get; }       // deutsch, nutzerfreundlich (z.B. Hinweis auf Windows-Kamera-Datenschutz)
    CameraResolution? ActiveResolution { get; }
    event EventHandler? StatusChanged;   // beliebiger Thread
    event EventHandler<CameraFrame>? FrameArrived; // Hintergrund-Thread
    Task<IReadOnlyList<CameraDevice>> GetDevicesAsync();
    Task<IReadOnlyList<CameraResolution>> GetResolutionsAsync(string deviceId);
    Task StartAsync(CameraSettings settings, CancellationToken ct = default); // stoppt ggf. laufende Session zuerst; wirft nicht bei Gerätefehlern, sondern setzt Status
    Task StopAsync();
    Task<BitmapSource> CapturePhotoAsync(CancellationToken ct = default); // aktuelles Bild des Livestreams (Auflösung/Format der aktiven Session, nicht die höchste der Kamera), Bgra32, Frozen, UNgespiegelt; InvalidOperationException wenn nicht Running

    /// <summary>
    /// Ton für die nächsten Sessions: <c>null</c> = ohne Mikrofon (nur Video). Wirkt erst beim nächsten <see cref="StartAsync"/>
    /// (Ton wird beim Initialisieren der Session festgelegt). Scheitert die Session mit Mikrofon, startet sie still ohne Ton
    /// (<see cref="AudioAvailable"/> = false, Log).
    /// </summary>
    void ConfigureAudio(CameraAudio? audio) { }
    /// <summary>true = die laufende Session hat ein Mikrofon geöffnet; Aufnahmen enthalten Ton.</summary>
    bool AudioAvailable => false;
    /// <summary>Mikrofone (Windows-Audioaufnahmegeräte). Wirft nie; bei Fehler/Timeout leere Liste (und Log).</summary>
    Task<IReadOnlyList<MicrophoneDevice>> GetMicrophonesAsync() => Task.FromResult<IReadOnlyList<MicrophoneDevice>>([]);
    /// <summary>
    /// Startet die Aufnahme der laufenden Session als MP4 in <see cref="VideoRecordingRequest.FilePath"/> (seitenrichtig, ungespiegelt);
    /// das Livebild (<see cref="FrameArrived"/>) läuft weiter. InvalidOperationException, wenn nicht Running oder schon eine
    /// Aufnahme läuft; andere Exceptions bei Gerätefehlern. Hat ein Timeout.
    /// </summary>
    Task<IVideoRecording> StartRecordingAsync(VideoRecordingRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("Videoaufnahme wird von diesem Kameradienst nicht unterstützt.");
}

/// <summary>Eine laufende Videoaufnahme. Wird die Kamera gestoppt oder neu verbunden, beendet der Dienst die Aufnahme vorher selbst (Teilvideo).</summary>
public interface IVideoRecording : IAsyncDisposable
{
    bool HasAudio { get; }
    /// <summary>Die Aufnahme wurde vom Gerät beendet (Kamera/Mikrofon weg, Datenträger voll …). Beliebiger Thread, höchstens einmal.
    /// Danach <see cref="StopAsync"/> aufrufen, um das bis dahin Aufgenommene abzuschließen. Wer sich erst nach dem Fehler
    /// anmeldet, wird sofort benachrichtigt (kein verpasster Fehler zwischen Start und Anmeldung).</summary>
    event EventHandler<string>? Faulted;
    /// <summary>Beendet die Aufnahme und schließt die Datei ab (best effort auch nach <see cref="Faulted"/>). Wirft, wenn keine
    /// abspielbare Datei entstanden ist. Mehrfacher Aufruf ist erlaubt (wartet auf den ersten). Hat ein Timeout.</summary>
    Task StopAsync(CancellationToken ct = default);
}

public interface IVideoStorage
{
    string ResolveTargetFolder(VideoSettings video, DateTime timestamp);
    /// <summary>
    /// Zielordner anlegen (bei Schreibfehler einmal Ausweichen auf <see cref="AppInfo.DefaultVideoFolder"/>), freien Platz für
    /// <paramref name="maxDuration"/> in <paramref name="quality"/> bei <paramref name="resolution"/> plus Reserve prüfen und eine
    /// temporäre Zieldatei festlegen. <see cref="InsufficientDiskSpaceException"/> bei zu wenig Platz, IOException wenn kein Ordner beschreibbar.
    /// </summary>
    Task<VideoTarget> PrepareAsync(VideoSettings video, CameraResolution? resolution, TimeSpan maxDuration, DateTime timestamp, CancellationToken ct = default);
    /// <summary>Benennt die temporäre Datei in {Prefix}_{yyyyMMdd_HHmmss}.mp4 um (Kollisionen _2, _3 …) und gibt den Pfad zurück.
    /// FileNotFoundException/IOException, wenn die temporäre Datei fehlt oder leer ist.</summary>
    Task<string> CommitAsync(VideoTarget target, CancellationToken ct = default);
    /// <summary>Löscht die temporäre Datei (falls vorhanden). Wirft nie.</summary>
    void Discard(VideoTarget target);
}

public interface IDisplayService
{
    /// <summary>Angeschlossene Monitore, sortiert nach Nummer. Wirft nie; bei Fehlern leere Liste (und Log).</summary>
    IReadOnlyList<DisplayMonitor> GetMonitors();
}

public interface ILayoutComposer
{
    int GetRequiredPhotoCount(LayoutSettings layout);   // Strip → Clamp(StripPhotoCount,2,4), sonst 1
    /// <summary>Komponiert die Druckseite. Ergebnis: Frozen BitmapSource mit exakt page.PixelWidth × page.PixelHeight, Dpi = page.Dpi. Muss auf STA-Thread laufen.</summary>
    BitmapSource Compose(IReadOnlyList<BitmapSource> photos, LayoutSettings layout, PageSpec page, string overlayDirectory, DateTime timestamp);
}

public interface IPrintService
{
    IReadOnlyList<PrinterInfo> GetPrinters();
    IReadOnlyList<PaperSizeInfo> GetPaperSizes(string? printerName);
    /// <summary>Seitengröße aus Einstellungen/Treiber, Ausrichtung aufgelöst (Auto: Strip → Hochformat, sonst Querformat). Fallback PageSpec.Default.</summary>
    PageSpec GetPageSpec(PrinterSettings printer, LayoutKind layout);
    /// <summary>
    /// Druckt und wartet, bis der Job die Warteschlange verlassen hat (oder Fehler/Timeout). Wirft nicht.
    /// <paramref name="ct"/>: nicht mehr warten (z. B. App-Ende) → Cancelled-Result, ein gesendeter Auftrag wird NICHT gelöscht.
    /// <paramref name="userCancel"/>: der Gast bricht ab. Vor dem Senden → UserCancelled ohne Auftrag; danach wird der Auftrag
    /// storniert (UserCancelled bzw. UserCancelFailed, wenn er nicht gefunden oder nicht storniert werden konnte).
    /// </summary>
    Task<PrintResult> PrintAsync(BitmapSource page, PrinterSettings printer, IProgress<string>? status = null,
        CancellationToken ct = default, CancellationToken userCancel = default);
}

public interface IPhotoStorage
{
    string ResolveTargetFolder(StorageSettings storage, DateTime timestamp);
    Task<SavedPhotoSet> SaveAsync(BitmapSource layout, IReadOnlyList<BitmapSource> originals, StorageSettings storage, DateTime timestamp, CancellationToken ct = default);
    // Dateinamen: {Prefix}_{yyyyMMdd_HHmmss}.jpg, Originale {Prefix}_{yyyyMMdd_HHmmss}_orig{n}.jpg; bei Kollision _2, _3 …
}

/// <summary>
/// Ein Erkennungsverfahren für das Fun-Feature (Ganzkörper per ONNX oder Gesicht per Windows). Nicht threadsicher für parallele
/// Aufrufe: Der Dienst ruft <see cref="DetectAsync"/> nie gleichzeitig auf. <see cref="IAsyncDisposable.DisposeAsync"/> darf
/// jederzeit kommen, auch während einer laufenden Analyse; die Implementierung gibt native Ressourcen erst nach deren Ende frei.
/// </summary>
public interface IPersonDetector : IAsyncDisposable
{
    Fun.DetectionKind Kind { get; }
    /// <summary>Lädt Modell bzw. Detektor (idempotent, hat ein Timeout). Wirft, wenn das Verfahren nicht verfügbar ist.</summary>
    Task InitializeAsync(CancellationToken ct = default);
    /// <summary>Analysiert ein Bild. Ergebnis nach <paramref name="minConfidence"/> gefiltert. Wirft bei Fehlern.</summary>
    Task<IReadOnlyList<Fun.Detection>> DetectAsync(Fun.DetectionFrame frame, double minConfidence, CancellationToken ct = default);
}

/// <summary>
/// Personenerkennung im Livebild für das Fun-Feature. Analysiert nur, solange mindestens eine Anforderung offen ist; dann hört der
/// Dienst auf <see cref="ICameraService.FrameArrived"/>, sonst nicht. Es gilt immer die zuletzt gestellte offene Anforderung
/// (die Admin-Vorschau überholt so die Gastansicht); nur sie bekommt Ergebnisse.
/// </summary>
public interface IPersonDetectionService : IAsyncDisposable
{
    /// <summary>
    /// Bedarf anmelden. Ereignisse der Anforderung kommen auf dem <see cref="SynchronizationContext"/> des Aufrufers (falls vorhanden).
    /// Dispose meldet ab; danach kommen keine Ereignisse mehr.
    /// </summary>
    IPersonDetectionRequest Request(FunSettings settings);
}

/// <summary>Eine offene Anforderung an <see cref="IPersonDetectionService"/>.</summary>
public interface IPersonDetectionRequest : IDisposable
{
    /// <summary>Einstellungen ändern (z. B. Entwurf im Admin); eine andere Methode wechselt den Detektor.</summary>
    void UpdateSettings(FunSettings settings);
    /// <summary>Neue Analyse (nur für die aktuell geltende Anforderung).</summary>
    event EventHandler<Fun.DetectionUpdate>? Updated;
    /// <summary>Statusänderung (Laden, Ausweichen auf Gesicht, nicht verfügbar).</summary>
    event EventHandler<Fun.DetectionStatus>? StatusChanged;
    Fun.DetectionStatus Status { get; }
}

/// <summary>
/// Spielt die kurzen Signaltöne des Fun-Features (<see cref="Fun.FunSoundCue"/>). Wirft nie: Fehlt ein Audiogerät, bleibt es still
/// (nur Log). <see cref="Play"/> kehrt sofort zurück (asynchrone Wiedergabe), ein neuer Ton ersetzt den laufenden.
/// </summary>
public interface IFunSoundPlayer
{
    void Play(Fun.FunSoundCue cue);
    /// <summary>Beendet einen laufenden Ton sofort.</summary>
    void Stop();
}

public interface IAdminLauncher
{
    /// <summary>Wenn noch keine PIN existiert: Erststart-Dialog (PIN + Bestätigung). false = abgebrochen → App beendet sich.</summary>
    bool EnsurePinConfigured(Window? owner);
    /// <summary>PIN-Abfrage, dann modale Admin-Ansicht.</summary>
    AdminResult OpenAdmin(Window owner);
}
