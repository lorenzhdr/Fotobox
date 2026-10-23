using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Services.Camera;
using Fotobox.ViewModels.Booth;
using Fotobox.Views.Booth;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

public class BoothViewModelTests
{
    [Fact]
    public void CanOpenAdmin_InIdleStartingAndError_ButNotMidSession()
    {
        var h = new Harness(markReady: false);
        Assert.Equal(BoothState.Starting, h.Controller.State);
        Assert.True(h.ViewModel.CanOpenAdmin);

        h.Controller.MarkReady();
        Assert.True(h.ViewModel.CanOpenAdmin);

        h.Settings.Current.Flow.CountdownSeconds = 3;
        Assert.True(h.ViewModel.TryTrigger());
        Assert.Equal(BoothState.Countdown, h.Controller.State);
        Assert.False(h.ViewModel.CanOpenAdmin);
    }

    [Fact]
    public void CanOpenAdmin_InErrorState()
    {
        var h = new Harness();
        h.Settings.Current.Flow.CountdownSeconds = 0;
        h.Camera.CaptureFailure = new InvalidOperationException("Kamera getrennt");

        Assert.True(h.ViewModel.TryTrigger());

        Assert.Equal(BoothState.Error, h.Controller.State);
        Assert.True(h.ViewModel.CanOpenAdmin);
    }

    [Fact]
    public void AdminOpen_BlocksOpeningAgainAndTrigger_AndNotifies()
    {
        var h = new Harness();
        var changed = new List<string?>();
        h.ViewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        h.ViewModel.IsAdminOpen = true;

        Assert.Contains(nameof(BoothViewModel.CanOpenAdmin), changed);
        Assert.False(h.ViewModel.CanOpenAdmin);
        Assert.False(h.ViewModel.TryTrigger());
        Assert.Equal(BoothState.Idle, h.Controller.State);

        h.ViewModel.IsAdminOpen = false;
        Assert.True(h.ViewModel.CanOpenAdmin);
        Assert.True(h.ViewModel.TryTrigger());
    }

    [Fact]
    public async Task TryTrigger_IsBlockedWhileCameraStarts()
    {
        var h = new Harness();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Camera.StartTask = start.Task;

        var restart = h.ViewModel.RestartCameraAsync();

        Assert.True(h.ViewModel.IsCameraBusy);
        Assert.False(h.ViewModel.TryTrigger());
        Assert.Equal(BoothState.Idle, h.Controller.State);

        start.SetResult();
        await restart;

        Assert.False(h.ViewModel.IsCameraBusy);
        Assert.True(h.ViewModel.TryTrigger());
    }

    [Fact]
    public void CameraProblem_IsShownInIdle_NotMidSession()
    {
        var h = new Harness();
        h.Camera.Status = CameraStatus.NoDevice;
        Assert.True(h.ViewModel.HasCameraProblem);
        Assert.False(h.ViewModel.ShowHint);

        h.Camera.Status = CameraStatus.Running;
        Assert.False(h.ViewModel.HasCameraProblem);
        Assert.True(h.ViewModel.ShowHint);
    }

    [Theory]
    [InlineData(CameraStatus.NoDevice, null, "Keine Kamera gefunden", "Bitte die Kamera anschließen.")]
    [InlineData(CameraStatus.NoDevice, "Keine Kamera gefunden.", "Keine Kamera gefunden", "Bitte die Kamera anschließen.")]
    [InlineData(CameraStatus.NoDevice, CameraMessages.NoDevice, "Keine Kamera gefunden", "Bitte die Kamera anschließen.")]
    [InlineData(CameraStatus.AccessDenied, null, "Kein Zugriff auf die Kamera", CameraMessages.AccessDeniedHint)]
    [InlineData(CameraStatus.AccessDenied, CameraMessages.AccessDenied, "Kein Zugriff auf die Kamera", CameraMessages.AccessDeniedHint)]
    [InlineData(CameraStatus.Error, null, "Die Kamera ist nicht verfügbar", CameraMessages.UnavailableHint)]
    [InlineData(CameraStatus.Error, CameraMessages.ConnectionLost, "Die Kamera ist nicht verfügbar", CameraMessages.ConnectionLost)]
    [InlineData(CameraStatus.Error, "Die Kamera wird bereits von einer anderen Anwendung verwendet.",
        "Die Kamera ist nicht verfügbar", "Die Kamera wird bereits von einer anderen Anwendung verwendet.")]
    public void CameraProblemTexts_AreActionableAndNotJustTheTitle(
        CameraStatus status, string? message, string expectedTitle, string expectedText)
    {
        var h = new Harness();
        h.Camera.Status = status;
        h.Camera.StatusMessage = message;

        Assert.Equal(expectedTitle, h.ViewModel.CameraProblemTitle);
        Assert.Equal(expectedText, h.ViewModel.CameraProblemText);
        Assert.NotEqual(expectedTitle.TrimEnd('.'), h.ViewModel.CameraProblemText.TrimEnd('.'));
        // Der Wiederhol-Hinweis steht nur im XAML, nicht zusätzlich im Text.
        Assert.DoesNotContain("erneut", h.ViewModel.CameraProblemText);
    }

    [Fact]
    public void AccessDeniedHint_NamesCurrentWindowsSettingsPath()
    {
        Assert.Contains("Datenschutz und Sicherheit", CameraMessages.AccessDeniedHint);
        Assert.Contains("Kamera", CameraMessages.AccessDeniedHint);
        Assert.Contains("Desktop-Apps", CameraMessages.AccessDeniedHint);
    }

    // ---- Fenster ----

    [StaFact]
    public void BoothWindow_Close_IsCancelledUnlessAllowed()
    {
        var h = new Harness();
        var window = new BoothWindow(h.ViewModel, h.Camera, new FakeAdminLauncher(), h.Settings);
        var closed = false;
        var closingRaised = false;
        var cancelled = false;
        window.Closed += (_, _) => closed = true;
        // Zweiter Handler läuft nach dem des Fensters und sieht dessen Cancel-Entscheidung.
        window.Closing += (_, e) => { closingRaised = true; cancelled = e.Cancel; };

        window.Close(); // wie Alt+F4 / Systemmenü

        Assert.True(closingRaised);
        Assert.True(cancelled);
        Assert.False(closed);
    }

    // ---- Hilfsmittel ----

    private sealed class Harness
    {
        public FakeTimeProvider Time { get; } = new();
        public FakeCamera Camera { get; } = new();
        public FakeSettingsStore Settings { get; } = new();
        public BoothController Controller { get; }
        public BoothViewModel ViewModel { get; }

        public Harness(bool markReady = true)
        {
            // Wie in den Controller-Tests: Fortsetzungen laufen synchron im aufrufenden Thread.
            SynchronizationContext.SetSynchronizationContext(null);
            Settings.Current.Flow.ErrorDisplaySeconds = 6;
            Controller = new BoothController(Camera, new FakeComposer(), new FakePrinter(), new FakeStorage(), Settings, Time);
            ViewModel = new BoothViewModel(Controller, Camera, Settings);
            if (markReady)
                Controller.MarkReady();
        }
    }

    private sealed class FakeCamera : ICameraService
    {
        public Exception? CaptureFailure { get; set; }
        public Task StartTask { get; set; } = Task.CompletedTask;

        public CameraStatus Status { get; set; } = CameraStatus.Running;
        public string? StatusMessage { get; set; }
        public CameraResolution? ActiveResolution => null;
        public event EventHandler? StatusChanged { add { } remove { } }
        public event EventHandler<CameraFrame>? FrameArrived { add { } remove { } }

        public Task<IReadOnlyList<CameraDevice>> GetDevicesAsync() =>
            Task.FromResult<IReadOnlyList<CameraDevice>>([]);

        public Task<IReadOnlyList<CameraResolution>> GetResolutionsAsync(string deviceId) =>
            Task.FromResult<IReadOnlyList<CameraResolution>>([]);

        public Task StartAsync(CameraSettings settings, CancellationToken ct = default) => StartTask;
        public Task StopAsync() => Task.CompletedTask;

        public Task<BitmapSource> CapturePhotoAsync(CancellationToken ct = default) =>
            Task.FromException<BitmapSource>(CaptureFailure ?? new InvalidOperationException("Kein Foto im Test."));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeComposer : ILayoutComposer
    {
        public int GetRequiredPhotoCount(LayoutSettings layout) => 1;

        public BitmapSource Compose(IReadOnlyList<BitmapSource> photos, LayoutSettings layout, PageSpec page,
            string overlayDirectory, DateTime timestamp) => throw new NotSupportedException();
    }

    private sealed class FakePrinter : IPrintService
    {
        public IReadOnlyList<PrinterInfo> GetPrinters() => [];
        public IReadOnlyList<PaperSizeInfo> GetPaperSizes(string? printerName) => [];
        public PageSpec GetPageSpec(PrinterSettings printer, LayoutKind layout) => PageSpec.Default;

        public Task<PrintResult> PrintAsync(BitmapSource page, PrinterSettings printer,
            IProgress<string>? status = null, CancellationToken ct = default) =>
            Task.FromResult(new PrintResult(PrintOutcome.Completed));
    }

    private sealed class FakeStorage : IPhotoStorage
    {
        public string ResolveTargetFolder(StorageSettings storage, DateTime timestamp) => "fake";

        public Task<SavedPhotoSet> SaveAsync(BitmapSource layout, IReadOnlyList<BitmapSource> originals,
            StorageSettings storage, DateTime timestamp, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public string ConfigDirectory => "fake";
        public string OverlayDirectory => "fake/overlays";
        public AppSettings Current { get; } = new();
        public event EventHandler? SettingsChanged { add { } remove { } }
        public void Save(AppSettings settings) { }
    }

    private sealed class FakeAdminLauncher : IAdminLauncher
    {
        public bool EnsurePinConfigured(System.Windows.Window? owner) => true;
        public AdminResult OpenAdmin(System.Windows.Window owner) => new(false, false, false);
    }
}
