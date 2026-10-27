using System.IO;
using Fotobox.Core;
using Fotobox.Services.Camera;
using Xunit;

namespace Fotobox.Tests;

public sealed class VideoBitrateTests
{
    [Fact]
    public void ForVideo_1080p30_MatchesBitsPerPixel()
    {
        Assert.Equal(9_331_200, VideoBitrate.ForVideo(VideoQuality.High, 1920, 1080, 30));   // 0,15 · 1920 · 1080 · 30
        Assert.Equal(5_598_720, VideoBitrate.ForVideo(VideoQuality.Medium, 1920, 1080, 30)); // 0,09
        Assert.Equal(3_110_400, VideoBitrate.ForVideo(VideoQuality.Low, 1920, 1080, 30));    // 0,05
    }

    [Fact]
    public void ForVideo_OrdersByQuality()
    {
        var high = VideoBitrate.ForVideo(VideoQuality.High, 1280, 720, 30);
        var medium = VideoBitrate.ForVideo(VideoQuality.Medium, 1280, 720, 30);
        var low = VideoBitrate.ForVideo(VideoQuality.Low, 1280, 720, 30);
        Assert.True(high > medium && medium > low);
    }

    [Fact]
    public void ForVideo_ClampsToLimits()
    {
        Assert.Equal(VideoBitrate.MinVideoBitsPerSecond, VideoBitrate.ForVideo(VideoQuality.Low, 160, 120, 5));
        Assert.Equal(VideoBitrate.MaxVideoBitsPerSecond, VideoBitrate.ForVideo(VideoQuality.High, 3840, 2160, 60));
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(-5, 30)]
    [InlineData(double.NaN, 30)]
    [InlineData(1, 5)]
    [InlineData(24, 24)]
    [InlineData(240, 60)]
    public void ClampFps_BringsRateIntoRange(double input, double expected) =>
        Assert.Equal(expected, VideoBitrate.ClampFps(input));

    [Fact]
    public void ForVideo_UnknownFps_UsesDefault() =>
        Assert.Equal(VideoBitrate.ForVideo(VideoQuality.Medium, 1920, 1080, 30), VideoBitrate.ForVideo(VideoQuality.Medium, 1920, 1080, 0));

    [Fact]
    public void EstimateBytes_ScalesWithDuration_AndIncludesAudio()
    {
        var res = new CameraResolution(1920, 1080);
        var minute = VideoBitrate.EstimateBytes(VideoQuality.High, res, TimeSpan.FromMinutes(1));
        var fiveMinutes = VideoBitrate.EstimateBytes(VideoQuality.High, res, TimeSpan.FromMinutes(5));
        var noAudio = VideoBitrate.EstimateBytes(VideoQuality.High, res, TimeSpan.FromMinutes(1), withAudio: false);

        Assert.InRange(fiveMinutes, minute * 5 - 5, minute * 5 + 5);
        Assert.True(noAudio < minute);
        // 1080p30 hoch: (9,33 + 0,19) Mbit/s · 60 s / 8 · 1,10 ≈ 78,5 MB
        Assert.InRange(minute, 70_000_000, 90_000_000);
    }

    [Fact]
    public void EstimateBytes_WithoutResolution_Assumes1080p() =>
        Assert.Equal(
            VideoBitrate.EstimateBytes(VideoQuality.Medium, new CameraResolution(1920, 1080), TimeSpan.FromMinutes(2)),
            VideoBitrate.EstimateBytes(VideoQuality.Medium, null, TimeSpan.FromMinutes(2)));

    [Fact]
    public void EstimateBytes_NegativeDuration_IsZero() =>
        Assert.Equal(0, VideoBitrate.EstimateBytes(VideoQuality.High, null, TimeSpan.FromSeconds(-5)));

    [Fact]
    public void RequiredFreeBytes_AddsReserve()
    {
        var estimate = VideoBitrate.EstimateBytes(VideoQuality.Low, null, TimeSpan.FromMinutes(5));
        Assert.Equal(estimate + 200L * 1024 * 1024, VideoBitrate.RequiredFreeBytes(VideoQuality.Low, null, TimeSpan.FromMinutes(5)));
    }
}

/// <summary>
/// Echte Kamera: Aufnahme zusätzlich zum laufenden Livebild. Laufen nur lokal (<c>Category=Hardware</c>). Ohne Kamera
/// scheitern sie bewusst (kein stilles Grün).
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class VideoRecordingHardwareTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FotoboxVideoTests_" + Guid.NewGuid().ToString("N"));

    public VideoRecordingHardwareTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private static async Task<WinRtCameraService> StartCameraAsync(Action<CameraFrame>? onFrame = null)
    {
        var camera = new WinRtCameraService();
        var devices = await camera.GetDevicesAsync();
        Assert.True(devices.Count > 0, "Keine Kamera angeschlossen: Hardware-Test kann nicht laufen.");

        camera.ConfigureAudio(new CameraAudio(null)); // Standardmikrofon; fehlt es oder ist es gesperrt, startet die Kamera ohne Ton.
        if (onFrame is not null)
            camera.FrameArrived += (_, f) => onFrame(f);
        await camera.StartAsync(new CameraSettings { DeviceId = devices[0].Id });
        Assert.True(camera.Status == CameraStatus.Running, $"Status {camera.Status}: {camera.StatusMessage}");
        return camera;
    }

    private static async Task<(TimeSpan Duration, uint Width, uint Height)> ReadVideoPropertiesAsync(string path)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask();
        var props = await file.Properties.GetVideoPropertiesAsync().AsTask();
        return (props.Duration, props.Width, props.Height);
    }

    private static async Task<int> CountAudioTracksAsync(string path)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask();
        var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file).AsTask();
        return clip.EmbeddedAudioTracks.Count;
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task Record5Seconds_WritesFinalizedMp4_AndLiveFramesKeepFlowing()
    {
        var frames = 0;
        await using var camera = await StartCameraAsync(_ => Interlocked.Increment(ref frames));
        var mics = await camera.GetMicrophonesAsync();
        var path = Path.Combine(_dir, ".test.tmp.mp4");

        var recording = await camera.StartRecordingAsync(new VideoRecordingRequest(path, VideoQuality.High));
        var faulted = 0;
        recording.Faulted += (_, _) => Interlocked.Increment(ref faulted);
        Assert.Equal(camera.AudioAvailable, recording.HasAudio);

        // Eine zweite gleichzeitige Aufnahme ist nicht erlaubt.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            camera.StartRecordingAsync(new VideoRecordingRequest(Path.Combine(_dir, "zweite.mp4"), VideoQuality.Low)));

        var start = Volatile.Read(ref frames);
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        var middle = Volatile.Read(ref frames);
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        var end = Volatile.Read(ref frames);

        await recording.StopAsync();
        await recording.StopAsync(); // idempotent
        await recording.DisposeAsync();

        Assert.True(middle > start + 10, $"Livebild stockt während der Aufnahme (Bilder {start} → {middle}).");
        Assert.True(end > middle + 10, $"Livebild stockt während der Aufnahme (Bilder {middle} → {end}).");
        Assert.Equal(0, faulted);
        Assert.True(new FileInfo(path).Length > 0);

        var props = await ReadVideoPropertiesAsync(path);
        Assert.InRange(props.Duration.TotalSeconds, 4.0, 7.0);
        Assert.Equal((uint)(camera.ActiveResolution!.Width & ~1), props.Width);
        Assert.Equal((uint)(camera.ActiveResolution!.Height & ~1), props.Height);
        Assert.Equal(recording.HasAudio, await CountAudioTracksAsync(path) > 0);
        Console.WriteLine($"Aufnahme: {props.Duration.TotalSeconds:0.0} s, {props.Width}x{props.Height}, {new FileInfo(path).Length / 1024} KB, " +
                          $"Ton: {recording.HasAudio} (Mikrofone: {mics.Count}), Bilder im Fenster: {end - start}");

        // Nach dem Ende ist die nächste Aufnahme möglich.
        var second = Path.Combine(_dir, "zweite.mp4");
        var again = await camera.StartRecordingAsync(new VideoRecordingRequest(second, VideoQuality.Low));
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await again.StopAsync();
        Assert.True(new FileInfo(second).Length > 0);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task StopCameraDuringRecording_FinishesPartialVideo_AndReportsFaultedOnce()
    {
        await using var camera = await StartCameraAsync();
        var path = Path.Combine(_dir, ".teil.tmp.mp4");
        var recording = await camera.StartRecordingAsync(new VideoRecordingRequest(path, VideoQuality.Medium));
        var faulted = 0;
        recording.Faulted += (_, _) => Interlocked.Increment(ref faulted);

        await Task.Delay(TimeSpan.FromSeconds(3));
        await camera.StopAsync();

        Assert.Equal(1, faulted);
        // Der Abbau hat das Teilvideo bereits abgeschlossen; Stop liefert dasselbe Ergebnis.
        await recording.StopAsync();
        var props = await ReadVideoPropertiesAsync(path);
        Assert.InRange(props.Duration.TotalSeconds, 1.5, 5.0);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task UnknownMicrophone_FallsBackToVideoOnly_AndStillRecords()
    {
        var camera = new WinRtCameraService();
        await using var _ = camera;
        var devices = await camera.GetDevicesAsync();
        Assert.True(devices.Count > 0, "Keine Kamera angeschlossen: Hardware-Test kann nicht laufen.");

        camera.ConfigureAudio(new CameraAudio("gibt-es-nicht-SWD#MMDEVAPI#{00000000-0000-0000-0000-000000000000}"));
        await camera.StartAsync(new CameraSettings { DeviceId = devices[0].Id });
        Assert.True(camera.Status == CameraStatus.Running, $"Status {camera.Status}: {camera.StatusMessage}");
        Assert.False(camera.AudioAvailable);

        var path = Path.Combine(_dir, ".ohneton.tmp.mp4");
        var recording = await camera.StartRecordingAsync(new VideoRecordingRequest(path, VideoQuality.Low));
        Assert.False(recording.HasAudio);
        await Task.Delay(TimeSpan.FromSeconds(2));
        await recording.StopAsync();

        Assert.True(new FileInfo(path).Length > 0);
        Assert.Equal(0, await CountAudioTracksAsync(path));
    }
}
