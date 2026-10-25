using System.IO;
using Fotobox.Core;
using Fotobox.ViewModels.Admin;
using Fotobox.Views.Admin;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Prüft, dass die XAML-Dateien der Admin-Ansicht zur Laufzeit ladbar sind (ohne sie anzuzeigen).</summary>
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
