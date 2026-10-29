namespace Fotobox.Core.Music;

/// <summary>Namen und Endungen der Kisscam-Musik im Musikordner.</summary>
public static class KisscamMusicFiles
{
    /// <summary>Dateiname der Config im Musikordner (sichtbar, enthält Häkchen, Startzeit und Song-Lautstärke).</summary>
    public const string ConfigFileName = "kisscam-musik.json";

    /// <summary>Dateiname, unter dem eine unlesbare Config beim nächsten Speichern aufbewahrt wird.</summary>
    public const string BrokenConfigFileName = ConfigFileName + ".defekt";

    /// <summary>Berücksichtigte Dateiendungen (klein, mit Punkt). Unterordner werden nicht durchsucht.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".mp3", ".wav", ".m4a", ".wma"];

    /// <summary>Ist <paramref name="fileName"/> eine Musikdatei (nach Endung, ohne Beachtung der Groß-/Kleinschreibung)?</summary>
    public static bool IsSong(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return false;
        var extension = System.IO.Path.GetExtension(fileName);
        foreach (var known in Extensions)
        {
            if (string.Equals(extension, known, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

/// <summary>Die gespeicherten Werte eines Songs in <c>kisscam-musik.json</c>; <see cref="FileName"/> ist der reine Dateiname im Musikordner.</summary>
public sealed class KisscamSongConfig
{
    public string FileName { get; set; } = string.Empty;

    /// <summary>Der Song darf in der Kisscam gespielt werden (neue Songs: nein).</summary>
    public bool Enabled { get; set; }

    /// <summary>Startzeit in Sekunden (<see cref="SettingsLimits.KisscamSongStartSecondsMin"/> bis <c>Max</c>).</summary>
    public int StartSeconds { get; set; }

    /// <summary>Lautstärke des Songs in Prozent der Gesamtlautstärke (<see cref="SettingsLimits.KisscamSongVolumeMin"/> bis <c>Max</c>).</summary>
    public int VolumePercent { get; set; } = SettingsLimits.KisscamSongVolumeDefault;

    public KisscamSongConfig Clone() => (KisscamSongConfig)MemberwiseClone();
}

/// <summary>Der Inhalt von <c>kisscam-musik.json</c>.</summary>
public sealed class KisscamMusicConfig
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public List<KisscamSongConfig> Songs { get; set; } = [];

    public KisscamMusicConfig Clone() => new()
    {
        Version = Version,
        Songs = Songs.Select(s => s.Clone()).ToList(),
    };
}
