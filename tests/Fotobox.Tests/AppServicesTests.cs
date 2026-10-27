using System.IO;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Services.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Composition Root (<c>App.RegisterServices</c>): Registrierungen und Auflösung der Gastansicht-Dienste.</summary>
public class AppServicesTests
{
    [Fact]
    public async Task VideoStorage_IsRegisteredAsSingleton_AndInjectedIntoBoothController()
    {
        var services = new ServiceCollection();
        App.RegisterServices(services);
        // Ersatz für alles, was Hardware oder den echten Einstellungsordner berührt.
        var settings = new TestSettingsStore();
        settings.Current.Video.CountdownSeconds = 30;
        services.AddSingleton<ISettingsStore>(settings);
        services.AddSingleton<ICameraService, RunningCamera>();
        // Kein echter Druckdienst: MarkReady holt vorab die Seitengröße vom Drucker.
        services.AddSingleton<IPrintService, NoPrinting>();

        await using var provider = services.BuildServiceProvider();

        var storage = provider.GetRequiredService<IVideoStorage>();
        Assert.IsType<VideoStorage>(storage);
        Assert.Same(storage, provider.GetRequiredService<IVideoStorage>());

        // Ohne Video-Speicher würde TryStartVideo immer false liefern: Das belegt, dass der Controller ihn per DI erhält.
        var controller = provider.GetRequiredService<BoothController>();
        controller.MarkReady();
        try
        {
            Assert.True(controller.TryStartVideo());
            Assert.Equal(BoothState.VideoCountdown, controller.State);
        }
        finally
        {
            controller.TryCancel();
            controller.Cancel();
        }
    }

    private sealed class TestSettingsStore : ISettingsStore
    {
        public string ConfigDirectory => Path.GetTempPath();
        public string OverlayDirectory => Path.GetTempPath();
        public AppSettings Current { get; } = new();
        public event EventHandler? SettingsChanged { add { } remove { } }
        public void Save(AppSettings settings) { }
    }

    private sealed class NoPrinting : IPrintService
    {
        public IReadOnlyList<PrinterInfo> GetPrinters() => [];
        public IReadOnlyList<PaperSizeInfo> GetPaperSizes(string? printerName) => [];
        public PageSpec GetPageSpec(PrinterSettings printer, LayoutKind layout) => PageSpec.Default;

        public Task<PrintResult> PrintAsync(BitmapSource page, PrinterSettings printer, IProgress<string>? status = null,
            CancellationToken ct = default, CancellationToken userCancel = default) =>
            Task.FromResult(new PrintResult(PrintOutcome.Completed));
    }

    private sealed class RunningCamera : ICameraService
    {
        public CameraStatus Status => CameraStatus.Running;
        public string? StatusMessage => null;
        public CameraResolution? ActiveResolution => null;
        public event EventHandler? StatusChanged { add { } remove { } }
        public event EventHandler<CameraFrame>? FrameArrived { add { } remove { } }
        public Task<IReadOnlyList<CameraDevice>> GetDevicesAsync() => Task.FromResult<IReadOnlyList<CameraDevice>>([]);
        public Task<IReadOnlyList<CameraResolution>> GetResolutionsAsync(string deviceId) => Task.FromResult<IReadOnlyList<CameraResolution>>([]);
        public Task StartAsync(CameraSettings settings, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task<BitmapSource> CapturePhotoAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
