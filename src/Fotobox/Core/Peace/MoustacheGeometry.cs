namespace Fotobox.Core.Peace;

/// <summary>
/// Lage eines Bartes aus den fünf Gesichtspunkten. Gerechnet wird in Bildpixeln, damit das
/// Seitenverhältnis stimmt; die Mitte wird wieder normiert (ungespiegelt) zurückgegeben.
/// </summary>
public static class MoustacheGeometry
{
    /// <summary>Anteil des Wegs von der Nasenspitze zur Mundmitte, an dem die Bartmitte sitzt.</summary>
    public const double NoseToMouthFactor = 0.55;
    public const double EyeDistanceFactor = 1.0;
    public const double MouthWidthFactor = 1.7;

    public static MoustachePose FromFace(FaceLandmarks f, double imageW, double imageH)
    {
        double W(double x) => x * imageW;
        double H(double y) => y * imageH;

        var noseX = W(f.Nose.X);
        var noseY = H(f.Nose.Y);
        var mouthX = (W(f.MouthRight.X) + W(f.MouthLeft.X)) / 2;
        var mouthY = (H(f.MouthRight.Y) + H(f.MouthLeft.Y)) / 2;
        var cx = noseX + NoseToMouthFactor * (mouthX - noseX);
        var cy = noseY + NoseToMouthFactor * (mouthY - noseY);

        // „Right“ = rechts der Person, im ungespiegelten Bild also links: Rechts → Links zeigt nach +x.
        var eyeDx = W(f.LeftEye.X) - W(f.RightEye.X);
        var eyeDy = H(f.LeftEye.Y) - H(f.RightEye.Y);
        var mouthDx = W(f.MouthLeft.X) - W(f.MouthRight.X);
        var mouthDy = H(f.MouthLeft.Y) - H(f.MouthRight.Y);
        var eyeDist = Math.Sqrt(eyeDx * eyeDx + eyeDy * eyeDy);
        var mouthWidth = Math.Sqrt(mouthDx * mouthDx + mouthDy * mouthDy);

        var width = Math.Max(EyeDistanceFactor * eyeDist, MouthWidthFactor * mouthWidth);
        if (width <= 0)
            width = f.Box.Width * imageW * 0.5;

        // Winkel als Mittel der Einheitsvektoren von Augen- und Mundlinie (robust gegen ±π-Sprünge).
        double ux = 0, uy = 0;
        if (eyeDist > 0) { ux += eyeDx / eyeDist; uy += eyeDy / eyeDist; }
        if (mouthWidth > 0) { ux += mouthDx / mouthWidth; uy += mouthDy / mouthWidth; }
        var angle = ux == 0 && uy == 0 ? 0 : Math.Atan2(uy, ux);

        var center = imageW > 0 && imageH > 0 ? new NormPoint(cx / imageW, cy / imageH) : f.Nose;
        return new MoustachePose(center, width, angle);
    }
}
