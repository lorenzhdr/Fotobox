using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Fotobox.Core;
using Fotobox.ViewModels.Admin;
using Fotobox.Views.Admin;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Admin, Tab „Leistung“, Karte „Leistungsanzeige“ und Tastenaktion „Leistungsanzeige ein/aus“ im Tab „Tasten“:
/// Entwurf, Texte, Validierung, Speichern, Tasten (Laden, Schreiben, Konflikte) und Ansicht.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class AdminPerformanceOverlayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-perfoverlay-" + Guid.NewGuid().ToString("N"));
    private readonly AdminViewModelTests.FakeStore _store;
    private readonly AdminViewModelTests.FakePinService _pin;
    private readonly List<AdminViewModel> _created = [];

    public AdminPerformanceOverlayTests()
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

    private AdminViewModel CreateViewModel()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var vm = new AdminViewModel(_store, _pin, new AdminViewModelTests.FakeCamera(), new AdminViewModelTests.FakePrint(),
            new AdminViewModelTests.FakeUi(), null, new AdminViewModelTests.FakeDisplays(AdminViewModelTests.AdminMonitors.Primary));
        _created.Add(vm);
        return vm;
    }

    private static KeyActionEditor Overlay(AdminViewModel vm) => vm.KeyEditors.Single(e => e.Action == KeyAction.PerformanceOverlay);

    private static KeyChord Chord(Key key, ModifierKeys modifiers = ModifierKeys.None) => new(key, modifiers);

    // ---- Karte (Entwurf)

    [Fact]
    public void Defaults_MatchTheSettingsDefaults()
    {
        var vm = CreateViewModel();

        Assert.True(vm.PerformanceOverlayShowCpu && vm.PerformanceOverlayShowCpuClock && vm.PerformanceOverlayShowCpuTemperature);
        Assert.True(vm.PerformanceOverlayShowGpu && vm.PerformanceOverlayShowGpuClock && vm.PerformanceOverlayShowGpuTemperature);
        Assert.True(vm.PerformanceOverlayShowGpuMemory && vm.PerformanceOverlayShowNpu && vm.PerformanceOverlayShowRam);
        Assert.True(vm.PerformanceOverlayShowCameraFps && vm.PerformanceOverlayShowDisplayFps);
        Assert.True(vm.PerformanceOverlayShowAnalysisRate && vm.PerformanceOverlayShowCycleTime && vm.PerformanceOverlayShowModelDevices);
        Assert.True(vm.IsPerformanceOverlayText);
        Assert.False(vm.IsPerformanceOverlayBars);
        Assert.Equal(1000, vm.PerformanceOverlayRefreshMs);
        Assert.Equal(18, vm.PerformanceOverlayFontSize);
        Assert.Equal(60, vm.PerformanceOverlayBackgroundOpacity);
        Assert.Equal("1,0 s", vm.PerformanceOverlayRefreshText);
        Assert.Equal("18 px", vm.PerformanceOverlayFontSizeText);
        Assert.Equal("60 %", vm.PerformanceOverlayBackgroundOpacityText);
    }

    [Fact]
    public void Edits_RoundtripIntoTheDraft_NotTheStore_AndCountAsUnsavedChanges_WithoutRestartHint()
    {
        var vm = CreateViewModel();
        Assert.False(vm.HasUnsavedChanges);

        vm.PerformanceOverlayShowCpu = false;
        vm.PerformanceOverlayShowCpuClock = false;
        vm.PerformanceOverlayShowCpuTemperature = false;
        vm.PerformanceOverlayShowGpu = false;
        vm.PerformanceOverlayShowGpuClock = false;
        vm.PerformanceOverlayShowGpuTemperature = false;
        vm.PerformanceOverlayShowGpuMemory = false;
        vm.PerformanceOverlayShowNpu = false;
        vm.PerformanceOverlayShowRam = false;
        vm.PerformanceOverlayShowCameraFps = false;
        vm.PerformanceOverlayShowDisplayFps = false;
        vm.PerformanceOverlayShowAnalysisRate = false;
        vm.PerformanceOverlayShowCycleTime = false;
        vm.PerformanceOverlayShowModelDevices = false;
        vm.IsPerformanceOverlayBars = true;
        vm.PerformanceOverlayRefreshMs = 2500;
        vm.PerformanceOverlayFontSize = 24;
        vm.PerformanceOverlayBackgroundOpacity = 35;

        Assert.True(vm.HasUnsavedChanges);
        var d = vm.Draft.PerformanceOverlay;
        Assert.False(d.ShowCpu || d.ShowCpuClock || d.ShowCpuTemperature || d.ShowGpu || d.ShowGpuClock || d.ShowGpuTemperature
                     || d.ShowGpuMemory || d.ShowNpu || d.ShowRam || d.ShowCameraFps || d.ShowDisplayFps
                     || d.ShowAnalysisRate || d.ShowCycleTime || d.ShowModelDevices);
        Assert.Equal(PerformanceOverlayStyle.Bars, d.Style);
        Assert.Equal((2500, 24, 35), (d.RefreshMs, d.FontSize, d.BackgroundOpacityPercent));
        Assert.True(_store.Current.PerformanceOverlay.ShowCpu);
        Assert.Equal(1000, _store.Current.PerformanceOverlay.RefreshMs);
        // Die Karte betrifft nur die Anzeige: kein Neustart-Hinweis der Hardwarebeschleunigung.
        Assert.False(vm.HasPerformanceRestartHint);
    }

    [Fact]
    public void Save_WritesTheOverlaySettingsToTheStore()
    {
        var vm = CreateViewModel();
        vm.PerformanceOverlayShowNpu = false;
        vm.IsPerformanceOverlayBars = true;
        vm.PerformanceOverlayRefreshMs = 3000;
        vm.PerformanceOverlayFontSize = 30;
        vm.PerformanceOverlayBackgroundOpacity = 100;

        vm.ApplyCommand.Execute(null);

        var s = _store.Current.PerformanceOverlay;
        Assert.False(s.ShowNpu);
        Assert.True(s.ShowCpu);
        Assert.Equal(PerformanceOverlayStyle.Bars, s.Style);
        Assert.Equal((3000, 30, 100), (s.RefreshMs, s.FontSize, s.BackgroundOpacityPercent));
    }

    [Fact]
    public void Style_Choices_AreMutuallyExclusive_AndNotifyBothProperties()
    {
        var vm = CreateViewModel();
        var raised = new ConcurrentQueue<string?>(); // Hintergrund-Meldungen des ViewModels dürfen mitlaufen
        vm.PropertyChanged += (_, e) => raised.Enqueue(e.PropertyName);

        vm.IsPerformanceOverlayBars = true;
        Assert.True(vm.IsPerformanceOverlayBars);
        Assert.False(vm.IsPerformanceOverlayText);
        Assert.Contains(nameof(AdminViewModel.IsPerformanceOverlayText), raised);
        Assert.Contains(nameof(AdminViewModel.IsPerformanceOverlayBars), raised);

        // Abwählen allein ändert nichts (die Wahlkarte meldet es beim Umschalten).
        vm.IsPerformanceOverlayBars = false;
        Assert.Equal(PerformanceOverlayStyle.Bars, vm.Draft.PerformanceOverlay.Style);

        vm.IsPerformanceOverlayText = true;
        Assert.Equal(PerformanceOverlayStyle.Text, vm.Draft.PerformanceOverlay.Style);
    }

    [Fact]
    public void Texts_FollowTheValues()
    {
        var vm = CreateViewModel();
        var raised = new ConcurrentQueue<string?>(); // Hintergrund-Meldungen des ViewModels dürfen mitlaufen
        vm.PropertyChanged += (_, e) => raised.Enqueue(e.PropertyName);

        vm.PerformanceOverlayRefreshMs = 1500;
        vm.PerformanceOverlayFontSize = 20;
        vm.PerformanceOverlayBackgroundOpacity = 5;

        Assert.Equal("1,5 s", vm.PerformanceOverlayRefreshText);
        Assert.Equal("20 px", vm.PerformanceOverlayFontSizeText);
        Assert.Equal("5 %", vm.PerformanceOverlayBackgroundOpacityText);
        Assert.Contains(nameof(AdminViewModel.PerformanceOverlayRefreshText), raised);
        Assert.Contains(nameof(AdminViewModel.PerformanceOverlayFontSizeText), raised);
        Assert.Contains(nameof(AdminViewModel.PerformanceOverlayBackgroundOpacityText), raised);
    }

    [Fact]
    public void Validate_RejectsOutOfRangeValues_AndAcceptsTheLimits()
    {
        var vm = CreateViewModel();
        Assert.DoesNotContain(vm.Validate(), e => e.Contains("Leistungsanzeige"));

        vm.Draft.PerformanceOverlay.RefreshMs = SettingsLimits.PerformanceOverlayRefreshMsMin - 1;
        vm.Draft.PerformanceOverlay.FontSize = SettingsLimits.PerformanceOverlayFontSizeMax + 1;
        vm.Draft.PerformanceOverlay.BackgroundOpacityPercent = SettingsLimits.PerformanceOverlayBackgroundOpacityMax + 1;
        var errors = vm.Validate();

        Assert.Contains(errors, e => e.Contains("Aktualisierung der Leistungsanzeige"));
        Assert.Contains(errors, e => e.Contains("Schriftgröße der Leistungsanzeige"));
        Assert.Contains(errors, e => e.Contains("Hintergrund-Deckkraft der Leistungsanzeige"));

        vm.Draft.PerformanceOverlay.RefreshMs = SettingsLimits.PerformanceOverlayRefreshMsMax;
        vm.Draft.PerformanceOverlay.FontSize = SettingsLimits.PerformanceOverlayFontSizeMin;
        vm.Draft.PerformanceOverlay.BackgroundOpacityPercent = SettingsLimits.PerformanceOverlayBackgroundOpacityMin;
        Assert.DoesNotContain(vm.Validate(), e => e.Contains("Leistungsanzeige"));
    }

    // ---- Taste

    [Fact]
    public void KeyEditor_Exists_NotRequired_WithDefaultCtrlAltY()
    {
        var vm = CreateViewModel();

        var editor = Overlay(vm);
        Assert.Contains(editor, vm.KeyEditors);
        Assert.Equal("Leistungsanzeige ein/aus", editor.Title);
        Assert.False(editor.IsRequired);
        Assert.Equal(["Strg+Alt+Y"], editor.Keys.Select(k => k.DisplayName));
        Assert.Equal(["Ctrl+Alt+Y"], vm.Draft.Input.PerformanceOverlayKeys);
    }

    [Fact]
    public void KeyEditor_LoadsStoredKeys()
    {
        _store.Current.Input.PerformanceOverlayKeys = ["F10", "Ctrl+F10"];

        var vm = CreateViewModel();

        Assert.Equal(["F10", "Ctrl+F10"], Overlay(vm).Keys.Select(k => k.Chord.ToString()));
    }

    [Fact]
    public void KeyEditor_StoredKeyHeldByVideo_IsDroppedWhenLoading()
    {
        _store.Current.Input.VideoStartKeys = ["Ctrl+Alt+Y"];

        var vm = CreateViewModel();

        Assert.Empty(Overlay(vm).Keys);
        Assert.Equal(["Ctrl+Alt+Y"], vm.KeyEditors.Single(e => e.Action == KeyAction.VideoStart).Keys.Select(k => k.Chord.ToString()));
        Assert.Contains("noch keine Taste", vm.PerformanceOverlayKeyHint);
    }

    [Fact]
    public void CaptureKey_AddsToTheDraft_AndSavesIntoTheStore()
    {
        var vm = CreateViewModel();
        vm.BeginKeyCapture(KeyAction.PerformanceOverlay);

        Assert.True(vm.CaptureKey(Chord(Key.F10)));

        Assert.False(vm.HasKeyMessage);
        Assert.Equal(["Ctrl+Alt+Y", "F10"], vm.Draft.Input.PerformanceOverlayKeys);

        vm.ApplyCommand.Execute(null);
        Assert.Equal(["Ctrl+Alt+Y", "F10"], _store.Current.Input.PerformanceOverlayKeys);
    }

    [Fact]
    public void RemovingTheLastKey_IsAllowed_AndSyncsAnEmptyList()
    {
        var vm = CreateViewModel();
        var editor = Overlay(vm);
        Assert.True(editor.Keys[0].RemoveCommand.CanExecute(null));

        editor.Keys[0].RemoveCommand.Execute(null);

        Assert.Empty(vm.Draft.Input.PerformanceOverlayKeys);
        Assert.Contains("noch keine Taste", vm.PerformanceOverlayKeyHint);
        vm.ApplyCommand.Execute(null);
        Assert.Empty(_store.Current.Input.PerformanceOverlayKeys);
    }

    [Theory]
    [InlineData(Key.Space, ModifierKeys.None, "Foto auslösen")]
    [InlineData(Key.Escape, ModifierKeys.None, "Abbrechen")]
    [InlineData(Key.A, ModifierKeys.Control | ModifierKeys.Alt, "Admin-Ansicht öffnen")]
    [InlineData(Key.Enter, ModifierKeys.None, "Video starten")]
    public void CaptureKey_KeyOfAnotherAction_IsRejected(Key key, ModifierKeys modifiers, string ownerTitle)
    {
        var vm = CreateViewModel();
        vm.BeginKeyCapture(KeyAction.PerformanceOverlay);

        Assert.False(vm.CaptureKey(Chord(key, modifiers)));

        Assert.Contains(ownerTitle, vm.KeyMessage);
        Assert.True(vm.IsKeyCaptureActive);
        Assert.Equal(["Ctrl+Alt+Y"], vm.Draft.Input.PerformanceOverlayKeys);
    }

    [Theory]
    [InlineData(KeyAction.Trigger)]
    [InlineData(KeyAction.Cancel)]
    [InlineData(KeyAction.VideoStart)]
    [InlineData(KeyAction.VideoStop)]
    public void OtherActions_CannotTakeTheOverlayKey(KeyAction target)
    {
        var vm = CreateViewModel();
        vm.BeginKeyCapture(target);

        Assert.False(vm.CaptureKey(Chord(Key.Y, ModifierKeys.Control | ModifierKeys.Alt)));

        Assert.Contains("Leistungsanzeige ein/aus", vm.KeyMessage);
    }

    [Fact]
    public void CaptureKey_SameKeyTwice_IsRejected()
    {
        var vm = CreateViewModel();
        vm.BeginKeyCapture(KeyAction.PerformanceOverlay);

        Assert.False(vm.CaptureKey(Chord(Key.Y, ModifierKeys.Control | ModifierKeys.Alt)));

        Assert.Contains("bereits bei „Leistungsanzeige ein/aus“", vm.KeyMessage);
    }

    [Fact]
    public void ResetKeys_RestoresTheOverlayDefault()
    {
        var vm = CreateViewModel();
        var editor = Overlay(vm);
        editor.Keys[0].RemoveCommand.Execute(null);
        vm.BeginKeyCapture(KeyAction.PerformanceOverlay);
        Assert.True(vm.CaptureKey(Chord(Key.F11)));
        vm.CancelKeyCapture();

        vm.ResetKeysCommand.Execute(null);

        Assert.Equal(["Ctrl+Alt+Y"], Overlay(vm).Keys.Select(k => k.Chord.ToString()));
        Assert.Equal(["Ctrl+Alt+Y"], vm.Draft.Input.PerformanceOverlayKeys);
    }

    [Fact]
    public void KeyHint_NamesTheCurrentKeys()
    {
        var vm = CreateViewModel();
        var raised = new ConcurrentQueue<string?>(); // Hintergrund-Meldungen des ViewModels dürfen mitlaufen
        vm.PropertyChanged += (_, e) => raised.Enqueue(e.PropertyName);
        Assert.Contains("Strg+Alt+Y", vm.PerformanceOverlayKeyHint);

        vm.BeginKeyCapture(KeyAction.PerformanceOverlay);
        Assert.True(vm.CaptureKey(Chord(Key.F10)));

        Assert.Contains("Strg+Alt+Y oder F10", vm.PerformanceOverlayKeyHint);
        Assert.Contains(nameof(AdminViewModel.PerformanceOverlayKeyHint), raised);
    }

    // ---- Ansicht

    [StaFact]
    public void PerformanceTab_ShowsTheOverlayCard_WithAllSwitchesBoundToTheDraft()
    {
        var vm = CreateViewModel();
        var window = new AdminWindow(vm) { ShowInTaskbar = false };
        try
        {
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(1100, 750));
            root.Arrange(new Rect(0, 0, 1100, 750));
            root.UpdateLayout();
            var tabs = FindDescendants<TabControl>(root).First();
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header as string == "Leistung");
            root.UpdateLayout();

            var card = (Border)window.FindName("PerformanceOverlayCard");
            Assert.NotNull(card);
            var checks = FindDescendants<CheckBox>(card).ToList();
            Assert.Equal(14, checks.Count);
            Assert.All(checks, c => Assert.True(c.IsChecked));
            Assert.Equal(3, FindDescendants<Slider>(card).Count());   // Aktualisierung, Schriftgröße, Hintergrund-Deckkraft
            var styleCards = FindDescendants<RadioButton>(card).Where(r => r.GroupName == "PerformanceOverlayStyle").ToList();
            Assert.Equal([true, false], styleCards.Select(r => r.IsChecked == true).ToArray());

            // Klick auf eine Wahlkarte und einen Schalter schreibt in den Entwurf.
            styleCards[1].IsChecked = true;
            checks[0].IsChecked = false;
            Assert.Equal(PerformanceOverlayStyle.Bars, vm.Draft.PerformanceOverlay.Style);
            Assert.False(vm.Draft.PerformanceOverlay.ShowCpu);

            var hint = (TextBlock)window.FindName("PerformanceOverlayKeyHintText");
            Assert.Contains("Strg+Alt+Y", hint.Text);
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
