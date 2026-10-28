using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core.Fun;
using Fotobox.Services.Detection;
using Xunit;
using Xunit.Abstractions;

namespace Fotobox.Tests;

/// <summary>
/// Sichtprüfung der Ganzkörper-Erkennung an einem echten Foto, kein Test im engeren Sinn: Läuft nur, wenn die Umgebungsvariable
/// <c>FOTOBOX_DETECTION_IMAGE</c> auf eine Bilddatei zeigt (sonst sofort beendet, auch in der CI; das Bild gehört nie ins Repo).
/// Verkleinert das Bild wie der Dienst auf höchstens 640 px Breite, analysiert es mit BGR (richtig) und mit vertauschten Kanälen
/// (Gegenprobe) und gibt Boxen, Konfidenzen und die Laufzeit aus. Mit <c>FOTOBOX_RENDER_DIR</c> entsteht ein PNG mit den Boxen.
/// <c>FOTOBOX_DETECTION_EXPECTED</c> (optional) = erwartete Mindestanzahl Personen mit Konfidenz &gt; 0,5.
/// Aufruf: <c>FOTOBOX_DETECTION_IMAGE=C:\bild.jpg FOTOBOX_RENDER_DIR=C:\out dotnet test --filter "Category=Visual&amp;FullyQualifiedName~DetectionVisual"</c>.
/// </summary>
[Collection(WpfCollection.Name)]
public class DetectionVisualTests(ITestOutputHelper output)
{
    private static DetectionFrame LoadFrame(string path, bool swapRedBlue)
    {
        var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];
        if (source.PixelWidth > PersonDetectionService.MaxAnalysisWidth)
        {
            var scale = (double)PersonDetectionService.MaxAnalysisWidth / source.PixelWidth;
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight;
        var pixels = new byte[w * h * 4];
        converted.CopyPixels(pixels, w * 4, 0);
        if (swapRedBlue)
            for (int i = 0; i < pixels.Length; i += 4)
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        return new DetectionFrame { Width = w, Height = h, Pixels = pixels, SourceWidth = w, SourceHeight = h };
    }

    [Trait("Category", "Visual")]
    [StaFact]
    public async Task Detect_PersonsInRealPhoto_BgrIsAtLeastAsGoodAsRgb()
    {
        var path = Environment.GetEnvironmentVariable("FOTOBOX_DETECTION_IMAGE");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        var frameBgr = LoadFrame(path, swapRedBlue: false);
        var frameRgb = LoadFrame(path, swapRedBlue: true);
        output.WriteLine($"Analysebild {frameBgr.Width} x {frameBgr.Height}");

        await using var detector = new YoloxPersonDetector();
        await detector.InitializeAsync();
        await detector.DetectAsync(frameBgr, 0.3); // Aufwärmlauf

        var watch = Stopwatch.StartNew();
        const int timedRuns = 10;
        IReadOnlyList<Detection> bgr = [];
        for (var i = 0; i < timedRuns; i++)
            bgr = await detector.DetectAsync(frameBgr, 0.3);
        watch.Stop();
        output.WriteLine($"Laufzeit je Analyse (Mittel aus {timedRuns}): {watch.Elapsed.TotalMilliseconds / timedRuns:F1} ms");

        var rgb = await detector.DetectAsync(frameRgb, 0.3);

        output.WriteLine("BGR (richtig):");
        foreach (var d in bgr)
            output.WriteLine($"  {d.Confidence:F3}  x={d.Box.X:F3} y={d.Box.Y:F3} w={d.Box.Width:F3} h={d.Box.Height:F3}");
        output.WriteLine("RGB (vertauscht, Gegenprobe):");
        foreach (var d in rgb)
            output.WriteLine($"  {d.Confidence:F3}  x={d.Box.X:F3} y={d.Box.Y:F3} w={d.Box.Width:F3} h={d.Box.Height:F3}");

        var bestBgr = bgr.Count > 0 ? bgr.Max(d => d.Confidence) : 0;
        var bestRgb = rgb.Count > 0 ? rgb.Max(d => d.Confidence) : 0;
        var sumBgr = bgr.Where(d => d.Confidence > 0.5).Sum(d => d.Confidence);
        var sumRgb = rgb.Where(d => d.Confidence > 0.5).Sum(d => d.Confidence);
        output.WriteLine($"Beste Konfidenz BGR {bestBgr:F3} / RGB {bestRgb:F3}; Summe > 0,5: BGR {sumBgr:F3} / RGB {sumRgb:F3}");

        var renderDir = Environment.GetEnvironmentVariable("FOTOBOX_RENDER_DIR");
        if (!string.IsNullOrWhiteSpace(renderDir))
        {
            Directory.CreateDirectory(renderDir);
            var png = Path.Combine(renderDir, "detection-" + Path.GetFileNameWithoutExtension(path) + ".png");
            SaveBoxes(frameBgr, bgr, png);
            output.WriteLine("PNG: " + png);
        }

        var expected = int.TryParse(Environment.GetEnvironmentVariable("FOTOBOX_DETECTION_EXPECTED"), out var n) ? n : 1;
        Assert.True(bgr.Count(d => d.Confidence > 0.5) >= expected, $"Erwartet: mindestens {expected} Person(en) mit Konfidenz > 0,5.");
        Assert.True(sumBgr >= sumRgb * 0.98, "BGR sollte (bis auf Rauschen von 2 %) besser oder gleich RGB sein.");
    }

    private static void SaveBoxes(DetectionFrame frame, IReadOnlyList<Detection> detections, string pngPath)
    {
        var image = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Stride);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(image, new Rect(0, 0, frame.Width, frame.Height));
            foreach (var d in detections)
            {
                var color = d.Confidence > 0.5 ? Colors.Red : Colors.Orange;
                var pen = new Pen(new SolidColorBrush(color), 3);
                var rect = new Rect(d.Box.X * frame.Width, d.Box.Y * frame.Height, d.Box.Width * frame.Width, d.Box.Height * frame.Height);
                dc.DrawRectangle(null, pen, rect);
                var label = new FormattedText($"{d.Confidence:F2}", System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 16, new SolidColorBrush(color), 1.0);
                dc.DrawText(label, new Point(rect.X + 4, rect.Y + 2));
            }
        }
        var bitmap = new RenderTargetBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(pngPath);
        encoder.Save(stream);
    }
}
