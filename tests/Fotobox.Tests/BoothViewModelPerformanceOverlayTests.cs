using System.Windows.Input;
using Fotobox.Core;
using Fotobox.ViewModels.Booth;
using Fotobox.Views.Booth;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Gastansicht, Taste „Leistungsanzeige ein/aus“: wirkt in jedem Zustand und unabhängig vom Videomodus; das Ein-/Ausschalten selbst
/// löst nur ein Ereignis aus (teilt Harness und Fakes mit den übrigen Gast-Tests; die WPF-Collection kommt vom Hauptteil der Klasse).
/// </summary>
public partial class BoothViewModelTests
{
    private static readonly KeyChord CtrlAltY = new(Key.Y, ModifierKeys.Control | ModifierKeys.Alt);

    [Fact]
    public void PerformanceOverlayKey_InIdle_Toggles()
    {
        var h = new Harness();

        Assert.Equal(BoothKeyAction.TogglePerformanceOverlay, h.ViewModel.HandleKey(CtrlAltY));
        Assert.Equal(BoothState.Idle, h.Controller.State);
    }

    [Fact]
    public void PerformanceOverlayKey_InCountdown_Toggles_AndDoesNotTouchTheCountdown()
    {
        var h = new Harness();
        h.Settings.Current.Flow.CountdownSeconds = 3;
        Assert.True(h.ViewModel.TryTrigger());
        Assert.Equal(BoothState.Countdown, h.Controller.State);

        Assert.Equal(BoothKeyAction.TogglePerformanceOverlay, h.ViewModel.HandleKey(CtrlAltY));
        h.ViewModel.TogglePerformanceOverlay();

        Assert.Equal(BoothState.Countdown, h.Controller.State);
    }

    [Fact]
    public void PerformanceOverlayKey_InRecording_Toggles_AndKeepsRecording()
    {
        var h = RecordingHarness();

        Assert.Equal(BoothKeyAction.TogglePerformanceOverlay, h.ViewModel.HandleKey(CtrlAltY));
        h.ViewModel.TogglePerformanceOverlay();

        Assert.Equal(BoothState.Recording, h.Controller.State);
        Assert.Equal(0, h.Camera.LastRecording!.StopCalls);
    }

    [Fact]
    public void PerformanceOverlayKey_WorksWithVideoModeOff()
    {
        var h = VideoHarness(v => v.Enabled = false);

        Assert.Equal(BoothKeyAction.None, h.ViewModel.HandleKey(Enter));   // Videotasten ruhen ...
        Assert.Equal(BoothKeyAction.TogglePerformanceOverlay, h.ViewModel.HandleKey(CtrlAltY));   // ... die Leistungsanzeige nicht
    }

    [Fact]
    public void PerformanceOverlayKey_FollowsTheSettings_AndEmptyListMapsNothing()
    {
        var h = new Harness();
        h.Settings.Current.Input.PerformanceOverlayKeys = ["F10"];
        h.Settings.RaiseSettingsChanged();

        Assert.Equal(BoothKeyAction.TogglePerformanceOverlay, h.ViewModel.HandleKey(new KeyChord(Key.F10, ModifierKeys.None)));
        Assert.Equal(BoothKeyAction.None, h.ViewModel.HandleKey(CtrlAltY));

        h.Settings.Current.Input.PerformanceOverlayKeys = [];
        h.Settings.RaiseSettingsChanged();

        Assert.Equal(BoothKeyAction.None, h.ViewModel.HandleKey(new KeyChord(Key.F10, ModifierKeys.None)));
        Assert.Equal(BoothKeyAction.None, h.ViewModel.HandleKey(CtrlAltY));
    }

    [Fact]
    public void PerformanceOverlayKey_OtherActionsKeepPrecedence()
    {
        var h = new Harness();
        h.Settings.Current.Input.PerformanceOverlayKeys = ["Space", "Ctrl+Alt+A"];   // von Hand eingetragene Doppelbelegung
        h.Settings.RaiseSettingsChanged();

        Assert.Equal(BoothKeyAction.Trigger, h.ViewModel.HandleKey(Space));
        Assert.Equal(BoothKeyAction.OpenAdmin, h.ViewModel.HandleKey(new KeyChord(Key.A, ModifierKeys.Control | ModifierKeys.Alt)));
    }

    [Fact]
    public void TogglePerformanceOverlay_RaisesTheEventOncePerCall_AndIsHarmlessWithoutSubscriber()
    {
        var h = new Harness();
        h.ViewModel.TogglePerformanceOverlay();   // kein Abonnent: keine Ausnahme

        var raised = 0;
        h.ViewModel.PerformanceOverlayToggleRequested += () => raised++;
        h.ViewModel.TogglePerformanceOverlay();
        h.ViewModel.TogglePerformanceOverlay();

        Assert.Equal(2, raised);
        Assert.Equal(BoothState.Idle, h.Controller.State);
    }

    [StaFact]
    public void BoothWindow_OverlayKey_RaisesTheToggleEvent_OncePerKeyDown_AndIgnoresRepeats()
    {
        var h = new Harness();
        var window = new BoothWindow(h.ViewModel, new FakeAdminLauncher(), h.Settings, new NoDisplays());
        using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters());
        h.Settings.Current.Input.PerformanceOverlayKeys = ["F10"];
        h.Settings.RaiseSettingsChanged();
        var raised = 0;
        h.ViewModel.PerformanceOverlayToggleRequested += () => raised++;

        var args = RaiseKeyDown(window, source, Key.F10);
        Assert.True(args.Handled);
        Assert.Equal(1, raised);

        RaiseKeyDown(window, source, Key.F10, isRepeat: true);
        Assert.Equal(1, raised);
        Assert.Equal(BoothState.Idle, h.Controller.State);
    }

    [Fact]
    public void PerformanceOverlay_StartsHidden_AndTheToggleEventSwitchesIt()
    {
        var h = new Harness();
        var overlay = h.ViewModel.PerformanceOverlay;
        Assert.False(overlay.IsVisible);

        try
        {
            h.ViewModel.TogglePerformanceOverlay();
            Assert.True(overlay.IsVisible);
            Assert.Equal(BoothState.Idle, h.Controller.State);   // beeinflusst den Ablauf nicht
        }
        finally
        {
            h.ViewModel.TogglePerformanceOverlay();
        }
        Assert.False(overlay.IsVisible);
    }

    [Fact]
    public void Shutdown_StopsThePerformanceOverlay()
    {
        var h = new Harness();
        h.ViewModel.TogglePerformanceOverlay();
        Assert.True(h.ViewModel.PerformanceOverlay.IsVisible);

        h.ViewModel.Shutdown();

        Assert.False(h.ViewModel.PerformanceOverlay.IsVisible);
    }

    [StaFact]
    public void BoothWindow_PerformanceOverlay_SitsBeforeTheFlash_OutsideTheZoomHost_AndNeverTakesInput()
    {
        WpfCollection.EnsurePackUris();
        var h = new Harness();
        var window = new BoothWindow(h.ViewModel, new FakeAdminLauncher(), h.Settings, new NoDisplays());
        var root = (System.Windows.Controls.Grid)window.Content;
        var overlay = (System.Windows.FrameworkElement)window.FindName("PerformanceOverlayView");
        var flash = (System.Windows.FrameworkElement)window.FindName("FlashOverlay");

        Assert.IsType<PerformanceOverlay>(overlay);
        Assert.False(overlay.IsHitTestVisible);
        Assert.False(((System.Windows.Controls.UserControl)overlay).Focusable);
        Assert.Same(root, System.Windows.Media.VisualTreeHelper.GetParent(overlay));   // Geschwister im äußeren Grid
        Assert.Equal(root.Children.IndexOf(flash) - 1, root.Children.IndexOf(overlay));   // direkt vor dem Blitz

        // Nicht im Zoom-Host (sonst zoomte und verschöbe es sich mit der Kisscam).
        for (System.Windows.DependencyObject? p = overlay; p is not null; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
            Assert.False(p is Fotobox.Views.Booth.Heart.LiveZoomHost);
        var zoomHost = FindLogical<Fotobox.Views.Booth.Heart.LiveZoomHost>(window)!;
        Assert.Null(FindLogical<PerformanceOverlay>(zoomHost));

        // Rechts oben, Rand wie die REC-Anzeige (oben links).
        Assert.Equal(System.Windows.HorizontalAlignment.Right, overlay.HorizontalAlignment);
        Assert.Equal(System.Windows.VerticalAlignment.Top, overlay.VerticalAlignment);
        Assert.Equal(new System.Windows.Thickness(0, 44, 48, 0), overlay.Margin);
    }

    [StaFact]
    public void BoothWindow_PerformanceOverlay_FollowsTheToggle_AndShowsTheRows()
    {
        WpfCollection.EnsurePackUris();
        var h = new Harness();
        var window = new BoothWindow(h.ViewModel, new FakeAdminLauncher(), h.Settings, new NoDisplays());
        var overlay = (System.Windows.FrameworkElement)window.FindName("PerformanceOverlayView");
        var root = (System.Windows.FrameworkElement)window.Content;
        Assert.Equal(System.Windows.Visibility.Collapsed, overlay.Visibility);

        try
        {
            h.Settings.Current.PerformanceOverlay.Style = PerformanceOverlayStyle.Bars;
            h.ViewModel.TogglePerformanceOverlay();
            root.Measure(new System.Windows.Size(1920, 1080));
            root.Arrange(new System.Windows.Rect(0, 0, 1920, 1080));
            root.UpdateLayout();

            Assert.Equal(System.Windows.Visibility.Visible, overlay.Visibility);
            Assert.True(overlay.ActualWidth > 0 && overlay.ActualHeight > 0);
            Assert.True(overlay.ActualWidth < 1920 / 2, "Die Anzeige soll klein bleiben.");
            Assert.Equal(h.ViewModel.PerformanceOverlay.Rows.Count > 0, overlay.ActualHeight > 0);
        }
        finally
        {
            h.ViewModel.TogglePerformanceOverlay();
        }
        Assert.Equal(System.Windows.Visibility.Collapsed, overlay.Visibility);
    }

    private sealed class FixedMetrics : Fotobox.Services.Diagnostics.ISystemMetricsSource
    {
        public Fotobox.Services.Diagnostics.SystemMetricsSnapshot Sample(Fotobox.Services.Diagnostics.GpuSelection? gpu) => new(
            CpuPercent: 34, CpuClockMhz: 3200, CpuTemperatureC: 61, GpuPercent: 75, GpuMemoryUsedBytes: 3.8 * 1024 * 1024 * 1024,
            GpuMemoryTotalBytes: 4.0 * 1024 * 1024 * 1024, RamUsedBytes: 7.4 * 1024 * 1024 * 1024, RamTotalBytes: 16.0 * 1024 * 1024 * 1024);

        public void Dispose() { }
    }

    /// <summary>
    /// Sichtprüfung per Bilddatei, kein Test im engeren Sinn: Läuft nur mit <c>FOTOBOX_RENDER_DIR</c> (sonst sofort beendet). Rendert die
    /// Leistungsanzeige in beiden Darstellungen (Text, Balken) mit Warnfarben auf einem synthetischen Hintergrund.
    /// </summary>
    [StaFact]
    [Trait("Category", "Visual")]
    public void Render_PerformanceOverlay_TextAndBars()
    {
        var dir = Environment.GetEnvironmentVariable("FOTOBOX_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            return;
        System.IO.Directory.CreateDirectory(dir);
        WpfCollection.EnsurePackUris();

        var h = new Harness();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var vm = new PerformanceOverlayViewModel(h.Settings, time, h.ViewModel.Fun, h.ViewModel.Peace, new FixedMetrics());
        vm.Toggle();
        vm.PendingSample.Wait();
        time.Advance(TimeSpan.FromSeconds(1));
        vm.PendingSample.Wait();
        time.Advance(TimeSpan.FromSeconds(1));
        var view = new PerformanceOverlay { DataContext = vm };
        var root = new System.Windows.Controls.Grid
        {
            Width = 1000, Height = 700,
            Background = new System.Windows.Media.LinearGradientBrush(
                System.Windows.Media.Color.FromRgb(0x5B, 0x8D, 0xC9), System.Windows.Media.Color.FromRgb(0x2E, 0x3A, 0x4E), 90),
        };
        root.Children.Add(view);

        void Save(string name)
        {
            root.Measure(new System.Windows.Size(1000, 700));
            root.Arrange(new System.Windows.Rect(0, 0, 1000, 700));
            root.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1000, 700, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = System.IO.File.Create(System.IO.Path.Combine(dir, name));
            encoder.Save(stream);
        }

        Save("leistungsanzeige-text.png");
        h.Settings.Current.PerformanceOverlay.Style = PerformanceOverlayStyle.Bars;
        h.Settings.Current.PerformanceOverlay.FontSize = 24;
        h.Settings.RaiseSettingsChanged();
        Save("leistungsanzeige-balken.png");
        vm.Dispose();
    }
}
