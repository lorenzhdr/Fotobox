using System.IO;
using Fotobox.Core.Fun;

namespace Fotobox.Services.Detection;

// Der Typname Detection kollidiert innerhalb dieses Namespaces mit dem Namespace selbst.
using Detection = Fotobox.Core.Fun.Detection;

/// <summary>
/// Dekodiert die Ausgabe von YOLOX (ohne Grid-Decoding im Graph): Tensor 1 × N × (5 + Klassen), N = 52² + 26² + 13² = 3549 bei 416 px.
/// Zeilen: Strides 8, 16, 32 hintereinander, je Stride Zeile für Zeile (x schneller). Box roh (cx, cy, w, h), dann Objectness und
/// Klassenwerte (schon sigmoid). Person = Klasse 0, Wert = Objectness · Klasse 0.
/// </summary>
internal static class YoloxDecoder
{
    public const float NmsIou = 0.45f;
    private const int MaxCandidates = 200;
    private static readonly int[] Strides = [8, 16, 32];

    /// <summary>
    /// Wertet die Ausgabe aus. <paramref name="ratio"/> ist das Ergebnis von <see cref="YoloxPreprocessor.Preprocess"/>;
    /// <paramref name="imageWidth"/>/<paramref name="imageHeight"/> sind die Pixelmaße des Analysebilds (die Boxen werden darauf normiert).
    /// </summary>
    /// <exception cref="InvalidDataException">Unerwartete Form der Ausgabe.</exception>
    public static IReadOnlyList<Detection> Decode(
        ReadOnlySpan<float> output, ReadOnlySpan<long> shape, float ratio, int imageWidth, int imageHeight, double minConfidence)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || !(ratio > 0))
            throw new ArgumentException("Ungültige Bildgröße oder Skalierung.");

        int anchors, columns;
        if (shape.Length == 3 && shape[0] == 1) { anchors = checked((int)shape[1]); columns = checked((int)shape[2]); }
        else if (shape.Length == 2) { anchors = checked((int)shape[0]); columns = checked((int)shape[1]); }
        else
            throw new InvalidDataException($"Unerwartete Ausgabeform des Erkennungsmodells (Rang {shape.Length}).");

        int inputSize = InputSizeForAnchors(anchors);
        if (columns < 6)
            throw new InvalidDataException($"Unerwartete Spaltenzahl des Erkennungsmodells ({columns}).");
        if (output.Length < (long)anchors * columns)
            throw new InvalidDataException("Die Ausgabe des Erkennungsmodells ist kürzer als ihre Form angibt.");

        var candidates = new List<Candidate>();
        int row = 0;
        foreach (var stride in Strides)
        {
            int grid = inputSize / stride;
            for (int gy = 0; gy < grid; gy++)
            {
                for (int gx = 0; gx < grid; gx++, row++)
                {
                    var p = output.Slice(row * columns, columns);
                    double score = (double)p[4] * p[5];
                    if (!(score >= minConfidence))
                        continue;

                    float cx = (p[0] + gx) * stride;
                    float cy = (p[1] + gy) * stride;
                    float w = MathF.Exp(p[2]) * stride;
                    float h = MathF.Exp(p[3]) * stride;
                    if (float.IsNaN(cx + cy + w + h) || float.IsInfinity(w) || float.IsInfinity(h))
                        continue;

                    // Modellkoordinaten → Pixel des Analysebilds (/ratio) → 0..1.
                    double left = (cx - w / 2) / ratio / imageWidth;
                    double top = (cy - h / 2) / ratio / imageHeight;
                    double right = (cx + w / 2) / ratio / imageWidth;
                    double bottom = (cy + h / 2) / ratio / imageHeight;
                    var rect = NormRect.FromEdges(left, top, right, bottom).Clamp01();
                    if (rect.IsEmpty)
                        continue;
                    candidates.Add(new Candidate(rect, score));
                }
            }
        }

        if (candidates.Count == 0)
            return [];

        candidates.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        if (candidates.Count > MaxCandidates)
            candidates.RemoveRange(MaxCandidates, candidates.Count - MaxCandidates);

        var kept = new List<Detection>();
        foreach (var c in candidates)
        {
            bool suppressed = false;
            for (int i = 0; i < kept.Count; i++)
            {
                if (kept[i].Box.IoU(c.Rect) > NmsIou)
                {
                    suppressed = true;
                    break;
                }
            }
            if (!suppressed)
                kept.Add(new Detection(c.Rect, c.Score, DetectionKind.Body));
        }
        return kept;
    }

    /// <summary>Leitet die Eingabegröße S aus der Ankerzahl N = S²·(1/64 + 1/256 + 1/1024) ab (S Vielfaches von 32).</summary>
    /// <exception cref="InvalidDataException">N passt zu keiner Eingabegröße.</exception>
    public static int InputSizeForAnchors(int anchors)
    {
        if (anchors > 0)
        {
            long squared = (long)anchors * 1024;
            if (squared % 21 == 0)
            {
                long s2 = squared / 21;
                int s = (int)Math.Round(Math.Sqrt(s2));
                if ((long)s * s == s2 && s % 32 == 0 && s > 0)
                    return s;
            }
        }
        throw new InvalidDataException($"Unerwartete Ankerzahl des Erkennungsmodells ({anchors}).");
    }

    private readonly record struct Candidate(NormRect Rect, double Score);
}
