using System.Globalization;
using Fotobox.Services.Detection.Acceleration;

namespace Fotobox.ViewModels.Admin;

/// <summary>
/// Anzeigetexte des Admin-Tabs „Leistung“ (Modellnamen, Gerätearten, Zeiten). Eine Quelle für Statuszeilen, Auswahl und Messtabelle;
/// reine Funktionen, deshalb ohne Oberfläche testbar. Die Zahlen werden immer deutsch formatiert (Dezimalkomma).
/// </summary>
public static class AccelerationDisplay
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Deutscher Name des Modells bzw. der Funktion, die es bedient.</summary>
    public static string ModelName(ModelId model) => model switch
    {
        ModelId.Yolox => "Personenerkennung (Zielerfassung)",
        ModelId.YuNet => "Gesichtserkennung",
        ModelId.Palm => "Handflächen",
        ModelId.Landmark => "Handpunkte",
        _ => model.ToString(),
    };

    /// <summary>Kurzname der Geräteart.</summary>
    public static string KindName(AcceleratorKind kind) => kind switch
    {
        AcceleratorKind.Cpu => "CPU",
        AcceleratorKind.Gpu => "GPU",
        AcceleratorKind.Npu => "NPU",
        _ => kind.ToString(),
    };

    /// <summary>Gerät mit Art, z. B. „Name (GPU)“; der Prozessor heißt „Prozessor (CPU)“.</summary>
    public static string PlacementText(AcceleratorKind kind, string deviceName) =>
        kind == AcceleratorKind.Cpu ? "Prozessor (CPU)" : $"{deviceName} ({KindName(kind)})";

    /// <summary>Mittlere Laufzeit als „x,x ms“.</summary>
    public static string FormatMs(double milliseconds) => milliseconds.ToString("0.0", German) + " ms";

    /// <summary>Datum und Uhrzeit einer Messung (Ortszeit).</summary>
    public static string FormatTime(DateTimeOffset time) => time.ToLocalTime().ToString("dd.MM.yyyy HH:mm", German);

    /// <summary>Videospeicher in Megabyte als Text („512 MB“, „8 GB“); 0 = unbekannt (leer).</summary>
    public static string FormatMemory(long megabytes)
    {
        if (megabytes <= 0)
            return string.Empty;
        return megabytes >= 1024
            ? (megabytes / 1024.0).ToString("0.#", German) + " GB"
            : megabytes.ToString(German) + " MB";
    }

    /// <summary>Verständlicher Name des Ausführungsanbieters (DirectML bzw. der technische Name).</summary>
    public static string ProviderLabel(string epName) =>
        string.Equals(epName, AcceleratorDevice.DirectMlEpName, StringComparison.Ordinal) ? "DirectML" : epName;

    /// <summary>Eintrag der Grafikkartenauswahl: „Name (dediziert, 8 GB) – Anbieter“.</summary>
    public static string GpuLabel(AcceleratorDevice device)
    {
        var details = new List<string> { device.IsDiscrete ? "dediziert" : "integriert" };
        var memory = FormatMemory(device.VideoMemory);
        if (memory.Length > 0)
            details.Add(memory);
        return $"{device.Name} ({string.Join(", ", details)}) – {ProviderLabel(device.EpName)}";
    }
}
