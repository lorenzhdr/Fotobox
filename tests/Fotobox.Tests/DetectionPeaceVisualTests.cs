using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Fotobox.Services.Detection.Peace;
using Xunit;
using Xunit.Abstractions;

namespace Fotobox.Tests;

/// <summary>
/// Sichtprüfung der Peace-Erkennung an einem echten Foto, kein Test im engeren Sinn: Läuft nur, wenn die Umgebungsvariable
/// <c>FOTOBOX_PEACE_IMAGE</c> auf eine Bilddatei zeigt (sonst sofort beendet, auch in der CI; das Bild gehört nie ins Repo).
/// Gibt Gesichter (Box, Score, Punkte), Handflächen im ganzen Bild und Handpunkte je Handfläche sowie die Laufzeiten aus.
/// Aufruf: <c>FOTOBOX_PEACE_IMAGE=C:\bild.jpg dotnet test --filter "Category=Visual&amp;FullyQualifiedName~DetectionPeaceVisual"</c>.
/// </summary>
[Collection(WpfCollection.Name)]
public class DetectionPeaceVisualTests(ITestOutputHelper output)
{
    private static BgraImage Load(string path)
    {
        var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight;
        var pixels = new byte[w * h * 4];
        converted.CopyPixels(pixels, w * 4, 0);
        return new BgraImage { Width = w, Height = h, Stride = w * 4, Pixels = pixels };
    }

    [Trait("Category", "Visual")]
    [StaFact]
    public async Task Detect_FacesAndHandsInRealPhoto_PrintsResults()
    {
        var path = Environment.GetEnvironmentVariable("FOTOBOX_PEACE_IMAGE");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        var image = Load(path);
        output.WriteLine($"Bild {image.Width} x {image.Height}");

        await using var faceDetector = new YuNetFaceDetector();
        await faceDetector.InitializeAsync();
        await faceDetector.DetectAsync(image, 0.5); // Aufwärmlauf
        var watch = Stopwatch.StartNew();
        IReadOnlyList<FaceLandmarks> faces = [];
        for (var i = 0; i < 10; i++)
            faces = await faceDetector.DetectAsync(image, 0.5);
        output.WriteLine($"YuNet: {watch.Elapsed.TotalMilliseconds / 10:F1} ms je Analyse, {faces.Count} Gesicht(er)");
        foreach (var f in faces)
        {
            output.WriteLine($"  Gesicht score {f.Score:F2} box ({f.Box.X:F3}, {f.Box.Y:F3}, {f.Box.Width:F3} x {f.Box.Height:F3})");
            output.WriteLine($"    rechtes Auge ({f.RightEye.X:F3}, {f.RightEye.Y:F3}) linkes Auge ({f.LeftEye.X:F3}, {f.LeftEye.Y:F3}) Nase ({f.Nose.X:F3}, {f.Nose.Y:F3})");
            output.WriteLine($"    Mund rechts ({f.MouthRight.X:F3}, {f.MouthRight.Y:F3}) links ({f.MouthLeft.X:F3}, {f.MouthLeft.Y:F3})");
        }

        await using var handDetector = new MediaPipeHandDetector();
        await handDetector.InitializeAsync();
        await handDetector.DetectPalmsAsync(image, NormRect.Full, 3); // Aufwärmlauf
        watch.Restart();
        IReadOnlyList<PalmCandidate> palms = [];
        for (var i = 0; i < 10; i++)
            palms = await handDetector.DetectPalmsAsync(image, NormRect.Full, 3);
        output.WriteLine($"Palm (ganzes Bild): {watch.Elapsed.TotalMilliseconds / 10:F1} ms je Analyse, {palms.Count} Handfläche(n)");
        foreach (var p in palms)
        {
            output.WriteLine($"  Palm score {p.Score:F2} Mitte ({p.Center.X:F3}, {p.Center.Y:F3}) Größe {p.Size:F3} kp0 ({p.Kp0.X:F3}, {p.Kp0.Y:F3}) kp2 ({p.Kp2.X:F3}, {p.Kp2.Y:F3})");
            var roi = HandRoiMath.FromPalm(p, image.Width, image.Height);
            output.WriteLine($"    ROI Mitte ({roi.Center.X:F3}, {roi.Center.Y:F3}) Kante {roi.SizePx:F0} px Winkel {roi.AngleRad * 180 / Math.PI:F0} Grad");
            watch.Restart();
            var hand = await handDetector.DetectLandmarksAsync(image, roi, 0.0);
            output.WriteLine($"    Handpunkte in {watch.Elapsed.TotalMilliseconds:F1} ms: " + (hand is null ? "keine" : $"score {hand.Score:F2} box ({hand.Box.X:F3}, {hand.Box.Y:F3}, {hand.Box.Width:F3} x {hand.Box.Height:F3})"));
            if (hand is not null)
            {
                output.WriteLine("    " + string.Join(" ", hand.Points.Select((pt, i) => $"{i}:({pt.X:F3},{pt.Y:F3},{pt.Z:F3})")));
                // Nachführen: Ausschnitt aus den Punkten muss dieselbe Hand wiederfinden.
                var tracked = HandRoiMath.FromLandmarks(hand, image.Width, image.Height);
                var again = await handDetector.DetectLandmarksAsync(image, tracked, 0.0);
                output.WriteLine($"    Nachführen: Kante {tracked.SizePx:F0} px Winkel {tracked.AngleRad * 180 / Math.PI:F0} Grad → " + (again is null ? "keine" : $"score {again.Score:F2}"));
            }
        }
    }
}
