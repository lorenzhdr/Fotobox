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
    Task<BitmapSource> CapturePhotoAsync(CancellationToken ct = default); // höchste verfügbare Auflösung der aktiven Session, Bgra32, Frozen, UNgespiegelt; InvalidOperationException wenn nicht Running
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

public interface IAdminLauncher
{
    /// <summary>Wenn noch keine PIN existiert: Erststart-Dialog (PIN + Bestätigung). false = abgebrochen → App beendet sich.</summary>
    bool EnsurePinConfigured(Window? owner);
    /// <summary>PIN-Abfrage, dann modale Admin-Ansicht.</summary>
    AdminResult OpenAdmin(Window owner);
}
