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
}
