// Stub – wird von Stream A implementiert
using System.Windows.Media.Imaging;
using Fotobox.Core;

namespace Fotobox.Services.Camera;

public sealed class WinRtCameraService : ICameraService
{
    public CameraStatus Status => CameraStatus.NoDevice;
    public string? StatusMessage => "Keine Kamera gefunden.";
    public CameraResolution? ActiveResolution => null;

    public event EventHandler? StatusChanged { add { } remove { } }
    public event EventHandler<CameraFrame>? FrameArrived { add { } remove { } }

    public Task<IReadOnlyList<CameraDevice>> GetDevicesAsync() =>
        Task.FromResult<IReadOnlyList<CameraDevice>>([]);

    public Task<IReadOnlyList<CameraResolution>> GetResolutionsAsync(string deviceId) =>
        Task.FromResult<IReadOnlyList<CameraResolution>>([]);

    public Task StartAsync(CameraSettings settings, CancellationToken ct = default) => Task.CompletedTask;

    public Task StopAsync() => Task.CompletedTask;

    public Task<BitmapSource> CapturePhotoAsync(CancellationToken ct = default) =>
        throw new InvalidOperationException("Die Kamera läuft nicht.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
