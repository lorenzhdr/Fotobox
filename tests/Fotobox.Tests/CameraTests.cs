using Xunit;
using Fotobox.Core;
using Fotobox.Services.Camera;

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
