using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Core.Peace;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// <see cref="TriggerOptions"/>, <see cref="BoothController.CountdownStyle"/> und der <see cref="IPhotoDecorator"/> des
/// <see cref="BoothController"/> (teilt Harness und Fakes mit den übrigen Foto-Tests).
/// </summary>
public partial class BoothControllerTests
{
    private static Harness PeaceHarness(IPhotoDecorator? decorator = null, LayoutKind kind = LayoutKind.SingleBorderless)
    {
        var h = new Harness(decorator: decorator);
        h.Settings.Current.Layout.Kind = kind;
        h.Settings.Current.Flow.PrintingEnabled = false;
        // Streifen brauchen drei Fotos, alles andere eines.
        h.Composer.CountFor = l => l.Kind == LayoutKind.Strip ? 3 : 1;
        return h;
    }

    private static BitmapSource TwoPixelBitmap(uint first, uint second)
    {
        var bmp = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new[] { first, second }, 8);
        bmp.Freeze();
        return bmp;
    }

    // ---- SingleShotOnly ----

    [Fact]
    public void SingleShotOnly_StripDefault_UsesSinglePhoto()
    {
        var h = PeaceHarness(kind: LayoutKind.Strip);

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { SingleShotOnly = true }));
        Assert.Equal(1, h.Controller.ShotCount);
        h.RunToIdle();

        Assert.Equal(1, h.Camera.CaptureCount);
        Assert.Equal(LayoutKind.SingleBorderless, h.Composer.LastLayout!.Kind);
        Assert.Equal(LayoutKind.Strip, h.Settings.Current.Layout.Kind);   // gespeicherte Einstellungen bleiben
    }

    [Theory]
    [InlineData(GuestLayout.StripDouble)]
    [InlineData(GuestLayout.StripSingle)]
    public void SingleShotOnly_StripGuestLayout_UsesSinglePhoto(GuestLayout guest)
    {
        var h = PeaceHarness();   // Standard ist ein Einzelfoto, der Gast wählt einen Streifen

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { Layout = guest, SingleShotOnly = true }));
        Assert.Equal(1, h.Controller.ShotCount);
        h.RunToIdle();

        Assert.Equal(1, h.Camera.CaptureCount);
        Assert.Equal(LayoutKind.SingleBorderless, h.Composer.LastLayout!.Kind);
    }

    [Fact]
    public void SingleShotOnly_Overlay_StaysOverlay()
    {
        var h = PeaceHarness(kind: LayoutKind.Strip);

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { Layout = GuestLayout.Overlay, SingleShotOnly = true }));
        h.RunToIdle();

        Assert.Equal(LayoutKind.Overlay, h.Composer.LastLayout!.Kind);
        Assert.Equal(1, h.Camera.CaptureCount);
    }

    [Fact]
    public void SingleShotOnly_SingleDefault_StaysSingle()
    {
        var h = PeaceHarness();

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { SingleShotOnly = true }));
        h.RunToIdle();

        Assert.Equal(LayoutKind.SingleBorderless, h.Composer.LastLayout!.Kind);
        Assert.Equal(1, h.Camera.CaptureCount);
    }

    [Fact]
    public void WithoutSingleShotOnly_Strip_StaysStrip()
    {
        var h = PeaceHarness(kind: LayoutKind.Strip);

        Assert.True(h.Controller.TryTrigger(new TriggerOptions()));
        Assert.Equal(3, h.Controller.ShotCount);
        h.RunToIdle();

        Assert.Equal(3, h.Camera.CaptureCount);
        Assert.Equal(LayoutKind.Strip, h.Composer.LastLayout!.Kind);
    }

    [Fact]
    public void TryTrigger_GuestLayoutOverload_BehavesAsBefore()
    {
        var h = PeaceHarness();

        Assert.True(h.Controller.TryTrigger(GuestLayout.StripSingle));
        Assert.Equal(3, h.Controller.ShotCount);
        h.RunToIdle();

        Assert.Equal(LayoutKind.Strip, h.Composer.LastLayout!.Kind);
        Assert.Equal(StripStyle.Single, h.Composer.LastLayout.StripStyle);
        Assert.Equal(LayoutKind.SingleBorderless, h.Settings.Current.Layout.Kind);
        Assert.Equal(CountdownStyle.Normal, h.Controller.CountdownStyle);
    }

    [Fact]
    public void TryTrigger_OptionsWhenBusyOrNotIdle_ReturnsFalse()
    {
        var h = PeaceHarness();
        Assert.True(h.Controller.TryTrigger(new TriggerOptions { CountdownSeconds = 5, Style = CountdownStyle.Peace }));

        Assert.False(h.Controller.TryTrigger(new TriggerOptions { Style = CountdownStyle.SelfDestruct }));
        Assert.Equal(CountdownStyle.Peace, h.Controller.CountdownStyle);   // der abgelehnte Aufruf ändert nichts

        var other = PeaceHarness();
        other.Camera.Status = CameraStatus.Stopped;
        Assert.False(other.Controller.TryTrigger(new TriggerOptions { Style = CountdownStyle.Peace }));
        Assert.Equal(CountdownStyle.Normal, other.Controller.CountdownStyle);
    }

    // ---- Countdown ----

    [Fact]
    public void CountdownSeconds_Zero_CapturesImmediately()
    {
        var h = PeaceHarness();

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { CountdownSeconds = 0 }));

        Assert.Equal(1, h.Camera.CaptureCount);   // ohne dass Zeit vergeht
        Assert.Equal(0, h.Controller.CountdownValue);
        h.RunToIdle();
        Assert.Equal(3, h.Settings.Current.Flow.CountdownSeconds);   // gespeicherte Einstellung unverändert
    }

    [Fact]
    public void CountdownSeconds_N_OverridesFlowSetting()
    {
        var h = PeaceHarness();

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { CountdownSeconds = 5 }));
        Assert.Equal(5, h.Controller.CountdownValue);
        h.Time.Advance(Second);
        h.Time.Advance(Second);
        h.Time.Advance(Second);
        h.Time.Advance(Second);
        Assert.Equal(1, h.Controller.CountdownValue);
        Assert.Equal(0, h.Camera.CaptureCount);

        h.AdvanceUntil(() => h.Camera.CaptureCount == 1, maxSeconds: 3);
        h.RunToIdle();
        Assert.Equal(3, h.Settings.Current.Flow.CountdownSeconds);
    }

    [Theory]
    [InlineData(99, 30)]
    [InlineData(-4, 0)]
    public void CountdownSeconds_OutOfRange_IsClamped(int requested, int expected)
    {
        var h = PeaceHarness();

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { CountdownSeconds = requested }));

        Assert.Equal(expected, h.Controller.CountdownValue);
        if (expected == 0)
            Assert.Equal(1, h.Camera.CaptureCount);
        h.Controller.TryCancel();
        h.WaitUntil(() => h.Controller.State == BoothState.Idle || h.Controller.State == BoothState.Capturing || h.Controller.State == BoothState.Preview);
        h.RunToIdle();
    }

    [Fact]
    public void CountdownSeconds_Null_UsesFlowSetting()
    {
        var h = PeaceHarness();

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { Layout = null }));

        Assert.Equal(3, h.Controller.CountdownValue);
        h.RunToIdle();
    }

    // ---- CountdownStyle ----

    [Theory]
    [InlineData(CountdownStyle.Peace)]
    [InlineData(CountdownStyle.SelfDestruct)]
    public void CountdownStyle_VisibleDuringSession_StillSetAtFlash_ThenNormal(CountdownStyle style)
    {
        var h = PeaceHarness();
        var changes = new List<CountdownStyle>();
        h.Controller.Changed += (_, _) =>
        {
            if (changes.Count == 0 || changes[^1] != h.Controller.CountdownStyle)
                changes.Add(h.Controller.CountdownStyle);
        };
        CountdownStyle? atFlash = null;
        h.Controller.FlashRequested += (_, _) => atFlash = h.Controller.CountdownStyle;

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { Style = style }));
        Assert.Equal(style, h.Controller.CountdownStyle);   // sofort sichtbar, schon im ersten Countdown-Zustand
        h.Time.Advance(Second);
        Assert.Equal(style, h.Controller.CountdownStyle);

        h.RunToIdle();

        Assert.Equal(style, atFlash);
        Assert.Equal(CountdownStyle.Normal, h.Controller.CountdownStyle);
        Assert.Equal([style, CountdownStyle.Normal], changes);
    }

    [Fact]
    public void CountdownStyle_ResetsAfterError()
    {
        var h = PeaceHarness();
        h.Camera.CaptureFailure = new InvalidOperationException("Kamera weg");

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { Style = CountdownStyle.Peace, CountdownSeconds = 0 }));
        h.RunToIdle();

        Assert.Equal(CountdownStyle.Normal, h.Controller.CountdownStyle);
    }

    [Fact]
    public void TryCancel_WithSelfDestructStyle_CancelsSilently()
    {
        var h = PeaceHarness();
        var states = h.RecordStates();
        Assert.True(h.Controller.TryTrigger(new TriggerOptions { Style = CountdownStyle.SelfDestruct, CountdownSeconds = 10 }));
        h.Time.Advance(Second);
        Assert.Equal(9, h.Controller.CountdownValue);

        Assert.True(h.Controller.TryCancel());

        h.WaitUntil(() => h.Controller.State == BoothState.Idle);
        Assert.Null(h.Controller.ErrorMessage);
        Assert.Equal(0, h.Camera.CaptureCount);
        Assert.Empty(h.Storage.Saved);
        Assert.DoesNotContain(BoothState.Error, states);
        Assert.Equal(CountdownStyle.Normal, h.Controller.CountdownStyle);
        Assert.True(h.Controller.TryTrigger());
    }

    // ---- Foto-Verzierer ----

    [Fact]
    public void Decorator_StripWithoutSingleShot_IsCalledPerShotWithIndex()
    {
        var decorator = new FakePhotoDecorator();
        var h = PeaceHarness(decorator, LayoutKind.Strip);

        Assert.True(h.Controller.TryTrigger());
        h.RunToIdle();

        Assert.Equal(3, decorator.Calls);
        Assert.Equal([1, 2, 3], decorator.Received.Select(r => r.ShotIndex).ToArray());
        Assert.Equal(3, h.Composer.LastPhotos!.Count);
    }

    [Fact]
    public void Decorator_SingleShotOnly_IsCalledOnce()
    {
        var decorator = new FakePhotoDecorator();
        var h = PeaceHarness(decorator, LayoutKind.Strip);

        Assert.True(h.Controller.TryTrigger(new TriggerOptions { SingleShotOnly = true }));
        h.RunToIdle();

        Assert.Equal(1, decorator.Calls);
        Assert.Equal(1, decorator.Received[0].ShotIndex);
    }

    [Fact]
    public void Decorator_RunsBeforeMirroring_AndResultIsMirrored()
    {
        var decorated = TwoPixelBitmap(0xFF00FF00, 0xFFFFFF00);   // links Grün, rechts Gelb
        var decorator = new FakePhotoDecorator { Result = decorated };
        var h = PeaceHarness(decorator);
        h.Settings.Current.Camera.MirrorPhoto = true;

        h.Controller.TryTrigger();
        h.RunToIdle();

        // Der Verzierer bekommt das rohe, ungespiegelte Kamerabild (links Rot, rechts Blau) ...
        var received = Assert.Single(decorator.Received).Photo;
        Assert.Same(h.Camera.Photo, received);
        Assert.Equal(FakeCamera.Red, ReadPixel(received, 0));
        // ... und erst sein Ergebnis wird gespiegelt.
        var photo = Assert.Single(h.Composer.LastPhotos!);
        Assert.Equal(0xFFFFFF00u, ReadPixel(photo, 0));
        Assert.Equal(0xFF00FF00u, ReadPixel(photo, 1));
    }

    [Fact]
    public void Decorator_NotActive_IsNotCalled()
    {
        var decorator = new FakePhotoDecorator { IsActive = false };
        var h = PeaceHarness(decorator);

        h.Controller.TryTrigger();
        h.RunToIdle();

        Assert.Equal(0, decorator.Calls);
        Assert.Same(h.Camera.Photo, Assert.Single(h.Composer.LastPhotos!));
    }

    [Fact]
    public void Decorator_Exception_FallsBackToPlainPhotoWithoutError()
    {
        var decorator = new FakePhotoDecorator { Throw = new InvalidOperationException("kaputt") };
        var h = PeaceHarness(decorator);
        var states = h.RecordStates();

        h.Controller.TryTrigger();
        h.RunToIdle();

        Assert.Equal(1, decorator.Calls);
        Assert.Null(h.Controller.ErrorMessage);
        Assert.DoesNotContain(BoothState.Error, states);
        Assert.Same(h.Camera.Photo, Assert.Single(h.Composer.LastPhotos!));
        Assert.Single(h.Storage.Saved);
    }

    [Fact]
    public void Decorator_Timeout_FallsBackToPlainPhotoWithoutError()
    {
        var decorator = new FakePhotoDecorator { Delay = TimeSpan.FromHours(1) };
        var h = PeaceHarness(decorator);
        var states = h.RecordStates();

        h.Controller.TryTrigger(new TriggerOptions { CountdownSeconds = 0 });
        Assert.Equal(1, decorator.Calls);
        Assert.Equal(BoothState.Capturing, h.Controller.State);   // wartet auf den Verzierer

        h.Time.Advance(BoothController.PhotoDecorateTimeout - TimeSpan.FromMilliseconds(1));
        h.Drain();
        Assert.Equal(BoothState.Capturing, h.Controller.State);

        h.RunToIdle();

        Assert.Null(h.Controller.ErrorMessage);
        Assert.DoesNotContain(BoothState.Error, states);
        Assert.Same(h.Camera.Photo, Assert.Single(h.Composer.LastPhotos!));
        Assert.Single(h.Storage.Saved);
    }

    [Fact]
    public void Decorator_CancellationWhileDecorating_AbortsSessionSilently()
    {
        var decorator = new FakePhotoDecorator { Delay = TimeSpan.FromHours(1) };
        var h = PeaceHarness(decorator);
        var states = h.RecordStates();

        h.Controller.TryTrigger(new TriggerOptions { CountdownSeconds = 0 });
        Assert.Equal(BoothState.Capturing, h.Controller.State);

        h.Controller.Cancel();   // wie beim App-Ende: bricht den Durchgang ab, auch während des Verzierens

        h.WaitUntil(() => h.Controller.State == BoothState.Idle);
        Assert.Null(h.Controller.ErrorMessage);
        Assert.DoesNotContain(BoothState.Error, states);
        Assert.Empty(h.Storage.Saved);
        Assert.Null(h.Composer.LastPhotos);
    }
}
