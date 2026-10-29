using System.Text.Json;

namespace Fotobox.Core.Music;

/// <summary>
/// Liest, normalisiert und schreibt <c>kisscam-musik.json</c>. Werte werden über <see cref="SettingsLimits"/> geklemmt, Dateinamen
/// ohne Beachtung der Groß-/Kleinschreibung dedupliziert (der erste gewinnt). Wirft nie außer in <see cref="Write"/> bei einem
/// Programmfehler; unlesbarer Inhalt ist kein Fehler, sondern der Rückgabewert <c>false</c> von <see cref="TryParse"/>.
/// </summary>
public static class KisscamMusicConfigCodec
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Liest die Config. <c>false</c> = der Text ist kein gültiges JSON in der erwarteten Form (leer, abgeschnitten, falsche Typen);
    /// die Datei gilt dann als defekt. Unbekannte Felder werden ignoriert, ein fehlendes Songfeld ergibt den Standard.
    /// </summary>
    public static bool TryParse(string? json, out KisscamMusicConfig config)
    {
        config = new KisscamMusicConfig();
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            var dto = JsonSerializer.Deserialize<ConfigDto>(json, ReadOptions);
            if (dto is null)
                return false;
            var result = new KisscamMusicConfig();
            foreach (var song in dto.Songs ?? [])
            {
                if (song is null)
                    continue;
                result.Songs.Add(new KisscamSongConfig
                {
                    FileName = song.File ?? string.Empty,
                    Enabled = song.Enabled ?? false,
                    StartSeconds = song.StartSeconds ?? 0,
                    VolumePercent = song.VolumePercent ?? SettingsLimits.KisscamSongVolumeDefault,
                });
            }
            config = Normalize(result);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Bereinigt eine Config: ungültige oder doppelte Dateinamen entfallen, Werte werden geklemmt, die Version ist
    /// <see cref="KisscamMusicConfig.CurrentVersion"/>, die Songs sind nach Dateiname sortiert. Verändert das Original nicht.
    /// </summary>
    public static KisscamMusicConfig Normalize(KisscamMusicConfig? config)
    {
        var result = new KisscamMusicConfig();
        if (config?.Songs is null)
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var song in config.Songs)
        {
            var name = song?.FileName?.Trim();
            if (song is null || !IsPlainFileName(name) || !seen.Add(name!))
                continue;
            result.Songs.Add(new KisscamSongConfig
            {
                FileName = name!,
                Enabled = song.Enabled,
                StartSeconds = ClampStart(song.StartSeconds),
                VolumePercent = ClampVolume(song.VolumePercent),
            });
            if (result.Songs.Count >= SettingsLimits.KisscamMusicMaxSongs)
                break;
        }
        result.Songs.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FileName, b.FileName));
        return result;
    }

    /// <summary>Schreibt die (normalisierte) Config als eingerücktes JSON.</summary>
    public static string Write(KisscamMusicConfig config)
    {
        var normalized = Normalize(config);
        var dto = new ConfigDto
        {
            Version = KisscamMusicConfig.CurrentVersion,
            Songs = normalized.Songs.Select(s => (SongDto?)new SongDto
            {
                File = s.FileName,
                Enabled = s.Enabled,
                StartSeconds = s.StartSeconds,
                VolumePercent = s.VolumePercent,
            }).ToList(),
        };
        return JsonSerializer.Serialize(dto, WriteOptions);
    }

    /// <summary>Startzeit auf [<see cref="SettingsLimits.KisscamSongStartSecondsMin"/>, <c>Max</c>] klemmen.</summary>
    public static int ClampStart(int seconds) =>
        Math.Clamp(seconds, SettingsLimits.KisscamSongStartSecondsMin, SettingsLimits.KisscamSongStartSecondsMax);

    /// <summary>Song-Lautstärke auf ein Vielfaches des Schritts runden und auf [Min, Max] klemmen.</summary>
    public static int ClampVolume(int percent)
    {
        var step = SettingsLimits.KisscamSongVolumeStep;
        var rounded = (int)Math.Round(percent / (double)step, MidpointRounding.AwayFromZero) * step;
        return Math.Clamp(rounded, SettingsLimits.KisscamSongVolumeMin, SettingsLimits.KisscamSongVolumeMax);
    }

    // Nur ein reiner Dateiname im Musikordner (kein Pfad, keine unzulässigen Zeichen).
    private static bool IsPlainFileName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= 260
        && name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0
        && name is not ("." or "..");

    private sealed class ConfigDto
    {
        public int? Version { get; set; }
        public List<SongDto?>? Songs { get; set; }
    }

    private sealed class SongDto
    {
        public string? File { get; set; }
        public bool? Enabled { get; set; }
        public int? StartSeconds { get; set; }
        public int? VolumePercent { get; set; }
    }
}
