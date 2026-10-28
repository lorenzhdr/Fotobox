namespace Fotobox.Core.Peace;

/// <summary>
/// Lage eines Bartes aus den fünf Gesichtspunkten. Gerechnet wird in Bildpixeln, damit das
/// Seitenverhältnis stimmt; die Mitte wird wieder normiert (ungespiegelt) zurückgegeben.
/// <para>
/// Größe und Höhe kommen vom Augenabstand, der sich mit der Entfernung zur Kamera linear ändert und von Mimik kaum abhängt.
/// Die Mundwinkel von YuNet sind dagegen aus der Nähe, beim Lachen und durch die Perspektive (die Nasenspitze steht vor dem
/// Gesicht) unzuverlässig – breiter bzw. höher. Sie dürfen Breite und Höhe deshalb nur begrenzt verschieben.
/// </para>
/// </summary>
public static class MoustacheGeometry
{
    // Kalibrierung: Für ein neutrales Gesicht (Mundbreite ≈ 0,68 · Augenabstand, Nase → Mundmitte ≈ 0,59 · Augenabstand, wie die
    // Gesichter in den Tests) ergibt die Augenformel dieselbe Breite und Höhe wie die frühere Mundformel
    // (1,7 · Mundbreite ≈ 1,16 · Augenabstand; 0,55 · 0,59 ≈ 0,325 · Augenabstand).

    /// <summary>Bartbreite je Augenabstand.</summary>
    public const double EyeWidthFactor = 1.16;
    /// <summary>Abstand der Bartmitte unter der Nasenspitze je Augenabstand (entlang der Gesichts-Senkrechten).</summary>
    public const double EyeAnchorFactor = 0.325;

    /// <summary>Bartbreite, die die Mundwinkel nahelegen: Faktor auf die Mundbreite.</summary>
    public const double MouthWidthFactor = 1.7;
    /// <summary>Anteil des Wegs von der Nasenspitze zur Mundmitte, an dem die Mundwinkel die Bartmitte nahelegen.</summary>
    public const double NoseToMouthFactor = 0.55;

    /// <summary>Der Mundwert darf die Breite höchstens um diesen Anteil der Augenbreite verändern (±10 %).</summary>
    public const double MouthWidthTolerance = 0.10;
    /// <summary>Der Mundwert darf die Höhe höchstens um diesen Anteil des Augenabstands verschieben (±0,05 · Augenabstand).</summary>
    public const double MouthAnchorTolerance = 0.05;

    /// <summary>Gewicht der Augenlinie beim Winkel (die Mundlinie ist kürzer, verrauschter und kippt bei Mimik).</summary>
    public const double EyeAngleWeight = 2;
    /// <summary>Gewicht der Mundlinie beim Winkel.</summary>
    public const double MouthAngleWeight = 1;

    public static MoustachePose FromFace(FaceLandmarks f, double imageW, double imageH)
    {
        double W(double x) => x * imageW;
        double H(double y) => y * imageH;

        var noseX = W(f.Nose.X);
        var noseY = H(f.Nose.Y);
        var mouthX = (W(f.MouthRight.X) + W(f.MouthLeft.X)) / 2;
        var mouthY = (H(f.MouthRight.Y) + H(f.MouthLeft.Y)) / 2;

        // „Right“ = rechts der Person, im ungespiegelten Bild also links: Rechts → Links zeigt nach +x.
        var eyeDx = W(f.LeftEye.X) - W(f.RightEye.X);
        var eyeDy = H(f.LeftEye.Y) - H(f.RightEye.Y);
        var mouthDx = W(f.MouthLeft.X) - W(f.MouthRight.X);
        var mouthDy = H(f.MouthLeft.Y) - H(f.MouthRight.Y);
        // Vertauschte Bezeichnungen (Augen bzw. Mundwinkel in umgekehrter Reihenfolge): Die Gesichtssenkrechte (−eyeDy, eyeDx) muss
        // von der Nase zum Mund zeigen, sonst landete der Bart über der Nase und stünde auf dem Kopf; die Mundlinie folgt der Augenlinie.
        if ((mouthX - noseX) * -eyeDy + (mouthY - noseY) * eyeDx < 0)
        {
            eyeDx = -eyeDx;
            eyeDy = -eyeDy;
        }
        if (mouthDx * eyeDx + mouthDy * eyeDy < 0)
        {
            mouthDx = -mouthDx;
            mouthDy = -mouthDy;
        }
        var eyeDist = Math.Sqrt(eyeDx * eyeDx + eyeDy * eyeDy);
        var mouthWidth = Math.Sqrt(mouthDx * mouthDx + mouthDy * mouthDy);

        double cx, cy, width;
        if (eyeDist > 0)
        {
            // Breite: Augenwert, der Mundwert verschiebt sie höchstens um ±MouthWidthTolerance.
            var byEyes = EyeWidthFactor * eyeDist;
            width = Math.Clamp(MouthWidthFactor * mouthWidth, byEyes * (1 - MouthWidthTolerance), byEyes * (1 + MouthWidthTolerance));

            // Gesichtsachsen: u = Augenlinie (Rechts → Links), v = „nach unten“ rechtwinklig dazu. Bei Neigung dreht v mit, bei
            // Spiegelung des Bilds spiegelt es mit (aus den Augen berechnet, nicht aus festen Bildachsen).
            var ux = eyeDx / eyeDist;
            var uy = eyeDy / eyeDist;
            var vx = -uy;
            var vy = ux;

            // Seitlich: 55 % des Wegs von der Nase zur Mundmitte (bei gedrehtem Kopf liegt die Mundmitte näher an der Gesichtsmitte).
            // Hoch/runter: Augenwert, der Mundwert verschiebt ihn höchstens um ±MouthAnchorTolerance · Augenabstand.
            var dx = mouthX - noseX;
            var dy = mouthY - noseY;
            var lateral = NoseToMouthFactor * (dx * ux + dy * uy);
            var byEyesDown = EyeAnchorFactor * eyeDist;
            var byMouthDown = NoseToMouthFactor * (dx * vx + dy * vy);
            var tolerance = MouthAnchorTolerance * eyeDist;
            var down = Math.Clamp(byMouthDown, byEyesDown - tolerance, byEyesDown + tolerance);

            cx = noseX + lateral * ux + down * vx;
            cy = noseY + lateral * uy + down * vy;
        }
        else
        {
            // Ohne brauchbare Augenlinie: wie früher aus Nase und Mundwinkeln; ohne Mundbreite aus der Gesichtsbox.
            cx = noseX + NoseToMouthFactor * (mouthX - noseX);
            cy = noseY + NoseToMouthFactor * (mouthY - noseY);
            width = MouthWidthFactor * mouthWidth;
        }
        if (width <= 0)
            width = f.Box.Width * imageW * 0.5;

        // Winkel als gewichtetes Mittel der Einheitsvektoren von Augen- und Mundlinie (robust gegen ±π-Sprünge).
        double ax = 0, ay = 0;
        if (eyeDist > 0) { ax += EyeAngleWeight * eyeDx / eyeDist; ay += EyeAngleWeight * eyeDy / eyeDist; }
        if (mouthWidth > 0) { ax += MouthAngleWeight * mouthDx / mouthWidth; ay += MouthAngleWeight * mouthDy / mouthWidth; }
        var angle = ax == 0 && ay == 0 ? 0 : Math.Atan2(ay, ax);

        var center = imageW > 0 && imageH > 0 ? new NormPoint(cx / imageW, cy / imageH) : f.Nose;
        return new MoustachePose(center, width, angle);
    }
}
