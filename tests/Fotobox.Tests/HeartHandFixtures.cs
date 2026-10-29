using Fotobox.Core.Fun;
using Fotobox.Core.Peace;

namespace Fotobox.Tests;

/// <summary>Wie die übrigen Finger (Mittel, Ring, klein) in einem Zwei-Hände-Herz stehen.</summary>
public enum OtherFingers { Folded, Curved, Extended, OneExtended }

/// <summary>Posen des Ein-Hand-Herzens, die <see cref="HeartHandFixtures.FingerHeart"/> erzeugen kann.</summary>
public enum FingerHeartPose
{
    /// <summary>Daumen kreuzt den Zeigefinger und liegt an dessen DIP, übrige Finger gebeugt.</summary>
    Crossed,
    /// <summary>Pinzettengriff: Daumenspitze an der Zeigefingerspitze (gleiche Seite), übrige Finger gebeugt.</summary>
    Pinch,
    /// <summary>OK-Zeichen: wie <see cref="Pinch"/>, aber Mittel-, Ring- und kleiner Finger gestreckt.</summary>
    OkSign,
    /// <summary>Daumen und Zeigefinger deutlich getrennt, übrige Finger gebeugt.</summary>
    PinchOpen,
}

/// <summary>
/// Synthetische 21-Punkt-Hände für die Herzerkennung (MediaPipe-Reihenfolge, Z = 0). Gebaut wird isotrop (Einheit = Bildhöhe,
/// Handgröße |P0–P9| = 0,2 · Skalierung) und dann auf Bildbreite/-höhe normiert. Beim Zwei-Hände-Herz wird eine lokale Hand
/// (Daumen abgespreizt, Zeigefinger gebogen) so gedreht und verschoben, dass Daumen- und Zeigefingerspitze auf der senkrechten Herzachse
/// liegen; die zweite Hand ist ihr Spiegelbild. Danach folgen Skalierung, Drehung um die Herzmitte und Verschiebung.
/// </summary>
internal static class HeartHandFixtures
{
    private const double Deg = Math.PI / 180;

    // ---- Zwei-Hände-Herz

    /// <summary>
    /// Zwei Hände, die ein Herz bilden (nach Drehung 0° senkrecht, Zeigefingerspitzen oben). <paramref name="rotationDeg"/> dreht im
    /// Uhrzeigersinn (180° = umgedreht), <paramref name="tipGap"/> verschiebt Hand B in X (Bildhöhen-Einheiten) von A weg,
    /// <paramref name="scaleB"/> vergrößert Hand B um den Daumenkontaktpunkt.
    /// </summary>
    public static IReadOnlyList<HandLandmarks> Pair(OtherFingers others = OtherFingers.Curved, double rotationDeg = 0, double scale = 1,
        double cx = 0.5, double cy = 0.5, double aspect = 16.0 / 9, double scoreA = 0.95, double scoreB = 0.95, double tipGap = 0, double scaleB = 1)
    {
        var a = Align(LocalHand(others, mirror: false));
        var b = Align(LocalHand(others, mirror: true));
        var contact = (X: 0.0, Y: HeartHeight / 2);
        b = b.Select(p => (X: contact.X + (p.X - contact.X) * scaleB + tipGap, Y: contact.Y + (p.Y - contact.Y) * scaleB)).ToArray();
        return [Place(a, scale, rotationDeg, cx, cy, aspect, scoreA), Place(b, scale, rotationDeg, cx, cy, aspect, scoreB)];
    }

    public static IReadOnlyList<HandLandmarks> Classic(double rotationDeg = 0, double scale = 1, double cx = 0.5, double cy = 0.5, double aspect = 16.0 / 9,
        double score = 0.95) => Pair(OtherFingers.Curved, rotationDeg, scale, cx, cy, aspect, score, score);

    public static IReadOnlyList<HandLandmarks> Roof(double rotationDeg = 0, double scale = 1, double cx = 0.5, double cy = 0.5, double aspect = 16.0 / 9,
        double score = 0.95) => Pair(OtherFingers.Extended, rotationDeg, scale, cx, cy, aspect, score, score);

    public static IReadOnlyList<HandLandmarks> Inverted(double rotationDeg = 0, double scale = 1, double cx = 0.5, double cy = 0.5, double aspect = 16.0 / 9,
        double score = 0.95) => Pair(OtherFingers.Curved, 180 + rotationDeg, scale, cx, cy, aspect, score, score);

    // ---- Ein-Hand-Herz

    public static HandLandmarks FingerHeart(FingerHeartPose pose, double rotationDeg = 0, double scale = 1, double cx = 0.5, double cy = 0.7,
        double aspect = 16.0 / 9, double score = 0.95, bool ringExtended = false)
    {
        var local = new (double X, double Y)[21];
        local[0] = (0, 0);
        local[1] = (-0.04, -0.04);
        local[2] = (-0.08, -0.08);
        local[5] = (-0.06, -0.19);
        local[9] = (0, -0.20);
        local[13] = (0.055, -0.19);
        local[17] = (0.10, -0.16);

        var others = pose == FingerHeartPose.OkSign ? OtherFingers.Extended : OtherFingers.Folded;
        Finger(local, 9, (0, -0.20), 8, others == OtherFingers.Extended, 0);
        Finger(local, 13, (0.055, -0.19), 14, others == OtherFingers.Extended || ringExtended, 0);
        Finger(local, 17, (0.10, -0.16), 20, others == OtherFingers.Extended, 0);

        switch (pose)
        {
            case FingerHeartPose.Crossed:
                // Zeigefinger gestreckt; Daumen von links über den Finger, Spitze rechts der Fingerachse am DIP.
                Finger(local, 5, (-0.06, -0.19), -12, true, 0);
                local[3] = (-0.11, -0.19);
                local[4] = (-0.065, -0.295);
                break;
            case FingerHeartPose.Pinch:
            case FingerHeartPose.OkSign:
                // Zeigefinger leicht gebeugt, Daumenspitze links daneben an der Zeigefingerspitze.
                local[6] = (-0.075, -0.26);
                local[7] = (-0.085, -0.30);
                local[8] = (-0.10, -0.33);
                local[3] = (-0.13, -0.20);
                local[4] = (-0.13, -0.31);
                break;
            default:
                Finger(local, 5, (-0.06, -0.19), -12, true, 0);
                local[3] = (-0.15, -0.14);
                local[4] = (-0.24, -0.16);
                break;
        }

        return Place(local, scale, rotationDeg, cx, cy, aspect, score);
    }

    // ---- Hilfen

    /// <summary>Abstand der Daumen- und Zeigefingerspitze der lokalen Hand = Herzhöhe bei Skalierung 1.</summary>
    private static readonly double HeartHeight = Dist(LocalHand(OtherFingers.Curved, false)[8], LocalHand(OtherFingers.Curved, false)[4]);

    private static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // Lokale Hand (Handgelenk im Ursprung, Finger nach oben, Daumen links abgespreizt, Zeigefinger zur Spitzenseite gebogen).
    private static (double X, double Y)[] LocalHand(OtherFingers others, bool mirror)
    {
        var p = new (double X, double Y)[21];
        p[0] = (0, 0);
        p[1] = (-0.04, -0.04);
        p[2] = (-0.09, -0.09);
        p[3] = (-0.15, -0.12);
        p[4] = (-0.21, -0.12);

        Finger(p, 5, (-0.06, -0.19), -12, true, 35);
        Curl(p, 9, (0, -0.20), 8, others);
        Curl(p, 13, (0.055, -0.19), 14, others);
        Curl(p, 17, (0.10, -0.16), 20, others);

        // OneExtended: nur der Mittelfinger gestreckt, die anderen beiden gekrümmt.
        if (others == OtherFingers.OneExtended)
        {
            Finger(p, 9, (0, -0.20), 8, true, 0);
            Curl(p, 13, (0.055, -0.19), 14, OtherFingers.Curved);
            Curl(p, 17, (0.10, -0.16), 20, OtherFingers.Curved);
        }

        if (mirror)
        {
            for (var i = 0; i < p.Length; i++)
                p[i] = (-p[i].X, p[i].Y);
        }
        return p;
    }

    private static void Curl((double X, double Y)[] p, int mcp, (double X, double Y) root, double tiltDeg, OtherFingers kind)
    {
        switch (kind)
        {
            case OtherFingers.Extended:
                Finger(p, mcp, root, tiltDeg, true, 0);
                break;
            case OtherFingers.Curved:
                // Locker gekrümmt: Spitze zurück Richtung Handfläche, PIP-Winkel weit offen.
                p[mcp] = root;
                p[mcp + 1] = (root.X + 0.01, root.Y - 0.06);
                p[mcp + 2] = (root.X + 0.03, root.Y - 0.08);
                p[mcp + 3] = (root.X + 0.05, root.Y - 0.045);
                break;
            default:
                Finger(p, mcp, root, tiltDeg, false, 0);
                break;
        }
    }

    // Finger: Wurzel, Neigung nach außen (Grad), gestreckt oder gebeugt (Spitze zurück zur Handfläche), optionaler Knick ab PIP.
    private static void Finger((double X, double Y)[] p, int mcp, (double X, double Y) root, double tiltDeg, bool extended, double pipBendDeg)
    {
        p[mcp] = root;
        if (!extended)
        {
            p[mcp + 1] = (root.X, root.Y - 0.05);
            p[mcp + 2] = (root.X, root.Y - 0.03);
            p[mcp + 3] = (root.X, root.Y + 0.02);
            return;
        }

        (double X, double Y) Dir(double deg) => (Math.Sin(deg * Deg), -Math.Cos(deg * Deg));
        var d = Dir(tiltDeg);
        var bent = Dir(tiltDeg + pipBendDeg);
        p[mcp + 1] = (root.X + 0.07 * d.X, root.Y + 0.07 * d.Y);
        p[mcp + 2] = (p[mcp + 1].X + 0.045 * bent.X, p[mcp + 1].Y + 0.045 * bent.Y);
        p[mcp + 3] = (p[mcp + 2].X + 0.04 * bent.X, p[mcp + 2].Y + 0.04 * bent.Y);
    }

    // Dreht und verschiebt die lokale Hand so, dass die Daumenspitze auf (0, h/2) und die Zeigefingerspitze auf (0, −h/2) liegt.
    private static (double X, double Y)[] Align((double X, double Y)[] local)
    {
        var vx = local[8].X - local[4].X;
        var vy = local[8].Y - local[4].Y;
        var a = -Math.Atan2(vx, -vy); // Drehwinkel (Uhrzeigersinn positiv, Y nach unten), der den Vektor senkrecht nach oben stellt
        var cos = Math.Cos(a);
        var sin = Math.Sin(a);
        var rotated = local.Select(q => (X: q.X * cos - q.Y * sin, Y: q.X * sin + q.Y * cos)).ToArray();
        var dx = -rotated[4].X;
        var dy = HeartHeight / 2 - rotated[4].Y;
        return rotated.Select(q => (X: q.X + dx, Y: q.Y + dy)).ToArray();
    }

    // Skalierung und Drehung um den Ursprung, Verschiebung auf (cx, cy) und Normierung auf Bildbreite/-höhe.
    private static HandLandmarks Place((double X, double Y)[] iso, double scale, double rotationDeg, double cx, double cy, double aspect, double score)
    {
        var rot = rotationDeg * Deg;
        var cos = Math.Cos(rot);
        var sin = Math.Sin(rot);
        var points = new HandPoint[21];
        for (var i = 0; i < 21; i++)
        {
            var x = iso[i].X * scale;
            var y = iso[i].Y * scale;
            var rx = x * cos - y * sin;
            var ry = x * sin + y * cos;
            points[i] = new HandPoint((cx * aspect + rx) / aspect, cy + ry, 0);
        }

        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        return new HandLandmarks(points, score, new NormRect(minX, minY, maxX - minX, maxY - minY));
    }

    // ---- Negativposen (nutzen die Peace-Fixtures)

    /// <summary>Zwei Fäuste nebeneinander, Handgelenke knapp über der Mindestentfernung, Zeigefinger- und Daumenspitzen berühren sich.</summary>
    public static IReadOnlyList<HandLandmarks> TwoFists(double aspect = 16.0 / 9) =>
    [
        PeaceHandFixtures.Hand(HandPose.Fist, mirror: false, aspect: aspect, cx: 0.5 + 0.085 / aspect, cy: 0.6),
        PeaceHandFixtures.Hand(HandPose.Fist, mirror: true, aspect: aspect, cx: 0.5 - 0.085 / aspect, cy: 0.6),
    ];

    /// <summary>Zwei offene Hände nebeneinander, Finger nach oben, Handgelenke 0,3 auseinander.</summary>
    public static IReadOnlyList<HandLandmarks> TwoOpenHands(double aspect = 16.0 / 9) =>
    [
        PeaceHandFixtures.Hand(HandPose.Open, aspect: aspect, cx: 0.5 - 0.15 / aspect, cy: 0.7),
        PeaceHandFixtures.Hand(HandPose.Open, mirror: true, aspect: aspect, cx: 0.5 + 0.15 / aspect, cy: 0.7),
    ];

    /// <summary>Gebetshände: Handflächen aneinander, Finger nach oben, Zeigefinger und Daumen berühren sich.</summary>
    public static IReadOnlyList<HandLandmarks> PrayerHands(double aspect = 16.0 / 9) =>
    [
        PeaceHandFixtures.Hand(HandPose.Open, mirror: true, aspect: aspect, cx: 0.5 - 0.05 / aspect, cy: 0.7),
        PeaceHandFixtures.Hand(HandPose.Open, mirror: false, aspect: aspect, cx: 0.5 + 0.05 / aspect, cy: 0.7),
    ];

    /// <summary>Zwei Peace-Hände, nach innen geneigt, so dass sich die Zeigefingerspitzen oben berühren.</summary>
    public static IReadOnlyList<HandLandmarks> TwoPeaceHands(double aspect = 16.0 / 9) =>
    [
        PeaceHandFixtures.Hand(HandPose.Peace, rotationDeg: 22, aspect: aspect, cx: 0.5 - 0.15 / aspect, cy: 0.7),
        PeaceHandFixtures.Hand(HandPose.Peace, rotationDeg: -22, mirror: true, aspect: aspect, cx: 0.5 + 0.15 / aspect, cy: 0.7),
    ];
}
