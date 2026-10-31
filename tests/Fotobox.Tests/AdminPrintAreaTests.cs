using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.ViewModels.Admin;
using Fotobox.Views.Admin;
using Fotobox.Views.Booth;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Admin, Tab „Gastansicht“, Karte „Druckbereich anzeigen“: Entwurf, Abhängigkeiten, Skizze, Validierung, Speichern und Ansicht.</summary>
[Collection(WpfCollection.Name)]
public sealed class AdminPrintAreaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-printarea-" + Guid.NewGuid().ToString("N"));
    private readonly AdminViewModelTests.FakeStore _store;
    private readonly AdminViewModelTests.FakePinService _pin;
    private readonly List<AdminViewModel> _created = [];

    public AdminPrintAreaTests()
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

    [Fact]
    public void Defaults_MatchTheSettingsDefaults()
    {
        var vm = CreateViewModel();

        Assert.False(vm.PrintAreaEnabled);
        Assert.Equal(PrintAreaStyle.LinesShaded, vm.PrintAreaShape);
        Assert.Equal(PrintAreaColor.White, vm.PrintAreaLineColor);
        Assert.Equal(80, vm.PrintAreaOpacityPercent);
        Assert.Equal(3, vm.PrintAreaThickness);
        Assert.Equal(40, vm.PrintAreaShadePercent);
        Assert.Equal("80 %", vm.PrintAreaOpacityText);
        Assert.Equal("40 %", vm.PrintAreaShadeText);
    }

    [Fact]
    public void Edits_GoToTheDraft_NotToTheStore_AndCountAsUnsavedChanges()
    {
        var vm = CreateViewModel();
        Assert.False(vm.HasUnsavedChanges);

        vm.PrintAreaEnabled = true;
        vm.PrintAreaShape = PrintAreaStyle.Corners;
        vm.PrintAreaLineColor = PrintAreaColor.Yellow;
        vm.PrintAreaOpacityPercent = 50;
        vm.PrintAreaThickness = 6;
        vm.PrintAreaShadePercent = 70;

        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.Draft.PrintArea.Enabled);
        Assert.Equal(PrintAreaStyle.Corners, vm.Draft.PrintArea.Style);
        Assert.Equal(PrintAreaColor.Yellow, vm.Draft.PrintArea.Color);
        Assert.Equal((50, 6, 70), (vm.Draft.PrintArea.OpacityPercent, vm.Draft.PrintArea.ThicknessPx, vm.Draft.PrintArea.ShadePercent));
        Assert.False(_store.Current.PrintArea.Enabled);
        Assert.Equal(PrintAreaStyle.LinesShaded, _store.Current.PrintArea.Style);
    }

    [Fact]
    public void Save_WritesThePrintAreaToTheStore()
    {
        var vm = CreateViewModel();
        vm.PrintAreaEnabled = true;
        vm.PrintAreaLineColor = PrintAreaColor.Blue;
        vm.PrintAreaThickness = 8;

        vm.ApplyCommand.Execute(null);

        Assert.True(_store.Current.PrintArea.Enabled);
        Assert.Equal(PrintAreaColor.Blue, _store.Current.PrintArea.Color);
        Assert.Equal(8, _store.Current.PrintArea.ThicknessPx);
    }

    [Fact]
    public void ShadeSlider_IsOnlyEnabled_WithTheFeatureOnAndTheShadedShape()
    {
        var vm = CreateViewModel();
        var raised = new ConcurrentQueue<string?>(); // Hintergrund-Meldungen des ViewModels dürfen mitlaufen
        vm.PropertyChanged += (_, e) => raised.Enqueue(e.PropertyName);

        Assert.False(vm.IsPrintAreaDetailEnabled);
        Assert.False(vm.IsPrintAreaShadeEnabled);

        vm.PrintAreaEnabled = true;
        Assert.True(vm.IsPrintAreaDetailEnabled);
        Assert.True(vm.IsPrintAreaShadeEnabled);
        Assert.Contains(nameof(AdminViewModel.IsPrintAreaDetailEnabled), raised);
        Assert.Contains(nameof(AdminViewModel.IsPrintAreaShadeEnabled), raised);

        raised.Clear();
        vm.PrintAreaShape = PrintAreaStyle.Lines;
        Assert.False(vm.IsPrintAreaShadeEnabled);
        Assert.True(vm.IsPrintAreaDetailEnabled);
        Assert.Contains(nameof(AdminViewModel.IsPrintAreaShadeEnabled), raised);

        vm.PrintAreaShape = PrintAreaStyle.Corners;
        Assert.False(vm.IsPrintAreaShadeEnabled);
        vm.PrintAreaShape = PrintAreaStyle.LinesShaded;
        Assert.True(vm.IsPrintAreaShadeEnabled);

        vm.PrintAreaEnabled = false;
        Assert.False(vm.IsPrintAreaShadeEnabled);
    }

    [Fact]
    public void Texts_FollowTheValues()
    {
        var vm = CreateViewModel();
        var raised = new ConcurrentQueue<string?>(); // Hintergrund-Meldungen des ViewModels dürfen mitlaufen
        vm.PropertyChanged += (_, e) => raised.Enqueue(e.PropertyName);

        vm.PrintAreaOpacityPercent = 35;
        vm.PrintAreaShadePercent = 65;

        Assert.Equal("35 %", vm.PrintAreaOpacityText);
        Assert.Equal("65 %", vm.PrintAreaShadeText);
        Assert.Contains(nameof(AdminViewModel.PrintAreaOpacityText), raised);
        Assert.Contains(nameof(AdminViewModel.PrintAreaShadeText), raised);
    }

    // ---- Vorschau-Skizze

    [Fact]
    public void Preview_ForTheDefaultSingleLayout_IsASideCropOfThe16To9Placeholder()
    {
        var vm = CreateViewModel();

        // Standard: Einzelfoto, 10 × 15 cm, Auto = Querformat → 3:2 auf 16:9.
        var rect = vm.PrintAreaPreviewRect;

        Assert.NotNull(rect);
        Assert.Equal(1620.0 / 1920, rect!.Value.Width, 6);
        Assert.Equal(1, rect.Value.Height, 9);
        Assert.Contains("Ein Foto", vm.PrintAreaPreviewText);
        Assert.Contains("Querformat", vm.PrintAreaPreviewText);
    }

    [Fact]
    public void Preview_FollowsTheLayoutPaperOrientationAndStripCount_OfTheDraft()
    {
        var vm = CreateViewModel();
        var raised = new ConcurrentQueue<string?>(); // Hintergrund-Meldungen des ViewModels dürfen mitlaufen
        vm.PropertyChanged += (_, e) => raised.Enqueue(e.PropertyName);
        var single = vm.PrintAreaPreviewRect;

        vm.Draft.Printer.Orientation = PageOrientation.Portrait;
        vm.Draft.Printer.PaperWidthMm = 210;
        vm.Draft.Printer.PaperHeightMm = 297;
        vm.SelectedOrientation = vm.Orientations.First(o => o.Orientation == PageOrientation.Portrait);
        var a4Portrait = vm.PrintAreaPreviewRect;
        Assert.NotEqual(single, a4Portrait);
        Assert.Contains(nameof(AdminViewModel.PrintAreaPreviewRect), raised);

        vm.IsStripLayout = true;
        vm.StripPhotoCount = 3;
        var strip = vm.PrintAreaPreviewRect;
        Assert.NotEqual(a4Portrait, strip);
        Assert.Contains("3 Fotos – 2 Streifen", vm.PrintAreaPreviewText);

        raised.Clear();
        vm.StripPhotoCount = 4;
        Assert.Contains(nameof(AdminViewModel.PrintAreaPreviewRect), raised);
        Assert.Contains(nameof(AdminViewModel.PrintAreaPreviewText), raised);
        Assert.NotEqual(strip, vm.PrintAreaPreviewRect);
    }

    [Fact]
    public void Preview_OfTheSingleStrip_HasNoRectAndExplainsWhy()
    {
        var vm = CreateViewModel();

        vm.IsStripLayout = true;
        vm.IsStripSingle = true;

        Assert.Null(vm.PrintAreaPreviewRect);
        Assert.Contains("keinen Zuschnitt", vm.PrintAreaPreviewText);
    }

    // ---- Validierung

    [Fact]
    public void Validate_RejectsOutOfRangeValues_AndAcceptsTheLimits()
    {
        var vm = CreateViewModel();
        Assert.DoesNotContain(vm.Validate(), e => e.Contains("Druckbereichs"));

        vm.Draft.PrintArea.OpacityPercent = SettingsLimits.PrintAreaOpacityMin - 1;
        vm.Draft.PrintArea.ThicknessPx = SettingsLimits.PrintAreaThicknessMax + 1;
        vm.Draft.PrintArea.ShadePercent = SettingsLimits.PrintAreaShadeMax + 1;
        var errors = vm.Validate();

        Assert.Contains(errors, e => e.Contains("Deckkraft des Druckbereichs"));
        Assert.Contains(errors, e => e.Contains("Linienstärke des Druckbereichs"));
        Assert.Contains(errors, e => e.Contains("Abdunklung des Druckbereichs"));

        vm.Draft.PrintArea.OpacityPercent = SettingsLimits.PrintAreaOpacityMin;
        vm.Draft.PrintArea.ThicknessPx = SettingsLimits.PrintAreaThicknessMax;
        vm.Draft.PrintArea.ShadePercent = SettingsLimits.PrintAreaShadeMax;
        Assert.DoesNotContain(vm.Validate(), e => e.Contains("Druckbereichs"));
    }

    // ---- Ansicht

    [StaFact]
    public void GuestTab_ShowsTheCard_WithFourSketches_AndGraysOutTheFieldsWhileOff()
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
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header as string == "Gastansicht");
            root.UpdateLayout();

            // Drei Wahlkarten der Form und die große Vorschau.
            var sketches = FindDescendants<PrintAreaSketch>(root).ToList();
            Assert.Equal(4, sketches.Count);
            Assert.Equal(3, sketches.Count(s => s.UseSample));
            Assert.All(sketches, s => Assert.False(s.IsHitTestVisible));

            // Acht Farbpunkte und die Wahlkarten sitzen in dem Bereich, der bei ausgeschalteter Funktion ausgegraut ist.
            var panel = (StackPanel)window.FindName("PrintAreaFields");
            Assert.Equal(8, FindDescendants<RadioButton>(panel).Count(r => r.GroupName == "PrintAreaColor"));
            Assert.False(panel.IsEnabled);
            Assert.Equal(0.55, panel.Opacity, 2);

            vm.PrintAreaEnabled = true;
            root.UpdateLayout();
            Assert.True(panel.IsEnabled);
            Assert.Equal(1.0, panel.Opacity, 2);

            // Die Form „Linien + Abdunklung“ ist vorgewählt, die Farbe Weiß.
            var styleCards = FindDescendants<RadioButton>(root).Where(r => r.GroupName == "PrintAreaStyle").ToList();
            Assert.Equal(3, styleCards.Count);
            Assert.Equal([false, true, false], styleCards.Select(r => r.IsChecked == true).ToArray());
            var colors = FindDescendants<RadioButton>(root).Where(r => r.GroupName == "PrintAreaColor").ToList();
            Assert.Equal(1, colors.Count(r => r.IsChecked == true));
            Assert.Same(PrintAreaPalette.White, colors.Single(r => r.IsChecked == true).Background);

            // Klick auf eine Farbe und eine Form schreibt in den Entwurf.
            colors.Single(r => ReferenceEquals(r.Background, PrintAreaPalette.Pink)).IsChecked = true;
            styleCards[2].IsChecked = true;
            Assert.Equal(PrintAreaColor.Pink, vm.PrintAreaLineColor);
            Assert.Equal(PrintAreaStyle.Corners, vm.PrintAreaShape);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void Palette_HasAnOpaqueFrozenBrushForEveryColor_AndTheColorsAreDistinct()
    {
        var colors = Enum.GetValues<PrintAreaColor>();

        foreach (var color in colors)
        {
            var brush = PrintAreaPalette.BrushOf(color);
            Assert.True(brush.IsFrozen);
            Assert.Equal(PrintAreaPalette.ColorOf(color), ((SolidColorBrush)brush).Color);
            Assert.Equal(255, PrintAreaPalette.ColorOf(color).A);
        }
        Assert.Equal(colors.Length, colors.Select(PrintAreaPalette.ColorOf).Distinct().Count());
        Assert.Equal(Colors.White, PrintAreaPalette.ColorOf(PrintAreaColor.White));
        Assert.Equal(Colors.Black, PrintAreaPalette.ColorOf(PrintAreaColor.Black));
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
