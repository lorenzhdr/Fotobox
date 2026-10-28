using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.ViewModels.Admin;
using Fotobox.Views.Admin;
using Fotobox.Views.Booth;
using Fotobox.Views.Controls;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Live-Vorschau, Test-Knopf und Statuszeile im Admin-Tab „Fun“.</summary>
[Collection(WpfCollection.Name)]
public sealed class AdminFunPreviewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-funpreview-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AdminViewModelTests.FakeStore _store;
    private readonly AdminViewModelTests.FakePinService _pin;
    private readonly AdminViewModelTests.FakePrint _print = new();
    private readonly AdminViewModelTests.FakeUi _ui = new();
    private readonly FakeDetectionService _detection = new();
    private readonly FakeSoundPlayer _sound = new();
    private readonly TrackingTimeProvider _time = new();
    private readonly List<AdminViewModel> _created = [];

    public AdminFunPreviewTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "overlays"));
        _store = new AdminViewModelTests.FakeStore(_dir);
        _pin = new AdminViewModelTests.FakePinService(_store);
    }

    public void Dispose()
    {
        foreach (var vm in _created)
            vm.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // xunit setzt je Test einen eigenen Kontext; Timer- und Kamera-Ereignisse sollen aber synchron im Test laufen.
    private static void NoContext() => SynchronizationContext.SetSynchronizationContext(null);

    private AdminViewModel CreateViewModel(ICameraService? camera = null, bool withServices = true)
    {
        var vm = new AdminViewModel(_store, _pin, camera ?? new AdminViewModelTests.FakeCamera(), _print, _ui, _time,
            new AdminViewModelTests.FakeDisplays(AdminViewModelTests.AdminMonitors.Primary),
            withServices ? _detection : null, withServices ? _sound : null);
        // Die Vorschau der Zielerfassung läuft nur auf der Unterseite „Personenerkennung“.
        vm.FunPage = FunPage.Persons;
        _created.Add(vm);
        return vm;
    }

    private static DetectionStatus Active(DetectionKind kind, double rate = 4.8, string? fallback = null) =>
        new(DetectionMethod.Body, kind, fallback, null, rate);

    // ------------------------------------------------------------------ Aktivierung

    [Fact]
    public void NoRequest_WhileFunTabIsNotSelected()
    {
        NoContext();
        var vm = CreateViewModel();

        vm.FunColor = TargetColor.Cyan;
        vm.MirrorPreview = true;

        Assert.False(vm.IsFunTabSelected);
        Assert.Empty(_detection.Requests);
        Assert.False(vm.FunPreview.IsActive);
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public void SelectingFunTab_OpensOneRequest_WithDraftSettingsAndMirror()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.FunColor = TargetColor.Cyan;
        vm.FunMaxTargets = 3;
        vm.MirrorPreview = true;

        vm.IsFunTabSelected = true;

        Assert.Single(_detection.Requests);
        Assert.True(vm.FunPreview.IsActive);
        Assert.Equal(TargetColor.Cyan, _detection.Last.Settings.Color);
        Assert.Equal(3, _detection.Last.Settings.MaxTargets);
        Assert.True(vm.FunPreview.Mirror);
        Assert.Equal(TargetColor.Cyan, vm.FunPreview.Color);
        Assert.Equal(1, _time.ActiveTimers);
        // Die Vorschau arbeitet mit einer Kopie, nicht mit dem Entwurf selbst.
        Assert.NotSame(vm.Draft.Fun, _detection.Last.Settings);
    }

    [Fact]
    public void Preview_RunsEvenWhileFunIsSwitchedOff()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.FunEnabled = false;
        Assert.False(vm.FunEnabled);

        vm.IsFunTabSelected = true;

        Assert.Single(_detection.Requests);
        Assert.True(vm.FunPreview.IsActive);
    }

    [Fact]
    public void LeavingFunTab_ReleasesRequestAndTimer_AndSelectingAgainOpensANewOne()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.IsFunTabSelected = true;
        var first = _detection.Last;

        vm.IsFunTabSelected = false;

        Assert.True(first.IsDisposed);
        Assert.False(vm.FunPreview.IsActive);
        Assert.Equal(0, _detection.OpenRequests);
        Assert.Equal(0, _time.ActiveTimers);

        vm.IsFunTabSelected = true;

        Assert.Equal(2, _detection.Requests.Count);
        Assert.Equal(1, _detection.OpenRequests);
        Assert.Equal(1, _time.ActiveTimers);
    }

    [Fact]
    public void Dispose_ReleasesTheRequest_AndLaterChangesDoNotRestartIt()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.IsFunTabSelected = true;

        vm.Dispose();
        vm.FunColor = TargetColor.Green;
        vm.IsFunTabSelected = true;

        Assert.Single(_detection.Requests);
        Assert.Equal(0, _detection.OpenRequests);
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public void DraftChange_WhileActive_UpdatesTheRunningRequest()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.IsFunTabSelected = true;
        var request = _detection.Last;

        vm.FunSensitivityPercent = 70;

        Assert.Single(_detection.Requests);
        Assert.Single(request.Updates);
        Assert.Equal(70, request.Settings.SensitivityPercent);

        vm.FunBoxStyle = TargetBoxStyle.CornersCrosshair;

        Assert.Equal(2, request.Updates.Count);
        Assert.Equal(TargetBoxStyle.CornersCrosshair, vm.FunPreview.BoxStyle);
    }

    [Fact]
    public void MirrorPreviewChange_WhileActive_UpdatesTheOverlayMirror_WithoutNewRequest()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.MirrorPreview = false;
        vm.IsFunTabSelected = true;
        Assert.False(vm.FunPreview.Mirror);

        vm.MirrorPreview = true;

        Assert.True(vm.FunPreview.Mirror);
        Assert.Single(_detection.Requests);
        Assert.Empty(_detection.Last.Updates);
    }

    [Fact]
    public void HintStyleChange_WhileActive_ReachesTheOverlay()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.IsFunTabSelected = true;

        vm.SelectedFunHintStyle = vm.FunHintStyles.Single(o => o.Style == HintStyle.Neon);

        Assert.Equal(HintStyle.Neon, vm.FunPreview.TextHintStyle);
    }

    [Fact]
    public async Task ResetToDefaults_WhileActive_AppliesTheDefaultsToThePreview()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.FunColor = TargetColor.Cyan;
        vm.IsFunTabSelected = true;
        _ui.ConfirmResult = true;

        await vm.ResetToDefaultsCommand.ExecuteAsync(null);

        Assert.Equal(new FunSettings().Color, vm.FunPreview.Color);
        Assert.Equal(new FunSettings().Color, _detection.Last.Settings.Color);
        Assert.Single(_detection.Requests);
    }

    [Fact]
    public async Task CameraRestart_WhileActive_KeepsThePreviewRequest()
    {
        NoContext();
        var camera = new AdminViewModelTests.FakeCamera();
        var vm = CreateViewModel(camera);
        await vm.InitializeAsync();
        vm.IsFunTabSelected = true;

        await vm.RestartCameraAsync();

        Assert.NotEmpty(camera.StartCalls);
        Assert.Single(_detection.Requests);
        Assert.True(vm.FunPreview.IsActive);
        Assert.False(_detection.Last.IsDisposed);
    }

    [Fact]
    public void WithoutDetectionService_PreviewIsInert_ButDoesNotThrow()
    {
        NoContext();
        var vm = CreateViewModel(withServices: false);

        vm.IsFunTabSelected = true;
        vm.FunColor = TargetColor.Green;
        vm.TestFunPreviewCommand.Execute(null);
        vm.IsFunTabSelected = false;

        Assert.Empty(_detection.Requests);
    }

    // ------------------------------------------------------------------ Test-Knopf

    [Fact]
    public void TestButton_SimulatesATarget_WithoutCameraAndWithoutRequest()
    {
        NoContext();
        var camera = new StoppedCamera();
        var vm = CreateViewModel(camera);
        vm.FunColor = TargetColor.Yellow;

        vm.TestFunPreviewCommand.Execute(null);

        Assert.Empty(_detection.Requests);
        Assert.True(vm.FunPreview.IsShown);
        Assert.Equal(FunPhase.LockingOn, vm.FunPreview.Snapshot.Phase);
        Assert.Single(vm.FunPreview.Snapshot.Targets);
        Assert.Equal(TargetColor.Yellow, vm.FunPreview.Color);
        Assert.True(vm.FunPreviewNoCamera);
    }

    [Fact]
    public void TestButton_ShowsTheTextAfterLockOn_AndEndsTheSimulation()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.FunTextSeconds = 2;

        vm.TestFunPreviewCommand.Execute(null);
        for (var i = 0; i < 15; i++)
            _time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.True(vm.FunPreview.TextVisible);
        Assert.False(string.IsNullOrWhiteSpace(vm.FunPreview.Text));

        for (var i = 0; i < 40; i++)
            _time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.False(vm.FunPreview.IsShown);
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public void TestButton_UsesTheCurrentDraftValues()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.MirrorPreview = true;
        vm.FunBoxStyle = TargetBoxStyle.CornersCrosshair;
        vm.FunAnimation = LockOnAnimation.ScanPulse;

        vm.TestFunPreviewCommand.Execute(null);

        Assert.True(vm.FunPreview.Mirror);
        Assert.Equal(TargetBoxStyle.CornersCrosshair, vm.FunPreview.BoxStyle);
        Assert.Equal(LockOnAnimation.ScanPulse, vm.FunPreview.Animation);
    }

    [Fact]
    public void StatusText_IsEmptyWithoutAnActivePreview()
    {
        NoContext();
        var vm = CreateViewModel();

        Assert.Equal(string.Empty, vm.FunPreviewStatusText);
        Assert.False(vm.HasFunPreviewWarning);
    }

    // ------------------------------------------------------------------ Statuszeile im Ansichtsmodell

    [Fact]
    public void StatusText_FollowsTheDetectionStatus()
    {
        NoContext();
        var vm = CreateViewModel();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.IsFunTabSelected = true;
        Assert.Equal(FunPreviewStatusFormatter.Loading, vm.FunPreviewStatusText);

        _detection.Last.RaiseStatus(Active(DetectionKind.Body, 4.8));
        Assert.Equal("Keine Person erkannt · 4,8 Analysen/s · Methode: Ganzkörper (KI)", vm.FunPreviewStatusText);
        Assert.False(vm.HasFunPreviewWarning);

        changed.Clear();
        _detection.Last.RaiseUpdated([], 1280, 720); // gleicher Status: keine Änderung nötig
        _detection.Last.RaiseStatus(Active(DetectionKind.Face, 2.0, "Modell nicht gefunden."));
        Assert.Contains(nameof(AdminViewModel.FunPreviewStatusText), changed);
        Assert.Equal("Keine Person erkannt · 2,0 Analysen/s · Methode: Gesicht (Windows)", vm.FunPreviewStatusText);
        Assert.True(vm.HasFunPreviewWarning);
        Assert.Equal("Modell nicht gefunden.", vm.FunPreviewWarningText);
    }

    [Fact]
    public void StatusText_RefreshesWhenAnAnalysisChangesTheRate()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.IsFunTabSelected = true;
        _detection.Last.RaiseStatus(Active(DetectionKind.Body, 1.0));
        Assert.Contains("1,0 Analysen/s", vm.FunPreviewStatusText);

        // Die Rate kommt mit der Analyse (nicht als eigenes Statusereignis).
        _detection.Last.RaiseUpdated([], 1280, 720, Active(DetectionKind.Body, 3.5));

        Assert.Contains("3,5 Analysen/s", vm.FunPreviewStatusText);
    }

    [Fact]
    public void StatusText_ShowsUnavailableReason()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.IsFunTabSelected = true;

        _detection.Last.RaiseStatus(new DetectionStatus(DetectionMethod.Body, null, null, "Keine Erkennung möglich.", 0));

        Assert.Equal("Keine Erkennung möglich.", vm.FunPreviewStatusText);
        Assert.False(vm.HasFunPreviewWarning);
    }

    // ------------------------------------------------------------------ Formatierung

    [Theory]
    [InlineData(0, "Keine Person erkannt")]
    [InlineData(-1, "Keine Person erkannt")]
    [InlineData(1, "1 Person erkannt")]
    [InlineData(2, "2 Personen erkannt")]
    [InlineData(12, "12 Personen erkannt")]
    public void Formatter_PersonText(int count, string expected) =>
        Assert.Equal(expected, FunPreviewStatusFormatter.PersonText(count));

    [Theory]
    [InlineData(0, 0.0, "Keine Person erkannt · 0,0 Analysen/s · Methode: Ganzkörper (KI)")]
    [InlineData(1, 4.84, "1 Person erkannt · 4,8 Analysen/s · Methode: Ganzkörper (KI)")]
    [InlineData(3, 10.0, "3 Personen erkannt · 10,0 Analysen/s · Methode: Ganzkörper (KI)")]
    public void Formatter_ActiveBody(int count, double rate, string expected)
    {
        var lines = FunPreviewStatusFormatter.Format(Active(DetectionKind.Body, rate), count);

        Assert.Equal(expected, lines.Text);
        Assert.Null(lines.Warning);
    }

    [Fact]
    public void Formatter_ActiveFace_WithoutFallback_HasNoWarning()
    {
        var lines = FunPreviewStatusFormatter.Format(
            new DetectionStatus(DetectionMethod.Face, DetectionKind.Face, null, null, 5), 2);

        Assert.Equal("2 Personen erkannt · 5,0 Analysen/s · Methode: Gesicht (Windows)", lines.Text);
        Assert.Null(lines.Warning);
    }

    [Fact]
    public void Formatter_Fallback_AddsTheReasonAsWarning()
    {
        var lines = FunPreviewStatusFormatter.Format(Active(DetectionKind.Face, 3.2, "Das KI-Modell konnte nicht geladen werden."), 1);

        Assert.Equal("1 Person erkannt · 3,2 Analysen/s · Methode: Gesicht (Windows)", lines.Text);
        Assert.Equal("Das KI-Modell konnte nicht geladen werden.", lines.Warning);
    }

    [Fact]
    public void Formatter_Unavailable_ShowsTheReason()
    {
        var lines = FunPreviewStatusFormatter.Format(
            new DetectionStatus(DetectionMethod.Face, null, null, "Gesichtserkennung ist nicht verfügbar.", 0), 0);

        Assert.Equal("Gesichtserkennung ist nicht verfügbar.", lines.Text);
        Assert.Null(lines.Warning);
    }

    [Fact]
    public void Formatter_Starting_ShowsLoading()
    {
        var lines = FunPreviewStatusFormatter.Format(DetectionStatus.Starting(DetectionMethod.Body), 0);

        Assert.Equal("Erkennung wird geladen …", lines.Text);
        Assert.Null(lines.Warning);
    }

    [Fact]
    public void Formatter_WithoutStatus_ReportsNotAvailable()
    {
        Assert.Equal(FunPreviewStatusFormatter.NotAvailable, FunPreviewStatusFormatter.Format(null, 0).Text);
    }

    // ------------------------------------------------------------------ Ansicht

    private void WithWindow(Action<AdminWindow, AdminViewModel> body, ICameraService? camera = null)
    {
        var vm = CreateViewModel(camera);
        var window = new AdminWindow(vm) { ShowInTaskbar = false };
        body(window, vm);
    }

    private static TabControl FindTabControl(DependencyObject parent) =>
        FindTabControlOrNull(parent) ?? throw new InvalidOperationException("Kein TabControl gefunden.");

    private static TabControl? FindTabControlOrNull(DependencyObject parent)
    {
        if (parent is TabControl tabs)
            return tabs;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (FindTabControlOrNull(child) is { } found)
                return found;
        }
        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                yield return match;
            foreach (var nested in FindDescendants<T>(child))
                yield return nested;
        }
    }

    private static FrameworkElement Select(AdminWindow window, string header)
    {
        var tabs = FindTabControl(window);
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header as string == header);
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1100, 750));
        root.Arrange(new Rect(0, 0, 1100, 750));
        root.UpdateLayout();
        return root;
    }

    [StaFact]
    public void AdminWindow_FunTab_ShowsPreviewElements_OutsideTheDisabledPanel()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            var root = Select(window, "Fun");
            vm.FunEnabled = false;

            var host = Assert.IsType<Border>(window.FindName("FunPreviewHost"));
            var panel = (StackPanel)window.FindName("FunSettingsPanel");
            Assert.False(vm.FunEnabled);
            Assert.False(panel.IsEnabled);
            Assert.True(host.IsEnabled);
            Assert.False(IsDescendant(host, panel));
            Assert.Single(FindDescendants<CameraPreview>(host));
            Assert.Single(FindDescendants<TargetOverlay>(host));
            Assert.Single(FindDescendants<FunText>(host));
            Assert.Equal(OverlayFit.Uniform, TargetOverlay_Fit(host));
            Assert.True(host.ActualWidth > 0);

            var button = Assert.IsType<Button>(window.FindName("FunTestButton"));
            Assert.Equal("Animation testen", button.Content);
            Assert.True(button.IsEnabled);
            Assert.Same(vm.TestFunPreviewCommand, button.Command);
            Assert.Contains(FindDescendants<TextBlock>(root),
                t => t.Text == "Vorschau mit den aktuellen (noch nicht gespeicherten) Einstellungen.");
            Assert.NotNull(window.FindName("FunPreviewStatus"));
            Assert.NotNull(window.FindName("FunPreviewWarning"));
        });
    }

    private static OverlayFit TargetOverlay_Fit(DependencyObject host) => FindDescendants<TargetOverlay>(host).Single().Fit;

    private static bool IsDescendant(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }
        return false;
    }

    [StaFact]
    public void AdminWindow_TabSelection_ControlsTheRequest_AndClosingReleasesIt()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            Assert.Empty(_detection.Requests);

            Select(window, "Kamera");
            Assert.Empty(_detection.Requests);

            Select(window, "Fun");
            Assert.True(vm.IsFunTabSelected);
            Assert.Equal(1, _detection.OpenRequests);

            Select(window, "Ablauf");
            Assert.False(vm.IsFunTabSelected);
            Assert.Equal(0, _detection.OpenRequests);

            Select(window, "Fun");
            Assert.Equal(1, _detection.OpenRequests);

            window.Close();

            Assert.Equal(0, _detection.OpenRequests);
            Assert.Equal(0, _time.ActiveTimers);
        });
    }

    [StaFact]
    public void AdminWindow_Preview_ShowsTheNoCameraHint_OnlyWithoutALiveImage()
    {
        NoContext();
        var camera = new StoppedCamera();
        WithWindow((window, vm) =>
        {
            Select(window, "Fun");
            var hint = (TextBlock)window.FindName("FunPreviewNoCameraText");

            Assert.Equal("Keine Kamera – „Animation testen“ funktioniert trotzdem.", hint.Text);
            Assert.Equal(Visibility.Visible, hint.Visibility);

            camera.SetStatus(CameraStatus.Running);

            Assert.Equal(Visibility.Collapsed, hint.Visibility);
        }, camera);
    }

    [StaFact]
    public void AdminWindow_TestButton_ShowsTheSimulatedTargetInTheOverlay()
    {
        NoContext();
        var camera = new StoppedCamera();
        WithWindow((window, vm) =>
        {
            Select(window, "Fun");
            var overlay = (TargetOverlay)window.FindName("FunPreviewOverlay");
            Assert.Equal(Visibility.Visible, overlay.Visibility); // aktiv, daher sichtbar
            Assert.Empty(overlay.Snapshot.Targets);

            vm.TestFunPreviewCommand.Execute(null);

            Assert.Single(overlay.Snapshot.Targets);
            Assert.Equal(FunPhase.LockingOn, overlay.Snapshot.Phase);
        }, camera);
    }

    [StaFact]
    public void AdminWindow_StatusLine_ShowsTheMainTextAndWarning()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            Select(window, "Fun");
            var status = (TextBlock)window.FindName("FunPreviewStatus");
            var warning = (TextBlock)window.FindName("FunPreviewWarning");
            Assert.Equal(FunPreviewStatusFormatter.Loading, status.Text);
            Assert.Equal(Visibility.Collapsed, warning.Visibility);

            _detection.Last.RaiseStatus(Active(DetectionKind.Face, 2.5, "Ausweichen auf Gesicht."));

            Assert.Contains("Methode: Gesicht (Windows)", status.Text);
            Assert.Equal("Ausweichen auf Gesicht.", warning.Text);
            Assert.Equal(Visibility.Visible, warning.Visibility);
        });
    }

    /// <summary>Kamera, deren Zustand der Test setzt (ohne Livebild).</summary>
    private sealed class StoppedCamera : ICameraService
    {
        public CameraStatus Status { get; private set; } = CameraStatus.NoDevice;
        public string? StatusMessage => null;
        public CameraResolution? ActiveResolution => null;
        public event EventHandler? StatusChanged;
#pragma warning disable CS0067
        public event EventHandler<CameraFrame>? FrameArrived;
#pragma warning restore CS0067

        public void SetStatus(CameraStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task<IReadOnlyList<CameraDevice>> GetDevicesAsync() => Task.FromResult<IReadOnlyList<CameraDevice>>([]);
        public Task<IReadOnlyList<CameraResolution>> GetResolutionsAsync(string deviceId) =>
            Task.FromResult<IReadOnlyList<CameraResolution>>([]);
        public Task StartAsync(CameraSettings settings, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task<BitmapSource> CapturePhotoAsync(CancellationToken ct = default) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
