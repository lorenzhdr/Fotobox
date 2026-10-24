using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.ViewModels.Admin;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

public class PinAttemptLimiterTests
{
    private static (PinAttemptLimiter Limiter, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider();
        return (new PinAttemptLimiter(time), time);
    }

    [Fact]
    public void FewerThanFiveFailures_DoNotLock()
    {
        var (limiter, _) = Create();

        for (var i = 0; i < 4; i++)
            limiter.RegisterFailure();

        Assert.False(limiter.IsLockedOut);
        Assert.Equal(1, limiter.RemainingAttempts);
    }

    [Fact]
    public void FifthFailure_LocksForThirtySeconds_ThenUnlocks()
    {
        var (limiter, time) = Create();

        for (var i = 0; i < 5; i++)
            limiter.RegisterFailure();

        Assert.True(limiter.IsLockedOut);
        Assert.Equal(TimeSpan.FromSeconds(30), limiter.RemainingLockout);
        Assert.Equal(0, limiter.RemainingAttempts);

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.True(limiter.IsLockedOut);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(limiter.IsLockedOut);
        Assert.Equal(1, limiter.RemainingAttempts); // Zähler bleibt: ein weiterer Fehlversuch löst die nächste Stufe aus
    }

    [Fact]
    public void FailuresDuringLockout_DoNotExtendIt()
    {
        var (limiter, time) = Create();
        for (var i = 0; i < 5; i++)
            limiter.RegisterFailure();

        time.Advance(TimeSpan.FromSeconds(20));
        limiter.RegisterFailure();

        Assert.Equal(TimeSpan.FromSeconds(10), limiter.RemainingLockout);
    }

    [Fact]
    public void Lockout_EscalatesWithEachFurtherFailure_UpToFifteenMinutes()
    {
        var (limiter, time) = Create();
        TimeSpan[] expected =
        [
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15),
        ];

        for (var i = 0; i < 5; i++)
            limiter.RegisterFailure();
        Assert.Equal(expected[0], limiter.RemainingLockout);

        for (var level = 1; level < expected.Length; level++)
        {
            // Nach Ablauf der Sperre genügt ein einziger weiterer Fehlversuch für die nächste Stufe.
            time.Advance(expected[level - 1]);
            Assert.False(limiter.IsLockedOut);
            Assert.Equal(1, limiter.RemainingAttempts);

            limiter.RegisterFailure();
            Assert.Equal(expected[level], limiter.RemainingLockout);
        }
    }

    [Fact]
    public void ExpiredLockout_DoesNotResetFailureCounter()
    {
        var (limiter, time) = Create();
        for (var i = 0; i < 5; i++)
            limiter.RegisterFailure();
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.False(limiter.IsLockedOut);

        limiter.RegisterFailure();

        Assert.True(limiter.IsLockedOut);
        Assert.Equal(TimeSpan.FromSeconds(60), limiter.RemainingLockout);
    }

    [Fact]
    public void Success_AfterEscalation_StartsOverAtThirtySeconds()
    {
        var (limiter, time) = Create();
        for (var i = 0; i < 6; i++)
        {
            limiter.RegisterFailure();
            time.Advance(TimeSpan.FromMinutes(20));
        }

        limiter.RegisterSuccess();
        for (var i = 0; i < 5; i++)
            limiter.RegisterFailure();

        Assert.Equal(TimeSpan.FromSeconds(30), limiter.RemainingLockout);
    }

    [Fact]
    public void Success_ResetsFailureCounter()
    {
        var (limiter, _) = Create();
        for (var i = 0; i < 4; i++)
            limiter.RegisterFailure();

        limiter.RegisterSuccess();
        limiter.RegisterFailure();

        Assert.False(limiter.IsLockedOut);
        Assert.Equal(4, limiter.RemainingAttempts);
    }

    [Fact]
    public void Lockout_IgnoresWallClockJumps_UsesMonotonicTime()
    {
        var time = new SplitClock();
        var limiter = new PinAttemptLimiter(time);
        for (var i = 0; i < 5; i++)
            limiter.RegisterFailure();

        // Die Wanduhr springt eine Stunde vor bzw. einen Tag zurück (nur UtcNow, nicht der monotone Zeitstempel): die Sperre bleibt gleich.
        time.Wall += TimeSpan.FromHours(1);
        Assert.True(limiter.IsLockedOut);
        Assert.Equal(TimeSpan.FromSeconds(30), limiter.RemainingLockout);

        time.Wall -= TimeSpan.FromDays(1);
        Assert.Equal(TimeSpan.FromSeconds(30), limiter.RemainingLockout);

        time.Monotonic += TimeSpan.FromSeconds(30);
        Assert.False(limiter.IsLockedOut);
    }

    /// <summary>Wanduhr und monotone Zeit lassen sich getrennt bewegen (FakeTimeProvider koppelt beide).</summary>
    private sealed class SplitClock : TimeProvider
    {
        public TimeSpan Wall { get; set; }
        public TimeSpan Monotonic { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Monotonic.Ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero) + Wall;
    }
}

public class PinRulesTests
{
    [Theory]
    [InlineData("1234", true)]
    [InlineData("123456789012", true)]
    [InlineData("123", false)]
    [InlineData("1234567890123", false)]
    [InlineData("12a4", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_RequiresFourToTwelveDigits(string? pin, bool expected) =>
        Assert.Equal(expected, PinRules.IsValid(pin));

    [Fact]
    public void GetError_ReportsMismatchAndAcceptsMatchingValidPins()
    {
        Assert.NotNull(PinRules.GetError("1234", "4321"));
        Assert.NotNull(PinRules.GetError("12", "12"));
        Assert.Null(PinRules.GetError("1234", "1234"));
    }
}

public sealed class AdminViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeStore _store;
    private readonly FakePinService _pin;
    private readonly FakeCamera _camera = new();
    private readonly FakePrint _print = new();
    private readonly FakeUi _ui = new();

    public AdminViewModelTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "overlays"));
        _store = new FakeStore(_dir);
        _pin = new FakePinService(_store);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AdminViewModel CreateViewModel(TimeProvider? time = null) => new(_store, _pin, _camera, _print, _ui, time);

    [Fact]
    public void EditsApplyToClone_NotToStoreSettings()
    {
        var vm = CreateViewModel();
        var original = _store.Current.Flow.CountdownSeconds;

        vm.CountdownSeconds = original + 3;
        vm.MirrorPhoto = !_store.Current.Camera.MirrorPhoto;

        Assert.NotSame(_store.Current, vm.Draft);
        Assert.Equal(original, _store.Current.Flow.CountdownSeconds);
        Assert.Equal(original + 3, vm.Draft.Flow.CountdownSeconds);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public void Cancel_DoesNotChangeStore_AndClosesWithoutSaving()
    {
        var vm = CreateViewModel();
        bool? closed = null;
        vm.CloseRequested += save => closed = save;

        vm.CountdownSeconds = 9;
        vm.CancelCommand.Execute(null);

        Assert.False(closed);
        Assert.False(vm.SettingsSaved);
        Assert.Equal(0, _store.SaveCount);
        Assert.Equal(5, _store.Current.Flow.CountdownSeconds);
    }

    [Fact]
    public void Save_WritesDraftToStore()
    {
        var vm = CreateViewModel();
        bool? closed = null;
        vm.CloseRequested += save => closed = save;

        vm.CountdownSeconds = 8;
        vm.PrintingEnabled = false;
        vm.IsStripLayout = true;
        vm.StripPhotoCount = 4;
        vm.FilePrefix = "Hochzeit";
        vm.JpegQuality = 80;
        vm.CaptionText = "Anna & Ben";
        vm.SaveCommand.Execute(null);

        Assert.True(closed);
        Assert.True(vm.SettingsSaved);
        Assert.Equal(1, _store.SaveCount);
        var saved = _store.Current;
        Assert.Equal(8, saved.Flow.CountdownSeconds);
        Assert.False(saved.Flow.PrintingEnabled);
        Assert.Equal(LayoutKind.Strip, saved.Layout.Kind);
        Assert.Equal(4, saved.Layout.StripPhotoCount);
        Assert.Equal("Hochzeit", saved.Storage.FilePrefix);
        Assert.Equal(80, saved.Storage.JpegQuality);
        Assert.Equal("Anna & Ben", saved.Layout.CaptionText);
    }

    [Fact]
    public void Save_WithInvalidInput_ShowsGermanMessage_AndDoesNotSave()
    {
        var vm = CreateViewModel();
        bool? closed = null;
        vm.CloseRequested += save => closed = save;

        vm.FilePrefix = "   ";
        vm.CountdownSeconds = 0;
        vm.SaveCommand.Execute(null);

        Assert.Null(closed);
        Assert.Equal(0, _store.SaveCount);
        Assert.True(vm.HasValidationMessage);
        Assert.Contains("Dateipräfix", vm.ValidationMessage);
        Assert.Contains("Countdown", vm.ValidationMessage);
    }

    [Fact]
    public void Save_OverlayLayoutWithoutFile_IsRejected()
    {
        var vm = CreateViewModel();
        vm.IsOverlayLayout = true;

        vm.SaveCommand.Execute(null);

        Assert.Equal(0, _store.SaveCount);
        Assert.Contains("Overlay", vm.ValidationMessage);
    }

    [Fact]
    public void LayoutRadioFlags_AreMutuallyExclusive()
    {
        var vm = CreateViewModel();
        Assert.True(vm.IsSingleLayout);

        vm.IsOverlayLayout = true;

        Assert.False(vm.IsSingleLayout);
        Assert.False(vm.IsStripLayout);
        Assert.True(vm.IsOverlayLayout);
        Assert.Equal(LayoutKind.Overlay, vm.Draft.Layout.Kind);
    }

    [Fact]
    public void ChangePin_CallsPinService_AndSurvivesLaterSave()
    {
        var vm = CreateViewModel();

        vm.NewPin = "123456";
        vm.RepeatPin = "123456";
        vm.ChangePinCommand.Execute(null);
        vm.CountdownSeconds = 7;
        vm.SaveCommand.Execute(null);

        Assert.Equal("123456", _pin.LastPin);
        Assert.Equal("hash:123456", _store.Current.Security.PinHash);
        Assert.Equal(7, _store.Current.Flow.CountdownSeconds);
    }

    [Fact]
    public void ChangePin_WithMismatch_DoesNotCallPinService()
    {
        var vm = CreateViewModel();

        vm.NewPin = "1234";
        vm.RepeatPin = "4321";
        vm.ChangePinCommand.Execute(null);

        Assert.Null(_pin.LastPin);
        Assert.Contains("stimmen nicht überein", vm.PinStatus);
    }

    [Fact]
    public void Save_WithPendingPinFields_AppliesPin()
    {
        var vm = CreateViewModel();

        vm.NewPin = "9876";
        vm.RepeatPin = "9876";
        vm.SaveCommand.Execute(null);

        Assert.Equal("9876", _pin.LastPin);
        Assert.Equal("hash:9876", _store.Current.Security.PinHash);
    }

    [Fact]
    public void Save_WithInvalidPendingPin_IsRejected()
    {
        var vm = CreateViewModel();

        vm.NewPin = "12";
        vm.RepeatPin = "12";
        vm.SaveCommand.Execute(null);

        Assert.Equal(0, _store.SaveCount);
        Assert.Null(_pin.LastPin);
    }

    [Fact]
    public async Task Initialize_LoadsDevicesResolutionsPrintersAndPapers()
    {
        _store.Current.Camera.DeviceId = "cam2";
        _store.Current.Camera.ResolutionWidth = 1280;
        _store.Current.Camera.ResolutionHeight = 720;
        _store.Current.Printer.PrinterName = "Fotodrucker";
        _store.Current.Printer.PaperSizeName = "4x6";
        var vm = CreateViewModel();

        await vm.InitializeAsync();

        Assert.Equal(2, vm.Devices.Count);
        Assert.Equal("cam2", vm.SelectedDevice?.Id);
        Assert.Null(vm.Resolutions[0].Resolution);
        Assert.Equal("Automatisch", vm.Resolutions[0].Display);
        Assert.Equal(new CameraResolution(1280, 720), vm.SelectedResolution?.Resolution);

        Assert.Equal("Windows-Standarddrucker", vm.Printers[0].Display);
        Assert.Null(vm.Printers[0].Name);
        Assert.Equal("Fotodrucker", vm.SelectedPrinter?.Name);
        Assert.Equal("4x6", vm.SelectedPaper?.Info?.Name);
        Assert.Empty(_camera.StartCalls);
    }

    [Fact]
    public async Task Initialize_KeepsUnavailablePrinterSelection()
    {
        _store.Current.Printer.PrinterName = "Abwesender Drucker";
        var vm = CreateViewModel();

        await vm.InitializeAsync();

        Assert.Equal("Abwesender Drucker", vm.SelectedPrinter?.Name);
        Assert.Contains("nicht verfügbar", vm.SelectedPrinter?.Display);
    }

    [Fact]
    public async Task DeviceChange_RestartsCameraWithDraftSettings_WithoutTouchingStore()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.MirrorPhoto = true;

        await vm.SelectDeviceAsync(vm.Devices[1]);

        var call = Assert.Single(_camera.StartCalls);
        Assert.Equal("cam2", call.DeviceId);
        Assert.Null(call.ResolutionWidth);
        Assert.True(call.MirrorPhoto);
        Assert.True(vm.CameraTouched);
        Assert.Null(_store.Current.Camera.DeviceId);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public async Task RestoreCamera_StartsCameraWithSavedSettings()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        await vm.SelectDeviceAsync(vm.Devices[1]);

        await vm.RestoreCameraAsync(new CameraSettings { DeviceId = "cam1" });

        Assert.Equal("cam1", _camera.StartCalls.Last().DeviceId);
    }

    [Fact]
    public async Task ResolutionChange_UpdatesDraftAndRestartsCamera()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        vm.SelectedResolution = vm.Resolutions.First(r => r.Resolution == new CameraResolution(640, 480));
        await vm.RestartCameraAsync();

        Assert.Equal(640, vm.Draft.Camera.ResolutionWidth);
        Assert.Equal(480, vm.Draft.Camera.ResolutionHeight);
        Assert.Equal(640, _camera.StartCalls.Last().ResolutionWidth);
    }

    [Fact]
    public async Task PrinterChange_ReloadsPaperSizes_AndResetsPaper()
    {
        _store.Current.Printer.PaperSizeName = "4x6";
        _store.Current.Printer.PaperWidthMm = 101.6;
        _store.Current.Printer.PaperHeightMm = 152.4;
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.SelectPrinterAsync(vm.Printers.First(p => p.Name == "Bürodrucker"));

        Assert.Equal("Bürodrucker", vm.Draft.Printer.PrinterName);
        Assert.Null(vm.Draft.Printer.PaperSizeName);
        Assert.Equal("Bürodrucker", _print.LastPaperQuery);
        Assert.Null(vm.SelectedPaper?.Info);
    }

    [Fact]
    public void ExitCommand_RequiresConfirmation()
    {
        var vm = CreateViewModel();
        bool? closed = null;
        vm.CloseRequested += save => closed = save;

        _ui.ConfirmResult = false;
        vm.ExitCommand.Execute(null);
        Assert.False(vm.ExitRequested);
        Assert.Null(closed);

        _ui.ConfirmResult = true;
        vm.ExitCommand.Execute(null);
        Assert.True(vm.ExitRequested);
        Assert.False(closed);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public async Task TestPrint_UsesDraftPrinterWithOneCopy_AndReportsStatus()
    {
        var vm = CreateViewModel();
        vm.Copies = 3;
        _store.Current.Printer.Copies = 1;
        vm.Draft.Printer.PrinterName = "Fotodrucker";

        await vm.TestPrintCommand.ExecuteAsync(null);

        Assert.NotNull(_print.PrintedPage);
        Assert.Equal(PageSpec.Default.PixelWidth, _ui.LastTestPage?.PixelWidth);
        Assert.Equal("Fotodrucker", _print.PrintedSettings?.PrinterName);
        Assert.Equal(1, _print.PrintedSettings?.Copies);
        Assert.Equal(3, vm.Draft.Printer.Copies);
        Assert.Equal("Testdruck abgeschlossen.", vm.TestPrintStatus);
        Assert.False(vm.IsTestPrinting);
    }

    [Fact]
    public async Task TestPrint_Failure_ShowsMessage()
    {
        _print.Result = new PrintResult(PrintOutcome.Failed, "Papier leer.");
        var vm = CreateViewModel();

        await vm.TestPrintCommand.ExecuteAsync(null);

        Assert.Contains("fehlgeschlagen", vm.TestPrintStatus);
        Assert.Contains("Papier leer.", vm.TestPrintStatus);
    }

    [Fact]
    public async Task TestPrint_HangingPrint_TimesOut_AndReEnablesButton()
    {
        var time = new FakeTimeProvider();
        _print.Gate = new TaskCompletionSource<PrintResult>();
        var vm = CreateViewModel(time);

        var running = vm.TestPrintCommand.ExecuteAsync(null);
        Assert.True(vm.IsTestPrinting);
        Assert.False(vm.TestPrintCommand.CanExecute(null));

        // Die Seitengröße wird im Hintergrund ermittelt: Die Zeit erst vorstellen, wenn der Druck wirklich wartet.
        await WaitUntilAsync(() => _print.PrintedPage is not null);
        while (!running.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(vm.Draft.Printer.TimeoutSeconds + 30));
            await Task.Delay(10);
        }
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("Zeitüberschreitung", vm.TestPrintStatus);
        Assert.False(vm.IsTestPrinting);
        Assert.True(vm.TestPrintCommand.CanExecute(null));
    }

    [Fact]
    public async Task TestPrint_ClosingAdminView_CancelsHangingPrint()
    {
        _print.Gate = new TaskCompletionSource<PrintResult>();
        var vm = CreateViewModel(new FakeTimeProvider());

        var running = vm.TestPrintCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => _print.PrintedPage is not null);
        vm.Dispose();
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(_print.LastToken.IsCancellationRequested);
        Assert.Equal("Testdruck abgebrochen.", vm.TestPrintStatus);
        Assert.False(vm.IsTestPrinting);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Bedingung wurde nicht rechtzeitig erfüllt.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task OverlayHint_NamesRecommendedPixelSize_AfterBackgroundLookup()
    {
        var vm = CreateViewModel();

        // Bis die Seitengröße ermittelt ist, steht ein neutraler Text da (kein synchroner Druckerzugriff im Getter).
        Assert.Contains("wird ermittelt", vm.OverlayHint);
        Assert.Equal(0, _print.PageSpecCalls);

        await vm.RefreshPageSpecAsync();

        Assert.Contains("1200 × 1800", vm.OverlayHint);
    }

    [Fact]
    public async Task OverlayHint_PageSpecArrives_RaisesPropertyChanged()
    {
        var vm = CreateViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await vm.RefreshPageSpecAsync();

        Assert.Contains(nameof(AdminViewModel.OverlayHint), raised);
    }

    [Fact]
    public async Task OverlayHint_StaleResult_IsDiscarded()
    {
        // Erste Abfrage (Querformat) hängt, zweite (Hochformat) ist sofort fertig und gewinnt.
        using var release = new ManualResetEventSlim();
        _print.PageSpecFor = p =>
        {
            if (p.Orientation == PageOrientation.Landscape)
            {
                release.Wait(TimeSpan.FromSeconds(10));
                return new PageSpec(152.4, 101.6);
            }
            return new PageSpec(210, 297);
        };
        var vm = CreateViewModel();
        vm.Draft.Printer.Orientation = PageOrientation.Landscape;
        var first = vm.RefreshPageSpecAsync();
        vm.Draft.Printer.Orientation = PageOrientation.Portrait;
        await vm.RefreshPageSpecAsync();
        Assert.Contains("2480 × 3508", vm.OverlayHint);

        release.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("2480 × 3508", vm.OverlayHint);
    }

    [Fact]
    public async Task OverlayHint_PageSpecThrows_FallsBackToDefault()
    {
        _print.PageSpecFor = _ => throw new InvalidOperationException("Drucker offline");
        var vm = CreateViewModel();

        await vm.RefreshPageSpecAsync();

        Assert.Contains("1200 × 1800", vm.OverlayHint);
    }

    [Fact]
    public async Task PageSpecLookup_HangingPrinter_TimesOut_WithoutBlocking()
    {
        var time = new FakeTimeProvider();
        using var release = new ManualResetEventSlim();
        _print.PageSpecFor = _ =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return PageSpec.Default;
        };
        var vm = CreateViewModel(time);

        var refresh = vm.RefreshPageSpecAsync();
        Assert.Contains("wird ermittelt", vm.OverlayHint);
        while (!refresh.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(10);
        }
        await refresh;
        release.Set();

        // Zeitüberschreitung → Standardgröße statt Endlos-Warten.
        Assert.Contains("1200 × 1800", vm.OverlayHint);
    }

    [Fact]
    public async Task PaperChange_RefreshesPageSpec()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        var before = _print.PageSpecCalls;

        vm.SelectedPaper = vm.PaperSizes.First(p => p.Info?.Name == "A4");
        await WaitUntilAsync(() => _print.PageSpecCalls > before);
        await WaitUntilAsync(() => !vm.OverlayHint.Contains("ermittelt"));

        Assert.Contains("A4", vm.SelectedPaper!.Display);
    }

    [StaFact]
    public void ImportOverlay_CopiesPngIntoOverlayDirectory_AndSetsDraft()
    {
        var source = Path.Combine(_dir, "rahmen.png");
        WritePng(source, 40, 60);
        var vm = CreateViewModel();

        var ok = vm.ImportOverlay(source);

        Assert.True(ok);
        Assert.True(File.Exists(Path.Combine(_store.OverlayDirectory, "rahmen.png")));
        Assert.Equal("rahmen.png", vm.Draft.Layout.OverlayFileName);
        Assert.NotNull(vm.OverlayThumbnail);
        Assert.Contains("40 × 60", vm.OverlayInfo);
        Assert.Null(_store.Current.Layout.OverlayFileName);
    }

    [StaFact]
    public void ImportOverlay_SameNameDifferentContent_KeepsExistingFile_AndUsesNewName()
    {
        var existing = Path.Combine(_store.OverlayDirectory, "rahmen.png");
        WritePng(existing, 10, 10);
        var existingBytes = File.ReadAllBytes(existing);
        var source = Path.Combine(_dir, "rahmen.png");
        WritePng(source, 40, 60);
        var vm = CreateViewModel();
        vm.Draft.Layout.OverlayFileName = "rahmen.png"; // aktives Overlay

        Assert.True(vm.ImportOverlay(source));

        Assert.Equal(existingBytes, File.ReadAllBytes(existing));
        Assert.Equal("rahmen_2.png", vm.Draft.Layout.OverlayFileName);
        Assert.True(File.Exists(Path.Combine(_store.OverlayDirectory, "rahmen_2.png")));

        // Eine dritte, wieder andere Datei bekommt den nächsten freien Namen.
        var third = Path.Combine(_dir, "andere", "rahmen.png");
        Directory.CreateDirectory(Path.GetDirectoryName(third)!);
        WritePng(third, 20, 30);
        Assert.True(vm.ImportOverlay(third));
        Assert.Equal("rahmen_3.png", vm.Draft.Layout.OverlayFileName);
        Assert.Equal(existingBytes, File.ReadAllBytes(existing));
    }

    [StaFact]
    public void ImportOverlay_IdenticalFile_IsReused()
    {
        var source = Path.Combine(_dir, "rahmen.png");
        WritePng(source, 40, 60);
        var vm = CreateViewModel();
        Assert.True(vm.ImportOverlay(source));

        // Gleicher Inhalt aus anderem Ordner → vorhandene Datei wiederverwenden, nichts Neues anlegen.
        var copy = Path.Combine(_dir, "kopie", "rahmen.png");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(source, copy);
        Assert.True(vm.ImportOverlay(copy));

        Assert.Equal("rahmen.png", vm.Draft.Layout.OverlayFileName);
        Assert.Single(Directory.GetFiles(_store.OverlayDirectory));
    }

    [StaFact]
    public void ImportOverlay_RejectsNonPng()
    {
        var source = Path.Combine(_dir, "bild.jpg");
        File.WriteAllText(source, "kein Bild");
        var vm = CreateViewModel();

        Assert.False(vm.ImportOverlay(source));
        Assert.Null(vm.Draft.Layout.OverlayFileName);
        Assert.True(vm.HasValidationMessage);
    }

    [StaFact]
    public void Save_OverlayLayoutWithImportedFile_Succeeds()
    {
        var source = Path.Combine(_dir, "rahmen.png");
        WritePng(source, 10, 10);
        var vm = CreateViewModel();
        vm.IsOverlayLayout = true;
        vm.ImportOverlay(source);

        vm.SaveCommand.Execute(null);

        Assert.Equal(1, _store.SaveCount);
        Assert.Equal("rahmen.png", _store.Current.Layout.OverlayFileName);
    }

    [Fact]
    public void ChooseFolder_SetsDraftFolder_AndResetRestoresDefault()
    {
        var vm = CreateViewModel();
        _ui.FolderToPick = Path.Combine(_dir, "Fotos");

        vm.ChooseFolderCommand.Execute(null);
        Assert.Equal(_ui.FolderToPick, vm.Draft.Storage.PhotoFolder);
        Assert.False(vm.IsDefaultPhotoFolder);

        vm.ResetFolderCommand.Execute(null);
        Assert.Null(vm.Draft.Storage.PhotoFolder);
        Assert.True(vm.IsDefaultPhotoFolder);
    }

    [Fact]
    public void Validate_WritableCustomFolder_PassesAndLeavesNoFiles()
    {
        var vm = CreateViewModel();
        var folder = Path.Combine(_dir, "Fotos", "Neu");
        vm.Draft.Storage.PhotoFolder = folder;

        Assert.Empty(vm.Validate());
        Assert.True(Directory.Exists(folder));
        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }

    [Fact]
    public void Validate_UnwritableFolder_ReportsGermanError_AndSaveIsRefused()
    {
        var blocker = Path.Combine(_dir, "blockiert");
        File.WriteAllText(blocker, "Datei statt Ordner");
        var vm = CreateViewModel();
        vm.Draft.Storage.PhotoFolder = Path.Combine(blocker, "unterordner");

        var errors = vm.Validate();

        Assert.Contains(errors, e => e.StartsWith("In den Speicherordner kann nicht geschrieben werden"));

        vm.SaveCommand.Execute(null);
        Assert.Equal(0, _store.SaveCount);
        Assert.True(vm.HasValidationMessage);
    }

    [Fact]
    public void OpenFolderCommand_OpensEffectiveFolder()
    {
        var vm = CreateViewModel();
        vm.Draft.Storage.PhotoFolder = Path.Combine(_dir, "Fotos");

        // PhotoFolder wird beim Lesen aus dem Entwurf übernommen
        vm.OpenFolderCommand.Execute(null);

        Assert.Equal(Path.Combine(_dir, "Fotos"), _ui.OpenedFolder);
    }

    // ------------------------------------------------------------------ Streifenvariante, Schrift

    [Fact]
    public void StripStyle_DefaultsToDoubleCut_RadioFlagsAreExclusive_AndIsSaved()
    {
        var vm = CreateViewModel();
        Assert.True(vm.IsStripDoubleCut);
        Assert.False(vm.IsStripSingle);

        vm.IsStripSingle = true;
        Assert.False(vm.IsStripDoubleCut);
        Assert.Equal(StripStyle.Single, vm.Draft.Layout.StripStyle);

        vm.IsStripLayout = true;
        vm.SaveCommand.Execute(null);

        Assert.Equal(StripStyle.Single, _store.Current.Layout.StripStyle);
    }

    [Fact]
    public void StripStyle_LoadsSavedValue()
    {
        _store.Current.Layout.StripStyle = StripStyle.Single;

        var vm = CreateViewModel();

        Assert.True(vm.IsStripSingle);
    }

    [Fact]
    public async Task FontList_IsSortedByDisplayName_AndContainsDefaultFont()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        Assert.NotEmpty(vm.CaptionFonts);
        var names = vm.CaptionFonts.Select(f => f.Display).ToList();
        var sorted = names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        Assert.Equal(sorted, names);
        Assert.Equal(LayoutSettings.DefaultCaptionFont, vm.SelectedCaptionFont.Source);
        Assert.False(vm.IsCaptionFontMissing);
    }

    [Fact]
    public async Task CaptionFont_SelectionIsSavedAsFamilySource()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        var other = vm.CaptionFonts.First(f => f.IsInstalled && f.Source != LayoutSettings.DefaultCaptionFont);

        vm.SelectedCaptionFont = other;
        vm.SaveCommand.Execute(null);

        Assert.Equal(other.Source, _store.Current.Layout.CaptionFontFamily);
        Assert.Same(other.Family, vm.CaptionPreviewFont);
    }

    [Fact]
    public async Task CaptionFont_NotInstalled_StaysSelectedWithHint_AndIsKept()
    {
        const string missing = "Nicht installierte Testschrift 4711";
        _store.Current.Layout.CaptionFontFamily = missing;
        var vm = CreateViewModel();
        Assert.False(vm.IsCaptionFontMissing); // vor dem Laden der Liste noch unbekannt

        await vm.InitializeAsync();

        Assert.True(vm.IsCaptionFontMissing);
        Assert.Equal(missing, vm.SelectedCaptionFont.Source);
        Assert.Equal(missing, vm.CaptionFonts[0].Source);
        Assert.Equal("Diese Schrift ist auf diesem PC nicht installiert – es wird Segoe UI verwendet.", AdminViewModel.CaptionFontMissingHint);
        Assert.Equal(LayoutSettings.DefaultCaptionFont, vm.CaptionPreviewFont.Source);

        vm.SaveCommand.Execute(null);
        Assert.Equal(missing, _store.Current.Layout.CaptionFontFamily);
    }

    [Fact]
    public void CaptionPreviewText_FollowsCaption()
    {
        var vm = CreateViewModel();
        Assert.False(string.IsNullOrWhiteSpace(vm.CaptionPreviewText));

        vm.CaptionText = "Hochzeit Anna & Ben";

        Assert.Equal("Hochzeit Anna & Ben", vm.CaptionPreviewText);
    }

    // ------------------------------------------------------------------ Druckdauer

    [Fact]
    public void ExpectedPrintSeconds_IsSaved()
    {
        var vm = CreateViewModel();
        Assert.Equal(240, vm.ExpectedPrintSeconds);

        vm.ExpectedPrintSeconds = 200;
        vm.SaveCommand.Execute(null);

        Assert.Equal(200, _store.Current.Flow.ExpectedPrintSeconds);
    }

    [Theory]
    [InlineData(SettingsLimits.ExpectedPrintMin - 1)]
    [InlineData(SettingsLimits.ExpectedPrintMax + 1)]
    public void ExpectedPrintSeconds_OutOfRange_IsRejected(int value)
    {
        var vm = CreateViewModel();
        vm.ExpectedPrintSeconds = value;

        var errors = vm.Validate();

        Assert.Contains(errors, e => e.Contains("erwartete Druckdauer") && e.Contains(SettingsLimits.ExpectedPrintMax.ToString()));
        vm.SaveCommand.Execute(null);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public void PrintTimeout_UsesSettingsLimits_NoSeparateMaximum()
    {
        var vm = CreateViewModel();

        vm.PrintTimeoutSeconds = SettingsLimits.PrintTimeoutMax;
        Assert.DoesNotContain(vm.Validate(), e => e.Contains("Druck-Timeout"));

        vm.PrintTimeoutSeconds = SettingsLimits.PrintTimeoutMax + 1;
        Assert.Contains(vm.Validate(), e => e.Contains("Druck-Timeout") && e.Contains(SettingsLimits.PrintTimeoutMax.ToString()));
    }

    [Fact]
    public void PrintTimeoutBelowExpectedPlusReserve_ShowsHint_ButIsNoError()
    {
        var vm = CreateViewModel();
        vm.ExpectedPrintSeconds = 120;
        vm.PrintTimeoutSeconds = 150;

        Assert.True(vm.HasPrintTimeoutHint);
        Assert.Contains("180", vm.PrintTimeoutHint);
        Assert.Empty(vm.Validate());

        vm.PrintTimeoutSeconds = 180;
        Assert.False(vm.HasPrintTimeoutHint);

        vm.ExpectedPrintSeconds = 300; // verschiebt die Grenze auf 360 s
        Assert.True(vm.HasPrintTimeoutHint);
        Assert.Contains("360", vm.PrintTimeoutHint);
    }

    // ------------------------------------------------------------------ Tasten

    private KeyActionEditor Keys(AdminViewModel vm, KeyAction action) => vm.KeyEditors.Single(e => e.Action == action);

    private static KeyChord Chord(Key key, ModifierKeys modifiers = ModifierKeys.None) => new(key, modifiers);

    [Fact]
    public void Keys_LoadDefaults_WithGermanDisplayNames()
    {
        var vm = CreateViewModel();

        Assert.Equal(["Leertaste"], Keys(vm, KeyAction.Trigger).Keys.Select(k => k.DisplayName));
        Assert.Equal(["Strg+Alt+A"], Keys(vm, KeyAction.Admin).Keys.Select(k => k.DisplayName));
        Assert.Equal(["Esc"], Keys(vm, KeyAction.CancelCountdown).Keys.Select(k => k.DisplayName));
        Assert.Equal(["Foto auslösen", "Admin-Ansicht öffnen", "Countdown abbrechen"], vm.KeyEditors.Select(e => e.Title));
        Assert.Equal("Strg+Alt+A", vm.AdminKeysDisplay);
    }

    [Fact]
    public void Keys_InvalidStoredEntries_AreReplacedByDefaults()
    {
        _store.Current.Input.TriggerKeys = ["Quatsch", "Alt+F4"];

        var vm = CreateViewModel();

        Assert.Equal(["Space"], vm.Draft.Input.TriggerKeys);
        Assert.Single(Keys(vm, KeyAction.Trigger).Keys);
    }

    [Fact]
    public void Keys_AddKey_EndsCaptureAndUpdatesDraft()
    {
        var vm = CreateViewModel();
        var trigger = Keys(vm, KeyAction.Trigger);

        trigger.AddKeyCommand.Execute(null);
        Assert.True(vm.IsKeyCaptureActive);
        Assert.True(trigger.IsCapturing);
        Assert.Contains("Foto auslösen", vm.KeyCaptureHint);

        Assert.True(vm.CaptureKey(Chord(Key.Enter)));

        Assert.False(vm.IsKeyCaptureActive);
        Assert.False(trigger.IsCapturing);
        Assert.Equal(["Space", "Enter"], vm.Draft.Input.TriggerKeys);
        Assert.Equal(["Leertaste", "Eingabetaste"], trigger.Keys.Select(k => k.DisplayName));
        Assert.False(vm.HasKeyMessage);
    }

    [Fact]
    public void Keys_EscapeAndTab_CanBeCaptured()
    {
        var vm = CreateViewModel();
        vm.BeginKeyCapture(KeyAction.Trigger);
        Assert.True(vm.CaptureKey(Chord(Key.Tab)));

        vm.BeginKeyCapture(KeyAction.Admin);
        Assert.True(vm.CaptureKey(Chord(Key.Escape, ModifierKeys.Shift)));

        Assert.Contains("Tab", vm.Draft.Input.TriggerKeys);
        Assert.Contains("Shift+Escape", vm.Draft.Input.AdminKeys);
    }

    [Fact]
    public void Keys_KeyOfAnotherAction_IsRejectedWithMessage_AndCaptureStaysActive()
    {
        var vm = CreateViewModel();
        vm.BeginKeyCapture(KeyAction.Admin);

        var ok = vm.CaptureKey(Chord(Key.Space)); // gehört zu „Foto auslösen“

        Assert.False(ok);
        Assert.Equal("Diese Taste ist bereits ‚Foto auslösen‘ zugeordnet.", vm.KeyMessage);
        Assert.True(vm.IsKeyCaptureActive);
        Assert.Single(Keys(vm, KeyAction.Admin).Keys);
        Assert.Equal(["Ctrl+Alt+A"], vm.Draft.Input.AdminKeys);
    }

    [Fact]
    public void Keys_KeyAlreadyInSameAction_IsIgnoredWithHint()
    {
        var vm = CreateViewModel();
        vm.BeginKeyCapture(KeyAction.Trigger);

        Assert.False(vm.CaptureKey(Chord(Key.Space)));

        Assert.Contains("bereits", vm.KeyMessage);
        Assert.Single(Keys(vm, KeyAction.Trigger).Keys);
    }

    [Theory]
    [InlineData(Key.F4, ModifierKeys.Alt)]
    [InlineData(Key.Tab, ModifierKeys.Alt)]
    [InlineData(Key.Escape, ModifierKeys.Control)]
    [InlineData(Key.Delete, ModifierKeys.Control | ModifierKeys.Alt)]
    [InlineData(Key.LWin, ModifierKeys.None)]
    [InlineData(Key.LeftCtrl, ModifierKeys.Control)]
    public void Keys_SystemReservedCombination_IsRejected(Key key, ModifierKeys modifiers)
    {
        var vm = CreateViewModel();
        vm.BeginKeyCapture(KeyAction.CancelCountdown);

        var ok = vm.CaptureKey(Chord(key, modifiers));

        Assert.False(ok);
        Assert.Equal("Diese Tastenkombination ist vom System belegt und kann nicht verwendet werden.", vm.KeyMessage);
        Assert.Single(Keys(vm, KeyAction.CancelCountdown).Keys);
    }

    [Fact]
    public void Keys_CaptureWithoutBegin_IsIgnored()
    {
        var vm = CreateViewModel();

        Assert.False(vm.CaptureKey(Chord(Key.F5)));
        Assert.Single(Keys(vm, KeyAction.Trigger).Keys);
    }

    [Fact]
    public void Keys_CancelCapture_LeavesKeysUnchanged()
    {
        var vm = CreateViewModel();
        vm.BeginKeyCapture(KeyAction.Trigger);

        vm.CancelKeyCaptureCommand.Execute(null);

        Assert.False(vm.IsKeyCaptureActive);
        Assert.Equal(["Space"], vm.Draft.Input.TriggerKeys);
    }

    [Theory]
    [InlineData(KeyAction.Trigger)]
    [InlineData(KeyAction.Admin)]
    public void Keys_LastRequiredKey_CannotBeRemoved(KeyAction action)
    {
        var vm = CreateViewModel();
        var editor = Keys(vm, action);
        var only = editor.Keys[0];

        Assert.False(only.RemoveCommand.CanExecute(null));
        only.RemoveCommand.Execute(null);

        Assert.Single(editor.Keys);
    }

    [Fact]
    public void Keys_RequiredAction_KeyRemovableOnceASecondExists()
    {
        var vm = CreateViewModel();
        var trigger = Keys(vm, KeyAction.Trigger);
        var first = trigger.Keys[0];
        vm.BeginKeyCapture(KeyAction.Trigger);
        vm.CaptureKey(Chord(Key.Enter));
        Assert.True(first.RemoveCommand.CanExecute(null));

        first.RemoveCommand.Execute(null);

        Assert.Equal(["Enter"], vm.Draft.Input.TriggerKeys);
        Assert.False(trigger.Keys[0].RemoveCommand.CanExecute(null)); // jetzt die letzte
    }

    [Fact]
    public void Keys_OptionalAction_CanBeEmptied()
    {
        var vm = CreateViewModel();
        var cancel = Keys(vm, KeyAction.CancelCountdown);

        cancel.Keys[0].RemoveCommand.Execute(null);

        Assert.Empty(cancel.Keys);
        Assert.Empty(vm.Draft.Input.CancelCountdownKeys);
        Assert.Empty(vm.Validate());
    }

    [Fact]
    public void Keys_MaximumPerAction_DisablesAdding()
    {
        var vm = CreateViewModel();
        var trigger = Keys(vm, KeyAction.Trigger);
        for (var i = 1; trigger.Keys.Count < SettingsLimits.KeysPerActionMax; i++)
        {
            vm.BeginKeyCapture(KeyAction.Trigger);
            Assert.True(vm.CaptureKey(Chord(Key.F1 + i)));
        }

        Assert.Equal(SettingsLimits.KeysPerActionMax, trigger.Keys.Count);
        Assert.False(trigger.CanAddKey);
        Assert.False(trigger.AddKeyCommand.CanExecute(null));
        vm.BeginKeyCapture(KeyAction.Trigger);
        Assert.False(vm.IsKeyCaptureActive);
    }

    [Fact]
    public void Keys_OnlyOneCaptureAtATime_DisablesOtherAddButtons()
    {
        var vm = CreateViewModel();
        var admin = Keys(vm, KeyAction.Admin);

        Keys(vm, KeyAction.Trigger).AddKeyCommand.Execute(null);

        Assert.False(admin.AddKeyCommand.CanExecute(null));
        vm.CancelKeyCapture();
        Assert.True(admin.AddKeyCommand.CanExecute(null));
    }

    [Fact]
    public void Keys_Save_WritesCanonicalStrings()
    {
        var vm = CreateViewModel();
        vm.BeginKeyCapture(KeyAction.Trigger);
        vm.CaptureKey(Chord(Key.K, ModifierKeys.Shift | ModifierKeys.Control));
        vm.BeginKeyCapture(KeyAction.Admin);
        vm.CaptureKey(Chord(Key.PageDown, ModifierKeys.Alt));
        vm.BeginKeyCapture(KeyAction.CancelCountdown);
        vm.CaptureKey(Chord(Key.Back));

        vm.SaveCommand.Execute(null);

        Assert.Equal(1, _store.SaveCount);
        Assert.Equal(["Space", "Ctrl+Shift+K"], _store.Current.Input.TriggerKeys);
        Assert.Equal(["Ctrl+Alt+A", "Alt+PageDown"], _store.Current.Input.AdminKeys);
        Assert.Equal(["Escape", "Back"], _store.Current.Input.CancelCountdownKeys);
    }

    [Fact]
    public void Keys_ResetToDefaults_RestoresStandardAssignment()
    {
        var vm = CreateViewModel();
        vm.BeginKeyCapture(KeyAction.Trigger);
        vm.CaptureKey(Chord(Key.Enter));
        Keys(vm, KeyAction.CancelCountdown).Keys[0].RemoveCommand.Execute(null);

        vm.ResetKeysCommand.Execute(null);

        Assert.Equal(["Space"], vm.Draft.Input.TriggerKeys);
        Assert.Equal(["Ctrl+Alt+A"], vm.Draft.Input.AdminKeys);
        Assert.Equal(["Escape"], vm.Draft.Input.CancelCountdownKeys);
        Assert.Single(Keys(vm, KeyAction.Trigger).Keys);
    }

    [Fact]
    public void Keys_RequiredActionWithoutKey_FailsValidation()
    {
        var vm = CreateViewModel();
        Keys(vm, KeyAction.Admin).Keys.Clear();

        var errors = vm.Validate();

        Assert.Contains(errors, e => e.Contains("Admin-Ansicht öffnen"));
        vm.SaveCommand.Execute(null);
        Assert.Equal(0, _store.SaveCount);
    }

    // ------------------------------------------------------------------ Speichern-Pfad

    [Fact]
    public void Save_StoreThrows_ShowsMessage_AndStaysOpen()
    {
        var vm = CreateViewModel();
        bool? closed = null;
        vm.CloseRequested += save => closed = save;
        _store.ThrowOnSave = new IOException("gesperrt");

        vm.SaveCommand.Execute(null);

        Assert.Null(closed);
        Assert.False(vm.SettingsSaved);
        Assert.Contains("gesperrt", vm.ValidationMessage);
    }

    [Fact]
    public void Save_Twice_WritesOnlyOnce()
    {
        var vm = CreateViewModel();

        vm.SaveCommand.Execute(null);
        vm.SaveCommand.Execute(null);

        Assert.Equal(1, _store.SaveCount);
    }

    // ------------------------------------------------------------------ CameraChanged

    [Fact]
    public void CameraSettingsDiffer_DetectsEveryRelevantChange()
    {
        static CameraSettings Base() => new() { DeviceId = "cam1", ResolutionWidth = 1280, ResolutionHeight = 720, MirrorPreview = true, MirrorPhoto = false };

        Assert.False(Fotobox.Views.Admin.AdminLauncher.CameraSettingsDiffer(Base(), Base()));

        var device = Base(); device.DeviceId = "cam2";
        var width = Base(); width.ResolutionWidth = 1920;
        var height = Base(); height.ResolutionHeight = 1080;
        var auto = Base(); auto.ResolutionWidth = null; auto.ResolutionHeight = null;
        var preview = Base(); preview.MirrorPreview = false;
        var photo = Base(); photo.MirrorPhoto = true;

        foreach (var changed in new[] { device, width, height, auto, preview, photo })
            Assert.True(Fotobox.Views.Admin.AdminLauncher.CameraSettingsDiffer(Base(), changed));
    }

    private static CameraSettings SnapshotCamera(CameraSettings c) => new()
    {
        DeviceId = c.DeviceId,
        ResolutionWidth = c.ResolutionWidth,
        ResolutionHeight = c.ResolutionHeight,
        MirrorPreview = c.MirrorPreview,
        MirrorPhoto = c.MirrorPhoto,
    };

    private void UseSavedCamera(string? deviceId, int? width, int? height)
    {
        _store.Current.Camera.DeviceId = deviceId;
        _store.Current.Camera.ResolutionWidth = width;
        _store.Current.Camera.ResolutionHeight = height;
        _store.Current.Camera.MirrorPhoto = true;
    }

    [Fact]
    public async Task OpenAndSaveWithoutChanges_LeavesCameraSettingsUntouched()
    {
        UseSavedCamera("cam1", 1280, 720);
        var before = SnapshotCamera(_store.Current.Camera);
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        vm.SaveCommand.Execute(null);

        Assert.Equal(1, _store.SaveCount);
        Assert.False(Fotobox.Views.Admin.AdminLauncher.CameraSettingsDiffer(before, _store.Current.Camera));
        Assert.False(vm.CameraTouched);
        Assert.Empty(_camera.StartCalls);
    }

    [Fact]
    public async Task OpenAndSaveWithoutChanges_ResolutionListUnavailable_KeepsSavedResolution()
    {
        UseSavedCamera("cam1", 1280, 720);
        var before = SnapshotCamera(_store.Current.Camera);
        _camera.ResolutionsError = new InvalidOperationException("Kamera belegt");
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        Assert.Contains("nicht verfügbar", vm.SelectedResolution?.Display);
        vm.SaveCommand.Execute(null);

        Assert.False(Fotobox.Views.Admin.AdminLauncher.CameraSettingsDiffer(before, _store.Current.Camera));
        Assert.Equal(1280, _store.Current.Camera.ResolutionWidth);
        Assert.Equal(720, _store.Current.Camera.ResolutionHeight);
    }

    [Fact]
    public async Task OpenAndSaveWithoutChanges_SavedDeviceNotListed_KeepsSavedCamera()
    {
        UseSavedCamera("abgesteckte-kamera", 800, 600);
        var before = SnapshotCamera(_store.Current.Camera);
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        vm.SaveCommand.Execute(null);

        Assert.False(Fotobox.Views.Admin.AdminLauncher.CameraSettingsDiffer(before, _store.Current.Camera));
        Assert.Equal("abgesteckte-kamera", _store.Current.Camera.DeviceId);
    }

    [Fact]
    public async Task SavedResolutionMissingFromDevicesList_FallsBackToAutomatic()
    {
        UseSavedCamera("cam1", 1000, 500);
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        Assert.Null(vm.SelectedResolution?.Resolution);
        Assert.Null(vm.Draft.Camera.ResolutionWidth);
        vm.SaveCommand.Execute(null);
        Assert.Null(_store.Current.Camera.ResolutionWidth);
    }

    [Fact]
    public async Task ChangingResolution_IsDetectedAsCameraChange()
    {
        UseSavedCamera("cam1", 1280, 720);
        var before = SnapshotCamera(_store.Current.Camera);
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        vm.SelectedResolution = vm.Resolutions.First(r => r.Resolution == new CameraResolution(640, 480));
        vm.SaveCommand.Execute(null);

        Assert.True(Fotobox.Views.Admin.AdminLauncher.CameraSettingsDiffer(before, _store.Current.Camera));
    }

    private static void WritePng(string path, int width, int height)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            new byte[width * height * 4], width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // ------------------------------------------------------------------ Auswahl für Gäste / Maus

    [Fact]
    public void GuestChoices_DefaultIsCheckedAndLocked_OthersAreOff()
    {
        var vm = CreateViewModel();

        Assert.True(vm.IsGuestSingle);
        Assert.False(vm.IsGuestSingleEnabled);
        Assert.False(vm.IsGuestStripDouble);
        Assert.False(vm.IsGuestStripSingle);
        Assert.False(vm.IsGuestOverlay);
        Assert.True(vm.IsGuestStripDoubleEnabled);
        Assert.Empty(vm.Draft.Layout.GuestChoices);
    }

    [Fact]
    public void GuestChoices_NewlyChecked_AreAppendedAtTheEnd_AndStandardIsAdded()
    {
        var vm = CreateViewModel();

        vm.IsGuestStripSingle = true;
        vm.IsGuestStripDouble = true;

        Assert.Equal([GuestLayout.Single, GuestLayout.StripSingle, GuestLayout.StripDouble], vm.Draft.Layout.GuestChoices);
        Assert.True(vm.IsGuestSingle);

        vm.IsGuestStripSingle = false;
        Assert.Equal([GuestLayout.Single, GuestLayout.StripDouble], vm.Draft.Layout.GuestChoices);
        Assert.False(vm.IsGuestStripSingle);
        Assert.True(vm.IsGuestStripDouble);
    }

    [Fact]
    public void GuestChoices_OnlyStandardLeft_ClearsList()
    {
        var vm = CreateViewModel();
        vm.IsGuestStripDouble = true;
        Assert.Equal(2, vm.Draft.Layout.GuestChoices.Count);

        vm.IsGuestStripDouble = false;

        Assert.Empty(vm.Draft.Layout.GuestChoices);
        Assert.True(vm.IsGuestSingle);
    }

    [Fact]
    public void GuestChoices_StandardCannotBeUnchecked()
    {
        var vm = CreateViewModel();
        vm.IsGuestStripDouble = true;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.IsGuestSingle = false;

        Assert.True(vm.IsGuestSingle);
        Assert.Contains(GuestLayout.Single, vm.Draft.Layout.GuestChoices);
        Assert.Contains(nameof(AdminViewModel.IsGuestSingle), raised);   // Kästchen springt zurück auf „angehakt“
    }

    [Fact]
    public void GuestChoices_ChangingStandard_AddsNewStandardToActiveList()
    {
        var vm = CreateViewModel();
        vm.IsGuestStripDouble = true;   // Liste: Einzelfoto + 2 Streifen
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.IsOverlayLayout = true;

        Assert.Equal([GuestLayout.Single, GuestLayout.StripDouble, GuestLayout.Overlay], vm.Draft.Layout.GuestChoices);
        Assert.False(vm.IsGuestOverlayEnabled);
        Assert.True(vm.IsGuestSingleEnabled);
        Assert.Contains(nameof(AdminViewModel.IsGuestOverlay), raised);
        Assert.Contains(nameof(AdminViewModel.IsGuestOverlayEnabled), raised);
        Assert.Contains(nameof(AdminViewModel.ShowOverlayOptions), raised);

        vm.IsStripLayout = true;
        vm.IsStripSingle = true;
        Assert.Contains(GuestLayout.StripSingle, vm.Draft.Layout.GuestChoices);
        Assert.False(vm.IsGuestStripSingleEnabled);
        Assert.True(vm.IsGuestStripDoubleEnabled);
    }

    [Fact]
    public void GuestChoices_ChangingStandard_WithEmptyList_KeepsListEmpty()
    {
        var vm = CreateViewModel();

        vm.IsOverlayLayout = true;

        Assert.Empty(vm.Draft.Layout.GuestChoices);
        Assert.True(vm.IsGuestOverlay);
        Assert.False(vm.IsGuestSingle);
    }

    [Fact]
    public void ShowOptions_FollowStandardAndGuestChoices()
    {
        var vm = CreateViewModel();
        Assert.False(vm.ShowStripOptions);
        Assert.False(vm.ShowOverlayOptions);

        vm.IsGuestStripSingle = true;
        Assert.True(vm.ShowStripOptions);
        Assert.False(vm.ShowOverlayOptions);

        vm.IsGuestStripSingle = false;
        vm.IsGuestOverlay = true;
        Assert.False(vm.ShowStripOptions);
        Assert.True(vm.ShowOverlayOptions);

        vm.IsGuestOverlay = false;
        vm.IsStripLayout = true;
        Assert.True(vm.ShowStripOptions);
        Assert.False(vm.ShowOverlayOptions);
    }

    [Fact]
    public void Save_OverlayOnlyAsGuestChoice_WithoutFile_IsRejected()
    {
        var vm = CreateViewModel();
        vm.IsGuestOverlay = true;

        vm.SaveCommand.Execute(null);

        Assert.Equal(0, _store.SaveCount);
        Assert.Contains("Gastauswahl", vm.ValidationMessage);
        Assert.Contains("Foto mit Overlay", vm.ValidationMessage);
    }

    [Fact]
    public void Save_OverlayAsGuestChoice_WithFile_IsSaved()
    {
        var vm = CreateViewModel();
        File.WriteAllBytes(Path.Combine(_store.OverlayDirectory, "rahmen.png"), [1]);
        vm.Draft.Layout.OverlayFileName = "rahmen.png";
        vm.IsGuestOverlay = true;
        vm.IsGuestStripDouble = true;

        vm.SaveCommand.Execute(null);

        Assert.Equal(1, _store.SaveCount);
        Assert.Equal([GuestLayout.Single, GuestLayout.Overlay, GuestLayout.StripDouble], _store.Current.Layout.GuestChoices);
    }

    [Fact]
    public void Validate_StripChoiceWithInvalidPhotoCount_IsRejected()
    {
        var vm = CreateViewModel();
        vm.IsGuestStripDouble = true;
        vm.Draft.Layout.StripPhotoCount = SettingsLimits.StripPhotosMax + 1;

        Assert.Contains(vm.Validate(), e => e.Contains("Fotostreifen"));

        // Ohne Streifen unter den Wahlmöglichkeiten spielt die Fotozahl keine Rolle.
        vm.IsGuestStripDouble = false;
        Assert.DoesNotContain(vm.Validate(), e => e.Contains("Fotostreifen"));
    }

    [Fact]
    public void MouseTrigger_DefaultsOn_AndRoundtripsIntoSettings()
    {
        var vm = CreateViewModel();
        Assert.True(vm.TriggerOnLeftClick);
        Assert.True(vm.TriggerOnRightClick);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.TriggerOnLeftClick = false;
        vm.TriggerOnRightClick = false;
        Assert.Contains(nameof(AdminViewModel.TriggerOnLeftClick), raised);
        Assert.Contains(nameof(AdminViewModel.TriggerOnRightClick), raised);
        Assert.True(_store.Current.Input.TriggerOnLeftClick);   // Entwurf, noch nicht gespeichert

        vm.SaveCommand.Execute(null);

        Assert.False(_store.Current.Input.TriggerOnLeftClick);
        Assert.False(_store.Current.Input.TriggerOnRightClick);
    }

    [Fact]
    public void ResetKeys_AlsoRestoresMouseTriggers()
    {
        var vm = CreateViewModel();
        vm.TriggerOnLeftClick = false;
        vm.TriggerOnRightClick = false;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ResetKeysCommand.Execute(null);

        Assert.True(vm.TriggerOnLeftClick);
        Assert.True(vm.TriggerOnRightClick);
        Assert.Contains(nameof(AdminViewModel.TriggerOnLeftClick), raised);
    }

    [Fact]
    public void InvertMouseWheel_DefaultsOff_RoundtripsIntoSettings_AndSurvivesResetKeys()
    {
        var vm = CreateViewModel();
        Assert.False(vm.InvertMouseWheel);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.InvertMouseWheel = true;
        Assert.Contains(nameof(AdminViewModel.InvertMouseWheel), raised);
        Assert.False(_store.Current.Input.InvertMouseWheel);   // Entwurf, noch nicht gespeichert

        // Gehört zur Layoutwahl, nicht zu den Tasten.
        vm.ResetKeysCommand.Execute(null);
        Assert.True(vm.InvertMouseWheel);

        vm.SaveCommand.Execute(null);
        Assert.True(_store.Current.Input.InvertMouseWheel);
    }

    // ------------------------------------------------------------------ Reihenfolge der Gastauswahl

    private static GuestLayout[] OrderOf(AdminViewModel vm) => vm.GuestOrder.Select(i => i.Layout).ToArray();

    [Fact]
    public void GuestOrder_ListsChoicesWithNumberAndName()
    {
        var vm = CreateViewModel();
        Assert.Empty(vm.GuestOrder);
        Assert.False(vm.HasGuestOrder);

        vm.StripPhotoCount = 4;
        vm.IsGuestStripDouble = true;

        Assert.True(vm.HasGuestOrder);
        Assert.Equal(["1.", "2."], vm.GuestOrder.Select(i => i.PositionText));
        Assert.Equal(["Ein Foto", "4 Fotos – 2 Streifen"], vm.GuestOrder.Select(i => i.Name));

        vm.StripPhotoCount = 2;   // Der Name folgt der Fotozahl.
        Assert.Equal("2 Fotos – 2 Streifen", vm.GuestOrder[1].Name);
    }

    [Fact]
    public void GuestOrder_MoveUpAndDown_ReordersDraft_AndRaisesChange()
    {
        var vm = CreateViewModel();
        vm.IsGuestStripDouble = true;
        vm.IsGuestStripSingle = true;   // Einzelfoto, 2 Streifen, 1 Streifen
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.GuestOrder[0].MoveDownCommand.Execute(null);

        Assert.Equal([GuestLayout.StripDouble, GuestLayout.Single, GuestLayout.StripSingle], vm.Draft.Layout.GuestChoices);
        Assert.Equal(vm.Draft.Layout.GuestChoices, OrderOf(vm));
        Assert.Contains(nameof(AdminViewModel.GuestOrder), raised);

        vm.GuestOrder[2].MoveUpCommand.Execute(null);
        Assert.Equal([GuestLayout.StripDouble, GuestLayout.StripSingle, GuestLayout.Single], vm.Draft.Layout.GuestChoices);

        vm.MoveGuestLayoutUp(GuestLayout.Single);
        vm.MoveGuestLayoutDown(GuestLayout.StripDouble);
        Assert.Equal([GuestLayout.Single, GuestLayout.StripDouble, GuestLayout.StripSingle], vm.Draft.Layout.GuestChoices);
        // Das Standard-Kästchen bleibt angehakt und gesperrt, egal an welcher Position der Standard steht.
        Assert.True(vm.IsGuestSingle);
        Assert.False(vm.IsGuestSingleEnabled);
    }

    [Fact]
    public void GuestOrder_FirstCannotMoveUp_LastCannotMoveDown()
    {
        var vm = CreateViewModel();
        vm.IsGuestStripDouble = true;
        vm.IsGuestOverlay = true;   // Einzelfoto, 2 Streifen, Overlay

        Assert.False(vm.GuestOrder[0].MoveUpCommand.CanExecute(null));
        Assert.True(vm.GuestOrder[0].MoveDownCommand.CanExecute(null));
        Assert.True(vm.GuestOrder[1].MoveUpCommand.CanExecute(null));
        Assert.True(vm.GuestOrder[1].MoveDownCommand.CanExecute(null));
        Assert.True(vm.GuestOrder[2].MoveUpCommand.CanExecute(null));
        Assert.False(vm.GuestOrder[2].MoveDownCommand.CanExecute(null));

        var before = vm.Draft.Layout.GuestChoices.ToArray();
        vm.MoveGuestLayoutUp(GuestLayout.Single);
        vm.MoveGuestLayoutDown(GuestLayout.Overlay);
        vm.MoveGuestLayoutUp(GuestLayout.StripSingle);   // nicht in der Liste
        Assert.Equal(before, vm.Draft.Layout.GuestChoices);
    }

    [Fact]
    public void GuestOrder_NewlyChecked_GoesToTheEnd_AfterReordering()
    {
        var vm = CreateViewModel();
        vm.IsGuestStripDouble = true;
        vm.MoveGuestLayoutUp(GuestLayout.StripDouble);   // 2 Streifen, Einzelfoto

        vm.IsGuestStripSingle = true;

        Assert.Equal([GuestLayout.StripDouble, GuestLayout.Single, GuestLayout.StripSingle], vm.Draft.Layout.GuestChoices);

        // Entfernen und erneutes Anhaken hängt hinten an.
        vm.IsGuestStripDouble = false;
        vm.IsGuestStripDouble = true;
        Assert.Equal([GuestLayout.Single, GuestLayout.StripSingle, GuestLayout.StripDouble], vm.Draft.Layout.GuestChoices);
    }

    [Fact]
    public void GuestOrder_ChangingStandard_AppendsMissingStandard_ButKeepsOrderOtherwise()
    {
        var vm = CreateViewModel();
        vm.IsGuestStripDouble = true;
        vm.MoveGuestLayoutUp(GuestLayout.StripDouble);   // 2 Streifen, Einzelfoto

        vm.IsStripLayout = true;   // Standard = 2 Streifen: schon enthalten → Reihenfolge unverändert
        Assert.Equal([GuestLayout.StripDouble, GuestLayout.Single], vm.Draft.Layout.GuestChoices);

        vm.IsOverlayLayout = true;   // Standard = Overlay: fehlt → hinten angehängt
        Assert.Equal([GuestLayout.StripDouble, GuestLayout.Single, GuestLayout.Overlay], vm.Draft.Layout.GuestChoices);
    }

    [Fact]
    public void GuestOrder_OnlyStandardLeft_ClearsList()
    {
        var vm = CreateViewModel();
        vm.IsGuestStripDouble = true;
        vm.MoveGuestLayoutUp(GuestLayout.StripDouble);   // Standard steht hinten

        vm.IsGuestStripDouble = false;

        Assert.Empty(vm.Draft.Layout.GuestChoices);
        Assert.Empty(vm.GuestOrder);
        Assert.False(vm.HasGuestOrder);
    }

    [Fact]
    public void GuestOrder_IsWrittenToSavedSettings()
    {
        var vm = CreateViewModel();
        vm.IsGuestStripDouble = true;
        vm.IsGuestStripSingle = true;
        vm.MoveGuestLayoutDown(GuestLayout.Single);
        vm.MoveGuestLayoutDown(GuestLayout.Single);   // 2 Streifen, 1 Streifen, Einzelfoto

        vm.SaveCommand.Execute(null);

        Assert.Equal(1, _store.SaveCount);
        Assert.Equal([GuestLayout.StripDouble, GuestLayout.StripSingle, GuestLayout.Single], _store.Current.Layout.GuestChoices);
    }

    // ------------------------------------------------------------------ Standardwerte wiederherstellen

    private static readonly string[] DefaultTriggerKeys = [InputSettings.DefaultTriggerKey];

    private static string Json(AppSettings s) => System.Text.Json.JsonSerializer.Serialize(s, AppSettings.JsonOptions);

    /// <summary>Ändert in jedem Bereich mindestens einen Wert weg vom Standard.</summary>
    private async Task<AdminViewModel> CreateChangedViewModelAsync()
    {
        _store.Current.Security.PinHash = "hash:4711";
        _store.Current.Security.PinSalt = "salt";
        _store.Current.Security.PinIterations = 123_456;
        _store.Current.Flow.CountdownSeconds = 9;

        var vm = CreateViewModel();
        await vm.InitializeAsync();

        await vm.SelectDeviceAsync(vm.Devices[1]);
        vm.SelectedResolution = vm.Resolutions[1];
        vm.MirrorPreview = false;
        vm.MirrorPhoto = true;
        vm.CountdownSeconds = 12;
        vm.PrintingEnabled = false;
        vm.ExpectedPrintSeconds = 100;
        vm.IsStripLayout = true;
        vm.IsStripSingle = true;
        vm.StripPhotoCount = 4;
        vm.IsGuestOverlay = true;
        vm.CaptionText = "Hochzeit";
        vm.ShowDate = true;
        File.WriteAllBytes(Path.Combine(_store.OverlayDirectory, "rahmen.png"), [1]);
        vm.Draft.Layout.OverlayFileName = "rahmen.png";
        await vm.SelectPrinterAsync(vm.Printers[1]);
        vm.SelectedPaper = vm.PaperSizes[1];
        vm.SelectedOrientation = vm.Orientations[2];
        vm.Copies = 3;
        vm.PrintTimeoutSeconds = 500;
        vm.DateSubfolder = false;
        vm.SaveOriginals = false;
        vm.JpegQuality = 70;
        vm.FilePrefix = "Party";
        vm.Draft.Storage.PhotoFolder = Path.Combine(_dir, "fotos");
        vm.TriggerOnLeftClick = false;
        vm.TriggerOnRightClick = false;
        vm.BeginKeyCapture(KeyAction.Trigger);
        Assert.True(KeyChord.TryParse("F5", out var f5));
        Assert.True(vm.CaptureKey(f5));
        return vm;
    }

    [Fact]
    public async Task ResetToDefaults_AfterConfirm_RestoresEverythingExceptPin()
    {
        var vm = await CreateChangedViewModelAsync();
        Assert.NotEqual(Json(new AppSettings()), Json(vm.Draft));
        _ui.ConfirmResult = true;

        await vm.ResetToDefaultsAsync();

        Assert.Equal(1, _ui.ConfirmCalls);
        Assert.Contains("außer der PIN", _ui.LastConfirmMessage);
        Assert.Contains("Speichern", _ui.LastConfirmMessage);

        var expected = new AppSettings();
        expected.Security.PinHash = "hash:4711";
        expected.Security.PinSalt = "salt";
        expected.Security.PinIterations = 123_456;
        Assert.Equal(Json(expected), Json(vm.Draft));

        // Stichproben je Bereich.
        Assert.Null(vm.Draft.Camera.DeviceId);
        Assert.Null(vm.Draft.Camera.ResolutionWidth);
        Assert.True(vm.Draft.Camera.MirrorPreview);
        Assert.False(vm.Draft.Camera.MirrorPhoto);
        Assert.Equal(5, vm.Draft.Flow.CountdownSeconds);
        Assert.Equal(240, vm.Draft.Flow.ExpectedPrintSeconds);
        Assert.True(vm.Draft.Flow.PrintingEnabled);
        Assert.Equal(LayoutKind.SingleBorderless, vm.Draft.Layout.Kind);
        Assert.Empty(vm.Draft.Layout.GuestChoices);
        Assert.Null(vm.Draft.Layout.OverlayFileName);
        Assert.Null(vm.Draft.Layout.CaptionText);
        Assert.Null(vm.Draft.Printer.PrinterName);
        Assert.Null(vm.Draft.Printer.PaperSizeName);
        Assert.Equal(1, vm.Draft.Printer.Copies);
        Assert.Equal("Fotobox", vm.Draft.Storage.FilePrefix);
        Assert.Null(vm.Draft.Storage.PhotoFolder);
        Assert.Equal(DefaultTriggerKeys, vm.Draft.Input.TriggerKeys);
        Assert.True(vm.Draft.Input.TriggerOnLeftClick);
        Assert.True(vm.Draft.Input.TriggerOnRightClick);
    }

    [Fact]
    public async Task ResetToDefaults_KeepsPinAndVersion()
    {
        var vm = await CreateChangedViewModelAsync();
        vm.Draft.Version = 7;
        _ui.ConfirmResult = true;

        await vm.ResetToDefaultsAsync();

        Assert.Equal("hash:4711", vm.Draft.Security.PinHash);
        Assert.Equal("salt", vm.Draft.Security.PinSalt);
        Assert.Equal(123_456, vm.Draft.Security.PinIterations);
        Assert.Equal(7, vm.Draft.Version);
    }

    [Fact]
    public async Task ResetToDefaults_WhenDeclined_ChangesNothing()
    {
        var vm = await CreateChangedViewModelAsync();
        var before = Json(vm.Draft);
        var startCalls = _camera.StartCalls.Count;
        _ui.ConfirmResult = false;

        await vm.ResetToDefaultsCommand.ExecuteAsync(null);

        Assert.Equal(1, _ui.ConfirmCalls);
        Assert.Equal(before, Json(vm.Draft));
        Assert.Equal("Party", vm.FilePrefix);
        Assert.Equal(startCalls, _camera.StartCalls.Count);
    }

    [Fact]
    public async Task ResetToDefaults_ThenCancel_LeavesStoreUntouched()
    {
        var vm = await CreateChangedViewModelAsync();
        _ui.ConfirmResult = true;
        bool? closed = null;
        vm.CloseRequested += save => closed = save;

        await vm.ResetToDefaultsCommand.ExecuteAsync(null);
        vm.CancelCommand.Execute(null);

        Assert.False(closed);
        Assert.False(vm.SettingsSaved);
        Assert.Equal(0, _store.SaveCount);
        Assert.Equal(9, _store.Current.Flow.CountdownSeconds);
        Assert.Equal("hash:4711", _store.Current.Security.PinHash);
    }

    [Fact]
    public async Task ResetToDefaults_ThenSave_WritesDefaultsAndKeepsPin()
    {
        var vm = await CreateChangedViewModelAsync();
        _ui.ConfirmResult = true;

        await vm.ResetToDefaultsCommand.ExecuteAsync(null);
        vm.SaveCommand.Execute(null);

        Assert.True(vm.SettingsSaved);
        Assert.Equal(1, _store.SaveCount);
        var saved = _store.Current;
        Assert.Equal(5, saved.Flow.CountdownSeconds);
        Assert.Equal(240, saved.Flow.ExpectedPrintSeconds);
        Assert.Equal(LayoutKind.SingleBorderless, saved.Layout.Kind);
        Assert.Empty(saved.Layout.GuestChoices);
        Assert.Equal("Fotobox", saved.Storage.FilePrefix);
        Assert.Equal(DefaultTriggerKeys, saved.Input.TriggerKeys);
        Assert.Null(saved.Camera.DeviceId);
        Assert.Null(saved.Printer.PrinterName);
        Assert.Equal("hash:4711", saved.Security.PinHash);
        Assert.Equal("salt", saved.Security.PinSalt);
        Assert.Equal(123_456, saved.Security.PinIterations);
    }

    [Fact]
    public async Task ResetToDefaults_UpdatesBoundStateAndRaisesPropertyChanged()
    {
        var vm = await CreateChangedViewModelAsync();
        vm.CountdownSeconds = 0;
        vm.SaveCommand.Execute(null);   // Validierungsmeldung
        vm.BeginKeyCapture(KeyAction.Admin);
        Assert.True(vm.HasValidationMessage);
        Assert.True(vm.IsKeyCaptureActive);
        Assert.True(vm.IsStripLayout);
        Assert.True(vm.HasOverlay);
        Assert.True(vm.HasGuestOrder);
        var raised = new System.Collections.Concurrent.ConcurrentBag<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        _ui.ConfirmResult = true;

        await vm.ResetToDefaultsCommand.ExecuteAsync(null);

        Assert.Contains(string.Empty, raised);   // alle gebundenen Eigenschaften neu lesen
        Assert.Contains(nameof(AdminViewModel.GuestOrder), raised);
        Assert.Contains(nameof(AdminViewModel.OverlayFileName), raised);

        // Layout
        Assert.True(vm.IsSingleLayout);
        Assert.False(vm.IsStripLayout);
        Assert.False(vm.IsOverlayLayout);
        Assert.True(vm.IsStripDoubleCut);
        Assert.False(vm.IsStripSingle);
        Assert.True(vm.IsGuestSingle);
        Assert.False(vm.IsGuestSingleEnabled);
        Assert.False(vm.IsGuestOverlay);
        Assert.False(vm.ShowStripOptions);
        Assert.False(vm.ShowOverlayOptions);
        Assert.Empty(vm.GuestOrder);
        Assert.False(vm.HasGuestOrder);
        Assert.False(vm.HasOverlay);
        Assert.Null(vm.OverlayThumbnail);
        Assert.Equal(string.Empty, vm.OverlayInfo);
        Assert.Equal(string.Empty, vm.CaptionText);
        Assert.False(vm.ShowDate);
        Assert.Equal(LayoutSettings.DefaultCaptionFont, vm.SelectedCaptionFont.Source);
        Assert.Equal(3, vm.StripPhotoCount);

        // Ablauf, Speicher, Drucker
        Assert.Equal(5, vm.CountdownSeconds);
        Assert.Equal(240, vm.ExpectedPrintSeconds);
        Assert.True(vm.PrintingEnabled);
        Assert.Equal("Fotobox", vm.FilePrefix);
        Assert.Equal(92, vm.JpegQuality);
        Assert.Equal("92 %", vm.JpegQualityText);
        Assert.True(vm.DateSubfolder);
        Assert.True(vm.SaveOriginals);
        Assert.True(vm.IsDefaultPhotoFolder);
        Assert.Equal(1, vm.Copies);
        Assert.Equal(300, vm.PrintTimeoutSeconds);
        Assert.False(vm.HasPrintTimeoutHint);
        Assert.Equal(PageOrientation.Auto, vm.SelectedOrientation.Orientation);

        // Auswahl in den Listen: wie bei einer frisch geöffneten Ansicht mit Standard-Einstellungen.
        Assert.Equal("cam1", vm.SelectedDevice?.Id);
        Assert.Null(vm.SelectedResolution?.Resolution);
        Assert.Null(vm.SelectedPrinter?.Name);
        Assert.Null(vm.SelectedPaper?.Info);
        Assert.True(vm.MirrorPreview);
        Assert.False(vm.MirrorPhoto);

        // Tasten
        Assert.False(vm.IsKeyCaptureActive);
        Assert.Null(vm.CapturingEditor);
        Assert.Equal(DefaultTriggerKeys, vm.KeyEditors[0].Keys.Select(k => k.Chord.ToString()));
        Assert.Equal([InputSettings.DefaultAdminKey], vm.KeyEditors[1].Keys.Select(k => k.Chord.ToString()));
        Assert.Equal([InputSettings.DefaultCancelKey], vm.KeyEditors[2].Keys.Select(k => k.Chord.ToString()));
        Assert.True(vm.TriggerOnLeftClick);
        Assert.True(vm.TriggerOnRightClick);

        // Meldungen
        Assert.False(vm.HasValidationMessage);
        Assert.Equal(string.Empty, vm.ValidationMessage);
        Assert.False(vm.HasKeyMessage);
    }

    [Fact]
    public async Task ResetToDefaults_RestartsCameraWithDefaults_OnlyWhenCameraSettingsChanged()
    {
        var vm = await CreateChangedViewModelAsync();
        _ui.ConfirmResult = true;
        var before = _camera.StartCalls.Count;

        await vm.ResetToDefaultsCommand.ExecuteAsync(null);

        Assert.Equal(before + 1, _camera.StartCalls.Count);
        Assert.Null(_camera.StartCalls[^1].DeviceId);
        Assert.True(_camera.StartCalls[^1].MirrorPreview);
        Assert.True(vm.CameraTouched);

        // Ein zweites Zurücksetzen ändert an der Kamera nichts mehr → kein weiterer Neustart.
        await vm.ResetToDefaultsCommand.ExecuteAsync(null);
        Assert.Equal(before + 1, _camera.StartCalls.Count);
    }

    // ------------------------------------------------------------------ Stabilität (v1.1.2)

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Bedingung wurde nicht rechtzeitig erfüllt.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task LateResolutionsOfPreviousDevice_AreDiscarded()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        var gate = new TaskCompletionSource();
        _camera.ResolutionsFor = id => id == "cam2" ? [new CameraResolution(800, 600)] : [new CameraResolution(640, 480), new CameraResolution(1280, 720)];
        _camera.ResolutionsGate = id => id == "cam2" ? gate.Task : Task.CompletedTask;

        var slow = vm.SelectDeviceAsync(vm.Devices[1]);   // Antwort von cam2 verzögert sich
        await vm.SelectDeviceAsync(vm.Devices[0]);         // inzwischen zurück auf cam1
        gate.SetResult();
        await slow;

        Assert.Equal("cam1", vm.Draft.Camera.DeviceId);
        Assert.DoesNotContain(vm.Resolutions, r => r.Resolution == new CameraResolution(800, 600));
        Assert.Contains(vm.Resolutions, r => r.Resolution == new CameraResolution(1280, 720));
    }

    [Fact]
    public async Task LatePaperSizesOfPreviousPrinter_AreDiscarded()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        using var release = new ManualResetEventSlim();
        _print.PaperSizesFor = name =>
        {
            if (name == "Fotodrucker")
                release.Wait(TimeSpan.FromSeconds(20));
            return [new PaperSizeInfo(name ?? "x", "Papier von " + name, 100, 150)];
        };

        var slow = vm.SelectPrinterAsync(vm.Printers.First(p => p.Name == "Fotodrucker"));
        await vm.SelectPrinterAsync(vm.Printers.First(p => p.Name == "Bürodrucker"));
        release.Set();
        await slow;

        Assert.Equal("Bürodrucker", vm.Draft.Printer.PrinterName);
        Assert.DoesNotContain(vm.PaperSizes, p => p.Display.Contains("Fotodrucker"));
        Assert.Contains(vm.PaperSizes, p => p.Display.Contains("Bürodrucker"));
    }

    [Fact]
    public async Task Initialize_AppliesFontsWithoutWaitingForSlowPrinterList()
    {
        var vm = CreateViewModel();
        using var release = new ManualResetEventSlim();
        _print.PaperSizesFor = _ =>
        {
            release.Wait(TimeSpan.FromSeconds(20));
            return [];
        };

        var init = vm.InitializeAsync();
        try
        {
            await WaitUntilAsync(() => vm.CaptionFonts.Count > 1 && vm.Devices.Count == 2 && vm.Printers.Count > 1);
            Assert.False(init.IsCompleted); // hängt noch an den Papiergrößen
        }
        finally
        {
            release.Set();
        }

        await init;
        Assert.Equal(2, vm.Devices.Count);
    }

    [Fact]
    public void Validate_UnchangedFolder_IsNotProbed_ChangedFolderIs()
    {
        // Ein Pfad unterhalb einer Datei ist nie beschreibbar: Wäre er geprüft worden, gäbe es einen Fehler.
        var blocker = Path.Combine(_dir, "datei");
        File.WriteAllText(blocker, "x");
        _store.Current.Storage.PhotoFolder = Path.Combine(blocker, "unter");
        var vm = CreateViewModel();

        Assert.Empty(vm.Validate());

        vm.Draft.Storage.PhotoFolder = Path.Combine(blocker, "anders");
        Assert.Contains(vm.Validate(), e => e.StartsWith("In den Speicherordner kann nicht geschrieben werden"));

        vm.Draft.Storage.PhotoFolder = _store.Current.Storage.PhotoFolder;
        Assert.Empty(vm.Validate());
    }

    [Theory]
    [InlineData(@"C:fotos")]
    [InlineData(@"\fotos")]
    [InlineData(@"fotos\neu")]
    public void Validate_NotFullyQualifiedFolder_IsRejected(string folder)
    {
        var vm = CreateViewModel();
        vm.Draft.Storage.PhotoFolder = folder;

        Assert.Contains(vm.Validate(), e => e.StartsWith("Der Speicherordner ist kein gültiger Pfad"));
    }

    [Fact]
    public void Validate_PrefixAndCaption_UseSettingsLimits()
    {
        var vm = CreateViewModel();
        vm.FilePrefix = new string('p', SettingsLimits.FilePrefixMaxLength);
        vm.CaptionText = new string('c', SettingsLimits.CaptionMaxLength);
        Assert.Empty(vm.Validate());

        vm.FilePrefix = new string('p', SettingsLimits.FilePrefixMaxLength + 1);
        vm.CaptionText = new string('c', SettingsLimits.CaptionMaxLength + 1);
        var errors = vm.Validate();

        Assert.Contains(errors, e => e.Contains($"höchstens {SettingsLimits.FilePrefixMaxLength} Zeichen"));
        Assert.Contains(errors, e => e.Contains($"höchstens {SettingsLimits.CaptionMaxLength} Zeichen"));
    }

    [Fact]
    public async Task ResetToDefaults_WhenSomethingThrows_ShowsMessageInsteadOfThrowing()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        _ui.ConfirmException = new InvalidOperationException("kaputt");

        await vm.ResetToDefaultsAsync();   // darf nicht werfen

        Assert.True(vm.HasValidationMessage);
        Assert.Contains("kaputt", vm.ValidationMessage);
    }

    [Fact]
    public void OverlayPreview_IsDecodedSmall_ShowsRealPixelSize_AndDoesNotLockFile()
    {
        var path = Path.Combine(_store.OverlayDirectory, "gross.png");
        WritePng(path, 1200, 1800);
        _store.Current.Layout.OverlayFileName = "gross.png";

        var vm = CreateViewModel();

        Assert.Equal("gross.png (1200 × 1800 Pixel)", vm.OverlayInfo);
        var thumbnail = Assert.IsAssignableFrom<BitmapSource>(vm.OverlayThumbnail);
        Assert.True(thumbnail.PixelWidth <= 240);
        File.Delete(path);   // wirft, wenn das Handle offen geblieben wäre
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ImportOverlay_InvalidPng_IsRejected()
    {
        var vm = CreateViewModel();
        var bad = Path.Combine(_dir, "kaputt.png");
        File.WriteAllBytes(bad, [1, 2, 3]);

        Assert.False(vm.ImportOverlay(bad));
        Assert.True(vm.HasValidationMessage);
    }

    [Fact]
    public async Task QuickSwitchBack_ThenSave_StillReportsCameraRestartNeeded()
    {
        UseSavedCamera("cam1", null, null);
        var before = SnapshotCamera(_store.Current.Camera);
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        Assert.Null(vm.LastAppliedCamera);

        var startGate = new TaskCompletionSource();
        _camera.StartGate = s => s.DeviceId == "cam2" ? startGate.Task : Task.CompletedTask;
        var toB = vm.SelectDeviceAsync(vm.Devices[1]);   // Neustart mit cam2 läuft (hängt im Start)
        Assert.Equal("cam2", vm.LastAppliedCamera?.DeviceId);
        var back = vm.SelectDeviceAsync(vm.Devices[0]);  // zurück auf cam1: dieser Neustart wartet auf den laufenden
        vm.SaveCommand.Execute(null);                     // Speichern verwirft den wartenden Neustart
        startGate.SetResult();
        await toB;
        await back;

        Assert.Equal("cam2", _camera.StartCalls.Last().DeviceId);          // Kamera läuft mit der falschen Kamera
        Assert.Equal("cam1", _store.Current.Camera.DeviceId);
        Assert.False(Fotobox.Views.Admin.AdminLauncher.CameraSettingsDiffer(before, _store.Current.Camera)); // der alte Vergleich allein reicht nicht
        Assert.True(Fotobox.Views.Admin.AdminLauncher.CameraNeedsRestart(before, vm.LastAppliedCamera, _store.Current.Camera));
    }

    [Fact]
    public void CameraNeedsRestart_OnlyWhenSomethingDiffers()
    {
        static CameraSettings Cam(string id) => new() { DeviceId = id };

        Assert.False(Fotobox.Views.Admin.AdminLauncher.CameraNeedsRestart(Cam("a"), null, Cam("a")));
        Assert.False(Fotobox.Views.Admin.AdminLauncher.CameraNeedsRestart(Cam("a"), Cam("a"), Cam("a")));
        Assert.True(Fotobox.Views.Admin.AdminLauncher.CameraNeedsRestart(Cam("a"), null, Cam("b")));
        Assert.True(Fotobox.Views.Admin.AdminLauncher.CameraNeedsRestart(Cam("a"), Cam("b"), Cam("a")));
    }

    // ------------------------------------------------------------------ Fakes

    internal sealed class FakeStore : ISettingsStore
    {
        public FakeStore(string dir)
        {
            ConfigDirectory = dir;
            OverlayDirectory = Path.Combine(dir, "overlays");
        }

        public string ConfigDirectory { get; }
        public string OverlayDirectory { get; }
        public AppSettings Current { get; private set; } = new();
        public int SaveCount { get; private set; }
        public event EventHandler? SettingsChanged;

        /// <summary>Gesetzt: <see cref="Save"/> wirft diese Ausnahme (z. B. gesperrte Datei).</summary>
        public Exception? ThrowOnSave { get; set; }

        public void Save(AppSettings settings)
        {
            if (ThrowOnSave is not null)
                throw ThrowOnSave;
            SaveCount++;
            Current = settings.Clone();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal sealed class FakePinService(FakeStore store) : IPinService
    {
        public string? LastPin { get; private set; }
        public bool HasPin => !string.IsNullOrEmpty(store.Current.Security.PinHash);

        public void SetPin(string pin)
        {
            LastPin = pin;
            // Wie der echte Dienst: schreibt den Hash über den Store.
            var copy = store.Current.Clone();
            copy.Security.PinHash = "hash:" + pin;
            store.Save(copy);
        }

        public bool Verify(string pin) => store.Current.Security.PinHash == "hash:" + pin;
    }

    internal sealed class FakeCamera : ICameraService
    {
        public List<CameraSettings> StartCalls { get; } = [];
        public CameraStatus Status { get; private set; } = CameraStatus.Running;
        public string? StatusMessage => null;
        public CameraResolution? ActiveResolution => new(1280, 720);

#pragma warning disable CS0067
        public event EventHandler? StatusChanged;
        public event EventHandler<CameraFrame>? FrameArrived;
#pragma warning restore CS0067

        public Task<IReadOnlyList<CameraDevice>> GetDevicesAsync() =>
            Task.FromResult<IReadOnlyList<CameraDevice>>([new CameraDevice("cam1", "Kamera 1"), new CameraDevice("cam2", "Kamera 2")]);

        /// <summary>Gesetzt: <see cref="GetResolutionsAsync"/> wirft (z. B. Kamera gerade belegt).</summary>
        public Exception? ResolutionsError { get; set; }

        /// <summary>Gesetzt: <see cref="GetResolutionsAsync"/> wartet vor der Antwort auf diese Aufgabe (je Gerät).</summary>
        public Func<string, Task>? ResolutionsGate { get; set; }

        /// <summary>Gesetzt: liefert die Auflösungen je Gerät statt der Standardliste.</summary>
        public Func<string, IReadOnlyList<CameraResolution>>? ResolutionsFor { get; set; }

        public async Task<IReadOnlyList<CameraResolution>> GetResolutionsAsync(string deviceId)
        {
            if (ResolutionsError is not null)
                throw ResolutionsError;
            if (ResolutionsGate is not null)
                await ResolutionsGate(deviceId);
            return ResolutionsFor?.Invoke(deviceId)
                   ?? [new CameraResolution(640, 480), new CameraResolution(1280, 720), new CameraResolution(1920, 1080)];
        }

        /// <summary>Gesetzt: <see cref="StartAsync"/> wartet nach dem Aufzeichnen des Aufrufs auf diese Aufgabe (je Einstellungen).</summary>
        public Func<CameraSettings, Task>? StartGate { get; set; }

        public async Task StartAsync(CameraSettings settings, CancellationToken ct = default)
        {
            StartCalls.Add(new CameraSettings
            {
                DeviceId = settings.DeviceId,
                ResolutionWidth = settings.ResolutionWidth,
                ResolutionHeight = settings.ResolutionHeight,
                MirrorPreview = settings.MirrorPreview,
                MirrorPhoto = settings.MirrorPhoto,
            });
            Status = CameraStatus.Running;
            if (StartGate is not null)
                await StartGate(settings);
        }

        public Task StopAsync() => Task.CompletedTask;

        public Task<BitmapSource> CapturePhotoAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class FakePrint : IPrintService
    {
        public string? LastPaperQuery { get; private set; }
        public BitmapSource? PrintedPage { get; private set; }
        public PrinterSettings? PrintedSettings { get; private set; }
        public PrintResult Result { get; set; } = new(PrintOutcome.Completed);
        /// <summary>Gesetzt: PrintAsync hängt, bis das Ergebnis gesetzt wird (ignoriert den Abbruch-Token, wie ein blockierendes Write).</summary>
        public TaskCompletionSource<PrintResult>? Gate { get; set; }
        public CancellationToken LastToken { get; private set; }

        public IReadOnlyList<PrinterInfo> GetPrinters() =>
            [new PrinterInfo("Fotodrucker", true), new PrinterInfo("Bürodrucker", false)];

        /// <summary>Gesetzt: liefert die Papiergrößen je Drucker (darf blockieren) statt der Standardliste.</summary>
        public Func<string?, IReadOnlyList<PaperSizeInfo>>? PaperSizesFor { get; set; }

        public IReadOnlyList<PaperSizeInfo> GetPaperSizes(string? printerName)
        {
            LastPaperQuery = printerName;
            if (PaperSizesFor is not null)
                return PaperSizesFor(printerName);
            return [new PaperSizeInfo("4x6", "10 x 15 cm randlos", 101.6, 152.4), new PaperSizeInfo("A4", "A4", 210, 297)];
        }

        private int _pageSpecCalls;
        public int PageSpecCalls => Volatile.Read(ref _pageSpecCalls);
        /// <summary>Gesetzt: liefert die Seitengröße (oder hängt/wirft) statt <see cref="PageSpec.Default"/>.</summary>
        public Func<PrinterSettings, PageSpec>? PageSpecFor { get; set; }

        public PageSpec GetPageSpec(PrinterSettings printer, LayoutKind layout)
        {
            Interlocked.Increment(ref _pageSpecCalls);
            return PageSpecFor?.Invoke(printer) ?? PageSpec.Default;
        }

        public Task<PrintResult> PrintAsync(BitmapSource page, PrinterSettings printer, IProgress<string>? status = null, CancellationToken ct = default)
        {
            PrintedPage = page;
            PrintedSettings = printer;
            LastToken = ct;
            return Gate?.Task ?? Task.FromResult(Result);
        }
    }

    internal sealed class FakeUi : IAdminUi
    {
        public bool ConfirmResult { get; set; }
        public int ConfirmCalls { get; private set; }
        public string? LastConfirmTitle { get; private set; }
        public string? LastConfirmMessage { get; private set; }
        public string? FolderToPick { get; set; }
        public string? OpenedFolder { get; private set; }
        public BitmapSource? LastTestPage { get; private set; }

        public string? PickOverlayFile() => null;
        public string? PickFolder(string? initialFolder) => FolderToPick;
        /// <summary>Gesetzt: <see cref="Confirm"/> wirft diese Ausnahme.</summary>
        public Exception? ConfirmException { get; set; }
        public bool Confirm(string title, string message)
        {
            if (ConfirmException is not null)
                throw ConfirmException;
            ConfirmCalls++;
            LastConfirmTitle = title;
            LastConfirmMessage = message;
            return ConfirmResult;
        }
        public void OpenFolder(string path) => OpenedFolder = path;

        public BitmapSource RenderTestPage(PageSpec page)
        {
            LastTestPage = BitmapSource.Create(page.PixelWidth, 4, 96, 96, PixelFormats.Gray8, null, new byte[page.PixelWidth * 4], page.PixelWidth);
            // Eingefroren: Die Fortsetzung nach dem Hintergrund-await läuft im Test nicht auf dem erzeugenden Thread.
            LastTestPage.Freeze();
            return LastTestPage;
        }
    }
}
