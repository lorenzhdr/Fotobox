using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fotobox.Core;
using Fotobox.Views.Controls;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Prüft die Live-Vorschau (Spiegelung in allen Stretch-Modi, Statushinweis) ohne echte Kamera.</summary>
public sealed class CameraPreviewTests
{
    private const int Size = 400;

    private sealed class FakeCameraService : ICameraService
    {
        public CameraStatus Status { get; set; } = CameraStatus.Running;
        public string? StatusMessage => null;
        public CameraResolution? ActiveResolution => new(160, 90);
        public event EventHandler? StatusChanged;
        public event EventHandler<CameraFrame>? FrameArrived;

        public void RaiseFrame(CameraFrame frame) => FrameArrived?.Invoke(this, frame);
        public void RaiseStatus() => StatusChanged?.Invoke(this, EventArgs.Empty);

        public Task<IReadOnlyList<CameraDevice>> GetDevicesAsync() => Task.FromResult<IReadOnlyList<CameraDevice>>([]);
        public Task<IReadOnlyList<CameraResolution>> GetResolutionsAsync(string deviceId) =>
            Task.FromResult<IReadOnlyList<CameraResolution>>([]);
        public Task StartAsync(CameraSettings settings, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task<BitmapSource> CapturePhotoAsync(CancellationToken ct = default) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Horizontaler Farbverlauf: Jede Spalte hat eine eindeutige Farbe, Spiegelung ist damit pixelgenau prüfbar.</summary>
    private static CameraFrame GradientFrame(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * stride + x * 4;
                var r = (byte)(x * 255 / (width - 1));
                pixels[i] = (byte)(255 - r); // B
                pixels[i + 1] = (byte)(y * 255 / (height - 1)); // G
                pixels[i + 2] = r; // R
                pixels[i + 3] = 255;
            }
        }
        return new CameraFrame { Width = width, Height = height, Stride = stride, Pixels = pixels };
    }

    private static async Task FlushDispatcherAsync() =>
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

    private static byte[] RenderPixels(FrameworkElement element)
    {
        element.UpdateLayout();
        var target = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        target.Render(element);
        var pixels = new byte[Size * Size * 4];
        target.CopyPixels(pixels, Size * 4, 0);
        return pixels;
    }

    private static Window ShowHost(CameraPreview preview)
    {
        preview.Width = Size;
        preview.Height = Size;
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            Content = preview,
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    [WpfTheory]
    [InlineData(Stretch.None)]
    [InlineData(Stretch.Fill)]
    [InlineData(Stretch.Uniform)]
    [InlineData(Stretch.UniformToFill)]
    public async Task Mirror_FlipsAroundTheCenterOfTheControl_ForEveryStretchMode(Stretch stretch)
    {
        var camera = new FakeCameraService();
        var preview = new CameraPreview { Camera = camera, Stretch = stretch };
        var window = ShowHost(preview);
        try
        {
            camera.RaiseFrame(GradientFrame(160, 90));
            await FlushDispatcherAsync();
            var plain = RenderPixels(preview);

            preview.Mirror = true;
            var mirrored = RenderPixels(preview);

            // Das Bild muss auch gefüllt sein (kein schwarzer Streifen durch eine verschobene Spiegelung): Mitte nicht schwarz.
            var center = (Size / 2 * Size + Size / 2) * 4;
            Assert.True(plain[center] + plain[center + 1] + plain[center + 2] > 0, "Das Bild wurde nicht gerendert.");

            // Spaltenweise: gespiegeltes Pixel (x, y) == ungespiegeltes Pixel (Size - 1 - x, y).
            var mismatches = 0;
            for (var y = 0; y < Size; y += 7)
            {
                for (var x = 0; x < Size; x++)
                {
                    var a = (y * Size + x) * 4;
                    var b = (y * Size + (Size - 1 - x)) * 4;
                    if (Math.Abs(mirrored[a] - plain[b]) > 6 ||
                        Math.Abs(mirrored[a + 1] - plain[b + 1]) > 6 ||
                        Math.Abs(mirrored[a + 2] - plain[b + 2]) > 6)
                        mismatches++;
                }
            }

            Assert.True(mismatches == 0, $"{stretch}: {mismatches} Pixel weichen von der erwarteten Spiegelung ab.");
        }
        finally
        {
            window.Close();
        }
    }

    [WpfFact]
    public async Task Mirror_ImageHostHasExactlyTheSizeOfTheControl_WithUniformToFill()
    {
        var camera = new FakeCameraService();
        var preview = new CameraPreview { Camera = camera, Stretch = Stretch.UniformToFill, Mirror = true };
        var window = ShowHost(preview);
        try
        {
            camera.RaiseFrame(GradientFrame(160, 90));
            await FlushDispatcherAsync();
            preview.UpdateLayout();

            var host = (FrameworkElement)preview.FindName("MirrorHost");
            Assert.NotNull(host);
            var bounds = host.TransformToAncestor(preview).TransformBounds(new Rect(0, 0, host.ActualWidth, host.ActualHeight));
            Assert.Equal(Size, bounds.Width, 1);
            Assert.Equal(Size, bounds.Height, 1);
            Assert.Equal(0, bounds.X, 1);
            Assert.Equal(0, bounds.Y, 1);
        }
        finally
        {
            window.Close();
        }
    }

    [WpfTheory]
    [InlineData(true, Visibility.Visible)]
    [InlineData(false, Visibility.Collapsed)]
    public async Task StatusOverlay_FollowsShowStatusOverlay(bool show, Visibility expected)
    {
        var camera = new FakeCameraService { Status = CameraStatus.Starting };
        var preview = new CameraPreview { Camera = camera, ShowStatusOverlay = show };
        var window = ShowHost(preview);
        try
        {
            camera.RaiseStatus();
            await FlushDispatcherAsync();

            var overlay = (FrameworkElement)preview.FindName("StatusOverlay");
            Assert.Equal(expected, overlay.Visibility);
        }
        finally
        {
            window.Close();
        }
    }
}
