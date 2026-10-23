using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

public class BoothControllerTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public void TryTrigger_InStartingState_ReturnsFalse()
    {
        var h = new Harness(markReady: false);

        Assert.Equal(BoothState.Starting, h.Controller.State);
        Assert.False(h.Controller.TryTrigger());
        Assert.Equal(0, h.Camera.CaptureCount);
    }

    [Theory]
    [InlineData(CameraStatus.Stopped)]
    [InlineData(CameraStatus.Starting)]
    [InlineData(CameraStatus.NoDevice)]
    [InlineData(CameraStatus.AccessDenied)]
    [InlineData(CameraStatus.Error)]
    public void TryTrigger_CameraNotRunning_ReturnsFalse(CameraStatus status)
    {
        var h = new Harness();
        h.Camera.Status = status;

        Assert.False(h.Controller.TryTrigger());
        Assert.Equal(BoothState.Idle, h.Controller.State);
    }

    [Fact]
    public void TryTrigger_InIdleWithRunningCamera_StartsCountdown()
    {
        var h = new Harness();

        Assert.True(h.Controller.TryTrigger());

        Assert.Equal(BoothState.Countdown, h.Controller.State);
        Assert.Equal(3, h.Controller.CountdownValue);
        Assert.Equal(1, h.Controller.ShotIndex);
        Assert.Equal(1, h.Controller.ShotCount);
    }

    [Fact]
    public void SecondTrigger_DuringSession_IsIgnored()
    {
        var h = new Harness();
        Assert.True(h.Controller.TryTrigger());

        Assert.False(h.Controller.TryTrigger());
        h.Time.Advance(Second);
        Assert.False(h.Controller.TryTrigger());

        h.RunToIdle();
        Assert.Equal(1, h.Camera.CaptureCount);
        Assert.Single(h.Storage.Saved);
    }

    [Fact]
    public void Countdown_TicksOncePerSecond_ThenCapturesWithFlash()
    {
        var h = new Harness();
        var flashes = 0;
        h.Controller.FlashRequested += (_, _) => flashes++;

        h.Controller.TryTrigger();
        Assert.Equal(3, h.Controller.CountdownValue);

        h.Time.Advance(Second);
        Assert.Equal(2, h.Controller.CountdownValue);
        Assert.Equal(0, h.Camera.CaptureCount);

        h.Time.Advance(Second);
        Assert.Equal(1, h.Controller.CountdownValue);
        Assert.Equal(0, flashes);

        h.Time.Advance(Second);
        Assert.Equal(0, h.Controller.CountdownValue);
        Assert.Equal(1, flashes);
        Assert.Equal(1, h.Camera.CaptureCount);
        Assert.NotEqual(BoothState.Countdown, h.Controller.State);
    }

    [Fact]
    public void Session_ShowsPreviewWithSavedLayout_ThenReturnsToIdle()
    {
        var h = new Harness();
        h.Settings.Current.Flow.PrintingEnabled = false;
        var states = h.RecordStates();

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Preview);

        Assert.NotNull(h.Controller.PreviewImage);
        Assert.NotNull(h.Controller.LastSaved);

        h.RunToIdle();
        Assert.Null(h.Controller.PreviewImage);
        Assert.Equal(
            [BoothState.Countdown, BoothState.Capturing, BoothState.Preview, BoothState.Idle],
            states);
    }

    [Fact]
    public void Strip_TakesThreePhotos_WithShotInfoAndPause()
    {
        var h = new Harness();
        h.Settings.Current.Layout.Kind = LayoutKind.Strip;
        h.Settings.Current.Layout.StripPhotoCount = 3;
        h.Settings.Current.Flow.PrintingEnabled = false;
        h.Composer.RequiredPhotoCount = 3;
        var flashes = 0;
        var shotIndices = new List<int>();
        h.Controller.FlashRequested += (_, _) => flashes++;
        h.Controller.Changed += (_, _) =>
        {
            if (shotIndices.Count == 0 || shotIndices[^1] != h.Controller.ShotIndex)
                shotIndices.Add(h.Controller.ShotIndex);
        };

        h.Controller.TryTrigger();
        Assert.Equal(3, h.Controller.ShotCount);
        h.RunToIdle();

        Assert.Equal(3, h.Camera.CaptureCount);
        Assert.Equal(3, flashes);
        Assert.Equal(3, h.Composer.LastPhotos!.Count);
        Assert.Single(h.Storage.Saved);
        Assert.Equal(3, h.Storage.Saved[0].Originals.Count);
        Assert.Contains(1, shotIndices);
        Assert.Contains(2, shotIndices);
        Assert.Contains(3, shotIndices);
    }

    [Fact]
    public void PrintingDisabled_NeverCallsPrint()
    {
        var h = new Harness();
        h.Settings.Current.Flow.PrintingEnabled = false;

        h.Controller.TryTrigger();
        h.RunToIdle();

        Assert.Equal(0, h.Printer.PrintCalls);
        Assert.Single(h.Storage.Saved);
    }

    [Fact]
    public async Task Printing_RunsParallelToPreview_ThenShowsPrintingStateUntilDone()
    {
        var h = new Harness();

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Preview);
        Assert.Equal(1, h.Printer.PrintCalls);   // Druck startet schon während der Vorschau
        Assert.Same(h.Composer.Result, h.Printer.LastPage);

        h.AdvanceUntil(() => h.Controller.State == BoothState.Printing);
        Assert.Equal(BoothState.Printing, h.Controller.State);

        h.Printer.Status?.Report("Warte auf den Drucker …");
        await WaitUntilAsync(() => h.Controller.StatusMessage == "Warte auf den Drucker …");

        h.Printer.Complete(new PrintResult(PrintOutcome.Completed));
        await WaitUntilAsync(() => h.Controller.State == BoothState.Idle);
        Assert.Null(h.Controller.ErrorMessage);
    }

    [Fact]
    public void PrintFinishedDuringPreview_SkipsPrintingState()
    {
        var h = new Harness();
        h.Settings.Current.Flow.PreviewSeconds = 6;
        var states = h.RecordStates();

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Preview);
        h.Printer.Complete(new PrintResult(PrintOutcome.Completed));
        h.RunToIdle();

        Assert.DoesNotContain(BoothState.Printing, states);
        Assert.DoesNotContain(BoothState.Error, states);
    }

    [Fact]
    public void PrintFailure_ShowsErrorMessage_ThenReturnsToIdle()
    {
        var h = new Harness();
        var states = h.RecordStates();

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Preview);
        h.Printer.Complete(new PrintResult(PrintOutcome.Failed, "Papier leer"));
        h.AdvanceUntil(() => h.Controller.State == BoothState.Error);

        Assert.Equal(
            "Das Foto wurde gespeichert, aber der Druck ist fehlgeschlagen: Papier leer",
            h.Controller.ErrorMessage);
        Assert.Single(h.Storage.Saved);

        h.RunToIdle();
        Assert.Contains(BoothState.Error, states);
        Assert.Equal(BoothState.Idle, h.Controller.State);
    }

    [Fact]
    public void PrintThrows_IsReportedAsError_ThenIdle()
    {
        var h = new Harness();
        h.Printer.ThrowOnPrint = true;

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Error);

        Assert.Contains("fehlgeschlagen", h.Controller.ErrorMessage);
        h.RunToIdle();
    }

    [Fact]
    public void PrintNeverReturns_HardTimeoutEndsInErrorThenIdle()
    {
        var h = new Harness();
        h.Settings.Current.Printer.TimeoutSeconds = 10;

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Error, maxSeconds: 200);

        Assert.StartsWith("Das Foto wurde gespeichert, aber der Druck ist fehlgeschlagen", h.Controller.ErrorMessage);
        h.RunToIdle();
    }

    [Fact]
    public void SaveFailure_ShowsErrorWithoutPrinting_ThenIdle()
    {
        var h = new Harness();
        h.Storage.Failure = new IOException("Datenträger voll");
        var states = h.RecordStates();

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Error);

        Assert.False(string.IsNullOrWhiteSpace(h.Controller.ErrorMessage));
        Assert.Equal(0, h.Printer.PrintCalls);

        h.RunToIdle();
        Assert.Equal(BoothState.Idle, h.Controller.State);
        Assert.DoesNotContain(BoothState.Preview, states);
    }

    [Fact]
    public void CaptureFailure_ShowsError_ThenIdle()
    {
        var h = new Harness();
        h.Camera.CaptureFailure = new InvalidOperationException("Kamera getrennt");

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Error);

        Assert.False(string.IsNullOrWhiteSpace(h.Controller.ErrorMessage));
        Assert.Empty(h.Storage.Saved);

        h.RunToIdle();
        Assert.Equal(BoothState.Idle, h.Controller.State);
    }

    [Fact]
    public void ComposeFailure_ShowsError_ThenIdle()
    {
        var h = new Harness();
        h.Composer.Failure = new InvalidOperationException("Overlay fehlt");

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Error);
        h.RunToIdle();

        Assert.Empty(h.Storage.Saved);
        Assert.Equal(BoothState.Idle, h.Controller.State);
    }

    [Fact]
    public void AfterSession_NewSessionCanBeStarted()
    {
        var h = new Harness();
        h.Settings.Current.Flow.PrintingEnabled = false;

        h.Controller.TryTrigger();
        h.RunToIdle();
        Assert.Equal(BoothState.Idle, h.Controller.State);

        Assert.True(h.Controller.TryTrigger());
        h.RunToIdle();
        Assert.Equal(2, h.Storage.Saved.Count);
    }

    [Fact]
    public async Task Cancel_DuringCountdown_ReturnsToIdleWithoutError()
    {
        var h = new Harness();
        h.Controller.TryTrigger();
        h.Time.Advance(Second);

        h.Controller.Cancel();

        await WaitUntilAsync(() => h.Controller.State == BoothState.Idle);
        Assert.Null(h.Controller.ErrorMessage);
        Assert.Equal(0, h.Camera.CaptureCount);
    }

    [Fact]
    public void MirrorPhoto_FlipsPhotoHorizontally()
    {
        var h = new Harness();
        h.Settings.Current.Camera.MirrorPhoto = true;
        h.Settings.Current.Flow.PrintingEnabled = false;

        h.Controller.TryTrigger();
        h.RunToIdle();

        var photo = Assert.Single(h.Composer.LastPhotos!);
        Assert.True(photo.IsFrozen);
        Assert.Equal(h.Camera.Photo.PixelWidth, photo.PixelWidth);
        // Kamerabild: links Rot, rechts Blau → nach dem Spiegeln umgekehrt.
        Assert.Equal(FakeCamera.Blue, ReadPixel(photo, 0));
        Assert.Equal(FakeCamera.Red, ReadPixel(photo, 1));
    }

    [Fact]
    public void MirrorPhotoOff_KeepsPhotoUntouched()
    {
        var h = new Harness();
        h.Settings.Current.Flow.PrintingEnabled = false;

        h.Controller.TryTrigger();
        h.RunToIdle();

        var photo = Assert.Single(h.Composer.LastPhotos!);
        Assert.Same(h.Camera.Photo, photo);
    }

    // ---- Hilfsmittel ----

    private static uint ReadPixel(BitmapSource source, int x)
    {
        var pixels = new uint[1];
        source.CopyPixels(new System.Windows.Int32Rect(x, 0, 1, 1), pixels, 4, 0);
        return pixels[0];
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "Bedingung wurde nicht rechtzeitig erfüllt.");
    }

    private sealed class Harness
    {
        public FakeTimeProvider Time { get; } = new();
        public FakeCamera Camera { get; } = new();
        public FakeComposer Composer { get; } = new();
        public FakePrinter Printer { get; } = new();
        public FakeStorage Storage { get; } = new();
        public FakeSettingsStore Settings { get; } = new();
        public BoothController Controller { get; }

        public Harness(bool markReady = true)
        {
            // Ohne SynchronizationContext laufen die Fortsetzungen des Controllers deterministisch
            // synchron im Thread, der die Fake-Zeit vorstellt (xunit installiert sonst einen Kontext).
            SynchronizationContext.SetSynchronizationContext(null);
            Settings.Current.Flow.CountdownSeconds = 3;
            Settings.Current.Flow.PreviewSeconds = 2;
            Settings.Current.Flow.PauseBetweenShotsSeconds = 1;
            Settings.Current.Flow.ErrorDisplaySeconds = 4;
            Controller = new BoothController(Camera, Composer, Printer, Storage, Settings, Time);
            if (markReady)
                Controller.MarkReady();
        }

        public List<BoothState> RecordStates()
        {
            var states = new List<BoothState>();
            Controller.StateChanged += (_, _) => states.Add(Controller.State);
            return states;
        }

        /// <summary>Lässt die Fake-Zeit sekundenweise laufen, bis die Bedingung gilt.</summary>
        public void AdvanceUntil(Func<bool> condition, int maxSeconds = 100)
        {
            for (var i = 0; i < maxSeconds && !condition(); i++)
            {
                Time.Advance(Second);
                Thread.Sleep(2); // Threadpool-Fortsetzungen (z. B. Druck-Task) abwarten
            }
            Assert.True(condition(), "Bedingung wurde in der Fake-Zeit nicht erreicht.");
        }

        public void RunToIdle() =>
            AdvanceUntil(() => Controller.State == BoothState.Idle, maxSeconds: 400);
    }

    private sealed class FakeCamera : ICameraService
    {
        public const uint Red = 0xFFFF0000;
        public const uint Blue = 0xFF0000FF;

        public BitmapSource Photo { get; } = CreatePhoto();
        public int CaptureCount { get; private set; }
        public Exception? CaptureFailure { get; set; }

        public CameraStatus Status { get; set; } = CameraStatus.Running;
        public string? StatusMessage => null;
        public CameraResolution? ActiveResolution => new(2, 1);
        public event EventHandler? StatusChanged { add { } remove { } }
        public event EventHandler<CameraFrame>? FrameArrived { add { } remove { } }

        public Task<IReadOnlyList<CameraDevice>> GetDevicesAsync() =>
            Task.FromResult<IReadOnlyList<CameraDevice>>([]);

        public Task<IReadOnlyList<CameraResolution>> GetResolutionsAsync(string deviceId) =>
            Task.FromResult<IReadOnlyList<CameraResolution>>([]);

        public Task StartAsync(CameraSettings settings, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;

        public Task<BitmapSource> CapturePhotoAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CaptureCount++;
            return CaptureFailure is null
                ? Task.FromResult(Photo)
                : Task.FromException<BitmapSource>(CaptureFailure);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>2 × 1 Pixel: links Rot, rechts Blau.</summary>
        private static BitmapSource CreatePhoto()
        {
            uint[] pixels = [Red, Blue];
            var bmp = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            bmp.Freeze();
            return bmp;
        }
    }

    private sealed class FakeComposer : ILayoutComposer
    {
        public int RequiredPhotoCount { get; set; } = 1;
        public Exception? Failure { get; set; }
        public IReadOnlyList<BitmapSource>? LastPhotos { get; private set; }
        public BitmapSource Result { get; } = CreateResult();

        public int GetRequiredPhotoCount(LayoutSettings layout) => RequiredPhotoCount;

        public BitmapSource Compose(IReadOnlyList<BitmapSource> photos, LayoutSettings layout, PageSpec page,
            string overlayDirectory, DateTime timestamp)
        {
            if (Failure is not null) throw Failure;
            LastPhotos = photos.ToList();
            return Result;
        }

        private static BitmapSource CreateResult()
        {
            var bmp = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            bmp.Freeze();
            return bmp;
        }
    }

    private sealed class FakePrinter : IPrintService
    {
        private readonly TaskCompletionSource<PrintResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PrintCalls { get; private set; }
        public bool ThrowOnPrint { get; set; }
        public BitmapSource? LastPage { get; private set; }
        public IProgress<string>? Status { get; private set; }

        public void Complete(PrintResult result) => _result.TrySetResult(result);

        public IReadOnlyList<PrinterInfo> GetPrinters() => [];
        public IReadOnlyList<PaperSizeInfo> GetPaperSizes(string? printerName) => [];
        public PageSpec GetPageSpec(PrinterSettings printer, LayoutKind layout) => PageSpec.Default;

        public Task<PrintResult> PrintAsync(BitmapSource page, PrinterSettings printer,
            IProgress<string>? status = null, CancellationToken ct = default)
        {
            PrintCalls++;
            LastPage = page;
            Status = status;
            if (ThrowOnPrint) throw new InvalidOperationException("Drucker nicht erreichbar");
            return _result.Task;
        }
    }

    private sealed class FakeStorage : IPhotoStorage
    {
        public List<(BitmapSource Layout, IReadOnlyList<BitmapSource> Originals)> Saved { get; } = [];
        public Exception? Failure { get; set; }

        public string ResolveTargetFolder(StorageSettings storage, DateTime timestamp) => "fake";

        public Task<SavedPhotoSet> SaveAsync(BitmapSource layout, IReadOnlyList<BitmapSource> originals,
            StorageSettings storage, DateTime timestamp, CancellationToken ct = default)
        {
            if (Failure is not null) return Task.FromException<SavedPhotoSet>(Failure);
            Saved.Add((layout, originals.ToList()));
            return Task.FromResult(new SavedPhotoSet("fake/layout.jpg", ["fake/orig1.jpg"]));
        }
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public string ConfigDirectory => "fake";
        public string OverlayDirectory => "fake/overlays";
        public AppSettings Current { get; } = new();
        public event EventHandler? SettingsChanged { add { } remove { } }
        public void Save(AppSettings settings) { }
    }
}
