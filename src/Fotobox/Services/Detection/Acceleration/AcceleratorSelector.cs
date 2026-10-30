using System.Globalization;
using System.Text;

namespace Fotobox.Services.Detection.Acceleration;

/// <summary>Bildet stabile Geräteschlüssel und wählt Geräte aus (reine Logik, ohne Hardware testbar).</summary>
public static class AcceleratorSelector
{
    private const int KeyPartMaxLength = 80;

    /// <summary>
    /// Stabiler Schlüssel: Ausführungsanbieter, Hersteller-Id, Geräte-Id und Beschreibung, normiert (klein, nur Buchstaben, Ziffern
    /// und einfache Satzzeichen, Leerraum zusammengefasst). Enthält absichtlich weder LUID noch Adapterindex (ändern sich je Start).
    /// </summary>
    public static string BuildKey(string epName, uint vendorId, uint deviceId, string? description) =>
        string.Create(CultureInfo.InvariantCulture, $"{Normalize(epName)}|{vendorId:x4}|{deviceId:x4}|{Normalize(description)}");

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var sb = new StringBuilder();
        var lastSpace = false;
        foreach (var c in text.Trim().ToLowerInvariant())
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace && sb.Length > 0)
                    sb.Append(' ');
                lastSpace = true;
                continue;
            }
            lastSpace = false;
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '(' or ')' or '+' ? c : '-');
            if (sb.Length >= KeyPartMaxLength)
                break;
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Die Grafikkarte mit dem Schlüssel; <paramref name="key"/> null = die leistungsstärkste. Null, wenn es sie nicht gibt.</summary>
    public static AcceleratorDevice? SelectGpu(IReadOnlyList<AcceleratorDevice> devices, string? key)
    {
        var gpus = devices.Where(d => d.Kind == AcceleratorKind.Gpu).ToList();
        if (key is not null)
            return gpus.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));
        return RankGpus(gpus).FirstOrDefault();
    }

    /// <summary>Die erste KI-Einheit (NPU); null, wenn es keine gibt.</summary>
    public static AcceleratorDevice? SelectNpu(IReadOnlyList<AcceleratorDevice> devices) =>
        devices.FirstOrDefault(d => d.Kind == AcceleratorKind.Npu);

    /// <summary>
    /// Grafikkarten nach Leistung: diskrete vor integrierten, dann der kleinere <c>HighPerformanceIndex</c> (Windows-Ranking), dann
    /// mehr Videospeicher, dann der Hersteller-Ausführungsanbieter vor DirectML (gleicher Adapter), dann der Schlüssel. Ein Gerät
    /// eines Hersteller-Anbieters ohne eigene Adapter-Metadaten erbt sie vom DirectML-Gerät mit gleicher Hersteller-/Geräte-Id.
    /// </summary>
    public static IReadOnlyList<AcceleratorDevice> RankGpus(IEnumerable<AcceleratorDevice> gpus)
    {
        var list = gpus.Where(d => d.Kind == AcceleratorKind.Gpu).ToList();
        return list
            .Select(d => (Device: d, Meta: EffectiveMeta(d, list)))
            .OrderByDescending(x => x.Meta.Discrete)
            .ThenBy(x => x.Meta.Index)
            .ThenByDescending(x => x.Meta.Memory)
            .ThenByDescending(x => !string.Equals(x.Device.EpName, AcceleratorDevice.DirectMlEpName, StringComparison.Ordinal))
            .ThenBy(x => x.Device.Key, StringComparer.Ordinal)
            .Select(x => x.Device)
            .ToList();
    }

    private static (bool Discrete, int Index, long Memory) EffectiveMeta(AcceleratorDevice d, List<AcceleratorDevice> all)
    {
        if (d.HighPerformanceIndex != int.MaxValue || d.VideoMemory > 0 || d.IsDiscrete)
            return (d.IsDiscrete, d.HighPerformanceIndex, d.VideoMemory);
        var sibling = all.FirstOrDefault(o => !ReferenceEquals(o, d) && o.VendorId == d.VendorId && o.DeviceId == d.DeviceId
                                              && (o.HighPerformanceIndex != int.MaxValue || o.VideoMemory > 0 || o.IsDiscrete));
        return sibling is null ? (false, int.MaxValue, 0) : (sibling.IsDiscrete, sibling.HighPerformanceIndex, sibling.VideoMemory);
    }
}
