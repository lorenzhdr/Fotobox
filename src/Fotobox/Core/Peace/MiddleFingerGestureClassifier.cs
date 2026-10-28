using static Fotobox.Core.Peace.HandPoseMath;

namespace Fotobox.Core.Peace;

/// <summary>Ergebnis der Mittelfinger-Prüfung; <see cref="RejectReason"/> ist ein kurzer Grund (nur Log/Admin), null bei Treffer.</summary>
public sealed record MiddleFingerVerdict(bool IsMiddleFinger, double Score, string? RejectReason);

/// <summary>
/// Erkennt den gezeigten Mittelfinger an den 21 Handpunkten: Mittelfinger gestreckt, Zeige-, Ring- und kleiner Finger gebeugt,
/// Daumen egal. Wie <see cref="PeaceGestureClassifier"/> zählen nur Längen- und Winkelverhältnisse (unabhängig von Drehung,
/// Spiegelung und Handgröße); <c>aspect</c> rechnet die auf Bildbreite/-höhe normierten Punkte in ein isotropes Maß um. Die
/// Empfindlichkeit (10…95 %) lockert die Streckungs-Schwellen und senkt den Mindest-Handscore.
/// </summary>
public static class MiddleFingerGestureClassifier
{
    public const int PointCount = HandPoseMath.PointCount;

    /// <summary>Schwellen für eine Empfindlichkeit (außerhalb der Grenzen geklemmt).</summary>
    public readonly record struct Thresholds(double ExtensionFactor, double PipAngleDeg, double MinScore)
    {
        public static Thresholds For(int sensitivityPercent)
        {
            var t = Math.Clamp((sensitivityPercent - SettingsLimits.MiddleFingerSensitivityMin)
                / (double)(SettingsLimits.MiddleFingerSensitivityMax - SettingsLimits.MiddleFingerSensitivityMin), 0, 1);
            return new Thresholds(
                ExtensionFactor: Lerp(1.25, 1.05, t),
                PipAngleDeg: Lerp(165, 140, t),
                // Eine Quelle mit MiddleFingerSettings.MinHandScore (10 → 0,9, 95 → 0,55).
                MinScore: MiddleFingerSettings.MinHandScoreFor(sensitivityPercent));
        }
    }

    /// <param name="aspect">Breite/Höhe des Kamerabilds (Standard 16:9).</param>
    public static MiddleFingerVerdict Classify(HandLandmarks hand, int sensitivityPercent, double aspect = 16.0 / 9)
    {
        ArgumentNullException.ThrowIfNull(hand);
        if (hand.Points is not { Count: >= PointCount })
            return new MiddleFingerVerdict(false, hand.Score, "Unvollständige Handpunkte.");
        if (!(aspect > 0))
            aspect = 1;

        var th = Thresholds.For(sensitivityPercent);
        if (hand.Score < th.MinScore)
            return new MiddleFingerVerdict(false, hand.Score, "Handscore zu niedrig.");

        var p = ToIsotropic(hand, aspect);

        var scale = Dist(p[0], p[9]);
        if (scale < 1e-6)
            return new MiddleFingerVerdict(false, hand.Score, "Hand zu klein.");

        var palm = Mean(p[0], p[5], p[9], p[13], p[17]);

        if (!IsExtended(p, 9, th.ExtensionFactor, th.PipAngleDeg))
            return new MiddleFingerVerdict(false, hand.Score, "Mittelfinger nicht gestreckt.");
        if (!IsFolded(p, 5, palm, scale))
            return new MiddleFingerVerdict(false, hand.Score, "Zeigefinger nicht gebeugt.");
        if (!IsFolded(p, 13, palm, scale))
            return new MiddleFingerVerdict(false, hand.Score, "Ringfinger nicht gebeugt.");
        if (!IsFolded(p, 17, palm, scale))
            return new MiddleFingerVerdict(false, hand.Score, "Kleiner Finger nicht gebeugt.");

        return new MiddleFingerVerdict(true, hand.Score, null);
    }
}
