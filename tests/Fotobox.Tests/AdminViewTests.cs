using System.IO;
using Fotobox.Core;
using Fotobox.ViewModels.Admin;
using Fotobox.Views.Admin;
using Fotobox.Views.Booth;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Prüft, dass die XAML-Dateien der Admin-Ansicht zur Laufzeit ladbar sind (ohne sie anzuzeigen).</summary>
[Collection(WpfCollection.Name)]
public class AdminViewTests
{
    [StaFact]
    public void PinDialogs_CanBeCreated()
    {
        var limiter = new PinAttemptLimiter(TimeProvider.System);

        var pin = new PinDialog(_ => false, limiter, owner: null);
        var first = new FirstRunPinDialog(owner: null);

        Assert.NotNull(pin);
        Assert.NotNull(first);
    }

    [StaFact]
    public void AdminWindow_CanBeCreated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new AdminViewModelTests.FakeStore(dir);
            var vm = new AdminViewModel(store, new AdminViewModelTests.FakePinService(store),
                new AdminViewModelTests.FakeCamera(), new AdminViewModelTests.FakePrint(), new AdminViewModelTests.FakeUi());

            var window = new AdminWindow(vm);

            Assert.Same(vm, window.DataContext);
            vm.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [StaFact]
    public void TestPageRenderer_ProducesPageSizedFrozenBitmap()
    {
        var page = PageSpec.Default;

        var bitmap = TestPageRenderer.Render(page);

        Assert.Equal(page.PixelWidth, bitmap.PixelWidth);
        Assert.Equal(page.PixelHeight, bitmap.PixelHeight);
        Assert.True(bitmap.IsFrozen);
    }


    [StaFact]
    public void AdminWindow_TabControlTemplate_ExposesSelectedContentHost()
    {
        // Ohne PART_SelectedContentHost sind die Tab-Inhalte für UI Automation (Screenreader) unsichtbar.
        var dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new AdminViewModelTests.FakeStore(dir);
            var vm = new AdminViewModel(store, new AdminViewModelTests.FakePinService(store),
                new AdminViewModelTests.FakeCamera(), new AdminViewModelTests.FakePrint(), new AdminViewModelTests.FakeUi());
            var window = new AdminWindow(vm);

            var tabs = FindTabControl(window)!;
            tabs.ApplyTemplate();

            Assert.IsType<System.Windows.Controls.ContentPresenter>(tabs.Template.FindName("PART_SelectedContentHost", tabs));
            vm.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [StaFact]
    public void AdminWindow_HasKeysTab()
    {
        WithWindow((window, _) =>
        {
            var tabs = FindTabControl(window)!;
            var headers = tabs.Items.OfType<System.Windows.Controls.TabItem>().Select(t => t.Header as string).ToList();

            Assert.Contains("Tasten", headers);
            Assert.Contains("Layout", headers);
        });
    }

    // ---- Farbschema

    private static System.Windows.Media.Color BrushColor(System.Windows.FrameworkElement element, string key) =>
        ((System.Windows.Media.SolidColorBrush)element.FindResource(key)).Color;

    [StaFact]
    public void AdminTheme_Apply_SwapsPalette_AndBack()
    {
        var limiter = new PinAttemptLimiter(TimeProvider.System);
        var dialog = new PinDialog(_ => false, limiter, owner: null, AdminThemeMode.Light);
        var light = BrushColor(dialog, "TextBrush");

        AdminThemeManager.Apply(dialog, AdminThemeMode.Dark);
        Assert.NotEqual(light, BrushColor(dialog, "TextBrush"));
        Assert.True(AdminThemeManager.IsDarkPalette(dialog.Resources));

        // Genau eine Palette, auch nach wiederholtem Umschalten.
        AdminThemeManager.Apply(dialog, AdminThemeMode.Dark);
        Assert.Single(dialog.Resources.MergedDictionaries, d => d.Source?.OriginalString.Contains("AdminColors.") == true);

        AdminThemeManager.Apply(dialog, AdminThemeMode.Light);
        Assert.Equal(light, BrushColor(dialog, "TextBrush"));
        Assert.False(AdminThemeManager.IsDarkPalette(dialog.Resources));
    }

    [StaFact]
    public void AdminTheme_PaletteFiles_DefineTheSameKeys()
    {
        var light = new System.Windows.ResourceDictionary
        {
            Source = new Uri("/Fotobox;component/Views/Admin/AdminColors.Light.xaml", UriKind.Relative)
        };
        var dark = new System.Windows.ResourceDictionary
        {
            Source = new Uri("/Fotobox;component/Views/Admin/AdminColors.Dark.xaml", UriKind.Relative)
        };

        var lightKeys = light.Keys.Cast<object>().Select(k => k.ToString()).Order().ToArray();
        var darkKeys = dark.Keys.Cast<object>().Select(k => k.ToString()).Order().ToArray();

        Assert.NotEmpty(lightKeys);
        Assert.Equal(lightKeys, darkKeys);
    }

    [Fact]
    public void AdminTheme_IsDark_ForExplicitModes()
    {
        Assert.True(AdminThemeManager.IsDark(AdminThemeMode.Dark));
        Assert.False(AdminThemeManager.IsDark(AdminThemeMode.Light));
    }

    [StaFact]
    public void AdminWindow_FollowsDraftTheme_AsLivePreview()
    {
        WithWindow((window, vm) =>
        {
            vm.IsAdminThemeLight = true;
            Assert.False(AdminThemeManager.IsDarkPalette(window.Resources));

            vm.IsAdminThemeDark = true;
            Assert.True(AdminThemeManager.IsDarkPalette(window.Resources));
        });
    }

    [StaFact]
    public void AdminWindow_DarkTheme_AllTabsLayoutWithoutErrors()
    {
        // Jeder Tab einmal mit Layout im dunklen Schema (eigene ComboBox-Templates, DynamicResource-Auflösung).
        WithWindow((window, vm) =>
        {
            vm.IsAdminThemeDark = true;
            var tabs = FindTabControl(window)!;
            var root = (System.Windows.FrameworkElement)window.Content;
            foreach (var tab in tabs.Items.OfType<System.Windows.Controls.TabItem>().ToList())
            {
                tabs.SelectedItem = tab;
                root.Measure(new System.Windows.Size(1100, 750));
                root.Arrange(new System.Windows.Rect(0, 0, 1100, 750));
                root.UpdateLayout();
            }

            tabs.SelectedItem = tabs.Items.OfType<System.Windows.Controls.TabItem>().Single(t => t.Header as string == "Kamera");
            root.UpdateLayout();
            var combo = FindDescendant<System.Windows.Controls.ComboBox>(root);
            Assert.NotNull(combo);
            Assert.NotNull(combo!.Template.FindName("PART_Popup", combo));
        });
    }

    [StaFact]
    public void AdminWindow_AppearanceTab_MonitorBox_IsDisabledForOneMonitor_AndEnabledForTwo()
    {
        var one = ReadMonitorBox(AdminViewModelTests.AdminMonitors.Primary);
        Assert.False(one.Enabled);
        Assert.Equal(1, one.Count);
        Assert.Contains("Hauptbildschirm", one.Text);

        var two = ReadMonitorBox(AdminViewModelTests.AdminMonitors.Primary, AdminViewModelTests.AdminMonitors.Second);
        Assert.True(two.Enabled);
        Assert.Equal(2, two.Count);
    }

    private static (bool Enabled, int Count, string? Text) ReadMonitorBox(params DisplayMonitor[] monitors)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new AdminViewModelTests.FakeStore(dir);
            var vm = new AdminViewModel(store, new AdminViewModelTests.FakePinService(store),
                new AdminViewModelTests.FakeCamera(), new AdminViewModelTests.FakePrint(), new AdminViewModelTests.FakeUi(),
                displays: new AdminViewModelTests.FakeDisplays(monitors));
            try
            {
                // Vor dem Fenster laden (ohne Synchronisationskontext läuft alles auf dem Aufrufer-/Threadpool-Thread, es gibt noch keine Bindung).
                vm.InitializeAsync().GetAwaiter().GetResult();
                var window = new AdminWindow(vm);

                var tabs = FindTabControl(window)!;
                tabs.SelectedItem = tabs.Items.OfType<System.Windows.Controls.TabItem>().Single(t => t.Header as string == "Darstellung");
                var root = (System.Windows.FrameworkElement)window.Content;
                root.Measure(new System.Windows.Size(1100, 750));
                root.Arrange(new System.Windows.Rect(0, 0, 1100, 750));
                root.UpdateLayout();

                var combo = FindDescendants<System.Windows.Controls.ComboBox>(root)
                    .Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Monitor der Gastansicht");
                return (combo.IsEnabled, combo.Items.Count, (combo.SelectedItem as MonitorOption)?.Display);
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

    [StaFact]
    public void AdminWindow_HasAppearanceTab_AfterGuestScreenTab()
    {
        WithWindow((window, _) =>
        {
            var tabs = FindTabControl(window)!;
            var headers = tabs.Items.OfType<System.Windows.Controls.TabItem>().Select(t => t.Header as string).ToList();

            Assert.Equal(headers.IndexOf("Gastansicht") + 1, headers.IndexOf("Darstellung"));
        });
    }

    [StaFact]
    public void AdminWindow_HasGuestScreenTab_AfterLayoutTab()
    {
        WithWindow((window, _) =>
        {
            var tabs = FindTabControl(window)!;
            var headers = tabs.Items.OfType<System.Windows.Controls.TabItem>().Select(t => t.Header as string).ToList();

            Assert.Contains("Gastansicht", headers);
            Assert.Equal(headers.IndexOf("Layout") + 1, headers.IndexOf("Gastansicht"));
        });
    }

    [StaFact]
    public void AdminWindow_GuestScreenTab_ShowsPreviewOfDraftText()
    {
        WithWindow((window, vm) =>
        {
            vm.HintText = "Los: {Fototaste_1}";
            vm.IsHintBanner = true;
            var tabs = FindTabControl(window)!;
            tabs.SelectedItem = tabs.Items.OfType<System.Windows.Controls.TabItem>().Single(t => t.Header as string == "Gastansicht");
            var root = (System.Windows.FrameworkElement)window.Content;
            root.Measure(new System.Windows.Size(1100, 750));
            root.Arrange(new System.Windows.Rect(0, 0, 1100, 750));
            root.UpdateLayout();

            var hint = FindDescendant<GuestHint>(root);

            Assert.NotNull(hint);
            Assert.Equal("Los: Leertaste", hint!.Text);
            Assert.Equal(HintStyle.Banner, hint.HintStyle);
            Assert.False(hint.ShowLayoutHint);

            vm.IsGuestStripDouble = true;
            Assert.True(hint.ShowLayoutHint);
            Assert.Equal("Mausrad: Layout wechseln · Ein Foto", hint.LayoutHintText);
        });
    }

    [StaTheory]
    [InlineData(HintStyle.Pill)]
    [InlineData(HintStyle.Plain)]
    [InlineData(HintStyle.Banner)]
    public void GuestHint_CanBeCreated_AndShowsOnlyTheChosenStyle(HintStyle style)
    {
        var hint = new GuestHint
        {
            Text = "Drücke die Leertaste oder klicke für ein Foto 📸",
            HintStyle = style,
            LayoutHintText = "Mausrad: Layout wechseln · Ein Foto",
            ShowLayoutHint = true,
        };

        hint.Measure(new System.Windows.Size(1920, 1080));
        hint.Arrange(new System.Windows.Rect(0, 0, 1920, 1080));
        hint.UpdateLayout();

        var parts = ((System.Windows.Controls.Grid)hint.Content).Children.OfType<System.Windows.Controls.Grid>().ToList();
        Assert.Equal(3, parts.Count);
        for (var i = 0; i < parts.Count; i++)
        {
            var expected = i == (int)style ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            Assert.Equal(expected, parts[i].Visibility);
        }
        Assert.False(hint.IsHitTestVisible);
    }

    [StaFact]
    public void GuestHint_LongText_StaysWithinTwoLines()
    {
        var text = new string('W', SettingsLimits.HintTextMaxLength);
        foreach (var style in Enum.GetValues<HintStyle>())
        {
            var hint = new GuestHint { Text = text, HintStyle = style };
            hint.Measure(new System.Windows.Size(1920, 1080));
            hint.Arrange(new System.Windows.Rect(0, 0, 1920, 1080));
            hint.UpdateLayout();

            // Die Bereiche stehen in der Reihenfolge des Enums (Pille, Schlicht, Banner).
            var part = ((System.Windows.Controls.Grid)hint.Content).Children.OfType<System.Windows.Controls.Grid>().ElementAt((int)style);
            var block = FindDescendants<System.Windows.Controls.TextBlock>(part).First(t => t.Text == text);
            Assert.True(block.ActualHeight <= block.LineHeight * 2 + 0.5, $"{style}: {block.ActualHeight}");
            Assert.True(block.ActualWidth <= 1500 + 0.5, $"{style}: {block.ActualWidth}");
        }
    }

    [StaFact]
    public void AdminWindow_KeyCapture_TakesKeyAndSwallowsIt()
    {
        WithWindow((window, vm) =>
        {
            using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters());
            vm.BeginKeyCapture(KeyAction.Trigger);

            // Reine Modifikatortaste: abwarten, nichts übernehmen, aber verschlucken.
            var modifier = RaiseKeyDown(window, source, System.Windows.Input.Key.LeftCtrl);
            Assert.True(modifier.Handled);
            Assert.True(vm.IsKeyCaptureActive);

            // Normale Taste: übernommen (Handled, damit kein fokussierter Button ausgelöst wird).
            var key = RaiseKeyDown(window, source, System.Windows.Input.Key.F9);
            Assert.True(key.Handled);
            Assert.False(vm.IsKeyCaptureActive);
            Assert.Contains("F9", vm.Draft.Input.TriggerKeys);
        });
    }

    [StaFact]
    public void AdminWindow_KeyDown_WithoutCapture_IsNotHandled()
    {
        WithWindow((window, vm) =>
        {
            using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters());

            var args = RaiseKeyDown(window, source, System.Windows.Input.Key.Space);

            Assert.False(args.Handled);
            Assert.Equal(["Space"], vm.Draft.Input.TriggerKeys);
        });
    }

    [StaFact]
    public void AdminWindow_KeyCapture_RejectsSystemCombination_AndKeepsCapturing()
    {
        WithWindow((window, vm) =>
        {
            using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters());
            vm.BeginKeyCapture(KeyAction.Trigger);

            // Win-Taste ist reiner Modifikator; Escape wird dagegen wie jede andere Taste geprüft (hier: gehört zu „Abbrechen“).
            RaiseKeyDown(window, source, System.Windows.Input.Key.Escape);

            Assert.True(vm.IsKeyCaptureActive);
            Assert.Contains("‚Abbrechen‘", vm.KeyMessage);
        });
    }

    private static System.Windows.Input.KeyEventArgs RaiseKeyDown(
        AdminWindow window, System.Windows.PresentationSource source, System.Windows.Input.Key key)
    {
        var args = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent,
        };
        window.RaiseEvent(args);
        return args;
    }

    private static void WithWindow(Action<AdminWindow, AdminViewModel> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new AdminViewModelTests.FakeStore(dir);
            var vm = new AdminViewModel(store, new AdminViewModelTests.FakePinService(store),
                new AdminViewModelTests.FakeCamera(), new AdminViewModelTests.FakePrint(), new AdminViewModelTests.FakeUi());
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

    private static T? FindDescendant<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject =>
        FindDescendants<T>(parent).FirstOrDefault();

    /// <summary>Alle Nachfahren im visuellen Baum (für Inhalte, die über Tab-Vorlagen erst beim Layout entstehen).</summary>
    private static IEnumerable<T> FindDescendants<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                yield return match;
            foreach (var nested in FindDescendants<T>(child))
                yield return nested;
        }
    }

    private static System.Windows.Controls.TabControl? FindTabControl(System.Windows.DependencyObject parent)
    {
        if (parent is System.Windows.Controls.TabControl tabs)
            return tabs;
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(parent).OfType<System.Windows.DependencyObject>())
        {
            if (FindTabControl(child) is { } found)
                return found;
        }
        return null;
    }
}
