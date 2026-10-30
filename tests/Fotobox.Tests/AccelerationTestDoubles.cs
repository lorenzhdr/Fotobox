using System.IO;
using Fotobox.Core;
using Fotobox.Services.Detection.Acceleration;

namespace Fotobox.Tests;

/// <summary>Testdoubles der Beschleunigung: Einstellungen, Katalog und Messung ohne Hardware. Alle Gerätenamen sind erfunden.</summary>
internal static class AccelDevices
{
    public static AcceleratorDevice Gpu(string name, bool discrete = false, int index = int.MaxValue, long memory = 0, string ep = AcceleratorDevice.DirectMlEpName, uint vendor = 1, uint device = 1) =>
        new(AcceleratorSelector.BuildKey(ep, vendor, device, name), name, AcceleratorKind.Gpu, ep, discrete, index, memory, null, vendor, device);

    public static AcceleratorDevice Npu(string name) =>
        new(AcceleratorSelector.BuildKey("TestNpuProvider", 9, 9, name), name, AcceleratorKind.Npu, "TestNpuProvider", false, int.MaxValue, 0, null, 9, 9);

    public static IReadOnlyList<AcceleratorDevice> WithCpu(params AcceleratorDevice[] devices) => [AcceleratorDevice.CpuDevice, .. devices];

    /// <summary>Messtabelle: je Gerät und Modell eine mittlere Laufzeit (Millisekunden); null = Fehler.</summary>
    public static BenchmarkResult Result(params (AcceleratorDevice Device, double? Yolox, double? YuNet, double? Palm, double? Landmark)[] rows)
    {
        var entries = new List<BenchmarkEntry>();
        foreach (var (device, yolox, yunet, palm, landmark) in rows)
        {
            void Add(ModelId model, double? ms) =>
                entries.Add(new BenchmarkEntry(device.Key, device.Name, model, 100, ms ?? 0, ms is null ? "Testfehler" : null));
            Add(ModelId.Yolox, yolox);
            Add(ModelId.YuNet, yunet);
            Add(ModelId.Palm, palm);
            Add(ModelId.Landmark, landmark);
        }
        return new BenchmarkResult(DateTimeOffset.UtcNow, entries);
    }
}

internal sealed class AccelSettingsStore : ISettingsStore
{
    public string ConfigDirectory { get; init; } = Path.GetTempPath();
    public string OverlayDirectory => ConfigDirectory;
    public AppSettings Current { get; } = new();
    public event EventHandler? SettingsChanged;
    public void Save(AppSettings settings) => SettingsChanged?.Invoke(this, EventArgs.Empty);
    public void RaiseChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class FakeAcceleratorCatalog(IReadOnlyList<AcceleratorDevice> devices) : IAcceleratorCatalog
{
    public IReadOnlyList<AcceleratorDevice> Devices { get; private set; } = devices;
    public IReadOnlyList<AcceleratorDevice> AfterInstall { get; set; } = devices;
    public IReadOnlyList<ProviderState> Providers { get; set; } = [];
    public Exception? DiscoverFailure { get; set; }
    public Task? DiscoverGate { get; set; }
    public ProviderInstallResult InstallResult { get; set; } = new(true, "ok", ["Testanbieter"]);

    public async Task<IReadOnlyList<AcceleratorDevice>> DiscoverAsync(CancellationToken ct = default)
    {
        if (DiscoverGate is not null)
            await DiscoverGate;
        if (DiscoverFailure is not null)
            throw DiscoverFailure;
        return Devices;
    }

    public IReadOnlyList<AcceleratorDevice> RefreshDevices() => Devices = AfterInstall;

    public IReadOnlyList<ProviderState> GetProviderStates() => Providers;

    public Task<ProviderInstallResult> InstallVendorProvidersAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(1);
        return Task.FromResult(InstallResult);
    }
}

internal sealed class FakeAcceleratorBenchmark(BenchmarkResult? result = null) : IAcceleratorBenchmark
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public BenchmarkResult? Result { get; set; } = result;
    public TimeSpan Duration { get; set; } = TimeSpan.Zero;
    public bool? LastLowPriority { get; private set; }

    public async Task<BenchmarkResult> RunAsync(IReadOnlyList<AcceleratorDevice> devices, bool lowPriority, IProgress<BenchmarkProgress>? progress, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        LastLowPriority = lowPriority;
        progress?.Report(new BenchmarkProgress(1, 4, devices.Count > 0 ? devices[0].Name : "", null));
        try
        {
            if (Duration > TimeSpan.Zero)
                await Task.Delay(Duration, ct);
        }
        catch (OperationCanceledException)
        {
            return new BenchmarkResult(DateTimeOffset.UtcNow, [], Cancelled: true);
        }
        return Result ?? new BenchmarkResult(DateTimeOffset.UtcNow, []);
    }
}

internal sealed class FakePlacement(AcceleratorDevice? device = null) : IModelPlacement
{
    public List<(ModelId Model, string Device, string Reason, bool AtRuntime)> Fallbacks { get; } = [];
    public AcceleratorDevice? GetDevice(ModelId model) => device;
    public void ReportFallback(ModelId model, AcceleratorDevice dev, string reason, bool atRuntime)
    {
        lock (Fallbacks)
            Fallbacks.Add((model, dev.Name, reason, atRuntime));
    }
}
