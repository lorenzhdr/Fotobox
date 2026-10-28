using System.Buffers;
using Fotobox.Core.Fun;

namespace Fotobox.Services.Detection;

/// <summary>
/// Vorverarbeitung für YOLOX: Letterbox (Seitenverhältnis bleibt, bilinear skaliert, oben links eingefügt, Rest mit 114 gefüllt),
/// Kanalreihenfolge BGR, Werte 0–255 ohne Normalisierung, Layout CHW (1 × 3 × S × S).
/// </summary>
internal static class YoloxPreprocessor
{
    public const int DefaultInputSize = 416;
    public const float PaddingValue = 114f;

    public static int RequiredLength(int inputSize) => 3 * inputSize * inputSize;

    /// <summary>
    /// Schreibt das Eingabebild nach <paramref name="destination"/> (Länge mindestens 3·S·S) und liefert das Skalierungsverhältnis
    /// <c>r = min(S/Höhe, S/Breite)</c>; eine Box aus dem Modell wird durch <c>r</c> geteilt, um in Pixel des Analysebilds zu kommen.
    /// <paramref name="swapRedBlue"/> vertauscht Rot und Blau (nur für Vergleichstests).
    /// </summary>
    public static float Preprocess(DetectionFrame frame, float[] destination, int inputSize = DefaultInputSize, bool swapRedBlue = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(destination);
        int srcW = frame.Width, srcH = frame.Height;
        if (srcW <= 0 || srcH <= 0)
            throw new ArgumentException("Das Analysebild ist leer.", nameof(frame));
        if (frame.Pixels.Length < srcW * srcH * 4)
            throw new ArgumentException("Das Analysebild ist kleiner als seine Größenangabe.", nameof(frame));
        if (destination.Length < RequiredLength(inputSize))
            throw new ArgumentException("Der Zielpuffer ist zu klein.", nameof(destination));

        int plane = inputSize * inputSize;
        Array.Fill(destination, PaddingValue, 0, 3 * plane);

        float r = Math.Min((float)inputSize / srcH, (float)inputSize / srcW);
        int newW = Math.Clamp((int)(srcW * r), 1, inputSize);
        int newH = Math.Clamp((int)(srcH * r), 1, inputSize);

        // Bilinear mit Pixelmitten (wie OpenCV INTER_LINEAR): Quellkoordinate = (dst + 0,5) · src/dst − 0,5.
        var xIdx = ArrayPool<int>.Shared.Rent(newW * 2);
        var xWeight = ArrayPool<float>.Shared.Rent(newW);
        try
        {
            float scaleX = (float)srcW / newW;
            for (int x = 0; x < newW; x++)
            {
                float fx = (x + 0.5f) * scaleX - 0.5f;
                int x0 = (int)MathF.Floor(fx);
                float wx = fx - x0;
                int x1 = x0 + 1;
                if (x0 < 0) { x0 = 0; wx = 0; }
                if (x1 > srcW - 1) x1 = srcW - 1;
                if (x0 > srcW - 1) x0 = srcW - 1;
                xIdx[2 * x] = x0 * 4;
                xIdx[2 * x + 1] = x1 * 4;
                xWeight[x] = wx;
            }

            int ch0 = swapRedBlue ? 2 : 0; // Kanal der Ausgabe 0 (Blau) liest in Bgra32 das Byte 0, vertauscht das Byte 2
            int ch2 = swapRedBlue ? 0 : 2;
            var pixels = frame.Pixels;
            int stride = frame.Stride;
            float scaleY = (float)srcH / newH;

            for (int y = 0; y < newH; y++)
            {
                float fy = (y + 0.5f) * scaleY - 0.5f;
                int y0 = (int)MathF.Floor(fy);
                float wy = fy - y0;
                int y1 = y0 + 1;
                if (y0 < 0) { y0 = 0; wy = 0; }
                if (y1 > srcH - 1) y1 = srcH - 1;
                if (y0 > srcH - 1) y0 = srcH - 1;
                int row0 = y0 * stride, row1 = y1 * stride;
                int dst = y * inputSize;

                for (int x = 0; x < newW; x++)
                {
                    int a = xIdx[2 * x], b = xIdx[2 * x + 1];
                    float wx = xWeight[x];
                    float w00 = (1 - wx) * (1 - wy), w10 = wx * (1 - wy), w01 = (1 - wx) * wy, w11 = wx * wy;
                    int p00 = row0 + a, p10 = row0 + b, p01 = row1 + a, p11 = row1 + b;

                    destination[dst + x] = pixels[p00 + ch0] * w00 + pixels[p10 + ch0] * w10 + pixels[p01 + ch0] * w01 + pixels[p11 + ch0] * w11;
                    destination[plane + dst + x] = pixels[p00 + 1] * w00 + pixels[p10 + 1] * w10 + pixels[p01 + 1] * w01 + pixels[p11 + 1] * w11;
                    destination[2 * plane + dst + x] = pixels[p00 + ch2] * w00 + pixels[p10 + ch2] * w10 + pixels[p01 + ch2] * w01 + pixels[p11 + ch2] * w11;
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(xIdx);
            ArrayPool<float>.Shared.Return(xWeight);
        }

        return r;
    }
}
