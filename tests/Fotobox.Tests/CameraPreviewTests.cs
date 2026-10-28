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
[Collection(WpfCollection.Name)]
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

    // Ausrichtung des Bildes: Bei UniformToFill und abweichendem Seitenverhältnis muss die Bildmitte auf der Mitte des Hosts liegen
    // (OverlayMapper rechnet mit zentriertem Ausschnitt); sonst sitzen Overlays (Bart, Zielmarken) versetzt.
    [WpfTheory]
    [InlineData(160, 90, false)]
    [InlineData(160, 90, true)]
    [InlineData(640, 480, false)]
    [InlineData(640, 480, true)]
    [InlineData(90, 160, false)]
    public async Task UniformToFill_CentersTheImageOnTheHost_WhenAspectRatiosDiffer(int frameW, int frameH, bool mirror)
    {
        var camera = new FakeCameraService();
        var preview = new CameraPreview { Camera = camera, Stretch = Stretch.UniformToFill, Mirror = mirror };
        var window = ShowHost(preview);
        try
        {
            camera.RaiseFrame(GradientFrame(frameW, frameH));
            await FlushDispatcherAsync();
            preview.UpdateLayout();

            var host = (FrameworkElement)preview.FindName("MirrorHost");
            var image = (FrameworkElement)preview.FindName("PreviewImage");
            var bounds = image.TransformToAncestor(host).TransformBounds(new Rect(0, 0, image.ActualWidth, image.ActualHeight));

            var scale = Math.Max((double)Size / frameW, (double)Size / frameH);
            Assert.Equal(frameW * scale, bounds.Width, 1);
            Assert.Equal(frameH * scale, bounds.Height, 1);
            Assert.Equal(host.ActualWidth / 2, bounds.X + bounds.Width / 2, 1);
            Assert.Equal(host.ActualHeight / 2, bounds.Y + bounds.Height / 2, 1);

            // Gerendert: Die mittlere Spalte zeigt die Mitte des Farbverlaufs (R ≈ 127), auch gespiegelt.
            var pixels = RenderPixels(preview);
            var center = (Size / 2 * Size + Size / 2) * 4;
            Assert.InRange(pixels[center + 2], 120, 135);
        }
        finally
        {
            window.Close();
        }
    }

    private static CameraFrame SolidFrame(int width, int height, byte value)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, value);
        return new CameraFrame { Width = width, Height = height, Stride = stride, Pixels = pixels };
    }

    private static byte[] BitmapPixels(CameraPreview preview)
    {
        var bitmap = (WriteableBitmap)((Image)preview.FindName("PreviewImage")).Source;
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    [WpfFact]
    public async Task Preview_ShowsOnlyTheNewestFrame_WhenSeveralArriveBeforeRender()
    {
        var camera = new FakeCameraService();
        var preview = new CameraPreview { Camera = camera };
        var window = ShowHost(preview);
        try
        {
            // Der UI-Thread kommt nicht zum Rendern: nur das letzte Bild zählt, ältere werden überholt.
            for (byte v = 10; v <= 60; v += 10)
                camera.RaiseFrame(SolidFrame(32, 18, v));
            await FlushDispatcherAsync();

            var pixels = BitmapPixels(preview);
            Assert.All(pixels.Where((_, i) => i % 4 != 3), b => Assert.Equal(60, b));
        }
        finally
        {
            window.Close();
        }
    }

    [WpfFact]
    public async Task Preview_WithConcurrentProducer_NeverShowsTornFrames_AndEndsOnLastFrame()
    {
        var camera = new FakeCameraService();
        var preview = new CameraPreview { Camera = camera };
        var window = ShowHost(preview);
        try
        {
            camera.RaiseFrame(SolidFrame(64, 36, 1));
            await FlushDispatcherAsync();

            // Kamera-Thread liefert schnell Vollflächen-Bilder (Wert = laufende Nummer), der UI-Thread rendert dazwischen.
            const int count = 400;
            var producer = Task.Run(() =>
            {
                for (var i = 0; i < count; i++)
                    camera.RaiseFrame(SolidFrame(64, 36, (byte)(20 + i % 200)));
            });

            var observed = 0;
            while (!producer.IsCompleted)
            {
                await FlushDispatcherAsync();
                var pixels = BitmapPixels(preview);
                var first = pixels[0];
                // Ein gemischtes Bild (Puffer wird während der Kopie beschrieben) hätte verschiedene Werte.
                Assert.All(pixels.Where((_, i) => i % 4 != 3), b => Assert.Equal(first, b));
                observed++;
            }
            await producer;
            await FlushDispatcherAsync();
            await FlushDispatcherAsync();

            var last = BitmapPixels(preview);
            Assert.All(last.Where((_, i) => i % 4 != 3), b => Assert.Equal((byte)(20 + (count - 1) % 200), b));
            Assert.True(observed > 0);
        }
        finally
        {
            window.Close();
        }
    }

    [WpfFact]
    public async Task Preview_HandlesResolutionChange_WithRecycledBuffers()
    {
        var camera = new FakeCameraService();
        var preview = new CameraPreview { Camera = camera };
        var window = ShowHost(preview);
        try
        {
            camera.RaiseFrame(SolidFrame(32, 18, 40));
            await FlushDispatcherAsync();
            camera.RaiseFrame(SolidFrame(32, 18, 50));
            await FlushDispatcherAsync();

            camera.RaiseFrame(SolidFrame(48, 27, 90));
            await FlushDispatcherAsync();

            var bitmap = (WriteableBitmap)((Image)preview.FindName("PreviewImage")).Source;
            Assert.Equal(48, bitmap.PixelWidth);
            Assert.Equal(27, bitmap.PixelHeight);
            Assert.All(BitmapPixels(preview).Where((_, i) => i % 4 != 3), b => Assert.Equal(90, b));
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
