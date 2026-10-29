using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Fotobox.Core;
using Fotobox.Core.Peace;
using Fotobox.ViewModels.Admin;
using Fotobox.ViewModels.Booth;
using Fotobox.Views.Admin;
using Fotobox.Views.Booth.Peace;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Admin-Tab „Fun“, Seite „Mittelfinger“: Entwurf, gemeinsame Vorschau mit der Peaceerkennung, Test-Bart, Ansicht.</summary>
[Collection(WpfCollection.Name)]
public sealed class AdminMiddleFingerTabTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-mf-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AdminViewModelTests.FakeStore _store;
    private readonly AdminViewModelTests.FakePinService _pin;
    private readonly AdminViewModelTests.FakeUi _ui = new();
    private readonly FakeDetectionService _detection = new();
    private readonly FakeGestureService _gestures = new();
    private readonly FakeSoundPlayer _sound = new();
    private readonly TrackingTimeProvider _time = new();
    private readonly List<AdminViewModel> _created = [];

    private static readonly HandLandmarks Middle = PeaceHandFixtures.Hand(HandPose.MiddleOnly);
    private static readonly HandLandmarks Peace = PeaceHandFixtures.Hand(HandPose.Peace);
    private static readonly FaceLandmarks Face = PeaceHandFixtures.Face(0.5, 0.3, 0.12);

    public AdminMiddleFingerTabTests()
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

    private static void NoContext() => SynchronizationContext.SetSynchronizationContext(null);

    private AdminViewModel CreateViewModel(bool withServices = true)
    {
        var vm = new AdminViewModel(_store, _pin, new AdminViewModelTests.FakeCamera(), new AdminViewModelTests.FakePrint(), _ui, _time,
            new AdminViewModelTests.FakeDisplays(AdminViewModelTests.AdminMonitors.Primary),
            withServices ? _detection : null, withServices ? _sound : null, withServices ? _gestures : null);
        _created.Add(vm);
        return vm;
    }

    private AdminViewModel OpenMiddleFingerPage()
    {
        var vm = CreateViewModel();
        vm.FunPage = FunPage.MiddleFinger;
        vm.IsFunTabSelected = true;
        return vm;
    }

    // Eine Analyse alle 100 ms, die Zeit läuft mit (Hände immer dabei, Gesichter fest).
    private void Run(double seconds, IReadOnlyList<HandLandmarks> hands)
    {
        var steps = (int)Math.Round(seconds * 10);
        for (var i = 0; i < steps; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            if (_gestures.Requests.Count > 0 && !_gestures.Last.IsDisposed)
                _gestures.Last.RaiseUpdated([Face], hands);
        }
    }

    // ------------------------------------------------------------------ Entwurf

    [Fact]
    public void Defaults_AreReadFromSettings()
    {
        NoContext();
        var vm = CreateViewModel();

        Assert.True(vm.MiddleFingerEnabled);
        Assert.True(vm.MiddleFingerActionCancelPeace);
        Assert.True(vm.MiddleFingerActionCancelKisscam);
        Assert.True(vm.MiddleFingerActionCancelPeaceEnabled);   // zwei Aktionen: keine ist die letzte
        Assert.True(vm.MiddleFingerActionCancelKisscamEnabled);
        Assert.False(vm.MiddleFingerHasNoAction);
        Assert.Equal(500, vm.MiddleFingerHoldMilliseconds);
        Assert.Equal(50, vm.MiddleFingerSensitivityPercent);
        Assert.Equal("An", vm.MiddleFingerStatusText);
        Assert.False(vm.HasMiddleFingerPeaceDisabledWarning);
        Assert.Equal(string.Empty, vm.MiddleFingerPeaceDisabledWarning);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void Setters_WriteIntoTheDraft_AndCountAsUnsavedChange_AndRevert()
    {
        NoContext();
        var vm = CreateViewModel();

        vm.MiddleFingerEnabled = false;
        vm.MiddleFingerHoldMilliseconds = 1500;
        vm.MiddleFingerSensitivityPercent = 70;

        var m = vm.Draft.MiddleFinger;
        Assert.False(m.Enabled);
        Assert.Equal(1500, m.HoldMilliseconds);
        Assert.Equal(70, m.SensitivityPercent);
        Assert.True(vm.HasUnsavedChanges);

        vm.MiddleFingerEnabled = true;
        vm.MiddleFingerHoldMilliseconds = 500;
        vm.MiddleFingerSensitivityPercent = 50;
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void Setters_ClampIntoTheLimits()
    {
        NoContext();
        var vm = CreateViewModel();

        vm.MiddleFingerHoldMilliseconds = 10;
        Assert.Equal(SettingsLimits.MiddleFingerHoldMillisecondsMin, vm.MiddleFingerHoldMilliseconds);
        vm.MiddleFingerHoldMilliseconds = 99999;
        Assert.Equal(SettingsLimits.MiddleFingerHoldMillisecondsMax, vm.Draft.MiddleFinger.HoldMilliseconds);
        vm.MiddleFingerSensitivityPercent = 0;
        Assert.Equal(SettingsLimits.MiddleFingerSensitivityMin, vm.MiddleFingerSensitivityPercent);
        vm.MiddleFingerSensitivityPercent = 500;
        Assert.Equal(SettingsLimits.MiddleFingerSensitivityMax, vm.Draft.MiddleFinger.SensitivityPercent);
    }

    [Fact]
    public void StatusText_FollowsTheSwitch_AndRaisesTheChange()
    {
        NoContext();
        var vm = CreateViewModel();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.MiddleFingerEnabled = false;

        Assert.Equal("Aus", vm.MiddleFingerStatusText);
        Assert.Contains(nameof(AdminViewModel.MiddleFingerStatusText), raised);
    }

    [Fact]
    public void NothingToCancelWarning_AppearsOnlyWhenPeaceAndHeartAreBothOff()
    {
        NoContext();
        var vm = CreateViewModel();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.PeaceEnabled = false;
        Assert.False(vm.HasMiddleFingerPeaceDisabledWarning); // Herz ist noch an
        vm.HeartEnabled = false;
        Assert.True(vm.HasMiddleFingerPeaceDisabledWarning);
        Assert.Equal("Peace- und Herzerkennung sind aus – es gibt nichts abzubrechen.", vm.MiddleFingerPeaceDisabledWarning);
        Assert.Contains(nameof(AdminViewModel.MiddleFingerPeaceDisabledWarning), raised);

        vm.HeartEnabled = true;
        Assert.False(vm.HasMiddleFingerPeaceDisabledWarning);
        vm.HeartEnabled = false;
        vm.PeaceEnabled = true;
        Assert.False(vm.HasMiddleFingerPeaceDisabledWarning);
        Assert.Equal(string.Empty, vm.MiddleFingerPeaceDisabledWarning);
    }

    [Fact]
    public void TheLastActionTick_IsLocked_AndNoAction_WhileEnabled_BlocksSaving()
    {
        NoContext();
        var vm = CreateViewModel();
        Assert.True(vm.MiddleFingerActionCancelPeaceEnabled);
        Assert.True(vm.MiddleFingerActionCancelKisscamEnabled);

        bool? closed = null;
        vm.CloseRequested += save => closed = save;
        vm.MiddleFingerActionCancelKisscam = false;
        Assert.False(vm.MiddleFingerActionCancelPeaceEnabled); // Peace ist jetzt die letzte Aktion
        Assert.True(vm.MiddleFingerActionCancelKisscamEnabled);
        vm.MiddleFingerActionCancelKisscam = true;
        vm.MiddleFingerActionCancelPeace = false;
        Assert.True(vm.MiddleFingerActionCancelPeaceEnabled);
        Assert.False(vm.MiddleFingerActionCancelKisscamEnabled); // Kisscam ist jetzt die letzte Aktion

        vm.MiddleFingerActionCancelKisscam = false; // (nur programmatisch möglich, die Ansicht sperrt den Haken)
        Assert.True(vm.MiddleFingerActionCancelPeaceEnabled);
        Assert.True(vm.MiddleFingerHasNoAction);
        vm.SaveCommand.Execute(null);

        Assert.Null(closed);
        Assert.Equal(0, _store.SaveCount);
        Assert.Contains("Mittelfingererkennung mindestens eine Aktion", vm.ValidationMessage);

        vm.MiddleFingerEnabled = false;
        Assert.False(vm.MiddleFingerHasNoAction);
    }

    [Fact]
    public void Save_PersistsTheMiddleFingerSettings()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.MiddleFingerEnabled = false;
        vm.MiddleFingerHoldMilliseconds = 1200;
        vm.MiddleFingerSensitivityPercent = 65;

        vm.SaveCommand.Execute(null);

        Assert.Equal(1, _store.SaveCount);
        var saved = _store.Current.MiddleFinger;
        Assert.False(saved.Enabled);
        Assert.Equal(1200, saved.HoldMilliseconds);
        Assert.Equal(65, saved.SensitivityPercent);
    }

    [Fact]
    public async Task ResetToDefaults_RestoresTheMiddleFingerSettings_AndRefreshesTheProperties()
    {
        NoContext();
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.MiddleFingerEnabled = false;
        vm.MiddleFingerHoldMilliseconds = 2000;
        vm.MiddleFingerSensitivityPercent = 90;
        _ui.ConfirmResult = true;
        // Threadsicher: InitializeAsync kann noch Änderungen aus anderen Threads melden.
        var raised = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Enqueue(e.PropertyName);

        await vm.ResetToDefaultsCommand.ExecuteAsync(null);

        var defaults = new MiddleFingerSettings();
        Assert.True(vm.Draft.MiddleFinger.Enabled);
        Assert.Equal(defaults.HoldMilliseconds, vm.MiddleFingerHoldMilliseconds);
        Assert.Equal(defaults.SensitivityPercent, vm.MiddleFingerSensitivityPercent);
        Assert.Equal("An", vm.MiddleFingerStatusText);
        Assert.Contains(string.Empty, raised.ToArray().Select(r => r ?? string.Empty)); // alle Eigenschaften neu gelesen
    }

    // ------------------------------------------------------------------ Gemeinsame Vorschau

    [Fact]
    public void Preview_RunsOnThePeaceAndMiddleFingerPages_Only_AsOneInstance()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.IsFunTabSelected = true;
        Assert.Equal(PeaceMode.Off, vm.PeacePreview.Mode);

        vm.FunPage = FunPage.MiddleFinger;
        Assert.Equal(PeaceMode.Watching, vm.PeacePreview.Mode);
        Assert.False(vm.FunPreview.IsActive);
        Assert.Equal(1, _gestures.OpenRequests);

        vm.FunPage = FunPage.Peace; // gleiche Instanz, gleiche Anforderung
        Assert.Equal(PeaceMode.Watching, vm.PeacePreview.Mode);
        Assert.Equal(1, _gestures.OpenRequests);

        vm.FunPage = FunPage.Persons;
        Assert.Equal(PeaceMode.Off, vm.PeacePreview.Mode);
        Assert.Equal(0, _gestures.OpenRequests);

        vm.FunPage = FunPage.MiddleFinger;
        Assert.Equal(PeaceMode.Watching, vm.PeacePreview.Mode);
        vm.IsFunTabSelected = false;
        Assert.Equal(PeaceMode.Off, vm.PeacePreview.Mode);
        Assert.Equal(FunPage.MiddleFinger, vm.FunPage);
    }

    [Fact]
    public void TestButton_StartsATestMoustache_AndArmsTheDetection_WithHandsRequested()
    {
        NoContext();
        var vm = OpenMiddleFingerPage();
        var request = _gestures.Last;
        Assert.True(request.Options.Needs.HasFlag(GestureNeeds.Hands));
        Assert.False(vm.PeacePreview.MiddleFingerWatching);
        Assert.False(vm.HasMiddleFingerPreviewHint);

        vm.TestMiddleFingerCancelCommand.Execute(null);

        Assert.True(vm.PeacePreview.HasMoustacheVisuals);
        Assert.True(vm.PeacePreview.MiddleFingerWatching);
        Assert.True(request.Options.Needs.HasFlag(GestureNeeds.Hands));
        Assert.True(vm.HasMiddleFingerPreviewHint);
        Assert.Equal("Zeigen Sie jetzt den Mittelfinger, um den Bart zu entfernen.", vm.MiddleFingerPreviewHint);
    }

    [Fact]
    public void HoldingTheMiddleFinger_VanishesTheTestMoustache()
    {
        NoContext();
        var vm = OpenMiddleFingerPage();
        _gestures.Last.RaiseStatus(new GestureStatus(true, true, null, null, 5));
        var fired = 0;
        vm.PeacePreview.MiddleFingerTriggered += (_, _) => fired++;
        vm.TestMiddleFingerCancelCommand.Execute(null);

        Run(0.6, []);          // zuerst ruhig (entwaffnet beim Start)
        Run(0.3, [Middle]);
        Assert.Equal(0, fired);
        Assert.True(vm.PeacePreview.MiddleFingerDetectedNow);
        Assert.Contains("Mittelfinger erkannt", vm.MiddleFingerPreviewStatusText);
        Run(0.4, [Middle]);

        Assert.Equal(1, fired);
        Run(0.5, []); // Verschwinden-Animation läuft aus
        Assert.False(vm.PeacePreview.HasMoustacheVisuals);
        Assert.False(vm.HasMiddleFingerPreviewHint);
    }

    [Fact]
    public void MiddleFingerShownFromTheStart_DoesNotVanishTheBeard()
    {
        NoContext();
        var vm = OpenMiddleFingerPage();
        vm.TestMiddleFingerCancelCommand.Execute(null);

        Run(2.0, [Middle]); // nie zuvor ruhig

        Assert.True(vm.PeacePreview.HasMoustacheVisuals);
    }

    [Fact]
    public void DraftChangeOfTheHold_ReconfiguresThePreview()
    {
        NoContext();
        var vm = OpenMiddleFingerPage();
        vm.MiddleFingerHoldMilliseconds = 1500;
        vm.TestMiddleFingerCancelCommand.Execute(null);
        var fired = 0;
        vm.PeacePreview.MiddleFingerTriggered += (_, _) => fired++;

        Run(0.6, []);
        Run(1.0, [Middle]);
        Assert.Equal(0, fired); // noch keine 1,5 s
        Run(0.6, [Middle]);
        Assert.Equal(1, fired);

        // und wieder kürzer: sofort wirksam, ohne die Seite zu verlassen
        vm.MiddleFingerHoldMilliseconds = 300;
        vm.TestMiddleFingerCancelCommand.Execute(null);
        Run(0.6, []);
        Run(0.5, [Middle]);
        Assert.Equal(2, fired);
    }

    [Fact]
    public void DisablingTheDetection_DisarmsThePreview()
    {
        NoContext();
        var vm = OpenMiddleFingerPage();
        var fired = 0;
        vm.PeacePreview.MiddleFingerTriggered += (_, _) => fired++;
        vm.MiddleFingerEnabled = false;
        vm.TestMiddleFingerCancelCommand.Execute(null);

        Run(0.6, []);
        Run(1.5, [Middle]);

        Assert.Equal(0, fired);
        Assert.True(vm.PeacePreview.HasMoustacheVisuals);
        Assert.False(vm.HasMiddleFingerPreviewHint);
    }

    [Fact]
    public void PeaceAction_FromThePreview_IsIgnoredOnTheMiddleFingerPage_ButNotOnThePeacePage()
    {
        NoContext();
        var vm = OpenMiddleFingerPage();

        Run(1.6, [Peace]);
        Assert.False(vm.PeaceTestSelfDestructVisible);

        vm.FunPage = FunPage.Peace;
        Run(1.0, []);      // Hand runter
        Run(1.6, [Peace]);
        Assert.True(vm.PeaceTestSelfDestructVisible);
    }

    [Fact]
    public void MiddleFinger_StopsARunningSelfDestructTest_OnThePeacePage()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.FunPage = FunPage.Peace;
        vm.IsFunTabSelected = true;
        vm.TestSelfDestructCommand.Execute(null);
        Assert.True(vm.PeaceTestSelfDestructVisible);
        Assert.True(vm.PeacePreview.MiddleFingerWatching); // der Test ist ein aufhebbares Ziel

        Run(0.5, []);
        Run(0.7, [Middle]);

        Assert.False(vm.PeaceTestSelfDestructVisible);
        Assert.False(vm.PeacePreview.MiddleFingerWatching);
    }

    [Fact]
    public void LeavingThePeacePage_CancelsTheSelfDestructTest_AndItsTarget()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.FunPage = FunPage.Peace;
        vm.IsFunTabSelected = true;
        vm.TestSelfDestructCommand.Execute(null);
        Assert.True(vm.PeacePreview.MiddleFingerWatching);

        vm.FunPage = FunPage.MiddleFinger;

        Assert.False(vm.PeaceTestSelfDestructVisible);
        Assert.False(vm.PeacePreview.MiddleFingerWatching);
    }

    [Fact]
    public void Dispose_UnsubscribesTheMiddleFingerTrigger_AndStopsThePreview()
    {
        NoContext();
        var vm = OpenMiddleFingerPage();
        vm.TestMiddleFingerCancelCommand.Execute(null);

        vm.Dispose();
        vm.TestMiddleFingerCancelCommand.Execute(null); // wirkt nicht mehr, wirft nicht

        Assert.Equal(0, _gestures.OpenRequests);
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public void WithoutGestureService_PreviewIsInert_ButDoesNotThrow()
    {
        NoContext();
        var vm = CreateViewModel(withServices: false);

        vm.FunPage = FunPage.MiddleFinger;
        vm.IsFunTabSelected = true;
        vm.MiddleFingerHoldMilliseconds = 900;
        vm.TestMiddleFingerCancelCommand.Execute(null);
        vm.IsFunTabSelected = false;

        Assert.Empty(_gestures.Requests);
    }

    [Fact]
    public void PreviewStatus_ShowsHandsAndFaces_AndTheDetection()
    {
        NoContext();
        var vm = OpenMiddleFingerPage();
        var request = _gestures.Last;
        request.RaiseStatus(new GestureStatus(true, true, null, null, 5));

        request.RaiseUpdated([Face], [Middle]);

        Assert.Contains("Hände: 1", vm.MiddleFingerPreviewStatusText);
        Assert.Contains("Gesichter: 1", vm.MiddleFingerPreviewStatusText);
        Assert.DoesNotContain("Peace erkannt", vm.MiddleFingerPreviewStatusText);
        Assert.False(vm.HasMiddleFingerPreviewFaceWarning);
    }

    [Fact]
    public void PreviewStatus_IsEmptyWhileThePreviewIsOff()
    {
        NoContext();
        var vm = CreateViewModel();
        Assert.Equal(string.Empty, vm.MiddleFingerPreviewStatusText);
        Assert.False(vm.HasMiddleFingerPreviewWarning);
        Assert.False(vm.HasMiddleFingerPreviewFaceWarning);
    }

    [Fact]
    public async Task PreviewStatusAndWarnings_DoNotHideTheSaveStatus()
    {
        NoContext();
        var vm = OpenMiddleFingerPage();
        await vm.ApplyCommand.ExecuteAsync(null); // Basis = gespeicherter Stand, „Einstellungen gespeichert.“ bleibt stehen
        Assert.True(vm.HasSaveStatus);

        _gestures.Last.RaiseStatus(new GestureStatus(true, true, null, null, 5));
        _gestures.Last.RaiseUpdated([Face], [Middle]);
        vm.TestMiddleFingerCancelCommand.Execute(null);
        Run(0.5, []);

        Assert.True(vm.HasSaveStatus);
    }

    // ------------------------------------------------------------------ Ansicht

    private void WithWindow(Action<AdminWindow, AdminViewModel> body)
    {
        var vm = CreateViewModel();
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

    private static FrameworkElement ShowFunTab(AdminWindow window)
    {
        var tabs = FindTabControl(window);
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header as string == "Fun");
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1100, 750));
        root.Arrange(new Rect(0, 0, 1100, 750));
        root.UpdateLayout();
        return root;
    }

    private static MiddleFingerSettingsView MfView(AdminWindow window) => (MiddleFingerSettingsView)window.FindName("MiddleFingerPage");

    private static T InView<T>(MiddleFingerSettingsView view, string name) where T : class =>
        view.FindName(name) as T ?? throw new InvalidOperationException(name + " nicht gefunden.");

    [StaFact]
    public void StartPage_ShowsTheMiddleFingerTile_WithItsStatus()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            ShowFunTab(window);
            var start = (UIElement)window.FindName("FunStartPage");
            var tile = (Button)window.FindName("FunTileMiddleFinger");

            Assert.Equal(Visibility.Visible, start.Visibility);
            Assert.Equal(Visibility.Collapsed, MfView(window).Visibility);
            Assert.Equal("Mittelfinger", AutomationProperties.GetName(tile));
            Assert.Same(window.TryFindResource("FunTile"), tile.Style);
            Assert.Contains(FindDescendants<TextBlock>(tile), t => t.Text == "Hebt Bart, Selbstzerstörung und Kisscam auf.");
            Assert.Contains(FindDescendants<TextBlock>(tile), t => t.Text == "An");

            vm.MiddleFingerEnabled = false;
            Assert.Contains(FindDescendants<TextBlock>(tile), t => t.Text == "Aus");
        });
    }

    [StaFact]
    public void Tile_NavigatesToThePage_AndTheBackButtonReturns()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            ShowFunTab(window);
            var tile = (Button)window.FindName("FunTileMiddleFinger");

            Assert.True(tile.Command.CanExecute(tile.CommandParameter));
            tile.Command.Execute(tile.CommandParameter);

            Assert.Equal(FunPage.MiddleFinger, vm.FunPage);
            Assert.Equal(Visibility.Visible, MfView(window).Visibility);
            Assert.Equal(Visibility.Collapsed, ((UIElement)window.FindName("FunStartPage")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((UIElement)window.FindName("PeacePage")).Visibility);
            Assert.Equal(PeaceMode.Watching, vm.PeacePreview.Mode);

            var back = InView<Button>(MfView(window), "MiddleFingerBackButton");
            back.Command.Execute(back.CommandParameter);
            Assert.Equal(FunPage.Start, vm.FunPage);
            Assert.Equal(PeaceMode.Off, vm.PeacePreview.Mode);
        });
    }

    [StaTheory]
    [InlineData(AdminThemeMode.Light)]
    [InlineData(AdminThemeMode.Dark)]
    public void Page_LaysOutInBothThemes_WithPreviewAndCards(AdminThemeMode theme)
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.IsAdminThemeLight = theme == AdminThemeMode.Light;
            vm.IsAdminThemeDark = theme == AdminThemeMode.Dark;
            vm.FunPage = FunPage.MiddleFinger;
            ShowFunTab(window);

            var view = MfView(window);
            Assert.True(view.ActualHeight > 0);
            var host = InView<Border>(view, "MiddleFingerPreviewHost");
            Assert.Equal(16.0 / 9.0, host.Width / host.Height, 3);
            Assert.Single(FindDescendants<Fotobox.Views.Controls.CameraPreview>(host));
            var overlay = Assert.Single(FindDescendants<PeaceOverlay>(host));
            Assert.True(overlay.ShowLandmarks);
            Assert.Empty(FindDescendants<SelfDestructOverlay>(host));
            Assert.Equal(Visibility.Collapsed, InView<Border>(view, "MiddleFingerDetectedBadge").Visibility);

            var test = InView<Button>(view, "MiddleFingerTestButton");
            Assert.Equal("Aufheben testen", test.Content);
            Assert.Same(vm.TestMiddleFingerCancelCommand, test.Command);

            Assert.Equal(3, FindDescendants<NumberStepper>(view).Count()); // Haltedauer, Empfindlichkeit, Analysen pro Sekunde
            var check = InView<CheckBox>(view, "MiddleFingerActionCancelPeaceCheck");
            var kisscamCheck = InView<CheckBox>(view, "MiddleFingerActionCancelKisscamCheck");
            Assert.True(check.IsChecked);
            Assert.True(kisscamCheck.IsChecked);
            Assert.True(check.IsEnabled);
            Assert.True(kisscamCheck.IsEnabled);
            Assert.Equal("Aktion: Kisscam abbrechen", AutomationProperties.GetName(kisscamCheck));
            vm.MiddleFingerActionCancelKisscam = false;
            Assert.False(kisscamCheck.IsChecked);
            Assert.False(check.IsEnabled); // der letzte Haken ist gesperrt
            Assert.Contains(FindDescendants<ToggleButton>(view), c => AutomationProperties.GetName(c) == "Mittelfingererkennung aktiv");
            Assert.Equal(Visibility.Collapsed, InView<TextBlock>(view, "MiddleFingerNoActionWarning").Visibility);
        });
    }

    [StaFact]
    public void Page_GreysOutTheSettings_WhenSwitchedOff_AndShowsThePeaceWarning()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.FunPage = FunPage.MiddleFinger;
            ShowFunTab(window);
            var view = MfView(window);
            var panel = InView<StackPanel>(view, "MiddleFingerSettingsPanel");
            var warning = InView<TextBlock>(view, "MiddleFingerPeaceDisabledWarning");
            var test = InView<Button>(view, "MiddleFingerTestButton");
            Assert.True(panel.IsEnabled);
            Assert.Equal(Visibility.Collapsed, warning.Visibility);

            vm.MiddleFingerEnabled = false;
            vm.PeaceEnabled = false;
            Assert.Equal(Visibility.Collapsed, warning.Visibility); // Herz ist noch an
            vm.HeartEnabled = false;

            Assert.False(panel.IsEnabled);
            Assert.True(test.IsEnabled); // die Vorschau liegt außerhalb des ausgegrauten Bereichs
            Assert.Equal(Visibility.Visible, warning.Visibility);
            Assert.Equal(AdminViewModel.MiddleFingerPeaceDisabledText, warning.Text);
        });
    }

    [StaFact]
    public void Page_CardsWriteIntoTheViewModel_AndFollowIt()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.FunPage = FunPage.MiddleFinger;
            ShowFunTab(window);
            var view = MfView(window);

            InView<NumberStepper>(view, "MiddleFingerHoldStepper").Value = 1700;
            InView<NumberStepper>(view, "MiddleFingerSensitivityStepper").Value = 80;
            Assert.Equal(1700, vm.Draft.MiddleFinger.HoldMilliseconds);
            Assert.Equal(80, vm.Draft.MiddleFinger.SensitivityPercent);

            vm.MiddleFingerHoldMilliseconds = 900;
            Assert.Equal(900, InView<NumberStepper>(view, "MiddleFingerHoldStepper").Value);
        });
    }

    [StaFact]
    public void Page_AnalysesStepper_IsBoundToTheSharedPeaceValue_InBothDirections()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.FunPage = FunPage.MiddleFinger;
            ShowFunTab(window);
            var view = MfView(window);
            var stepper = InView<NumberStepper>(view, "MiddleFingerAnalysesStepper");
            var peaceStepper = InView<NumberStepper>(PeaceViewOf(window), "PeaceAnalysesStepper");

            Assert.Equal(5, stepper.Value);
            Assert.Equal(SettingsLimits.PeaceAnalysesPerSecondMin, stepper.Minimum);
            Assert.Equal(SettingsLimits.PeaceAnalysesPerSecondMax, stepper.Maximum);

            stepper.Value = 8;
            Assert.Equal(8, vm.PeaceAnalysesPerSecond);
            Assert.Equal(8, vm.Draft.Peace.AnalysesPerSecond);
            Assert.Equal(8, peaceStepper.Value); // die Peace-Seite folgt

            peaceStepper.Value = 3;
            Assert.Equal(3, stepper.Value);      // und umgekehrt
            vm.PeaceAnalysesPerSecond = 6;
            Assert.Equal(6, stepper.Value);
            Assert.Equal(6, peaceStepper.Value);
        });
    }

    [StaFact]
    public void Page_AnalysesLabel_HasAnInfoBadge_WithATooltipAndAutomationText()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.FunPage = FunPage.MiddleFinger;
            ShowFunTab(window);
            var badge = InView<Button>(MfView(window), "MiddleFingerAnalysesInfo");

            AssertInfoBadge(window, badge, AdminInfoTexts.MiddleFingerAnalysesPerSecond);
        });
    }

    [StaFact]
    public void PeaceAndFunPages_HaveInfoBadgesOnTheirAnalysesRate()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            ShowFunTab(window);
            AssertInfoBadge(window, InView<Button>(PeaceViewOf(window), "PeaceAnalysesInfo"), AdminInfoTexts.PeaceAnalysesPerSecond);
            var fun = Assert.IsType<Button>(window.FindName("FunAnalysesInfo"));
            AssertInfoBadge(window, fun, AdminInfoTexts.FunAnalysesPerSecond);
        });
    }

    private static PeaceSettingsView PeaceViewOf(AdminWindow window) => (PeaceSettingsView)window.FindName("PeacePage");

    private static T InView<T>(PeaceSettingsView view, string name) where T : class =>
        view.FindName(name) as T ?? throw new InvalidOperationException(name + " nicht gefunden.");

    private static void AssertInfoBadge(AdminWindow window, Button badge, string expectedText)
    {
        Assert.Equal(expectedText, badge.ToolTip);
        Assert.False(string.IsNullOrWhiteSpace(expectedText));
        Assert.Equal(AdminInfoTexts.AnalysesPerSecondName, AutomationProperties.GetName(badge));
        Assert.Equal(expectedText, AutomationProperties.GetHelpText(badge));
        Assert.Same(window.TryFindResource("InfoBadge"), badge.Style);
        Assert.Equal(60000, ToolTipService.GetShowDuration(badge));
        Assert.True(ToolTipService.GetShowOnDisabled(badge));
        Assert.False(badge.Focusable);
    }

    [StaFact]
    public void Page_StatusLine_FollowsThePreview_AndShowsTheTestHint()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            ShowFunTab(window);
            var view = MfView(window);
            var status = InView<TextBlock>(view, "MiddleFingerPreviewStatus");
            var hint = InView<TextBlock>(view, "MiddleFingerPreviewHint");
            Assert.Equal(string.Empty, status.Text);

            vm.FunPage = FunPage.MiddleFinger;
            Assert.Equal(PeacePreviewStatusFormatter.Loading, status.Text);
            Assert.Equal(Visibility.Collapsed, hint.Visibility);

            vm.TestMiddleFingerCancelCommand.Execute(null);
            Assert.Equal(Visibility.Visible, hint.Visibility);
            Assert.Equal(AdminViewModel.MiddleFingerTestHintText, hint.Text);
        });
    }
}
