using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Fotobox.Core;
using Fotobox.Services.Detection.Acceleration;
using Fotobox.ViewModels.Admin;
using Fotobox.Views.Admin;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Ansicht des Admin-Tabs „Leistung“: Tab-Position, Layout in beiden Farbschemata, Bedienelemente und Messtabelle.</summary>
[Collection(WpfCollection.Name)]
public class AdminPerformanceViewTests
{
    private static readonly AcceleratorDevice Gpu = AccelDevices.Gpu("Testkarte Stark", discrete: true, index: 0, memory: 8192);

    private static void WithWindow(FakeAccelerationService? service, Action<AdminWindow, AdminViewModel> body, bool withDispatcherContext = false)
    {
        // Mit Dispatcher-Kontext verhält sich das Ansichtsmodell wie im Programm (Fortsetzungen und Ereignisse landen auf dem UI-Thread).
        SynchronizationContext.SetSynchronizationContext(withDispatcherContext
            ? new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher)
            : null);
        var dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-performance-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new AdminViewModelTests.FakeStore(dir);
            var vm = new AdminViewModel(store, new AdminViewModelTests.FakePinService(store), new AdminViewModelTests.FakeCamera(),
                new AdminViewModelTests.FakePrint(), new AdminViewModelTests.FakeUi(), acceleration: service);
            try
            {
                body(new AdminWindow(vm), vm);
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

    private static FakeAccelerationService ReadyService(params AcceleratorDevice[] devices) =>
        new(FakeAccelerationService.Ready(AccelDevices.WithCpu(devices)));

    private static TabControl FindTabControl(DependencyObject parent)
    {
        if (parent is TabControl tabs)
            return tabs;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            var found = TryFindTabControl(child);
            if (found is not null)
                return found;
        }
        throw new InvalidOperationException("Kein TabControl gefunden.");
    }

    private static TabControl? TryFindTabControl(DependencyObject parent)
    {
        if (parent is TabControl tabs)
            return tabs;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            var found = TryFindTabControl(child);
            if (found is not null)
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

    private static List<string> Names<T>(DependencyObject root) where T : DependencyObject =>
        FindDescendants<T>(root).Select(AutomationProperties.GetName).ToList();

    // Nur sichtbare Texte (das Fenster wird nicht angezeigt, IsVisible gälte nie): kein Element der Kette ist eingeklappt.
    private static List<string> Texts(DependencyObject root) =>
        FindDescendants<TextBlock>(root).Where(EffectivelyVisible).Select(t => t.Text).ToList();

    private static bool EffectivelyVisible(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
                return false;
        }
        return true;
    }

    [StaFact]
    public void AdminWindow_HasPerformanceTab_BeforeSecurity_AndFunStaysLast()
    {
        WithWindow(ReadyService(Gpu), (window, _) =>
        {
            var items = FindTabControl(window).Items.OfType<TabItem>().ToList();
            var headers = items.Select(t => t.Header as string).ToList();

            Assert.Equal(headers.IndexOf("Sicherheit & Daten") - 1, headers.IndexOf("Leistung"));
            Assert.Equal(headers.Count - 1, headers.IndexOf("Fun"));
            Assert.Equal("", items[headers.IndexOf("Leistung")].Tag);
        });
    }

    [StaTheory]
    [InlineData(AdminThemeMode.Light)]
    [InlineData(AdminThemeMode.Dark)]
    public void AdminWindow_PerformanceTab_LaysOutInBothThemes_WithAllControls(AdminThemeMode theme)
    {
        WithWindow(ReadyService(Gpu), (window, vm) =>
        {
            vm.IsAdminThemeLight = theme == AdminThemeMode.Light;
            vm.IsAdminThemeDark = theme == AdminThemeMode.Dark;
            Assert.Equal(theme == AdminThemeMode.Dark, AdminThemeManager.IsDarkPalette(window.Resources));

            var root = ShowTab(window, "Leistung");

            var radios = FindDescendants<RadioButton>(root).Where(r => r.GroupName == "AcceleratorMode").ToList();
            Assert.Equal(
                ["Automatisch (empfohlen)", "Nur Prozessor (CPU)", "Grafikkarte (GPU)", "KI-Beschleuniger (NPU)"],
                radios.Select(AutomationProperties.GetName));
            Assert.Contains("Grafikkarte", Names<ComboBox>(root));
            var buttons = Names<Button>(root);
            Assert.Contains("Jetzt messen", buttons);
            Assert.Contains("Beschleuniger installieren", buttons);
            Assert.Contains("Fotobox auf der leistungsstärksten Grafikkarte ausführen", Names<System.Windows.Controls.Primitives.ToggleButton>(root));
            var info = (Button)window.FindName("AccelerationInfo");
            Assert.Equal(AdminInfoTexts.Acceleration, AutomationProperties.GetHelpText(info));
            Assert.Equal(AdminInfoTexts.Acceleration, info.ToolTip);

            var texts = Texts(root);
            Assert.Contains("Hardwarebeschleunigung", texts);
            Assert.Contains("Messung", texts);
            Assert.Contains("Zusätzliche Beschleuniger", texts);
            Assert.Contains("Windows-Grafikeinstellung", texts);
            Assert.Contains("Personenerkennung (Zielerfassung): Prozessor (CPU)", texts);
            Assert.Contains("Noch keine Messung vorhanden. Klicken Sie auf „Jetzt messen“.", texts);
            Assert.True(root.ActualHeight > 0);
        });
    }

    [StaFact]
    public void AdminWindow_PerformanceTab_RendersWithoutService()
    {
        WithWindow(null, (window, _) =>
        {
            var root = ShowTab(window, "Leistung");

            Assert.Contains("Die Beschleunigung steht in dieser Ansicht nicht zur Verfügung.", Texts(root));
            var start = FindDescendants<Button>(root).Single(b => AutomationProperties.GetName(b) == "Jetzt messen");
            Assert.False(start.IsEnabled);
        });
    }

    [StaFact]
    public void AdminWindow_PerformanceTab_RadiosWriteIntoTheDraft_AndNpuIsDisabledWithoutNpu()
    {
        WithWindow(ReadyService(Gpu), (window, vm) =>
        {
            var root = ShowTab(window, "Leistung");
            var radios = FindDescendants<RadioButton>(root).Where(r => r.GroupName == "AcceleratorMode").ToList();

            Assert.True(radios[0].IsChecked);
            Assert.False(radios[3].IsEnabled);
            Assert.Contains(Texts(root), t => t.Contains("kein KI-Beschleuniger (NPU)"));

            radios[1].IsChecked = true;
            Assert.Equal(AcceleratorMode.Cpu, vm.Draft.Performance.Accelerator);
            radios[2].IsChecked = true;
            Assert.Equal(AcceleratorMode.Gpu, vm.Draft.Performance.Accelerator);
            Assert.False(radios[1].IsChecked);
            var combo = FindDescendants<ComboBox>(root).Single(c => AutomationProperties.GetName(c) == "Grafikkarte");
            Assert.True(combo.IsEnabled);
            Assert.Equal(2, combo.Items.Count);
            combo.SelectedIndex = 1;
            Assert.Equal(Gpu.Key, vm.Draft.Performance.GpuDeviceKey);
            Assert.Contains(Texts(root), t => t == AdminViewModel.PerformanceRestartText);
        });
    }

    [StaFact]
    public void AdminWindow_PerformanceTab_ShowsTheMeasurementTable_WithFastestAndFailedCells()
    {
        var service = ReadyService(Gpu);
        service.Update(s => s with
        {
            LastBenchmark = AccelDevices.Result((AcceleratorDevice.CpuDevice, 10.0, 8.0, 6.0, null), (Gpu, 5.0, 9.0, 3.0, 4.0)),
        });
        WithWindow(service, (window, _) =>
        {
            var root = ShowTab(window, "Leistung");
            var texts = Texts(root);

            Assert.DoesNotContain("Noch keine Messung vorhanden. Klicken Sie auf „Jetzt messen“.", texts);
            Assert.Contains("Testkarte Stark", texts);
            Assert.Contains("Gesichtserkennung", texts);
            Assert.Contains("10,0 ms", texts);
            Assert.Contains("5,0 ms", texts);
            Assert.Contains("–", texts);   // fehlgeschlagene Zelle
            Assert.Contains(texts, t => t.StartsWith("Gemessen am "));
            // Die schnellste Zelle je Zeile ist fett (hier: mindestens die GPU bei Personenerkennung).
            var fast = FindDescendants<TextBlock>(root).First(t => t.Text == "5,0 ms");
            Assert.Equal(FontWeights.Bold, fast.FontWeight);
            var slow = FindDescendants<TextBlock>(root).First(t => t.Text == "10,0 ms");
            Assert.NotEqual(FontWeights.Bold, slow.FontWeight);
        });
    }

    [StaFact]
    public void AdminWindow_PerformanceTab_FallbackLineShowsReason()
    {
        var plan = FakeAccelerationService.CpuPlan().Select(p => p.Model == ModelId.Yolox ? p with { FellBack = true } : p).ToList();
        var state = FakeAccelerationService.Ready(AccelDevices.WithCpu(Gpu), plan: plan) with
        {
            Fallbacks = [new AccelerationFallback(ModelId.Yolox, Gpu.Name, "Testfehler", true, DateTimeOffset.UtcNow)],
        };
        WithWindow(new FakeAccelerationService(state), (window, _) =>
        {
            var root = ShowTab(window, "Leistung");

            var detail = FindDescendants<TextBlock>(root).First(t => t.Text.Contains("Testfehler"));
            Assert.True(EffectivelyVisible(detail));
            Assert.Contains("Rückfall von „Testkarte Stark“", detail.Text);
        });
    }

    [StaTheory]
    [InlineData(AdminThemeMode.Light)]
    [InlineData(AdminThemeMode.Dark)]
    public void AdminWindow_PerformanceTab_RunningMeasurementShowsProgressAndCancel(AdminThemeMode theme)
    {
        var service = ReadyService(Gpu);
        service.BenchmarkGate = new TaskCompletionSource<BenchmarkResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        WithWindow(service, (window, vm) =>
        {
            vm.IsAdminThemeLight = theme == AdminThemeMode.Light;
            vm.IsAdminThemeDark = theme == AdminThemeMode.Dark;
            var root = ShowTab(window, "Leistung");
            var bar = FindDescendants<ProgressBar>(root).Single(b => AutomationProperties.GetName(b) == "Fortschritt der Messung");
            var cancel = FindDescendants<Button>(root).Single(b => AutomationProperties.GetName(b) == "Messung abbrechen");
            Assert.False(EffectivelyVisible(bar));
            Assert.False(EffectivelyVisible(cancel));

            var running = vm.StartBenchmarkCommand.ExecuteAsync(null);
            service.LastBenchmarkProgress!.Report(new BenchmarkProgress(2, 8, "Testkarte Stark", null));
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            root.UpdateLayout();

            Assert.True(EffectivelyVisible(bar));
            Assert.Equal(25, bar.Value);
            Assert.True(EffectivelyVisible(cancel));
            Assert.Contains("Messe Testkarte Stark (2/8)", Texts(root));

            cancel.Command.Execute(null);
            PumpUntilDone(running);
        }, withDispatcherContext: true);
    }

    // Lässt den Dispatcher arbeiten, bis die Aufgabe fertig ist (ihre Fortsetzung läuft auf dem UI-Thread).
    private static void PumpUntilDone(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Thread.Sleep(5);
        }
        Assert.True(task.IsCompleted, "Aufgabe wurde nicht rechtzeitig fertig.");
    }
}
