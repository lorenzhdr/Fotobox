// Stub – wird von Stream D implementiert
using System.IO;
using System.Text.Json;
using Fotobox.Core;

namespace Fotobox.Services.Storage;

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
        var copy = settings.Clone();
        lock (_gate)
        {
            var tmp = _settingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(copy, AppSettings.JsonOptions));
            File.Move(tmp, _settingsPath, overwrite: true);
            _current = copy;
        }
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), AppSettings.JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppLog.Error("settings.json konnte nicht gelesen werden – Standardwerte werden verwendet.", ex);
        }
        return new AppSettings();
    }
}
