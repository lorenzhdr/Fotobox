using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Fotobox.Core;
using Fotobox.ViewModels.Admin;
using Fotobox.Views.Admin;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Admin-Tab „Ablauf“, Karte „Töne“: Entwurf, Anhören mit den Werten des Entwurfs, Validierung, Zurücksetzen und die Ansicht.</summary>
[Collection(WpfCollection.Name)]
public sealed class AdminSoundsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-sounds-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AdminViewModelTests.FakeStore _store;
    private readonly AdminViewModelTests.FakePinService _pin;
    private readonly AdminViewModelTests.FakeUi _ui = new();
    private readonly FakeBoothSoundPlayer _sound = new();
    private readonly TrackingTimeProvider _time = new();
    private readonly List<AdminViewModel> _created = [];

    public AdminSoundsTests()
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
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AdminViewModel CreateViewModel(bool withSound = true)
    {
        // Ohne Synchronisationskontext laufen die Timer-Ticks der Vorschau synchron im Test.
        SynchronizationContext.SetSynchronizationContext(null);
        var vm = new AdminViewModel(_store, _pin, new AdminViewModelTests.FakeCamera(), new AdminViewModelTests.FakePrint(), _ui, _time,
            new AdminViewModelTests.FakeDisplays(AdminViewModelTests.AdminMonitors.Primary),
            boothSound: withSound ? _sound : null);
        _created.Add(vm);
        return vm;
    }

    [Fact]
    public void Properties_ReadFromTheDraft_AndWriteThrough()
    {
        _store.Current.Flow.CountdownBeepSound = CountdownBeepSound.Bright;
        _store.Current.Flow.ShutterVolumePercent = 25;
        var vm = CreateViewModel();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(vm.CountdownBeepEnabled);
        Assert.Equal(CountdownBeepSound.Bright, vm.CountdownBeepSound);
        Assert.Equal(70, vm.CountdownBeepVolumePercent);
        Assert.Equal("70 %", vm.CountdownBeepVolumeText);
        Assert.True(vm.ShutterSoundEnabled);
        Assert.Equal(ShutterSound.Slr, vm.ShutterSound);
        Assert.Equal(25, vm.ShutterVolumePercent);
        Assert.Equal("25 %", vm.ShutterVolumeText);

        vm.CountdownBeepEnabled = false;
        vm.CountdownBeepSound = CountdownBeepSound.WoodBlock;
        vm.CountdownBeepVolumePercent = 35;
        vm.ShutterSoundEnabled = false;
        vm.ShutterSound = ShutterSound.Retro;
        vm.ShutterVolumePercent = 90;

        Assert.False(vm.Draft.Flow.CountdownBeepEnabled);
        Assert.Equal(CountdownBeepSound.WoodBlock, vm.Draft.Flow.CountdownBeepSound);
        Assert.Equal(35, vm.Draft.Flow.CountdownBeepVolumePercent);
        Assert.False(vm.Draft.Flow.ShutterSoundEnabled);
        Assert.Equal(ShutterSound.Retro, vm.Draft.Flow.ShutterSound);
        Assert.Equal(90, vm.Draft.Flow.ShutterVolumePercent);
        Assert.Equal("35 %", vm.CountdownBeepVolumeText);
        Assert.Equal("90 %", vm.ShutterVolumeText);
        Assert.Contains(nameof(AdminViewModel.CountdownBeepVolumeText), changed);
        Assert.Contains(nameof(AdminViewModel.ShutterVolumeText), changed);
        // Der gespeicherte Stand bleibt unberührt, bis gespeichert wird.
        Assert.True(_store.Current.Flow.CountdownBeepEnabled);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public void Options_HaveGermanNames()
    {
        var vm = CreateViewModel();

        Assert.Equal(["Weich", "Hell", "Holzblock"], vm.CountdownBeepSounds.Select(o => o.Name).ToArray());
        Assert.Equal([CountdownBeepSound.Soft, CountdownBeepSound.Bright, CountdownBeepSound.WoodBlock],
            vm.CountdownBeepSounds.Select(o => o.Value).ToArray());
        Assert.Equal(["Spiegelreflex", "Kompaktkamera", "Retro (mit Filmtransport)"], vm.ShutterSounds.Select(o => o.Name).ToArray());
        Assert.Equal([ShutterSound.Slr, ShutterSound.Compact, ShutterSound.Retro], vm.ShutterSounds.Select(o => o.Value).ToArray());
    }

    [Fact]
    public void Changes_AreUnsavedChanges_AndAreSaved()
    {
        var vm = CreateViewModel();
        Assert.False(vm.HasUnsavedChanges);

        vm.ShutterSound = ShutterSound.Compact;
        Assert.True(vm.HasUnsavedChanges);
        vm.CountdownBeepVolumePercent = 45;
        vm.SaveCommand.Execute(null);

        Assert.Equal(ShutterSound.Compact, _store.Current.Flow.ShutterSound);
        Assert.Equal(45, _store.Current.Flow.CountdownBeepVolumePercent);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(105)]
    public void VolumeOutOfRange_IsRejected(int value)
    {
        var vm = CreateViewModel();
        vm.Draft.Flow.CountdownBeepVolumePercent = value;
        vm.Draft.Flow.ShutterVolumePercent = value;

        var errors = vm.Validate();

        Assert.Contains(errors, e => e.Contains("Countdown-Piepens") && e.Contains("Prozent"));
        Assert.Contains(errors, e => e.Contains("Kamera-Sounds") && e.Contains("Prozent"));
        vm.SaveCommand.Execute(null);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public async Task ResetToDefaults_RestoresTheSelections()
    {
        var vm = CreateViewModel();
        vm.CountdownBeepSound = CountdownBeepSound.WoodBlock;
        vm.ShutterSound = ShutterSound.Retro;
        vm.CountdownBeepVolumePercent = 10;
        vm.ShutterSoundEnabled = false;
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _ui.ConfirmResult = true;

        await vm.ResetToDefaultsAsync();

        Assert.Equal(CountdownBeepSound.Soft, vm.CountdownBeepSound);
        Assert.Equal(ShutterSound.Slr, vm.ShutterSound);
        Assert.Equal(70, vm.CountdownBeepVolumePercent);
        Assert.True(vm.ShutterSoundEnabled);
        // Die Oberfläche liest alles neu (leerer Name = alle Eigenschaften).
        Assert.Contains(string.Empty, changed.Select(c => c ?? string.Empty));
    }

    // ------------------------------------------------------------------ Anhören

    [Fact]
    public void PreviewBeep_PlaysNormalNormalHigh_OneSecondApart_WithoutSaving()
    {
        var vm = CreateViewModel();
        vm.CountdownBeepSound = CountdownBeepSound.Bright;
        vm.CountdownBeepVolumePercent = 40;

        vm.PreviewCountdownBeepCommand.Execute(null);
        Assert.Equal([(BoothSoundCue.BeepBright, 40)], _sound.Played);

        _time.Advance(TimeSpan.FromMilliseconds(900));
        Assert.Single(_sound.Played);
        _time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal([(BoothSoundCue.BeepBright, 40), (BoothSoundCue.BeepBright, 40)], _sound.Played);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(
            [(BoothSoundCue.BeepBright, 40), (BoothSoundCue.BeepBright, 40), (BoothSoundCue.BeepBrightFinal, 40)],
            _sound.Played);

        // Danach ist Schluss und der Timer freigegeben.
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(3, _sound.Played.Count);
        Assert.Equal(0, _time.ActiveTimers);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public void PreviewBeep_PressedAgain_StartsOver()
    {
        var vm = CreateViewModel();

        vm.PreviewCountdownBeepCommand.Execute(null);
        _time.Advance(TimeSpan.FromSeconds(1));
        vm.PreviewCountdownBeepCommand.Execute(null);

        Assert.Equal(3, _sound.Played.Count);
        _time.Advance(TimeSpan.FromSeconds(1));
        _time.Advance(TimeSpan.FromSeconds(1));
        // Zwei Piepe der ersten Folge (normal, normal) und drei der zweiten (normal, normal, hoch) – keine Doppelung durch die alte Folge.
        Assert.Equal(
            [BoothSoundCue.BeepSoft, BoothSoundCue.BeepSoft, BoothSoundCue.BeepSoft, BoothSoundCue.BeepSoft, BoothSoundCue.BeepSoftFinal],
            _sound.PlayedCues);
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public void PreviewBeep_StopsWhenSwitchedOffOrClosed()
    {
        var vm = CreateViewModel();

        vm.PreviewCountdownBeepCommand.Execute(null);
        vm.CountdownBeepEnabled = false;
        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.Single(_sound.Played);
        Assert.Equal(0, _time.ActiveTimers);

        vm.CountdownBeepEnabled = true;
        vm.PreviewCountdownBeepCommand.Execute(null);
        vm.Dispose();
        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(2, _sound.Played.Count);
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public void PreviewShutter_PlaysChosenVariantAtDraftVolume()
    {
        var vm = CreateViewModel();
        vm.ShutterSound = ShutterSound.Retro;
        vm.ShutterVolumePercent = 55;

        vm.PreviewShutterSoundCommand.Execute(null);

        Assert.Equal([(BoothSoundCue.ShutterRetro, 55)], _sound.Played);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public void Preview_WithoutPlayer_DoesNothing_AndThrowingPlayerIsHarmless()
    {
        var vm = CreateViewModel(withSound: false);
        vm.PreviewShutterSoundCommand.Execute(null);
        vm.PreviewCountdownBeepCommand.Execute(null);
        _time.Advance(TimeSpan.FromSeconds(3));

        var vm2 = CreateViewModel();
        _sound.Throw = true;
        vm2.PreviewShutterSoundCommand.Execute(null);
        vm2.PreviewCountdownBeepCommand.Execute(null);
        _time.Advance(TimeSpan.FromSeconds(3));

        Assert.NotEmpty(_sound.Played);
    }

    // ------------------------------------------------------------------ Ansicht

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

    private static TabControl FindTabControl(DependencyObject parent)
    {
        if (parent is TabControl tabs)
            return tabs;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            var found = FindTabControlOrNull(child);
            if (found is not null)
                return found;
        }
        throw new InvalidOperationException("Kein TabControl gefunden.");
    }

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

    private static FrameworkElement ShowFlowTab(AdminWindow window)
    {
        var tabs = FindTabControl(window);
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header as string == "Ablauf");
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1100, 750));
        root.Arrange(new Rect(0, 0, 1100, 750));
        root.UpdateLayout();
        return root;
    }

    [StaFact]
    public void FlowTab_ShowsTheSoundCard_WithBindingsToTheDraft()
    {
        var vm = CreateViewModel();
        var window = new AdminWindow(vm);
        var root = ShowFlowTab(window);

        var texts = FindDescendants<TextBlock>(root).Select(t => t.Text).ToList();
        Assert.Contains("Töne", texts);

        var beepCheck = FindDescendants<CheckBox>(root).Single(c => AutomationProperties.GetName(c) == "Countdown-Piepen");
        var shutterCheck = FindDescendants<CheckBox>(root).Single(c => AutomationProperties.GetName(c) == "Kamera-Sound beim Auslösen");
        Assert.True(beepCheck.IsChecked);
        Assert.True(shutterCheck.IsChecked);

        var beepCombo = FindDescendants<ComboBox>(root).Single(c => AutomationProperties.GetName(c) == "Klang des Countdown-Piepens");
        var shutterCombo = FindDescendants<ComboBox>(root).Single(c => AutomationProperties.GetName(c) == "Klang des Kamera-Sounds");
        Assert.Equal(3, beepCombo.Items.Count);
        Assert.Equal(CountdownBeepSound.Soft, beepCombo.SelectedValue);
        Assert.Equal(ShutterSound.Slr, shutterCombo.SelectedValue);

        var beepSlider = FindDescendants<Slider>(root).Single(s => AutomationProperties.GetName(s) == "Lautstärke des Countdown-Piepens");
        var shutterSlider = FindDescendants<Slider>(root).Single(s => AutomationProperties.GetName(s) == "Lautstärke des Kamera-Sounds");
        Assert.Equal(SettingsLimits.SoundVolumeMin, beepSlider.Minimum);
        Assert.Equal(SettingsLimits.SoundVolumeMax, beepSlider.Maximum);
        Assert.Equal(SettingsLimits.SoundVolumeStep, beepSlider.TickFrequency);
        Assert.Equal(70, beepSlider.Value);
        Assert.Equal(70, shutterSlider.Value);

        // Bindung in beide Richtungen.
        beepCombo.SelectedValue = CountdownBeepSound.WoodBlock;
        shutterCombo.SelectedValue = ShutterSound.Compact;
        beepSlider.Value = 30;
        shutterSlider.Value = 95;
        Assert.Equal(CountdownBeepSound.WoodBlock, vm.Draft.Flow.CountdownBeepSound);
        Assert.Equal(ShutterSound.Compact, vm.Draft.Flow.ShutterSound);
        Assert.Equal(30, vm.Draft.Flow.CountdownBeepVolumePercent);
        Assert.Equal(95, vm.Draft.Flow.ShutterVolumePercent);
        Assert.Contains("30 %", FindDescendants<TextBlock>(root).Select(t => t.Text));
        Assert.Contains("95 %", FindDescendants<TextBlock>(root).Select(t => t.Text));

        // „Anhören“ nutzt die Befehle der Karte.
        var buttons = FindDescendants<Button>(root).Where(b => (b.Content as string) == "Anhören").ToList();
        Assert.Equal(2, buttons.Count);
        Assert.Same(vm.PreviewCountdownBeepCommand, buttons[0].Command);
        Assert.Same(vm.PreviewShutterSoundCommand, buttons[1].Command);
    }

    [StaFact]
    public void FlowTab_GreysOutTheSettingsWhenTheSwitchIsOff()
    {
        var vm = CreateViewModel();
        var window = new AdminWindow(vm);
        var root = ShowFlowTab(window);
        var beepCombo = FindDescendants<ComboBox>(root).Single(c => AutomationProperties.GetName(c) == "Klang des Countdown-Piepens");
        var shutterCombo = FindDescendants<ComboBox>(root).Single(c => AutomationProperties.GetName(c) == "Klang des Kamera-Sounds");
        Assert.True(beepCombo.IsEnabled);
        Assert.True(shutterCombo.IsEnabled);

        vm.CountdownBeepEnabled = false;
        Assert.False(beepCombo.IsEnabled);
        Assert.True(shutterCombo.IsEnabled);

        vm.ShutterSoundEnabled = false;
        Assert.False(shutterCombo.IsEnabled);
        var buttons = FindDescendants<Button>(root).Where(b => (b.Content as string) == "Anhören").ToList();
        Assert.All(buttons, b => Assert.False(b.IsEnabled));

        vm.CountdownBeepEnabled = true;
        vm.ShutterSoundEnabled = true;
        Assert.All(buttons, b => Assert.True(b.IsEnabled));
    }
}
