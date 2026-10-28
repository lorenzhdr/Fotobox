namespace Fotobox.Core.Peace;

/// <summary>
/// Gemeinsame Geometrie der Handgesten-Klassifikatoren (Peace, Mittelfinger) auf den 21 MediaPipe-Handpunkten:
/// isotrope Umrechnung, Längen/Winkel und die Finger-Tests „gestreckt“ bzw. „gebeugt“. Reine Rechnung, ohne Zustand.
/// </summary>
internal static class HandPoseMath
{
    /// <summary>Anzahl der Handpunkte (MediaPipe: 0 Handgelenk; 5–8 Zeige, 9–12 Mittel, 13–16 Ring, 17–20 klein, je MCP, PIP, DIP, TIP).</summary>
    public const int PointCount = 21;

    /// <summary>Dreidimensionaler Punkt bzw. Vektor im isotropen Maß.</summary>
    public readonly record struct V3(double X, double Y, double Z)
    {
        public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    }

    /// <summary>
    /// Rechnet die auf Bildbreite (X) bzw. -höhe (Y) normierten Punkte in ein isotropes Maß um (X und Z mal <paramref name="aspect"/>,
    /// Z hat die Größenordnung von X). Voraussetzung: mindestens <see cref="PointCount"/> Punkte, <paramref name="aspect"/> &gt; 0.
    /// </summary>
    public static V3[] ToIsotropic(HandLandmarks hand, double aspect)
    {
        var p = new V3[PointCount];
        for (var i = 0; i < PointCount; i++)
        {
            var q = hand.Points[i];
            p[i] = new V3(q.X * aspect, q.Y, q.Z * aspect);
        }
        return p;
    }

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    public static double Dist(V3 a, V3 b) => (a - b).Length;

    public static V3 Mean(params V3[] v) => new(v.Average(x => x.X), v.Average(x => x.Y), v.Average(x => x.Z));

    public static double AngleDeg(V3 a, V3 b)
    {
        var la = a.Length;
        var lb = b.Length;
        if (la < 1e-9 || lb < 1e-9)
            return 0;
        var cos = (a.X * b.X + a.Y * b.Y + a.Z * b.Z) / (la * lb);
        return Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI;
    }

    /// <summary>
    /// Finger gestreckt: Die Spitze liegt deutlich weiter vom Handgelenk als das PIP-Gelenk (<paramref name="extensionFactor"/>) und das
    /// PIP-Gelenk ist fast gestreckt (Winkel MCP–PIP–TIP über <paramref name="pipAngleDeg"/>). <paramref name="mcp"/> = Index des
    /// MCP-Punkts (5, 9, 13, 17).
    /// </summary>
    public static bool IsExtended(V3[] p, int mcp, double extensionFactor, double pipAngleDeg)
    {
        var pip = p[mcp + 1];
        var tip = p[mcp + 3];
        if (Dist(p[0], tip) <= extensionFactor * Dist(p[0], pip))
            return false;
        return AngleDeg(p[mcp] - pip, tip - pip) > pipAngleDeg;
    }

    /// <summary>Finger gebeugt: Die Spitze liegt näher am Handgelenk als das PIP-Gelenk oder nahe der Handfläche.</summary>
    public static bool IsFolded(V3[] p, int mcp, V3 palm, double scale)
    {
        var tip = p[mcp + 3];
        return Dist(p[0], tip) < Dist(p[0], p[mcp + 1]) || Dist(tip, palm) < 0.6 * scale;
    }
}
