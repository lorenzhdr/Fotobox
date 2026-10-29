using System.IO;
using Fotobox.Core;
using Fotobox.ViewModels.Admin;
using Fotobox.Views;
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
    public void FirstRunPinDialog_Hint_UsesConfiguredAdminKeys()
    {
        var custom = new FirstRunPinDialog(owner: null, AdminThemeMode.Light, ["Ctrl+Shift+F9", "F10"]);
        var standard = new FirstRunPinDialog(owner: null);

        Assert.Contains("Strg+Umschalt+F9 oder F10", custom.AdminKeyHint.Text);
        Assert.DoesNotContain("Strg+Alt+A", custom.AdminKeyHint.Text);
        Assert.Contains("Strg+Alt+A", standard.AdminKeyHint.Text);
    }

    [Theory]
    [InlineData(new[] { "Ctrl+Alt+A" }, "Strg+Alt+A")]
    [InlineData(new[] { "F10", "Ctrl+Alt+A" }, "F10 oder Strg+Alt+A")]
    [InlineData(new[] { "F10", "F10" }, "F10")]
    [InlineData(new[] { "kaputt", "F10" }, "F10")]
    [InlineData(new string[0], "Strg+Alt+A")]
    [InlineData(new[] { "kaputt" }, "Strg+Alt+A")]
    public void FirstRunPinDialog_FormatAdminKeys(string[] keys, string expected) =>
        Assert.Equal(expected, FirstRunPinDialog.FormatAdminKeys(keys));

    [StaFact]
    public void PinBoxes_UseSettingsLimitsForMaxLength()
    {
        var first = new FirstRunPinDialog(owner: null);

        Assert.Equal(SettingsLimits.PinMaxLength, first.PinBox.MaxLength);
        Assert.Equal(SettingsLimits.PinMaxLength, first.RepeatBox.MaxLength);
    }

    [StaFact]
    public void AdminWindow_LimitSize_AlsoLimitsMinimumSize_SoFooterStaysReachable()
    {
        WithWindow((window, _) =>
        {
            Assert.True(window.MinHeight > 560);   // Ausgangslage: XAML-Mindesthöhe 620

            window.LimitSizeToArea(new System.Windows.Size(1000, 580));   // z. B. 1280 x 800 bei 125 % Skalierung

            Assert.Equal(560, window.MinHeight);
            Assert.Equal(560, window.Height);
            Assert.Equal(900, window.MinWidth);   // passt in 980: bleibt
            Assert.Equal(980, window.Width);
        });
    }

    [StaFact]
    public void AdminWindow_JpegQualitySlider_ShowsStoredValue_AndUsesSettingsLimits()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new AdminViewModelTests.FakeStore(dir);
            store.Current.Storage.JpegQuality = 80;
            var vm = new AdminViewModel(store, new AdminViewModelTests.FakePinService(store),
                new AdminViewModelTests.FakeCamera(), new AdminViewModelTests.FakePrint(), new AdminViewModelTests.FakeUi());
            try
            {
                var window = new AdminWindow(vm);
                var tabs = FindTabControl(window)!;
                tabs.SelectedItem = tabs.Items.OfType<System.Windows.Controls.TabItem>().Single(t => t.Header as string == "Speicher");
                var root = (System.Windows.FrameworkElement)window.Content;
                root.Measure(new System.Windows.Size(1100, 750));
                root.Arrange(new System.Windows.Rect(0, 0, 1100, 750));
                root.UpdateLayout();

                var slider = FindDescendant<System.Windows.Controls.Slider>(root)!;

                Assert.Equal(SettingsLimits.JpegQualityMin, slider.Minimum);
                Assert.Equal(SettingsLimits.JpegQualityMax, slider.Maximum);
                Assert.Equal(80, slider.Value);
                Assert.Equal(80, vm.JpegQuality);
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
        WpfCollection.EnsurePackUris();
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
            // Der Tab „Fun“ steht ganz unten in der Liste.
            Assert.Equal(headers.Count - 1, headers.IndexOf("Fun"));
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
            vm.HintStyle = HintStyle.Banner;
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

    public static IEnumerable<object[]> AllHintStyles => Enum.GetValues<HintStyle>().Select(s => new object[] { s });

    [StaTheory]
    [MemberData(nameof(AllHintStyles))]
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

        // Je Darstellung ein Bereich, in der Reihenfolge des Enums.
        var parts = ((System.Windows.Controls.Grid)hint.Content).Children.OfType<System.Windows.Controls.Grid>().ToList();
        Assert.Equal(Enum.GetValues<HintStyle>().Length, parts.Count);
        for (var i = 0; i < parts.Count; i++)
        {
            var expected = i == (int)style ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            Assert.Equal(expected, parts[i].Visibility);
        }
        Assert.False(hint.IsHitTestVisible);
    }

    [StaFact]
    public void GuestHint_LongText_StaysWithinTwoLines_TickerWithinOne()
    {
        var text = new string('W', SettingsLimits.HintTextMaxLength);
        foreach (var style in Enum.GetValues<HintStyle>())
        {
            var hint = new GuestHint { Text = text, HintStyle = style };
            hint.Measure(new System.Windows.Size(1920, 1080));
            hint.Arrange(new System.Windows.Rect(0, 0, 1920, 1080));
            hint.UpdateLayout();

            // Die Bereiche stehen in der Reihenfolge des Enums.
            var part = ((System.Windows.Controls.Grid)hint.Content).Children.OfType<System.Windows.Controls.Grid>().ElementAt((int)style);
            var block = FindDescendants<System.Windows.Controls.TextBlock>(part).First(t => t.Text == text);
            if (style == HintStyle.Ticker)
            {
                // Laufschrift: eine Zeile, nie umgebrochen oder gekürzt; sie läuft aus dem Band hinaus.
                Assert.True(block.ActualHeight <= block.LineHeight + 0.5, $"{style}: {block.ActualHeight}");
                Assert.Equal(System.Windows.TextWrapping.NoWrap, block.TextWrapping);
                Assert.True(block.ActualWidth > 1500, $"{style}: {block.ActualWidth}");
                continue;
            }
            Assert.True(block.ActualHeight <= block.LineHeight * 2 + 0.5, $"{style}: {block.ActualHeight}");
            Assert.True(block.ActualWidth <= 1500 + 0.5, $"{style}: {block.ActualWidth}");
        }
    }

    // ---- Admin-Tab „Gastansicht“: Wahlkarten für Hinweis-Stile und Mausradzeile

    private static List<System.Windows.Controls.RadioButton> CardsOf(System.Windows.FrameworkElement root, string group) =>
        FindDescendants<System.Windows.Controls.RadioButton>(root).Where(r => r.GroupName == group).ToList();

    [StaFact]
    public void AdminWindow_GuestScreenTab_HasNineStyleCardsForPhotoAndVideo_AndFiveWheelCards()
    {
        WithWindow((window, vm) =>
        {
            var root = ShowTab(window, "Gastansicht");

            var photo = CardsOf(root, "HintStyle");
            var video = CardsOf(root, "VideoHintStyle");
            var wheel = CardsOf(root, "WheelHintStyle");
            Assert.Equal(Enum.GetValues<HintStyle>().Length, photo.Count);
            Assert.Equal(Enum.GetValues<HintStyle>().Length, video.Count);
            Assert.Equal(Enum.GetValues<WheelHintStyle>().Length, wheel.Count);

            // Genau die Karte des Entwurfs ist gewählt.
            Assert.Equal([true, false, false, false, false, false, false, false, false], photo.Select(r => r.IsChecked == true));
            Assert.Equal([true, false, false, false, false, false, false, false, false], video.Select(r => r.IsChecked == true));
            Assert.Equal([true, false, false, false, false], wheel.Select(r => r.IsChecked == true));

            // Klick auf eine Karte schreibt in den Entwurf (EnumMatchConverter, zwei Richtungen) …
            photo[(int)HintStyle.Neon].IsChecked = true;
            video[(int)HintStyle.Cinema].IsChecked = true;
            wheel[(int)WheelHintStyle.Chip].IsChecked = true;
            Assert.Equal(HintStyle.Neon, vm.HintStyle);
            Assert.Equal(HintStyle.Cinema, vm.VideoHintStyle);
            Assert.Equal(WheelHintStyle.Chip, vm.WheelHintStyle);
            Assert.Equal(HintStyle.Neon, vm.Draft.GuestScreen.HintStyle);
            Assert.Equal([HintStyle.Neon], photo.Where(r => r.IsChecked == true).Select(r => (HintStyle)photo.IndexOf(r)));

            // … und Änderungen am Entwurf wandern auf die Karten (auch das Vorschau-Control folgt).
            vm.HintStyle = HintStyle.Note;
            Assert.True(photo[(int)HintStyle.Note].IsChecked);
            Assert.False(photo[(int)HintStyle.Neon].IsChecked);
            var hints = FindDescendants<GuestHint>(root).ToList();
            Assert.Equal(HintStyle.Note, hints[0].HintStyle);
            Assert.Equal(HintStyle.Cinema, hints[1].HintStyle);
            Assert.Equal(WheelHintStyle.Chip, hints[0].LayoutHintStyle);
        });
    }

    [StaFact]
    public void AdminWindow_GuestScreenTab_WheelSection_PreviewFollowsCheckboxTextAndStyle()
    {
        WithWindow((window, vm) =>
        {
            var root = ShowTab(window, "Gastansicht");
            var hint = FindDescendants<GuestHint>(root).First();
            var panel = (System.Windows.Controls.StackPanel)window.FindName("WheelHintPanel");
            var checkBox = FindDescendants<System.Windows.Controls.CheckBox>(root)
                .Single(c => c.Content as string == "Mausradzeile anzeigen");
            Assert.True(checkBox.IsChecked);
            Assert.True(panel.IsEnabled);

            vm.IsGuestStripDouble = true;
            Assert.True(hint.ShowLayoutHint);
            Assert.Equal("Mausrad: Layout wechseln · Ein Foto", hint.LayoutHintText);

            vm.WheelHintText = "Rad drehen";
            Assert.Equal("Rad drehen", hint.LayoutHintText);

            // Text und Karten werden ausgegraut, die Zeile verschwindet aus der Vorschau.
            checkBox.IsChecked = false;
            Assert.False(vm.ShowWheelHint);
            Assert.False(panel.IsEnabled);
            Assert.Equal(0.55, panel.Opacity);
            Assert.False(hint.ShowLayoutHint);

            checkBox.IsChecked = true;
            Assert.True(panel.IsEnabled);
            Assert.True(hint.ShowLayoutHint);
        });
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

    // ---- Video

    private static System.Windows.FrameworkElement ShowTab(AdminWindow window, string header)
    {
        var tabs = FindTabControl(window)!;
        tabs.SelectedItem = tabs.Items.OfType<System.Windows.Controls.TabItem>().Single(t => t.Header as string == header);
        var root = (System.Windows.FrameworkElement)window.Content;
        root.Measure(new System.Windows.Size(1100, 750));
        root.Arrange(new System.Windows.Rect(0, 0, 1100, 750));
        root.UpdateLayout();
        return root;
    }

    [StaFact]
    public void AdminWindow_HasVideoTab_AfterFlowTab_AndKeepsOtherTabNeighbours()
    {
        WithWindow((window, _) =>
        {
            var tabs = FindTabControl(window)!;
            var headers = tabs.Items.OfType<System.Windows.Controls.TabItem>().Select(t => t.Header as string).ToList();

            Assert.Equal(headers.IndexOf("Ablauf") + 1, headers.IndexOf("Video"));
            Assert.Equal(headers.IndexOf("Layout") + 1, headers.IndexOf("Gastansicht"));
        });
    }

    [StaTheory]
    [InlineData(AdminThemeMode.Light)]
    [InlineData(AdminThemeMode.Dark)]
    public void AdminWindow_VideoTab_LaysOutInBothThemes_WithAllControls(AdminThemeMode theme)
    {
        WithWindow((window, vm) =>
        {
            vm.IsAdminThemeLight = theme == AdminThemeMode.Light;
            vm.IsAdminThemeDark = theme == AdminThemeMode.Dark;
            Assert.Equal(theme == AdminThemeMode.Dark, AdminThemeManager.IsDarkPalette(window.Resources));

            var root = ShowTab(window, "Video");

            var names = FindDescendants<System.Windows.Controls.Primitives.ToggleButton>(root)
                .Select(c => System.Windows.Automation.AutomationProperties.GetName(c)).ToList();
            Assert.Contains("Videomodus aktiv", names);
            var combos = FindDescendants<System.Windows.Controls.ComboBox>(root)
                .Select(c => System.Windows.Automation.AutomationProperties.GetName(c)).ToList();
            Assert.DoesNotContain("Videoqualität", combos);   // steht jetzt im Tab „Speicher“
            Assert.Contains("Mikrofon", combos);
            Assert.NotEmpty(FindDescendants<NumberStepper>(root));
            Assert.True(root.ActualHeight > 0);
        });
    }

    [StaFact]
    public void AdminWindow_VideoTab_IsGreyedOutWhenVideoModeIsOff()
    {
        WithWindow((window, vm) =>
        {
            var root = ShowTab(window, "Video");
            var panel = (System.Windows.Controls.StackPanel)window.FindName("VideoSettingsPanel");
            var mic = FindDescendants<System.Windows.Controls.ComboBox>(root)
                .Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Mikrofon");
            Assert.True(panel.IsEnabled);
            Assert.True(mic.IsEnabled);
            Assert.Equal(1.0, panel.Opacity);

            vm.VideoEnabled = false;

            Assert.False(panel.IsEnabled);
            Assert.False(mic.IsEnabled);
            Assert.True(panel.Opacity < 1.0);

            vm.VideoEnabled = true;
            vm.VideoRecordAudio = false;   // nur das Mikrofon ist dann aus
            Assert.True(panel.IsEnabled);
            Assert.False(mic.IsEnabled);
        });
    }

    private static List<string> ControlNames<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject =>
        FindDescendants<T>(root).Select(c => System.Windows.Automation.AutomationProperties.GetName(c)).ToList();

    [StaTheory]
    [InlineData(AdminThemeMode.Light)]
    [InlineData(AdminThemeMode.Dark)]
    public void AdminWindow_StorageTab_HoldsVideoFolderPrefixSubfolderAndQuality_VideoTabDoesNot(AdminThemeMode theme)
    {
        WithWindow((window, vm) =>
        {
            vm.IsAdminThemeLight = theme == AdminThemeMode.Light;
            vm.IsAdminThemeDark = theme == AdminThemeMode.Dark;

            var storage = ShowTab(window, "Speicher");
            Assert.True(storage.ActualHeight > 0);
            Assert.Contains("Speicherordner der Videos", ControlNames<System.Windows.Controls.TextBox>(storage));
            Assert.Contains("Dateipräfix der Videos", ControlNames<System.Windows.Controls.TextBox>(storage));
            Assert.Contains("Dateipräfix der Fotos", ControlNames<System.Windows.Controls.TextBox>(storage));
            Assert.Contains("Videoqualität", ControlNames<System.Windows.Controls.ComboBox>(storage));
            Assert.Contains(FindDescendants<System.Windows.Controls.Button>(storage), b => Equals(b.Command, vm.ChooseVideoFolderCommand));
            Assert.Contains(FindDescendants<System.Windows.Controls.Button>(storage), b => Equals(b.Command, vm.OpenVideoFolderCommand));
            Assert.Contains(FindDescendants<System.Windows.Controls.Button>(storage), b => Equals(b.Command, vm.ResetVideoFolderCommand));
            Assert.Contains(FindDescendants<System.Windows.Controls.CheckBox>(storage),
                c => c.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.ParentBinding.Path.Path == nameof(vm.VideoDateSubfolder));

            var video = ShowTab(window, "Video");
            Assert.DoesNotContain("Speicherordner der Videos", ControlNames<System.Windows.Controls.TextBox>(video));
            Assert.DoesNotContain("Dateipräfix der Videos", ControlNames<System.Windows.Controls.TextBox>(video));
            Assert.DoesNotContain("Videoqualität", ControlNames<System.Windows.Controls.ComboBox>(video));
            Assert.DoesNotContain(FindDescendants<System.Windows.Controls.Button>(video),
                b => Equals(b.Command, vm.ChooseVideoFolderCommand) || Equals(b.Command, vm.OpenVideoFolderCommand) || Equals(b.Command, vm.ResetVideoFolderCommand));
            Assert.DoesNotContain(FindDescendants<System.Windows.Controls.CheckBox>(video),
                c => c.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.ParentBinding.Path.Path == nameof(vm.VideoDateSubfolder));
        });
    }

    [StaFact]
    public void AdminWindow_StorageTab_VideoCardIsGreyedOutWhenVideoModeIsOff_PhotoCardStaysUsable()
    {
        WithWindow((window, vm) =>
        {
            var root = ShowTab(window, "Speicher");
            var panel = (System.Windows.Controls.StackPanel)window.FindName("VideoStoragePanel");
            var videoPrefix = FindDescendants<System.Windows.Controls.TextBox>(root)
                .Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Dateipräfix der Videos");
            var photoPrefix = FindDescendants<System.Windows.Controls.TextBox>(root)
                .Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Dateipräfix der Fotos");
            Assert.True(panel.IsEnabled);
            Assert.True(videoPrefix.IsEnabled);
            Assert.Equal(1.0, panel.Opacity);

            vm.VideoEnabled = false;

            Assert.False(panel.IsEnabled);
            Assert.False(videoPrefix.IsEnabled);
            Assert.True(panel.Opacity < 1.0);
            Assert.True(photoPrefix.IsEnabled);

            vm.VideoEnabled = true;
            Assert.True(panel.IsEnabled);
            Assert.True(videoPrefix.IsEnabled);
            Assert.Equal(1.0, panel.Opacity);
        });
    }

    [StaTheory]
    [InlineData(AdminThemeMode.Light)]
    [InlineData(AdminThemeMode.Dark)]
    public void AdminWindow_PrinterTab_HoldsPrintToggle_AndGreysOutRestWhenPrintingIsOff(AdminThemeMode theme)
    {
        WithWindow((window, vm) =>
        {
            vm.IsAdminThemeLight = theme == AdminThemeMode.Light;
            vm.IsAdminThemeDark = theme == AdminThemeMode.Dark;

            // Der Schalter steht nicht mehr im Tab „Ablauf“.
            var flow = ShowTab(window, "Ablauf");
            Assert.DoesNotContain(FindDescendants<System.Windows.Controls.CheckBox>(flow),
                c => System.Windows.Automation.AutomationProperties.GetName(c) == "Fotos nach der Aufnahme drucken");
            Assert.DoesNotContain(FindDescendants<NumberStepper>(flow), s => s.Maximum == SettingsLimits.ExpectedPrintMax);

            var root = ShowTab(window, "Drucker");
            var toggle = FindDescendants<System.Windows.Controls.CheckBox>(root)
                .Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Fotos nach der Aufnahme drucken");
            var panel = (System.Windows.Controls.StackPanel)window.FindName("PrinterSettingsPanel");
            var printers = FindDescendants<System.Windows.Controls.ComboBox>(root).First();
            Assert.True(toggle.IsEnabled);
            Assert.True(panel.IsEnabled);
            Assert.Equal(1.0, panel.Opacity);

            vm.PrintingEnabled = false;

            Assert.True(toggle.IsEnabled);   // der Schalter selbst bleibt bedienbar
            Assert.False(panel.IsEnabled);
            Assert.False(printers.IsEnabled);
            Assert.True(panel.Opacity < 1.0);

            vm.PrintingEnabled = true;
            Assert.True(panel.IsEnabled);
            Assert.True(printers.IsEnabled);
        });
    }

    [StaFact]
    public void AdminWindow_SecurityTab_IsCalledSecurityAndData_WithThreeCards()
    {
        WithWindow((window, _) =>
        {
            var tabs = FindTabControl(window)!;
            var headers = tabs.Items.OfType<System.Windows.Controls.TabItem>().Select(t => t.Header as string).ToList();
            Assert.DoesNotContain("Sicherheit", headers);
            Assert.Contains("Sicherheit & Daten", headers);

            var root = ShowTab(window, "Sicherheit & Daten");
            var titles = FindDescendants<System.Windows.Controls.TextBlock>(root).Select(t => t.Text).ToList();
            Assert.Contains("PIN", titles);
            Assert.Contains("Einstellungen teilen", titles);
            Assert.Contains("Standardwerte", titles);
        });
    }

    [StaFact]
    public void AdminWindow_SecurityTab_CardsAreLaidOutInBothThemes()
    {
        foreach (var theme in new[] { AdminThemeMode.Light, AdminThemeMode.Dark })
        {
            WithWindow((window, vm) =>
            {
                vm.IsAdminThemeLight = theme == AdminThemeMode.Light;
                vm.IsAdminThemeDark = theme == AdminThemeMode.Dark;
                var root = ShowTab(window, "Sicherheit & Daten");
                var buttons = FindDescendants<System.Windows.Controls.Button>(root)
                    .Select(b => System.Windows.Automation.AutomationProperties.GetName(b)).ToList();
                Assert.Contains("Einstellungen exportieren", buttons);
                Assert.Contains("Einstellungen importieren", buttons);
                Assert.Contains("Standardwerte wiederherstellen", buttons);
                Assert.True(root.ActualHeight > 0);
            });
        }
    }

    [StaFact]
    public void AdminWindow_VideoTab_SuccessTextPlaceholderShowsDefaultOnlyWhileEmpty()
    {
        WithWindow((window, vm) =>
        {
            var root = ShowTab(window, "Video");
            var placeholder = FindDescendants<System.Windows.Controls.TextBlock>(root)
                .Single(t => t.Text == VideoSettings.DefaultSuccessText);
            Assert.Equal(System.Windows.Visibility.Visible, placeholder.Visibility);

            vm.VideoSuccessText = "Danke!";

            Assert.Equal(System.Windows.Visibility.Collapsed, placeholder.Visibility);
        });
    }

    [StaFact]
    public void AdminWindow_GuestScreenTab_ShowsVideoLineOnTop_OnlyWithVideoMode()
    {
        WithWindow((window, vm) =>
        {
            vm.VideoHintStyle = HintStyle.Banner;
            var root = ShowTab(window, "Gastansicht");

            var hints = FindDescendants<GuestHint>(root).ToList();
            Assert.Equal(2, hints.Count);
            Assert.Equal(HintPosition.Bottom, hints[0].Position);   // Foto-Zeile wie bisher zuerst
            Assert.Equal(HintPosition.Top, hints[1].Position);
            Assert.Equal("Drücke die Eingabetaste für ein Video 🎬", hints[1].Text);
            Assert.Equal(HintStyle.Banner, hints[1].HintStyle);
            Assert.Equal(System.Windows.Visibility.Visible, hints[1].Visibility);

            vm.VideoHintText = "Los: {Videotaste_1}";
            Assert.Equal("Los: Eingabetaste", hints[1].Text);

            vm.VideoEnabled = false;
            Assert.Equal(System.Windows.Visibility.Collapsed, hints[1].Visibility);
            var panel = (System.Windows.Controls.StackPanel)window.FindName("VideoHintPanel");
            Assert.False(panel.IsEnabled);

            vm.VideoEnabled = true;
            vm.VideoHintText = "";   // automatischer Text: ohne Starttaste leer
            Keys(vm, KeyAction.VideoStart).Keys.ToList().ForEach(k => k.RemoveCommand.Execute(null));
            Assert.Equal(System.Windows.Visibility.Collapsed, hints[1].Visibility);   // ohne Starttaste keine Zeile
        });
    }

    [StaFact]
    public void AdminWindow_KeysTab_ListsVideoActions_AndShowsStartKeyHint()
    {
        WithWindow((window, vm) =>
        {
            var root = ShowTab(window, "Tasten");

            var titles = FindDescendants<System.Windows.Controls.TextBlock>(root).Select(t => t.Text).ToList();
            Assert.Contains("Video starten", titles);
            Assert.Contains("Video beenden", titles);
            Assert.DoesNotContain(titles, t => t.StartsWith("Ohne Taste für „Video starten“"));

            Keys(vm, KeyAction.VideoStart).Keys.ToList().ForEach(k => k.RemoveCommand.Execute(null));
            var shown = FindDescendants<System.Windows.Controls.TextBlock>(root)
                .Single(t => t.Text.StartsWith("Ohne Taste für „Video starten“"));
            Assert.Equal(System.Windows.Visibility.Visible, shown.Visibility);
        });
    }

    private static KeyActionEditor Keys(AdminViewModel vm, KeyAction action) => vm.KeyEditors.Single(e => e.Action == action);

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
