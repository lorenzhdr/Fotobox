using Fotobox.Core.Peace;

namespace Fotobox.Services.Detection.Peace;

/// <summary>
/// Gedrehter Ausschnitt aus einem <see cref="BgraImage"/> als Gleitkomma-Tensor (NCHW, ein Bild). Der Ausschnitt ist ein um
/// (<c>cx</c>, <c>cy</c>) gedrehtes Rechteck <c>srcW × srcH</c> (Bildpixel, Pixelkanten-Koordinaten: Pixel i deckt [i, i+1]); ein
/// positiver Winkel dreht im Uhrzeigersinn (y zeigt nach unten): „oben“ im Ausschnitt zeigt dann nach (sin, −cos). Bereiche
/// außerhalb des Bilds sind schwarz. Skaliert bilinear, bei starkem Verkleinern mit 2 × 2 Teilproben je Zielpixel.
/// </summary>
internal static class AffineCrop
{
    /// <summary>
    /// Schreibt den Ausschnitt nach <paramref name="dest"/> (Länge mindestens 3·planeW·planeH; zuerst vollständig auf 0 gesetzt).
    /// Der Ausschnitt liegt in der oberen linken Ecke einer Ebene <paramref name="planeW"/> × <paramref name="planeH"/>
    /// (≥ <paramref name="dstW"/> × <paramref name="dstH"/>, für YuNet-Auffüllung). <paramref name="bgr"/>: Kanalreihenfolge B, G, R
    /// statt R, G, B. <paramref name="scale"/> multipliziert die Bytewerte (1 = 0–255, 1/255 = 0–1).
    /// </summary>
    public static void Sample(BgraImage img, double cx, double cy, double angleRad, double srcW, double srcH,
        int dstW, int dstH, float[] dest, int planeW, int planeH, bool bgr, float scale)
    {
        ArgumentNullException.ThrowIfNull(img);
        ArgumentNullException.ThrowIfNull(dest);
        if (dstW <= 0 || dstH <= 0 || planeW < dstW || planeH < dstH)
            throw new ArgumentException("Ungültige Zielgröße.");
        var plane = planeW * planeH;
        if (dest.Length < 3 * plane)
            throw new ArgumentException("Der Zielpuffer ist zu klein.", nameof(dest));
        if (img.Width <= 0 || img.Height <= 0 || img.Stride < img.Width * 4 || img.Pixels.Length < (long)(img.Height - 1) * img.Stride + img.Width * 4)
            throw new ArgumentException("Das Bild ist leer oder kleiner als seine Größenangabe.", nameof(img));

        Array.Clear(dest, 0, 3 * plane);

        var w = img.Width;
        var h = img.Height;
        var stride = img.Stride;
        var pixels = img.Pixels;
        // Bis ~1,75-fach verkleinert: eine bilinear gelesene Probe; darüber n × n nächste Nachbarn je Zielpixel (Kastenfilter, billig).
        var factor = Math.Max(srcW / dstW, srcH / dstH);
        var taps = factor < 1.75 ? 1 : factor < 4 ? 2 : 3;
        var weight = scale / (taps * taps);
        var cos = Math.Cos(angleRad);
        var sin = Math.Sin(angleRad);
        // Kanal 0 der Ausgabe: B (Byte 0) bei BGR, sonst R (Byte 2).
        int c0 = bgr ? 0 : 2, c2 = bgr ? 2 : 0;

        for (var y = 0; y < dstH; y++)
        {
            for (var x = 0; x < dstW; x++)
            {
                float a0 = 0, a1 = 0, a2 = 0;
                for (var ty = 0; ty < taps; ty++)
                {
                    var v = ((y + (ty + 0.5) / taps) / dstH - 0.5) * srcH;
                    for (var tx = 0; tx < taps; tx++)
                    {
                        var u = ((x + (tx + 0.5) / taps) / dstW - 0.5) * srcW;
                        var gx = cx + u * cos - v * sin;
                        var gy = cy + u * sin + v * cos;
                        if (taps > 1)
                        {
                            var nx = (int)Math.Floor(gx);
                            var ny = (int)Math.Floor(gy);
                            if ((uint)nx < (uint)w && (uint)ny < (uint)h)
                            {
                                var q = ny * stride + nx * 4;
                                a0 += pixels[q + c0];
                                a1 += pixels[q + 1];
                                a2 += pixels[q + c2];
                            }
                            continue;
                        }
                        var fx = gx - 0.5;
                        var fy = gy - 0.5;
                        var x0 = (int)Math.Floor(fx);
                        var y0 = (int)Math.Floor(fy);
                        var wx = (float)(fx - x0);
                        var wy = (float)(fy - y0);
                        for (var j = 0; j < 2; j++)
                        {
                            var yy = y0 + j;
                            if ((uint)yy >= (uint)h)
                                continue;
                            var wyj = j == 0 ? 1 - wy : wy;
                            for (var i = 0; i < 2; i++)
                            {
                                var xx = x0 + i;
                                if ((uint)xx >= (uint)w)
                                    continue;
                                var wgt = wyj * (i == 0 ? 1 - wx : wx);
                                var p = yy * stride + xx * 4;
                                a0 += pixels[p + c0] * wgt;
                                a1 += pixels[p + 1] * wgt;
                                a2 += pixels[p + c2] * wgt;
                            }
                        }
                    }
                }
                var o = y * planeW + x;
                dest[o] = a0 * weight;
                dest[plane + o] = a1 * weight;
                dest[2 * plane + o] = a2 * weight;
            }
        }
    }

    /// <summary>Gedrehter, quadratischer Ausschnitt (<see cref="HandRoi"/>) in Zielgröße <paramref name="size"/> (NCHW, RGB, 0–1).</summary>
    public static void SampleRoi(BgraImage img, HandRoi roi, int size, float[] dest) =>
        Sample(img, roi.Center.X * img.Width, roi.Center.Y * img.Height, roi.AngleRad, roi.SizePx, roi.SizePx, size, size, dest, size, size, bgr: false, scale: 1f / 255f);

    /// <summary>Rechnet einen Punkt im Ausschnitt (Zielpixel, Kantenkoordinaten) zurück in Bildpixel.</summary>
    public static (double X, double Y) CropToImage(double cx, double cy, double angleRad, double srcW, double srcH, int dstW, int dstH, double px, double py)
    {
        var u = (px / dstW - 0.5) * srcW;
        var v = (py / dstH - 0.5) * srcH;
        var cos = Math.Cos(angleRad);
        var sin = Math.Sin(angleRad);
        return (cx + u * cos - v * sin, cy + u * sin + v * cos);
    }

    /// <summary>Wie <see cref="CropToImage"/> für ein <see cref="HandRoi"/>; Ergebnis normiert auf das Bild.</summary>
    public static NormPoint RoiPointToNorm(HandRoi roi, int imageW, int imageH, int cropSize, double px, double py)
    {
        var (x, y) = CropToImage(roi.Center.X * imageW, roi.Center.Y * imageH, roi.AngleRad, roi.SizePx, roi.SizePx, cropSize, cropSize, px, py);
        return new NormPoint(x / imageW, y / imageH);
    }
}
