using Fotobox.Services.Detection.Acceleration;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Skriptbarer Messprozess für die Tests des Hauptprozesses (kein echter Kindprozess).</summary>
internal sealed class FakeMeasureProcess : IMeasureProcess
{
    private readonly Queue<string> _lines;
    private readonly bool _hangAfterLines;
    private readonly int _exitCode;

    public FakeMeasureProcess(IEnumerable<string> lines, int exitCode = 0, bool hangAfterLines = false, string errorOutput = "")
    {
        _lines = new Queue<string>(lines);
        _exitCode = exitCode;
        _hangAfterLines = hangAfterLines;
        ErrorOutput = errorOutput;
    }

    public bool Killed { get; private set; }
    public bool Disposed { get; private set; }
    public int ExitCode => _exitCode;
    public int Id => 0;
    public string ErrorOutput { get; }

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_lines.TryDequeue(out var line))
            return line;
        if (_hangAfterLines && !Killed)
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        return null;
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        if (_hangAfterLines && !Killed)
        {
            await Task.Delay(timeout).ConfigureAwait(false);
            return false;
        }
        return true;
    }

    public void Kill() => Killed = true;

    public void Dispose() => Disposed = true;
}

/// <summary>Startet nacheinander die Skripte; merkt sich die Aufträge, die der Hauptprozess geschrieben hat.</summary>
internal sealed class FakeMeasureLauncher(params Func<MeasureJob, FakeMeasureProcess>[] scripts) : IMeasureProcessLauncher
{
    public List<MeasureJob> Jobs { get; } = [];
    public List<FakeMeasureProcess> Processes { get; } = [];
    public Exception? StartFailure { get; init; }

    public IMeasureProcess Start(string jobFile)
    {
        if (StartFailure is not null)
            throw StartFailure;
        var job = MeasureProtocol.ReadFile<MeasureJob>(jobFile) ?? throw new InvalidOperationException("Auftrag nicht lesbar");
        Jobs.Add(job);
        Assert.True(Jobs.Count <= scripts.Length, "Mehr Messprozesse als erwartet gestartet.");
        var process = scripts[Jobs.Count - 1](job);
        Processes.Add(process);
        return process;
    }
}

internal static class MeasureLines
{
    public static string Start(string key) => MeasureProtocol.Serialize(new MeasureMessage { Type = "start", Key = key, Name = key });

    public static string Progress(int done, int total, string name) => MeasureProtocol.Serialize(new MeasureMessage { Type = "progress", Done = done, Total = total, Name = name });

    public static string Entry(string key, ModelId model, double? ms) => MeasureProtocol.Serialize(new MeasureMessage
    {
        Type = "entry",
        Entry = new BenchmarkEntry(key, key, model, 100, ms ?? 0, ms is null ? "Testfehler" : null),
    });

    public static string DeviceDone(string key) => MeasureProtocol.Serialize(new MeasureMessage { Type = "deviceDone", Key = key });

    public static string Done() => MeasureProtocol.Serialize(new MeasureMessage { Type = "done" });

    public static string Log(string level, string text) => MeasureProtocol.Serialize(new MeasureMessage { Type = "log", Level = level, Text = text });

    /// <summary>Ein vollständig gemessenes Gerät (alle Modelle, je <paramref name="ms"/>).</summary>
    public static IEnumerable<string> Device(string key, double ms)
    {
        yield return Start(key);
        foreach (var model in AccelerationPlanner.AllModels)
            yield return Entry(key, model, ms);
        yield return DeviceDone(key);
    }
}

/// <summary>Der Hauptprozess der Messung (Absturzschutz) ohne echten Kindprozess.</summary>
public sealed class OutOfProcessBenchmarkTests
{
    private const int FailFastExitCode = unchecked((int)0xC0000409);

    private readonly AcceleratorDevice _gpuA = AccelDevices.Gpu("Testkarte A", discrete: true, index: 0, device: 1);
    private readonly AcceleratorDevice _gpuB = AccelDevices.Gpu("Testkarte B", discrete: false, index: 1, device: 2);

    private static OutOfProcessBenchmark.ChildTimeouts FastTimeouts(TimeSpan? total = null) => new(
        PerDevice: TimeSpan.FromMilliseconds(100), Total: total ?? TimeSpan.FromSeconds(20), Startup: TimeSpan.FromMilliseconds(400),
        DeviceReserve: TimeSpan.FromMilliseconds(200), BetweenDevices: TimeSpan.FromMilliseconds(400), Exit: TimeSpan.FromMilliseconds(100));

    private static OutOfProcessBenchmark Create(FakeMeasureLauncher launcher, TimeSpan? total = null) => new(launcher, FastTimeouts(total));

    private IReadOnlyList<AcceleratorDevice> Devices() => AccelDevices.WithCpu(_gpuA, _gpuB);

    private static IEnumerable<string> FullRun(params (string Key, double Ms)[] devices)
    {
        foreach (var (key, ms) in devices)
        {
            foreach (var line in MeasureLines.Device(key, ms))
                yield return line;
        }
        yield return MeasureLines.Done();
    }

    [Fact]
    public async Task AllDevicesMeasured_InOneProcess_WithProgress_AndPriorityPassedOn()
    {
        var launcher = new FakeMeasureLauncher(job => new FakeMeasureProcess(
            FullRun((AcceleratorDevice.CpuKey, 10), (_gpuA.Key, 4), (_gpuB.Key, 6)).Prepend(MeasureLines.Progress(1, 12, "Testkarte A"))));
        var progress = new List<BenchmarkProgress>();

        var result = await Create(launcher).RunAsync(Devices(), lowPriority: true, new SyncProgress<BenchmarkProgress>(progress.Add), CancellationToken.None);

        Assert.False(result.Cancelled);
        Assert.Equal(12, result.Entries.Count);
        Assert.All(result.Entries, e => Assert.True(e.Succeeded));
        Assert.Equal(4, result.Find(_gpuA.Key, ModelId.Palm)!.AverageMs);
        Assert.Equal("Testkarte A", result.Find(_gpuA.Key, ModelId.Palm)!.DeviceName); // Namen kommen vom Hauptprozess
        var job = Assert.Single(launcher.Jobs);
        Assert.True(job.LowPriority);
        Assert.Equal([AcceleratorDevice.CpuKey, _gpuA.Key, _gpuB.Key], job.DeviceKeys);
        Assert.Equal(new BenchmarkProgress(1, 12, "Testkarte A", null), Assert.Single(progress));
        Assert.True(launcher.Processes[0].Disposed);
    }

    [Fact]
    public async Task ChildLogLines_AreForwardedToTheLog_WithoutBreakingTheRun()
    {
        var launcher = new FakeMeasureLauncher(job => new FakeMeasureProcess(
            FullRun((AcceleratorDevice.CpuKey, 10)).Prepend(MeasureLines.Log("WARN", "Beschleunigung: Testzeile")).Prepend("kein JSON")));

        var result = await Create(launcher).RunAsync(AccelDevices.WithCpu(), false, null, CancellationToken.None);

        Assert.All(result.Entries, e => Assert.True(e.Succeeded));
    }

    [Fact]
    public async Task DeviceCrash_IsFailedForAllModels_PartialValuesAreDropped_AndTheRestIsMeasuredInANewProcess()
    {
        var launcher = new FakeMeasureLauncher(
            // Prozess 1: CPU fertig, auf Karte A kommt ein Messwert, dann stürzt der Prozess ab.
            job => new FakeMeasureProcess(MeasureLines.Device(AcceleratorDevice.CpuKey, 10)
                .Append(MeasureLines.Start(_gpuA.Key)).Append(MeasureLines.Entry(_gpuA.Key, ModelId.Yolox, 1)), FailFastExitCode),
            // Prozess 2: nur noch Karte B.
            job => new FakeMeasureProcess(FullRun((_gpuB.Key, 6))));

        var result = await Create(launcher).RunAsync(Devices(), true, null, CancellationToken.None);

        Assert.Equal(2, launcher.Jobs.Count);
        Assert.Equal([_gpuB.Key], launcher.Jobs[1].DeviceKeys);
        Assert.False(result.Cancelled);
        Assert.All(AccelerationPlanner.AllModels, m =>
        {
            Assert.True(result.Find(AcceleratorDevice.CpuKey, m)!.Succeeded);
            Assert.True(result.Find(_gpuB.Key, m)!.Succeeded);
            var crashed = result.Find(_gpuA.Key, m)!;
            Assert.False(crashed.Succeeded); // auch Yolox, dessen Wert schon gemeldet war
            Assert.Equal("Messprozess abgestürzt (Exit-Code 0xC0000409)", crashed.Error);
        });
        Assert.True(launcher.Processes.All(p => p.Disposed));

        // Das abgestürzte Gerät wird im Plan nie gewählt, auch wenn sein (verworfener) Wert schneller war.
        var plan = AccelerationPlanner.FromBenchmark(result, Devices());
        Assert.All(AccelerationPlanner.AllModels, m => Assert.Same(_gpuB, plan[m]));
    }

    [Fact]
    public async Task CrashBeforeAnyDevice_FailsEverything_WithoutRestartLoop()
    {
        var launcher = new FakeMeasureLauncher(job => new FakeMeasureProcess([], FailFastExitCode, errorOutput: "Testausgabe"));

        var result = await Create(launcher).RunAsync(Devices(), false, null, CancellationToken.None);

        Assert.Single(launcher.Jobs);
        Assert.Equal(12, result.Entries.Count);
        Assert.All(result.Entries, e => Assert.Contains("0xC0000409", e.Error));
    }

    [Fact]
    public async Task EveryProcessCrashes_RestartsAreBoundedByTheNumberOfDevices()
    {
        FakeMeasureProcess Crash(MeasureJob job) => new([MeasureLines.Start(job.DeviceKeys[0])], 1);
        var launcher = new FakeMeasureLauncher(Crash, Crash, Crash);

        var result = await Create(launcher).RunAsync(Devices(), false, null, CancellationToken.None);

        Assert.Equal(3, launcher.Jobs.Count); // je Gerät ein Prozess, danach ist nichts mehr offen
        Assert.All(result.Entries, e => Assert.False(e.Succeeded));
        Assert.Equal(12, result.Entries.Count);
    }

    [Fact]
    public async Task HangingDevice_IsKilledByTheWatchdog_AndTheRestContinuesInANewProcess()
    {
        var launcher = new FakeMeasureLauncher(
            job => new FakeMeasureProcess(MeasureLines.Device(AcceleratorDevice.CpuKey, 10).Append(MeasureLines.Start(_gpuA.Key)), hangAfterLines: true),
            job => new FakeMeasureProcess(FullRun((_gpuB.Key, 6))));

        var result = await Create(launcher).RunAsync(Devices(), false, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(launcher.Processes[0].Killed);
        Assert.Equal([_gpuB.Key], launcher.Jobs[1].DeviceKeys);
        Assert.StartsWith("Zeitüberschreitung", result.Find(_gpuA.Key, ModelId.Yolox)!.Error);
        Assert.True(result.Find(_gpuB.Key, ModelId.Yolox)!.Succeeded);
        Assert.True(result.Find(AcceleratorDevice.CpuKey, ModelId.Yolox)!.Succeeded);
    }

    [Fact]
    public async Task SilentProcessBeforeTheFirstDevice_IsKilled_AndAllDevicesFail()
    {
        var launcher = new FakeMeasureLauncher(job => new FakeMeasureProcess([], hangAfterLines: true));

        var result = await Create(launcher).RunAsync(Devices(), false, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(launcher.Processes[0].Killed);
        Assert.Single(launcher.Jobs);
        Assert.All(result.Entries, e => Assert.Contains("antwortet nicht", e.Error));
    }

    [Fact]
    public async Task TotalDeadline_KillsTheProcess_AndMarksTheUnfinishedDevices()
    {
        var launcher = new FakeMeasureLauncher(job => new FakeMeasureProcess(
            MeasureLines.Device(AcceleratorDevice.CpuKey, 10).Append(MeasureLines.Start(_gpuA.Key)), hangAfterLines: true));
        var benchmark = new OutOfProcessBenchmark(launcher, FastTimeouts(total: TimeSpan.FromMilliseconds(150)) with { Startup = TimeSpan.FromSeconds(30), DeviceReserve = TimeSpan.FromSeconds(30) });

        var result = await benchmark.RunAsync(Devices(), false, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(launcher.Processes[0].Killed);
        Assert.Single(launcher.Jobs);
        Assert.True(result.Find(AcceleratorDevice.CpuKey, ModelId.Palm)!.Succeeded);
        Assert.Contains("Gesamtfrist", result.Find(_gpuA.Key, ModelId.Palm)!.Error);
        Assert.Contains("Gesamtfrist", result.Find(_gpuB.Key, ModelId.Palm)!.Error);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task Cancellation_KillsTheProcess_AndReturnsWhatWasMeasured()
    {
        var launcher = new FakeMeasureLauncher(job => new FakeMeasureProcess(
            MeasureLines.Device(AcceleratorDevice.CpuKey, 10).Append(MeasureLines.Start(_gpuA.Key)), hangAfterLines: true));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var benchmark = new OutOfProcessBenchmark(launcher, FastTimeouts() with { Startup = TimeSpan.FromSeconds(30), DeviceReserve = TimeSpan.FromSeconds(30) });

        var result = await benchmark.RunAsync(Devices(), false, null, cts.Token).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(result.Cancelled);
        Assert.True(launcher.Processes[0].Killed);
        Assert.Equal(4, result.Entries.Count); // nur die fertig gemessene CPU
        Assert.All(result.Entries, e => Assert.Equal(AcceleratorDevice.CpuKey, e.DeviceKey));
    }

    [Fact]
    public async Task ProcessCannotStart_FailsEverythingWithReason_AndNeverThrows()
    {
        var launcher = new FakeMeasureLauncher { StartFailure = new InvalidOperationException("kein Start") };

        var result = await Create(launcher).RunAsync(Devices(), false, null, CancellationToken.None);

        Assert.Equal(12, result.Entries.Count);
        Assert.All(result.Entries, e => Assert.Contains("kein Start", e.Error));
    }

    [Fact]
    public async Task NonZeroExitAfterDone_StillTakesTheValues()
    {
        var launcher = new FakeMeasureLauncher(job => new FakeMeasureProcess(FullRun((AcceleratorDevice.CpuKey, 10), (_gpuA.Key, 4)), FailFastExitCode));

        var result = await Create(launcher).RunAsync(AccelDevices.WithCpu(_gpuA), false, null, CancellationToken.None);

        Assert.All(result.Entries, e => Assert.True(e.Succeeded));
        Assert.Single(launcher.Jobs);
    }

    [Fact]
    public async Task DoneWithoutDeviceValues_MarksTheMissingDevicesAsNotMeasured()
    {
        var launcher = new FakeMeasureLauncher(job => new FakeMeasureProcess(FullRun((AcceleratorDevice.CpuKey, 10))));

        var result = await Create(launcher).RunAsync(AccelDevices.WithCpu(_gpuA), false, null, CancellationToken.None);

        Assert.True(result.Find(AcceleratorDevice.CpuKey, ModelId.Yolox)!.Succeeded);
        Assert.All(AccelerationPlanner.AllModels, m => Assert.False(result.Find(_gpuA.Key, m)!.Succeeded));
    }

    [Fact]
    public void ExitCodeFormat_IsHexadecimal()
    {
        Assert.Equal("0xC0000409", OutOfProcessBenchmark.FormatExitCode(FailFastExitCode));
        Assert.Equal("0x00000001", OutOfProcessBenchmark.FormatExitCode(1));
    }

    [Fact]
    public void Protocol_RoundTripsMessages_AndIgnoresGarbage()
    {
        var line = MeasureLines.Entry("k", ModelId.Palm, 3.5);
        var parsed = MeasureProtocol.TryParse(line);

        Assert.Equal("entry", parsed!.Type);
        Assert.Equal((ModelId.Palm, 3.5, "k"), (parsed.Entry!.Model, parsed.Entry.AverageMs, parsed.Entry.DeviceKey));
        Assert.Null(MeasureProtocol.TryParse("Irgendein Text"));
        Assert.Null(MeasureProtocol.TryParse("{ kaputt"));
        Assert.Null(MeasureProtocol.TryParse(""));
        Assert.Null(MeasureProtocol.TryParse(null));
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
