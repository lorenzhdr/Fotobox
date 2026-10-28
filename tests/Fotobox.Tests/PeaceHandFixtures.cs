using Fotobox.Core.Fun;
using Fotobox.Core.Peace;

namespace Fotobox.Tests;

/// <summary>Zeigegesten, die <see cref="PeaceHandFixtures"/> erzeugen kann.</summary>
public enum HandPose { Peace, Fist, Open, IndexOnly, ThreeFingers }

/// <summary>
/// Synthetische 21-Punkt-Hände (MediaPipe-Reihenfolge). Gebaut wird isotrop (Einheit = Bildhöhe, Hand zeigt nach oben, Handgelenk im
/// Ursprung, |P0–P9| = 0,2) und dann gedreht, gespiegelt, verschoben und auf Bildbreite/-höhe normiert.
/// </summary>
internal static class PeaceHandFixtures
{
    private const double Scale = 0.2;

    public static HandLandmarks Hand(HandPose pose, double rotationDeg = 0, bool mirror = false, bool thumbOut = false,
        double aspect = 16.0 / 9, double score = 0.95, double cx = 0.5, double cy = 0.7, double indexBendDeg = 0)
    {
        var local = new (double X, double Y)[21];
        local[0] = (0, 0);

        // Daumen (1–4): angelegt oder abgespreizt – für die Geste egal.
        local[1] = (-0.04, -0.04);
        local[2] = (-0.08, -0.08);
        if (thumbOut)
        {
            local[3] = (-0.15, -0.11);
            local[4] = (-0.21, -0.12);
        }
        else
        {
            local[3] = (-0.09, -0.12);
            local[4] = (-0.08, -0.15);
        }

        var index = pose is HandPose.Peace or HandPose.Open or HandPose.IndexOnly or HandPose.ThreeFingers;
        var middle = pose is HandPose.Peace or HandPose.Open or HandPose.ThreeFingers;
        var ring = pose is HandPose.Open or HandPose.ThreeFingers;
        var little = pose is HandPose.Open;

        Finger(local, 5, (-0.06, -0.19), -12, index, indexBendDeg);
        Finger(local, 9, (0.0, -0.20), 8, middle, 0);
        Finger(local, 13, (0.055, -0.19), 14, ring, 0);
        Finger(local, 17, (0.10, -0.16), 20, little, 0);

        var rot = rotationDeg * Math.PI / 180;
        var cos = Math.Cos(rot);
        var sin = Math.Sin(rot);
        var points = new HandPoint[21];
        for (var i = 0; i < 21; i++)
        {
            var (u, v) = local[i];
            if (mirror)
                u = -u;
            var ru = u * cos - v * sin;
            var rv = u * sin + v * cos;
            points[i] = new HandPoint((cx * aspect + ru) / aspect, cy + rv, 0);
        }

        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        return new HandLandmarks(points, score, new NormRect(minX, minY, maxX - minX, maxY - minY));
    }

    // Finger: Wurzel, Neigung nach außen (Grad), gestreckt oder gebeugt (Spitze zurück zur Handfläche).
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

        var tilt = tiltDeg * Math.PI / 180;
        (double X, double Y) Dir(double extra) => (Math.Sin(tilt + extra), -Math.Cos(tilt + extra));
        var d = Dir(0);
        var bent = Dir(pipBendDeg * Math.PI / 180);
        p[mcp + 1] = (root.X + 0.07 * d.X, root.Y + 0.07 * d.Y);
        p[mcp + 2] = (p[mcp + 1].X + 0.045 * bent.X, p[mcp + 1].Y + 0.045 * bent.Y);
        p[mcp + 3] = (p[mcp + 2].X + 0.04 * bent.X, p[mcp + 2].Y + 0.04 * bent.Y);
    }

    public static FaceLandmarks Face(double centerX = 0.3, double centerY = 0.25, double width = 0.12)
    {
        var height = width * 16.0 / 9 * 1.3; // grob quadratisch in Pixeln
        var box = new NormRect(centerX - width / 2, centerY - height / 2, width, height);
        var eyeDx = width * 0.22;
        return new FaceLandmarks(box, 0.9,
            RightEye: new NormPoint(centerX - eyeDx, centerY - height * 0.1),
            LeftEye: new NormPoint(centerX + eyeDx, centerY - height * 0.1),
            Nose: new NormPoint(centerX, centerY + height * 0.05),
            MouthRight: new NormPoint(centerX - width * 0.15, centerY + height * 0.25),
            MouthLeft: new NormPoint(centerX + width * 0.15, centerY + height * 0.25));
    }
}
