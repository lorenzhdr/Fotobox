using System.IO;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Fotobox.Services.Detection;
using Fotobox.Services.Detection.Acceleration;
using Fotobox.Services.Detection.Peace;
using Microsoft.ML.OnnxRuntime;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Session-Fabrik (CPU-Pfad mit echten Modellen, CI-tauglich) und der Rückfall der Detektoren: Ein Gerät ohne ONNX-Runtime-Handle
/// scheitert beim Erzeugen der Session, ohne Hardware wird so der Rückfallpfad auf die CPU geprüft.
/// </summary>
public sealed class AcceleratedSessionTests
{
    // Ein „Gerät“, auf dem keine Session entstehen kann (kein Handle).
    private static readonly AcceleratorDevice BrokenGpu = AccelDevices.Gpu("Defekte Testkarte", discrete: true, index: 0);

    private static DetectionFrame GrayFrame(int w, int h)
    {
        var pixels = new byte[w * h * 4];
        Array.Fill(pixels, (byte)128);
        return new DetectionFrame { Width = w, Height = h, Pixels = pixels, SourceWidth = w, SourceHeight = h };
    }

    private static BgraImage GrayImage(int w, int h)
    {
        var pixels = new byte[w * h * 4];
        Array.Fill(pixels, (byte)128);
        return new BgraImage { Width = w, Height = h, Stride = w * 4, Pixels = pixels };
    }

    [Fact]
    public void Factory_CpuOptions_AreTheOnesTheDetectorsAlwaysUsed()
    {
        using var options = OnnxSessionFactory.CreateOptions(null);
        using var cpuDeviceOptions = OnnxSessionFactory.CreateOptions(AcceleratorDevice.CpuDevice);

        Assert.Equal(2, options.IntraOpNumThreads);
        Assert.Equal(1, options.InterOpNumThreads);
        Assert.Equal(GraphOptimizationLevel.ORT_ENABLE_ALL, options.GraphOptimizationLevel);
        Assert.Equal(2, cpuDeviceOptions.IntraOpNumThreads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Factory_CpuSession_LoadsAndRunsTheRealModel(bool explicitCpuDevice)
    {
        using var session = OnnxSessionFactory.Create(YoloxPersonDetector.DefaultModelPath, explicitCpuDevice ? AcceleratorDevice.CpuDevice : null);

        Assert.Single(session.InputNames);
        var data = new float[3 * 416 * 416];
        using var input = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, data.AsMemory(), [1L, 3L, 416L, 416L]);
        using var runOptions = new RunOptions();
        using var results = session.Run(runOptions, [session.InputNames[0]], [input], [session.OutputNames[0]]);
        Assert.Equal([1L, 3549L, 85L], results[0].GetTensorTypeAndShape().Shape);
    }

    [Fact]
    public void Factory_DeviceWithoutHandle_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => OnnxSessionFactory.CreateOptions(BrokenGpu));
        Assert.Throws<InvalidOperationException>(() => OnnxSessionFactory.Create(YoloxPersonDetector.DefaultModelPath, BrokenGpu));
    }

    [Fact]
    public void Load_WithoutPlacementOrWithCpuPlan_LoadsOnTheCpu_AndReportsNothing()
    {
        var placement = new FakePlacement(AcceleratorDevice.CpuDevice);

        using var withoutPlacement = AcceleratedSession.Load(ModelId.Yolox, YoloxPersonDetector.DefaultModelPath, null,
            new DeviceBudget(TimeSpan.FromSeconds(5)), s => s.InputNames[0], out var name1);
        using var withCpuPlan = AcceleratedSession.Load(ModelId.Yolox, YoloxPersonDetector.DefaultModelPath, placement,
            new DeviceBudget(TimeSpan.FromSeconds(5)), s => s.InputNames[0], out var name2);

        Assert.Null(withoutPlacement.Device);
        Assert.Null(withCpuPlan.Device);
        Assert.Equal(name1, name2);
        Assert.Empty(placement.Fallbacks);
    }

    [Fact]
    public void Load_DeviceFails_FallsBackToCpuOnce_AndReportsTheReason()
    {
        var placement = new FakePlacement(BrokenGpu);

        using var session = AcceleratedSession.Load(ModelId.Palm, MediaPipeHandDetector.DefaultPalmModelPath, placement,
            new DeviceBudget(TimeSpan.FromSeconds(5)), s => s.InputNames[0], out var input);

        Assert.Null(session.Device);
        Assert.NotNull(input);
        var fallback = Assert.Single(placement.Fallbacks);
        Assert.Equal((ModelId.Palm, "Defekte Testkarte", false), (fallback.Model, fallback.Device, fallback.AtRuntime));
        Assert.False(string.IsNullOrWhiteSpace(fallback.Reason));
    }

    [Fact]
    public void Load_ExhaustedDeviceBudget_SkipsTheDevice_AndFallsBackToCpu()
    {
        var placement = new FakePlacement(BrokenGpu);

        using var session = AcceleratedSession.Load(ModelId.Palm, MediaPipeHandDetector.DefaultPalmModelPath, placement,
            new DeviceBudget(TimeSpan.Zero), s => s.InputNames[0], out _);

        Assert.Null(session.Device);
        var fallback = Assert.Single(placement.Fallbacks);
        Assert.Contains("aufgebraucht", fallback.Reason);
    }

    [Fact]
    public void Load_PrepareFailsOnCpuToo_Throws()
    {
        Assert.Throws<InvalidDataException>(() => AcceleratedSession.Load<int>(ModelId.Yolox, YoloxPersonDetector.DefaultModelPath, null,
            new DeviceBudget(TimeSpan.FromSeconds(5)), _ => throw new InvalidDataException("kaputt"), out _));
    }

    [Fact]
    public void Run_OnCpu_DoesNotRetry_AndPropagatesExceptions()
    {
        using var session = AcceleratedSession.Load(ModelId.Yolox, YoloxPersonDetector.DefaultModelPath, null,
            new DeviceBudget(TimeSpan.FromSeconds(5)), s => 0, out _);
        var calls = 0;

        // Falsche Eingabeform: ONNX Runtime wirft eine OnnxRuntimeException, die auf der CPU nicht zu einem Rückfall führt.
        Assert.Throws<OnnxRuntimeException>(() => session.Run<int>(s =>
        {
            calls++;
            var data = new float[3 * 10 * 10];
            using var input = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, data.AsMemory(), [1L, 3L, 10L, 10L]);
            using var runOptions = new RunOptions();
            s.Run(runOptions, [s.InputNames[0]], [input], [s.OutputNames[0]]).Dispose();
            return 0;
        }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task YoloxDetector_DeviceFailsToLoad_RunsOnCpuAndTellsThePlacement()
    {
        var placement = new FakePlacement(BrokenGpu);
        await using var detector = new YoloxPersonDetector(placement: placement);

        var result = await detector.DetectAsync(GrayFrame(640, 360), 0.3);

        Assert.Empty(result);
        var fallback = Assert.Single(placement.Fallbacks);
        Assert.Equal(ModelId.Yolox, fallback.Model);
    }

    [Fact]
    public async Task YuNetDetector_DeviceFailsToLoad_RunsOnCpuAndTellsThePlacement()
    {
        var placement = new FakePlacement(BrokenGpu);
        await using var detector = new YuNetFaceDetector(placement: placement);

        var result = await detector.DetectAsync(GrayImage(640, 360), 0.6);

        Assert.Empty(result);
        Assert.Equal(ModelId.YuNet, Assert.Single(placement.Fallbacks).Model);
    }

    [Fact]
    public async Task HandDetector_DeviceFailsToLoad_FallsBackPerModel()
    {
        var placement = new FakePlacement(BrokenGpu);
        await using var detector = new MediaPipeHandDetector(placement: placement);

        await detector.InitializeAsync();

        Assert.Equal([ModelId.Palm, ModelId.Landmark], placement.Fallbacks.Select(f => f.Model).Order().ToArray());
    }

    [Fact]
    public async Task Detectors_WithoutPlacement_BehaveAsBefore()
    {
        await using var detector = new YoloxPersonDetector();

        Assert.Empty(await detector.DetectAsync(GrayFrame(320, 240), 0.5));
    }

    [Fact]
    public void Timeouts_AreConsistent_ServiceOutlastsDetectorOutlastsItsParts()
    {
        Assert.True(AccelerationTimeouts.DetectorInit >= AccelerationTimeouts.PlanWait + AccelerationTimeouts.DeviceLoad + AccelerationTimeouts.CpuLoad);
        Assert.True(AccelerationTimeouts.ServiceInit > AccelerationTimeouts.DetectorInit);
        Assert.Equal(AccelerationTimeouts.ServiceInit, PersonDetectionService.InitTimeout);
        Assert.Equal(AccelerationTimeouts.ServiceInit, PeaceDetectionService.InitTimeout);
        Assert.Equal(AccelerationTimeouts.DetectorInit, YoloxPersonDetector.InitTimeout);
        Assert.Equal(AccelerationTimeouts.DetectorInit, YuNetFaceDetector.InitTimeout);
        Assert.Equal(AccelerationTimeouts.DetectorInit, MediaPipeHandDetector.InitTimeout);
        Assert.True(AccelerationTimeouts.BenchmarkTotal > AccelerationTimeouts.BenchmarkPerDevice);
    }
}
