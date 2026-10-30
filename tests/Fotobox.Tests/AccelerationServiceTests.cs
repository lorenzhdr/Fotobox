using System.IO;
using Fotobox.Core;
using Fotobox.Services.Detection.Acceleration;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Plan-Logik und Zustand des <see cref="AccelerationService"/> mit Fake-Katalog und Fake-Messung (ohne Hardware).</summary>
public sealed class AccelerationServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-test-" + Guid.NewGuid().ToString("N"));
    private readonly AcceleratorDevice _gpu = AccelDevices.Gpu("Testkarte", discrete: true, index: 0, device: 1);
    private readonly AcceleratorDevice _weakGpu = AccelDevices.Gpu("Schwache Testkarte", discrete: false, index: 1, device: 2);
    private readonly List<AccelerationService> _services = [];

    public void Dispose()
    {
        foreach (var service in _services)
            service.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private AccelerationService Create(AccelSettingsStore settings, FakeAcceleratorCatalog catalog, FakeAcceleratorBenchmark benchmark,
        string appVersion = "1.0", TimeSpan? planWait = null)
    {
        var service = new AccelerationService(settings, catalog, benchmark, TimeProvider.System, new AccelerationOptions
        {
            CacheFilePath = Path.Combine(_dir, AccelerationCache.FileName),
            BackgroundBenchmarkDelay = TimeSpan.Zero,
            AppVersion = appVersion,
            OrtVersion = () => "ort-test",
            PlanWait = planWait,
        });
        _services.Add(service);
        return service;
    }

    private static AccelSettingsStore Settings(AcceleratorMode mode, string? gpuKey = null)
    {
        var store = new AccelSettingsStore();
        store.Current.Performance.Accelerator = mode;
        store.Current.Performance.GpuDeviceKey = gpuKey;
        return store;
    }

    private static async Task WaitUntil(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Bedingung wurde nicht rechtzeitig erfüllt.");
            await Task.Delay(10);
        }
    }

    private BenchmarkResult GpuTwiceAsFast() => AccelDevices.Result(
        (AcceleratorDevice.CpuDevice, 10, 10, 10, 10),
        (_gpu, 5, 5, 5, 5));

    // ------------------------------------------------------------------ Modi

    [Fact]
    public async Task Cpu_Mode_PutsEverythingOnTheCpu_AndNeverMeasures()
    {
        var benchmark = new FakeAcceleratorBenchmark(GpuTwiceAsFast());
        var service = Create(Settings(AcceleratorMode.Cpu), new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)), benchmark);

        await service.StartAsync();

        Assert.True(service.State.PlanReady);
        Assert.All(Enum.GetValues<ModelId>(), m => Assert.Null(service.GetDevice(m)));
        Assert.All(service.State.Plan, p => Assert.Equal(AcceleratorKind.Cpu, p.Kind));
        Assert.Equal(4, service.State.Plan.Count);
        Assert.Equal(0, benchmark.Calls);
    }

    [Fact]
    public async Task Gpu_Mode_UsesTheStrongestGpu_ForEveryModel()
    {
        var service = Create(Settings(AcceleratorMode.Gpu), new FakeAcceleratorCatalog(AccelDevices.WithCpu(_weakGpu, _gpu)), new FakeAcceleratorBenchmark());

        await service.StartAsync();

        Assert.All(Enum.GetValues<ModelId>(), m => Assert.Same(_gpu, service.GetDevice(m)));
        Assert.All(service.State.Plan, p => Assert.Equal(("Testkarte", AcceleratorKind.Gpu, _gpu.Key), (p.DeviceName, p.Kind, p.DeviceKey)));
        Assert.Null(service.State.Notice);
        Assert.Equal(AcceleratorMode.Gpu, service.State.Mode);
    }

    [Fact]
    public async Task Gpu_Mode_ChosenKeyIsHonoured()
    {
        var service = Create(Settings(AcceleratorMode.Gpu, _weakGpu.Key), new FakeAcceleratorCatalog(AccelDevices.WithCpu(_weakGpu, _gpu)), new FakeAcceleratorBenchmark());

        await service.StartAsync();

        Assert.Same(_weakGpu, service.GetDevice(ModelId.Yolox));
    }

    [Fact]
    public async Task Gpu_Mode_ChosenKeyMissing_RunsOnCpuWithNotice()
    {
        var service = Create(Settings(AcceleratorMode.Gpu, "weg|0000|0000|karte"), new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)), new FakeAcceleratorBenchmark());

        await service.StartAsync();

        Assert.Null(service.GetDevice(ModelId.Yolox));
        Assert.NotNull(service.State.Notice);
        Assert.All(service.State.Plan, p => Assert.Equal(AcceleratorKind.Cpu, p.Kind));
    }

    [Fact]
    public async Task Npu_Mode_UsesTheNpu_OrFallsBackWithNotice()
    {
        var npu = AccelDevices.Npu("Test-KI-Einheit");
        var withNpu = Create(Settings(AcceleratorMode.Npu), new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu, npu)), new FakeAcceleratorBenchmark());
        var withoutNpu = Create(Settings(AcceleratorMode.Npu), new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)), new FakeAcceleratorBenchmark());

        await withNpu.StartAsync();
        await withoutNpu.StartAsync();

        Assert.Same(npu, withNpu.GetDevice(ModelId.Palm));
        Assert.Equal(AcceleratorKind.Npu, withNpu.State.Plan[0].Kind);
        Assert.Null(withoutNpu.GetDevice(ModelId.Palm));
        Assert.NotNull(withoutNpu.State.Notice);
    }

    // ------------------------------------------------------------------ Auto: Cache und Messung

    [Fact]
    public async Task Auto_WithoutCache_StartsOnCpu_MeasuresInBackground_AndWritesCacheForTheNextStart()
    {
        var benchmark = new FakeAcceleratorBenchmark(GpuTwiceAsFast());
        var catalog = new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu));
        var service = Create(Settings(AcceleratorMode.Auto), catalog, benchmark);

        await service.StartAsync();

        Assert.Null(service.GetDevice(ModelId.Yolox)); // dieser Start: CPU
        Assert.False(service.State.PlanFromCache);
        await WaitUntil(() => service.State.LastBenchmark is not null && !service.State.BenchmarkRunning);
        Assert.Equal(1, benchmark.Calls);
        Assert.True(benchmark.LastLowPriority);          // Hintergrundmessung
        Assert.True(service.State.RestartRecommended);   // die Messung ergäbe einen anderen Plan
        Assert.Null(service.GetDevice(ModelId.Yolox));   // der laufende Plan ändert sich nicht
        Assert.True(File.Exists(Path.Combine(_dir, AccelerationCache.FileName)));
        Assert.False(File.Exists(Path.Combine(_dir, AccelerationCache.FileName + ".tmp")));

        // Nächster Start mit demselben System: Treffer, keine neue Messung, Gerät aus dem Cache.
        var nextBenchmark = new FakeAcceleratorBenchmark();
        var next = Create(Settings(AcceleratorMode.Auto), new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)), nextBenchmark);
        await next.StartAsync();

        Assert.True(next.State.PlanFromCache);
        // Die Messtabelle aus dem Cache steht für den Admin-Tab „Leistung“ sofort bereit.
        Assert.NotNull(next.State.LastBenchmark);
        Assert.NotEmpty(next.State.LastBenchmark!.Entries);
        Assert.Same(_gpu, next.GetDevice(ModelId.Yolox));
        Assert.Same(_gpu, next.GetDevice(ModelId.Landmark));
        await Task.Delay(100);
        Assert.Equal(0, nextBenchmark.Calls);
    }

    [Fact]
    public async Task Auto_ChangedFingerprint_MeasuresAgain()
    {
        var first = new FakeAcceleratorBenchmark(GpuTwiceAsFast());
        var a = Create(Settings(AcceleratorMode.Auto), new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)), first, appVersion: "1.0");
        await a.StartAsync();
        await WaitUntil(() => a.State.LastBenchmark is not null);

        var second = new FakeAcceleratorBenchmark(GpuTwiceAsFast());
        var b = Create(Settings(AcceleratorMode.Auto), new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)), second, appVersion: "2.0");
        await b.StartAsync();

        Assert.False(b.State.PlanFromCache);
        Assert.Null(b.GetDevice(ModelId.Yolox));
        await WaitUntil(() => second.Calls == 1);
    }

    [Fact]
    public async Task Auto_ChangedDeviceList_MeasuresAgain()
    {
        var a = Create(Settings(AcceleratorMode.Auto), new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)), new FakeAcceleratorBenchmark(GpuTwiceAsFast()));
        await a.StartAsync();
        await WaitUntil(() => a.State.LastBenchmark is not null);

        var second = new FakeAcceleratorBenchmark(GpuTwiceAsFast());
        var b = Create(Settings(AcceleratorMode.Auto), new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu, _weakGpu)), second);
        await b.StartAsync();

        Assert.False(b.State.PlanFromCache);
        await WaitUntil(() => second.Calls == 1);
    }

    [Fact]
    public async Task Auto_CorruptCacheFile_IsIgnored_AndMeasuredAgain()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, AccelerationCache.FileName), "{ kaputt");
        var benchmark = new FakeAcceleratorBenchmark(GpuTwiceAsFast());
        var service = Create(Settings(AcceleratorMode.Auto), new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)), benchmark);

        await service.StartAsync();

        Assert.Null(service.GetDevice(ModelId.Yolox));
        await WaitUntil(() => benchmark.Calls == 1);
        await WaitUntil(() => service.State.LastBenchmark is not null);
        // Die Messung ersetzt die kaputte Datei durch eine gültige.
        var again = Create(Settings(AcceleratorMode.Auto), new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)), new FakeAcceleratorBenchmark());
        await again.StartAsync();
        Assert.True(again.State.PlanFromCache);
    }

    [Fact]
    public async Task Auto_GpuBarelyFaster_StaysOnCpuEvenFromCache()
    {
        var slowGain = AccelDevices.Result((AcceleratorDevice.CpuDevice, 10, 10, 10, 10), (_gpu, 9, 9, 9, 9)); // nur 11 %
        var a = Create(Settings(AcceleratorMode.Auto), new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)), new FakeAcceleratorBenchmark(slowGain));
        await a.StartAsync();
        await WaitUntil(() => a.State.LastBenchmark is not null);
        Assert.False(a.State.RestartRecommended); // Plan bliebe CPU

        var b = Create(Settings(AcceleratorMode.Auto), new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)), new FakeAcceleratorBenchmark());
        await b.StartAsync();

        Assert.True(b.State.PlanFromCache);
        Assert.All(Enum.GetValues<ModelId>(), m => Assert.Null(b.GetDevice(m)));
    }

    [Fact]
    public async Task Auto_OnlyCpu_DoesNotMeasure()
    {
        var benchmark = new FakeAcceleratorBenchmark(GpuTwiceAsFast());
        var service = Create(Settings(AcceleratorMode.Auto), new FakeAcceleratorCatalog(AccelDevices.WithCpu()), benchmark);

        await service.StartAsync();
        await Task.Delay(100);

        Assert.Equal(0, benchmark.Calls);
        Assert.Null(service.GetDevice(ModelId.Yolox));
    }

    [Fact]
    public async Task Explicit_RunBenchmark_UpdatesLastBenchmark_RaisesEvents_AndReportsProgress()
    {
        var benchmark = new FakeAcceleratorBenchmark(GpuTwiceAsFast());
        var service = Create(Settings(AcceleratorMode.Cpu), new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)), benchmark);
        await service.StartAsync();
        var events = 0;
        service.StateChanged += (_, _) => Interlocked.Increment(ref events);
        BenchmarkProgress? last = null;

        var result = await service.RunBenchmarkAsync(new Progress<BenchmarkProgress>(p => last = p));

        Assert.False(result.Cancelled);
        Assert.Same(result, service.State.LastBenchmark);
        Assert.False(service.State.BenchmarkRunning);
        Assert.True(events >= 2); // läuft + fertig
        Assert.False(benchmark.LastLowPriority); // von der Oberfläche: normale Priorität
        await WaitUntil(() => last is not null);
        Assert.True(File.Exists(Path.Combine(_dir, AccelerationCache.FileName)));
        Assert.False(service.State.RestartRecommended); // nur im Modus Auto
    }

    [Fact]
    public async Task Explicit_RunBenchmark_Cancelled_IsNotCached_AndKeepsTheOldResult()
    {
        var benchmark = new FakeAcceleratorBenchmark(GpuTwiceAsFast()) { Duration = TimeSpan.FromSeconds(30) };
        var service = Create(Settings(AcceleratorMode.Cpu), new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)), benchmark);
        await service.StartAsync();
        using var cts = new CancellationTokenSource();

        var task = service.RunBenchmarkAsync(null, cts.Token);
        await WaitUntil(() => service.State.BenchmarkRunning);
        cts.Cancel();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Cancelled);
        Assert.Null(service.State.LastBenchmark);
        Assert.False(service.State.BenchmarkRunning);
        Assert.False(File.Exists(Path.Combine(_dir, AccelerationCache.FileName)));
    }

    [Fact]
    public async Task UserBenchmark_ReplacesARunningBackgroundMeasurement()
    {
        var benchmark = new FakeAcceleratorBenchmark(GpuTwiceAsFast()) { Duration = TimeSpan.FromSeconds(30) };
        var service = Create(Settings(AcceleratorMode.Auto), new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)), benchmark);
        await service.StartAsync();
        await WaitUntil(() => service.State.BenchmarkRunning && service.State.BenchmarkIsBackground);

        benchmark.Duration = TimeSpan.Zero;
        var result = await service.RunBenchmarkAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.Cancelled);
        Assert.Equal(2, benchmark.Calls);
        Assert.False(service.State.BenchmarkRunning);
    }

    // ------------------------------------------------------------------ Rückfälle und Plan-Zugriff

    [Fact]
    public async Task ReportFallback_MovesTheModelToCpuInTheState_RaisesEvent_AndKeepsTheReason()
    {
        var service = Create(Settings(AcceleratorMode.Gpu), new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)), new FakeAcceleratorBenchmark());
        await service.StartAsync();
        var raised = 0;
        service.StateChanged += (_, _) => Interlocked.Increment(ref raised);

        service.ReportFallback(ModelId.Palm, _gpu, "Treiber zurückgesetzt", atRuntime: true);

        var fallback = Assert.Single(service.State.Fallbacks);
        Assert.Equal((ModelId.Palm, "Testkarte", "Treiber zurückgesetzt", true), (fallback.Model, fallback.DeviceName, fallback.Reason, fallback.AtRuntime));
        var palm = service.State.Plan.Single(p => p.Model == ModelId.Palm);
        Assert.Equal((AcceleratorKind.Cpu, true), (palm.Kind, palm.FellBack));
        Assert.Equal(AcceleratorKind.Gpu, service.State.Plan.Single(p => p.Model == ModelId.Yolox).Kind); // andere Modelle unberührt
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task GetDevice_BeforeStart_ReturnsNull_WithoutWaiting()
    {
        var service = Create(Settings(AcceleratorMode.Gpu), new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)), new FakeAcceleratorBenchmark());

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(service.GetDevice(ModelId.Yolox));

        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task GetDevice_PlanNotReadyInTime_ReturnsNull_AndLaterShowsUpAsFallback()
    {
        var gate = new TaskCompletionSource();
        var catalog = new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)) { DiscoverGate = gate.Task };
        var service = Create(Settings(AcceleratorMode.Gpu), catalog, new FakeAcceleratorBenchmark(), planWait: TimeSpan.FromMilliseconds(50));
        var start = service.StartAsync();

        var device = await Task.Run(() => service.GetDevice(ModelId.Yolox)); // wie ein Detektor auf dem Threadpool
        Assert.Null(device);
        Assert.False(service.State.PlanReady);

        gate.SetResult();
        await start;

        var fallback = Assert.Single(service.State.Fallbacks);
        Assert.Equal(ModelId.Yolox, fallback.Model);
        Assert.Equal("Testkarte", fallback.DeviceName);
        Assert.True(service.State.Plan.Single(p => p.Model == ModelId.Yolox).FellBack);
        Assert.Same(_gpu, service.GetDevice(ModelId.Palm)); // später geladene Modelle bekommen das Gerät
    }

    [Fact]
    public async Task CatalogFailure_ResolvesThePlanToCpu_AndDoesNotThrow()
    {
        var catalog = new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)) { DiscoverFailure = new InvalidOperationException("Katalog kaputt") };
        var service = Create(Settings(AcceleratorMode.Gpu), catalog, new FakeAcceleratorBenchmark());

        await service.StartAsync();

        Assert.True(service.State.PlanReady);
        Assert.Null(service.GetDevice(ModelId.Yolox));
        Assert.NotNull(service.State.Notice);
        Assert.All(service.State.Plan, p => Assert.Equal(AcceleratorKind.Cpu, p.Kind));
    }

    [Fact]
    public async Task StartAsync_IsIdempotent()
    {
        var service = Create(Settings(AcceleratorMode.Cpu), new FakeAcceleratorCatalog(AccelDevices.WithCpu()), new FakeAcceleratorBenchmark());

        var a = service.StartAsync();
        var b = service.StartAsync();

        Assert.Same(a, b);
        await a;
    }

    // ------------------------------------------------------------------ Installation und Einstellungen

    [Fact]
    public async Task Install_Success_RefreshesDevices_AndRequiresRestart()
    {
        var catalog = new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)) { AfterInstall = AccelDevices.WithCpu(_gpu, AccelDevices.Npu("Neue KI-Einheit")) };
        var service = Create(Settings(AcceleratorMode.Cpu), catalog, new FakeAcceleratorBenchmark());
        await service.StartAsync();
        Assert.False(service.State.RestartRequired);
        double? progress = null;

        var result = await service.InstallVendorProvidersAsync(new Progress<double>(p => progress = p));

        Assert.True(result.Success);
        Assert.True(service.State.RestartRequired);
        Assert.Contains(service.State.Devices, d => d.Kind == AcceleratorKind.Npu);
        await WaitUntil(() => progress == 1);
    }

    [Fact]
    public async Task Install_Failure_ChangesNothing()
    {
        var catalog = new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)) { InstallResult = new ProviderInstallResult(false, "Kein Internet", []) };
        var service = Create(Settings(AcceleratorMode.Cpu), catalog, new FakeAcceleratorBenchmark());
        await service.StartAsync();

        var result = await service.InstallVendorProvidersAsync();

        Assert.False(result.Success);
        Assert.Equal("Kein Internet", result.Message);
        Assert.False(service.State.RestartRequired);
    }

    [Fact]
    public async Task SettingsChanged_DifferentModeOrGpu_RequiresRestart_AndBackAgainClearsIt()
    {
        var settings = Settings(AcceleratorMode.Gpu, _gpu.Key);
        var service = Create(settings, new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)), new FakeAcceleratorBenchmark());
        await service.StartAsync();

        settings.Current.Performance.Accelerator = AcceleratorMode.Cpu;
        settings.RaiseChanged();
        Assert.True(service.State.RestartRequired);

        settings.Current.Performance.Accelerator = AcceleratorMode.Gpu;
        settings.RaiseChanged();
        Assert.False(service.State.RestartRequired);

        settings.Current.Performance.GpuDeviceKey = _weakGpu.Key;
        settings.RaiseChanged();
        Assert.True(service.State.RestartRequired);
    }

    [Fact]
    public async Task SettingsChanged_GpuKeyIsIrrelevantOutsideGpuMode()
    {
        var settings = Settings(AcceleratorMode.Cpu);
        var service = Create(settings, new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)), new FakeAcceleratorBenchmark());
        await service.StartAsync();

        settings.Current.Performance.GpuDeviceKey = _gpu.Key;
        settings.RaiseChanged();

        Assert.False(service.State.RestartRequired);
    }

    [Fact]
    public async Task Dispose_CancelsAPendingBackgroundMeasurement_AndStopsRaisingEvents()
    {
        var benchmark = new FakeAcceleratorBenchmark(GpuTwiceAsFast());
        var catalog = new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu));
        var service = new AccelerationService(Settings(AcceleratorMode.Auto), catalog, benchmark, TimeProvider.System, new AccelerationOptions
        {
            CacheFilePath = Path.Combine(_dir, AccelerationCache.FileName),
            BackgroundBenchmarkDelay = TimeSpan.FromSeconds(30),
            AppVersion = "1.0",
            OrtVersion = () => "ort-test",
        });
        await service.StartAsync();
        var raised = 0;
        service.StateChanged += (_, _) => Interlocked.Increment(ref raised);

        await service.DisposeAsync();
        await Task.Delay(100);

        Assert.Equal(0, benchmark.Calls);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Construction_TouchesNothing()
    {
        // Der Container baut den Dienst ohne Katalog/Registry/Dateien auf (AppServicesTests lösen ihn auf).
        var service = Create(Settings(AcceleratorMode.Auto), new FakeAcceleratorCatalog(AccelDevices.WithCpu(_gpu)), new FakeAcceleratorBenchmark());

        Assert.False(service.State.PlanReady);
        Assert.False(Directory.Exists(_dir));
    }
}
