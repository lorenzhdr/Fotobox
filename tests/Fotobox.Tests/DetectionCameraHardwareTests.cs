using System.Diagnostics;
using System.Runtime.InteropServices;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Services.Camera;
using Fotobox.Services.Detection;
using Xunit;
using Xunit.Abstractions;

namespace Fotobox.Tests;

/// <summary>
/// Echte Kamera → <see cref="PersonDetectionService"/> → echter Detektor (wie in <c>App.RegisterServices</c>): Analysen kommen im
/// eingestellten Takt, ohne Ausweichen, und das Livebild bricht dabei nicht ein. Eine Person vor der Kamera ist nicht nötig.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class DetectionCameraHardwareTests(ITestOutputHelper output)
{
    [Theory]
    [Trait("Category", "Hardware")]
    [InlineData(DetectionMethod.Body, DetectionKind.Body)]
    [InlineData(DetectionMethod.Face, DetectionKind.Face)]
    public async Task RealCamera_DeliversAnalysesAtTheConfiguredRate_WithoutSlowingThePreview(DetectionMethod method, DetectionKind expected)
    {
        SynchronizationContext.SetSynchronizationContext(null); // Ereignisse direkt auf dem Threadpool
        await using var camera = new WinRtCameraService();
        var devices = await camera.GetDevicesAsync();
        if (devices.Count == 0)
            return; // Keine Kamera angeschlossen: nichts zu prüfen.

        var frames = 0;
        camera.FrameArrived += (_, _) => Interlocked.Increment(ref frames);
        await camera.StartAsync(new CameraSettings { DeviceId = devices[0].Id });
        Assert.True(camera.Status == CameraStatus.Running, $"Status {camera.Status}: {camera.StatusMessage}");

        // Grundtakt des Livebilds ohne Erkennung.
        await Task.Delay(1000);
        var baselineStart = Volatile.Read(ref frames);
        await Task.Delay(3000);
        var baselineFps = (Volatile.Read(ref frames) - baselineStart) / 3.0;

        await using var service = new PersonDetectionService(
            camera,
            kind => kind == DetectionKind.Body ? new YoloxPersonDetector() : new WindowsFaceDetector(),
            TimeProvider.System);
        var updates = 0;
        using var request = service.Request(new FunSettings { Method = method, AnalysesPerSecond = 5 });
        request.Updated += (_, _) => Interlocked.Increment(ref updates);

        // Laden abwarten (Modell bzw. FaceDetector).
        var wait = Stopwatch.StartNew();
        while (request.Status.Active is null && request.Status.UnavailableReason is null && wait.Elapsed < TimeSpan.FromSeconds(25))
            await Task.Delay(100);
        await Task.Delay(1000);

        var updatesStart = Volatile.Read(ref updates);
        var framesStart = Volatile.Read(ref frames);
        await Task.Delay(5000);
        var updateRate = (Volatile.Read(ref updates) - updatesStart) / 5.0;
        var detectionFps = (Volatile.Read(ref frames) - framesStart) / 5.0;
        var status = request.Status;

        output.WriteLine($"Prozess: {RuntimeInformation.ProcessArchitecture}, Methode {method}: aktiv {status.Active}, " +
            $"Ausweichgrund '{status.FallbackReason}', nicht verfügbar '{status.UnavailableReason}'");
        output.WriteLine($"Analysen/s gemessen {updateRate:0.0} (Status {status.AnalysesPerSecond:0.0}); " +
            $"Livebild {baselineFps:0.0} fps ohne, {detectionFps:0.0} fps mit Erkennung");

        Assert.Equal(expected, status.Active);
        Assert.Null(status.FallbackReason);
        Assert.Null(status.UnavailableReason);
        Assert.InRange(updateRate, 3.0, 6.0);
        Assert.True(detectionFps >= baselineFps * 0.7, $"Livebild eingebrochen: {baselineFps:0.0} → {detectionFps:0.0} fps");

        await camera.StopAsync();
    }
}
