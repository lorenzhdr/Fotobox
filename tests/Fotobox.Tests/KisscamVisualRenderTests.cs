using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Core.Peace;
using Fotobox.Services.Layout;
using Fotobox.Views.Booth.Heart;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Sichtprüfung per Bilddatei, kein Test im engeren Sinn: Läuft nur, wenn die Umgebungsvariable <c>FOTOBOX_RENDER_DIR</c> auf einen
/// Ordner zeigt (sonst sofort beendet, auch in der CI). Rendert den Kisscam-Rahmen in allen fünf Farben auf einem synthetischen
/// Foto (Layout wie im Druck, 15 × 10 cm) und als Live-Variante (1280 × 720, 96 dpi), dazu einen langen Bannertext und Hochformat.
/// Aufruf: <c>FOTOBOX_RENDER_DIR=C:\pfad\render dotnet test --filter "FullyQualifiedName~KisscamVisualRenderTests"</c>.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait("Category", "Visual")]
public class KisscamVisualRenderTests
{
    private static string? RenderDir()
    {
        var dir = Environment.GetEnvironmentVariable("FOTOBOX_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            return null;
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Synthetisches Foto: Verlauf mit zwei Cartoon-Köpfen und einem Herz dazwischen.</summary>
    private static BitmapSource SyntheticPhoto(int w, int h)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x5B, 0x8D, 0xC9), Color.FromRgb(0x2E, 0x3A, 0x4E), 90), null, new Rect(0, 0, w, h));
            var skin = new SolidColorBrush(Color.FromRgb(0xF1, 0xC2, 0x9A));
            var hair = new SolidColorBrush(Color.FromRgb(0x3A, 0x26, 0x18));
            var s = Math.Min(w, h);
            foreach (var cx in new[] { w * 0.40, w * 0.60 })
            {
                dc.DrawEllipse(hair, null, new Point(cx, h * 0.42), s * 0.17, s * 0.19);
                dc.DrawEllipse(skin, null, new Point(cx, h * 0.46), s * 0.15, s * 0.18);
            }
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x6D)), null, new Point(w * 0.5, h * 0.72), s * 0.07, s * 0.07);
        }

        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    [StaFact]
    public void Render_AllColors()
    {
        var dir = RenderDir();
        if (dir is null)
            return;

        var composer = new LayoutComposer();
        var photo = SyntheticPhoto(1920, 1080);
        var landscape = new PageSpec(152.4, 101.6, 300);
        var portrait = new PageSpec(101.6, 152.4, 300);
        foreach (var color in Enum.GetValues<KisscamColor>())
        {
            var frame = new PhotoFrame(PhotoFrameKind.Kisscam, "KISS CAM", color);
            SavePng(composer.Compose([photo], new LayoutSettings(), landscape, dir, DateTime.Now, frame), Path.Combine(dir, $"kisscam_foto_{color}.png"));
            SavePng(RenderLive(photo, frame, 1280, 720), Path.Combine(dir, $"kisscam_live_{color}.png"));
        }

        var longText = new PhotoFrame(PhotoFrameKind.Kisscam, "HEUTE KÜSSEN SICH ALLE – WER BIST ALS NÄCHSTES DRAN?", KisscamColor.PinkRed);
        SavePng(composer.Compose([photo], new LayoutSettings(), landscape, dir, DateTime.Now, longText), Path.Combine(dir, "kisscam_foto_langer_text.png"));
        SavePng(composer.Compose([SyntheticPhoto(1080, 1920)], new LayoutSettings(), portrait, dir, DateTime.Now,
            new PhotoFrame(PhotoFrameKind.Kisscam, "KISS CAM", KisscamColor.Purple)), Path.Combine(dir, "kisscam_foto_hochformat.png"));
    }

    private static BitmapSource RenderLive(BitmapSource photo, PhotoFrame frame, int w, int h)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(photo, new Rect(0, 0, w, h));
            KisscamFrameRenderer.Draw(dc, new Rect(0, 0, w, h), frame, 96);
        }

        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }
}
