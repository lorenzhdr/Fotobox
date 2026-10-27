using System.Windows;
using Xunit;
using Fotobox.Core;
using Fotobox.Services.Camera;
using Fotobox.Views.Controls;

namespace Fotobox.Tests;

public sealed class CameraFormatSelectorTests
{
    private static CameraFormatCandidate F(int w, int h, double fps) => new(w, h, fps);

    [Fact]
    public void EmptyList_ReturnsMinusOne() =>
        Assert.Equal(-1, CameraFormatSelector.SelectIndex([], null, null));

    [Fact]
    public void ConfiguredResolution_IsUsedWhenSupported()
    {
        var formats = new[] { F(1920, 1080, 30), F(1280, 720, 30), F(640, 480, 30) };
        Assert.Equal(1, CameraFormatSelector.SelectIndex(formats, 1280, 720));
    }

    [Fact]
    public void ConfiguredResolution_PrefersUsableFrameRate()
    {
        var formats = new[] { F(1280, 720, 5), F(1280, 720, 30), F(1280, 720, 60) };
        Assert.Equal(1, CameraFormatSelector.SelectIndex(formats, 1280, 720));
    }

    [Fact]
    public void ConfiguredResolution_AboveLimit_IsHonoured()
    {
        var formats = new[] { F(1920, 1080, 30), F(3840, 2160, 30) };
        Assert.Equal(1, CameraFormatSelector.SelectIndex(formats, 3840, 2160));
    }

    [Fact]
    public void UnsupportedConfiguredResolution_FallsBackToAuto()
    {
        var formats = new[] { F(640, 480, 30), F(1920, 1080, 30), F(1280, 720, 30) };
        Assert.Equal(1, CameraFormatSelector.SelectIndex(formats, 800, 600));
    }

    [Fact]
    public void Auto_PicksHighestUpTo1080p_IgnoringBigger()
    {
        var formats = new[] { F(3840, 2160, 30), F(1920, 1080, 30), F(1280, 720, 30) };
        Assert.Equal(1, CameraFormatSelector.SelectIndex(formats, null, null));
    }

    [Fact]
    public void Auto_SkipsFormatsBelow15Fps()
    {
        var formats = new[] { F(1920, 1080, 5), F(1280, 720, 30) };
        Assert.Equal(1, CameraFormatSelector.SelectIndex(formats, null, null));
    }

    [Fact]
    public void Auto_PrefersSixteenByNine()
    {
        var formats = new[] { F(1440, 1080, 30), F(1280, 720, 30) };
        Assert.Equal(1, CameraFormatSelector.SelectIndex(formats, null, null));
    }

    [Fact]
    public void Auto_FallsBackToNonSixteenByNine_WhenNothingElse()
    {
        var formats = new[] { F(640, 480, 30), F(800, 600, 30) };
        Assert.Equal(1, CameraFormatSelector.SelectIndex(formats, null, null));
    }

    [Fact]
    public void Auto_PrefersLowerFrameRateAbove30()
    {
        var formats = new[] { F(1920, 1080, 60), F(1920, 1080, 30) };
        Assert.Equal(1, CameraFormatSelector.SelectIndex(formats, null, null));
    }

    [Fact]
    public void Auto_AllBelow15Fps_StillPicksBestWithinLimit()
    {
        var formats = new[] { F(1920, 1080, 5), F(1280, 720, 10), F(3840, 2160, 5) };
        Assert.Equal(0, CameraFormatSelector.SelectIndex(formats, null, null));
    }

    [Fact]
    public void Auto_AllAboveLimit_PicksSmallest()
    {
        var formats = new[] { F(3840, 2160, 30), F(2560, 1440, 30) };
        Assert.Equal(1, CameraFormatSelector.SelectIndex(formats, null, null));
    }

    private static CameraFormatCandidate S(int w, int h, double fps, string? subtype) => new(w, h, fps, subtype);

    [Fact]
    public void Subtype_Uncompressed_BeatsMjpgBeatsCompressed_AtSameResolutionAndRate()
    {
        var formats = new[] { S(1280, 720, 30, "H264"), S(1280, 720, 30, "MJPG"), S(1280, 720, 30, "NV12"), S(1280, 720, 30, "HEVC") };
        Assert.Equal(2, CameraFormatSelector.SelectIndex(formats, null, null));
        Assert.Equal(2, CameraFormatSelector.SelectIndex(formats, 1280, 720));
    }

    [Theory]
    [InlineData("YUY2", "MJPG")]
    [InlineData("MJPG", null)]
    [InlineData(null, "H264")]
    [InlineData("MJPG", "H264")]
    [InlineData("NV12", "HEVC")]
    [InlineData("RGB32", "H264_ES")]
    public void Subtype_Ranking_FirstIsPreferred(string? better, string? worse)
    {
        // Der bessere Subtyp gewinnt unabhängig von der Position in der Liste.
        Assert.Equal(0, CameraFormatSelector.SelectIndex([S(1280, 720, 30, better), S(1280, 720, 30, worse)], null, null));
        Assert.Equal(1, CameraFormatSelector.SelectIndex([S(1280, 720, 30, worse), S(1280, 720, 30, better)], null, null));
    }

    [Fact]
    public void Subtype_UnknownOrMissing_KeepsFirstIndexAsBefore()
    {
        var formats = new[] { S(1280, 720, 30, null), S(1280, 720, 30, ""), S(1280, 720, 30, "XYZ1") };
        Assert.Equal(0, CameraFormatSelector.SelectIndex(formats, null, null));
    }

    [Fact]
    public void Subtype_IsOnlyTieBreaker_ResolutionAndFrameRateStillWin()
    {
        // Höhere Auflösung gewinnt auch dann, wenn sie nur als MJPG angeboten wird.
        var byResolution = new[] { S(1280, 720, 30, "NV12"), S(1920, 1080, 30, "MJPG") };
        Assert.Equal(1, CameraFormatSelector.SelectIndex(byResolution, null, null));

        // Bessere Bildrate gewinnt gegen besseres Pixelformat.
        var byRate = new[] { S(1280, 720, 10, "NV12"), S(1280, 720, 30, "MJPG") };
        Assert.Equal(1, CameraFormatSelector.SelectIndex(byRate, 1280, 720));

        // Ein komprimiertes Format ist nur Rückfall: Die gewünschte Auflösung gibt es nur als H264 (kein Bild), also das nutzbare Format.
        var onlyCompressed = new[] { S(1280, 720, 30, "NV12"), S(1920, 1080, 30, "H264") };
        Assert.Equal(0, CameraFormatSelector.SelectIndex(onlyCompressed, 1920, 1080));
    }

    [Fact]
    public void Auto_PrefersUsableFormat_OverHigherResolutionCompressed()
    {
        // 1080p gibt es nur als H264 (der Frame-Reader liefert dafür kein Bild), also 720p MJPG.
        var formats = new[] { S(1920, 1080, 30, "H264"), S(1280, 720, 30, "MJPG") };
        Assert.Equal(1, CameraFormatSelector.SelectIndex(formats, null, null));
    }

    [Fact]
    public void ConfiguredResolution_PrefersUsableFormat_OverCompressedWithHigherFrameRate()
    {
        var formats = new[] { S(1280, 720, 30, "H264"), S(1280, 720, 15, "MJPG") };
        Assert.Equal(1, CameraFormatSelector.SelectIndex(formats, 1280, 720));
        Assert.Equal(1, CameraFormatSelector.SelectIndex(formats, null, null));
    }

    [Fact]
    public void OnlyCompressedFormats_StillSelectsOne()
    {
        var formats = new[] { S(1280, 720, 30, "H264"), S(1920, 1080, 30, "H264"), S(640, 480, 30, "HEVC") };
        Assert.Equal(1, CameraFormatSelector.SelectIndex(formats, null, null));
        Assert.Equal(0, CameraFormatSelector.SelectIndex(formats, 1280, 720));
    }

    [Fact]
    public void Subtype_IsCaseInsensitive()
    {
        var formats = new[] { S(640, 480, 30, "h264"), S(640, 480, 30, "nv12") };
        Assert.Equal(1, CameraFormatSelector.SelectIndex(formats, null, null));
    }

    [Fact]
    public void DistinctResolutions_AreUniqueAndDescending()
    {
        var formats = new[] { F(640, 480, 30), F(1920, 1080, 30), F(1920, 1080, 60), F(1280, 720, 30), F(0, 0, 30) };
        var result = CameraFormatSelector.DistinctResolutions(formats);
        Assert.Equal(
            new[] { new CameraResolution(1920, 1080), new CameraResolution(1280, 720), new CameraResolution(640, 480) },
            result);
    }
}

[Collection(WpfCollection.Name)]
public sealed class CameraHardwareTests
{
    [Fact]
    [Trait("Category", "Hardware")]
    public async Task StartCaptureStop_WithRealCamera()
    {
        await using var camera = new WinRtCameraService();
        var devices = await camera.GetDevicesAsync();
        if (devices.Count == 0)
            return; // Keine Kamera angeschlossen: nichts zu prüfen.

        var resolutions = await camera.GetResolutionsAsync(devices[0].Id);
        Assert.NotEmpty(resolutions);

        var frames = 0;
        var firstFrame = new TaskCompletionSource<CameraFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        camera.FrameArrived += (_, f) =>
        {
            Interlocked.Increment(ref frames);
            firstFrame.TrySetResult(f);
        };

        await camera.StartAsync(new CameraSettings { DeviceId = devices[0].Id });
        Assert.True(camera.Status == CameraStatus.Running, $"Status {camera.Status}: {camera.StatusMessage}");
        Assert.NotNull(camera.ActiveResolution);

        var frame = await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(frame.Width > 0 && frame.Height > 0);
        Assert.Equal(frame.Width * 4, frame.Stride);

        var photo = await camera.CapturePhotoAsync();
        Assert.True(photo.PixelWidth > 0);
        Assert.True(photo.PixelHeight > 0);
        Assert.True(photo.IsFrozen);
        Assert.Equal(camera.ActiveResolution!.Width, photo.PixelWidth);
        Assert.True(frames > 0);

        await camera.StopAsync();
        Assert.Equal(CameraStatus.Stopped, camera.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.CapturePhotoAsync());
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task Restart_ReportsStartingBeforeTeardown_AndFramesKeepFlowing()
    {
        await using var camera = new WinRtCameraService();
        var devices = await camera.GetDevicesAsync();
        if (devices.Count == 0)
            return; // Keine Kamera angeschlossen: nichts zu prüfen.

        var settings = new CameraSettings { DeviceId = devices[0].Id };
        var frames = 0;
        camera.FrameArrived += (_, _) => Interlocked.Increment(ref frames);

        await camera.StartAsync(settings);
        Assert.True(camera.Status == CameraStatus.Running, $"Status {camera.Status}: {camera.StatusMessage}");

        var statuses = new List<CameraStatus>();
        camera.StatusChanged += (_, _) => { lock (statuses) statuses.Add(camera.Status); };
        await camera.StartAsync(settings);

        // Der erste gemeldete Status des Neustarts ist Starting (nicht mehr Running während des Abbaus).
        lock (statuses)
        {
            Assert.NotEmpty(statuses);
            Assert.Equal(CameraStatus.Starting, statuses[0]);
        }
        Assert.Equal(CameraStatus.Running, camera.Status);

        var before = Volatile.Read(ref frames);
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.True(Volatile.Read(ref frames) - before >= 5, "Nach dem Neustart kommen kaum Frames an.");
    }

    private const int StressIterations = 20;
    private static readonly TimeSpan StressStartTimeout = TimeSpan.FromSeconds(60);
    // Nach einem Gerätewechsel braucht z. B. eine Frontkamera mitunter einige Sekunden; der Wiederholmechanismus (5 s) fängt das auf.
    private static readonly TimeSpan StressFrameTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Bildet die Admin-Ansicht nach: Die Tests laufen auf einem Dispatcher-Thread (wie die ViewModels), die Vorschau hängt
    /// am Dienst, und der Neustart passiert mit neuen Einstellungen während der laufenden Session.
    /// </summary>
    private sealed class RestartHarness : IAsyncDisposable
    {
        private readonly Window _window;
        private readonly Random _random = new(42);
        private int _frames;

        public RestartHarness(WinRtCameraService camera)
        {
            Camera = camera;
            _window = new Window { Width = 640, Height = 360, Content = new CameraPreview { Camera = camera, Mirror = true } };
            _window.Show();
            camera.FrameArrived += (_, _) => Interlocked.Increment(ref _frames);
        }

        public WinRtCameraService Camera { get; }

        public async Task RestartAsync(CameraSettings settings, string label)
        {
            var before = Volatile.Read(ref _frames);
            await Camera.StartAsync(settings).WaitAsync(StressStartTimeout);

            var deadline = DateTime.UtcNow + StressFrameTimeout;
            while (Volatile.Read(ref _frames) <= before)
            {
                Assert.True(DateTime.UtcNow < deadline, $"{label}: Nach dem Neustart kam kein Bild an.");
                await Task.Delay(20);
            }

            // Unterschiedlich lange Laufzeit, bevor der nächste Neustart kommt (der Nutzer „speichert“ zu beliebiger Zeit).
            await Task.Delay(_random.Next(100, 1500));
        }

        public async Task StopAsync()
        {
            await Camera.StopAsync().WaitAsync(StressStartTimeout);
            Assert.Equal(CameraStatus.Stopped, Camera.Status);
        }

        public ValueTask DisposeAsync()
        {
            _window.Close();
            return ValueTask.CompletedTask;
        }
    }

    [WpfFact]
    [Trait("Category", "Hardware")]
    public async Task RestartWhileRunning_Repeatedly_PerDevice_DoesNotCrashOrHang()
    {
        await using var camera = new WinRtCameraService();
        var devices = await camera.GetDevicesAsync();
        if (devices.Count == 0)
            return; // Keine Kamera angeschlossen: nichts zu prüfen.

        await using var harness = new RestartHarness(camera);
        foreach (var device in devices)
        {
            for (var i = 1; i <= StressIterations; i++)
            {
                // Neue Einstellungs-Instanz je Durchlauf, wie beim Speichern in der Admin-Ansicht.
                var settings = new CameraSettings { DeviceId = device.Id, MirrorPreview = i % 2 == 0 };
                await harness.RestartAsync(settings, $"Gerät {device.Name} #{i}");
            }

            await harness.StopAsync();
        }
    }

    [WpfFact]
    [Trait("Category", "Hardware")]
    public async Task RestartWhileRunning_AlternatingDevices_DoesNotCrashOrHang()
    {
        await using var camera = new WinRtCameraService();
        var devices = await camera.GetDevicesAsync();
        if (devices.Count < 2)
            return; // Für den Gerätewechsel sind mindestens zwei Kameras nötig.

        await using var harness = new RestartHarness(camera);
        for (var i = 1; i <= StressIterations; i++)
        {
            var device = devices[i % devices.Count];
            await harness.RestartAsync(new CameraSettings { DeviceId = device.Id }, $"Wechsel auf {device.Name} #{i}");
        }

        await harness.StopAsync();
    }

    [WpfFact]
    [Trait("Category", "Hardware")]
    public async Task StartStop_Repeatedly_DoesNotCrashOrHang()
    {
        await using var camera = new WinRtCameraService();
        var devices = await camera.GetDevicesAsync();
        if (devices.Count == 0)
            return; // Keine Kamera angeschlossen: nichts zu prüfen.

        await using var harness = new RestartHarness(camera);
        foreach (var device in devices)
        {
            for (var i = 1; i <= StressIterations; i++)
            {
                await harness.RestartAsync(new CameraSettings { DeviceId = device.Id }, $"Gerät {device.Name} #{i}");
                await harness.StopAsync();
            }
        }
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task StartThenStop_WithoutAwaiting_EndsStopped_AndOvertakenStartIsDiscarded()
    {
        // Welcher Pool-Thread zuerst das Gate erreicht, ist nicht festgelegt: Start gefolgt von Stopp muss immer in
        // „Stopped“ enden (auch ohne Kamera), und ein überholter Start darf keine Session hinterlassen.
        await using var camera = new WinRtCameraService();
        var devices = await camera.GetDevicesAsync();
        var settings = new CameraSettings { DeviceId = devices.Count > 0 ? devices[0].Id : null };

        for (var i = 0; i < 10; i++)
        {
            var start = camera.StartAsync(settings);
            var stop = camera.StopAsync();
            await Task.WhenAll(start, stop).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(CameraStatus.Stopped, camera.Status);
        }
    }

    [Fact]
    public async Task CapturePhoto_WhenNotStarted_Throws()
    {
        await using var camera = new WinRtCameraService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.CapturePhotoAsync());
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var camera = new WinRtCameraService();
        await camera.DisposeAsync();
        await camera.DisposeAsync();
        Assert.Equal(CameraStatus.Stopped, camera.Status);
    }
}
