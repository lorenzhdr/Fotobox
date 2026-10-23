using System.IO;
using System.Text;
using System.Text.Json;
using Fotobox.Core;

namespace Fotobox.Services.Storage;

/// <summary>
/// Einstellungen als settings.json in %LOCALAPPDATA%\Fotobox (atomar geschrieben, thread-sicher).
/// Fail-closed: Existiert die Datei, ist aber nicht lesbar (z. B. kurz von einem Virenscanner gesperrt), werden nicht
/// stillschweigend Standardwerte (ohne PIN) verwendet, sondern <see cref="LoadFailed"/> gesetzt und <see cref="Save"/> verweigert.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly object _gate = new();
    private const int ReadAttempts = 3;

    private readonly string _settingsPath;
    private readonly TimeSpan _readRetryDelay;
    private AppSettings _current;
    private bool _loadFailed;

    /// <param name="configDirectory">Ordner der Einstellungen (null = %LOCALAPPDATA%\Fotobox).</param>
    /// <param name="readRetryDelay">Pause zwischen den Leseversuchen bei gesperrter Datei (Standard 200 ms).</param>
    public JsonSettingsStore(string? configDirectory = null, TimeSpan? readRetryDelay = null)
    {
        _readRetryDelay = readRetryDelay ?? TimeSpan.FromMilliseconds(200);
        ConfigDirectory = string.IsNullOrWhiteSpace(configDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fotobox")
            : configDirectory;
        OverlayDirectory = Path.Combine(ConfigDirectory, "overlays");
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(OverlayDirectory);
        _settingsPath = Path.Combine(ConfigDirectory, "settings.json");
        var loaded = Load();
        _loadFailed = loaded is null;
        _current = loaded ?? SettingsNormalizer.Normalize(new AppSettings());
    }

    public string ConfigDirectory { get; }
    public string OverlayDirectory { get; }

    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public bool LoadFailed
    {
        get { lock (_gate) return _loadFailed; }
    }

    public event EventHandler? SettingsChanged;

    /// <summary>Liest eine beim Start nicht lesbare Datei erneut. true = Store ist (wieder) einsatzbereit.</summary>
    public bool TryReload()
    {
        lock (_gate)
        {
            if (!_loadFailed)
                return true;

            var loaded = Load();
            if (loaded is null)
                return false;

            _current = loaded;
            _loadFailed = false;
        }

        AppLog.Info("settings.json konnte nach einem Fehler erneut gelesen werden.");
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = SettingsNormalizer.Normalize(settings.Clone());
        var json = JsonSerializer.Serialize(copy, AppSettings.JsonOptions);

        lock (_gate)
        {
            // Die vorhandene Datei war beim Start nicht lesbar: nicht überschreiben (sie enthält ggf. die echte PIN).
            if (_loadFailed)
                throw new IOException("Die Einstellungsdatei konnte nicht gelesen werden und wird deshalb nicht überschrieben. " +
                                      "Bitte die Fotobox neu starten.");

            var tmp = Path.Combine(ConfigDirectory, $"settings.json.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = new StreamWriter(fs, new UTF8Encoding(false), leaveOpen: true))
                        writer.Write(json);
                    fs.Flush(true);
                }

                if (File.Exists(_settingsPath))
                    File.Replace(tmp, _settingsPath, null);
                else
                    File.Move(tmp, _settingsPath);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
            _current = copy;
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Liest die Datei. Fehlt sie, kommen Standardwerte (echter Erststart). Ist sie nicht lesbar, wird kurz wiederholt;
    /// danach <c>null</c> (= fail-closed, siehe <see cref="LoadFailed"/>). Ungültiges JSON wird als .bak gesichert
    /// und durch Standardwerte ersetzt; gelingt die Sicherung nicht, ebenfalls <c>null</c>.
    /// </summary>
    private AppSettings? Load()
    {
        if (!File.Exists(_settingsPath))
            return SettingsNormalizer.Normalize(new AppSettings());

        string json;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                json = File.ReadAllText(_settingsPath);
                break;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Zwischen Prüfung und Lesen gelöscht → wie Erststart.
                return SettingsNormalizer.Normalize(new AppSettings());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReadAttempts)
                {
                    AppLog.Error("settings.json konnte nicht gelesen werden – die vorhandene Datei bleibt unangetastet.", ex);
                    return null;
                }

                AppLog.Warn($"settings.json ist gesperrt (Versuch {attempt}/{ReadAttempts}) – neuer Versuch folgt.", ex);
                Thread.Sleep(_readRetryDelay);
            }
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions)
                         ?? throw new JsonException("settings.json enthält keinen Inhalt.");
            return SettingsNormalizer.Normalize(loaded);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            // Defekte Datei: sichern und mit Standardwerten neu beginnen (Wiederherstellungsweg, die Daten bleiben als .bak erhalten).
            // Kann sie nicht gesichert werden, würde ein späteres Speichern sie ungesichert überschreiben → fail-closed.
            if (!BackupBrokenFile())
                return null;
            AppLog.Warn("settings.json ist defekt – Standardwerte werden verwendet.", ex);
            return SettingsNormalizer.Normalize(new AppSettings());
        }
    }

    private bool BackupBrokenFile()
    {
        try
        {
            var backup = $"{_settingsPath}.bak-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(_settingsPath, backup, overwrite: true);
            AppLog.Info($"Defekte Einstellungsdatei gesichert als {Path.GetFileName(backup)}.");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Defekte settings.json konnte nicht gesichert werden.", ex);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* best effort */ }
    }
}
