using static Fotobox.Core.Peace.HandPoseMath;

namespace Fotobox.Core.Peace;

/// <summary>Ergebnis der Gestenprüfung; <see cref="RejectReason"/> ist ein kurzer Grund (nur Log/Admin), null bei Treffer.</summary>
public sealed record PeaceVerdict(bool IsPeace, double Score, string? RejectReason);

/// <summary>
/// Erkennt das Peace-Zeichen (Victory) an den 21 Handpunkten: Zeige- und Mittelfinger gestreckt und gespreizt, Ring- und kleiner
/// Finger gebeugt, Daumen egal. Es zählen nur Längen- und Winkelverhältnisse, daher gilt die Prüfung unabhängig von Drehung,
/// Spiegelung und Handgröße. Die Punkte sind auf Bildbreite (X) bzw. Bildhöhe (Y) normiert; <c>aspect</c> (Breite/Höhe des Bildes)
/// rechnet sie in ein isotropes Maß um (X und Z werden mit <c>aspect</c> multipliziert, Z hat die Größenordnung von X; die
/// gemeinsame Geometrie steckt in <see cref="HandPoseMath"/>).
/// </summary>
public static class PeaceGestureClassifier
{
    // MediaPipe: 0 Handgelenk; 5–8 Zeige, 9–12 Mittel, 13–16 Ring, 17–20 klein (MCP, PIP, DIP, TIP).
    public const int PointCount = HandPoseMath.PointCount;

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

        var p = ToIsotropic(hand, aspect);

        var scale = Dist(p[0], p[9]);
        if (scale < 1e-6)
            return new PeaceVerdict(false, hand.Score, "Hand zu klein.");

        var palm = Mean(p[0], p[5], p[9], p[13], p[17]);

        if (!IsExtended(p, 5, th.ExtensionFactor, th.PipAngleDeg))
            return new PeaceVerdict(false, hand.Score, "Zeigefinger nicht gestreckt.");
        if (!IsExtended(p, 9, th.ExtensionFactor, th.PipAngleDeg))
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
}
