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

/// <summary>
/// Direkter Fake des <see cref="IAccelerationService"/> für die Tests des Admin-Tabs „Leistung“: Der Zustand ist frei setzbar,
/// <see cref="Raise"/> darf von jedem Thread kommen, Messung und Installation sind sofort fertig oder warten auf ein TaskCompletionSource
/// (und beenden sich beim Abbruch). Alle Gerätenamen sind erfunden.
/// </summary>
internal sealed class FakeAccelerationService : IAccelerationService
{
    private readonly object _lock = new();
    private AccelerationState _state;
    private int _benchmarkCalls;
    private int _installCalls;

    public FakeAccelerationService(AccelerationState? state = null) => _state = state ?? AccelerationState.Initial;

    public AccelerationState State
    {
        get { lock (_lock) return _state; }
    }

    public event EventHandler? StateChanged;

    public int SubscriberCount => StateChanged?.GetInvocationList().Length ?? 0;

    /// <summary>Ändert den Zustand und meldet es (auf dem aufrufenden Thread).</summary>
    public void Update(Func<AccelerationState, AccelerationState> change)
    {
        lock (_lock)
            _state = change(_state);
        Raise();
    }

    public void Raise() => StateChanged?.Invoke(this, EventArgs.Empty);

    public Task StartAsync() => Task.CompletedTask;

    /// <summary>Ein fertiger Plan: alle vier Modelle auf der CPU (bzw. auf den gegebenen Plätzen).</summary>
    public static AccelerationState Ready(IReadOnlyList<AcceleratorDevice> devices, AcceleratorMode mode = AcceleratorMode.Auto,
        IReadOnlyList<ModelPlacement>? plan = null) =>
        AccelerationState.Initial with
        {
            Mode = mode,
            PlanReady = true,
            Devices = devices,
            Plan = plan ?? CpuPlan(),
        };

    public static IReadOnlyList<ModelPlacement> CpuPlan() =>
        Enum.GetValues<ModelId>().Select(m => new ModelPlacement(m, "CPU", AcceleratorKind.Cpu, AcceleratorDevice.CpuKey, false)).ToList();

    // ---- Messung

    public int BenchmarkCalls => Volatile.Read(ref _benchmarkCalls);
    public IProgress<BenchmarkProgress>? LastBenchmarkProgress { get; private set; }
    public CancellationToken LastBenchmarkToken { get; private set; }

    /// <summary>Ergebnis, wenn die Messung nicht wartet.</summary>
    public BenchmarkResult? BenchmarkOutcome { get; set; }

    /// <summary>Gesetzt: Die Messung wartet auf dieses Ergebnis (oder den Abbruch).</summary>
    public TaskCompletionSource<BenchmarkResult>? BenchmarkGate { get; set; }

    public async Task<BenchmarkResult> RunBenchmarkAsync(IProgress<BenchmarkProgress>? progress = null, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _benchmarkCalls);
        LastBenchmarkProgress = progress;
        LastBenchmarkToken = ct;
        if (BenchmarkGate is { } gate)
        {
            await using var registration = ct.Register(() => gate.TrySetResult(new BenchmarkResult(DateTimeOffset.UtcNow, [], Cancelled: true)));
            return await gate.Task;
        }
        return BenchmarkOutcome ?? new BenchmarkResult(DateTimeOffset.UtcNow, []);
    }

    // ---- Installation

    public int InstallCalls => Volatile.Read(ref _installCalls);
    public IProgress<double>? LastInstallProgress { get; private set; }
    public CancellationToken LastInstallToken { get; private set; }
    public ProviderInstallResult InstallOutcome { get; set; } = new(true, "Die Ausführungsanbieter wurden installiert.", ["Testanbieter"]);
    public TaskCompletionSource<ProviderInstallResult>? InstallGate { get; set; }

    public async Task<ProviderInstallResult> InstallVendorProvidersAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _installCalls);
        LastInstallProgress = progress;
        LastInstallToken = ct;
        if (InstallGate is { } gate)
        {
            await using var registration = ct.Register(() => gate.TrySetResult(new ProviderInstallResult(false, "Die Installation wurde abgebrochen.", [], Cancelled: true)));
            return await gate.Task;
        }
        return InstallOutcome;
    }

    // ---- Anbieter

    public Func<IReadOnlyList<ProviderState>> Providers { get; set; } = () => [];
    public int ProviderThreadId { get; private set; }
    public bool ProviderOnThreadPool { get; private set; }

    public IReadOnlyList<ProviderState> GetProviderStates()
    {
        ProviderThreadId = Environment.CurrentManagedThreadId;
        ProviderOnThreadPool = Thread.CurrentThread.IsThreadPoolThread;
        return Providers();
    }

    // ---- IModelPlacement

    public AcceleratorDevice? GetDevice(ModelId model) => null;

    public void ReportFallback(ModelId model, AcceleratorDevice device, string reason, bool atRuntime)
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
