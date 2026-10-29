using Fotobox.Core.Peace;
using static Fotobox.Core.Peace.HandPoseMath;

namespace Fotobox.Core.Heart;

// Diagnose: rechnet alle Prüfgrößen mit ihren Schwellen aus, ohne bei der ersten Abweichung abzubrechen. Dieselben Formeln und
// Schwellen wie ClassifyPair/ClassifyFingerHeart (die unverändert das Urteil fällen); nur fürs Log, ohne Einfluss auf die Erkennung.
public static partial class HeartGestureClassifier
{
    private const double MinHandSize = 1e-6;

    /// <summary>
    /// Alle Prüfgrößen von <see cref="ClassifyPair"/> für das Paar <paramref name="a"/>/<paramref name="b"/> samt Schwelle, auch nach
    /// der ersten nicht bestandenen. Bei unvollständigen oder ungültigen Handpunkten oder einer Hand ohne Größe gibt es nur die
    /// Prüfungen, die sich noch rechnen lassen.
    /// </summary>
    public static HeartDiagnosis Diagnose(HandLandmarks a, HandLandmarks b, HeartSettings s, double aspect = 16.0 / 9)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(s);
        var verdict = ClassifyPair(a, b, s, aspect);
        var checks = new List<HeartCheck>(20);
        if (a.Points is not { Count: >= PointCount } || b.Points is not { Count: >= PointCount })
            return new HeartDiagnosis("Paar", checks, verdict, "Unvollständige Handpunkte");
        if (!IsFinite(a) || !IsFinite(b))
            return new HeartDiagnosis("Paar", checks, verdict, "Ungültige Handpunkte");
        if (!(aspect > 0))
            aspect = 1;

        var th = Thresholds.For(s.SensitivityPercent);
        var minScore = s.MinHandScore;
        checks.Add(new HeartCheck("Score A", a.Score, ">=", minScore, a.Score >= minScore));
        checks.Add(new HeartCheck("Score B", b.Score, ">=", minScore, b.Score >= minScore));

        var pa = ToIsotropic(a, aspect);
        var pb = ToIsotropic(b, aspect);
        var sA = D2(pa[0], pa[9]);
        var sB = D2(pb[0], pb[9]);
        var info = $"Seitenverh.={HeartDiagnosisFormat.Num(aspect)}, S_A={HeartDiagnosisFormat.Num(sA)}, S_B={HeartDiagnosisFormat.Num(sB)}";
        if (sA < MinHandSize || sB < MinHandSize)
        {
            checks.Add(new HeartCheck("Handgröße", Math.Min(sA, sB), ">=", MinHandSize, false));
            return new HeartDiagnosis("Paar", checks, verdict, info);
        }

        var size = (sA + sB) / 2;
        info += $", S={HeartDiagnosisFormat.Num(size)}";
        checks.Add(new HeartCheck("Größenverhältnis", Math.Max(sA, sB) / Math.Min(sA, sB), "<=", th.MaxSizeRatio,
            Math.Max(sA, sB) / Math.Min(sA, sB) <= th.MaxSizeRatio));

        var wrist = D2(pa[0], pb[0]) / size;
        checks.Add(new HeartCheck("Handgelenkabstand/S", wrist, ">=", MinWristDistance, wrist >= MinWristDistance));

        var meanDistance = 0.0;
        for (var i = 0; i < PointCount; i++)
            meanDistance += D2(pa[i], pb[i]);
        var mean = meanDistance / PointCount / size;
        checks.Add(new HeartCheck("Mittl. Punktabstand/S", mean, ">=", MinMeanLandmarkDistance, mean >= MinMeanLandmarkDistance));

        var tipsIndex = D2(pa[8], pb[8]) / size;
        var tipsThumb = D2(pa[4], pb[4]) / size;
        checks.Add(new HeartCheck("Zeigefingerspitzen/S", tipsIndex, "<=", th.TipTouch, tipsIndex <= th.TipTouch));
        checks.Add(new HeartCheck("Daumenspitzen/S", tipsThumb, "<=", th.TipTouch, tipsThumb <= th.TipTouch));

        var (tx, ty) = Mid(pa[8], pb[8]);
        var (ux, uy) = Mid(pa[4], pb[4]);
        var vx = tx - ux;
        var vy = ty - uy;
        var heightAbs = Math.Sqrt(vx * vx + vy * vy);
        var height = heightAbs / size;
        checks.Add(new HeartCheck("Höhe/S (min)", height, ">=", th.MinHeight, height >= th.MinHeight));
        checks.Add(new HeartCheck("Höhe/S (max)", height, "<=", MaxHeightFactor, height <= MaxHeightFactor));

        var angleUp = Angle2(vx, vy, 0, -1);
        var angleInverted = Angle2(-vx, -vy, 0, -1);
        var tilt = Math.Min(angleUp, angleInverted);
        checks.Add(new HeartCheck("Neigung°", tilt, "<=", th.MaxTiltDeg, tilt <= th.MaxTiltDeg));
        var upright = angleUp <= th.MaxTiltDeg;
        var inverted = angleInverted <= th.MaxTiltDeg;

        // Handgelenke beidseits der Herzachse (bei Höhe 0 gibt es keine Achse: Seiten 0, die Prüfung scheitert).
        var sideA = heightAbs > 1e-12 ? Cross2(vx, vy, pa[0].X - ux, pa[0].Y - uy) / heightAbs : 0;
        var sideB = heightAbs > 1e-12 ? Cross2(vx, vy, pb[0].X - ux, pb[0].Y - uy) / heightAbs : 0;
        var differentSides = Math.Sign(sideA) != Math.Sign(sideB);
        checks.Add(new HeartCheck("Handgelenk-Seiten verschieden (Produkt/S²)", sideA * sideB / (size * size), "<", 0, differentSides));
        checks.Add(new HeartCheck("Handgelenk A Abstand zur Achse/S", Math.Abs(sideA) / size, ">=", MinWristSide, Math.Abs(sideA) >= MinWristSide * size));
        checks.Add(new HeartCheck("Handgelenk B Abstand zur Achse/S", Math.Abs(sideB) / size, ">=", MinWristSide, Math.Abs(sideB) >= MinWristSide * size));

        var openingA = D2(pa[8], pa[4]) / sA;
        var openingB = D2(pb[8], pb[4]) / sB;
        checks.Add(new HeartCheck("Öffnung A (Zeige-Daumen)/S_A", openingA, ">=", th.MinOpening, openingA >= th.MinOpening));
        checks.Add(new HeartCheck("Öffnung B (Zeige-Daumen)/S_B", openingB, ">=", th.MinOpening, openingB >= th.MinOpening));

        // Formkriterien (auch wenn eine Prüfung oben scheitert: so sieht man, was daraus würde).
        var roofA = RoofFingerCount(pa, th, out var extA);
        var roofB = RoofFingerCount(pb, th, out var extB);
        string shape;
        if (inverted)
            shape = "Umgedreht";
        else if (upright && roofA >= 2 && roofB >= 2)
            shape = "Dach";
        else if (upright)
            shape = "Klassisch";
        else
            shape = "keine (zu geneigt)";
        info += $", Neigung aufrecht={HeartDiagnosisFormat.Num(angleUp)}°/umgedreht={HeartDiagnosisFormat.Num(angleInverted)}°"
            + $", Dach-Finger A={HeartDiagnosisFormat.Int(roofA)}(gestreckt {HeartDiagnosisFormat.Int(extA)})/B={HeartDiagnosisFormat.Int(roofB)}(gestreckt {HeartDiagnosisFormat.Int(extB)}) (je >=2 für Dach)"
            + $", Form falls alles besteht={shape}";

        return new HeartDiagnosis("Paar", checks, verdict, info);
    }

    /// <summary>
    /// Alle Prüfgrößen von <see cref="ClassifyFingerHeart"/> für eine einzelne Hand samt Schwelle, auch nach der ersten nicht
    /// bestandenen.
    /// </summary>
    public static HeartDiagnosis DiagnoseFingerHeart(HandLandmarks hand, HeartSettings s, double aspect = 16.0 / 9)
    {
        ArgumentNullException.ThrowIfNull(hand);
        ArgumentNullException.ThrowIfNull(s);
        var verdict = ClassifyFingerHeart(hand, s, aspect);
        var checks = new List<HeartCheck>(10);
        if (hand.Points is not { Count: >= PointCount })
            return new HeartDiagnosis("Fingerherz", checks, verdict, "Unvollständige Handpunkte");
        if (!IsFinite(hand))
            return new HeartDiagnosis("Fingerherz", checks, verdict, "Ungültige Handpunkte");
        if (!(aspect > 0))
            aspect = 1;

        var th = Thresholds.For(s.SensitivityPercent);
        checks.Add(new HeartCheck("Score", hand.Score, ">=", s.MinHandScore, hand.Score >= s.MinHandScore));

        var p = ToIsotropic(hand, aspect);
        var size = D2(p[0], p[9]);
        var info = $"Seitenverh.={HeartDiagnosisFormat.Num(aspect)}, S={HeartDiagnosisFormat.Num(size)}";
        if (size < MinHandSize)
        {
            checks.Add(new HeartCheck("Handgröße", size, ">=", MinHandSize, false));
            return new HeartDiagnosis("Fingerherz", checks, verdict, info);
        }

        var pinch = Math.Min(D2(p[4], p[8]), D2(p[4], p[7])) / size;
        checks.Add(new HeartCheck("Daumen-Zeigefinger-Abstand/S", pinch, "<=", th.FingerHeartPinch, pinch <= th.FingerHeartPinch));

        var palm = Mean(p[0], p[5], p[9], p[13], p[17]);
        var indexToPalm = D2(p[8], palm) / size;
        checks.Add(new HeartCheck("Zeigefingerspitze-Handfläche/S", indexToPalm, ">=", FingerHeartPalmDistance, indexToPalm >= FingerHeartPalmDistance));
        var rootReach = D2(p[0], p[5]);
        var reach = rootReach > 1e-12 ? D2(p[0], p[8]) / rootReach : double.PositiveInfinity;
        checks.Add(new HeartCheck("Zeigefinger-Reichweite", reach, ">", FingerHeartIndexReach, reach > FingerHeartIndexReach));

        var scale3 = Dist(p[0], p[9]);
        var middleFolded = IsFolded(p, 9, palm, scale3);
        var ringFolded = IsFolded(p, 13, palm, scale3);
        var littleFolded = IsFolded(p, 17, palm, scale3);
        var foldedOk = th.T >= 0.5 ? middleFolded && (ringFolded || littleFolded) : middleFolded && ringFolded && littleFolded;
        var foldedCount = (middleFolded ? 1 : 0) + (ringFolded ? 1 : 0) + (littleFolded ? 1 : 0);
        checks.Add(new HeartCheck("Übrige Finger gebeugt (Anzahl)", foldedCount, ">=", th.T >= 0.5 ? 2 : 3, foldedOk));
        info += $", gebeugt Mittel={YesNo(middleFolded)}/Ring={YesNo(ringFolded)}/klein={YesNo(littleFolded)}";

        if (th.T < 0.5)
        {
            var axisX = p[8].X - p[5].X;
            var axisY = p[8].Y - p[5].Y;
            var tip = Cross2(axisX, axisY, p[4].X - p[5].X, p[4].Y - p[5].Y);
            var root = Cross2(axisX, axisY, p[2].X - p[5].X, p[2].Y - p[5].Y);
            checks.Add(new HeartCheck("Daumen kreuzt Zeigefinger (Produkt)", tip * root, "<", 0, Math.Sign(tip) != Math.Sign(root)));
        }
        else
        {
            info += ", Daumenkreuzung nicht erforderlich";
        }

        return new HeartDiagnosis("Fingerherz", checks, verdict, info);
    }

    /// <summary>
    /// Das „nächstliegende“ Paar unter den Händen: kleinste Summe aus Zeigefingerspitzen- und Daumenspitzen-Abstand relativ zur
    /// mittleren Handgröße. Berücksichtigt nur Hände mit gültigen Punkten (höchstens <see cref="MaxHands"/>); null, wenn es kein
    /// Paar gibt. Nur für die Diagnose.
    /// </summary>
    public static (int A, int B, double Cost)? FindNearestPair(IReadOnlyList<HandLandmarks> hands, double aspect = 16.0 / 9)
    {
        ArgumentNullException.ThrowIfNull(hands);
        if (!(aspect > 0))
            aspect = 1;
        var count = Math.Min(hands.Count, MaxHands);
        var iso = new V3[count][];
        for (var i = 0; i < count; i++)
        {
            var hand = hands[i];
            if (hand.Points is { Count: >= PointCount } && IsFinite(hand))
                iso[i] = ToIsotropic(hand, aspect);
        }

        (int A, int B, double Cost)? best = null;
        for (var i = 0; i < count; i++)
        {
            if (iso[i] is not { } pa)
                continue;
            for (var j = i + 1; j < count; j++)
            {
                if (iso[j] is not { } pb)
                    continue;
                var size = (D2(pa[0], pa[9]) + D2(pb[0], pb[9])) / 2;
                if (size < MinHandSize)
                    continue;
                var cost = (D2(pa[8], pb[8]) + D2(pa[4], pb[4])) / size;
                if (best is null || cost < best.Value.Cost)
                    best = (i, j, cost);
            }
        }
        return best;
    }

    private static string YesNo(bool value) => value ? "ja" : "nein";
}
