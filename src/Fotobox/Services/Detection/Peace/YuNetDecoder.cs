using System.IO;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;

namespace Fotobox.Services.Detection.Peace;

/// <summary>
/// Decoding der zwölf YuNet-Ausgaben (ankerfrei, je Stride 8/16/32 ein Raster aus <c>PaddedHöhe/Stride × PaddedBreite/Stride</c> Zellen):
/// <c>cls</c> [Zellen,1], <c>obj</c> [Zellen,1], <c>bbox</c> [Zellen,4] (dx, dy, dw, dh), <c>kps</c> [Zellen,10] (5 Punkte × x, y).
/// Wie OpenCV: Score = √(clamp(cls) · clamp(obj)); Mitte = (Spalte + dx) · Stride, Größe = exp(dw) · Stride; Punkte = (Spalte + kx) · Stride.
/// Punktreihenfolge: rechtes Auge, linkes Auge, Nase, rechter, linker Mundwinkel (rechts = rechts der Person = im Bild links).
/// </summary>
internal static class YuNetDecoder
{
    public static readonly int[] Strides = [8, 16, 32];

    /// <summary>
    /// Arrays je Stride (Index 0 = 8, 1 = 16, 2 = 32). Ergebnisse sind auf das Inhaltsbild
    /// (<see cref="YuNetInputSize.ContentWidth"/> × <see cref="YuNetInputSize.ContentHeight"/>) normiert, absteigend nach Score.
    /// </summary>
    public static IReadOnlyList<FaceLandmarks> Decode(float[][] cls, float[][] obj, float[][] bbox, float[][] kps, YuNetInputSize size,
        double minScore, double nmsIou = 0.3, int maxFaces = 16)
    {
        var candidates = new List<Candidate>();
        for (var s = 0; s < Strides.Length; s++)
        {
            var stride = Strides[s];
            var cols = size.PaddedWidth / stride;
            var rows = size.PaddedHeight / stride;
            var cells = cols * rows;
            if (cls[s].Length < cells || obj[s].Length < cells || bbox[s].Length < cells * 4 || kps[s].Length < cells * 10)
                throw new InvalidDataException($"Die YuNet-Ausgabe für Stride {stride} passt nicht zur Eingabegröße.");

            for (var i = 0; i < cells; i++)
            {
                var c = Math.Clamp(cls[s][i], 0f, 1f);
                var o = Math.Clamp(obj[s][i], 0f, 1f);
                var score = Math.Sqrt(c * o);
                if (!(score >= minScore))
                    continue;
                int row = i / cols, col = i % cols;
                var b = bbox[s].AsSpan(i * 4, 4);
                double cx = (col + b[0]) * stride, cy = (row + b[1]) * stride;
                double w = Math.Exp(b[2]) * stride, h = Math.Exp(b[3]) * stride;
                var k = kps[s].AsSpan(i * 10, 10);
                var pts = new double[10];
                for (var n = 0; n < 5; n++)
                {
                    pts[2 * n] = (col + k[2 * n]) * stride;
                    pts[2 * n + 1] = (row + k[2 * n + 1]) * stride;
                }
                candidates.Add(new Candidate(score, cx - w / 2, cy - h / 2, w, h, pts));
            }
        }

        candidates.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        var kept = new List<Candidate>();
        foreach (var cand in candidates)
        {
            if (kept.Count >= maxFaces)
                break;
            var overlaps = false;
            foreach (var other in kept)
            {
                if (cand.IoU(other) > nmsIou)
                {
                    overlaps = true;
                    break;
                }
            }
            if (!overlaps)
                kept.Add(cand);
        }

        double nx = 1.0 / size.ContentWidth, ny = 1.0 / size.ContentHeight;
        var result = new List<FaceLandmarks>(kept.Count);
        foreach (var c in kept)
        {
            NormPoint P(int n) => new(c.Points[2 * n] * nx, c.Points[2 * n + 1] * ny);
            result.Add(new FaceLandmarks(new NormRect(c.X * nx, c.Y * ny, c.W * nx, c.H * ny), c.Score, P(0), P(1), P(2), P(3), P(4)));
        }
        return result;
    }

    private sealed record Candidate(double Score, double X, double Y, double W, double H, double[] Points)
    {
        public double IoU(Candidate o)
        {
            var l = Math.Max(X, o.X);
            var t = Math.Max(Y, o.Y);
            var r = Math.Min(X + W, o.X + o.W);
            var b = Math.Min(Y + H, o.Y + o.H);
            if (r <= l || b <= t)
                return 0;
            var inter = (r - l) * (b - t);
            return inter / (W * H + o.W * o.H - inter);
        }
    }
}
