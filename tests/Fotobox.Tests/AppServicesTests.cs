using System.IO;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Services.Detection;
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

    [Fact]
    public async Task PersonDetectionService_IsRegisteredAsSingleton_WithoutTouchingTheCamera()
    {
        var services = new ServiceCollection();
        App.RegisterServices(services);
        var camera = new DetectionFakeCamera();
        services.AddSingleton<ICameraService>(camera);

        await using var provider = services.BuildServiceProvider();

        var detection = provider.GetRequiredService<IPersonDetectionService>();
        Assert.IsType<PersonDetectionService>(detection);
        Assert.Same(detection, provider.GetRequiredService<IPersonDetectionService>());
        Assert.Equal(0, camera.Subscribers); // ohne Anforderung kein Abo
    }

    [Fact]
    public async Task FunServices_AreRegistered_AndGuestViewAndAdminReceiveTheRealOnes()
    {
        var services = new ServiceCollection();
        App.RegisterServices(services);
        services.AddSingleton<ISettingsStore>(new TestSettingsStore());
        services.AddSingleton<ICameraService>(new DetectionFakeCamera());
        services.AddSingleton<IPrintService, NoPrinting>();

        await using var provider = services.BuildServiceProvider();

        var sound = provider.GetRequiredService<IFunSoundPlayer>();
        Assert.IsType<Fotobox.Services.Audio.FunSoundPlayer>(sound);
        Assert.Same(sound, provider.GetRequiredService<IFunSoundPlayer>());

        // Transient: Gastansicht und Admin-Vorschau bekommen je ein eigenes Exemplar.
        var a = provider.GetRequiredService<Fotobox.ViewModels.Booth.FunOverlayViewModel>();
        var b = provider.GetRequiredService<Fotobox.ViewModels.Booth.FunOverlayViewModel>();
        Assert.NotSame(a, b);
        Assert.False(a.IsInert);

        // Die optionalen Konstruktorparameter dürfen nicht still auf „inert“ bzw. null zurückfallen.
        var booth = provider.GetRequiredService<Fotobox.ViewModels.Booth.BoothViewModel>();
        Assert.False(booth.Fun.IsInert);
        var launcher = Assert.IsType<Fotobox.Views.Admin.AdminLauncher>(provider.GetRequiredService<IAdminLauncher>());
        Assert.True(launcher.HasFunServices);
        Assert.True(launcher.HasAccelerationService);

        // Countdown-Piepen und Kamera-Sound: ein gemeinsamer Spieler für Gastansicht und Admin („Anhören“).
        var boothSound = provider.GetRequiredService<IBoothSoundPlayer>();
        Assert.IsType<Fotobox.Services.Audio.BoothSoundPlayer>(boothSound);
        Assert.Same(boothSound, provider.GetRequiredService<IBoothSoundPlayer>());
        Assert.True(booth.HasBoothSound);
        Assert.True(launcher.HasBoothSound);
    }

    [Fact]
    public async Task PeaceServices_AreRegistered_AndGuestViewControllerAndAdminReceiveTheRealOnes()
    {
        var services = new ServiceCollection();
        App.RegisterServices(services);
        var camera = new DetectionFakeCamera();
        services.AddSingleton<ISettingsStore>(new TestSettingsStore());
        services.AddSingleton<ICameraService>(camera);
        services.AddSingleton<IPrintService, NoPrinting>();

        await using var provider = services.BuildServiceProvider();

        var gestures = provider.GetRequiredService<IGestureDetectionService>();
        Assert.IsType<Fotobox.Services.Detection.Peace.PeaceDetectionService>(gestures);
        Assert.Same(gestures, provider.GetRequiredService<IGestureDetectionService>());
        Assert.Equal(0, camera.Subscribers); // ohne Anforderung kein Abo, die Modelle laden erst bei Bedarf

        var decorator = provider.GetRequiredService<IPhotoDecorator>();
        Assert.IsType<Fotobox.Services.Peace.MoustachePhotoDecorator>(decorator);
        Assert.Same(decorator, provider.GetRequiredService<IPhotoDecorator>());

        // Die optionalen Konstruktorparameter dürfen nicht still auf null bzw. „inert“ zurückfallen.
        Assert.True(provider.GetRequiredService<BoothController>().HasPhotoDecorator);
        var booth = provider.GetRequiredService<Fotobox.ViewModels.Booth.BoothViewModel>();
        Assert.False(booth.Peace.IsInert);
        Assert.True(booth.Peace.HasGuestState);
        var launcher = Assert.IsType<Fotobox.Views.Admin.AdminLauncher>(provider.GetRequiredService<IAdminLauncher>());
        Assert.True(launcher.HasPeaceServices);
    }

    [Fact]
    public async Task MusicPlayer_IsRegisteredAsSingleton_AndInjectedIntoTheGuestView()
    {
        var services = new ServiceCollection();
        App.RegisterServices(services);
        services.AddSingleton<ISettingsStore>(new TestSettingsStore());
        services.AddSingleton<ICameraService>(new DetectionFakeCamera());
        services.AddSingleton<IPrintService, NoPrinting>();

        await using var provider = services.BuildServiceProvider();

        var music = provider.GetRequiredService<IMusicPlayer>();
        Assert.IsType<Fotobox.Services.Audio.MusicPlayer>(music);
        Assert.Same(music, provider.GetRequiredService<IMusicPlayer>());
        Assert.False(music.IsPlaying);
        // Der optionale Konstruktorparameter darf nicht still auf null zurückfallen.
        Assert.True(provider.GetRequiredService<Fotobox.ViewModels.Booth.BoothViewModel>().HasMusic);
    }

    [Fact]
    public async Task KisscamMusicLibrary_IsRegisteredAsSingleton_AndInjectedIntoGuestViewAndAdminLauncher()
    {
        var services = new ServiceCollection();
        App.RegisterServices(services);
        services.AddSingleton<ISettingsStore>(new TestSettingsStore());
        services.AddSingleton<ICameraService>(new DetectionFakeCamera());
        services.AddSingleton<IPrintService, NoPrinting>();

        await using var provider = services.BuildServiceProvider();

        var library = provider.GetRequiredService<Fotobox.Core.Music.IKisscamMusicLibrary>();
        Assert.IsType<Fotobox.Services.Audio.KisscamMusicLibrary>(library);
        Assert.Same(library, provider.GetRequiredService<Fotobox.Core.Music.IKisscamMusicLibrary>());
        // Ohne gesetzten Ordner liegt noch nichts im Dateisystem; der Start (App.xaml.cs) ruft SetFolder auf.
        Assert.Equal(Fotobox.Core.Music.KisscamLibraryStatus.Loading, library.Current.Status);
        // Die optionalen Konstruktorparameter dürfen nicht still auf null zurückfallen.
        Assert.True(provider.GetRequiredService<Fotobox.ViewModels.Booth.BoothViewModel>().HasMusicLibrary);
        Assert.True(Assert.IsType<Fotobox.Views.Admin.AdminLauncher>(provider.GetRequiredService<IAdminLauncher>()).HasMusicLibrary);
    }

    [Fact]
    public async Task PerformanceOverlay_IsWiredByDi_WithoutStartingAnythingNative()
    {
        var services = new ServiceCollection();
        App.RegisterServices(services);
        services.AddSingleton<ISettingsStore>(new TestSettingsStore());
        services.AddSingleton<ICameraService>(new DetectionFakeCamera());
        services.AddSingleton<IPrintService, NoPrinting>();

        await using var provider = services.BuildServiceProvider();

        var metrics = provider.GetRequiredService<Fotobox.Services.Diagnostics.ISystemMetricsSource>();
        Assert.IsType<Fotobox.Services.Diagnostics.WindowsSystemMetricsSource>(metrics);
        Assert.Same(metrics, provider.GetRequiredService<Fotobox.Services.Diagnostics.ISystemMetricsSource>());

        var booth = provider.GetRequiredService<Fotobox.ViewModels.Booth.BoothViewModel>();
        Assert.NotNull(booth.PerformanceOverlay);
        Assert.False(booth.PerformanceOverlay.IsVisible);   // nach dem Start aus
        Assert.NotNull(booth.DisplayFrames);
    }

    [Fact]
    public void WinRtCameraService_CountsFrames_AndIsTheFrameStatsOfTheApp()
    {
        var camera = new Fotobox.Services.Camera.WinRtCameraService();

        var stats = Assert.IsAssignableFrom<ICameraFrameStats>(camera);
        Assert.Equal(0, stats.FramesReceived);
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
            CancellationToken ct = default, CancellationToken userCancel = default, PrintBlockOptions? block = null) =>
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
