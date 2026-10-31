using System.Globalization;
using System.Text.RegularExpressions;
using Fotobox.Services.Detection.Acceleration;

namespace Fotobox.Services.Diagnostics;

/// <summary>Ein Grafik-/KI-Adapter, wie ihn D3DKMT meldet (Eigenschaften zum Auswählen; keine Handles).</summary>
internal sealed record GraphicsAdapterInfo(
    ulong Luid,
    uint VendorId,
    uint DeviceId,
    string? Name,
    bool RenderSupported,
    bool DisplaySupported,
    bool SoftwareDevice,
    bool ComputeOnly,
    ulong DedicatedVideoMemoryBytes = 0,
    ulong LocalMemoryBytes = 0,
    ulong NonLocalMemoryBytes = 0);

/// <summary>Zerlegter Instanzname des Zählers „GPU Engine“.</summary>
internal readonly record struct GpuEngineInstance(ulong Luid, int ProcessId, int Physical, int Engine, string EngineType);

/// <summary>
/// Reine Auswertungslogik der Systemwerte (ohne Win32; ohne Hardware testbar): Instanznamen der PDH-Zähler zerlegen, je Adapter
/// aggregieren, Plausibilitäten, Adapterauswahl.
/// </summary>
internal static class SystemMetricsLogic
{
    /// <summary>Plausible Temperatur in °C (außerhalb gilt der Wert als nicht gemessen).</summary>
    internal const double MinPlausibleTemperatureC = 1;

    internal const double MaxPlausibleTemperatureC = 130;

    /// <summary>Ab dieser dedizierten Speichergröße gilt eine Grafikkarte als „mit eigenem Videospeicher“ (diskret).</summary>
    internal const ulong DiscreteLocalMemoryBytes = 512UL * 1024 * 1024;

    private const double KelvinOffset = 273.15;

    // pid_N_luid_0xHHHHHHHH_0xLLLLLLLL_phys_P_eng_E_engtype_X
    private static readonly Regex EngineInstanceRegex = new(
        @"^pid_(?<pid>\d{1,10})_luid_0x(?<hi>[0-9A-Fa-f]{1,8})_0x(?<lo>[0-9A-Fa-f]{1,8})_phys_(?<phys>\d{1,10})_eng_(?<eng>\d{1,10})_engtype_(?<type>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // luid_0xHHHHHHHH_0xLLLLLLLL_phys_P
    private static readonly Regex AdapterInstanceRegex = new(
        @"^luid_0x(?<hi>[0-9A-Fa-f]{1,8})_0x(?<lo>[0-9A-Fa-f]{1,8})_phys_(?<phys>\d{1,10})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>LUID aus High- und Low-Teil (so heißen die beiden Hälften auch im PDH-Instanznamen).</summary>
    internal static ulong MakeLuid(uint high, uint low) => ((ulong)high << 32) | low;

    /// <summary>Zerlegt einen Instanznamen von „GPU Engine“. false bei Müll (null, leer, falsches Muster, Zahl zu groß).</summary>
    internal static bool TryParseEngineInstance(string? name, out GpuEngineInstance instance)
    {
        instance = default;
        if (string.IsNullOrWhiteSpace(name))
            return false;
        try
        {
            var m = EngineInstanceRegex.Match(name.Trim());
            if (!m.Success)
                return false;
            if (!int.TryParse(m.Groups["pid"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                || !int.TryParse(m.Groups["phys"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var phys)
                || !int.TryParse(m.Groups["eng"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var eng)
                || !uint.TryParse(m.Groups["hi"].ValueSpan, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hi)
                || !uint.TryParse(m.Groups["lo"].ValueSpan, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var lo))
                return false;
            instance = new GpuEngineInstance(MakeLuid(hi, lo), pid, phys, eng, m.Groups["type"].Value);
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Zerlegt einen Instanznamen von „GPU Adapter Memory“ zu (LUID, physischer Adapter). false bei Müll.</summary>
    internal static bool TryParseAdapterInstance(string? name, out ulong luid, out int physical)
    {
        luid = 0;
        physical = 0;
        if (string.IsNullOrWhiteSpace(name))
            return false;
        try
        {
            var m = AdapterInstanceRegex.Match(name.Trim());
            if (!m.Success)
                return false;
            if (!int.TryParse(m.Groups["phys"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out physical)
                || !uint.TryParse(m.Groups["hi"].ValueSpan, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hi)
                || !uint.TryParse(m.Groups["lo"].ValueSpan, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var lo))
                return false;
            luid = MakeLuid(hi, lo);
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Auslastung je Adapter (LUID) aus den Instanzen von „GPU Engine\Utilization Percentage“: je (LUID, physischer Adapter, Engine)
    /// über alle Prozesse summieren, der Adapterwert ist das Maximum seiner Engines (wie im Task-Manager), auf 0..100 geklemmt.
    /// Unlesbare Namen und nicht endliche Werte werden übersprungen; negative Werte zählen als 0.
    /// </summary>
    internal static Dictionary<ulong, double> AggregateEngineUtilization(IEnumerable<(string Name, double Value)> items)
    {
        var perEngine = new Dictionary<(ulong Luid, int Phys, int Eng), double>();
        foreach (var (name, value) in items)
        {
            if (!double.IsFinite(value) || !TryParseEngineInstance(name, out var id))
                continue;
            var key = (id.Luid, id.Physical, id.Engine);
            perEngine[key] = perEngine.GetValueOrDefault(key) + Math.Max(0, value);
        }

        var perAdapter = new Dictionary<ulong, double>();
        foreach (var ((luid, _, _), sum) in perEngine)
        {
            var clamped = Math.Clamp(sum, 0, 100);
            perAdapter[luid] = perAdapter.TryGetValue(luid, out var best) ? Math.Max(best, clamped) : clamped;
        }
        return perAdapter;
    }

    /// <summary>
    /// Summe je Adapter (LUID) aus den Instanzen von „GPU Adapter Memory“ (Dedicated bzw. Shared Usage; über die physischen Adapter
    /// summiert). Unlesbare Namen, nicht endliche und negative Werte werden übersprungen.
    /// </summary>
    internal static Dictionary<ulong, double> AggregateAdapterMemory(IEnumerable<(string Name, double Value)> items)
    {
        var result = new Dictionary<ulong, double>();
        foreach (var (name, value) in items)
        {
            if (!double.IsFinite(value) || value < 0 || !TryParseAdapterInstance(name, out var luid, out _))
                continue;
            result[luid] = result.GetValueOrDefault(luid) + value;
        }
        return result;
    }

    /// <summary>Prozentwert auf 0..100 klemmen; null bei NaN/unendlich.</summary>
    internal static double? ClampPercent(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : null;

    /// <summary>
    /// CPU-Takt in MHz: Basisfrequenz mal „% Processor Performance“ / 100 (die Leistung kann über 100 liegen, Turbo). null, wenn
    /// eine der beiden Größen fehlt oder nicht positiv ist.
    /// </summary>
    internal static double? ComputeCpuClockMhz(double baseFrequencyMhz, double performancePercent)
    {
        if (!double.IsFinite(baseFrequencyMhz) || !double.IsFinite(performancePercent) || baseFrequencyMhz <= 0 || performancePercent <= 0)
            return null;
        return baseFrequencyMhz * performancePercent / 100.0;
    }

    /// <summary>Temperatur in °C nur, wenn plausibel (1..130), sonst null.</summary>
    internal static double? PlausibleTemperatureC(double celsius) =>
        double.IsFinite(celsius) && celsius >= MinPlausibleTemperatureC && celsius <= MaxPlausibleTemperatureC ? celsius : null;

    /// <summary>Höchste plausible Temperatur aller Thermal Zones (Zähler liefern Kelvin); null, wenn keine plausibel ist.</summary>
    internal static double? HighestZoneCelsius(IEnumerable<double> kelvin)
    {
        double? best = null;
        foreach (var k in kelvin)
        {
            if (PlausibleTemperatureC(k - KelvinOffset) is not { } c)
                continue;
            if (best is null || c > best)
                best = c;
        }
        return best;
    }

    /// <summary>Takt in MHz aus Hertz, nur wenn positiv und endlich.</summary>
    internal static double? PlausibleClockMhzFromHz(double hertz) =>
        double.IsFinite(hertz) && hertz > 0 ? hertz / 1_000_000.0 : null;

    /// <summary>
    /// GPU-Speicher (belegt, gesamt). Mit eigenem Videospeicher (dedizierte Größe ≥ 512 MiB, diskrete Karte): Dedicated Usage von der
    /// dedizierten Größe. Sonst (integrierte Grafik bzw. gemeinsamer Speicher): Dedicated + Shared Usage von lokal + nicht lokal.
    /// „Gesamt“ ist null, wenn die Größe nicht bekannt ist (0); „belegt“ ist null, wenn die Zähler fehlen.
    /// </summary>
    internal static (double? Used, double? Total) ComputeGpuMemory(
        double? dedicatedUsage, double? sharedUsage, ulong dedicatedVideoBytes, ulong localBytes, ulong nonLocalBytes)
    {
        if (dedicatedVideoBytes >= DiscreteLocalMemoryBytes)
            return (dedicatedUsage, dedicatedVideoBytes);

        double? used = dedicatedUsage is null && sharedUsage is null ? null : (dedicatedUsage ?? 0) + (sharedUsage ?? 0);
        var total = localBytes + nonLocalBytes;
        return (used, total > 0 ? total : null);
    }

    /// <summary>Software-Adapter (WARP / „Microsoft Basic Render Driver“) oder als solcher gemeldet.</summary>
    internal static bool IsSoftware(GraphicsAdapterInfo a) =>
        a.SoftwareDevice || AcceleratorSelector.IsSoftwareAdapter(a.VendorId, a.DeviceId, a.Name);

    /// <summary>
    /// Wählt die Grafikkarte: Mit Auswahl der Adapter mit gleicher Hersteller-/Geräte-Id (bei mehreren gleichen der mit gleichem
    /// Namen, sonst der erste). Ohne Auswahl oder ohne Treffer der erste Adapter mit Render und Anzeige. Software-Adapter und reine
    /// Rechen-Adapter (NPU) werden nie ohne ausdrückliche Auswahl gewählt, Software-Adapter nie.
    /// </summary>
    internal static GraphicsAdapterInfo? SelectGpu(IReadOnlyList<GraphicsAdapterInfo> adapters, GpuSelection? selection)
    {
        var real = adapters.Where(a => !IsSoftware(a)).ToList();

        if (selection is not null)
        {
            var matches = real.Where(a => a.VendorId == selection.VendorId && a.DeviceId == selection.DeviceId).ToList();
            if (matches.Count > 1 && !string.IsNullOrWhiteSpace(selection.Name))
            {
                var byName = matches.FirstOrDefault(a => string.Equals(a.Name?.Trim(), selection.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (byName is not null)
                    return byName;
            }
            if (matches.Count > 0)
                return matches[0];
        }

        return real.FirstOrDefault(a => a.RenderSupported && a.DisplaySupported && !a.ComputeOnly);
    }

    /// <summary>Die NPU: der erste reine Rechen-Adapter (ComputeOnly), der kein Software-Adapter ist; sonst null.</summary>
    internal static GraphicsAdapterInfo? SelectNpu(IReadOnlyList<GraphicsAdapterInfo> adapters) =>
        adapters.FirstOrDefault(a => a.ComputeOnly && !IsSoftware(a));
}
