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
    private readonly PinAttemptLimiter _limiter;

    public AdminLauncher(ISettingsStore settings, IPinService pinService, ICameraService camera, IPrintService print)
        : this(settings, pinService, camera, print, TimeProvider.System)
    {
    }

    public AdminLauncher(ISettingsStore settings, IPinService pinService, ICameraService camera, IPrintService print, TimeProvider time)
    {
        _settings = settings;
        _pinService = pinService;
        _camera = camera;
        _print = print;
        // Ein Limiter pro Prozess: Die Sperre überdauert das Schließen des PIN-Dialogs.
        _limiter = new PinAttemptLimiter(time, maxAttempts: 5, lockoutDuration: TimeSpan.FromSeconds(30));
    }

    public bool EnsurePinConfigured(Window? owner)
    {
        if (_pinService.HasPin)
            return true;

        while (true)
        {
            var dialog = new FirstRunPinDialog(owner);
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
        if (!_pinService.HasPin && !EnsurePinConfigured(owner))
            return new AdminResult(false, false, false);

        var pinDialog = new PinDialog(_pinService.Verify, _limiter, owner);
        if (pinDialog.ShowDialog() != true)
            return new AdminResult(false, false, false);

        AppLog.Info("Admin-Ansicht geöffnet.");

        var ui = new WpfAdminUi();
        using var viewModel = new AdminViewModel(_settings, _pinService, _camera, _print, ui);
        var window = new AdminWindow(viewModel);
        if (owner.IsLoaded)
            window.Owner = owner;
        ui.Owner = window;

        window.ShowDialog();

        // Abbrechen: Wurde die Kamera mit Entwurfs-Einstellungen neu gestartet, wieder auf den gespeicherten Stand bringen.
        // (Bei Speichern oder Beenden übernimmt das Hauptfenster bzw. die Anwendung.)
        if (!viewModel.SettingsSaved && !viewModel.ExitRequested && viewModel.CameraTouched)
        {
            var saved = _settings.Current.Camera;
            _ = viewModel.RestoreCameraAsync(new CameraSettings
            {
                DeviceId = saved.DeviceId,
                ResolutionWidth = saved.ResolutionWidth,
                ResolutionHeight = saved.ResolutionHeight,
                MirrorPreview = saved.MirrorPreview,
                MirrorPhoto = saved.MirrorPhoto,
            });
        }

        AppLog.Info($"Admin-Ansicht geschlossen (gespeichert: {viewModel.SettingsSaved}, beenden: {viewModel.ExitRequested}).");
        return new AdminResult(true, viewModel.SettingsSaved, viewModel.ExitRequested);
    }

    private static void ShowError(Window? owner, string message)
    {
        if (owner is { IsLoaded: true })
            MessageBox.Show(owner, message, "Fotobox", MessageBoxButton.OK, MessageBoxImage.Error);
        else
            MessageBox.Show(message, "Fotobox", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
