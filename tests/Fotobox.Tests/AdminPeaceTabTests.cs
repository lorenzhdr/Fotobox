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

/// <summary>Admin-Tab „Fun“: Startseite mit Kacheln, Navigation und die Seite „Peaceerkennung“ (Entwurf, Vorschau, Tests, Ansicht).</summary>
[Collection(WpfCollection.Name)]
public sealed class AdminPeaceTabTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-peace-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AdminViewModelTests.FakeStore _store;
    private readonly AdminViewModelTests.FakePinService _pin;
    private readonly AdminViewModelTests.FakeUi _ui = new();
    private readonly FakeDetectionService _detection = new();
    private readonly FakeGestureService _gestures = new();
    private readonly FakeSoundPlayer _sound = new();
    private readonly TrackingTimeProvider _time = new();
    private readonly List<AdminViewModel> _created = [];

    public AdminPeaceTabTests()
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

    // xunit setzt je Test einen eigenen Kontext; Timer-Ereignisse sollen aber synchron im Test laufen.
    private static void NoContext() => SynchronizationContext.SetSynchronizationContext(null);

    private AdminViewModel CreateViewModel(bool withServices = true)
    {
        var vm = new AdminViewModel(_store, _pin, new AdminViewModelTests.FakeCamera(), new AdminViewModelTests.FakePrint(), _ui, _time,
            new AdminViewModelTests.FakeDisplays(AdminViewModelTests.AdminMonitors.Primary),
            withServices ? _detection : null, withServices ? _sound : null, withServices ? _gestures : null);
        _created.Add(vm);
        return vm;
    }

    // ------------------------------------------------------------------ Navigation

    [Fact]
    public void FunPage_StartsOnTheStartPage()
    {
        NoContext();
        Assert.Equal(FunPage.Start, CreateViewModel().FunPage);
    }

    [Fact]
    public void ShowFunPageCommand_NavigatesWithTheTypedParameter()
    {
        NoContext();
        var vm = CreateViewModel();

        vm.ShowFunPageCommand.Execute(FunPage.Peace);
        Assert.Equal(FunPage.Peace, vm.FunPage);

        vm.ShowFunPageCommand.Execute(FunPage.Start);
        Assert.Equal(FunPage.Start, vm.FunPage);

        vm.ShowFunPageCommand.Execute(FunPage.Persons);
        Assert.Equal(FunPage.Persons, vm.FunPage);
    }

    [Fact]
    public void FunPageChange_IsNoUnsavedChange()
    {
        NoContext();
        var vm = CreateViewModel();

        vm.FunPage = FunPage.Peace;
        vm.FunPage = FunPage.Persons;

        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void StatusTexts_FollowTheEnabledFlags_AndTheReset()
    {
        NoContext();
        var vm = CreateViewModel();
        Assert.Equal("An", vm.FunPersonsStatusText);
        Assert.Equal("An", vm.PeaceStatusText);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.FunEnabled = false;
        vm.PeaceEnabled = false;

        Assert.Equal("Aus", vm.FunPersonsStatusText);
        Assert.Equal("Aus", vm.PeaceStatusText);
        Assert.Contains(nameof(AdminViewModel.FunPersonsStatusText), raised);
        Assert.Contains(nameof(AdminViewModel.PeaceStatusText), raised);
    }

    [Fact]
    public async Task ResetToDefaults_RestoresPeaceSettings_AndTheStatusTexts()
    {
        NoContext();
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.PeaceEnabled = false;
        vm.PeaceActionPhoto = true;
        vm.PeaceHoldMilliseconds = 2000;
        vm.PeaceMoustacheStyle = MoustacheStyle.Brush;
        vm.PeaceSelfDestructSoundEnabled = true;
        _ui.ConfirmResult = true;

        await vm.ResetToDefaultsCommand.ExecuteAsync(null);

        var defaults = new PeaceSettings();
        Assert.True(vm.Draft.Peace.Enabled);
        Assert.Equal(defaults.ActionPhoto, vm.Draft.Peace.ActionPhoto);
        Assert.Equal(defaults.HoldMilliseconds, vm.Draft.Peace.HoldMilliseconds);
        Assert.Equal(defaults.MoustacheStyle, vm.Draft.Peace.MoustacheStyle);
        Assert.False(vm.Draft.Peace.SelfDestructSoundEnabled);
        Assert.Equal("An", vm.PeaceStatusText);
        Assert.True(vm.PeaceEnabled);
        Assert.Equal(1000, vm.PeaceHoldMilliseconds);
    }

    // ------------------------------------------------------------------ Entwurf

    [Fact]
    public void Defaults_AreReadFromSettings()
    {
        NoContext();
        var vm = CreateViewModel();

        Assert.True(vm.PeaceEnabled);
        Assert.True(vm.PeaceActionSelfDestruct);
        Assert.False(vm.PeaceActionPhoto);
        Assert.False(vm.PeaceActionMoustache);
        Assert.False(vm.PeaceHasNoAction);
        Assert.Equal(1000, vm.PeaceHoldMilliseconds);
        Assert.False(vm.PeaceShowHoldProgress);
        Assert.Equal(0, vm.PeaceCooldownSeconds);
        Assert.Equal(50, vm.PeaceSensitivityPercent);
        Assert.Equal(5, vm.PeaceAnalysesPerSecond);
        Assert.Equal(10, vm.PeaceSelfDestructSeconds);
        Assert.False(vm.PeaceSelfDestructSoundEnabled);
        Assert.Equal(3, vm.PeacePhotoCountdownSeconds);
        Assert.Equal(MoustacheTargets.Everyone, vm.PeaceMoustacheTargets);
        Assert.Equal(MoustacheStyle.Random, vm.PeaceMoustacheStyle);
        Assert.Equal(MoustacheVisibility.UntilPhotoOrGone, vm.PeaceMoustacheVisibility);
        Assert.False(vm.PeaceMoustacheDurationEnabled);
        Assert.Equal(15, vm.PeaceMoustacheDurationSeconds);
        Assert.False(vm.PeaceMoustacheAutoPhoto);
        Assert.Equal(3, vm.PeaceMoustacheAutoPhotoCountdownSeconds);
    }

    [Fact]
    public void Setters_WriteIntoTheDraft_AndCountAsUnsavedChange()
    {
        NoContext();
        var vm = CreateViewModel();

        vm.PeaceEnabled = false;
        vm.PeaceActionSelfDestruct = false;
        vm.PeaceActionPhoto = true;
        vm.PeaceActionMoustache = true;
        vm.PeaceHoldMilliseconds = 1500;
        vm.PeaceShowHoldProgress = true;
        vm.PeaceCooldownSeconds = 30;
        vm.PeaceSensitivityPercent = 70;
        vm.PeaceAnalysesPerSecond = 8;
        vm.PeaceSelfDestructSeconds = 20;
        vm.PeaceSelfDestructSoundEnabled = true;
        vm.PeacePhotoCountdownSeconds = 5;
        vm.PeaceMoustacheTargets = MoustacheTargets.PeaceShower;
        vm.PeaceMoustacheStyle = MoustacheStyle.Walrus;
        vm.PeaceMoustacheVisibility = MoustacheVisibility.FixedDuration;
        vm.PeaceMoustacheDurationSeconds = 40;
        vm.PeaceMoustacheAutoPhoto = true;
        vm.PeaceMoustacheAutoPhotoCountdownSeconds = 7;

        var p = vm.Draft.Peace;
        Assert.False(p.Enabled);
        Assert.False(p.ActionSelfDestruct);
        Assert.True(p.ActionPhoto);
        Assert.True(p.ActionMoustache);
        Assert.Equal(1500, p.HoldMilliseconds);
        Assert.True(p.ShowHoldProgress);
        Assert.Equal(30, p.CooldownSeconds);
        Assert.Equal(70, p.SensitivityPercent);
        Assert.Equal(8, p.AnalysesPerSecond);
        Assert.Equal(20, p.SelfDestructSeconds);
        Assert.True(p.SelfDestructSoundEnabled);
        Assert.Equal(5, p.PhotoCountdownSeconds);
        Assert.Equal(MoustacheTargets.PeaceShower, p.MoustacheTargets);
        Assert.Equal(MoustacheStyle.Walrus, p.MoustacheStyle);
        Assert.Equal(MoustacheVisibility.FixedDuration, p.MoustacheVisibility);
        Assert.Equal(40, p.MoustacheDurationSeconds);
        Assert.True(p.MoustacheAutoPhoto);
        Assert.Equal(7, p.MoustacheAutoPhotoCountdownSeconds);
        Assert.True(vm.PeaceMoustacheDurationEnabled);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public void HasUnsavedChanges_ReactsToPeaceChanges_AndTheirRevert()
    {
        NoContext();
        var vm = CreateViewModel();

        vm.PeaceHoldMilliseconds = 2000;
        Assert.True(vm.HasUnsavedChanges);
        vm.PeaceHoldMilliseconds = 1000;
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void NoAction_WhileEnabled_IsWarned_AndBlocksSaving()
    {
        NoContext();
        var vm = CreateViewModel();
        bool? closed = null;
        vm.CloseRequested += save => closed = save;

        vm.PeaceActionSelfDestruct = false;
        Assert.True(vm.PeaceHasNoAction);
        vm.SaveCommand.Execute(null);

        Assert.Null(closed);
        Assert.Equal(0, _store.SaveCount);
        Assert.Contains("mindestens eine Aktion", vm.ValidationMessage);
    }

    [Fact]
    public void NoAction_WhileDisabled_IsNoError()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.PeaceActionSelfDestruct = false;
        vm.PeaceEnabled = false;

        Assert.False(vm.PeaceHasNoAction);
        vm.SaveCommand.Execute(null);

        Assert.DoesNotContain("Peaceerkennung", vm.ValidationMessage);
    }

    // ------------------------------------------------------------------ Vorschau-Steuerung

    [Fact]
    public void Previews_RunOnlyOnTheirOwnPage()
    {
        NoContext();
        var vm = CreateViewModel();

        vm.IsFunTabSelected = true; // Startseite: keine Vorschau
        Assert.False(vm.FunPreview.IsActive);
        Assert.Equal(PeaceMode.Off, vm.PeacePreview.Mode);
        Assert.Empty(_detection.Requests);

        vm.FunPage = FunPage.Persons;
        Assert.True(vm.FunPreview.IsActive);
        Assert.Equal(PeaceMode.Off, vm.PeacePreview.Mode);
        Assert.Equal(1, _detection.OpenRequests);

        vm.FunPage = FunPage.Peace;
        Assert.False(vm.FunPreview.IsActive);
        Assert.Equal(0, _detection.OpenRequests);
        Assert.Equal(PeaceMode.Watching, vm.PeacePreview.Mode);

        vm.FunPage = FunPage.Start;
        Assert.Equal(PeaceMode.Off, vm.PeacePreview.Mode);
        Assert.False(vm.FunPreview.IsActive);
    }

    [Fact]
    public void PeacePreview_StopsWhenLeavingTheTab_AndRunsEvenWhilePeaceIsDisabled()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.PeaceEnabled = false;
        vm.FunPage = FunPage.Peace;

        vm.IsFunTabSelected = true;
        Assert.Equal(PeaceMode.Watching, vm.PeacePreview.Mode);

        vm.IsFunTabSelected = false;
        Assert.Equal(PeaceMode.Off, vm.PeacePreview.Mode);
        Assert.Equal(FunPage.Peace, vm.FunPage); // Tab-Wechsel lässt die Seite stehen
    }

    [Fact]
    public void Dispose_StopsThePeacePreview()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.FunPage = FunPage.Peace;
        vm.IsFunTabSelected = true;

        vm.Dispose();
        vm.IsFunTabSelected = true;

        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public void WithoutGestureService_PreviewIsInert_ButDoesNotThrow()
    {
        NoContext();
        var vm = CreateViewModel(withServices: false);

        vm.FunPage = FunPage.Peace;
        vm.IsFunTabSelected = true;
        vm.PeaceHoldMilliseconds = 1500;
        vm.TestMoustacheCommand.Execute(null);
        vm.TestSelfDestructCommand.Execute(null);
        vm.IsFunTabSelected = false;

        Assert.Empty(_gestures.Requests);
    }

    [Fact]
    public void PreviewStatus_ShowsTheFaceCount_FromTheLastAnalysis()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.FunPage = FunPage.Peace;
        vm.IsFunTabSelected = true;
        var request = _gestures.Last;
        var ready = new GestureStatus(true, true, null, null, 5);
        request.RaiseStatus(ready);

        request.RaiseUpdated(new GestureUpdate(
            [PeaceHandFixtures.Face(0.3, 0.3, 0.12), PeaceHandFixtures.Face(0.7, 0.3, 0.12)], [], 1280, 720, ready));

        Assert.Equal(2, vm.PeacePreview.FaceCount);
        Assert.Contains("Gesichter: 2", vm.PeacePreviewStatusText);
    }

    [Fact]
    public void MoustacheWarning_AppearsOnlyWhenTheFaceModelIsUnavailable_AndMoustacheIsChosen()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.PeaceActionMoustache = true;
        Assert.False(vm.HasPeacePreviewMoustacheWarning); // Vorschau aus: noch nichts bekannt

        vm.FunPage = FunPage.Peace;
        vm.IsFunTabSelected = true;
        Assert.False(vm.HasPeacePreviewMoustacheWarning); // Vorschau läuft, Gesichtserkennung noch nicht gemeldet defekt

        _gestures.Last.RaiseStatus(new GestureStatus(false, true, "Das Gesichtsmodell fehlt.", null, 0));
        Assert.True(vm.HasPeacePreviewMoustacheWarning);
        Assert.Equal(AdminViewModel.PeaceMoustacheUnavailableText, vm.PeacePreviewMoustacheWarning);

        vm.PeaceActionMoustache = false;
        Assert.False(vm.HasPeacePreviewMoustacheWarning);
    }

    // ------------------------------------------------------------------ Test „Selbstzerstörung“

    [Fact]
    public void TestSelfDestruct_CountsDown_RequestsTheExplosionOnce_AndEndsWithoutPhoto()
    {
        NoContext();
        var vm = CreateViewModel();
        var explosions = 0;
        vm.PeaceTestExplosionRequested += (_, _) => explosions++;
        var seconds = new List<int>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AdminViewModel.PeaceTestSecondsRemaining))
                seconds.Add(vm.PeaceTestSecondsRemaining);
        };

        vm.TestSelfDestructCommand.Execute(null);

        Assert.True(vm.PeaceTestSelfDestructVisible);
        Assert.Equal(5, vm.PeaceTestSecondsRemaining);
        Assert.Equal(1, _time.ActiveTimers);

        for (var i = 0; i < 4; i++)
            _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, vm.PeaceTestSecondsRemaining);
        Assert.Equal(0, explosions);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, vm.PeaceTestSecondsRemaining);
        Assert.Equal(1, explosions);
        Assert.True(vm.PeaceTestSelfDestructVisible);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(vm.PeaceTestSelfDestructVisible);
        Assert.Equal(0, _time.ActiveTimers);
        Assert.Equal(1, explosions);
        Assert.Equal([5, 4, 3, 2, 1, 0], seconds);
        // Der Test löst nie ein Foto aus und ist kein ungespeicherter Entwurf.
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void TestSelfDestruct_Restart_ResetsTheCountdown()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.TestSelfDestructCommand.Execute(null);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(3, vm.PeaceTestSecondsRemaining);

        vm.TestSelfDestructCommand.Execute(null);

        Assert.Equal(5, vm.PeaceTestSecondsRemaining);
        Assert.Equal(1, _time.ActiveTimers);
    }

    [Fact]
    public void LeavingThePeacePage_CancelsTheSelfDestructTest()
    {
        NoContext();
        var vm = CreateViewModel();
        vm.FunPage = FunPage.Peace;
        vm.IsFunTabSelected = true;
        vm.TestSelfDestructCommand.Execute(null);
        Assert.True(vm.PeaceTestSelfDestructVisible);

        vm.FunPage = FunPage.Start;

        Assert.False(vm.PeaceTestSelfDestructVisible);
        Assert.Equal(0, _time.ActiveTimers);
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
        return Layout(window);
    }

    private static FrameworkElement Layout(AdminWindow window)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1100, 750));
        root.Arrange(new Rect(0, 0, 1100, 750));
        root.UpdateLayout();
        return root;
    }

    private static PeaceSettingsView PeaceView(AdminWindow window) => (PeaceSettingsView)window.FindName("PeacePage");

    private static T InPeace<T>(PeaceSettingsView view, string name) where T : class =>
        view.FindName(name) as T ?? throw new InvalidOperationException(name + " nicht gefunden.");

    [StaFact]
    public void StartPage_ShowsTwoTiles_AndOnlyTheStartPageIsVisible()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            ShowFunTab(window);

            var start = (UIElement)window.FindName("FunStartPage");
            var persons = (UIElement)window.FindName("FunPersonsPage");
            var peace = PeaceView(window);
            Assert.Equal(Visibility.Visible, start.Visibility);
            Assert.Equal(Visibility.Collapsed, persons.Visibility);
            Assert.Equal(Visibility.Collapsed, peace.Visibility);

            var tiles = FindDescendants<Button>(start).Where(b => b.Style == window.TryFindResource("FunTile")).ToList();
            Assert.Equal(2, tiles.Count);
            Assert.Equal(["Personenerkennung", "Peaceerkennung"], tiles.Select(AutomationProperties.GetName));
            Assert.Contains(FindDescendants<TextBlock>(start), t => t.Text == "Zielmarkierungen über erkannten Personen im Livebild.");
            Assert.Contains(FindDescendants<TextBlock>(start), t => t.Text == "Verstecktes Extra: Ein Peace-Zeichen löst eine Überraschung aus.");
            Assert.Equal(["An", "An"], FindDescendants<TextBlock>(start).Where(t => t.Text is "An" or "Aus").Select(t => t.Text));

            vm.FunEnabled = false;
            vm.PeaceEnabled = false;
            Assert.Equal(["Aus", "Aus"], FindDescendants<TextBlock>(start).Where(t => t.Text is "An" or "Aus").Select(t => t.Text));
        });
    }

    [StaFact]
    public void Tiles_NavigateToTheSubpages_AndBackButtonsReturn()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            ShowFunTab(window);
            var persons = (Button)window.FindName("FunTilePersons");
            var peace = (Button)window.FindName("FunTilePeace");

            // Klick = Befehl mit dem typisierten Parameter aus dem XAML
            Assert.True(persons.Command.CanExecute(persons.CommandParameter));
            persons.Command.Execute(persons.CommandParameter);
            Assert.Equal(FunPage.Persons, vm.FunPage);
            Assert.Equal(Visibility.Visible, ((UIElement)window.FindName("FunPersonsPage")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((UIElement)window.FindName("FunStartPage")).Visibility);

            var back = (Button)window.FindName("FunPersonsBackButton");
            back.Command.Execute(back.CommandParameter);
            Assert.Equal(FunPage.Start, vm.FunPage);

            peace.Command.Execute(peace.CommandParameter);
            Assert.Equal(FunPage.Peace, vm.FunPage);
            Assert.Equal(Visibility.Visible, PeaceView(window).Visibility);
            Assert.Equal(Visibility.Collapsed, ((UIElement)window.FindName("FunPersonsPage")).Visibility);

            var peaceBack = InPeace<Button>(PeaceView(window), "PeaceBackButton");
            peaceBack.Command.Execute(peaceBack.CommandParameter);
            Assert.Equal(FunPage.Start, vm.FunPage);
        });
    }

    [StaFact]
    public void TabSwitch_KeepsTheFunPage_AndPreviewsFollowTheSelection()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            ShowFunTab(window);
            vm.FunPage = FunPage.Peace;
            Assert.Equal(PeaceMode.Watching, vm.PeacePreview.Mode);

            var tabs = FindTabControl(window);
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => t.Header as string == "Ablauf");
            Assert.False(vm.IsFunTabSelected);
            Assert.Equal(PeaceMode.Off, vm.PeacePreview.Mode);
            Assert.Equal(FunPage.Peace, vm.FunPage);

            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header as string == "Fun");
            Assert.Equal(PeaceMode.Watching, vm.PeacePreview.Mode);
        });
    }

    [StaTheory]
    [InlineData(AdminThemeMode.Light)]
    [InlineData(AdminThemeMode.Dark)]
    public void PeacePage_LaysOutInBothThemes_WithPreviewAndAllCards(AdminThemeMode theme)
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.IsAdminThemeLight = theme == AdminThemeMode.Light;
            vm.IsAdminThemeDark = theme == AdminThemeMode.Dark;
            vm.FunPage = FunPage.Peace;
            var root = ShowFunTab(window);

            Assert.True(root.ActualHeight > 0);
            var view = PeaceView(window);
            Assert.True(view.ActualHeight > 0);
            var host = InPeace<Border>(view, "PeacePreviewHost");
            Assert.Equal(16.0 / 9.0, host.Width / host.Height, 3);
            Assert.Single(FindDescendants<Fotobox.Views.Controls.CameraPreview>(host));
            var overlay = Assert.Single(FindDescendants<PeaceOverlay>(host));
            Assert.Equal(Fotobox.Core.Fun.OverlayFit.Uniform, overlay.Fit);
            Assert.True(overlay.ShowLandmarks);
            Assert.Single(FindDescendants<SelfDestructOverlay>(host));
            Assert.Equal(Visibility.Collapsed, InPeace<SelfDestructOverlay>(view, "PeaceSelfDestruct").Visibility);

            Assert.Equal("Selbstzerstörung testen", InPeace<Button>(view, "PeaceTestSelfDestructButton").Content);
            Assert.Equal("Bart testen", InPeace<Button>(view, "PeaceTestMoustacheButton").Content);
            Assert.Same(vm.TestSelfDestructCommand, InPeace<Button>(view, "PeaceTestSelfDestructButton").Command);
            Assert.Same(vm.TestMoustacheCommand, InPeace<Button>(view, "PeaceTestMoustacheButton").Command);

            var checks = FindDescendants<CheckBox>(view).Where(c => c.Style == window.TryFindResource("CardCheck")).ToList();
            Assert.Equal(3, checks.Count);
            Assert.Equal(2, FindDescendants<RadioButton>(view).Count(r => r.GroupName == "PeaceMoustacheTargets"));
            Assert.Equal(5, FindDescendants<RadioButton>(view).Count(r => r.GroupName == "PeaceMoustacheStyle"));
            Assert.Equal(2, FindDescendants<RadioButton>(view).Count(r => r.GroupName == "PeaceMoustacheVisibility"));
            Assert.Equal(6, FindDescendants<MoustacheSketch>(view).Count()); // Aktionskarte + 5 Stile
            Assert.Single(FindDescendants<SelfDestructSketch>(view));
            // Haltedauer, Sperrzeit, Empfindlichkeit, Analysen, Dauer, Foto-Countdown, Bartdauer, Auto-Foto-Countdown
            Assert.Equal(8, FindDescendants<NumberStepper>(view).Count());
            Assert.Contains(FindDescendants<ToggleButton>(view), c => AutomationProperties.GetName(c) == "Peaceerkennung einschalten");
        });
    }

    [StaFact]
    public void PeacePage_GreysOutSectionsByTheirSwitches()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.FunPage = FunPage.Peace;
            ShowFunTab(window);
            var view = PeaceView(window);
            var panel = InPeace<StackPanel>(view, "PeaceSettingsPanel");
            var selfDestruct = InPeace<StackPanel>(view, "PeaceSelfDestructPanel");
            var photo = InPeace<StackPanel>(view, "PeacePhotoPanel");
            var moustache = InPeace<StackPanel>(view, "PeaceMoustachePanel");
            var duration = InPeace<StackPanel>(view, "PeaceMoustacheDurationPanel");
            var autoPhoto = InPeace<StackPanel>(view, "PeaceMoustacheAutoPhotoPanel");

            Assert.True(panel.IsEnabled);
            Assert.True(selfDestruct.IsEnabled);
            Assert.False(photo.IsEnabled);
            Assert.False(moustache.IsEnabled);
            Assert.True(photo.Opacity < 1.0);
            Assert.False(duration.IsEnabled);
            Assert.False(autoPhoto.IsEnabled);

            vm.PeaceActionPhoto = true;
            vm.PeaceActionMoustache = true;
            vm.PeaceMoustacheVisibility = MoustacheVisibility.FixedDuration;
            vm.PeaceMoustacheAutoPhoto = true;
            Assert.True(photo.IsEnabled);
            Assert.True(moustache.IsEnabled);
            Assert.True(duration.IsEnabled);
            Assert.True(autoPhoto.IsEnabled);

            vm.PeaceEnabled = false;
            Assert.False(panel.IsEnabled);
            Assert.True(panel.Opacity < 1.0);
            // Die Vorschau bleibt auch bei ausgeschalteter Erkennung bedienbar.
            Assert.True(InPeace<Border>(view, "PeacePreviewHost").IsEnabled);
            Assert.True(InPeace<Button>(view, "PeaceTestSelfDestructButton").IsEnabled);
        });
    }

    [StaFact]
    public void PeacePage_CardsWriteIntoTheViewModel_AndFollowIt()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.FunPage = FunPage.Peace;
            ShowFunTab(window);
            var view = PeaceView(window);

            InPeace<CheckBox>(view, "PeaceActionPhotoCheck").IsChecked = true;
            FindDescendants<RadioButton>(view).Single(r => AutomationProperties.GetName(r) == "Stil: Walross").IsChecked = true;
            FindDescendants<RadioButton>(view).Single(r => AutomationProperties.GetName(r) == "Nur wer Peace zeigt").IsChecked = true;
            FindDescendants<RadioButton>(view).Single(r => AutomationProperties.GetName(r) == "Feste Dauer").IsChecked = true;

            Assert.True(vm.PeaceActionPhoto);
            Assert.Equal(MoustacheStyle.Walrus, vm.PeaceMoustacheStyle);
            Assert.Equal(MoustacheTargets.PeaceShower, vm.PeaceMoustacheTargets);
            Assert.Equal(MoustacheVisibility.FixedDuration, vm.PeaceMoustacheVisibility);

            vm.PeaceMoustacheStyle = MoustacheStyle.Chevron;
            Assert.Equal(["Stil: Chevron"],
                FindDescendants<RadioButton>(view).Where(r => r.GroupName == "PeaceMoustacheStyle" && r.IsChecked == true)
                    .Select(AutomationProperties.GetName));
        });
    }

    [StaFact]
    public void PeacePage_WarningAppearsOnlyWithoutAnyAction()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.FunPage = FunPage.Peace;
            ShowFunTab(window);
            var warning = InPeace<TextBlock>(PeaceView(window), "PeaceNoActionWarning");
            Assert.Equal(Visibility.Collapsed, warning.Visibility);

            vm.PeaceActionSelfDestruct = false;
            Assert.Equal(Visibility.Visible, warning.Visibility);

            vm.PeaceActionMoustache = true;
            Assert.Equal(Visibility.Collapsed, warning.Visibility);
        });
    }

    [StaFact]
    public void PeacePage_TestCountdown_ShowsTheOverlay_AndEndsAfterTheExplosion()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            vm.FunPage = FunPage.Peace;
            ShowFunTab(window);
            var overlay = InPeace<SelfDestructOverlay>(PeaceView(window), "PeaceSelfDestruct");

            vm.TestSelfDestructCommand.Execute(null);
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.Equal(5, overlay.SecondsRemaining);

            _time.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(3, overlay.SecondsRemaining);

            for (var i = 0; i < 4; i++)
                _time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
        });
    }

    [StaFact]
    public void PeacePage_StatusLine_ShowsTheLoadingTextWhilePreviewIsActive()
    {
        NoContext();
        WithWindow((window, vm) =>
        {
            ShowFunTab(window);
            var status = InPeace<TextBlock>(PeaceView(window), "PeacePreviewStatus");
            Assert.Equal(string.Empty, status.Text); // Startseite: Vorschau aus

            vm.FunPage = FunPage.Peace;

            Assert.Equal(PeacePreviewStatusFormatter.Loading, status.Text);
        });
    }
}
