using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Core.Peace;
using Fotobox.Views.Booth.Peace;

namespace Fotobox.Services.Peace;

/// <summary>
/// Zeichnet den Schnurrbart der Gastansicht ins rohe (ungespiegelte) Foto. Die Gesichter im Foto werden in einer verkleinerten
/// Kopie gesucht, der <see cref="MoustachePhotoPlanner"/> ordnet sie den Live-Spuren zu; scheitert die Suche, gelten die letzten
/// Live-Posen. Aufrufe und Zeichnen laufen auf dem UI-Thread, nur Verkleinern und Gesichtssuche im Hintergrund.
/// </summary>
public sealed class MoustachePhotoDecorator(IGestureDetectionService gestures, GuestMoustacheState state, TimeProvider time) : IPhotoDecorator
{
    /// <summary>Frist der Gesichtssuche im Foto.</summary>
    public static readonly TimeSpan PhotoFaceTimeout = TimeSpan.FromMilliseconds(1500);
    /// <summary>Zusätzliche Frist über der des Dienstes (Schutz, falls ein Dienst die eigene Frist nicht einhält).</summary>
    private static readonly TimeSpan HardLimitReserve = TimeSpan.FromSeconds(1);
    /// <summary>Breite der verkleinerten Kopie für die Gesichtssuche.</summary>
    public const int MaxDetectionWidth = 960;

    public bool IsActive => state.Capture() is { Tracks.Count: > 0 };

    public async Task<BitmapSource> DecorateAsync(BitmapSource rawPhoto, int shotIndex, CancellationToken ct)
    {
        // 1. UI-Thread: Zustand festhalten und Pixel kopieren.
        if (state.Capture() is not { Tracks.Count: > 0 } input)
            return rawPhoto;

        var width = rawPhoto.PixelWidth;
        var height = rawPhoto.PixelHeight;
        if (width <= 0 || height <= 0)
            return rawPhoto;

        var source = rawPhoto.Format == PixelFormats.Bgra32 ? rawPhoto : new FormatConvertedBitmap(rawPhoto, PixelFormats.Bgra32, null, 0);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        // 2. Hintergrund: verkleinern und Gesichter suchen (Ergebnis ist normiert, also unabhängig von der Verkleinerung).
        IReadOnlyList<FaceLandmarks>? faces;
        try
        {
            faces = await Task.Run(() => DetectAsync(pixels, width, height, stride, ct), ct)
                .WaitAsync(PhotoFaceTimeout + HardLimitReserve, time, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Gesichtssuche im Foto fehlgeschlagen, Live-Posen werden verwendet ({ex.GetType().Name}).");
            faces = null;
        }

        // 3. + 4. Planen und auf dem UI-Kontext zeichnen.
        ct.ThrowIfCancellationRequested();
        var plan = MoustachePhotoPlanner.Plan(input, faces, width, height, Random.Shared.Next);
        return plan.Count == 0 ? rawPhoto : MoustacheRenderer.RenderOnto(rawPhoto, plan);
    }

    private Task<IReadOnlyList<FaceLandmarks>?> DetectAsync(byte[] pixels, int width, int height, int stride, CancellationToken ct)
    {
        var image = Downscale(pixels, width, height, stride);
        return gestures.DetectFacesInPhotoAsync(image, PhotoFaceTimeout, ct);
    }

    /// <summary>Verkleinert (Nearest) auf höchstens <see cref="MaxDetectionWidth"/> Pixel Breite; kleinere Bilder bleiben unverändert.</summary>
    internal static BgraImage Downscale(byte[] pixels, int width, int height, int stride)
    {
        if (width <= MaxDetectionWidth)
            return new BgraImage { Width = width, Height = height, Stride = stride, Pixels = pixels };

        var newWidth = MaxDetectionWidth;
        var newHeight = Math.Max(1, (int)Math.Round(height * (double)newWidth / width));
        var newStride = newWidth * 4;
        var result = new byte[newStride * newHeight];
        for (var y = 0; y < newHeight; y++)
        {
            var srcY = Math.Min(height - 1, (int)((y + 0.5) * height / newHeight));
            var srcRow = srcY * stride;
            var dstRow = y * newStride;
            for (var x = 0; x < newWidth; x++)
            {
                var srcX = Math.Min(width - 1, (int)((x + 0.5) * width / newWidth));
                Buffer.BlockCopy(pixels, srcRow + srcX * 4, result, dstRow + x * 4, 4);
            }
        }
        return new BgraImage { Width = newWidth, Height = newHeight, Stride = newStride, Pixels = result };
    }
}
