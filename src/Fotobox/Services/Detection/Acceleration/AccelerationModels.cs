using Fotobox.Core;
using Microsoft.ML.OnnxRuntime;

namespace Fotobox.Services.Detection.Acceleration;

/// <summary>Art eines Rechengeräts für die KI-Erkennung.</summary>
public enum AcceleratorKind { Cpu, Gpu, Npu }

/// <summary>Die vier ONNX-Modelle der Erkennung (je Modell kann ein anderes Gerät laufen).</summary>
public enum ModelId { Yolox, YuNet, Palm, Landmark }

/// <summary>
/// Ein Rechengerät (CPU, Grafikkarte oder KI-Einheit), auf dem ONNX-Sessions laufen können. <see cref="Key"/> ist über Neustarts
/// stabil (Ausführungsanbieter + Hersteller-/Geräte-Id + Beschreibung; nicht die LUID oder der Adapterindex, die sich ändern).
/// <see cref="VideoMemory"/> in Megabyte (0 = unbekannt), <see cref="HighPerformanceIndex"/> = Windows-Ranking der Grafikkarten (0 = stärkste,
/// <see cref="int.MaxValue"/> = unbekannt), <see cref="Version"/> = Version des Ausführungsanbieters.
/// </summary>
public sealed record AcceleratorDevice(
    string Key,
    string Name,
    AcceleratorKind Kind,
    string EpName,
    bool IsDiscrete = false,
    int HighPerformanceIndex = int.MaxValue,
    long VideoMemory = 0,
    string? Version = null,
    uint VendorId = 0,
    uint DeviceId = 0)
{
    /// <summary>Schlüssel des Prozessors (immer vorhanden, Rückfallziel).</summary>
    public const string CpuKey = "cpu";

    /// <summary>Name des Standard-Ausführungsanbieters für die CPU in ONNX Runtime.</summary>
    public const string CpuEpName = "CPUExecutionProvider";

    /// <summary>Name des DirectML-Ausführungsanbieters.</summary>
    public const string DirectMlEpName = "DmlExecutionProvider";

    /// <summary>Der Prozessor als Gerät.</summary>
    public static AcceleratorDevice CpuDevice { get; } = new(CpuKey, "CPU", AcceleratorKind.Cpu, CpuEpName);

    /// <summary>Das ONNX-Runtime-Gerät (nicht serialisieren). Bei der CPU und bei Test-Geräten null: eine Session darauf scheitert dann.</summary>
    internal OrtEpDevice? Handle { get; init; }

    public bool IsCpu => Kind == AcceleratorKind.Cpu;

    public override string ToString() => $"{Name} ({Kind}, {EpName})";
}

/// <summary>Ein Ausführungsanbieter des Windows-ML-Katalogs (Anzeige für den Admin).</summary>
public sealed record ProviderState(string Name, string PackageId, string ReadyState, bool IsReady);

/// <summary>Ergebnis der Installation der Hersteller-Ausführungsanbieter (braucht Internet).</summary>
public sealed record ProviderInstallResult(bool Success, string Message, IReadOnlyList<string> Providers, bool Cancelled = false);

/// <summary>Ein Messwert: ein Modell auf einem Gerät.</summary>
public sealed record BenchmarkEntry(string DeviceKey, string DeviceName, ModelId Model, double LoadMs, double AverageMs, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>Messtabelle. <see cref="Cancelled"/> = abgebrochen (unvollständig, nie gecacht).</summary>
public sealed record BenchmarkResult(DateTimeOffset MeasuredAt, IReadOnlyList<BenchmarkEntry> Entries, bool Cancelled = false)
{
    /// <summary>Gemessener Eintrag des Geräts für das Modell (null = nicht gemessen).</summary>
    public BenchmarkEntry? Find(string deviceKey, ModelId model) =>
        Entries.FirstOrDefault(e => e.Model == model && string.Equals(e.DeviceKey, deviceKey, StringComparison.Ordinal));
}

/// <summary>Fortschritt der Messung: <see cref="Done"/> von <see cref="Total"/> Modell-Messungen, gerade <see cref="DeviceName"/>/<see cref="Model"/>.</summary>
public sealed record BenchmarkProgress(int Done, int Total, string DeviceName, ModelId? Model);

/// <summary>Das Gerät, auf dem ein Modell in diesem Programmlauf tatsächlich laufen soll bzw. läuft.</summary>
public sealed record ModelPlacement(ModelId Model, string DeviceName, AcceleratorKind Kind, string DeviceKey, bool FellBack);

/// <summary>Ein Rückfall auf die CPU (beim Laden oder zur Laufzeit) mit Grund.</summary>
public sealed record AccelerationFallback(ModelId Model, string DeviceName, string Reason, bool AtRuntime, DateTimeOffset Time);

/// <summary>
/// Momentaufnahme des Beschleunigungsdienstes für die Statusanzeige. <see cref="RestartRequired"/>: Einstellung oder
/// Ausführungsanbieter wurden seit dem Start geändert (wirkt erst nach Neustart). <see cref="RestartRecommended"/>: Modus Auto, Grafikkarte
/// oder KI-Einheit; die letzte Messung ergäbe einen anderen Plan als der laufende.
/// </summary>
public sealed record AccelerationState(
    AcceleratorMode Mode,
    bool PlanReady,
    IReadOnlyList<ModelPlacement> Plan,
    IReadOnlyList<AccelerationFallback> Fallbacks,
    IReadOnlyList<AcceleratorDevice> Devices,
    bool BenchmarkRunning,
    bool BenchmarkIsBackground,
    BenchmarkResult? LastBenchmark,
    string? Notice,
    bool PlanFromCache,
    bool RestartRequired,
    bool RestartRecommended)
{
    public static AccelerationState Initial { get; } = new(
        AcceleratorMode.Auto, false, [], [], [AcceleratorDevice.CpuDevice], false, false, null, null, false, false, false);
}

/// <summary>Welches Gerät ein Modell nutzt und wohin ein Rückfall zu melden ist (vom <see cref="AccelerationService"/> umgesetzt; null in Detektoren = immer CPU).</summary>
public interface IModelPlacement
{
    /// <summary>Gerät für das Modell; null = CPU. Wartet höchstens kurz auf den Plan und gehört nur auf den Threadpool.</summary>
    AcceleratorDevice? GetDevice(ModelId model);

    /// <summary>Meldet, dass das Modell vom Gerät auf die CPU zurückgefallen ist (Laden oder Laufzeit). Wirft nie.</summary>
    void ReportFallback(ModelId model, AcceleratorDevice device, string reason, bool atRuntime);
}

/// <summary>Fristen der Beschleunigung (siehe docs/beschleunigung.md und docs/architektur.md).</summary>
public static class AccelerationTimeouts
{
    /// <summary>Der Katalog (Registrierung der vorhandenen Ausführungsanbieter) und die Geräteliste müssen so schnell fertig sein, sonst nur CPU.</summary>
    public static readonly TimeSpan Discovery = TimeSpan.FromSeconds(10);

    /// <summary>Der Detektor wartet beim ersten Laden höchstens so lange auf den Plan, sonst CPU (und ein Rückfall-Eintrag).</summary>
    public static readonly TimeSpan PlanWait = TimeSpan.FromSeconds(3);

    /// <summary>Session-Erstellung samt Aufwärmlauf auf dem Gerät (je Detektor, gemeinsam für alle seine Modelle); danach Rückfall auf die CPU.</summary>
    public static readonly TimeSpan DeviceLoad = TimeSpan.FromSeconds(10);

    /// <summary>Laden auf der CPU (bisherige Frist der Detektoren).</summary>
    public static readonly TimeSpan CpuLoad = TimeSpan.FromSeconds(15);

    /// <summary>Gesamtfrist eines Detektors beim Laden: Plan + Gerät + CPU-Rückfall.</summary>
    public static readonly TimeSpan DetectorInit = PlanWait + DeviceLoad + CpuLoad;

    /// <summary>Frist der Erkennungsdienste für das Laden eines Detektors: <see cref="DetectorInit"/> plus Reserve.</summary>
    public static readonly TimeSpan ServiceInit = DetectorInit + TimeSpan.FromSeconds(7);

    /// <summary>Messung: je Gerät (alle vier Modelle) und insgesamt.</summary>
    public static readonly TimeSpan BenchmarkPerDevice = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan BenchmarkTotal = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Messprozess (Wachhund des Hauptprozesses): bis zum ersten gemeldeten Gerät (Prozessstart + Geräteliste), über die Frist je Gerät hinaus
    /// (<see cref="BenchmarkPerDevice"/> + diese Reserve) und zwischen zwei Geräten; danach wird der Messprozess beendet (Kill samt Prozessbaum).
    /// </summary>
    public static readonly TimeSpan BenchmarkChildStartup = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan BenchmarkChildDeviceReserve = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan BenchmarkChildBetweenDevices = TimeSpan.FromSeconds(10);

    /// <summary>So lange wartet der Hauptprozess nach der Meldung „fertig“ auf das Ende des Messprozesses, danach Kill (ein nativer Abbau kann hängen).</summary>
    public static readonly TimeSpan BenchmarkChildExit = TimeSpan.FromSeconds(3);

    /// <summary>Installation der Hersteller-Ausführungsanbieter (Download).</summary>
    public static readonly TimeSpan Install = TimeSpan.FromMinutes(10);

    /// <summary>Verzögerung der Hintergrundmessung nach dem Start, damit der Start nicht leidet.</summary>
    public static readonly TimeSpan BackgroundBenchmarkDelay = TimeSpan.FromSeconds(10);
}
