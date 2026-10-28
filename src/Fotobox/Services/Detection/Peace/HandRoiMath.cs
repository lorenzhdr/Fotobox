using Fotobox.Core.Peace;

namespace Fotobox.Services.Detection.Peace;

/// <summary>
/// Ausschnitte für die Handpunkte (reine Mathematik). Winkel im Bogenmaß, positiv = im Uhrzeigersinn gedreht (y nach unten);
/// 0 = Hand zeigt mit den Fingern nach oben. Alles in Bildpixeln gerechnet, damit Drehung und Größe isotrop bleiben.
/// </summary>
internal static class HandRoiMath
{
    public const double PalmScale = 2.9;
    public const double PalmShift = 0.5;
    public const double LandmarkScale = 2.0;
    public const double LandmarkShift = 0.1;
    public const double MinRoiPx = 16;

    /// <summary>Handgelenk → Mittelfingerwurzel: Winkel gegenüber „nach oben“ (wie MediaPipe/PINTO), normiert auf (−π, π].</summary>
    public static double AngleFromWristToMiddle(double wristX, double wristY, double middleX, double middleY)
    {
        var dx = middleX - wristX;
        var dy = middleY - wristY;
        return NormalizeRadians(Math.PI / 2 - Math.Atan2(-dy, dx));
    }

    public static double NormalizeRadians(double angle) => angle - 2 * Math.PI * Math.Floor((angle + Math.PI) / (2 * Math.PI));

    /// <summary>
    /// Ausschnitt aus einer Handfläche: Größe 2,9 · Palm-Größe, Mitte um 0,5 · Palm-Größe in Richtung der Finger verschoben.
    /// <see cref="PalmCandidate.Size"/> ist relativ zur Bildbreite.
    /// </summary>
    public static HandRoi FromPalm(PalmCandidate palm, int imageW, int imageH)
    {
        ArgumentNullException.ThrowIfNull(palm);
        var angle = AngleFromWristToMiddle(palm.Kp0.X * imageW, palm.Kp0.Y * imageH, palm.Kp2.X * imageW, palm.Kp2.Y * imageH);
        var palmPx = palm.Size * imageW;
        var cx = palm.Center.X * imageW + PalmShift * palmPx * Math.Sin(angle);
        var cy = palm.Center.Y * imageH - PalmShift * palmPx * Math.Cos(angle);
        return new HandRoi(new NormPoint(cx / imageW, cy / imageH), Math.Max(MinRoiPx, palmPx * PalmScale), angle);
    }

    /// <summary>
    /// Ausschnitt für das Nachführen aus den letzten Handpunkten (wie MediaPipe): Drehung Handgelenk(0) → Mittelfinger-MCP(9),
    /// Größe 2,0 · längste Ausdehnung der Punkte im gedrehten Rahmen, Mitte leicht zu den Fingern verschoben.
    /// </summary>
    public static HandRoi FromLandmarks(HandLandmarks hand, int imageW, int imageH)
    {
        ArgumentNullException.ThrowIfNull(hand);
        if (hand.Points.Count < 21)
            throw new ArgumentException("Eine Hand braucht 21 Punkte.", nameof(hand));
        var wx = hand.Points[0].X * imageW;
        var wy = hand.Points[0].Y * imageH;
        var angle = AngleFromWristToMiddle(wx, wy, hand.Points[9].X * imageW, hand.Points[9].Y * imageH);
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);

        double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
        foreach (var p in hand.Points)
        {
            var dx = p.X * imageW - wx;
            var dy = p.Y * imageH - wy;
            var u = dx * cos + dy * sin;
            var v = -dx * sin + dy * cos;
            minU = Math.Min(minU, u);
            maxU = Math.Max(maxU, u);
            minV = Math.Min(minV, v);
            maxV = Math.Max(maxV, v);
        }

        var extent = Math.Max(maxU - minU, maxV - minV);
        var cu = (minU + maxU) / 2;
        var cv = (minV + maxV) / 2 - LandmarkShift * extent;
        var cx = wx + cu * cos - cv * sin;
        var cy = wy + cu * sin + cv * cos;
        return new HandRoi(new NormPoint(cx / imageW, cy / imageH), Math.Max(MinRoiPx, extent * LandmarkScale), angle);
    }
}
