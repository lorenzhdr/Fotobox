using System.Windows;
using Fotobox.Core;
using Fotobox.ViewModels.Admin;

namespace Fotobox.Views.Admin;

/// <summary>
/// Öffnet die Admin-Ansicht: Erststart-PIN, PIN-Abfrage (mit Sperre nach Fehlversuchen) und das modale Admin-Fenster.
/// </summary>
public sealed class AdminLauncher : IAdminLauncher
{
    private readonly ISettingsStore _settings;
    private readonly IPinService _pinService;
    private readonly ICameraService _camera;
    private readonly IPrintService _print;
    private readonly IDisplayService _displays;
    private readonly IPersonDetectionService? _detection;
    private readonly IFunSoundPlayer? _sound;
    private readonly PinAttemptLimiter _limiter;

    /// <summary>Erkennung und Ton für die Fun-Vorschau sind vorhanden (sonst ist die Vorschau inert); für den DI-Test.</summary>
    internal bool HasFunServices => _detection is not null && _sound is not null;

    public AdminLauncher(ISettingsStore settings, IPinService pinService, ICameraService camera, IPrintService print,
        IDisplayService displays)
        : this(settings, pinService, camera, print, displays, TimeProvider.System)
    {
    }

    // Die Dienste der Zielerfassung sind für die Live-Vorschau im Tab „Fun“; ohne sie bleibt die Vorschau inert.
    public AdminLauncher(ISettingsStore settings, IPinService pinService, ICameraService camera, IPrintService print,
        IDisplayService displays, TimeProvider time, IPersonDetectionService? detection = null, IFunSoundPlayer? sound = null)
    {
        _settings = settings;
        _pinService = pinService;
        _camera = camera;
        _print = print;
        _displays = displays;
        _detection = detection;
        _sound = sound;
        // Ein Limiter pro Prozess: Die Sperre überdauert das Schließen des PIN-Dialogs.
        _limiter = new PinAttemptLimiter(time, maxAttempts: 5, lockoutDuration: TimeSpan.FromSeconds(30));
    }

    public bool EnsurePinConfigured(Window? owner)
    {
        // Fail-closed: Ist settings.json vorhanden, aber nicht lesbar, wäre HasPin fälschlich false –
        // dann dürfte jeder Gast eine neue PIN festlegen. Stattdessen Fehlermeldung mit Erneut-versuchen/Beenden.
        while (_settings.LoadFailed)
        {
            var retry = ShowRetry(owner,
                "Die Einstellungsdatei (settings.json) konnte nicht gelesen werden.\n\n" +
                "Vermutlich greift gerade ein anderes Programm darauf zu (z. B. Virenscanner oder Datensicherung). " +
                "Die gespeicherten Einstellungen und die PIN bleiben unverändert erhalten.\n\n" +
                "Klicken Sie auf „OK“, um es erneut zu versuchen, oder auf „Abbrechen“, um Fotobox+ zu beenden.");
            if (!retry)
                return false;
            if (_settings.TryReload())
                break;
        }

        if (_pinService.HasPin)
            return true;

        while (true)
        {
            var dialog = new FirstRunPinDialog(owner, _settings.Current.Appearance.AdminTheme, _settings.Current.Input.AdminKeys);
            if (dialog.ShowDialog() != true)
                return false;

            try
            {
                _pinService.SetPin(dialog.Pin);
                AppLog.Info("PIN beim Erststart festgelegt.");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("PIN konnte nicht gespeichert werden.", ex);
                ShowError(owner, "Die PIN konnte nicht gespeichert werden:\n" + ex.Message);
            }
        }
    }

    public AdminResult OpenAdmin(Window owner)
    {
        // Auch hier prüfen: bei nicht lesbarer Einstellungsdatei bleibt die Admin-Ansicht gesperrt.
        if ((_settings.LoadFailed || !_pinService.HasPin) && !EnsurePinConfigured(owner))
            return new AdminResult(false, false);

        var pinDialog = new PinDialog(_pinService.Verify, _limiter, owner, _settings.Current.Appearance.AdminTheme);
        if (pinDialog.ShowDialog() != true)
            return new AdminResult(false, false);

        AppLog.Info("Admin-Ansicht geöffnet.");

        // Stand vor dem Öffnen: Daran erkennt das Hauptfenster, ob nach dem Speichern ein Kamera-Neustart nötig ist.
        var cameraBefore = _settings.Current.Camera.Clone();
        // Der Ton (Mikrofon an/aus bzw. anderes Mikrofon) wird beim Sessionstart festgelegt: Auch das erfordert einen Kamera-Neustart.
        var videoBefore = _settings.Current.Clone().Video;

        var ui = new WpfAdminUi();
        using var viewModel = new AdminViewModel(_settings, _pinService, _camera, _print, ui, displays: _displays,
            detection: _detection, sound: _sound);
        var window = new AdminWindow(viewModel);
        if (owner.IsLoaded)
            window.Owner = owner;
        window.LimitSizeToOwnerMonitor();
        ui.Owner = window;

        window.ShowDialog();

        var handling = DecideCameraHandling(viewModel.SettingsSaved, viewModel.ExitRequested, viewModel.CameraTouched,
            cameraBefore, videoBefore, viewModel.LastAppliedCamera, _settings.Current);
        // Nie gespeichert (Schließen): Wurde die Kamera mit Entwurfs-Einstellungen neu gestartet, wieder auf den gespeicherten
        // Stand bringen. (Bei Speichern oder Beenden übernimmt das Hauptfenster bzw. die Anwendung.)
        if (handling.RestoreCamera)
        {
            _ = viewModel.RestoreCameraAsync(_settings.Current.Clone());
        }

        var cameraChanged = handling.CameraChanged;
        AppLog.Info($"Admin-Ansicht geschlossen (gespeichert: {viewModel.SettingsSaved}, Kamera geändert: {cameraChanged}, beenden: {viewModel.ExitRequested}).");
        return new AdminResult(viewModel.SettingsSaved, viewModel.ExitRequested, cameraChanged);
    }

    /// <summary>Was nach dem Schließen der Admin-Ansicht mit der Kamera zu tun ist.</summary>
    /// <param name="RestoreCamera">Die Admin-Ansicht stellt die Kamera selbst auf den gespeicherten Stand zurück (nie gespeichert).</param>
    /// <param name="CameraChanged">Das Hauptfenster startet die Kamera mit den gespeicherten Einstellungen neu (<see cref="AdminResult.CameraChanged"/>).</param>
    public readonly record struct CameraHandling(bool RestoreCamera, bool CameraChanged);

    /// <summary>
    /// Entscheidet, wie die Kamera nach dem Schließen wieder zu den gespeicherten Einstellungen kommt – genau auf einem Weg,
    /// damit sie nicht doppelt neu startet:
    /// <list type="bullet">
    /// <item>Nie gespeichert (nur Schließen): Hat die Admin-Ansicht die Kamera mit Entwurfswerten gestartet, stellt sie
    /// sie selbst zurück (<see cref="CameraHandling.RestoreCamera"/>); das Hauptfenster startet nichts.</item>
    /// <item>Mindestens einmal gespeichert (Übernehmen und/oder Speichern &amp; schließen): Das Hauptfenster startet die Kamera
    /// neu, wenn sich die gespeicherten Einstellungen gegenüber dem Öffnen geändert haben oder die Kamera zuletzt mit etwas anderem
    /// gestartet wurde – das deckt auch eine nach dem Übernehmen verstellte und beim Schließen verworfene Kamera ab (letzter
    /// Start ≠ gespeicherter Stand) sowie einen übernommenen Ton-/Mikrofonwechsel.</item>
    /// </list>
    /// Beim Beenden der Anwendung wird nichts unternommen.
    /// </summary>
    public static CameraHandling DecideCameraHandling(bool settingsSaved, bool exitRequested, bool cameraTouched,
        CameraSettings cameraBefore, VideoSettings videoBefore, CameraSettings? lastApplied, AppSettings saved)
    {
        var restore = !settingsSaved && !exitRequested && cameraTouched;
        var cameraChanged = settingsSaved
            && (CameraNeedsRestart(cameraBefore, lastApplied, saved.Camera)
                || videoBefore.RequiresCameraRestartComparedTo(saved.Video));
        return new CameraHandling(restore, cameraChanged);
    }

    /// <summary>Unterscheiden sich die Kameraeinstellungen so, dass die Kamera neu gestartet werden muss? (Spiegeln zählt nicht.)</summary>
    public static bool CameraSettingsDiffer(CameraSettings a, CameraSettings b) => a.RequiresRestartComparedTo(b);

    /// <summary>
    /// Muss die Gastansicht die Kamera nach dem Speichern neu starten? Ja, wenn sich die gespeicherten Einstellungen gegenüber
    /// dem Stand beim Öffnen geändert haben, oder wenn die Admin-Ansicht die Kamera zuletzt mit etwas anderem gestartet hat
    /// (z. B. nach schnellem Hin- und Herwechseln: Speichern verwarf den Neustart auf das ursprüngliche Gerät, die Kamera
    /// läuft noch mit dem zwischenzeitlich gewählten).
    /// </summary>
    public static bool CameraNeedsRestart(CameraSettings before, CameraSettings? lastApplied, CameraSettings saved) =>
        CameraSettingsDiffer(before, saved) || (lastApplied is not null && CameraSettingsDiffer(lastApplied, saved));

    private static bool ShowRetry(Window? owner, string message)
    {
        var result = owner is { IsLoaded: true }
            ? MessageBox.Show(owner, message, AppInfo.DisplayName, MessageBoxButton.OKCancel, MessageBoxImage.Error)
            : MessageBox.Show(message, AppInfo.DisplayName, MessageBoxButton.OKCancel, MessageBoxImage.Error);
        return result == MessageBoxResult.OK;
    }

    private static void ShowError(Window? owner, string message)
    {
        if (owner is { IsLoaded: true })
            MessageBox.Show(owner, message, AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
        else
            MessageBox.Show(message, AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
