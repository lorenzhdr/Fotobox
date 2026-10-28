using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Fotobox.Services.Peace;
using Xunit;

namespace Fotobox.Tests;

/// <summary><see cref="MoustachePhotoDecorator"/> mit Attrappen für Gestendienst und Bart-Zustand.</summary>
[Collection(WpfCollection.Name)]
public sealed class MoustachePhotoDecoratorTests
{
    private const int W = 240, H = 160;

    private static BitmapSource WhitePhoto(int w = W, int h = H, PixelFormat? format = null)
    {
        var pixels = new byte[w * h * 4];
        Array.Fill(pixels, (byte)255);
        BitmapSource bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        if (format is { } f && f != PixelFormats.Bgra32)
            bmp = new FormatConvertedBitmap(bmp, f, null, 0);
        bmp.Freeze();
        return bmp;
    }

    private static MoustacheTrack Track(NormPoint center, MoustacheStyle style = MoustacheStyle.Walrus, double width = 70) =>
        new(1, style, new NormRect(center.X - 0.15, center.Y - 0.3, 0.3, 0.45),
            new MoustachePose(center, width, 0), TimeSpan.Zero);

    private static FaceLandmarks FaceAt(NormPoint nose, double boxWidth = 0.3)
    {
        var w = boxWidth;
        return new FaceLandmarks(new NormRect(nose.X - w / 2, nose.Y - w * 0.9, w, w * 1.3), 0.9,
            RightEye: new NormPoint(nose.X - 0.07, nose.Y - 0.12),
            LeftEye: new NormPoint(nose.X + 0.07, nose.Y - 0.12),
            Nose: nose,
            MouthRight: new NormPoint(nose.X - 0.05, nose.Y + 0.08),
            MouthLeft: new NormPoint(nose.X + 0.05, nose.Y + 0.08));
    }

    private static (MoustachePhotoDecorator Decorator, FakeGestureService Gestures, GuestMoustacheState State) Create(MoustachePlanInput? input)
    {
        var gestures = new FakeGestureService();
        var state = new GuestMoustacheState { Current = input };
        return (new MoustachePhotoDecorator(gestures, state, TimeProvider.System), gestures, state);
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var data = new byte[source.PixelWidth * source.PixelHeight * 4];
        converted.CopyPixels(data, source.PixelWidth * 4, 0);
        return data;
    }

    private static int DarkPixelsNear(BitmapSource image, NormPoint center, int radius)
    {
        var data = Pixels(image);
        var cx = (int)(center.X * image.PixelWidth);
        var cy = (int)(center.Y * image.PixelHeight);
        var dark = 0;
        for (var y = Math.Max(0, cy - radius); y < Math.Min(image.PixelHeight, cy + radius); y++)
        {
            for (var x = Math.Max(0, cx - radius); x < Math.Min(image.PixelWidth, cx + radius); x++)
            {
                if (data[(y * image.PixelWidth + x) * 4] < 128)
                    dark++;
            }
        }
        return dark;
    }

    [StaFact]
    public void IsActive_FollowsTracksInState()
    {
        var (decorator, _, state) = Create(null);
        Assert.False(decorator.IsActive);

        state.Current = new MoustachePlanInput(MoustacheTargets.Everyone, null, []);
        Assert.False(decorator.IsActive);

        state.Current = new MoustachePlanInput(MoustacheTargets.Everyone, null, [Track(new NormPoint(0.5, 0.5))]);
        Assert.True(decorator.IsActive);
    }

    [StaFact]
    public async Task WithoutMoustache_ReturnsSameInstance_AndSkipsDetection()
    {
        var (decorator, gestures, _) = Create(null);
        var photo = WhitePhoto();

        var result = await decorator.DecorateAsync(photo, 1, CancellationToken.None);

        Assert.Same(photo, result);
        Assert.Equal(0, gestures.PhotoDetectCalls);
    }

    [StaFact]
    public async Task EmptyPlan_ReturnsSameInstance()
    {
        // Der Dienst findet nichts (leere Liste), die Spur ist alt: kein Bart im Foto.
        var old = Track(new NormPoint(0.5, 0.5)) with { Age = TimeSpan.FromSeconds(3) };
        var (decorator, gestures, _) = Create(new MoustachePlanInput(MoustacheTargets.Everyone, null, [old]));
        gestures.PhotoFaces = [];
        var photo = WhitePhoto();

        var result = await decorator.DecorateAsync(photo, 1, CancellationToken.None);

        Assert.Same(photo, result);
        Assert.Equal(1, gestures.PhotoDetectCalls);
    }

    [StaFact]
    public async Task PhotoFaces_DrawMoustacheAtPhotoPosition()
    {
        var liveCenter = new NormPoint(0.1, 0.5);
        var photoNose = new NormPoint(0.4, 0.5);   // im Foto etwas anders als live
        var photoFace = FaceAt(photoNose);
        var track = Track(liveCenter) with { FaceBox = photoFace.Box };
        var (decorator, gestures, _) = Create(new MoustachePlanInput(MoustacheTargets.Everyone, null, [track]));
        gestures.PhotoFaces = [photoFace];
        var photo = WhitePhoto();

        var result = await decorator.DecorateAsync(photo, 1, CancellationToken.None);

        Assert.NotSame(photo, result);
        Assert.True(result.IsFrozen);
        Assert.Equal(photo.PixelWidth, result.PixelWidth);
        Assert.Equal(photo.PixelHeight, result.PixelHeight);
        Assert.Equal(96, result.DpiX, 3);
        Assert.Equal(96, result.DpiY, 3);
        var pose = MoustacheGeometry.FromFace(photoFace, W, H);
        Assert.True(DarkPixelsNear(result, pose.Center, 6) > 0, "Am Bartsitz im Foto fehlt der Bart.");
        Assert.Equal(0, DarkPixelsNear(result, liveCenter, 6));   // nicht an der Live-Position
        Assert.Equal(0, DarkPixelsNear(photo, pose.Center, 6));   // Original bleibt unverändert
        var sent = Assert.Single(gestures.PhotoImages);
        Assert.Equal(W, sent.Width);
        Assert.Equal(H, sent.Height);
    }

    [StaFact]
    public async Task NullFromService_FallsBackToLivePoses()
    {
        var center = new NormPoint(0.5, 0.5);
        var (decorator, gestures, _) = Create(new MoustachePlanInput(MoustacheTargets.Everyone, null, [Track(center)]));
        gestures.PhotoFaces = null;   // Fehler/Timeout des Dienstes
        var photo = WhitePhoto();

        var result = await decorator.DecorateAsync(photo, 1, CancellationToken.None);

        Assert.NotSame(photo, result);
        Assert.Equal(1, gestures.PhotoDetectCalls);
        Assert.True(DarkPixelsNear(result, center, 6) > 0);
    }

    [StaFact]
    public async Task LargePhoto_IsDownscaledForDetection_ButRenderedAtFullSize()
    {
        var center = new NormPoint(0.5, 0.5);
        var (decorator, gestures, _) = Create(new MoustachePlanInput(MoustacheTargets.Everyone, null, [Track(center, width: 300)]));
        gestures.PhotoFaces = null;
        var photo = WhitePhoto(2000, 1000);

        var result = await decorator.DecorateAsync(photo, 1, CancellationToken.None);

        var sent = Assert.Single(gestures.PhotoImages);
        Assert.Equal(MoustachePhotoDecorator.MaxDetectionWidth, sent.Width);
        Assert.Equal(480, sent.Height);
        Assert.Equal(sent.Width * 4, sent.Stride);
        Assert.Equal(sent.Stride * sent.Height, sent.Pixels.Length);
        Assert.Equal(2000, result.PixelWidth);
        Assert.Equal(1000, result.PixelHeight);
        Assert.True(DarkPixelsNear(result, center, 10) > 0);
    }

    [StaFact]
    public async Task NonBgraPhoto_IsConverted()
    {
        var center = new NormPoint(0.5, 0.5);
        var (decorator, gestures, _) = Create(new MoustachePlanInput(MoustacheTargets.Everyone, null, [Track(center)]));
        gestures.PhotoFaces = null;
        var photo = WhitePhoto(format: PixelFormats.Bgr24);

        var result = await decorator.DecorateAsync(photo, 1, CancellationToken.None);

        Assert.NotSame(photo, result);
        Assert.Equal(W * 4, Assert.Single(gestures.PhotoImages).Stride);
        Assert.True(DarkPixelsNear(result, center, 6) > 0);
    }

    [StaFact]
    public async Task Cancellation_IsPropagated()
    {
        var (decorator, _, _) = Create(new MoustachePlanInput(MoustacheTargets.Everyone, null, [Track(new NormPoint(0.5, 0.5))]));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decorator.DecorateAsync(WhitePhoto(), 1, cts.Token));
    }

    [Fact]
    public void Downscale_SmallImage_IsReturnedUnchanged()
    {
        var pixels = new byte[10 * 5 * 4];
        var image = MoustachePhotoDecorator.Downscale(pixels, 10, 5, 40);

        Assert.Same(pixels, image.Pixels);
        Assert.Equal(10, image.Width);
    }
}
