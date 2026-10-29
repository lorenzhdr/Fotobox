using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Core.Heart;
using Fotobox.Core.Peace;
using Fotobox.Services.Layout;
using Fotobox.Views.Booth.Heart;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Layout mit Kisscam-Rahmen: Seitengröße, Rahmenfarbe, kein Caption-Band/Overlay, robuste Texte.</summary>
[Collection(WpfCollection.Name)]
public class LayoutComposerKisscamTests : IDisposable
{
    private static readonly PageSpec Portrait = new(101.6, 152.4, 150);          // 600 × 900
    private static readonly PageSpec Landscape = Portrait.ToOrientation(true);   // 900 × 600
    private static readonly DateTime Stamp = new(2026, 10, 23, 18, 30, 0);

    private readonly LayoutComposer _composer = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-kisscam-" + Guid.NewGuid().ToString("N"));

    public LayoutComposerKisscamTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* Aufräumen ist best effort */ }
    }

    private static BitmapSource Solid(Color c, int w = 1200, int h = 800)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = c.A;
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    private static byte[] Pixels(BitmapSource bmp)
    {
        var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
        return px;
    }

    private static (byte B, byte G, byte R, byte A) Pixel(BitmapSource bmp, int x, int y)
    {
        var px = new byte[4];
        bmp.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return (px[0], px[1], px[2], px[3]);
    }

    private BitmapSource Compose(LayoutSettings layout, PageSpec page, PhotoFrame? frame, BitmapSource? photo = null) =>
        _composer.Compose([photo ?? Solid(Colors.SteelBlue)], layout, page, _dir, Stamp, frame);

    [StaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Frame_KeepsPageSizeAndDpi(bool landscape)
    {
        var page = landscape ? Landscape : Portrait;

        var result = Compose(new LayoutSettings { Kind = LayoutKind.SingleBorderless }, page, new PhotoFrame(PhotoFrameKind.Kisscam, "KISS CAM", KisscamColor.PinkRed));

        Assert.Equal(page.PixelWidth, result.PixelWidth);
        Assert.Equal(page.PixelHeight, result.PixelHeight);
        Assert.Equal(page.Dpi, result.DpiX, 3);
        Assert.Equal(page.Dpi, result.DpiY, 3);
        Assert.Equal(PixelFormats.Bgra32, result.Format);
        Assert.True(result.IsFrozen);
    }

    [StaTheory]
    [InlineData(KisscamColor.PinkRed)]
    [InlineData(KisscamColor.Red)]
    [InlineData(KisscamColor.Gold)]
    [InlineData(KisscamColor.Purple)]
    [InlineData(KisscamColor.BlackWhite)]
    public void Frame_BandPixelsHavePaletteColor_AndPhotoShowsInside(KisscamColor color)
    {
        var page = Portrait;
        var expected = KisscamPalette.For(color).Frame;
        var geo = KisscamFrameGeometry.For(page.PixelWidth, page.PixelHeight);

        var result = Compose(new LayoutSettings { Kind = LayoutKind.SingleBorderless }, page, new PhotoFrame(PhotoFrameKind.Kisscam, "KISS CAM", color), Solid(Colors.White));

        // Äußerster Pixel des Bandes jeweils mittig zwischen zwei Lichtern (das Glühen reicht nicht bis dorthin).
        var half = geo.Thickness / 2;
        var samples = new[]
        {
            Between(geo.Lights.Where(p => Math.Abs(p.X - half) < 1e-6).OrderBy(p => p.Y).ToList(), x: 1),
            Between(geo.Lights.Where(p => Math.Abs(p.X - (page.PixelWidth - half)) < 1e-6).OrderBy(p => p.Y).ToList(), x: page.PixelWidth - 2),
            Between(geo.Lights.Where(p => Math.Abs(p.Y - half) < 1e-6).OrderBy(p => p.X).ToList(), y: 1),
            Between(geo.Lights.Where(p => Math.Abs(p.Y - (page.PixelHeight - half)) < 1e-6).OrderBy(p => p.X).ToList(), y: page.PixelHeight - 2),
        };
        foreach (var (px, py) in samples)
        {
            var actual = Pixel(result, px, py);
            Assert.InRange(actual.R, expected.R - 3, expected.R + 3);
            Assert.InRange(actual.G, expected.G - 3, expected.G + 3);
            Assert.InRange(actual.B, expected.B - 3, expected.B + 3);
            Assert.Equal(255, actual.A);
        }

        // Das Foto (weiß) bleibt in der Bildmitte sichtbar.
        var center = Pixel(result, page.PixelWidth / 2, page.PixelHeight / 2);
        Assert.True(center.R > 240 && center.G > 240 && center.B > 240);
    }

    /// <summary>Mitte zwischen dem dritten und vierten Licht der Kante; die feste Koordinate (x oder y) kommt vom Aufrufer.</summary>
    private static (int X, int Y) Between(List<FramePoint> lights, int? x = null, int? y = null)
    {
        var a = lights[3];
        var b = lights[4];
        return (x ?? (int)Math.Round((a.X + b.X) / 2), y ?? (int)Math.Round((a.Y + b.Y) / 2));
    }

    [StaFact]
    public void Frame_HasNoCaptionBandAndNoOverlay_RegardlessOfLayoutSettings()
    {
        var frame = new PhotoFrame(PhotoFrameKind.Kisscam, "KISS CAM", KisscamColor.PinkRed);
        var plain = Compose(new LayoutSettings { Kind = LayoutKind.SingleBorderless }, Portrait, frame, Solid(Colors.White));

        File.WriteAllBytes(Path.Combine(_dir, "ov.png"), Encode(Solid(Colors.Red)));
        var captioned = Compose(new LayoutSettings { Kind = LayoutKind.SingleBorderless, CaptionText = "Max & Erika", ShowDate = true }, Portrait, frame, Solid(Colors.White));
        var overlay = Compose(new LayoutSettings { Kind = LayoutKind.Overlay, OverlayFileName = "ov.png", CaptionText = "Text", ShowDate = true }, Portrait, frame, Solid(Colors.White));
        var strip = Compose(new LayoutSettings { Kind = LayoutKind.Strip, StripPhotoCount = 3, CaptionText = "Text" }, Portrait, frame, Solid(Colors.White));

        Assert.Equal(Pixels(plain), Pixels(captioned));
        Assert.Equal(Pixels(plain), Pixels(overlay));
        Assert.Equal(Pixels(plain), Pixels(strip));
    }

    [StaFact]
    public void WithoutFrame_StillComposesAsBefore()
    {
        var layout = new LayoutSettings { Kind = LayoutKind.SingleBorderless };
        var photo = Solid(Colors.SteelBlue);

        var a = _composer.Compose([photo], layout, Portrait, _dir, Stamp);
        var b = _composer.Compose([photo], layout, Portrait, _dir, Stamp, null);

        Assert.Equal(Pixels(a), Pixels(b));
        var corner = Pixel(a, 0, 0);
        Assert.Equal(Colors.SteelBlue.R, corner.R);
    }

    [StaTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A")]
    [InlineData("KISS CAM")]
    [InlineData("Dies ist ein sehr, sehr langer Bannertext, der unmöglich in ein einziges Banner passt und gekürzt werden muss")]
    [InlineData("ÄÖÜ ♥ WWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWW")]
    public void Frame_AnyBannerText_DoesNotThrow(string text)
    {
        var frame = new PhotoFrame(PhotoFrameKind.Kisscam, text, KisscamColor.Gold);

        var portrait = Compose(new LayoutSettings(), Portrait, frame);
        var landscape = Compose(new LayoutSettings(), Landscape, frame);

        Assert.Equal(Portrait.PixelWidth, portrait.PixelWidth);
        Assert.Equal(Landscape.PixelWidth, landscape.PixelWidth);
    }

    [StaFact]
    public void Frame_TinyPage_DoesNotThrow()
    {
        var result = Compose(new LayoutSettings(), new PageSpec(10, 10, 72), new PhotoFrame(PhotoFrameKind.Kisscam, "KISS CAM", KisscamColor.Red));

        Assert.True(result.PixelWidth > 0);
    }

    private static byte[] Encode(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
