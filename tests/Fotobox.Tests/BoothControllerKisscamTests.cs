using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Kisscam im <see cref="BoothController"/>: stilabhängige Countdown-Grenzen, Zuschnitt (nach dem Verzieren, vor dem Spiegeln),
/// Rahmen (erzwingt das Einzelfoto, geht an den Composer), Dateipräfix des Durchgangs und Abbruch. Teilt Harness und Fakes mit den
/// übrigen Controller-Tests.
/// </summary>
public partial class BoothControllerTests
{
    private static readonly PhotoFrame KisscamFrame = new(PhotoFrameKind.Kisscam, "KISS CAM", KisscamColor.PinkRed);

    /// <summary>4 × 2 Pixel, jedes Pixel eine eigene Farbe (Zeile 0: 1 2 3 4, Zeile 1: 5 6 7 8): asymmetrisch, damit Zuschnitt und Spiegelung unterscheidbar sind.</summary>
    private static BitmapSource AsymmetricPhoto()
    {
        uint[] pixels = [0xFF000001, 0xFF000002, 0xFF000003, 0xFF000004, 0xFF000005, 0xFF000006, 0xFF000007, 0xFF000008];
        var bmp = BitmapSource.Create(4, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        bmp.Freeze();
        return bmp;
    }

    private static uint ReadPixelAt(BitmapSource source, int x, int y)
    {
        var pixels = new uint[1];
        source.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixels, 4, 0);
        return pixels[0];
    }

    // ---- Countdown-Grenzen ----

    [Theory]
    [InlineData(60, 60)]
    [InlineData(45, 45)]
    [InlineData(2, 3)]
    [InlineData(0, 3)]
    [InlineData(-5, 3)]
    [InlineData(99, 60)]
    public void Kisscam_CountdownSeconds_UsesKisscamLimits(int requested, int expected)
    {
        var h = PeaceHarness();

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { Style = CountdownStyle.Kisscam, CountdownSeconds = requested }));

        Assert.Equal(expected, h.Controller.CountdownValue);
        Assert.Equal(3, h.Settings.Current.Flow.CountdownSeconds);   // gespeicherte Einstellung unverändert
        Assert.True(h.Controller.TryCancelCountdown());
        h.WaitUntil(() => h.Controller.State == BoothState.Idle);
    }

    [Theory]
    [InlineData(CountdownStyle.Normal, 99, 30)]
    [InlineData(CountdownStyle.Peace, 99, 30)]
    [InlineData(CountdownStyle.SelfDestruct, 99, 30)]
    [InlineData(CountdownStyle.Normal, 2, 2)]
    [InlineData(CountdownStyle.Peace, 2, 2)]
    [InlineData(CountdownStyle.Peace, 60, 30)]
    public void OtherStyles_CountdownSeconds_StayZeroToThirty(CountdownStyle style, int requested, int expected)
    {
        var h = PeaceHarness();

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { Style = style, CountdownSeconds = requested }));

        Assert.Equal(expected, h.Controller.CountdownValue);
        Assert.True(h.Controller.TryCancelCountdown());
        h.WaitUntil(() => h.Controller.State == BoothState.Idle);
    }

    [Fact]
    public void Kisscam_FullSession_CountsDownAndResetsStyle()
    {
        var h = PeaceHarness();
        CountdownStyle? atFlash = null;
        h.Controller.FlashRequested += (_, _) => atFlash = h.Controller.CountdownStyle;

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { Style = CountdownStyle.Kisscam, CountdownSeconds = 5, Frame = KisscamFrame }));
        Assert.Equal(CountdownStyle.Kisscam, h.Controller.CountdownStyle);
        Assert.Equal(5, h.Controller.CountdownValue);
        h.Time.Advance(Second);
        Assert.Equal(4, h.Controller.CountdownValue);
        Assert.Equal(0, h.Camera.CaptureCount);

        h.RunToIdle();

        Assert.Equal(CountdownStyle.Kisscam, atFlash);
        Assert.Equal(1, h.Camera.CaptureCount);
        Assert.Single(h.Storage.Saved);
        Assert.Equal(CountdownStyle.Normal, h.Controller.CountdownStyle);
        Assert.Equal(BoothState.Idle, h.Controller.State);
    }

    // ---- Abbruch ----

    [Fact]
    public void Kisscam_CancelDuringCountdown_GoesIdleWithoutPhoto()
    {
        var h = PeaceHarness();
        var states = h.RecordStates();
        Assert.True(h.Controller.TryTrigger(new TriggerOptions { Style = CountdownStyle.Kisscam, CountdownSeconds = 20, Frame = KisscamFrame }));
        h.Time.Advance(Second);
        Assert.Equal(19, h.Controller.CountdownValue);

        Assert.True(h.Controller.TryCancelCountdown());

        h.WaitUntil(() => h.Controller.State == BoothState.Idle);
        Assert.Null(h.Controller.ErrorMessage);
        Assert.Equal(0, h.Camera.CaptureCount);
        Assert.Empty(h.Storage.Saved);
        Assert.DoesNotContain(BoothState.Error, states);
        Assert.Equal(CountdownStyle.Normal, h.Controller.CountdownStyle);
        Assert.True(h.Controller.TryTrigger());   // Gate wieder frei
    }

    [Fact]
    public void Kisscam_TryCancel_AlsoCancelsCountdown()
    {
        var h = PeaceHarness();
        Assert.True(h.Controller.TryTrigger(new TriggerOptions { Style = CountdownStyle.Kisscam, CountdownSeconds = 20 }));

        Assert.True(h.Controller.TryCancel());

        h.WaitUntil(() => h.Controller.State == BoothState.Idle);
        Assert.Equal(0, h.Camera.CaptureCount);
        Assert.Equal(CountdownStyle.Normal, h.Controller.CountdownStyle);
    }

    // ---- Rahmen ----

    [Fact]
    public void Frame_IsPassedToComposer()
    {
        var h = PeaceHarness();

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { Style = CountdownStyle.Kisscam, CountdownSeconds = 3, Frame = KisscamFrame }));
        h.RunToIdle();

        Assert.Equal(1, h.Composer.FrameCalls);
        Assert.Same(KisscamFrame, h.Composer.LastFrame);
    }

    [Fact]
    public void WithoutFrame_ComposerGetsTheFramelessCall()
    {
        var h = PeaceHarness();

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { CountdownSeconds = 3 }));
        h.RunToIdle();

        Assert.Equal(0, h.Composer.FrameCalls);
        Assert.Null(h.Composer.LastFrame);
        Assert.NotNull(h.Composer.LastLayout);
    }

    [Theory]
    [InlineData(LayoutKind.Strip, null)]
    [InlineData(LayoutKind.Overlay, null)]
    [InlineData(LayoutKind.SingleBorderless, GuestLayout.StripDouble)]
    [InlineData(LayoutKind.SingleBorderless, GuestLayout.StripSingle)]
    [InlineData(LayoutKind.SingleBorderless, GuestLayout.Overlay)]
    [InlineData(LayoutKind.Strip, GuestLayout.Overlay)]
    public void Frame_ForcesSinglePhoto_EvenForStripOrOverlay(LayoutKind configured, GuestLayout? guest)
    {
        var h = PeaceHarness(kind: configured);

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { Layout = guest, Style = CountdownStyle.Kisscam, CountdownSeconds = 3, Frame = KisscamFrame }));
        Assert.Equal(1, h.Controller.ShotCount);
        h.RunToIdle();

        Assert.Equal(1, h.Camera.CaptureCount);
        Assert.Equal(LayoutKind.SingleBorderless, h.Composer.LastLayout!.Kind);
        Assert.Equal(configured, h.Settings.Current.Layout.Kind);   // gespeicherte Einstellungen bleiben
    }

    // ---- Dateipräfix ----

    [Fact]
    public void FilePrefix_AppliesToSessionCopyOnly()
    {
        var h = PeaceHarness();
        var original = h.Settings.Current.Storage.FilePrefix;

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { CountdownSeconds = 3, FilePrefix = "Kisscam" }));
        h.RunToIdle();

        Assert.Equal("Kisscam", h.Storage.LastStorage!.FilePrefix);
        Assert.Equal(original, h.Settings.Current.Storage.FilePrefix);
        Assert.NotEqual("Kisscam", original);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FilePrefix_EmptyOrNull_KeepsConfiguredPrefix(string? prefix)
    {
        var h = PeaceHarness();
        var original = h.Settings.Current.Storage.FilePrefix;

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { CountdownSeconds = 3, FilePrefix = prefix }));
        h.RunToIdle();

        Assert.Equal(original, h.Storage.LastStorage!.FilePrefix);
    }

    [Fact]
    public void FilePrefix_SameAsConfigured_DoesNotNeedACopy()
    {
        var h = PeaceHarness();
        var configured = h.Settings.Current.Storage;

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { CountdownSeconds = 3, FilePrefix = configured.FilePrefix }));
        h.RunToIdle();

        Assert.Same(configured, h.Storage.LastStorage);
    }

    // ---- Zuschnitt ----

    [Fact]
    public void Crop_RunsAfterDecorateAndBeforeMirroring()
    {
        var decorator = new FakePhotoDecorator { Result = AsymmetricPhoto() };
        var h = PeaceHarness(decorator);
        h.Settings.Current.Camera.MirrorPhoto = true;

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { CountdownSeconds = 3, CropRegion = new NormRect(0.5, 0, 0.5, 1) }));
        h.RunToIdle();

        // Der Verzierer bekommt das rohe, unbeschnittene Kamerabild ...
        Assert.Same(h.Camera.Photo, Assert.Single(decorator.Received).Photo);
        // ... sein Ergebnis wird auf die rechte Hälfte zugeschnitten (Pixel 3 4 / 7 8) und erst danach gespiegelt (4 3 / 8 7).
        var photo = Assert.Single(h.Composer.LastPhotos!);
        Assert.Equal(2, photo.PixelWidth);
        Assert.Equal(2, photo.PixelHeight);
        Assert.Equal(0xFF000004u, ReadPixelAt(photo, 0, 0));
        Assert.Equal(0xFF000003u, ReadPixelAt(photo, 1, 0));
        Assert.Equal(0xFF000008u, ReadPixelAt(photo, 0, 1));
        Assert.Equal(0xFF000007u, ReadPixelAt(photo, 1, 1));
    }

    [Fact]
    public void Crop_WithoutMirroring_KeepsOrientation()
    {
        var decorator = new FakePhotoDecorator { Result = AsymmetricPhoto() };
        var h = PeaceHarness(decorator);
        h.Settings.Current.Camera.MirrorPhoto = false;

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { CountdownSeconds = 3, CropRegion = new NormRect(0, 0.5, 0.5, 0.5) }));
        h.RunToIdle();

        var photo = Assert.Single(h.Composer.LastPhotos!);
        Assert.Equal(2, photo.PixelWidth);
        Assert.Equal(1, photo.PixelHeight);
        Assert.Equal(0xFF000005u, ReadPixelAt(photo, 0, 0));
        Assert.Equal(0xFF000006u, ReadPixelAt(photo, 1, 0));
    }

    [Fact]
    public void NoCrop_PhotoStaysUntouched()
    {
        var h = PeaceHarness();

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { CountdownSeconds = 3 }));
        h.RunToIdle();

        Assert.Same(h.Camera.Photo, Assert.Single(h.Composer.LastPhotos!));
    }

    [StaFact]
    public void CropPhoto_FullRegionReturnsSameInstance_PartialCrops()
    {
        var photo = AsymmetricPhoto();

        Assert.Same(photo, BoothController.CropPhoto(photo, NormRect.Full));

        var cropped = BoothController.CropPhoto(photo, new NormRect(0.25, 0, 0.5, 1));
        Assert.True(cropped.IsFrozen);
        Assert.Equal(2, cropped.PixelWidth);
        Assert.Equal(2, cropped.PixelHeight);
        Assert.Equal(0xFF000002u, ReadPixelAt(cropped, 0, 0));
        Assert.Equal(0xFF000007u, ReadPixelAt(cropped, 1, 1));
    }

    [StaFact]
    public void CropPhoto_InvalidRegion_FallsBackToWholePhoto()
    {
        var photo = AsymmetricPhoto();

        Assert.Same(photo, BoothController.CropPhoto(photo, new NormRect(double.NaN, 0, 0.5, 1)));
        // Außerhalb des Bildes: auf mindestens ein Pixel am Rand geklemmt, kein Fehler.
        var edge = BoothController.CropPhoto(photo, new NormRect(5, 5, 1, 1));
        Assert.Equal(1, edge.PixelWidth);
        Assert.Equal(1, edge.PixelHeight);
    }
}
