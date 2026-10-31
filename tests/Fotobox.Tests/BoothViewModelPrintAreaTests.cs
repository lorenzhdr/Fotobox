using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Fotobox.Services.Layout;
using Fotobox.Services.Printing;
using Fotobox.ViewModels.Booth;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Gastansicht „Druckbereich anzeigen“: Sichtbarkeit je Zustand, Rechteck je Layout (Mausrad-Auswahl und Layout des laufenden Durchgangs),
/// Seitengröße (zuletzt bekannt bzw. Rückfall) und Benachrichtigung des Controllers (teilt Harness und Fakes mit den übrigen Gast-Tests).
/// </summary>
public partial class BoothViewModelTests
{
    /// <summary>Wie <see cref="Harness"/>, aber ohne Vorabruf der Seitengröße: Der Druckbereich rechnet dann sicher mit der Rückfallseite.</summary>
    private sealed class AreaHarness
    {
        public FakeTimeProvider Time { get; } = new();
        public FakeCamera Camera { get; } = new();
        public FakeSettingsStore Settings { get; } = new();
        public BoothController Controller { get; }
        public BoothViewModel ViewModel { get; }

        public AreaHarness(bool enabled = true)
        {
            SynchronizationContext.SetSynchronizationContext(null);
            Settings.Current.PrintArea.Enabled = enabled;
            Settings.Current.Flow.CountdownSeconds = 5;
            Controller = new BoothController(Camera, new FakeComposer(), new FakePrinter(), new FakeStorage(), Settings, Time, new FakeVideoStorage())
            {
                PrefetchPageSpec = false,
            };
            ViewModel = new BoothViewModel(Controller, Camera, Settings);
            Controller.MarkReady();
        }

        /// <summary>Erwartetes Rechteck für ein Layout auf der Rückfallseite (Einstellungen bzw. 10 × 15 cm in der Ausrichtung des Layouts).</summary>
        public NormRect? Expected(GuestLayout guest)
        {
            var applied = new LayoutSettings();
            GuestLayouts.ApplyTo(applied, guest);
            var page = PrintLayoutResolver.ResolvePageSpec(Settings.Current.Printer, applied.Kind, [], null);
            return PrintAreaPreview.Compute(guest, Settings.Current.Layout, page, ViewModel.PrintAreaImageWidth, ViewModel.PrintAreaImageHeight);
        }
    }

    // ---- Schalter und Zustände

    [Fact]
    public void PrintArea_IsOffByDefault()
    {
        var h = new AreaHarness(enabled: false);

        Assert.False(h.ViewModel.ShowPrintArea);
        Assert.Null(h.ViewModel.PrintAreaRect);
    }

    [Fact]
    public void PrintArea_InIdle_ShowsTheCropOfTheSingleLayoutOnTheFallbackPage()
    {
        var h = new AreaHarness();

        Assert.True(h.ViewModel.ShowPrintArea);
        var rect = h.ViewModel.PrintAreaRect;
        Assert.NotNull(rect);
        Assert.Equal(h.Expected(GuestLayout.Single), rect);
        // Standard 10 × 15 cm im Querformat (Auto, Einzelfoto): 3:2 auf 16:9 – seitlicher Zuschnitt, volle Höhe.
        Assert.Equal(0, rect!.Value.Y, 9);
        Assert.Equal(1, rect.Value.Height, 9);
        Assert.InRange(rect.Value.Width, 0.8, 0.9);
    }

    [Fact]
    public void PrintArea_UsesTheConfiguredPaperAndOrientationWhenNothingIsKnownYet()
    {
        var h = new AreaHarness();
        h.Settings.Current.Printer.PaperWidthMm = 210;
        h.Settings.Current.Printer.PaperHeightMm = 297;
        h.Settings.Current.Printer.Orientation = PageOrientation.Portrait;
        h.Settings.RaiseSettingsChanged();

        Assert.Equal(h.Expected(GuestLayout.Single), h.ViewModel.PrintAreaRect);
        // A4 hoch (0,707) auf 16:9: deutlich schmaler als 10 × 15 cm quer.
        Assert.InRange(h.ViewModel.PrintAreaRect!.Value.Width, 0.35, 0.45);
    }

    [Fact]
    public void PrintArea_IsHidden_WhenTheLayoutHasNoCrop()
    {
        var h = new AreaHarness();
        h.Settings.Current.Layout.Kind = LayoutKind.Strip;
        h.Settings.Current.Layout.StripStyle = StripStyle.Single;
        h.Settings.RaiseSettingsChanged();

        Assert.Null(h.ViewModel.PrintAreaRect);
        Assert.False(h.ViewModel.ShowPrintArea);
    }

    [Fact]
    public void PrintArea_IsHidden_WhenTheCameraIsNotRunning()
    {
        var h = new AreaHarness();
        h.Camera.Status = CameraStatus.NoDevice;

        Assert.False(h.ViewModel.ShowPrintArea);
    }

    [Fact]
    public void PrintArea_InPhotoCountdown_StaysVisible()
    {
        var h = new AreaHarness();

        Assert.True(h.ViewModel.TryTrigger());

        Assert.Equal(BoothState.Countdown, h.Controller.State);
        Assert.True(h.ViewModel.ShowPrintArea);
        Assert.Equal(h.Expected(GuestLayout.Single), h.ViewModel.PrintAreaRect);
    }

    [Fact]
    public void PrintArea_IsHidden_InTheKisscamCountdown()
    {
        var h = new AreaHarness();

        Assert.True(h.Controller.TryTrigger(new TriggerOptions
        {
            SingleShotOnly = true,
            CountdownSeconds = 10,
            Style = CountdownStyle.Kisscam,
            Frame = new PhotoFrame(PhotoFrameKind.Kisscam, "KISS CAM", KisscamColor.PinkRed),
        }));

        Assert.Equal(BoothState.Countdown, h.Controller.State);
        Assert.False(h.ViewModel.ShowPrintArea);
    }

    [Fact]
    public void PrintArea_IsHidden_InTheVideoCountdownAndWhileRecording()
    {
        var countdown = VideoHarness(v => v.CountdownSeconds = 3);
        countdown.Settings.Current.PrintArea.Enabled = true;
        Assert.True(countdown.ViewModel.ShowPrintArea);
        Assert.True(countdown.ViewModel.TryStartVideo());
        Assert.Equal(BoothState.VideoCountdown, countdown.Controller.State);
        Assert.False(countdown.ViewModel.ShowPrintArea);

        var recording = RecordingHarness();
        recording.Settings.Current.PrintArea.Enabled = true;
        Assert.Equal(BoothState.Recording, recording.Controller.State);
        Assert.False(recording.ViewModel.ShowPrintArea);
    }

    [Fact]
    public void PrintArea_IsHidden_WhilePrintingAndInThePrinterBlock()
    {
        var printing = PrintingHarness();
        printing.Settings.Current.PrintArea.Enabled = true;
        Assert.Equal(BoothState.Printing, printing.Controller.State);
        Assert.False(printing.ViewModel.ShowPrintArea);

        var blocked = BlockedByPrinterHarness();
        blocked.Settings.Current.PrintArea.Enabled = true;
        Assert.Equal(BoothState.PrinterBlocked, blocked.Controller.State);
        Assert.False(blocked.ViewModel.ShowPrintArea);
    }

    [Fact]
    public void PrintArea_Appearance_FollowsTheSettings()
    {
        var h = new AreaHarness();
        var p = h.Settings.Current.PrintArea;
        p.Style = PrintAreaStyle.Corners;
        p.Color = PrintAreaColor.Orange;
        p.OpacityPercent = 55;
        p.ThicknessPx = 7;
        p.ShadePercent = 65;

        Assert.Equal(PrintAreaStyle.Corners, h.ViewModel.PrintAreaShape);
        Assert.Equal(PrintAreaColor.Orange, h.ViewModel.PrintAreaLineColor);
        Assert.Equal(55, h.ViewModel.PrintAreaOpacityPercent);
        Assert.Equal(7, h.ViewModel.PrintAreaThickness);
        Assert.Equal(65, h.ViewModel.PrintAreaShadePercent);
    }

    // ---- Layout des Gastes

    [Fact]
    public void PrintArea_InIdle_FollowsTheLayoutChosenWithTheMouseWheel()
    {
        var h = new AreaHarness();
        h.Settings.Current.Layout.GuestChoices = [GuestLayout.Single, GuestLayout.StripDouble];
        h.Settings.RaiseSettingsChanged();
        var single = h.ViewModel.PrintAreaRect;
        Assert.Equal(h.Expected(GuestLayout.Single), single);

        h.ViewModel.CycleLayout(1);

        Assert.Equal(GuestLayout.StripDouble, h.ViewModel.SelectedLayout);
        Assert.Equal(h.Expected(GuestLayout.StripDouble), h.ViewModel.PrintAreaRect);
        Assert.NotEqual(single, h.ViewModel.PrintAreaRect);
    }

    [Fact]
    public void PrintArea_InTheSession_KeepsTheLayoutChosenBeforeTriggering_AfterTheSelectionResets()
    {
        var h = new AreaHarness();
        h.Settings.Current.Layout.GuestChoices = [GuestLayout.Single, GuestLayout.StripDouble];
        h.Settings.RaiseSettingsChanged();
        h.ViewModel.CycleLayout(1);
        Assert.Equal(GuestLayout.StripDouble, h.ViewModel.SelectedLayout);

        Assert.True(h.ViewModel.TryTrigger());

        // Die Auswahl springt beim Auslösen zurück, der Durchgang fotografiert aber den Streifen.
        Assert.Equal(GuestLayout.Single, h.ViewModel.SelectedLayout);
        Assert.Equal(BoothState.Countdown, h.Controller.State);
        Assert.Equal(h.Expected(GuestLayout.StripDouble), h.ViewModel.PrintAreaRect);
        Assert.NotEqual(h.Expected(GuestLayout.Single), h.ViewModel.PrintAreaRect);

        // Nach dem Abbruch gilt wieder die Auswahl (der Standard).
        Assert.True(h.ViewModel.TryCancel());
        Assert.True(SpinWait.SpinUntil(() => h.Controller.State == BoothState.Idle, TimeSpan.FromSeconds(10)));
        Assert.Equal(h.Expected(GuestLayout.Single), h.ViewModel.PrintAreaRect);
    }

    [Fact]
    public void PrintArea_InAPeacePhoto_ShowsTheSingleLayout_BecausePeacePhotosAreAlwaysSingleShots()
    {
        var h = new AreaHarness();
        h.Settings.Current.Layout.GuestChoices = [GuestLayout.Single, GuestLayout.StripDouble];
        h.Settings.Current.Peace.PhotoCountdownSeconds = 5;
        h.Settings.RaiseSettingsChanged();
        h.ViewModel.CycleLayout(1);
        Assert.Equal(GuestLayout.StripDouble, h.ViewModel.SelectedLayout);

        Assert.True(h.ViewModel.OnPeaceAction(new PeaceTrigger(PeaceAction.Photo, 1, null, new NormPoint(0.5, 0.5))));

        Assert.Equal(BoothState.Countdown, h.Controller.State);
        Assert.Equal(h.Expected(GuestLayout.Single), h.ViewModel.PrintAreaRect);
    }

    [Fact]
    public void PrintArea_FailedTrigger_DoesNotLeaveASessionLayoutBehind()
    {
        var h = new AreaHarness();
        h.Settings.Current.Layout.GuestChoices = [GuestLayout.Single, GuestLayout.StripDouble];
        h.Settings.RaiseSettingsChanged();
        h.ViewModel.CycleLayout(1);
        h.Camera.Status = CameraStatus.NoDevice;

        Assert.False(h.ViewModel.TryTrigger());
        h.Camera.Status = CameraStatus.Running;

        // Weiterhin Leerlauf: Es gilt die Mausrad-Auswahl.
        Assert.Equal(h.Expected(GuestLayout.StripDouble), h.ViewModel.PrintAreaRect);
    }

    // ---- Seitengröße vom Controller

    [Fact]
    public void Controller_ReportsAPageSpecOnce_AndKeepsTheLastKnownValue()
    {
        var h = new Harness(markReady: false);
        var updates = 0;
        h.Controller.PageSpecUpdated += (_, _) => Interlocked.Increment(ref updates);
        Assert.Null(h.Controller.GetLastKnownPageSpec(LayoutKind.SingleBorderless));

        h.Controller.MarkReady();

        Assert.True(SpinWait.SpinUntil(() => h.Controller.GetLastKnownPageSpec(LayoutKind.SingleBorderless) is not null, TimeSpan.FromSeconds(10)));
        Assert.Equal(PageSpec.Default, h.Controller.GetLastKnownPageSpec(LayoutKind.SingleBorderless));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref updates) >= 1, TimeSpan.FromSeconds(10)));

        // Dieselbe Seite noch einmal ermittelt: keine weitere Meldung.
        var before = Volatile.Read(ref updates);
        h.Settings.RaiseSettingsChanged();
        Thread.Sleep(200);
        Assert.Equal(before, Volatile.Read(ref updates));
    }

    [Fact]
    public void PrintArea_UsesTheLastKnownPage_OnceTheControllerHasIt()
    {
        var h = new Harness();
        Assert.True(SpinWait.SpinUntil(() => h.Controller.GetLastKnownPageSpec(LayoutKind.SingleBorderless) is not null, TimeSpan.FromSeconds(10)));
        h.Settings.Current.PrintArea.Enabled = true;

        // Der Fake-Drucker meldet 10 × 15 cm hochkant für jede Layoutart, der Rückfall wäre quer.
        var expected = PrintAreaPreview.Compute(GuestLayout.Single, h.Settings.Current.Layout, PageSpec.Default,
            h.ViewModel.PrintAreaImageWidth, h.ViewModel.PrintAreaImageHeight);

        Assert.Equal(expected, h.ViewModel.PrintAreaRect);
        Assert.InRange(h.ViewModel.PrintAreaRect!.Value.Width, 0.3, 0.4);
    }
}
