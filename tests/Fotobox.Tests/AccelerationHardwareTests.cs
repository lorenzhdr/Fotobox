using System.IO;
using Fotobox.Core;
using Fotobox.Services.Detection;
using Fotobox.Services.Detection.Acceleration;
using Xunit;
using Xunit.Abstractions;

namespace Fotobox.Tests;

/// <summary>
/// Mit der echten Hardware dieses Rechners (Windows ML, DirectML, ggf. NPU): echter Katalog, echte Messung, echter Dienst mit
/// Cache in einem Temp-Ordner. Nicht in der CI (Category Hardware). Gibt Geräte, Plan und Messtabelle im Testprotokoll aus.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class AccelerationHardwareTests(ITestOutputHelper output)
{
    private static string Describe(AccelerationState state) =>
        $"Modus {state.Mode}, Plan: " + string.Join(", ", state.Plan.Select(p => $"{p.Model}={p.DeviceName}{(p.FellBack ? " (Rückfall)" : "")}")) +
        $"; aus Cache: {state.PlanFromCache}; Hinweis: {state.Notice}";

    [Fact]
    public async Task RealService_Auto_DiscoversDevices_MeasuresAndUsesTheCacheOnTheNextStart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fotobox-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AccelSettingsStore { ConfigDirectory = dir };
            settings.Current.Performance.Accelerator = AcceleratorMode.Auto;

            await using var first = new AccelerationService(settings, new AcceleratorCatalog(), new AcceleratorBenchmark(), TimeProvider.System,
                new AccelerationOptions { BackgroundBenchmarkDelay = TimeSpan.Zero });
            await first.StartAsync();
            foreach (var d in first.State.Devices)
                output.WriteLine($"Gerät: {d.Name} | {d.Kind} | {d.EpName} | diskret={d.IsDiscrete} | idx={d.HighPerformanceIndex} | vram={d.VideoMemory} | Version={d.Version} | Schlüssel={d.Key}");
            foreach (var p in first.GetProviderStates())
                output.WriteLine($"Anbieter: {p.Name} | {p.ReadyState} | {p.PackageId}");
            output.WriteLine("Start 1: " + Describe(first.State));
            Assert.Contains(first.State.Devices, d => d.IsCpu);

            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (first.State.LastBenchmark is null && DateTime.UtcNow < deadline)
                await Task.Delay(200);
            var result = first.State.LastBenchmark;
            Assert.NotNull(result);
            foreach (var e in result.Entries)
                output.WriteLine($"Messung: {e.DeviceName} | {e.Model} | laden {e.LoadMs:F0} ms | Ø {e.AverageMs:F2} ms | {e.Error}");
            output.WriteLine("Neustart empfohlen: " + first.State.RestartRecommended);

            await using var second = new AccelerationService(settings, new AcceleratorCatalog(), new AcceleratorBenchmark(), TimeProvider.System,
                new AccelerationOptions { BackgroundBenchmarkDelay = TimeSpan.Zero });
            await second.StartAsync();
            output.WriteLine("Start 2: " + Describe(second.State));
            Assert.True(second.State.PlanFromCache || second.State.Devices.All(d => d.IsCpu));

            // Die Detektoren laufen mit dem Plan: echte Modelle laden und einmal analysieren.
            await using var yolox = new YoloxPersonDetector(placement: second);
            var pixels = new byte[640 * 360 * 4];
            Array.Fill(pixels, (byte)128);
            var frame = new Fotobox.Core.Fun.DetectionFrame { Width = 640, Height = 360, Pixels = pixels, SourceWidth = 640, SourceHeight = 360 };
            Assert.Empty(await yolox.DetectAsync(frame, 0.3));
            output.WriteLine("Rückfälle nach dem Laden: " + string.Join("; ", second.State.Fallbacks.Select(f => $"{f.Model}: {f.Reason}")));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task RealService_Gpu_RunsAllDetectorsOnTheStrongestGpu_IfPresent()
    {
        var settings = new AccelSettingsStore { ConfigDirectory = Path.GetTempPath() };
        settings.Current.Performance.Accelerator = AcceleratorMode.Gpu;
        await using var service = new AccelerationService(settings, new AcceleratorCatalog(), new AcceleratorBenchmark(), TimeProvider.System,
            new AccelerationOptions { CacheFilePath = Path.Combine(Path.GetTempPath(), "fotobox-test-" + Guid.NewGuid().ToString("N") + ".json") });
        await service.StartAsync();
        output.WriteLine(Describe(service.State));

        await using var yolox = new YoloxPersonDetector(placement: service);
        await using var yunet = new Fotobox.Services.Detection.Peace.YuNetFaceDetector(placement: service);
        await using var hands = new Fotobox.Services.Detection.Peace.MediaPipeHandDetector(placement: service);
        await yolox.InitializeAsync();
        await yunet.InitializeAsync();
        await hands.InitializeAsync();

        output.WriteLine("Rückfälle: " + string.Join("; ", service.State.Fallbacks.Select(f => $"{f.Model}: {f.Reason}")));
        output.WriteLine("Plan danach: " + Describe(service.State));
    }
}
