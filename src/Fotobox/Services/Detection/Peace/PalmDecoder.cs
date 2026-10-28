using Fotobox.Core.Fun;
using Fotobox.Core.Peace;

namespace Fotobox.Services.Detection.Peace;

/// <summary>Quadratisches Fenster im Bild (Pixel), das als 192 × 192 in das Palm-Modell geht; darf über den Bildrand hinausragen (dort schwarz).</summary>
internal readonly record struct PalmWindow(double X0, double Y0, double Side)
{
    /// <summary>Quadrat um die Mitte von <paramref name="region"/> mit der längeren Kantenlänge (das Gegenstück zum Letterbox der Vorlage).</summary>
    public static PalmWindow For(NormRect region, int imageW, int imageH)
    {
        var rw = Math.Max(1, region.Width * imageW);
        var rh = Math.Max(1, region.Height * imageH);
        var side = Math.Max(8, Math.Max(rw, rh));
        return new PalmWindow(region.CenterX * imageW - side / 2, region.CenterY * imageH - side / 2, side);
    }
}

/// <summary>
/// Liest die Ausgabe des Palm-Modells (PINTO0309, Anker-Decoding und NMS stecken im Modell): je Zeile
/// <c>score, box_x, box_y, box_size, kp0_x, kp0_y, kp2_x, kp2_y</c>, alle Koordinaten relativ zum 192 × 192 großen Fenster (0..1).
/// </summary>
internal static class PalmDecoder
{
    public const int Columns = 8;

    /// <summary>
    /// <paramref name="data"/> = Zeilen × 8 Werte. Liefert Kandidaten mit Score ≥ <paramref name="minScore"/>, absteigend, höchstens
    /// <paramref name="max"/>, in auf das Bild (<paramref name="imageW"/> × <paramref name="imageH"/>) normierten Koordinaten;
    /// <see cref="PalmCandidate.Size"/> relativ zur Bildbreite. Kandidaten mit Mitte außerhalb des Bilds entfallen.
    /// </summary>
    public static IReadOnlyList<PalmCandidate> Decode(ReadOnlySpan<float> data, int rows, PalmWindow window, int imageW, int imageH, double minScore, int max)
    {
        if (rows < 0 || data.Length < rows * Columns)
            throw new ArgumentException("Die Palm-Ausgabe ist zu kurz.", nameof(data));
        if (max <= 0 || imageW <= 0 || imageH <= 0)
            return [];

        var list = new List<PalmCandidate>();
        for (var r = 0; r < rows; r++)
        {
            var row = data.Slice(r * Columns, Columns);
            double score = row[0], size = row[3];
            if (!(score >= minScore) || !(size > 0))
                continue;
            double cx = window.X0 + row[1] * window.Side, cy = window.Y0 + row[2] * window.Side;
            if (cx < 0 || cx > imageW || cy < 0 || cy > imageH)
                continue;
            list.Add(new PalmCandidate(
                score,
                new NormPoint(cx / imageW, cy / imageH),
                size * window.Side / imageW,
                new NormPoint((window.X0 + row[4] * window.Side) / imageW, (window.Y0 + row[5] * window.Side) / imageH),
                new NormPoint((window.X0 + row[6] * window.Side) / imageW, (window.Y0 + row[7] * window.Side) / imageH)));
        }

        list.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        if (list.Count > max)
            list.RemoveRange(max, list.Count - max);
        return list;
    }
}
