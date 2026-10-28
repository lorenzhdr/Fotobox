using System.IO;
using CommunityToolkit.Mvvm.Input;
using Fotobox.Core;
using Fotobox.Services.Storage;

namespace Fotobox.ViewModels.Admin;

// Einstellungen teilen – Export und Import (Tab „Sicherheit“)
public sealed partial class AdminViewModel
{
    private const string TransferHint = "Die Änderungen gelten erst nach „Übernehmen“ bzw. „Speichern & schließen“.";

    private bool _exportIncludePin;
    private bool _isTransferring;
    private AsyncRelayCommand? _exportSettingsCommand;
    private AsyncRelayCommand? _importSettingsCommand;

    /// <summary>
    /// Aus einer importierten Datei stammende, bestätigte, aber noch nicht gespeicherte PIN. Wird beim Speichern in den Store
    /// geschrieben (eine zugleich eingetragene neue PIN hat Vorrang) und von „Standardwerte wiederherstellen“ verworfen.
    /// </summary>
    private SecuritySettings? _pendingImportedSecurity;

    /// <summary>Beim Export die PIN (als Prüfwert) mit in die Datei schreiben. Nicht gespeichert, Standard aus.</summary>
    public bool ExportIncludePin
    {
        get => _exportIncludePin;
        set => SetProperty(ref _exportIncludePin, value);
    }

    /// <summary>Ein Export oder Import läuft (Dialog, Lesen/Schreiben der Datei): beide Knöpfe sind dann gesperrt.</summary>
    public bool IsTransferring
    {
        get => _isTransferring;
        private set
        {
            if (SetProperty(ref _isTransferring, value))
                NotifyTransferCommands();
        }
    }

    /// <summary>Exportiert den aktuellen Entwurf (auch ungespeichert) in eine Datei.</summary>
    public IAsyncRelayCommand ExportSettingsCommand =>
        _exportSettingsCommand ??= new AsyncRelayCommand(ExportSettingsAsync, CanTransfer);

    /// <summary>Liest eine Einstellungsdatei in den Entwurf (gilt erst nach dem Speichern).</summary>
    public IAsyncRelayCommand ImportSettingsCommand =>
        _importSettingsCommand ??= new AsyncRelayCommand(ImportSettingsAsync, CanTransfer);

    private bool CanTransfer() => !_isSaving && !_isTransferring && !_disposed;

    private void NotifyTransferCommands()
    {
        _exportSettingsCommand?.NotifyCanExecuteChanged();
        _importSettingsCommand?.NotifyCanExecuteChanged();
    }

    // ---------------------------------------------------------------- Export

    private async Task ExportSettingsAsync()
    {
        if (!CanTransfer())
            return;

        IsTransferring = true;
        try
        {
            await ExportSettingsCoreAsync();
        }
        catch (Exception ex)
        {
            // Kein Fehler darf als unbehandelte Ausnahme aus dem Befehl in den Dispatcher laufen (globaler Handler, Absturz).
            AppLog.Error("Einstellungen konnten nicht exportiert werden.", ex);
            ShowTransferInfo("Export fehlgeschlagen", "Die Einstellungen konnten nicht exportiert werden: " + ex.Message);
        }
        finally
        {
            IsTransferring = false;
        }
    }

    private async Task ExportSettingsCoreAsync()
    {
        // Eine PIN nur auf ausdrücklichen Wunsch und nur, wenn eine gesetzt ist (Prüfwert aus dem Store, nicht aus dem Entwurf).
        var stored = _store.Current.Security;
        var hasPin = !string.IsNullOrEmpty(stored.PinHash) && !string.IsNullOrEmpty(stored.PinSalt);
        var includePin = _exportIncludePin && hasPin;
        var pin = includePin ? CopySecurity(stored) : null;

        var path = _ui.PickExportFile(SettingsTransfer.SuggestedFileName(_time.GetLocalNow().DateTime));
        if (string.IsNullOrWhiteSpace(path))
            return;

        string json;
        try
        {
            // Der aktuelle (auch ungespeicherte) Entwurf; Serialize normalisiert eine Kopie und entfernt die Gerätefelder.
            json = SettingsTransfer.Serialize(_draft, pin, AppInfo.Version, _time.GetUtcNow());
            await SettingsTransfer.WriteFileAsync(path, json);
        }
        catch (TimeoutException)
        {
            AppLog.Warn("Export der Einstellungen: Zeitüberschreitung beim Schreiben.");
            ShowTransferInfo("Export fehlgeschlagen",
                "Die Datei konnte nicht rechtzeitig geschrieben werden (Zeitüberschreitung). Bitte wählen Sie einen anderen Speicherort.");
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Export der Einstellungen fehlgeschlagen.", ex);
            ShowTransferInfo("Export fehlgeschlagen", "Die Datei konnte nicht geschrieben werden: " + ex.Message);
            return;
        }

        var pinLine = includePin
            ? "Die Datei enthält die Admin-PIN (als Prüfwert). Bitte geben Sie sie nur an Personen weiter, denen Sie vertrauen."
            : _exportIncludePin
                ? "Es ist keine PIN festgelegt, daher enthält die Datei keine PIN."
                : "Die Datei enthält keine PIN.";
        ShowTransferInfo("Einstellungen exportiert",
            $"Die Einstellungen wurden gespeichert unter:\n{path}\n\n" +
            "Kamera, Drucker, Speicherorte, Mikrofon, Bildschirm und das Overlay-Bild sind nicht enthalten.\n" + pinLine);
    }

    // ---------------------------------------------------------------- Import

    private async Task ImportSettingsAsync()
    {
        if (!CanTransfer())
            return;

        IsTransferring = true;
        try
        {
            await ImportSettingsCoreAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Einstellungen konnten nicht importiert werden.", ex);
            ShowTransferInfo("Import fehlgeschlagen", "Die Einstellungen konnten nicht importiert werden: " + ex.Message);
        }
        finally
        {
            IsTransferring = false;
        }
    }

    private async Task ImportSettingsCoreAsync()
    {
        var path = _ui.PickImportFile();
        if (string.IsNullOrWhiteSpace(path))
            return;

        SettingsImportResult result;
        try
        {
            var json = await SettingsTransfer.ReadFileAsync(path);
            if (_disposed || _closeRequested)
                return;
            result = SettingsTransfer.Parse(json);
        }
        catch (SettingsImportException ex)
        {
            ShowTransferInfo("Import fehlgeschlagen", ex.Message);
            return;
        }
        catch (TimeoutException)
        {
            AppLog.Warn("Import der Einstellungen: Zeitüberschreitung beim Lesen.");
            ShowTransferInfo("Import fehlgeschlagen", "Die Datei konnte nicht rechtzeitig gelesen werden (Zeitüberschreitung).");
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Import der Einstellungen fehlgeschlagen.", ex);
            ShowTransferInfo("Import fehlgeschlagen", "Die Datei konnte nicht gelesen werden: " + ex.Message);
            return;
        }

        if (!_ui.Confirm("Einstellungen importieren",
                "Alle Einstellungen außer Kamera, Drucker, Speicherorten, Mikrofon und Bildschirm werden durch die Datei ersetzt. " +
                "Die Änderungen gelten erst nach „Übernehmen“ bzw. „Speichern & schließen“. Fortfahren?"))
            return;

        // Eine abweichende PIN in der Datei nur nach ausdrücklicher Bestätigung (Standard: Nein).
        SecuritySettings? acceptedPin = null;
        var pinDeclined = false;
        if (result.Pin is { } filePin && !SameSecurity(filePin, _store.Current.Security))
        {
            if (_ui.Confirm("Admin-PIN importieren", "Die Datei enthält eine Admin-PIN. Soll sie die aktuelle PIN ersetzen?"))
                acceptedPin = CopySecurity(filePin);
            else
                pinDeclined = true;
        }

        // Die Geräte und Ordner des Zielrechners (hier: des Entwurfs) bleiben; ein fehlendes Overlay-Bild fällt auf ein Layout ohne Overlay zurück.
        var imported = result.Sections;
        DeviceSpecificSettings.CopyFrom(_draft, imported);
        var overlayMissing = SettingsTransfer.ApplyOverlayFallback(imported,
            name => File.Exists(Path.Combine(_store.OverlayDirectory, name)));

        SettingsSections.CopyPortableSections(imported, _draft);
        // Jeder Import entscheidet über die PIN neu: Eine vorgemerkte PIN eines früheren Imports gilt nicht weiter.
        _pendingImportedSecurity = acceptedPin;
        // Geräte bleiben gewählt, die Kamera wird nie neu gestartet; nur die Seitengröße hängt vom Layout ab.
        RebuildAfterDraftReplaced(keepDeviceSelections: true);
        RequestPageSpec();

        var lines = new List<string>(result.Notes);
        if (overlayMissing)
            lines.Add(SettingsTransfer.OverlayMissingNote);
        if (acceptedPin is not null)
            lines.Add("Die Admin-PIN aus der Datei ersetzt die aktuelle PIN, sobald Sie speichern.");
        else if (pinDeclined)
            lines.Add("Die Admin-PIN aus der Datei wurde nicht übernommen.");
        lines.Add(TransferHint);
        ShowTransferInfo("Einstellungen importiert", string.Join(Environment.NewLine, lines));
    }

    // ---------------------------------------------------------------- Hilfen

    private static SecuritySettings CopySecurity(SecuritySettings s) => new()
    {
        PinHash = s.PinHash,
        PinSalt = s.PinSalt,
        PinIterations = s.PinIterations,
    };

    private static bool SameSecurity(SecuritySettings a, SecuritySettings b) =>
        string.Equals(a.PinHash, b.PinHash, StringComparison.Ordinal)
        && string.Equals(a.PinSalt, b.PinSalt, StringComparison.Ordinal)
        && a.PinIterations == b.PinIterations;

    /// <summary>Zeigt einen Hinweis; scheitert der Dialog, wird nur geloggt (Export/Import dürfen daran nicht hängen).</summary>
    private void ShowTransferInfo(string title, string message)
    {
        if (_disposed)
            return;
        try
        {
            _ui.ShowInfo(title, message);
        }
        catch (Exception ex)
        {
            AppLog.Error("Hinweis zu Export/Import konnte nicht angezeigt werden.", ex);
        }
    }
}
