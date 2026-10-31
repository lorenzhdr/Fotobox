using System.Windows.Input;
using Fotobox.Core;
using Fotobox.ViewModels.Booth;
using Fotobox.Views.Booth;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Gastansicht, Taste „Tracking-Overlay ein/aus“: wirkt in jedem Zustand, sichtbar wird das Overlay aber nur im Leerlauf und im
/// Countdown; die Einbindung im Fenster (Zoom-Host, Statuszeile außerhalb, keine Eingaben). Teilt Harness und Fakes mit den übrigen
/// Gast-Tests (die WPF-Collection kommt vom Hauptteil der Klasse).
/// </summary>
public partial class BoothViewModelTests
{
    private static readonly KeyChord CtrlAltX = new(Key.X, ModifierKeys.Control | ModifierKeys.Alt);

    [Fact]
    public void TrackingOverlayKey_Toggles_InIdleAndCountdown_AndDoesNotTouchTheFlow()
    {
        var h = new Harness();
        Assert.Equal(BoothKeyAction.ToggleTrackingOverlay, h.ViewModel.HandleKey(CtrlAltX));
        Assert.Equal(BoothState.Idle, h.Controller.State);

        h.Settings.Current.Flow.CountdownSeconds = 3;
        Assert.True(h.ViewModel.TryTrigger());
        Assert.Equal(BoothState.Countdown, h.Controller.State);
        Assert.Equal(BoothKeyAction.ToggleTrackingOverlay, h.ViewModel.HandleKey(CtrlAltX));
        h.ViewModel.ToggleTrackingOverlay();
        Assert.Equal(BoothState.Countdown, h.Controller.State);
        h.ViewModel.ToggleTrackingOverlay();   // wieder aus
    }

    [Fact]
    public void TrackingOverlayKey_InRecording_StillMapsAndKeepsRecording()
    {
        var h = RecordingHarness();

        Assert.Equal(BoothKeyAction.ToggleTrackingOverlay, h.ViewModel.HandleKey(CtrlAltX));
        h.ViewModel.ToggleTrackingOverlay();

        Assert.Equal(BoothState.Recording, h.Controller.State);
        Assert.Equal(0, h.Camera.LastRecording!.StopCalls);
    }

    [Fact]
    public void TrackingOverlayKey_FollowsTheSettings_EmptyListMapsNothing_AndOtherActionsKeepPrecedence()
    {
        var h = new Harness();
        h.Settings.Current.Input.TrackingOverlayKeys = ["F11"];
        h.Settings.RaiseSettingsChanged();
        Assert.Equal(BoothKeyAction.ToggleTrackingOverlay, h.ViewModel.HandleKey(new KeyChord(Key.F11, ModifierKeys.None)));
        Assert.Equal(BoothKeyAction.None, h.ViewModel.HandleKey(CtrlAltX));

        h.Settings.Current.Input.TrackingOverlayKeys = [];
        h.Settings.RaiseSettingsChanged();
        Assert.Equal(BoothKeyAction.None, h.ViewModel.HandleKey(new KeyChord(Key.F11, ModifierKeys.None)));

        // Von Hand eingetragene Doppelbelegung: Die älteren Aktionen gewinnen.
        h.Settings.Current.Input.TrackingOverlayKeys = ["Space", "Ctrl+Alt+A", "Ctrl+Alt+Y"];
        h.Settings.RaiseSettingsChanged();
        Assert.Equal(BoothKeyAction.Trigger, h.ViewModel.HandleKey(Space));
        Assert.Equal(BoothKeyAction.OpenAdmin, h.ViewModel.HandleKey(new KeyChord(Key.A, ModifierKeys.Control | ModifierKeys.Alt)));
        Assert.Equal(BoothKeyAction.TogglePerformanceOverlay, h.ViewModel.HandleKey(CtrlAltY));
    }

    [Fact]
    public void ToggleTrackingOverlay_RaisesTheEventOncePerCall_AndIsHarmlessWithoutSubscriber()
    {
        var h = new Harness();
        var raised = 0;
        h.ViewModel.TrackingOverlayToggleRequested += () => raised++;

        h.ViewModel.ToggleTrackingOverlay();
        h.ViewModel.ToggleTrackingOverlay();

        Assert.Equal(2, raised);   // das echte Overlay ist danach wieder aus
        Assert.False(h.ViewModel.TrackingOverlay.IsEnabled);
    }

    [Fact]
    public void TrackingOverlay_StartsHidden_ShowsInIdleAndCountdown_AndHidesInOtherStates()
    {
        var h = new Harness();
        var overlay = h.ViewModel.TrackingOverlay;
        Assert.False(overlay.IsEnabled);
        Assert.False(overlay.IsVisible);

        h.ViewModel.ToggleTrackingOverlay();
        Assert.True(overlay.IsEnabled);
        Assert.True(overlay.IsVisible);                     // Leerlauf
        Assert.Equal(BoothState.Idle, h.Controller.State);  // beeinflusst den Ablauf nicht

        h.Settings.Current.Flow.CountdownSeconds = 3;
        Assert.True(h.ViewModel.TryTrigger());
        Assert.Equal(BoothState.Countdown, h.Controller.State);
        Assert.True(overlay.IsVisible);                     // Countdown

        h.ViewModel.ToggleTrackingOverlay();
        Assert.False(overlay.IsVisible);
    }

    [Fact]
    public void TrackingOverlay_IsHiddenWhileRecording()
    {
        var h = RecordingHarness();
        var overlay = h.ViewModel.TrackingOverlay;
        Assert.False(overlay.IsVisible);

        h.ViewModel.ToggleTrackingOverlay();
        Assert.True(overlay.IsEnabled);
        Assert.False(overlay.IsVisible);                    // Aufnahme: eingeschaltet, aber nicht zu sehen
        Assert.Equal(BoothState.Recording, h.Controller.State);

        Assert.True(h.ViewModel.ExecuteVideoAction(h.ViewModel.HandleKey(Enter)));
        Assert.NotEqual(BoothState.Recording, h.Controller.State);
        h.ViewModel.ToggleTrackingOverlay();
    }

    [Fact]
    public void Shutdown_StopsTheTrackingOverlay()
    {
        var h = new Harness();
        h.ViewModel.ToggleTrackingOverlay();
        Assert.True(h.ViewModel.TrackingOverlay.IsVisible);

        h.ViewModel.Shutdown();

        Assert.False(h.ViewModel.TrackingOverlay.IsVisible);
    }

    [StaFact]
    public void BoothWindow_TrackingOverlayKey_RaisesTheToggleEvent_OncePerKeyDown_AndIgnoresRepeats()
    {
        var h = new Harness();
        var window = new BoothWindow(h.ViewModel, new FakeAdminLauncher(), h.Settings, new NoDisplays());
        using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters());
        h.Settings.Current.Input.TrackingOverlayKeys = ["F11"];
        h.Settings.RaiseSettingsChanged();
        var raised = 0;
        h.ViewModel.TrackingOverlayToggleRequested += () => raised++;

        var args = RaiseKeyDown(window, source, Key.F11);
        Assert.True(args.Handled);
        Assert.Equal(1, raised);

        RaiseKeyDown(window, source, Key.F11, isRepeat: true);
        Assert.Equal(1, raised);
        Assert.Equal(BoothState.Idle, h.Controller.State);
        h.ViewModel.ToggleTrackingOverlay();   // wieder aus (zwei Umschaltungen insgesamt)
    }

    [StaFact]
    public void BoothWindow_TrackingLayer_SitsInsideTheZoomHost_OnTop_AndNeverTakesInput()
    {
        WpfCollection.EnsurePackUris();
        var h = new Harness();
        var window = new BoothWindow(h.ViewModel, new FakeAdminLauncher(), h.Settings, new NoDisplays());
        var layer = (System.Windows.FrameworkElement)window.FindName("TrackingLayer");

        Assert.IsType<TrackingOverlay>(layer);
        Assert.False(layer.IsHitTestVisible);
        Assert.False(layer.Focusable);

        var zoomHost = FindLogical<Fotobox.Views.Booth.Heart.LiveZoomHost>(window)!;
        Assert.Same(layer, FindLogical<TrackingOverlay>(zoomHost));
        var grid = (System.Windows.Controls.Grid)System.Windows.Media.VisualTreeHelper.GetParent(layer);
        Assert.Equal(grid.Children.Count - 1, grid.Children.IndexOf(layer));   // über allen übrigen Livebild-Overlays
        Assert.Equal(System.Windows.Visibility.Collapsed, layer.Visibility);
    }

    [StaFact]
    public void BoothWindow_TrackingStatusLine_SitsOutsideTheZoomHost_BeforeThePerformanceOverlay_AndNeverTakesInput()
    {
        WpfCollection.EnsurePackUris();
        var h = new Harness();
        var window = new BoothWindow(h.ViewModel, new FakeAdminLauncher(), h.Settings, new NoDisplays());
        var root = (System.Windows.Controls.Grid)window.Content;
        var status = (System.Windows.FrameworkElement)window.FindName("TrackingStatusView");
        var perf = (System.Windows.FrameworkElement)window.FindName("PerformanceOverlayView");
        var flash = (System.Windows.FrameworkElement)window.FindName("FlashOverlay");

        Assert.IsType<TrackingStatusLine>(status);
        Assert.False(status.IsHitTestVisible);
        Assert.False(((System.Windows.Controls.UserControl)status).Focusable);
        Assert.Same(root, System.Windows.Media.VisualTreeHelper.GetParent(status));
        Assert.True(root.Children.IndexOf(status) < root.Children.IndexOf(perf));
        Assert.Equal(root.Children.IndexOf(flash) - 1, root.Children.IndexOf(perf));   // die Leistungsanzeige bleibt direkt vor dem Blitz
        for (System.Windows.DependencyObject? p = status; p is not null; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
            Assert.False(p is Fotobox.Views.Booth.Heart.LiveZoomHost);

        // Oben links, Rand wie die REC-Anzeige.
        Assert.Equal(System.Windows.HorizontalAlignment.Left, status.HorizontalAlignment);
        Assert.Equal(System.Windows.VerticalAlignment.Top, status.VerticalAlignment);
        Assert.Equal(new System.Windows.Thickness(48, 44, 0, 0), status.Margin);
    }

    [StaFact]
    public void BoothWindow_TrackingOverlay_FollowsTheToggle()
    {
        WpfCollection.EnsurePackUris();
        var h = new Harness();
        var window = new BoothWindow(h.ViewModel, new FakeAdminLauncher(), h.Settings, new NoDisplays());
        var layer = (System.Windows.FrameworkElement)window.FindName("TrackingLayer");
        var status = (System.Windows.FrameworkElement)window.FindName("TrackingStatusView");
        Assert.Equal(System.Windows.Visibility.Collapsed, layer.Visibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, status.Visibility);

        try
        {
            h.ViewModel.ToggleTrackingOverlay();
            Assert.Equal(System.Windows.Visibility.Visible, layer.Visibility);
            Assert.Equal(System.Windows.Visibility.Visible, status.Visibility);

            h.Settings.Current.TrackingOverlay.ShowStatusLine = false;
            h.Settings.RaiseSettingsChanged();
            Assert.Equal(System.Windows.Visibility.Collapsed, status.Visibility);
            Assert.Equal(System.Windows.Visibility.Visible, layer.Visibility);
        }
        finally
        {
            h.ViewModel.ToggleTrackingOverlay();
        }
        Assert.Equal(System.Windows.Visibility.Collapsed, layer.Visibility);
    }
}
