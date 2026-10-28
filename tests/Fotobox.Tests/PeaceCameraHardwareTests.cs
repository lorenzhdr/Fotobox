using System.Diagnostics;
using System.Runtime.InteropServices;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Fotobox.Services.Camera;
using Fotobox.Services.Detection.Peace;
using Xunit;
using Xunit.Abstractions;

namespace Fotobox.Tests;

/// <summary>
/// Echte Kamera → <see cref="PeaceDetectionService"/> → echte Detektoren (wie in <c>App.RegisterServices</c>): 20 s Live-Analyse,
/// gemessen werden Analyserate, Livebild-Takt und die Zeit je Stufe (YuNet, Palm, Handpunkte). Eine Person vor der Kamera ist nicht
/// nötig; mit Hand und Gesicht im Bild sind die Zeiten aussagekräftiger.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class PeaceCameraHardwareTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Hardware")]
    public async Task RealCamera_Runs20Seconds_AndReportsStageTimings()
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
        await Task.Delay(1000);
        var baselineStart = Volatile.Read(ref frames);
        await Task.Delay(3000);
        var baselineFps = (Volatile.Read(ref frames) - baselineStart) / 3.0;

        var timing = new Timings();
        await using var service = new PeaceDetectionService(camera,
            () => new TimedFace(new YuNetFaceDetector(), timing),
            () => new TimedHand(new MediaPipeHandDetector(), timing),
            TimeProvider.System);
        var updates = 0;
        var faces = 0;
        var hands = 0;
        using var request = service.Request(new GestureRequestOptions(GestureNeeds.Faces | GestureNeeds.Hands, 5, 0.6, 0.5));
        request.Updated += (_, u) =>
        {
            Interlocked.Increment(ref updates);
            Interlocked.Add(ref faces, u.Faces.Count);
            Interlocked.Add(ref hands, u.Hands.Count);
        };

        var wait = Stopwatch.StartNew();
        while ((!request.Status.FacesReady || !request.Status.HandsReady)
            && request.Status.FaceUnavailableReason is null && request.Status.HandUnavailableReason is null
            && wait.Elapsed < TimeSpan.FromSeconds(25))
            await Task.Delay(100);

        var framesStart = Volatile.Read(ref frames);
        var updatesStart = Volatile.Read(ref updates);
        await Task.Delay(20000);
        var seconds = 20.0;
        var rate = (Volatile.Read(ref updates) - updatesStart) / seconds;
        var fps = (Volatile.Read(ref frames) - framesStart) / seconds;
        var status = request.Status;

        output.WriteLine($"Prozess: {RuntimeInformation.ProcessArchitecture}; Gesicht bereit {status.FacesReady} ('{status.FaceUnavailableReason}'), Hand bereit {status.HandsReady} ('{status.HandUnavailableReason}')");
        output.WriteLine($"Analysen/s {rate:0.0} (Status {status.AnalysesPerSecond:0.0}); Livebild {baselineFps:0.0} fps ohne, {fps:0.0} fps mit Erkennung");
        output.WriteLine($"Gefundene Gesichter insgesamt {faces}, Hände {hands}");
        output.WriteLine($"YuNet {timing.Mean("face"):0.0} ms ({timing.Count("face")}x), Palm {timing.Mean("palm"):0.0} ms ({timing.Count("palm")}x), Handpunkte {timing.Mean("landmark"):0.0} ms ({timing.Count("landmark")}x)");

        Assert.Null(status.FaceUnavailableReason);
        Assert.Null(status.HandUnavailableReason);
        Assert.InRange(rate, 2.0, 6.0);
        Assert.True(fps >= baselineFps * 0.7, $"Livebild eingebrochen: {baselineFps:0.0} → {fps:0.0} fps");
        await camera.StopAsync();
    }

    private sealed class Timings
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, (double Total, int Count)> _data = [];

        public void Add(string key, double ms)
        {
            lock (_lock)
            {
                _data.TryGetValue(key, out var v);
                _data[key] = (v.Total + ms, v.Count + 1);
            }
        }

        public double Mean(string key)
        {
            lock (_lock)
                return _data.TryGetValue(key, out var v) && v.Count > 0 ? v.Total / v.Count : 0;
        }

        public int Count(string key)
        {
            lock (_lock)
                return _data.TryGetValue(key, out var v) ? v.Count : 0;
        }
    }

    private sealed class TimedFace(IFaceLandmarkDetector inner, Timings timing) : IFaceLandmarkDetector
    {
        public Task InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);

        public async Task<IReadOnlyList<FaceLandmarks>> DetectAsync(BgraImage image, double minScore, CancellationToken ct = default)
        {
            var watch = Stopwatch.StartNew();
            var result = await inner.DetectAsync(image, minScore, ct);
            timing.Add("face", watch.Elapsed.TotalMilliseconds);
            return result;
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class TimedHand(IHandLandmarkDetector inner, Timings timing) : IHandLandmarkDetector
    {
        public Task InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);

        public async Task<IReadOnlyList<PalmCandidate>> DetectPalmsAsync(BgraImage image, NormRect region, int max, CancellationToken ct = default)
        {
            var watch = Stopwatch.StartNew();
            var result = await inner.DetectPalmsAsync(image, region, max, ct);
            timing.Add("palm", watch.Elapsed.TotalMilliseconds);
            return result;
        }

        public async Task<HandLandmarks?> DetectLandmarksAsync(BgraImage image, HandRoi roi, double minScore, CancellationToken ct = default)
        {
            var watch = Stopwatch.StartNew();
            var result = await inner.DetectLandmarksAsync(image, roi, minScore, ct);
            timing.Add("landmark", watch.Elapsed.TotalMilliseconds);
            return result;
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
