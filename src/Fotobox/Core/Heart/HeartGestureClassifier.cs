using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using static Fotobox.Core.Peace.HandPoseMath;

namespace Fotobox.Core.Heart;

/// <summary>
/// Ergebnis einer Herzprüfung. <see cref="Score"/> = Güte 0..1 (höher = besser, Grundlage für <see cref="HeartMatch.Score"/>),
/// <see cref="Cost"/> = Abstandsmaß (kleiner = besser, Grundlage der Paarwahl). <see cref="Center"/>/<see cref="SizeNorm"/> sind normiert
/// (ungespiegeltes Bild) und nur bei einem Treffer gesetzt. <see cref="RejectReason"/> ist ein kurzer deutscher Grund (nur Log/Admin),
/// null bei Treffer.
/// </summary>
public sealed record HeartVerdict(bool IsHeart, HeartShape? Shape, double Score, double Cost, NormPoint Center, double SizeNorm, string? RejectReason)
{
    public static HeartVerdict Reject(string reason) => new(false, null, 0, double.MaxValue, default, 0, reason);
}

/// <summary>
/// Erkennt Herzgesten an den 21 Handpunkten (MediaPipe-Reihenfolge, siehe <see cref="HandPoseMath"/>): Zwei-Hände-Herzen
/// (<see cref="HeartShape.Classic"/>, <see cref="HeartShape.Roof"/>, <see cref="HeartShape.Inverted"/>) aus Paaren von Händen und das
/// Ein-Hand-<see cref="HeartShape.FingerHeart"/> (experimentell). Gerechnet wird 2D-isotrop (P = (x·aspect, y), Einheit Bildhöhe), daher
/// gelten alle Prüfungen unabhängig von Seitenverhältnis und Handgröße. Jede Schwelle wird mit der Empfindlichkeit von streng
/// (<c>t</c> = 0, Empfindlichkeit 10) auf locker (<c>t</c> = 1, Empfindlichkeit 95) gelegt. Reine Rechnung, ohne Zustand.
/// </summary>
public static class HeartGestureClassifier
{
    public const int PointCount = HandPoseMath.PointCount;

    /// <summary>Mehr Hände werden nicht paarweise geprüft (Obergrenze der Rechenzeit).</summary>
    public const int MaxHands = 8;

    // Feste Grenzen (keine Empfindlichkeit): Verhältnisse zur gemittelten Handgröße S.
    private const double MinWristDistance = 0.8;
    private const double MinMeanLandmarkDistance = 0.5;
    private const double MaxHeightFactor = 3.0;
    private const double MinWristSide = 0.3;
    private const double RoofFingerMaxDeg = 60;
    private const double FingerHeartPalmDistance = 0.7;
    private const double FingerHeartIndexReach = 1.1;

    /// <summary>Schwellen für eine Empfindlichkeit (10…95 %, außerhalb geklemmt); <see cref="T"/> = 0 (streng) … 1 (locker).</summary>
    public readonly record struct Thresholds(
        double T,
        double MaxSizeRatio,
        double TipTouch,
        double MinHeight,
        double MaxTiltDeg,
        double MinOpening,
        double FingerHeartPinch,
        double ExtensionFactor,
        double PipAngleDeg)
    {
        public static Thresholds For(int sensitivityPercent)
        {
            var t = Math.Clamp((sensitivityPercent - SettingsLimits.HeartSensitivityMin)
                / (double)(SettingsLimits.HeartSensitivityMax - SettingsLimits.HeartSensitivityMin), 0, 1);
            return new Thresholds(
                T: t,
                MaxSizeRatio: Lerp(1.6, 2.2, t),
                TipTouch: Lerp(0.30, 0.55, t),
                MinHeight: Lerp(0.70, 0.45, t),
                MaxTiltDeg: Lerp(30, 55, t),
                MinOpening: Lerp(0.70, 0.45, t),
                FingerHeartPinch: Lerp(0.30, 0.45, t),
                ExtensionFactor: Lerp(1.25, 1.05, t),
                PipAngleDeg: Lerp(165, 140, t));
        }
    }

    /// <summary>
    /// Findet alle Herzen unter den Händen: Paare werden geprüft, die Treffer gierig nach Güte vergeben (jede Hand höchstens einmal),
    /// danach prüft das Fingerherz die übrigen Hände. Nur Formen, die <paramref name="s"/> aktiviert, liefern Treffer (eine Hand eines
    /// nicht aktivierten Zwei-Hände-Herzens zählt trotzdem als vergeben). <see cref="HeartSettings.Enabled"/> wertet die Engine aus, nicht
    /// der Klassifikator. Das Ergebnis ist nach Güte absteigend sortiert; <see cref="HeartMatch.HandA"/>/<see cref="HeartMatch.HandB"/>
    /// sind Indizes in <paramref name="hands"/>.
    /// </summary>
    /// <param name="aspect">Breite/Höhe des Kamerabilds (Standard 16:9).</param>
    public static IReadOnlyList<HeartMatch> FindHearts(IReadOnlyList<HandLandmarks> hands, HeartSettings s, double aspect = 16.0 / 9)
    {
        ArgumentNullException.ThrowIfNull(hands);
        ArgumentNullException.ThrowIfNull(s);
        if (hands.Count == 0 || !s.HasAnyShape)
            return [];
        if (!(aspect > 0))
            aspect = 1;

        var count = Math.Min(hands.Count, MaxHands);
        var candidates = new List<(int A, int B, HeartVerdict Verdict)>();
        for (var i = 0; i < count; i++)
        {
            for (var j = i + 1; j < count; j++)
            {
                var verdict = ClassifyPair(hands[i], hands[j], s, aspect);
                if (verdict.IsHeart)
                    candidates.Add((i, j, verdict));
            }
        }

        var used = new HashSet<int>();
        var matches = new List<HeartMatch>();
        foreach (var (a, b, verdict) in candidates.OrderBy(c => c.Verdict.Cost).ThenBy(c => c.A).ThenBy(c => c.B))
        {
            if (used.Contains(a) || used.Contains(b))
                continue;
            used.Add(a);
            used.Add(b);
            var shape = verdict.Shape!.Value;
            if (s.IsShapeEnabled(shape))
                matches.Add(new HeartMatch(shape, a, b, verdict.Center, verdict.SizeNorm, Union(hands[a].Box, hands[b].Box), verdict.Score));
        }

        if (s.ShapeFingerHeart)
        {
            for (var i = 0; i < count; i++)
            {
                if (used.Contains(i))
                    continue;
                var verdict = ClassifyFingerHeart(hands[i], s, aspect);
                if (verdict.IsHeart)
                    matches.Add(new HeartMatch(HeartShape.FingerHeart, i, -1, verdict.Center, verdict.SizeNorm, hands[i].Box, verdict.Score));
            }
        }

        return matches.OrderByDescending(m => m.Score).ToList();
    }

    /// <summary>
    /// Prüft, ob zwei Hände ein Zwei-Hände-Herz bilden, und bestimmt die Form (Dach, Klassisch oder Umgedreht; die Prüfung kennt keine
    /// Formfilter, <see cref="HeartSettings"/> liefert hier nur Empfindlichkeit und Mindest-Handscore).
    /// </summary>
    public static HeartVerdict ClassifyPair(HandLandmarks a, HandLandmarks b, HeartSettings s, double aspect = 16.0 / 9)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(s);
        if (a.Points is not { Count: >= PointCount } || b.Points is not { Count: >= PointCount })
            return HeartVerdict.Reject("Unvollständige Handpunkte.");
        if (!IsFinite(a) || !IsFinite(b))
            return HeartVerdict.Reject("Ungültige Handpunkte.");
        if (!(aspect > 0))
            aspect = 1;

        var th = Thresholds.For(s.SensitivityPercent);
        var minScore = s.MinHandScore;
        if (a.Score < minScore || b.Score < minScore)
            return HeartVerdict.Reject("Handscore zu niedrig.");

        var pa = ToIsotropic(a, aspect);
        var pb = ToIsotropic(b, aspect);
        var sA = D2(pa[0], pa[9]);
        var sB = D2(pb[0], pb[9]);
        if (sA < 1e-6 || sB < 1e-6)
            return HeartVerdict.Reject("Hand zu klein.");

        if (Math.Max(sA, sB) / Math.Min(sA, sB) > th.MaxSizeRatio)
            return HeartVerdict.Reject("Hände sehr unterschiedlich groß.");
        var size = (sA + sB) / 2;

        if (D2(pa[0], pb[0]) < MinWristDistance * size)
            return HeartVerdict.Reject("Handgelenke zu dicht beieinander.");
        var meanDistance = 0.0;
        for (var i = 0; i < PointCount; i++)
            meanDistance += D2(pa[i], pb[i]);
        if (meanDistance / PointCount < MinMeanLandmarkDistance * size)
            return HeartVerdict.Reject("Es ist dieselbe Hand.");

        var tipsIndex = D2(pa[8], pb[8]);
        var tipsThumb = D2(pa[4], pb[4]);
        if (tipsIndex > th.TipTouch * size || tipsThumb > th.TipTouch * size)
            return HeartVerdict.Reject("Fingerspitzen berühren sich nicht.");

        var (tx, ty) = Mid(pa[8], pb[8]);
        var (ux, uy) = Mid(pa[4], pb[4]);
        var vx = tx - ux;
        var vy = ty - uy;
        var height = Math.Sqrt(vx * vx + vy * vy);
        if (height < th.MinHeight * size)
            return HeartVerdict.Reject("Herz zu flach.");
        if (height > MaxHeightFactor * size)
            return HeartVerdict.Reject("Herz zu hoch.");

        var upright = Angle2(vx, vy, 0, -1) <= th.MaxTiltDeg;
        var inverted = Angle2(-vx, -vy, 0, -1) <= th.MaxTiltDeg;
        if (!upright && !inverted)
            return HeartVerdict.Reject("Herz zu stark geneigt.");

        // Handgelenke auf verschiedenen Seiten der Linie Daumenspitzen → Zeigefingerspitzen (schließt Gebetshände aus).
        var sideA = Cross2(vx, vy, pa[0].X - ux, pa[0].Y - uy) / height;
        var sideB = Cross2(vx, vy, pb[0].X - ux, pb[0].Y - uy) / height;
        if (Math.Sign(sideA) == Math.Sign(sideB) || Math.Abs(sideA) < MinWristSide * size || Math.Abs(sideB) < MinWristSide * size)
            return HeartVerdict.Reject("Handgelenke nicht beidseits des Herzens.");

        if (D2(pa[8], pa[4]) < th.MinOpening * sA || D2(pb[8], pb[4]) < th.MinOpening * sB)
            return HeartVerdict.Reject("Daumen und Zeigefinger nicht geöffnet.");

        HeartShape shape;
        if (inverted)
            shape = HeartShape.Inverted;
        else if (IsRoofHand(pa, th) && IsRoofHand(pb, th))
            shape = HeartShape.Roof;
        else
            shape = HeartShape.Classic;

        var cost = (tipsIndex + tipsThumb) / size;
        var quality = Math.Clamp(1 - cost / (2 * th.TipTouch), 0, 1);
        var center = new NormPoint((tx + ux) / 2 / aspect, (ty + uy) / 2);
        return new HeartVerdict(true, shape, quality, cost, center, 0.8 * height, null);
    }

    /// <summary>
    /// Prüft das Fingerherz (eine Hand, experimentell): Daumenspitze am Zeigefinger (Spitze oder DIP), Zeigefinger nicht in der Faust,
    /// Mittel-, Ring- und kleiner Finger gebeugt (ab <c>t</c> ≥ 0,5 genügen Mittelfinger und ein weiterer) und, nur bei strenger Einstellung
    /// (<c>t</c> &lt; 0,5), der Daumen kreuzt den Zeigefinger. Ob die Hand zu einem Zwei-Hände-Herz gehört, entscheidet
    /// <see cref="FindHearts"/>.
    /// </summary>
    public static HeartVerdict ClassifyFingerHeart(HandLandmarks hand, HeartSettings s, double aspect = 16.0 / 9)
    {
        ArgumentNullException.ThrowIfNull(hand);
        ArgumentNullException.ThrowIfNull(s);
        if (hand.Points is not { Count: >= PointCount })
            return HeartVerdict.Reject("Unvollständige Handpunkte.");
        if (!IsFinite(hand))
            return HeartVerdict.Reject("Ungültige Handpunkte.");
        if (!(aspect > 0))
            aspect = 1;

        var th = Thresholds.For(s.SensitivityPercent);
        if (hand.Score < s.MinHandScore)
            return HeartVerdict.Reject("Handscore zu niedrig.");

        var p = ToIsotropic(hand, aspect);
        var size = D2(p[0], p[9]);
        if (size < 1e-6)
            return HeartVerdict.Reject("Hand zu klein.");

        var pinch = Math.Min(D2(p[4], p[8]), D2(p[4], p[7]));
        if (pinch > th.FingerHeartPinch * size)
            return HeartVerdict.Reject("Daumen berührt den Zeigefinger nicht.");

        var palm = Mean(p[0], p[5], p[9], p[13], p[17]);
        if (D2(p[8], palm) < FingerHeartPalmDistance * size || !(D2(p[0], p[8]) > FingerHeartIndexReach * D2(p[0], p[5])))
            return HeartVerdict.Reject("Zeigefinger zur Faust eingerollt.");

        var scale3 = Dist(p[0], p[9]);
        var middleFolded = IsFolded(p, 9, palm, scale3);
        var ringFolded = IsFolded(p, 13, palm, scale3);
        var littleFolded = IsFolded(p, 17, palm, scale3);
        var foldedOk = th.T >= 0.5 ? middleFolded && (ringFolded || littleFolded) : middleFolded && ringFolded && littleFolded;
        if (!foldedOk)
            return HeartVerdict.Reject("Übrige Finger nicht gebeugt.");

        if (th.T < 0.5)
        {
            var axisX = p[8].X - p[5].X;
            var axisY = p[8].Y - p[5].Y;
            var tip = Cross2(axisX, axisY, p[4].X - p[5].X, p[4].Y - p[5].Y);
            var root = Cross2(axisX, axisY, p[2].X - p[5].X, p[2].Y - p[5].Y);
            if (Math.Sign(tip) == Math.Sign(root))
                return HeartVerdict.Reject("Daumen kreuzt den Zeigefinger nicht.");
        }

        var cost = pinch / size;
        var quality = Math.Clamp(1 - cost / th.FingerHeartPinch, 0, 1);
        var (cx, cy) = Mid(p[4], p[8]);
        return new HeartVerdict(true, HeartShape.FingerHeart, quality, cost, new NormPoint(cx / aspect, cy), 0.8 * size, null);
    }

    // ---- intern

    // Eine Hand mit nicht endlichen Werten (NaN/Unendlich aus einem kaputten Modellergebnis) wird verworfen: Math.Sign wirft bei NaN
    // eine ArithmeticException, und Vergleiche mit NaN sind stets falsch (die Schwellen würden sonst fälschlich bestanden).
    private static bool IsFinite(HandLandmarks hand)
    {
        if (!double.IsFinite(hand.Score)
            || !double.IsFinite(hand.Box.X) || !double.IsFinite(hand.Box.Y)
            || !double.IsFinite(hand.Box.Right) || !double.IsFinite(hand.Box.Bottom))
            return false;
        for (var i = 0; i < PointCount; i++)
        {
            var point = hand.Points[i];
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
                return false;
        }
        return true;
    }

    // Dach: Mindestens zwei der Finger Mittel/Ring/klein sind gestreckt und zeigen grob nach oben.
    private static bool IsRoofHand(V3[] p, Thresholds th)
    {
        var up = 0;
        foreach (var mcp in new[] { 9, 13, 17 })
        {
            if (!IsExtended(p, mcp, th.ExtensionFactor, th.PipAngleDeg))
                continue;
            if (Angle2(p[mcp + 3].X - p[mcp].X, p[mcp + 3].Y - p[mcp].Y, 0, -1) <= RoofFingerMaxDeg)
                up++;
        }
        return up >= 2;
    }

    private static double D2(V3 a, V3 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static (double X, double Y) Mid(V3 a, V3 b) => ((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private static double Cross2(double ax, double ay, double bx, double by) => ax * by - ay * bx;

    private static double Angle2(double ax, double ay, double bx, double by) => AngleDeg(new V3(ax, ay, 0), new V3(bx, by, 0));

    private static NormRect Union(NormRect a, NormRect b) =>
        NormRect.FromEdges(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
}
