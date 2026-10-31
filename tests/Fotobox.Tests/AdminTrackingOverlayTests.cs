using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Fotobox.Core;
using Fotobox.ViewModels.Booth;
using Fotobox.ViewModels.Admin;
using Fotobox.Views.Admin;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Admin, Tab „Fun“, Seite „Tracking-Overlay“ und Tastenaktion „Tracking-Overlay ein/aus“ im Tab „Tasten“:
/// Entwurf, Texte, Validierung, Speichern, Tasten (Laden, Schreiben, Konflikte, Zurücksetzen), Ansicht und Passivität (keine Erkennung).
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class AdminTrackingOverlayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-trackoverlay-" + Guid.NewGuid().ToString("N"));
    private readonly AdminViewModelTests.FakeStore _store;
    private readonly AdminViewModelTests.FakePinService _pin;
    private readonly AdminViewModelTests.FakeUi _ui = new();
    private readonly FakeDetectionService _detection = new();
    private readonly FakeGestureService _gestures = new();
    private readonly FakeSoundPlayer _sound = new();
    private readonly List<AdminViewModel> _created = [];

    public AdminTrackingOverlayTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "overlays"));
        _store = new AdminViewModelTests.FakeStore(_dir);
        _pin = new AdminViewModelTests.FakePinService(_store);
    }

    public void Dispose()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        foreach (var vm in _created)
            vm.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private AdminViewModel CreateViewModel(bool withServices = false)
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var vm = new AdminViewModel(_store, _pin, new AdminViewModelTests.FakeCamera(), new AdminViewModelTests.FakePrint(), _ui, null,
            new AdminViewModelTests.FakeDisplays(AdminViewModelTests.AdminMonitors.Primary),
            detection: withServices ? _detection : null, sound: withServices ? _sound : null, gestures: withServices ? _gestures : null);
        _created.Add(vm);
        return vm;
    }

    private static KeyActionEditor Tracking(AdminViewModel vm) => vm.KeyEditors.Single(e => e.Action == KeyAction.TrackingOverlay);

    private static KeyChord Chord(Key key, ModifierKeys modifiers = ModifierKeys.None) => new(key, modifiers);

    // ---- Seite (Entwurf)

    [Fact]
    public void Defaults_MatchTheSettingsDefaults()
    {
        var vm = CreateViewModel();

        Assert.True(vm.TrackingOverlayShowPersons && vm.TrackingOverlayShowFaces && vm.TrackingOverlayShowHands);
        Assert.True(vm.TrackingOverlayShowGestures && vm.TrackingOverlayShowMoustaches);
        Assert.True(vm.TrackingOverlayShowLabels && vm.TrackingOverlayShowStatusLine);
        Assert.False(vm.TrackingOverlayShowVerdicts);   // Warum-Info ist standardmäßig aus (rechnet zusätzlich)
        Assert.True(vm.IsTrackingOverlayTechnik);
        Assert.False(vm.IsTrackingOverlaySciFi);
        Assert.Equal(SettingsLimits.TrackingOverlayScaleDefault, vm.TrackingOverlayScalePercent);
        Assert.Equal("100 %", vm.TrackingOverlayScaleText);
    }

    [Fact]
    public void Edits_RoundtripIntoTheDraft_NotTheStore_AndCountAsUnsavedChanges()
    {
        var vm = CreateViewModel();
        Assert.False(vm.HasUnsavedChanges);

        vm.TrackingOverlayShowPersons = false;
        vm.TrackingOverlayShowFaces = false;
        vm.TrackingOverlayShowHands = false;
        vm.TrackingOverlayShowGestures = false;
        vm.TrackingOverlayShowMoustaches = false;
        vm.TrackingOverlayShowLabels = false;
        vm.TrackingOverlayShowStatusLine = false;
        vm.TrackingOverlayShowVerdicts = true;
        vm.IsTrackingOverlaySciFi = true;
        vm.TrackingOverlayScalePercent = 150;

        Assert.True(vm.HasUnsavedChanges);
        var d = vm.Draft.TrackingOverlay;
        Assert.False(d.ShowPersons || d.ShowFaces || d.ShowHands || d.ShowGestures || d.ShowMoustaches || d.ShowLabels || d.ShowStatusLine);
        Assert.True(d.ShowVerdicts);
        Assert.Equal(TrackingOverlayStyle.SciFi, d.Style);
        Assert.Equal(150, d.ScalePercent);
        Assert.True(_store.Current.TrackingOverlay.ShowPersons);
        Assert.Equal(TrackingOverlayStyle.Technik, _store.Current.TrackingOverlay.Style);
    }

    [Fact]
    public void Save_WritesTheOverlaySettingsAndTheKeysToTheStore()
    {
        var vm = CreateViewModel();
        vm.TrackingOverlayShowHands = false;
        vm.TrackingOverlayShowVerdicts = true;
        vm.IsTrackingOverlaySciFi = true;
        vm.TrackingOverlayScalePercent = SettingsLimits.TrackingOverlayScaleMax;

        vm.ApplyCommand.Execute(null);

        var s = _store.Current.TrackingOverlay;
        Assert.False(s.ShowHands);
        Assert.True(s.ShowPersons);
        Assert.True(s.ShowVerdicts);
        Assert.Equal(TrackingOverlayStyle.SciFi, s.Style);
        Assert.Equal(SettingsLimits.TrackingOverlayScaleMax, s.ScalePercent);
        // Die Taste geht beim Speichern nicht verloren.
        Assert.Equal([InputSettings.DefaultTrackingOverlayKey], _store.Current.Input.TrackingOverlayKeys);
    }

    [Fact]
    public void Style_Choices_AreMutuallyExclusive_AndNotifyBothProperties()
    {
        var vm = CreateViewModel();
        var raised = new ConcurrentQueue<string?>(); // Hintergrund-Meldungen des ViewModels dürfen mitlaufen
        vm.PropertyChanged += (_, e) => raised.Enqueue(e.PropertyName);

        vm.IsTrackingOverlaySciFi = true;
        Assert.True(vm.IsTrackingOverlaySciFi);
        Assert.False(vm.IsTrackingOverlayTechnik);
        Assert.Contains(nameof(AdminViewModel.IsTrackingOverlayTechnik), raised);
        Assert.Contains(nameof(AdminViewModel.IsTrackingOverlaySciFi), raised);

        // Abwählen allein ändert nichts (die Wahlkarte meldet es beim Umschalten).
        vm.IsTrackingOverlaySciFi = false;
        Assert.Equal(TrackingOverlayStyle.SciFi, vm.Draft.TrackingOverlay.Style);

        vm.IsTrackingOverlayTechnik = true;
        Assert.Equal(TrackingOverlayStyle.Technik, vm.Draft.TrackingOverlay.Style);
    }

    [Fact]
    public void ScaleText_FollowsTheValue()
    {
        var vm = CreateViewModel();
        var raised = new ConcurrentQueue<string?>();
        vm.PropertyChanged += (_, e) => raised.Enqueue(e.PropertyName);

        vm.TrackingOverlayScalePercent = 70;

        Assert.Equal("70 %", vm.TrackingOverlayScaleText);
        Assert.Contains(nameof(AdminViewModel.TrackingOverlayScaleText), raised);
    }

    [Fact]
    public void Validate_RejectsAnOutOfRangeScale_AndAcceptsTheLimits()
    {
        var vm = CreateViewModel();
        Assert.DoesNotContain(vm.Validate(), e => e.Contains("Tracking-Overlay"));

        vm.Draft.TrackingOverlay.ScalePercent = SettingsLimits.TrackingOverlayScaleMin - 1;
        Assert.Contains(vm.Validate(), e => e.Contains("Größe des Tracking-Overlays"));

        vm.Draft.TrackingOverlay.ScalePercent = SettingsLimits.TrackingOverlayScaleMax + 1;
        Assert.Contains(vm.Validate(), e => e.Contains("Größe des Tracking-Overlays"));

        vm.Draft.TrackingOverlay.ScalePercent = SettingsLimits.TrackingOverlayScaleMin;
        Assert.DoesNotContain(vm.Validate(), e => e.Contains("Tracking-Overlay"));
        vm.Draft.TrackingOverlay.ScalePercent = SettingsLimits.TrackingOverlayScaleMax;
        Assert.DoesNotContain(vm.Validate(), e => e.Contains("Tracking-Overlay"));
    }

    [Fact]
    public void InvalidScale_BlocksSaving()
    {
        var vm = CreateViewModel();
        vm.Draft.TrackingOverlay.ScalePercent = SettingsLimits.TrackingOverlayScaleMax + 10;

        vm.SaveCommand.Execute(null);

        Assert.Equal(0, _store.SaveCount);
        Assert.Contains("Größe des Tracking-Overlays", vm.ValidationMessage);
    }

    [Fact]
    public async Task ResetToDefaults_RestoresTheOverlaySettingsAndTheKey()
    {
        _store.Current.Input.TrackingOverlayKeys = ["F10"];
        _store.Current.TrackingOverlay.ShowFaces = false;
        _store.Current.TrackingOverlay.Style = TrackingOverlayStyle.SciFi;
        _store.Current.TrackingOverlay.ScalePercent = 200;
        var vm = CreateViewModel();
        Assert.Equal(["F10"], Tracking(vm).Keys.Select(k => k.Chord.ToString()));
        _ui.ConfirmResult = true;

        await vm.ResetToDefaultsAsync();

        Assert.True(vm.TrackingOverlayShowFaces);
        Assert.True(vm.IsTrackingOverlayTechnik);
        Assert.Equal(SettingsLimits.TrackingOverlayScaleDefault, vm.TrackingOverlayScalePercent);
        Assert.Equal([InputSettings.DefaultTrackingOverlayKey], Tracking(vm).Keys.Select(k => k.Chord.ToString()));
        Assert.Equal([InputSettings.DefaultTrackingOverlayKey], vm.Draft.Input.TrackingOverlayKeys);
    }

    // ---- Taste

    [Fact]
    public void KeyEditor_Exists_NotRequired_WithDefaultCtrlAltX()
    {
        var vm = CreateViewModel();

        var editor = Tracking(vm);
        Assert.Contains(editor, vm.KeyEditors);
        Assert.Equal("Tracking-Overlay ein/aus", editor.Title);
        Assert.False(editor.IsRequired);
        Assert.Equal(["Strg+Alt+X"], editor.Keys.Select(k => k.DisplayName));
        Assert.Equal(["Ctrl+Alt+X"], vm.Draft.Input.TrackingOverlayKeys);
    }

    [Fact]
    public void KeyEditor_LoadsStoredKeys()
    {
        _store.Current.Input.TrackingOverlayKeys = ["F9", "Ctrl+F9"];

        var vm = CreateViewModel();

        Assert.Equal(["F9", "Ctrl+F9"], Tracking(vm).Keys.Select(k => k.Chord.ToString()));
    }

    [Fact]
    public void KeyEditor_StoredKeyHeldByAnotherAction_IsDroppedWhenLoading()
    {
        _store.Current.Input.VideoStartKeys = ["Ctrl+Alt+X"];

        var vm = CreateViewModel();

        Assert.Empty(Tracking(vm).Keys);
        Assert.Contains("noch keine Taste", vm.TrackingOverlayKeyHint);
        Assert.Equal("Aus", vm.TrackingOverlayStatusText);
    }

    [Fact]
    public void CaptureKey_AddsToTheDraft_AndSavesIntoTheStore()
    {
        var vm = CreateViewModel();
        vm.BeginKeyCapture(KeyAction.TrackingOverlay);

        Assert.True(vm.CaptureKey(Chord(Key.F9)));

        Assert.False(vm.HasKeyMessage);
        Assert.Equal(["Ctrl+Alt+X", "F9"], vm.Draft.Input.TrackingOverlayKeys);

        vm.ApplyCommand.Execute(null);
        Assert.Equal(["Ctrl+Alt+X", "F9"], _store.Current.Input.TrackingOverlayKeys);
        // Die Taste der Leistungsanzeige bleibt unberührt.
        Assert.Equal(["Ctrl+Alt+Y"], _store.Current.Input.PerformanceOverlayKeys);
    }

    [Fact]
    public void RemovingTheLastKey_IsAllowed_AndSyncsAnEmptyList()
    {
        var vm = CreateViewModel();
        var editor = Tracking(vm);
        Assert.True(editor.Keys[0].RemoveCommand.CanExecute(null));

        editor.Keys[0].RemoveCommand.Execute(null);

        Assert.Empty(vm.Draft.Input.TrackingOverlayKeys);
        Assert.Contains("noch keine Taste", vm.TrackingOverlayKeyHint);
        Assert.Equal("Aus", vm.TrackingOverlayStatusText);
        vm.ApplyCommand.Execute(null);
        Assert.Empty(_store.Current.Input.TrackingOverlayKeys);
    }

    [Theory]
    [InlineData(Key.Space, ModifierKeys.None, "Foto auslösen")]
    [InlineData(Key.Escape, ModifierKeys.None, "Abbrechen")]
    [InlineData(Key.A, ModifierKeys.Control | ModifierKeys.Alt, "Admin-Ansicht öffnen")]
    [InlineData(Key.Enter, ModifierKeys.None, "Video starten")]
    [InlineData(Key.Y, ModifierKeys.Control | ModifierKeys.Alt, "Leistungsanzeige ein/aus")]
    public void CaptureKey_KeyOfAnotherAction_IsRejected(Key key, ModifierKeys modifiers, string ownerTitle)
    {
        var vm = CreateViewModel();
        vm.BeginKeyCapture(KeyAction.TrackingOverlay);

        Assert.False(vm.CaptureKey(Chord(key, modifiers)));

        Assert.Contains(ownerTitle, vm.KeyMessage);
        Assert.True(vm.IsKeyCaptureActive);
        Assert.Equal(["Ctrl+Alt+X"], vm.Draft.Input.TrackingOverlayKeys);
    }

    [Theory]
    [InlineData(KeyAction.Trigger)]
    [InlineData(KeyAction.Cancel)]
    [InlineData(KeyAction.VideoStart)]
    [InlineData(KeyAction.VideoStop)]
    [InlineData(KeyAction.PerformanceOverlay)]
    public void OtherActions_CannotTakeTheTrackingKey(KeyAction target)
    {
        var vm = CreateViewModel();
        vm.BeginKeyCapture(target);

        Assert.False(vm.CaptureKey(Chord(Key.X, ModifierKeys.Control | ModifierKeys.Alt)));

        Assert.Contains("Tracking-Overlay ein/aus", vm.KeyMessage);
    }

    [Fact]
    public void CaptureKey_SameKeyTwice_IsRejected()
    {
        var vm = CreateViewModel();
        vm.BeginKeyCapture(KeyAction.TrackingOverlay);

        Assert.False(vm.CaptureKey(Chord(Key.X, ModifierKeys.Control | ModifierKeys.Alt)));

        Assert.Contains("bereits bei „Tracking-Overlay ein/aus“", vm.KeyMessage);
    }

    [Fact]
    public void ResetKeys_RestoresTheTrackingDefault()
    {
        var vm = CreateViewModel();
        Tracking(vm).Keys[0].RemoveCommand.Execute(null);
        vm.BeginKeyCapture(KeyAction.TrackingOverlay);
        Assert.True(vm.CaptureKey(Chord(Key.F11)));
        vm.CancelKeyCapture();

        vm.ResetKeysCommand.Execute(null);

        Assert.Equal(["Ctrl+Alt+X"], Tracking(vm).Keys.Select(k => k.Chord.ToString()));
        Assert.Equal(["Ctrl+Alt+X"], vm.Draft.Input.TrackingOverlayKeys);
    }

    [Fact]
    public void KeyHint_AndStatus_NameTheCurrentKeys()
    {
        var vm = CreateViewModel();
        var raised = new ConcurrentQueue<string?>(); // Hintergrund-Meldungen des ViewModels dürfen mitlaufen
        vm.PropertyChanged += (_, e) => raised.Enqueue(e.PropertyName);
        Assert.Contains("Strg+Alt+X", vm.TrackingOverlayKeyHint);
        Assert.Equal("Strg+Alt+X", vm.TrackingOverlayStatusText);

        vm.BeginKeyCapture(KeyAction.TrackingOverlay);
        Assert.True(vm.CaptureKey(Chord(Key.F9)));

        Assert.Contains("Strg+Alt+X oder F9", vm.TrackingOverlayKeyHint);
        Assert.Contains(nameof(AdminViewModel.TrackingOverlayKeyHint), raised);
        Assert.Contains(nameof(AdminViewModel.TrackingOverlayStatusText), raised);
    }

    // ---- Passivität: die Seite startet keine Erkennung

    [Fact]
    public void TrackingPage_StartsNoDetection_AndLeavesNoPreviewState()
    {
        var vm = CreateViewModel(withServices: true);
        vm.IsFunTabSelected = true;

        vm.FunPage = FunPage.TrackingOverlay;

        Assert.Empty(_detection.Requests);
        Assert.Empty(_gestures.Requests);
        Assert.False(vm.FunPreview.IsActive);
        Assert.Equal(PeaceMode.Off, vm.PeacePreview.Mode);

        // Einstellungen zu ändern startet ebenfalls nichts.
        vm.TrackingOverlayShowVerdicts = true;
        vm.TrackingOverlayShowHands = false;
        vm.IsTrackingOverlaySciFi = true;
        Assert.Empty(_detection.Requests);
        Assert.Empty(_gestures.Requests);
    }

    [Fact]
    public void LeavingAnActivePreviewPage_ForTheTrackingPage_EndsThePreview()
    {
        var vm = CreateViewModel(withServices: true);
        vm.IsFunTabSelected = true;
        vm.FunPage = FunPage.Peace;
        Assert.Equal(1, _gestures.OpenRequests);
        vm.FunPage = FunPage.Persons;
        Assert.Equal(0, _gestures.OpenRequests);
        Assert.Equal(1, _detection.OpenRequests);

        vm.FunPage = FunPage.TrackingOverlay;

        Assert.Equal(0, _gestures.OpenRequests);
        Assert.Equal(0, _detection.OpenRequests);
        Assert.False(vm.FunPreview.IsActive);
        Assert.Equal(PeaceMode.Off, vm.PeacePreview.Mode);
    }

    // ---- Ansicht

    private static FrameworkElement ShowFunTab(AdminWindow window)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1100, 750));
        root.Arrange(new Rect(0, 0, 1100, 750));
        root.UpdateLayout();
        var tabs = FindDescendants<TabControl>(root).First();
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header as string == "Fun");
        root.UpdateLayout();
        return root;
    }

    private static TrackingOverlaySettingsView TrackingView(AdminWindow window) => (TrackingOverlaySettingsView)window.FindName("TrackingOverlayPage");

    private static T InView<T>(TrackingOverlaySettingsView view, string name) where T : class =>
        view.FindName(name) as T ?? throw new InvalidOperationException(name + " nicht gefunden.");

    [StaFact]
    public void StartPage_ShowsTheTrackingTile_WithTheKeyAsStatus()
    {
        var vm = CreateViewModel();
        var window = new AdminWindow(vm) { ShowInTaskbar = false };
        try
        {
            ShowFunTab(window);
            var start = (UIElement)window.FindName("FunStartPage");
            var tile = (Button)window.FindName("FunTileTrackingOverlay");

            Assert.Equal(Visibility.Visible, start.Visibility);
            Assert.Equal(Visibility.Collapsed, TrackingView(window).Visibility);
            Assert.Equal("Tracking-Overlay", AutomationProperties.GetName(tile));
            Assert.Same(window.TryFindResource("FunTile"), tile.Style);
            Assert.Contains(FindDescendants<TextBlock>(tile), t => t.Text == "Tracking-Overlay");
            Assert.Contains(FindDescendants<TextBlock>(tile), t => t.Text == "Strg+Alt+X");

            Tracking(vm).Keys[0].RemoveCommand.Execute(null);
            Assert.Contains(FindDescendants<TextBlock>(tile), t => t.Text == "Aus");
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void Tile_NavigatesToThePage_AndTheBackButtonReturns()
    {
        var vm = CreateViewModel();
        var window = new AdminWindow(vm) { ShowInTaskbar = false };
        try
        {
            ShowFunTab(window);
            var tile = (Button)window.FindName("FunTileTrackingOverlay");

            Assert.True(tile.Command.CanExecute(tile.CommandParameter));
            tile.Command.Execute(tile.CommandParameter);

            Assert.Equal(FunPage.TrackingOverlay, vm.FunPage);
            Assert.Equal(Visibility.Visible, TrackingView(window).Visibility);
            Assert.Equal(Visibility.Collapsed, ((UIElement)window.FindName("FunStartPage")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((UIElement)window.FindName("PeacePage")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((UIElement)window.FindName("MiddleFingerPage")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((UIElement)window.FindName("HeartPage")).Visibility);

            var back = InView<Button>(TrackingView(window), "TrackingOverlayBackButton");
            back.Command.Execute(back.CommandParameter);
            Assert.Equal(FunPage.Start, vm.FunPage);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void Page_BindsSwitchesStyleAndScaleToTheDraft()
    {
        var vm = CreateViewModel();
        var window = new AdminWindow(vm) { ShowInTaskbar = false };
        try
        {
            var root = ShowFunTab(window);
            vm.FunPage = FunPage.TrackingOverlay;
            root.UpdateLayout();
            var view = TrackingView(window);

            var layers = new[] { "TrackingOverlayPersonsCheck", "TrackingOverlayFacesCheck", "TrackingOverlayHandsCheck",
                "TrackingOverlayGesturesCheck", "TrackingOverlayMoustachesCheck", "TrackingOverlayLabelsCheck", "TrackingOverlayStatusLineCheck" };
            Assert.All(layers, n => Assert.True(InView<CheckBox>(view, n).IsChecked));
            Assert.False(InView<CheckBox>(view, "TrackingOverlayVerdictsCheck").IsChecked);

            var slider = InView<Slider>(view, "TrackingOverlayScaleSlider");
            Assert.Equal(SettingsLimits.TrackingOverlayScaleMin, slider.Minimum);
            Assert.Equal(SettingsLimits.TrackingOverlayScaleMax, slider.Maximum);
            Assert.Equal(SettingsLimits.TrackingOverlayScaleStep, slider.TickFrequency);
            Assert.Equal(SettingsLimits.TrackingOverlayScaleDefault, slider.Value);

            Assert.True(InView<RadioButton>(view, "TrackingOverlayStyleTechnik").IsChecked);
            Assert.False(InView<RadioButton>(view, "TrackingOverlayStyleSciFi").IsChecked);

            // Klicks schreiben in den Entwurf.
            InView<RadioButton>(view, "TrackingOverlayStyleSciFi").IsChecked = true;
            InView<CheckBox>(view, "TrackingOverlayHandsCheck").IsChecked = false;
            InView<CheckBox>(view, "TrackingOverlayVerdictsCheck").IsChecked = true;
            slider.Value = 130;
            Assert.Equal(TrackingOverlayStyle.SciFi, vm.Draft.TrackingOverlay.Style);
            Assert.False(vm.Draft.TrackingOverlay.ShowHands);
            Assert.True(vm.Draft.TrackingOverlay.ShowVerdicts);
            Assert.Equal(130, vm.Draft.TrackingOverlay.ScalePercent);

            Assert.Contains("Strg+Alt+X", InView<TextBlock>(view, "TrackingOverlayKeyHintText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [StaTheory]
    [InlineData(AdminThemeMode.Light)]
    [InlineData(AdminThemeMode.Dark)]
    public void Page_RendersInBothPalettes(AdminThemeMode mode)
    {
        var vm = CreateViewModel();
        var window = new AdminWindow(vm) { ShowInTaskbar = false };
        try
        {
            vm.IsAdminThemeLight = mode == AdminThemeMode.Light;
            vm.IsAdminThemeDark = mode == AdminThemeMode.Dark;
            var root = ShowFunTab(window);
            vm.FunPage = FunPage.TrackingOverlay;
            root.UpdateLayout();

            var view = TrackingView(window);
            Assert.Equal(Visibility.Visible, view.Visibility);
            Assert.True(view.ActualWidth > 0 && view.ActualHeight > 0);
        }
        finally
        {
            window.Close();
        }
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
}
