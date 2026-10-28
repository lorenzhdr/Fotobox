using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Fotobox.Core;
using Fotobox.ViewModels.Admin;
using Fotobox.Views.Admin;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Admin-Tab „Fun“ (Zielerfassung): Durchreicher in den Entwurf, Speichern, Standardwerte, ungespeicherte Änderungen und die Ansicht.</summary>
[Collection(WpfCollection.Name)]
public sealed class AdminFunTabTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-fun-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AdminViewModelTests.FakeStore _store;
    private readonly AdminViewModelTests.FakePinService _pin;
    private readonly AdminViewModelTests.FakeCamera _camera = new();
    private readonly AdminViewModelTests.FakePrint _print = new();
    private readonly AdminViewModelTests.FakeUi _ui = new();
    private readonly AdminViewModelTests.FakeDisplays _displays = new(AdminViewModelTests.AdminMonitors.Primary);

    public AdminFunTabTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "overlays"));
        _store = new AdminViewModelTests.FakeStore(_dir);
        _pin = new AdminViewModelTests.FakePinService(_store);
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

    private AdminViewModel CreateViewModel() => new(_store, _pin, _camera, _print, _ui, null, _displays);

    // ------------------------------------------------------------------ Durchreicher

    [Fact]
    public void Defaults_AreReadFromSettings()
    {
        var vm = CreateViewModel();

        Assert.True(vm.FunEnabled);
        Assert.Equal(DetectionMethod.Body, vm.FunMethod);
        Assert.Equal(TargetScope.FullBody, vm.FunScope);
        Assert.Equal(TargetBoxStyle.Corners, vm.FunBoxStyle);
        Assert.Equal(TargetColor.Red, vm.FunColor);
        Assert.Equal(LockOnAnimation.ShrinkPulse, vm.FunAnimation);
        Assert.Equal(string.Empty, vm.FunSingularText);
        Assert.Equal(string.Empty, vm.FunPluralText);
        Assert.Equal(FunSettings.DefaultSingularText, vm.FunSingularTextPlaceholder);
        Assert.Equal(FunSettings.DefaultPluralText, vm.FunPluralTextPlaceholder);
        Assert.Equal(FunTextPosition.Center, vm.FunTextPosition);
        Assert.Equal(FunTextStyle.Hud, vm.FunTextStyle);
        Assert.False(vm.FunHintStyleVisible);
        Assert.Equal(HintStyle.Pill, vm.SelectedFunHintStyle.Style);
        Assert.Equal(6, vm.FunTextSeconds);
        Assert.Equal(20, vm.FunCooldownSeconds);
        Assert.True(vm.FunSwapTriggers);
        Assert.False(vm.FunRepeatEnabled);
        Assert.Equal(60, vm.FunRepeatSeconds);
        Assert.Equal(15, vm.FunMinSizePercent);
        Assert.Equal(6, vm.FunMaxTargets);
        Assert.Equal(50, vm.FunSensitivityPercent);
        Assert.Equal(800, vm.FunDwellMilliseconds);
        Assert.Equal(5, vm.FunAnalysesPerSecond);
        Assert.False(vm.FunSoundEnabled);
    }

    [Fact]
    public void Setters_WriteIntoTheDraft()
    {
        var vm = CreateViewModel();

        vm.FunEnabled = false;
        vm.FunMethod = DetectionMethod.Face;
        vm.FunScope = TargetScope.UpperBody;
        vm.FunBoxStyle = TargetBoxStyle.CornersCrosshair;
        vm.FunColor = TargetColor.Cyan;
        vm.FunAnimation = LockOnAnimation.ScanPulse;
        vm.FunSingularText = "Hallo du!";
        vm.FunPluralText = "Hallo {Anzahl} Leute!";
        vm.FunTextPosition = FunTextPosition.Top;
        vm.FunTextStyle = FunTextStyle.Hint;
        vm.SelectedFunHintStyle = vm.FunHintStyles.Single(o => o.Style == HintStyle.Neon);
        vm.FunTextSeconds = 12;
        vm.FunCooldownSeconds = 45;
        vm.FunSwapTriggers = false;
        vm.FunRepeatEnabled = true;
        vm.FunRepeatSeconds = 120;
        vm.FunMinSizePercent = 30;
        vm.FunMaxTargets = 3;
        vm.FunSensitivityPercent = 70;
        vm.FunDwellMilliseconds = 1500;
        vm.FunAnalysesPerSecond = 8;
        vm.FunSoundEnabled = true;

        var fun = vm.Draft.Fun;
        Assert.False(fun.Enabled);
        Assert.Equal(DetectionMethod.Face, fun.Method);
        Assert.Equal(TargetScope.UpperBody, fun.Scope);
        Assert.Equal(TargetBoxStyle.CornersCrosshair, fun.BoxStyle);
        Assert.Equal(TargetColor.Cyan, fun.Color);
        Assert.Equal(LockOnAnimation.ScanPulse, fun.Animation);
        Assert.Equal("Hallo du!", fun.SingularText);
        Assert.Equal("Hallo {Anzahl} Leute!", fun.PluralText);
        Assert.Equal(FunTextPosition.Top, fun.TextPosition);
        Assert.Equal(FunTextStyle.Hint, fun.TextStyle);
        Assert.Equal(HintStyle.Neon, fun.TextHintStyle);
        Assert.Equal(12, fun.TextSeconds);
        Assert.Equal(45, fun.CooldownSeconds);
        Assert.False(fun.SwapTriggers);
        Assert.True(fun.RepeatEnabled);
        Assert.Equal(120, fun.RepeatSeconds);
        Assert.Equal(30, fun.MinSizePercent);
        Assert.Equal(3, fun.MaxTargets);
        Assert.Equal(70, fun.SensitivityPercent);
        Assert.Equal(1500, fun.DwellMilliseconds);
        Assert.Equal(8, fun.AnalysesPerSecond);
        Assert.True(fun.SoundEnabled);
    }

    [Fact]
    public void EmptyOrBlankText_IsStoredAsNull_SoTheDefaultTextApplies()
    {
        var vm = CreateViewModel();
        vm.FunSingularText = "Eigener Text";
        vm.FunPluralText = "Noch einer";

        vm.FunSingularText = string.Empty;
        vm.FunPluralText = "   ";

        Assert.Null(vm.Draft.Fun.SingularText);
        Assert.Null(vm.Draft.Fun.PluralText);
    }

    [Fact]
    public void HintStyleList_HasAllNineStylesWithGermanNames_InEnumOrder()
    {
        var vm = CreateViewModel();

        Assert.Equal(Enum.GetValues<HintStyle>(), vm.FunHintStyles.Select(o => o.Style));
        Assert.Equal(
            ["Pille", "Schlicht", "Banner", "Neon", "Sprechblase", "Laufschrift", "Glas", "Zettel", "Kino"],
            vm.FunHintStyles.Select(o => o.Display));
    }

    [Fact]
    public void FunTextStyle_RaisesHintStyleVisibility()
    {
        var vm = CreateViewModel();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.FunTextStyle = FunTextStyle.Hint;

        Assert.True(vm.FunHintStyleVisible);
        Assert.Contains(nameof(AdminViewModel.FunTextStyle), raised);
        Assert.Contains(nameof(AdminViewModel.FunHintStyleVisible), raised);

        vm.FunTextStyle = FunTextStyle.Hud;
        Assert.False(vm.FunHintStyleVisible);
    }

    [Fact]
    public void Setters_RaisePropertyChanged_OnlyOnRealChange()
    {
        var vm = CreateViewModel();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.FunColor = TargetColor.Red;   // unverändert
        Assert.DoesNotContain(nameof(AdminViewModel.FunColor), raised);

        vm.FunColor = TargetColor.Green;
        Assert.Contains(nameof(AdminViewModel.FunColor), raised);
    }

    // ------------------------------------------------------------------ Speichern / Übernehmen

    [Fact]
    public async Task Apply_PersistsFunSettings_AndKeepsTheWindowState()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.FunEnabled = false;
        vm.FunMethod = DetectionMethod.Face;
        vm.FunColor = TargetColor.Yellow;
        vm.FunSingularText = "Hey!";
        vm.FunTextStyle = FunTextStyle.Hint;
        vm.SelectedFunHintStyle = vm.FunHintStyles.Single(o => o.Style == HintStyle.Cinema);
        vm.FunCooldownSeconds = 90;
        vm.FunRepeatEnabled = true;
        vm.FunRepeatSeconds = 45;
        vm.FunSwapTriggers = false;
        vm.FunSoundEnabled = true;

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.True(vm.SettingsSaved);
        var saved = _store.Current.Fun;
        Assert.False(saved.Enabled);
        Assert.Equal(DetectionMethod.Face, saved.Method);
        Assert.Equal(TargetColor.Yellow, saved.Color);
        Assert.Equal("Hey!", saved.SingularText);
        Assert.Null(saved.PluralText);
        Assert.Equal(FunTextStyle.Hint, saved.TextStyle);
        Assert.Equal(HintStyle.Cinema, saved.TextHintStyle);
        Assert.Equal(90, saved.CooldownSeconds);
        Assert.True(saved.RepeatEnabled);
        Assert.Equal(45, saved.RepeatSeconds);
        Assert.False(saved.SwapTriggers);
        Assert.True(saved.SoundEnabled);
        Assert.False(vm.HasUnsavedChanges);
    }

    // ------------------------------------------------------------------ Ungespeicherte Änderungen

    [Fact]
    public void HasUnsavedChanges_ReactsToFunChanges_AndToTheirRevert()
    {
        var vm = CreateViewModel();
        Assert.False(vm.HasUnsavedChanges);

        vm.FunEnabled = false;
        Assert.True(vm.HasUnsavedChanges);
        vm.FunEnabled = true;
        Assert.False(vm.HasUnsavedChanges);

        vm.FunSingularText = "Hallo";
        Assert.True(vm.HasUnsavedChanges);
        vm.FunSingularText = string.Empty;
        Assert.False(vm.HasUnsavedChanges);

        vm.SelectedFunHintStyle = vm.FunHintStyles.Single(o => o.Style == HintStyle.Glass);
        Assert.True(vm.HasUnsavedChanges);
    }

    // ------------------------------------------------------------------ Standardwerte wiederherstellen

    [Fact]
    public async Task ResetToDefaults_RestoresFunSettings_AndKeepsThePin()
    {
        _store.Current.Security.PinHash = "hash:4711";
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.FunEnabled = false;
        vm.FunMethod = DetectionMethod.Face;
        vm.FunScope = TargetScope.Head;
        vm.FunColor = TargetColor.White;
        vm.FunAnimation = LockOnAnimation.Pulse;
        vm.FunSingularText = "Anders";
        vm.FunTextPosition = FunTextPosition.Bottom;
        vm.FunTextStyle = FunTextStyle.Hint;
        vm.SelectedFunHintStyle = vm.FunHintStyles.Single(o => o.Style == HintStyle.Note);
        vm.FunCooldownSeconds = 300;
        vm.FunRepeatEnabled = true;
        vm.FunRepeatSeconds = 200;
        vm.FunSwapTriggers = false;
        vm.FunSoundEnabled = true;
        _ui.ConfirmResult = true;
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await vm.ResetToDefaultsCommand.ExecuteAsync(null);

        var defaults = new FunSettings();
        var fun = vm.Draft.Fun;
        Assert.True(fun.Enabled);
        Assert.Equal(defaults.Method, fun.Method);
        Assert.Equal(defaults.Scope, fun.Scope);
        Assert.Equal(defaults.Color, fun.Color);
        Assert.Equal(defaults.Animation, fun.Animation);
        Assert.Null(fun.SingularText);
        Assert.Equal(defaults.TextPosition, fun.TextPosition);
        Assert.Equal(defaults.TextStyle, fun.TextStyle);
        Assert.Equal(defaults.TextHintStyle, fun.TextHintStyle);
        Assert.Equal(defaults.CooldownSeconds, fun.CooldownSeconds);
        Assert.Equal(defaults.RepeatEnabled, fun.RepeatEnabled);
        Assert.Equal(defaults.RepeatSeconds, fun.RepeatSeconds);
        Assert.Equal(defaults.SwapTriggers, fun.SwapTriggers);
        Assert.False(fun.SoundEnabled);
        Assert.Equal("hash:4711", vm.Draft.Security.PinHash);

        // Die Eigenschaften melden den neuen Stand (UI-Bindungen lesen neu).
        Assert.Contains(string.Empty, raised);
        Assert.True(vm.FunEnabled);
        Assert.Equal(TargetColor.Red, vm.FunColor);
        Assert.Equal(string.Empty, vm.FunSingularText);
        Assert.Equal(HintStyle.Pill, vm.SelectedFunHintStyle.Style);
        Assert.False(vm.FunHintStyleVisible);
        Assert.Equal(FunTextPosition.Center, vm.FunTextPosition);
    }

    [Fact]
    public async Task ResetToDefaults_Declined_KeepsFunSettings()
    {
        var vm = CreateViewModel();
        vm.FunEnabled = false;
        vm.FunColor = TargetColor.Green;
        _ui.ConfirmResult = false;

        await vm.ResetToDefaultsCommand.ExecuteAsync(null);

        Assert.False(vm.Draft.Fun.Enabled);
        Assert.Equal(TargetColor.Green, vm.Draft.Fun.Color);
    }

    // ------------------------------------------------------------------ Ansicht

    private static void WithWindow(Action<AdminWindow, AdminViewModel> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-fun-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new AdminViewModelTests.FakeStore(dir);
            var vm = new AdminViewModel(store, new AdminViewModelTests.FakePinService(store),
                new AdminViewModelTests.FakeCamera(), new AdminViewModelTests.FakePrint(), new AdminViewModelTests.FakeUi());
            // Die Zielerfassung liegt auf der Unterseite „Personenerkennung“ (Startseite mit Kacheln davor).
            vm.FunPage = FunPage.Persons;
            var window = new AdminWindow(vm);
            try
            {
                body(window, vm);
            }
            finally
            {
                vm.Dispose();
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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

    private static FrameworkElement ShowTab(AdminWindow window, string header)
    {
        var tabs = FindTabControl(window);
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header as string == header);
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1100, 750));
        root.Arrange(new Rect(0, 0, 1100, 750));
        root.UpdateLayout();
        return root;
    }

    private static List<RadioButton> Cards(FrameworkElement root, string group) =>
        FindDescendants<RadioButton>(root).Where(r => r.GroupName == group).ToList();

    [StaFact]
    public void AdminWindow_HasFunTab_AsLastTab()
    {
        WithWindow((window, _) =>
        {
            var headers = FindTabControl(window).Items.OfType<TabItem>().Select(t => t.Header as string).ToList();

            Assert.Contains("Fun", headers);
            Assert.Equal(headers.Count - 1, headers.IndexOf("Fun"));
        });
    }

    [StaTheory]
    [InlineData(AdminThemeMode.Light)]
    [InlineData(AdminThemeMode.Dark)]
    public void AdminWindow_FunTab_LaysOutInBothThemes_WithLivePreviewAndAllCards(AdminThemeMode theme)
    {
        WithWindow((window, vm) =>
        {
            vm.IsAdminThemeLight = theme == AdminThemeMode.Light;
            vm.IsAdminThemeDark = theme == AdminThemeMode.Dark;

            var root = ShowTab(window, "Fun");

            Assert.True(root.ActualHeight > 0);
            var host = Assert.IsType<Border>(window.FindName("FunPreviewHost"));
            Assert.Equal(16.0 / 9.0, host.Width / host.Height, 3);
            Assert.NotEmpty(FindDescendants<Fotobox.Views.Controls.CameraPreview>(host));
            Assert.NotEmpty(FindDescendants<Fotobox.Views.Booth.TargetOverlay>(host));
            Assert.NotEmpty(FindDescendants<Fotobox.Views.Booth.FunText>(host));
            Assert.Equal(2, Cards(root, "FunMethod").Count);
            Assert.Equal(3, Cards(root, "FunScope").Count);
            Assert.Equal(3, Cards(root, "FunBoxStyle").Count);
            Assert.Equal(5, Cards(root, "FunColor").Count);
            Assert.Equal(3, Cards(root, "FunAnimation").Count);
            Assert.Equal(3, Cards(root, "FunTextPosition").Count);
            Assert.Equal(2, Cards(root, "FunTextStyle").Count);
            Assert.Equal(3, FindDescendants<TargetStyleSketch>(root).Count());
            Assert.Equal(3, FindDescendants<LockOnSketch>(root).Count());
            // Acht Zahlenfelder: fünf bei „Erkennung“ sowie Anzeigedauer, Pause und Wiederholungsabstand beim Text.
            Assert.Equal(8, FindDescendants<NumberStepper>((DependencyObject)window.FindName("FunPersonsPage")).Count());
            Assert.Contains(FindDescendants<ToggleButton>(root),
                c => System.Windows.Automation.AutomationProperties.GetName(c) == "Personentausch löst aus");
            Assert.Contains(FindDescendants<ToggleButton>(root),
                c => System.Windows.Automation.AutomationProperties.GetName(c) == "Bei gleicher Gruppe wiederholen");
            Assert.Contains(FindDescendants<ToggleButton>(root),
                c => System.Windows.Automation.AutomationProperties.GetName(c) == "Zielerfassung einschalten");
        });
    }

    [StaFact]
    public void AdminWindow_FunTab_IsGreyedOutWhileDisabled()
    {
        WithWindow((window, vm) =>
        {
            ShowTab(window, "Fun");
            var panel = (StackPanel)window.FindName("FunSettingsPanel");

            vm.FunEnabled = false;
            Assert.False(vm.FunEnabled);
            Assert.False(panel.IsEnabled);
            Assert.True(panel.Opacity < 1.0);

            vm.FunEnabled = true;

            Assert.True(panel.IsEnabled);
            Assert.Equal(1.0, panel.Opacity);
        });
    }

    [StaFact]
    public void AdminWindow_FunTab_CardsAndSwatchesWriteIntoTheViewModel()
    {
        WithWindow((window, vm) =>
        {
            var root = ShowTab(window, "Fun");

            Cards(root, "FunMethod").Single(r => System.Windows.Automation.AutomationProperties.GetName(r) == "Gesicht (Windows)").IsChecked = true;
            Cards(root, "FunBoxStyle").Single(r => System.Windows.Automation.AutomationProperties.GetName(r) == "Rechteck").IsChecked = true;
            Cards(root, "FunColor").Single(r => System.Windows.Automation.AutomationProperties.GetName(r) == "Farbe: Gelb").IsChecked = true;
            Cards(root, "FunAnimation").Single(r => System.Windows.Automation.AutomationProperties.GetName(r) == "Pulsieren").IsChecked = true;
            Cards(root, "FunTextPosition").Single(r => System.Windows.Automation.AutomationProperties.GetName(r) == "Position: Oben").IsChecked = true;

            Assert.Equal(DetectionMethod.Face, vm.FunMethod);
            Assert.Equal(TargetBoxStyle.Rectangle, vm.FunBoxStyle);
            Assert.Equal(TargetColor.Yellow, vm.FunColor);
            Assert.Equal(LockOnAnimation.Pulse, vm.FunAnimation);
            Assert.Equal(FunTextPosition.Top, vm.FunTextPosition);

            // Gegenrichtung: Die Karten folgen dem Entwurf.
            vm.FunColor = TargetColor.Cyan;
            Assert.Equal(["Farbe: Türkis"],
                Cards(root, "FunColor").Where(r => r.IsChecked == true).Select(System.Windows.Automation.AutomationProperties.GetName));
        });
    }

    [StaFact]
    public void AdminWindow_FunTab_SketchesFollowTheChosenColor()
    {
        WithWindow((window, vm) =>
        {
            var root = ShowTab(window, "Fun");
            var sketch = FindDescendants<TargetStyleSketch>(root).First();
            Assert.Equal(AdminTargetColorOf(TargetColor.Red), ((SolidColorBrush)sketch.BoxBrush).Color);

            vm.FunColor = TargetColor.Green;

            Assert.Equal(TargetColor.Green, sketch.Color);
            Assert.Equal(AdminTargetColorOf(TargetColor.Green), ((SolidColorBrush)sketch.BoxBrush).Color);
            Assert.All(FindDescendants<LockOnSketch>(root), s => Assert.Equal(TargetColor.Green, s.Color));
        });
    }

    private static Color AdminTargetColorOf(TargetColor color) => color switch
    {
        TargetColor.Green => Color.FromRgb(0x39, 0xFF, 0x14),
        _ => Color.FromRgb(0xFF, 0x3B, 0x30),
    };

    [StaFact]
    public void AdminWindow_FunTab_HintStyleBox_AppearsOnlyForHintStyle_AndTextPlaceholdersFollowEmptiness()
    {
        WithWindow((window, vm) =>
        {
            var root = ShowTab(window, "Fun");
            var combo = FindDescendants<ComboBox>(root)
                .Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Hinweis-Stil des Texts");
            Assert.Equal(Visibility.Collapsed, ((UIElement)combo.Parent).Visibility);
            Assert.Equal(9, combo.Items.Count);

            vm.FunTextStyle = FunTextStyle.Hint;
            Assert.Equal(Visibility.Visible, ((UIElement)combo.Parent).Visibility);
            Assert.Equal("Pille", ((HintStyleOption)combo.SelectedItem).Display);

            var placeholder = FindDescendants<TextBlock>(root).Single(t => t.Text == FunSettings.DefaultSingularText);
            Assert.Equal(Visibility.Visible, placeholder.Visibility);
            vm.FunSingularText = "Eigener Text";
            Assert.Equal(Visibility.Collapsed, placeholder.Visibility);

            var boxes = FindDescendants<TextBox>(root).Where(t => t.MaxLength == SettingsLimits.HintTextMaxLength).ToList();
            Assert.Equal(2, boxes.Count);
        });
    }
}
