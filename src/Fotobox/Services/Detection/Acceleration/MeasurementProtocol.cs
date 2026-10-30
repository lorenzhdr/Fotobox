using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fotobox.Core;

namespace Fotobox.Services.Detection.Acceleration;

/// <summary>
/// Vereinbarung zwischen Hauptprozess und Messprozess (<c>Fotobox.exe --beschleunigung-messen &lt;Auftragsdatei&gt;</c>): Die Auftragsdatei
/// (JSON) nennt die zu messenden Geräte (Schlüssel, Reihenfolge = Messreihenfolge), die Ergebnisdatei und die Priorität. Der Messprozess
/// schreibt zeilenweise JSON auf die Standardausgabe (<see cref="MeasureMessage"/>) und am Ende die Ergebnisdatei (temporäre Datei + Ersetzen).
/// </summary>
internal static class MeasureProtocol
{
    /// <summary>Kommandozeilenschalter des Messprozesses (erstes Argument, danach der Pfad der Auftragsdatei).</summary>
    public const string Switch = "--beschleunigung-messen";

    /// <summary>
    /// Nur zum Testen des Absturzpfads (wirkt ausschließlich im Messprozess): Komma-getrennte Teilstrings von Geräteschlüsseln; vor der
    /// Messung eines passenden Geräts beendet sich der Messprozess mit <see cref="Environment.FailFast(string)"/>.
    /// </summary>
    public const string CrashEnvironmentVariable = "FOTOBOX_MESSPROZESS_TESTABSTURZ";

    public const int Format = 1;

    public static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(MeasureMessage message) => JsonSerializer.Serialize(message, Json);

    /// <summary>Eine Zeile der Ausgabe des Messprozesses; null bei Unlesbarem (Zeile wird ignoriert).</summary>
    public static MeasureMessage? TryParse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
            return null;
        try
        {
            var message = JsonSerializer.Deserialize<MeasureMessage>(line, Json);
            return message is { Type.Length: > 0 } ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Schreibt atomar (temporäre Datei + Ersetzen). Wirft bei Fehlern.</summary>
    public static void WriteAtomic<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    public static T? ReadFile<T>(string path) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Auftrag an den Messprozess.</summary>
internal sealed class MeasureJob
{
    public int Format { get; set; } = MeasureProtocol.Format;

    /// <summary>Wohin der Messprozess am Ende alle Einträge schreibt.</summary>
    public string ResultFile { get; set; } = "";

    /// <summary>Niedrige Priorität (Hintergrundmessung): wirkt auf den ganzen Messprozess, also auch auf die Rechen-Threads von ONNX Runtime.</summary>
    public bool LowPriority { get; set; }

    /// <summary>Geräteschlüssel in Messreihenfolge (<c>cpu</c> = Prozessor).</summary>
    public List<string> DeviceKeys { get; set; } = [];
}

/// <summary>Inhalt der Ergebnisdatei.</summary>
internal sealed class MeasureResultFile
{
    public int Format { get; set; } = MeasureProtocol.Format;
    public DateTimeOffset MeasuredAt { get; set; }
    public List<BenchmarkEntry> Entries { get; set; } = [];
}

/// <summary>
/// Eine Ausgabezeile des Messprozesses. <see cref="Type"/>: <c>start</c> (Gerät <see cref="Key"/> beginnt), <c>progress</c> (<see cref="Done"/> von
/// <see cref="Total"/> Modell-Messungen des Auftrags), <c>entry</c> (Messwert eines Modells), <c>deviceDone</c> (Gerät fertig), <c>log</c>
/// (<see cref="Level"/>, <see cref="Text"/>), <c>done</c> (alles gemessen, Ergebnisdatei geschrieben).
/// </summary>
internal sealed class MeasureMessage
{
    [JsonPropertyName("t")]
    public string Type { get; set; } = "";
    public string? Key { get; set; }
    public string? Name { get; set; }
    public int Done { get; set; }
    public int Total { get; set; }
    public BenchmarkEntry? Entry { get; set; }
    public string? Level { get; set; }
    public string? Text { get; set; }
}
