using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Services.Detection.Acceleration;
using Fotobox.ViewModels.Admin;
using Fotobox.Views.Admin;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Sichtprüfung per Bilddatei, kein Test im engeren Sinn: Läuft nur, wenn die Umgebungsvariable <c>FOTOBOX_RENDER_DIR</c> auf einen
/// Ordner zeigt (sonst sofort beendet, auch in der CI). Rendert die Admin-Tabs Drucker (Druck an/aus), Ablauf, Video, Sicherheit &amp; Daten
/// und Speicher (auch bei ausgeschaltetem Videomodus), die Fun-Seiten Mittelfinger und Peace (dazu die Karte mit „Analysen pro Sekunde“) sowie den Tooltip des Info-Symbols
/// in Hell und Dunkel als PNG, jeweils mit dem ganzen Inhalt des ScrollViewers (nicht nur dem sichtbaren Ausschnitt).
/// Aufruf: <c>FOTOBOX_RENDER_DIR=C:\pfad\render dotnet test --filter "Category=Visual"</c>.
/// </summary>
[Collection(WpfCollection.Name)]
public class AdminVisualRenderTests
{
    private static string? RenderDir()
    {
        var dir = Environment.GetEnvironmentVariable("FOTOBOX_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            return null;
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SavePng(Visual visual, int width, int height, string path, Brush? background)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        if (background is not null)
        {
            var fill = new DrawingVisual();
            using (var dc = fill.RenderOpen())
                dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            bitmap.Render(fill);
        }
        if (visual is FrameworkElement element && VisualTreeHelper.GetParent(visual) is not null)
        {
            // Ein Element mitten im Baum würde mit seinem Versatz zum Elternteil gerendert (oben leer, unten abgeschnitten):
            // Über einen VisualBrush landet es immer links oben im Bild.
            var copy = new DrawingVisual();
            using (var dc = copy.RenderOpen())
                dc.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.Fill }, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            bitmap.Render(copy);
        }
        else
        {
            bitmap.Render(visual);
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static string ThemeName(AdminThemeMode mode) => mode.ToString().ToLowerInvariant();

    /// <summary>Die Testumgebung eines Admin-Fensters mit allen Diensten (wie in den Fun-Tab-Tests), räumt am Ende auf.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-render-" + Guid.NewGuid().ToString("N"));

        public AdminViewModel ViewModel { get; }
        public AdminWindow Window { get; }
        public FrameworkElement Root => (FrameworkElement)Window.Content;
        public Brush Background => (Brush)Window.FindResource("BgBrush");

        public Rig(AdminThemeMode mode, Action<AdminViewModel>? prepare = null, IAccelerationService? acceleration = null)
        {
            SynchronizationContext.SetSynchronizationContext(null);
            Directory.CreateDirectory(_dir);
            var store = new AdminViewModelTests.FakeStore(_dir);
            ViewModel = new AdminViewModel(store, new AdminViewModelTests.FakePinService(store),
                new AdminViewModelTests.FakeCamera(), new AdminViewModelTests.FakePrint(), new AdminViewModelTests.FakeUi(),
                new TrackingTimeProvider(), new AdminViewModelTests.FakeDisplays(AdminViewModelTests.AdminMonitors.Primary),
                new FakeDetectionService(), new FakeSoundPlayer(), new FakeGestureService(), acceleration: acceleration);
            ViewModel.Draft.Appearance.AdminTheme = mode;
            prepare?.Invoke(ViewModel);
            Window = new AdminWindow(ViewModel);
            AdminThemeManager.Apply(Window, mode);
        }

        public void SelectTab(string header)
        {
            var tabs = FindTabControl(Root);
            Assert.NotNull(tabs);
            tabs!.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header as string == header);
            Layout(Root, 1100, 750);
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Wählt den Tab, legt ihn aus und speichert den ganzen Inhalt des ScrollViewers (Karten) auf dem Fensterhintergrund.</summary>
    private static string RenderTab(string dir, string tab, string name, AdminThemeMode mode, Action<AdminViewModel>? prepare = null,
        Action<Rig, FrameworkElement>? extra = null, string? scope = null, IAccelerationService? acceleration = null)
    {
        using var rig = new Rig(mode, prepare, acceleration);
        rig.SelectTab(tab);
        DependencyObject searchRoot = rig.Root;
        if (scope is not null)
        {
            // Seiten des Fun-Tabs liegen als Geschwister im Baum: nur die gewünschte benannte Seite durchsuchen und prüfen, dass sie sichtbar ist.
            var page = FindByName(rig.Root, scope);
            Assert.NotNull(page);
            Assert.Equal(Visibility.Visible, page!.Visibility);
            Assert.True(page.ActualHeight > 0, $"{name}: Seite {scope} hat keine Größe");
            searchRoot = page;
        }
        var scroll = FindVisibleScrollViewer(searchRoot);
        Assert.NotNull(scroll);
        var card = (FrameworkElement)scroll!.Content;
        Layout(rig.Root, 1100, 750);
        var width = (int)Math.Ceiling(card.ActualWidth);
        var height = (int)Math.Ceiling(card.ActualHeight);
        Assert.True(width > 0 && height > 0, $"{name}: leerer Inhalt");

        var path = Path.Combine(dir, $"admin_{name}_{ThemeName(mode)}.png");
        SavePng(card, width, height, path, rig.Background);
        extra?.Invoke(rig, card);
        return path;
    }

    [Trait("Category", "Visual")]
    [StaTheory]
    [InlineData(AdminThemeMode.Light)]
    [InlineData(AdminThemeMode.Dark)]
    public void Render_AdminTabs(AdminThemeMode mode)
    {
        var dir = RenderDir();
        if (dir is null)
            return;

        RenderTab(dir, "Drucker", "printer_on", mode, vm => vm.PrintingEnabled = true);
        RenderTab(dir, "Drucker", "printer_off", mode, vm => vm.PrintingEnabled = false);
        RenderTab(dir, "Ablauf", "flow", mode);
        RenderTab(dir, "Sicherheit & Daten", "security", mode);
        RenderTab(dir, "Speicher", "storage", mode);
        RenderTab(dir, "Speicher", "storage_video_off", mode, vm => vm.VideoEnabled = false);
        RenderTab(dir, "Video", "video", mode);
        // Darstellung bei Standardzoom 80 % (der Inhalt wird auf Layoutgröße gerendert) und das ganze Fenster mit verkleinertem Inhalt.
        RenderTab(dir, "Darstellung", "appearance", mode, extra: (rig, _) =>
            SavePng(rig.Root, 1100, 750, Path.Combine(dir, $"admin_appearance_window80_{ThemeName(mode)}.png"), rig.Background));

        Assert.True(Directory.GetFiles(dir, $"admin_*_{ThemeName(mode)}.png").Length >= 6);
    }

    /// <summary>Das ganze Admin-Fenster auf Mindesthöhe: Die Tab-Leiste links scrollt (schmale Scrollleiste), der gewählte Tab bleibt sichtbar.</summary>
    [Trait("Category", "Visual")]
    [StaTheory]
    [InlineData(AdminThemeMode.Light)]
    [InlineData(AdminThemeMode.Dark)]
    public void Render_AdminTabStrip_AtMinimumHeight(AdminThemeMode mode)
    {
        var dir = RenderDir();
        if (dir is null)
            return;

        using var rig = new Rig(mode);
        Layout(rig.Root, 900, 620);
        SavePng(rig.Root, 900, 620, Path.Combine(dir, $"admin_tabstrip_minheight_{ThemeName(mode)}.png"), rig.Background);

        var tabs = FindTabControl(rig.Root)!;
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header as string == "Fun");
        rig.Window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, () => { });
        Layout(rig.Root, 900, 620);
        SavePng(rig.Root, 900, 620, Path.Combine(dir, $"admin_tabstrip_minheight_fun_{ThemeName(mode)}.png"), rig.Background);
    }

    /// <summary>Tab „Leistung“ mit erfundenen Geräten: laufende Zuordnung mit Rückfall, Messtabelle, Anbieter und Neustart-Hinweis.</summary>
    [Trait("Category", "Visual")]
    [StaTheory]
    [InlineData(AdminThemeMode.Light)]
    [InlineData(AdminThemeMode.Dark)]
    public void Render_AdminPerformanceTab(AdminThemeMode mode)
    {
        var dir = RenderDir();
        if (dir is null)
            return;

        var strong = AccelDevices.Gpu("Beispielkarte Pro", discrete: true, index: 0, memory: 12288, device: 1);
        var integrated = AccelDevices.Gpu("Beispielgrafik Integriert", discrete: false, index: 1, memory: 512, device: 2);
        var devices = AccelDevices.WithCpu(strong, integrated);
        var plan = new List<ModelPlacement>
        {
            new(ModelId.Yolox, strong.Name, AcceleratorKind.Gpu, strong.Key, false),
            new(ModelId.YuNet, "CPU", AcceleratorKind.Cpu, AcceleratorDevice.CpuKey, false),
            new(ModelId.Palm, "CPU", AcceleratorKind.Cpu, AcceleratorDevice.CpuKey, true),
            new(ModelId.Landmark, strong.Name, AcceleratorKind.Gpu, strong.Key, false),
        };
        var benchmark = AccelDevices.Result(
            (AcceleratorDevice.CpuDevice, 11.8, 7.4, 6.1, 5.9),
            (strong, 5.2, 8.0, 3.4, 2.9),
            (integrated, 9.6, null, 5.8, 6.3));
        var state = FakeAccelerationService.Ready(devices, plan: plan) with
        {
            PlanFromCache = true,
            LastBenchmark = benchmark,
            RestartRecommended = true,
            Fallbacks = [new AccelerationFallback(
                ModelId.Palm, strong.Name, "Das Modell konnte nicht geladen werden.", false, DateTimeOffset.UtcNow)],
        };
        var service = new FakeAccelerationService(state)
        {
            Providers = () =>
            [
                new ProviderState("Beispiel-Beschleuniger A", "pkg.a", "Ready", true),
                new ProviderState("Beispiel-Beschleuniger B", "pkg.b", "NotReady", false),
            ],
        };

        RenderTab(dir, "Leistung", "performance", mode, vm =>
        {
            vm.InitializeAsync().GetAwaiter().GetResult();
        }, extra: (rig, _) =>
            SavePng(rig.Root, 1100, 750, Path.Combine(dir, $"admin_performance_window_{ThemeName(mode)}.png"), rig.Background),
            acceleration: service);
        RenderTab(dir, "Leistung", "performance_changed", mode, vm =>
        {
            vm.InitializeAsync().GetAwaiter().GetResult();
            vm.IsAcceleratorGpu = true;
        }, acceleration: new FakeAccelerationService(FakeAccelerationService.Ready(AccelDevices.WithCpu(strong))));

        Assert.True(Directory.GetFiles(dir, $"admin_performance*_{ThemeName(mode)}.png").Length == 3);
    }

    [Trait("Category", "Visual")]
    [StaTheory]
    [InlineData(AdminThemeMode.Light)]
    [InlineData(AdminThemeMode.Dark)]
    public void Render_AdminFunPages(AdminThemeMode mode)
    {
        var dir = RenderDir();
        if (dir is null)
            return;

        RenderTab(dir, "Fun", "fun_middlefinger", mode, vm =>
        {
            vm.FunPage = FunPage.MiddleFinger;
            vm.IsFunTabSelected = true;
        }, scope: "MiddleFingerPage");

        RenderTab(dir, "Fun", "fun_peace", mode, vm =>
        {
            vm.FunPage = FunPage.Peace;
            vm.IsFunTabSelected = true;
        }, (rig, _) =>
        {
            // Ausschnitt: die Karte mit „Analysen pro Sekunde“ (Vorfahre des Info-Symbols mit dem Stil „Card“).
            var info = FindByName(rig.Root, "PeaceAnalysesInfo");
            Assert.NotNull(info);
            var cardStyle = rig.Window.FindResource("Card");
            DependencyObject? current = info;
            while (current is not null && !(current is Border b && ReferenceEquals(b.Style, cardStyle)))
                current = VisualTreeHelper.GetParent(current);
            var card = Assert.IsType<Border>(current);
            SavePng(card, (int)Math.Ceiling(card.ActualWidth), (int)Math.Ceiling(card.ActualHeight),
                Path.Combine(dir, $"admin_fun_peace_analyses_{ThemeName(mode)}.png"), rig.Background);
        }, scope: "PeacePage");

        Assert.True(Directory.GetFiles(dir, $"admin_fun_*_{ThemeName(mode)}.png").Length >= 3);
    }

    [Trait("Category", "Visual")]
    [StaTheory]
    [InlineData(AdminThemeMode.Light)]
    [InlineData(AdminThemeMode.Dark)]
    public void Render_InfoBadgeToolTip(AdminThemeMode mode)
    {
        var dir = RenderDir();
        if (dir is null)
            return;

        using var rig = new Rig(mode, vm =>
        {
            vm.FunPage = FunPage.MiddleFinger;
            vm.IsFunTabSelected = true;
        });
        rig.SelectTab("Fun");

        var badge = FindByName(rig.Root, "MiddleFingerAnalysesInfo") as Button;
        Assert.NotNull(badge);
        Assert.Equal(AdminInfoTexts.MiddleFingerAnalysesPerSecond, badge!.ToolTip);

        // Ein echtes Popup lässt sich nicht in ein Bild rendern, und ein ToolTip nimmt keinen Elternknoten an: Das ToolTip-Element
        // selbst (impliziter Stil aus AdminTheme.xaml über die Ressourcen des Admin-Fensters, Text aus AdminInfoTexts) wird frei
        // ausgelegt und mit Rand auf dem Fensterhintergrund gerendert.
        const int pad = 24;
        var tip = new ToolTip { Content = badge.ToolTip, Resources = rig.Window.Resources, RenderTransform = new TranslateTransform(pad, pad) };
        Layout(tip, 560, 900);
        tip.Measure(new Size(560, 900));
        tip.Arrange(new Rect(0, 0, tip.DesiredSize.Width, tip.DesiredSize.Height));
        tip.UpdateLayout();
        var width = (int)Math.Ceiling(tip.ActualWidth) + 2 * pad;
        var height = (int)Math.Ceiling(tip.ActualHeight) + 2 * pad;
        Assert.True(width > 2 * pad + 40 && height > 2 * pad + 40);

        SavePng(tip, width, height, Path.Combine(dir, $"admin_tooltip_infobadge_{ThemeName(mode)}.png"), rig.Background);
    }

    private static TabControl? FindTabControl(DependencyObject parent)
    {
        if (parent is TabControl tabs)
            return tabs;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var found = FindTabControl(VisualTreeHelper.GetChild(parent, i));
            if (found is not null)
                return found;
        }
        return null;
    }

    /// <summary>Der äußerste ScrollViewer mit Inhalt, der Platz einnimmt (ausgeblendete Seiten des Fun-Tabs haben Höhe 0).</summary>
    private static ScrollViewer? FindVisibleScrollViewer(DependencyObject parent)
    {
        // Die scrollbare Tab-Leiste (PART_TabStripScroll) ist nicht der Inhalt des Tabs.
        if (parent is ScrollViewer scroll && scroll.Name != "PART_TabStripScroll" && scroll.Content is FrameworkElement && scroll.ActualHeight > 0)
            return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var found = FindVisibleScrollViewer(VisualTreeHelper.GetChild(parent, i));
            if (found is not null)
                return found;
        }
        return null;
    }

    private static FrameworkElement? FindByName(DependencyObject parent, string name)
    {
        if (parent is FrameworkElement element && element.Name == name)
            return element;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var found = FindByName(VisualTreeHelper.GetChild(parent, i), name);
            if (found is not null)
                return found;
        }
        return null;
    }
}
