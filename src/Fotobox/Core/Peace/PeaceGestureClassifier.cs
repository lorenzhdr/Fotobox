namespace Fotobox.Core.Peace;

/// <summary>Ergebnis der Gestenprüfung; <see cref="RejectReason"/> ist ein kurzer Grund (nur Log/Admin), null bei Treffer.</summary>
public sealed record PeaceVerdict(bool IsPeace, double Score, string? RejectReason);

/// <summary>
/// Erkennt das Peace-Zeichen (Victory) an den 21 Handpunkten: Zeige- und Mittelfinger gestreckt und gespreizt, Ring- und kleiner
/// Finger gebeugt, Daumen egal. Es zählen nur Längen- und Winkelverhältnisse, daher gilt die Prüfung unabhängig von Drehung,
/// Spiegelung und Handgröße. Die Punkte sind auf Bildbreite (X) bzw. Bildhöhe (Y) normiert; <c>aspect</c> (Breite/Höhe des Bildes)
/// rechnet sie in ein isotropes Maß um (X und Z werden mit <c>aspect</c> multipliziert, Z hat die Größenordnung von X).
/// </summary>
public static class PeaceGestureClassifier
{
    // MediaPipe: 0 Handgelenk; 5–8 Zeige, 9–12 Mittel, 13–16 Ring, 17–20 klein (MCP, PIP, DIP, TIP).
    public const int PointCount = 21;

    /// <summary>Schwellen für eine Empfindlichkeit (10…95 %, außerhalb geklemmt).</summary>
    public readonly record struct Thresholds(double ExtensionFactor, double PipAngleDeg, double SpreadAngleDeg, double MinScore)
    {
        public static Thresholds For(int sensitivityPercent)
        {
            var t = Math.Clamp((sensitivityPercent - SettingsLimits.PeaceSensitivityMin)
                / (double)(SettingsLimits.PeaceSensitivityMax - SettingsLimits.PeaceSensitivityMin), 0, 1);
            return new Thresholds(
                ExtensionFactor: Lerp(1.25, 1.05, t),
                PipAngleDeg: Lerp(165, 140, t),
                SpreadAngleDeg: Lerp(25, 10, t),
                MinScore: Lerp(0.9, 0.55, t));
        }
    }

    /// <param name="aspect">Breite/Höhe des Kamerabilds (Standard 16:9).</param>
    public static PeaceVerdict Classify(HandLandmarks hand, int sensitivityPercent, double aspect = 16.0 / 9)
    {
        ArgumentNullException.ThrowIfNull(hand);
        if (hand.Points is not { Count: >= PointCount })
            return new PeaceVerdict(false, hand.Score, "Unvollständige Handpunkte.");
        if (!(aspect > 0))
            aspect = 1;

        var th = Thresholds.For(sensitivityPercent);
        if (hand.Score < th.MinScore)
            return new PeaceVerdict(false, hand.Score, "Handscore zu niedrig.");

        var p = new V3[PointCount];
        for (var i = 0; i < PointCount; i++)
        {
            var q = hand.Points[i];
            p[i] = new V3(q.X * aspect, q.Y, q.Z * aspect);
        }

        var scale = Dist(p[0], p[9]);
        if (scale < 1e-6)
            return new PeaceVerdict(false, hand.Score, "Hand zu klein.");

        var palm = Mean(p[0], p[5], p[9], p[13], p[17]);

        if (!IsExtended(p, 5, th))
            return new PeaceVerdict(false, hand.Score, "Zeigefinger nicht gestreckt.");
        if (!IsExtended(p, 9, th))
            return new PeaceVerdict(false, hand.Score, "Mittelfinger nicht gestreckt.");
        if (!IsFolded(p, 13, palm, scale))
            return new PeaceVerdict(false, hand.Score, "Ringfinger nicht gebeugt.");
        if (!IsFolded(p, 17, palm, scale))
            return new PeaceVerdict(false, hand.Score, "Kleiner Finger nicht gebeugt.");

        var spreadAngle = AngleDeg(p[8] - p[5], p[12] - p[9]);
        if (!(spreadAngle > th.SpreadAngleDeg || Dist(p[8], p[12]) > 0.3 * scale))
            return new PeaceVerdict(false, hand.Score, "Finger nicht gespreizt.");

        return new PeaceVerdict(true, hand.Score, null);
    }

    private static bool IsExtended(V3[] p, int mcp, Thresholds th)
    {
        var pip = p[mcp + 1];
        var tip = p[mcp + 3];
        if (Dist(p[0], tip) <= th.ExtensionFactor * Dist(p[0], pip))
            return false;
        return AngleDeg(p[mcp] - pip, tip - pip) > th.PipAngleDeg;
    }

    private static bool IsFolded(V3[] p, int mcp, V3 palm, double scale)
    {
        var tip = p[mcp + 3];
        return Dist(p[0], tip) < Dist(p[0], p[mcp + 1]) || Dist(tip, palm) < 0.6 * scale;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private readonly record struct V3(double X, double Y, double Z)
    {
        public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    }

    private static double Dist(V3 a, V3 b) => (a - b).Length;

    private static V3 Mean(params V3[] v) => new(v.Average(x => x.X), v.Average(x => x.Y), v.Average(x => x.Z));

    private static double AngleDeg(V3 a, V3 b)
    {
        var la = a.Length;
        var lb = b.Length;
        if (la < 1e-9 || lb < 1e-9)
            return 0;
        var cos = (a.X * b.X + a.Y * b.Y + a.Z * b.Z) / (la * lb);
        return Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI;
    }
}
