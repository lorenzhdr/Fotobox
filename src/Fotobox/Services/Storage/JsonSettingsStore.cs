using System.IO;
using System.Text;
using System.Text.Json;
using Fotobox.Core;

namespace Fotobox.Services.Storage;

/// <summary>Einstellungen als settings.json in %LOCALAPPDATA%\Fotobox (atomar geschrieben, thread-sicher).</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly object _gate = new();
    private readonly string _settingsPath;
    private AppSettings _current;

    public JsonSettingsStore(string? configDirectory = null)
    {
        ConfigDirectory = string.IsNullOrWhiteSpace(configDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fotobox")
            : configDirectory;
        OverlayDirectory = Path.Combine(ConfigDirectory, "overlays");
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(OverlayDirectory);
        _settingsPath = Path.Combine(ConfigDirectory, "settings.json");
        _current = Load();
    }

    public string ConfigDirectory { get; }
    public string OverlayDirectory { get; }

    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler? SettingsChanged;

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = SettingsNormalizer.Normalize(settings.Clone());
        var json = JsonSerializer.Serialize(copy, AppSettings.JsonOptions);

        lock (_gate)
        {
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

    private AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
            return SettingsNormalizer.Normalize(new AppSettings());

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions)
                         ?? throw new JsonException("settings.json enthält keinen Inhalt.");
            return SettingsNormalizer.Normalize(loaded);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            BackupBrokenFile();
            AppLog.Warn("settings.json ist defekt – Standardwerte werden verwendet.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("settings.json konnte nicht gelesen werden – Standardwerte werden verwendet.", ex);
        }
        return SettingsNormalizer.Normalize(new AppSettings());
    }

    private void BackupBrokenFile()
    {
        try
        {
            var backup = $"{_settingsPath}.bak-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(_settingsPath, backup, overwrite: true);
            AppLog.Info($"Defekte Einstellungsdatei gesichert als {Path.GetFileName(backup)}.");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Defekte settings.json konnte nicht gesichert werden.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* best effort */ }
    }
}
