using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using Fotobox.Core;

namespace Fotobox.Services.Detection.Acceleration;

/// <summary>Ein Modell, das auf dem gewählten Gerät schon in der Messung gescheitert ist (läuft deshalb auf der CPU), mit Grund.</summary>
internal sealed record PlanFailure(ModelId Model, AcceleratorDevice Device, string Reason);

/// <summary>Ergebnis der Planung: je Modell das Gerät (null = CPU); <paramref name="Failures"/> = in der Messung gescheiterte Modelle des gewählten Geräts.</summary>
internal sealed record PlanDecision(IReadOnlyDictionary<ModelId, AcceleratorDevice?> Plan, string? Notice, bool NeedsMeasurement, bool FromCache,
    IReadOnlyList<PlanFailure>? Failures = null);

/// <summary>Entscheidet reine Logik ohne Hardware: welches Modell bei welchem Modus auf welches Gerät kommt.</summary>
internal static class AccelerationPlanner
{
    /// <summary>
    /// Im Modus Auto muss ein Gerät mindestens 15 % schneller als die CPU sein (Laufzeit höchstens 1/1,15 der CPU-Laufzeit). Die kleinen
    /// Modelle schwanken von Lauf zu Lauf um einige Prozent; für einen knappen Gewinn lohnt weder das Risiko eines Treiberfehlers noch
    /// der längere Start (Ladezeit der Geräte-Session), also gewinnt bei Gleichstand die CPU.
    /// </summary>
    public const double MinimumSpeedup = 1.15;

    public static IReadOnlyList<ModelId> AllModels { get; } = Enum.GetValues<ModelId>();

    public static PlanDecision Decide(AcceleratorMode mode, string? gpuKey, IReadOnlyList<AcceleratorDevice> devices, BenchmarkResult? cached)
    {
        switch (mode)
        {
            case AcceleratorMode.Cpu:
                return new PlanDecision(AllOn(null), null, false, false);

            case AcceleratorMode.Gpu:
            {
                var gpu = AcceleratorSelector.SelectGpu(devices, gpuKey);
                if (gpu is not null)
                    return ForChosenDevice(gpu, "Die Grafikkarte", cached);
                var notice = gpuKey is null
                    ? "Es wurde keine Grafikkarte gefunden – die Erkennung läuft auf der CPU."
                    : "Die gewählte Grafikkarte wurde nicht gefunden – die Erkennung läuft auf der CPU.";
                return new PlanDecision(AllOn(null), notice, false, false);
            }

            case AcceleratorMode.Npu:
            {
                var npu = AcceleratorSelector.SelectNpu(devices);
                if (npu is not null)
                    return ForChosenDevice(npu, "Die KI-Einheit (NPU)", cached);
                return new PlanDecision(AllOn(null), "Es wurde keine KI-Einheit (NPU) gefunden – die Erkennung läuft auf der CPU.", false, false);
            }

            default:
                // Auto: ohne weiteres Gerät gibt es nichts zu messen.
                if (devices.Count(d => !d.IsCpu) == 0)
                    return new PlanDecision(AllOn(null), null, false, false);
                if (cached is null)
                    return new PlanDecision(AllOn(null), null, true, false);
                return new PlanDecision(FromBenchmark(cached, devices), null, false, true);
        }
    }

    /// <summary>
    /// Ausdrücklich gewähltes Gerät (Grafikkarte/NPU): Es wird im Hauptprozess nur geladen, wenn die Messung im Messprozess (Cache mit
    /// gleichem Fingerabdruck) das Modell darauf erfolgreich geschafft hat. Fehlt jede Messung → dieser Start auf der CPU, im Hintergrund
    /// wird geprüft (<see cref="PlanDecision.NeedsMeasurement"/>). Gescheitert (auch abgestürzt) → CPU mit Grund, ohne bei jedem Start neu
    /// zu messen (der Admin kann mit „Jetzt messen“ wiederholen). Anders als im Modus Auto zählt hier keine Mindestgeschwindigkeit.
    /// </summary>
    private static PlanDecision ForChosenDevice(AcceleratorDevice device, string label, BenchmarkResult? cached)
    {
        var plan = new Dictionary<ModelId, AcceleratorDevice?>();
        var failures = new List<PlanFailure>();
        var unverified = false;
        foreach (var model in AllModels)
        {
            var entry = cached?.Find(device.Key, model);
            if (entry is null)
            {
                unverified = true;
                plan[model] = null;
            }
            else if (entry.Succeeded)
            {
                plan[model] = device;
            }
            else
            {
                plan[model] = null;
                failures.Add(new PlanFailure(model, device, string.IsNullOrWhiteSpace(entry.Error) ? "Messung fehlgeschlagen" : entry.Error));
            }
        }

        if (unverified)
        {
            return new PlanDecision(AllOn(null), $"{label} wird zuerst geprüft und ab dem nächsten Start genutzt.", NeedsMeasurement: true, FromCache: cached is not null);
        }
        if (failures.Count > 0)
        {
            return new PlanDecision(plan,
                $"{label} hat die Prüfung nicht bestanden – die Erkennung läuft auf dem Prozessor. Mit „Jetzt messen“ können Sie die Prüfung wiederholen.",
                NeedsMeasurement: false, FromCache: true, failures);
        }
        return new PlanDecision(plan, null, NeedsMeasurement: false, FromCache: true);
    }

    private static Dictionary<ModelId, AcceleratorDevice?> AllOn(AcceleratorDevice? device) =>
        AllModels.ToDictionary(m => m, _ => device is { IsCpu: true } ? null : device);

    /// <summary>
    /// Je Modell das schnellste Gerät der Messung, wenn es mindestens <see cref="MinimumSpeedup"/>-mal schneller als die CPU ist; sonst
    /// die CPU. Fehlt eine gültige CPU-Messung, zählt das schnellste gültige Gerät. Nur Geräte aus <paramref name="devices"/>.
    /// </summary>
    public static IReadOnlyDictionary<ModelId, AcceleratorDevice?> FromBenchmark(BenchmarkResult result, IReadOnlyList<AcceleratorDevice> devices)
    {
        var plan = new Dictionary<ModelId, AcceleratorDevice?>();
        foreach (var model in AllModels)
        {
            var cpu = result.Find(AcceleratorDevice.CpuKey, model);
            var cpuAverage = cpu is { Succeeded: true, AverageMs: > 0 } ? cpu.AverageMs : (double?)null;

            AcceleratorDevice? best = null;
            var bestAverage = double.MaxValue;
            foreach (var device in devices.Where(d => !d.IsCpu))
            {
                var entry = result.Find(device.Key, model);
                if (entry is { Succeeded: true, AverageMs: > 0 } && entry.AverageMs < bestAverage)
                {
                    best = device;
                    bestAverage = entry.AverageMs;
                }
            }

            plan[model] = best is not null && (cpuAverage is null || bestAverage * MinimumSpeedup <= cpuAverage.Value) ? best : null;
        }
        return plan;
    }

    /// <summary>Gleiche Geräteschlüssel je Modell?</summary>
    public static bool SamePlan(IReadOnlyDictionary<ModelId, AcceleratorDevice?> a, IReadOnlyDictionary<ModelId, AcceleratorDevice?> b) =>
        AllModels.All(m => string.Equals(KeyOf(a, m), KeyOf(b, m), StringComparison.Ordinal));

    private static string KeyOf(IReadOnlyDictionary<ModelId, AcceleratorDevice?> plan, ModelId model) =>
        plan.TryGetValue(model, out var device) && device is { IsCpu: false } ? device.Key : AcceleratorDevice.CpuKey;
}

/// <summary>Die Cache-Datei <c>beschleunigung.json</c> (Messergebnis des Auto-Modus). Lesen und Schreiben sind fehlertolerant.</summary>
internal static class AccelerationCache
{
    public const string FileName = "beschleunigung.json";
    // 2: Die Messwerte entstehen im Messprozess (ältere, im Hauptprozess gemessene Dateien gelten nicht als dort erfolgreich geprüft).
    private const int CurrentFormat = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class CacheFile
    {
        public int Format { get; set; }
        public string Fingerprint { get; set; } = "";
        public DateTimeOffset MeasuredAt { get; set; }
        public List<BenchmarkEntry> Entries { get; set; } = [];
    }

    /// <summary>
    /// Fingerabdruck der Umgebung: Geräte (Schlüssel + Version), bereitgestellte Ausführungsanbieter, App- und ONNX-Runtime-Version.
    /// Ändert sich etwas davon (Treiber-, Hardware-, Programmwechsel), gilt der Cache nicht mehr.
    /// </summary>
    public static string Fingerprint(IEnumerable<AcceleratorDevice> devices, IEnumerable<ProviderState> providers, string appVersion, string ortVersion)
    {
        var parts = new List<string>();
        parts.AddRange(devices.Where(d => !d.IsCpu).Select(d => $"dev:{d.Key}={d.Version}").OrderBy(p => p, StringComparer.Ordinal));
        parts.AddRange(providers.Where(p => p.IsReady).Select(p => $"ep:{p.Name}={p.PackageId}").OrderBy(p => p, StringComparer.Ordinal));
        parts.Add("app:" + appVersion);
        parts.Add("ort:" + ortVersion);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))))[..32];
    }

    /// <summary>Gültiges Messergebnis oder null (fehlt, defekt, anderer Fingerabdruck). Wirft nie.</summary>
    public static BenchmarkResult? TryRead(string path, string fingerprint)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path), JsonOptions);
            if (file is null || file.Format != CurrentFormat || file.Entries is null)
            {
                AppLog.Warn("Beschleunigung: Die Cache-Datei hat ein unbekanntes Format und wird neu gemessen.");
                return null;
            }
            if (!string.Equals(file.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                AppLog.Info("Beschleunigung: Die Cache-Datei passt nicht mehr zu diesem System (Geräte, Treiber oder Programmversion geändert).");
                return null;
            }
            if (file.Entries.Any(e => e is null || e.DeviceKey is null || !Enum.IsDefined(e.Model) || double.IsNaN(e.AverageMs) || double.IsInfinity(e.AverageMs)))
            {
                AppLog.Warn("Beschleunigung: Die Cache-Datei enthält ungültige Werte und wird neu gemessen.");
                return null;
            }
            return new BenchmarkResult(file.MeasuredAt, file.Entries);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Die Cache-Datei ist nicht lesbar und wird neu gemessen.", ex);
            return null;
        }
    }

    /// <summary>Schreibt atomar (temporäre Datei + Ersetzen). true = geschrieben. Wirft nie.</summary>
    public static bool TryWrite(string path, string fingerprint, BenchmarkResult result)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var file = new CacheFile { Format = CurrentFormat, Fingerprint = fingerprint, MeasuredAt = result.MeasuredAt, Entries = [.. result.Entries] };
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Die Cache-Datei konnte nicht geschrieben werden.", ex);
            return false;
        }
    }
}
