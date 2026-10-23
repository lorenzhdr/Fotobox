using System.IO;
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

        time.Advance(TimeSpan.FromSeconds(vm.Draft.Printer.TimeoutSeconds + 30));
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
        vm.Dispose();
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(_print.LastToken.IsCancellationRequested);
        Assert.Equal("Testdruck abgebrochen.", vm.TestPrintStatus);
        Assert.False(vm.IsTestPrinting);
    }

    [Fact]
    public void OverlayHint_NamesRecommendedPixelSize()
    {
        var vm = CreateViewModel();

        Assert.Contains("1200 × 1800", vm.OverlayHint);
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

    private static void WritePng(string path, int width, int height)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            new byte[width * height * 4], width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
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

        public void Save(AppSettings settings)
        {
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

        public Task<IReadOnlyList<CameraResolution>> GetResolutionsAsync(string deviceId) =>
            Task.FromResult<IReadOnlyList<CameraResolution>>(
                [new CameraResolution(640, 480), new CameraResolution(1280, 720), new CameraResolution(1920, 1080)]);

        public Task StartAsync(CameraSettings settings, CancellationToken ct = default)
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
            return Task.CompletedTask;
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

        public IReadOnlyList<PaperSizeInfo> GetPaperSizes(string? printerName)
        {
            LastPaperQuery = printerName;
            return [new PaperSizeInfo("4x6", "10 x 15 cm randlos", 101.6, 152.4), new PaperSizeInfo("A4", "A4", 210, 297)];
        }

        public PageSpec GetPageSpec(PrinterSettings printer, LayoutKind layout) => PageSpec.Default;

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
        public string? FolderToPick { get; set; }
        public string? OpenedFolder { get; private set; }
        public BitmapSource? LastTestPage { get; private set; }

        public string? PickOverlayFile() => null;
        public string? PickFolder(string? initialFolder) => FolderToPick;
        public bool Confirm(string title, string message) => ConfirmResult;
        public void OpenFolder(string path) => OpenedFolder = path;

        public BitmapSource RenderTestPage(PageSpec page)
        {
            LastTestPage = BitmapSource.Create(page.PixelWidth, 4, 96, 96, PixelFormats.Gray8, null, new byte[page.PixelWidth * 4], page.PixelWidth);
            return LastTestPage;
        }
    }
}
