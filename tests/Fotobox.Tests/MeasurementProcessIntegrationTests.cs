using System.IO;
using Fotobox.Services.Detection.Acceleration;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Echter Messprozess: Die gebaute <c>Fotobox.exe</c> (liegt im Testordner) wird mit <c>--beschleunigung-messen</c> gestartet. Gemessen wird die CPU
/// (überall vorhanden); den Absturz löst der Test-Schalter aus (<see cref="MeasureProtocol.CrashEnvironmentVariable"/>), ohne echte Grafikkarte.
/// </summary>
public sealed class MeasurementProcessIntegrationTests
{
    private static string ExePath => Path.Combine(AppContext.BaseDirectory, "Fotobox.exe");

    [Fact]
    public async Task Cpu_IsMeasuredInARealChildProcess()
    {
        Assert.True(File.Exists(ExePath), "Fotobox.exe fehlt im Testordner.");
        var benchmark = new OutOfProcessBenchmark(ExePath);
        var progress = new List<BenchmarkProgress>();

        var result = await benchmark.RunAsync(AccelDevices.WithCpu(), lowPriority: true, new Progress<BenchmarkProgress>(progress.Add), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(90));

        Assert.False(result.Cancelled);
        Assert.Equal(4, result.Entries.Count);
        Assert.All(result.Entries, e =>
        {
            Assert.True(e.Succeeded, e.Error);
            Assert.True(e.AverageMs > 0);
            Assert.Equal(AcceleratorDevice.CpuKey, e.DeviceKey);
        });
    }

    [Fact]
    public async Task CrashingChild_DoesNotTakeDownTheTestProcess_FailsTheDevice_AndTheRestIsMeasuredInANewProcess()
    {
        Assert.True(File.Exists(ExePath), "Fotobox.exe fehlt im Testordner.");
        var unknown = new AcceleratorDevice("gibt-es-nicht|0000|0000|testkarte", "Testkarte", AcceleratorKind.Gpu, AcceleratorDevice.DirectMlEpName);
        var launcher = new SystemMeasureProcessLauncher(ExePath, new Dictionary<string, string> { [MeasureProtocol.CrashEnvironmentVariable] = AcceleratorDevice.CpuKey });
        var benchmark = new OutOfProcessBenchmark(launcher, OutOfProcessBenchmark.ChildTimeouts.Default);

        // Erster Messprozess: stürzt bei der CPU ab (FailFast). Zweiter Messprozess: kennt das Testgerät nicht.
        var result = await benchmark.RunAsync(AccelDevices.WithCpu(unknown), lowPriority: false, null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(90));

        Assert.False(result.Cancelled);
        Assert.Equal(8, result.Entries.Count);
        Assert.All(AccelerationPlanner.AllModels, m =>
        {
            var cpu = result.Find(AcceleratorDevice.CpuKey, m)!;
            Assert.False(cpu.Succeeded);
            Assert.StartsWith("Messprozess abgestürzt (Exit-Code 0x", cpu.Error);
            var other = result.Find(unknown.Key, m)!;
            Assert.False(other.Succeeded);
            Assert.Contains("nicht gefunden", other.Error); // kam aus dem zweiten Messprozess
        });
    }
}
