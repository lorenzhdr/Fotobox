using System.Windows.Input;
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

    // ---- Tastenbelegung ----

    private static readonly KeyChord Space = new(Key.Space, ModifierKeys.None);
    private static readonly KeyChord Enter = new(Key.Enter, ModifierKeys.None);

    [Fact]
    public void HandleKey_DefaultSettings_MapsSpaceAdminAndEscape()
    {
        var h = new Harness();

        Assert.Equal(BoothKeyAction.Trigger, h.ViewModel.HandleKey(Space));
        Assert.Equal(BoothKeyAction.OpenAdmin, h.ViewModel.HandleKey(new KeyChord(Key.A, ModifierKeys.Control | ModifierKeys.Alt)));
        Assert.Equal(BoothKeyAction.CancelCountdown, h.ViewModel.HandleKey(new KeyChord(Key.Escape, ModifierKeys.None)));
        Assert.Equal(BoothKeyAction.None, h.ViewModel.HandleKey(new KeyChord(Key.X, ModifierKeys.None)));
        Assert.Equal(BoothKeyAction.None, h.ViewModel.HandleKey(new KeyChord(Key.A, ModifierKeys.None)));
        Assert.Equal(BoothKeyAction.None, h.ViewModel.HandleKey(new KeyChord(Key.A, ModifierKeys.Control)));
        Assert.Equal(BoothKeyAction.None, h.ViewModel.HandleKey(new KeyChord(Key.Space, ModifierKeys.Shift)));
    }

    [Fact]
    public void HandleKey_UsesConfiguredKeys_AndReparsesAfterSettingsChange()
    {
        var h = new Harness();
        Assert.Equal(BoothKeyAction.Trigger, h.ViewModel.HandleKey(Space));   // Belegung ist jetzt zwischengespeichert

        var input = h.Settings.Current.Input;
        input.TriggerKeys = ["Enter", "F5", "Ctrl+Shift+P"];
        input.AdminKeys = ["Ctrl+Alt+A", "Ctrl+Alt+B"];
        input.CancelCountdownKeys = ["Back", "Q"];
        h.Settings.RaiseSettingsChanged();

        Assert.Equal(BoothKeyAction.Trigger, h.ViewModel.HandleKey(Enter));
        Assert.Equal(BoothKeyAction.Trigger, h.ViewModel.HandleKey(new KeyChord(Key.F5, ModifierKeys.None)));
        Assert.Equal(BoothKeyAction.Trigger, h.ViewModel.HandleKey(new KeyChord(Key.P, ModifierKeys.Control | ModifierKeys.Shift)));
        Assert.Equal(BoothKeyAction.OpenAdmin, h.ViewModel.HandleKey(new KeyChord(Key.B, ModifierKeys.Control | ModifierKeys.Alt)));
        Assert.Equal(BoothKeyAction.CancelCountdown, h.ViewModel.HandleKey(new KeyChord(Key.Back, ModifierKeys.None)));
        Assert.Equal(BoothKeyAction.CancelCountdown, h.ViewModel.HandleKey(new KeyChord(Key.Q, ModifierKeys.None)));
        Assert.Equal(BoothKeyAction.None, h.ViewModel.HandleKey(Space));   // alte Belegung gilt nicht mehr
        Assert.Equal(BoothKeyAction.None, h.ViewModel.HandleKey(new KeyChord(Key.Escape, ModifierKeys.None)));
    }

    [Fact]
    public void HandleKey_UnparsableEntries_AreIgnored_AndTriggerAndAdminKeepDefaults()
    {
        var h = new Harness();
        var input = h.Settings.Current.Input;
        input.TriggerKeys = ["Quatsch"];
        input.AdminKeys = [];
        input.CancelCountdownKeys = ["Ctrl+"];
        h.Settings.RaiseSettingsChanged();

        Assert.Equal(BoothKeyAction.Trigger, h.ViewModel.HandleKey(Space));
        Assert.Equal(BoothKeyAction.OpenAdmin, h.ViewModel.HandleKey(new KeyChord(Key.A, ModifierKeys.Control | ModifierKeys.Alt)));
        Assert.Equal(BoothKeyAction.None, h.ViewModel.HandleKey(new KeyChord(Key.Escape, ModifierKeys.None)));
    }

    [Fact]
    public void HintText_SingleKey_NamesTheKey()
    {
        var h = new Harness();
        Assert.Equal("Drücke die Leertaste für ein Foto 📸", h.ViewModel.HintText);

        h.Settings.Current.Input.TriggerKeys = ["Enter"];
        h.Settings.RaiseSettingsChanged();
        Assert.Equal("Drücke Eingabetaste für ein Foto 📸", h.ViewModel.HintText);

        h.Settings.Current.Input.TriggerKeys = ["Ctrl+F5"];
        h.Settings.RaiseSettingsChanged();
        Assert.Equal("Drücke Strg+F5 für ein Foto 📸", h.ViewModel.HintText);
    }

    [Fact]
    public void HintText_MultipleKeys_ListsAtMostThree()
    {
        var h = new Harness();

        h.Settings.Current.Input.TriggerKeys = ["Space", "Enter"];
        h.Settings.RaiseSettingsChanged();
        Assert.Equal("Drücke Leertaste oder Eingabetaste für ein Foto 📸", h.ViewModel.HintText);

        h.Settings.Current.Input.TriggerKeys = ["Space", "Enter", "F5", "F6"];
        h.Settings.RaiseSettingsChanged();
        Assert.Equal("Drücke Leertaste, Eingabetaste oder F5 für ein Foto 📸", h.ViewModel.HintText);
    }

    [Theory]
    [InlineData(30, "30 Sekunden")]
    [InlineData(45, "45 Sekunden")]
    [InlineData(59, "59 Sekunden")]
    [InlineData(60, "1 Minute")]
    [InlineData(89, "1 Minute")]
    [InlineData(90, "2 Minuten")]
    [InlineData(120, "2 Minuten")]
    [InlineData(150, "3 Minuten")]
    [InlineData(600, "10 Minuten")]
    public void FormatDuration_RoundsToSensibleUnits(int seconds, string expected) =>
        Assert.Equal(expected, BoothViewModel.FormatDuration(seconds));

    // ---- Countdown abbrechen ----

    [Fact]
    public void TryCancelCountdown_CancelsRunningCountdown_ButNotWhenAdminIsOpen()
    {
        var h = new Harness();
        h.Settings.Current.Flow.CountdownSeconds = 3;
        Assert.False(h.ViewModel.TryCancelCountdown());   // Leerlauf

        Assert.True(h.ViewModel.TryTrigger());
        h.ViewModel.IsAdminOpen = true;
        Assert.False(h.ViewModel.TryCancelCountdown());
        h.ViewModel.IsAdminOpen = false;

        Assert.True(h.ViewModel.TryCancelCountdown());
        SpinWait.SpinUntil(() => h.Controller.State == BoothState.Idle, TimeSpan.FromSeconds(10));
        Assert.Equal(BoothState.Idle, h.Controller.State);
        Assert.False(h.ViewModel.IsError);
    }

    // ---- Druckanzeige ----

    [Fact]
    public void PrintingAndPrinted_ShowProgressWaitTextAndBlockAdmin()
    {
        var h = new Harness();
        var flow = h.Settings.Current.Flow;
        flow.CountdownSeconds = 0;
        flow.PreviewSeconds = 2;
        flow.ExpectedPrintSeconds = 120;
        h.Camera.Photo = CreateBitmap();
        h.Composer.Result = CreateBitmap();
        h.Storage.Works = true;
        h.Printer.Pending = new TaskCompletionSource<PrintResult>();
        Assert.True(h.ViewModel.TryTrigger());

        // Die Sitzung läuft ab dem Task.Run-Sprung auf dem Threadpool; Zeit nur in der Vorschau vorstellen.
        Assert.True(SpinUntil(h, () => h.Controller.State == BoothState.Printing, advanceInPreview: true));

        Assert.True(h.ViewModel.IsPrinting);
        Assert.True(h.ViewModel.ShowResult);
        Assert.True(h.ViewModel.ShowPrintInfo);
        Assert.False(h.ViewModel.IsPrinted);
        Assert.False(h.ViewModel.ShowDone);
        Assert.False(h.ViewModel.CanOpenAdmin);
        Assert.False(h.ViewModel.IsPrintOverdue);
        Assert.Equal("Der Drucker ist etwas langsam – dein Foto kommt in ca. 2 Minuten.", h.ViewModel.PrintWaitText);
        Assert.InRange(h.ViewModel.PrintProgress, 0, 0.1);

        h.Time.Advance(TimeSpan.FromSeconds(120));
        Assert.True(h.ViewModel.IsPrintOverdue);
        Assert.Equal("Gleich geschafft …", h.ViewModel.PrintWaitText);
        Assert.Equal(1, h.ViewModel.PrintProgress);
        Assert.Equal(BoothState.Printing, h.Controller.State);

        h.Printer.Pending.SetResult(new PrintResult(PrintOutcome.Completed));
        Assert.True(SpinWait.SpinUntil(() => h.Controller.State == BoothState.Printed, TimeSpan.FromSeconds(10)));

        // Eigener Fertig-Bildschirm: Foto-Ansicht und Druckbereich sind ausgeblendet.
        Assert.True(h.ViewModel.IsPrinted);
        Assert.True(h.ViewModel.ShowDone);
        Assert.False(h.ViewModel.ShowResult);
        Assert.False(h.ViewModel.ShowPrintInfo);
        Assert.False(h.ViewModel.IsPrinting);
        Assert.False(h.ViewModel.IsPrintOverdue);
        Assert.False(h.ViewModel.CanOpenAdmin);
        Assert.False(h.ViewModel.TryTrigger());
        Assert.Equal(1, h.ViewModel.PrintProgress);

        Assert.True(SpinUntil(h, () => h.Controller.State == BoothState.Idle, advanceInPreview: false, advanceInPrinted: true));
        Assert.False(h.ViewModel.ShowPrintInfo);
        Assert.False(h.ViewModel.ShowResult);
        Assert.False(h.ViewModel.ShowDone);
        Assert.True(h.ViewModel.CanOpenAdmin);
    }

    private static bool SpinUntil(Harness h, Func<bool> condition, bool advanceInPreview, bool advanceInPrinted = false)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (condition())
                return true;
            var state = h.Controller.State;
            if ((advanceInPreview && state == BoothState.Preview) || (advanceInPrinted && state == BoothState.Printed))
                h.Time.Advance(TimeSpan.FromSeconds(1));
            Thread.Sleep(5);
        }
        return condition();
    }

    private static System.Windows.Media.Imaging.BitmapSource CreateBitmap()
    {
        var bmp = BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[4], 4);
        bmp.Freeze();
        return bmp;
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

    [StaFact]
    public void BoothWindow_PrintProgressBar_FillsTrackProportionally()
    {
        var h = new Harness();
        var window = new BoothWindow(h.ViewModel, h.Camera, new FakeAdminLauncher(), h.Settings);
        var bar = new System.Windows.Controls.ProgressBar
        {
            Style = (System.Windows.Style)window.Resources["PrintProgressBar"],
            Value = 0.5,
        };

        bar.Measure(new System.Windows.Size(800, 100));
        bar.Arrange(new System.Windows.Rect(0, 0, 800, 38));
        bar.ApplyTemplate();
        bar.UpdateLayout();

        var indicator = (System.Windows.FrameworkElement)bar.Template.FindName("PART_Indicator", bar);
        Assert.InRange(indicator.ActualWidth, 380, 420);
        bar.Value = 1;
        bar.UpdateLayout();
        Assert.InRange(indicator.ActualWidth, 780, 800);
    }

    // ---- Hilfsmittel ----

    private sealed class Harness
    {
        public FakeTimeProvider Time { get; } = new();
        public FakeCamera Camera { get; } = new();
        public FakeSettingsStore Settings { get; } = new();
        public FakeComposer Composer { get; } = new();
        public FakePrinter Printer { get; } = new();
        public FakeStorage Storage { get; } = new();
        public BoothController Controller { get; }
        public BoothViewModel ViewModel { get; }

        public Harness(bool markReady = true)
        {
            // Wie in den Controller-Tests: Fortsetzungen laufen synchron im aufrufenden Thread.
            SynchronizationContext.SetSynchronizationContext(null);
            Settings.Current.Flow.ErrorDisplaySeconds = 6;
            Controller = new BoothController(Camera, Composer, Printer, Storage, Settings, Time);
            ViewModel = new BoothViewModel(Controller, Camera, Settings);
            if (markReady)
                Controller.MarkReady();
        }
    }

    private sealed class FakeCamera : ICameraService
    {
        public Exception? CaptureFailure { get; set; }
        /// <summary>Foto für erfolgreiche Aufnahmen; ohne Wert schlägt die Aufnahme fehl.</summary>
        public BitmapSource? Photo { get; set; }
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
            Photo is not null && CaptureFailure is null
                ? Task.FromResult(Photo)
                : Task.FromException<BitmapSource>(CaptureFailure ?? new InvalidOperationException("Kein Foto im Test."));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeComposer : ILayoutComposer
    {
        /// <summary>Ergebnis von Compose; ohne Wert wird eine Ausnahme geworfen.</summary>
        public BitmapSource? Result { get; set; }

        public int GetRequiredPhotoCount(LayoutSettings layout) => 1;

        public BitmapSource Compose(IReadOnlyList<BitmapSource> photos, LayoutSettings layout, PageSpec page,
            string overlayDirectory, DateTime timestamp) => Result ?? throw new NotSupportedException();
    }

    private sealed class FakePrinter : IPrintService
    {
        /// <summary>Wenn gesetzt, endet der Druck erst mit diesem Ergebnis; sonst sofort erfolgreich.</summary>
        public TaskCompletionSource<PrintResult>? Pending { get; set; }

        public IReadOnlyList<PrinterInfo> GetPrinters() => [];
        public IReadOnlyList<PaperSizeInfo> GetPaperSizes(string? printerName) => [];
        public PageSpec GetPageSpec(PrinterSettings printer, LayoutKind layout) => PageSpec.Default;

        public Task<PrintResult> PrintAsync(BitmapSource page, PrinterSettings printer,
            IProgress<string>? status = null, CancellationToken ct = default) =>
            Pending?.Task ?? Task.FromResult(new PrintResult(PrintOutcome.Completed));
    }

    private sealed class FakeStorage : IPhotoStorage
    {
        /// <summary>Speichern gelingt; sonst wird eine Ausnahme geworfen.</summary>
        public bool Works { get; set; }

        public string ResolveTargetFolder(StorageSettings storage, DateTime timestamp) => "fake";

        public Task<SavedPhotoSet> SaveAsync(BitmapSource layout, IReadOnlyList<BitmapSource> originals,
            StorageSettings storage, DateTime timestamp, CancellationToken ct = default) =>
            Works ? Task.FromResult(new SavedPhotoSet("fake/layout.jpg", [])) : throw new NotSupportedException();
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public string ConfigDirectory => "fake";
        public string OverlayDirectory => "fake/overlays";
        public AppSettings Current { get; } = new();
        public event EventHandler? SettingsChanged;
        public void Save(AppSettings settings) { }
        public void RaiseSettingsChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeAdminLauncher : IAdminLauncher
    {
        public bool EnsurePinConfigured(System.Windows.Window? owner) => true;
        public AdminResult OpenAdmin(System.Windows.Window owner) => new(false, false, false);
    }
}
