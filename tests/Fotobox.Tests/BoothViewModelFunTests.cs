using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Services.Camera;
using Fotobox.ViewModels.Booth;
using Fotobox.Views.Booth;
using Xunit;

namespace Fotobox.Tests;

// Fun „Zielerfassung“ in der Gastansicht: Aktivierung nur im Leerlauf, Zusammenspiel mit den Hinweiszeilen.
public partial class BoothViewModelTests
{
    private sealed class FunRig
    {
        public Harness H { get; } = new();
        public FakeDetectionService Detection { get; } = new();
        public FakeSoundPlayer Sound { get; } = new();
        public TrackingTimeProvider FunTime { get; } = new();
        public FunOverlayViewModel Fun { get; }
        public BoothViewModel ViewModel { get; }

        public FunRig(bool enabled = true, Action<FunSettings>? configure = null)
        {
            H.Settings.Current.Fun = new FunSettings
            {
                Enabled = enabled,
                Scope = TargetScope.FullBody,
                DwellMilliseconds = 0,
                MinSizePercent = 5,
                TextSeconds = 6,
            };
            configure?.Invoke(H.Settings.Current.Fun);
            Fun = new FunOverlayViewModel(Detection, Sound, FunTime);
            ViewModel = new BoothViewModel(H.Controller, H.Camera, H.Settings, Fun);
            // Erste Entscheidung wie beim Fensterstart.
            H.Settings.RaiseSettingsChanged();
        }

        /// <summary>Eine Person im Bild erkennen lassen und so lange laufen, bis der Fun-Text erscheint.</summary>
        public void ShowText()
        {
            var person = new Detection(new NormRect(0.1, 0.1, 0.12, 0.7), 0.9, DetectionKind.Body);
            for (var i = 0; i < 20 && !Fun.TextVisible; i++)
            {
                FunTime.Advance(TimeSpan.FromMilliseconds(100));
                Detection.Last.RaiseUpdated([person]);
            }
            Assert.True(Fun.TextVisible);
        }
    }

    [Fact]
    public void Fun_IsActive_OnlyWhenEnabledIdleCameraRunningAndAdminClosed()
    {
        var rig = new FunRig();
        Assert.True(rig.Fun.IsActive);
        Assert.Equal(1, rig.Detection.OpenRequests);

        rig.ViewModel.IsAdminOpen = true;
        Assert.False(rig.Fun.IsActive);
        Assert.Equal(0, rig.Detection.OpenRequests);

        rig.ViewModel.IsAdminOpen = false;
        Assert.True(rig.Fun.IsActive);
        Assert.Equal(1, rig.Detection.OpenRequests);
    }

    [Fact]
    public void Fun_StaysOff_WhenDisabled()
    {
        var rig = new FunRig(enabled: false);

        Assert.False(rig.Fun.IsActive);
        Assert.Empty(rig.Detection.Requests);
    }

    [Fact]
    public void Fun_TurnsOn_WhenEnabledLater_AndOff_WhenDisabledAgain()
    {
        var rig = new FunRig(enabled: false);

        rig.H.Settings.Current.Fun.Enabled = true;
        rig.H.Settings.RaiseSettingsChanged();
        Assert.True(rig.Fun.IsActive);

        rig.H.Settings.Current.Fun.Enabled = false;
        rig.H.Settings.RaiseSettingsChanged();
        Assert.False(rig.Fun.IsActive);
        Assert.Equal(0, rig.Detection.OpenRequests);
    }

    [Fact]
    public void Fun_StaysOff_WhileTheCameraIsNotRunning()
    {
        var rig = new FunRig();
        rig.H.Camera.Status = CameraStatus.Starting;
        rig.H.Settings.RaiseSettingsChanged();
        Assert.False(rig.Fun.IsActive);

        rig.H.Camera.Status = CameraStatus.Running;
        rig.H.Settings.RaiseSettingsChanged();
        Assert.True(rig.Fun.IsActive);
    }

    [Fact]
    public void Fun_StopsImmediately_WhenACountdownStarts_AndSilencesTheSound()
    {
        var rig = new FunRig();
        rig.H.Settings.Current.Flow.CountdownSeconds = 3;
        rig.ShowText();
        var request = rig.Detection.Last;
        var stopsBefore = rig.Sound.Stops;

        Assert.True(rig.ViewModel.TryTrigger());

        Assert.Equal(BoothState.Countdown, rig.H.Controller.State);
        Assert.False(rig.Fun.IsActive);
        Assert.True(request.IsDisposed);
        Assert.Empty(rig.Fun.Snapshot.Targets);
        Assert.False(rig.Fun.TextVisible);
        Assert.True(rig.Sound.Stops > stopsBefore);
    }

    [Fact]
    public void Fun_ReactivatesAfterTheSession_BackInIdle()
    {
        var rig = new FunRig();
        rig.H.Settings.Current.Flow.CountdownSeconds = 0;
        rig.H.Camera.CaptureFailure = new InvalidOperationException("Test");
        rig.ViewModel.TryTrigger(); // Fehlerzustand
        Assert.False(rig.Fun.IsActive);

        rig.H.Time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(BoothState.Idle, rig.H.Controller.State);
        Assert.True(rig.Fun.IsActive);
    }

    [Fact]
    public void Fun_SettingsChange_ReachesTheOverlayWithoutANewRequest()
    {
        var rig = new FunRig();

        rig.H.Settings.Current.Fun.Color = TargetColor.Green;
        rig.H.Settings.RaiseSettingsChanged();

        Assert.Equal(TargetColor.Green, rig.Fun.Color);
        Assert.Single(rig.Detection.Requests);
        Assert.Single(rig.Detection.Last.Updates);
    }

    [Fact]
    public void Fun_MirrorSetting_IsPassedOn()
    {
        var rig = new FunRig();
        Assert.Equal(rig.H.Settings.Current.Camera.MirrorPreview, rig.Fun.Mirror);

        rig.H.Settings.Current.Camera.MirrorPreview = !rig.H.Settings.Current.Camera.MirrorPreview;
        rig.H.Settings.RaiseSettingsChanged();

        Assert.Equal(rig.H.Settings.Current.Camera.MirrorPreview, rig.Fun.Mirror);
    }

    [Fact]
    public void Shutdown_TurnsFunOff_AndItStaysOff()
    {
        var rig = new FunRig();

        rig.ViewModel.Shutdown();
        rig.H.Settings.RaiseSettingsChanged();

        Assert.False(rig.Fun.IsActive);
        Assert.Equal(0, rig.Detection.OpenRequests);
    }

    [Fact]
    public void ViewModelWithoutFun_BehavesLikeBefore()
    {
        var h = new Harness();
        h.Settings.Current.Fun.Enabled = false;
        h.Settings.RaiseSettingsChanged();

        Assert.NotNull(h.ViewModel.Fun);
        Assert.False(h.ViewModel.Fun.IsActive);
        Assert.True(h.ViewModel.ShowHint);
    }

    [Theory]
    [InlineData(FunTextPosition.Bottom, false, true)]
    [InlineData(FunTextPosition.Top, true, false)]
    [InlineData(FunTextPosition.Center, true, true)]
    public void FunText_ReplacesTheHintLineAtItsPosition_OnlyWhileVisible(FunTextPosition position, bool photoHint, bool videoHint)
    {
        var rig = new FunRig(configure: s => s.TextPosition = position);
        Assert.True(rig.ViewModel.ShowHint);
        Assert.True(rig.ViewModel.ShowVideoHint);

        var changed = new List<string?>();
        rig.ViewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        rig.ShowText();

        Assert.Equal(photoHint, rig.ViewModel.ShowHint);
        Assert.Equal(videoHint, rig.ViewModel.ShowVideoHint);
        Assert.Contains(nameof(BoothViewModel.ShowHint), changed);

        // Text weg: beide Zeilen wieder da.
        rig.H.Settings.Current.Fun.Enabled = false;
        rig.H.Settings.RaiseSettingsChanged();
        Assert.True(rig.ViewModel.ShowHint);
        Assert.True(rig.ViewModel.ShowVideoHint);
    }

    [Fact]
    public void FunText_AtTheBottom_DoesNotHideTheVideoHint_AndHidesTheWheelLine()
    {
        var rig = new FunRig(configure: s => s.TextPosition = FunTextPosition.Bottom);
        rig.H.Settings.Current.Layout.GuestChoices = [GuestLayout.Single, GuestLayout.StripDouble];
        rig.H.Settings.RaiseSettingsChanged();
        Assert.True(rig.ViewModel.ShowLayoutHint);

        rig.ShowText();

        Assert.False(rig.ViewModel.ShowHint);
        Assert.False(rig.ViewModel.ShowLayoutHint);
        Assert.True(rig.ViewModel.ShowVideoHint);
    }

    // ---- BoothWindow: Bindungen an Fun.*

    private static T? FindLogical<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(parent).OfType<System.Windows.DependencyObject>())
        {
            if (child is T match)
                return match;
            var nested = FindLogical<T>(child);
            if (nested is not null)
                return nested;
        }
        return null;
    }

    [StaFact]
    public void BoothWindow_FunBindings_ShowOverlayAndText_PushRegionBack_AndHideOnTrigger()
    {
        var rig = new FunRig();
        rig.H.Settings.Current.Flow.CountdownSeconds = 3;
        var window = new BoothWindow(rig.ViewModel, new FakeAdminLauncher(), rig.H.Settings, new NoDisplays());
        var overlay = FindLogical<TargetOverlay>(window)!;
        var text = FindLogical<FunText>(window)!;
        Assert.NotNull(overlay);
        Assert.NotNull(text);
        var content = (System.Windows.FrameworkElement)window.Content;
        void Layout()
        {
            content.Measure(new System.Windows.Size(1920, 1080));
            content.Arrange(new System.Windows.Rect(0, 0, 1920, 1080));
            content.UpdateLayout();
        }

        Layout();
        Assert.Equal(System.Windows.Visibility.Visible, overlay.Visibility); // Fun aktiv (Leerlauf, Kamera läuft)
        Assert.False(text.IsShown);

        rig.ShowText();
        Layout();

        Assert.Same(rig.Fun.Snapshot, overlay.Snapshot);
        Assert.Single(overlay.Visuals);
        Assert.True(text.IsShown);
        Assert.Equal(rig.Fun.Text, text.Text);
        Assert.Equal(rig.Fun.BoxStyle, overlay.BoxStyle);
        Assert.Equal(rig.Fun.Color, overlay.BoxColor);

        // Bildgröße 4:3 in der 16:9-Ansicht: oben/unten beschnitten; der Ausschnitt geht per OneWayToSource zurück ans Ansichtsmodell.
        rig.Detection.Last.RaiseUpdated([new Detection(new NormRect(0.1, 0.1, 0.12, 0.7), 0.9, DetectionKind.Body)], 640, 480);
        Layout();
        Assert.Equal(640, overlay.ImageWidth);
        Assert.Equal(0.75, rig.Fun.VisibleRegion.Height, 2);
        Assert.Equal(1.0, rig.Fun.VisibleRegion.Width, 2);

        // Auslösen: Overlay leer/weg, Text aus.
        Assert.True(rig.ViewModel.TryTrigger());
        Layout();
        Assert.Equal(System.Windows.Visibility.Collapsed, overlay.Visibility);
        Assert.Empty(overlay.Visuals);
        Assert.False(text.IsShown);
    }
}
