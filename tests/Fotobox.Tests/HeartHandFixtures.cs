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

    // ---- Echte Aufnahmen
    // Gemessene Handpunkte (normiert, ungespiegeltes Bild, Seitenverhältnis RealAspect, 21 Punkte je Hand als x, y im Wechsel, Z = 0,
    // Score 0,98), je eine Person mit beiden Händen vor der Kamera. RealHeart1…5 sind Herzen mit stark gekrümmten Zeigefingern (oben
    // berühren sich die Fingerrücken, nicht die Spitzen); RealNonHeart1…5 sind Haltungen derselben Aufnahmen, die kein Herz sind.

    /// <summary>Seitenverhältnis der echten Aufnahmen.</summary>
    public const double RealAspect = 1.778;

    /// <summary>Echtes Herz Nr. <paramref name="number"/> (1…5).</summary>
    public static IReadOnlyList<HandLandmarks> RealHeart(int number) => number switch
    {
        1 => RealHeart1,
        2 => RealHeart2,
        3 => RealHeart3,
        4 => RealHeart4,
        5 => RealHeart5,
        _ => throw new ArgumentOutOfRangeException(nameof(number)),
    };

    /// <summary>Echte Aufnahme ohne Herz Nr. <paramref name="number"/> (1…5).</summary>
    public static IReadOnlyList<HandLandmarks> RealNonHeart(int number) => number switch
    {
        1 => RealNonHeart1,
        2 => RealNonHeart2,
        3 => RealNonHeart3,
        4 => RealNonHeart4,
        5 => RealNonHeart5,
        _ => throw new ArgumentOutOfRangeException(nameof(number)),
    };

    public static IReadOnlyList<HandLandmarks> RealHeart1 => RealPair(
        [
            0.629, 0.513, 0.597, 0.521, 0.571, 0.522, 0.549, 0.539, 0.529, 0.551, 0.566, 0.435,
            0.540, 0.435, 0.528, 0.461, 0.525, 0.488, 0.572, 0.417, 0.553, 0.365, 0.537, 0.332,
            0.524, 0.303, 0.583, 0.416, 0.572, 0.356, 0.562, 0.313, 0.552, 0.274, 0.596, 0.428,
            0.593, 0.381, 0.588, 0.352, 0.582, 0.322
        ],
        [
            0.425, 0.519, 0.457, 0.532, 0.484, 0.532, 0.507, 0.546, 0.526, 0.571, 0.487, 0.446,
            0.514, 0.433, 0.523, 0.454, 0.524, 0.475, 0.480, 0.424, 0.505, 0.371, 0.522, 0.340,
            0.535, 0.315, 0.468, 0.417, 0.484, 0.360, 0.496, 0.321, 0.508, 0.287, 0.452, 0.421,
            0.460, 0.373, 0.468, 0.341, 0.478, 0.313
        ]);

    public static IReadOnlyList<HandLandmarks> RealHeart2 => RealPair(
        [
            0.487, 0.535, 0.510, 0.540, 0.528, 0.537, 0.544, 0.545, 0.559, 0.556, 0.531, 0.476,
            0.549, 0.469, 0.555, 0.491, 0.555, 0.513, 0.529, 0.465, 0.544, 0.425, 0.558, 0.399,
            0.569, 0.373, 0.523, 0.465, 0.534, 0.424, 0.543, 0.396, 0.551, 0.370, 0.515, 0.475,
            0.520, 0.443, 0.525, 0.419, 0.532, 0.397
        ],
        [
            0.636, 0.535, 0.615, 0.539, 0.596, 0.533, 0.580, 0.540, 0.565, 0.551, 0.592, 0.473,
            0.573, 0.458, 0.566, 0.484, 0.564, 0.509, 0.598, 0.466, 0.585, 0.421, 0.575, 0.395,
            0.567, 0.369, 0.607, 0.472, 0.601, 0.429, 0.594, 0.401, 0.588, 0.376, 0.617, 0.488,
            0.617, 0.459, 0.615, 0.440, 0.611, 0.420
        ]);

    public static IReadOnlyList<HandLandmarks> RealHeart3 => RealPair(
        [
            0.622, 0.466, 0.591, 0.471, 0.564, 0.467, 0.542, 0.474, 0.523, 0.478, 0.561, 0.383,
            0.535, 0.376, 0.522, 0.403, 0.518, 0.429, 0.567, 0.369, 0.547, 0.307, 0.531, 0.265,
            0.519, 0.226, 0.577, 0.371, 0.565, 0.306, 0.555, 0.259, 0.546, 0.215, 0.589, 0.386,
            0.584, 0.335, 0.579, 0.301, 0.573, 0.268
        ],
        [
            0.405, 0.459, 0.455, 0.459, 0.484, 0.451, 0.505, 0.462, 0.518, 0.472, 0.507, 0.335,
            0.545, 0.334, 0.558, 0.372, 0.553, 0.404, 0.494, 0.331, 0.530, 0.274, 0.551, 0.273,
            0.555, 0.280, 0.476, 0.342, 0.503, 0.279, 0.522, 0.260, 0.529, 0.246, 0.455, 0.367,
            0.472, 0.314, 0.486, 0.283, 0.497, 0.257
        ]);

    public static IReadOnlyList<HandLandmarks> RealHeart4 => RealPair(
        [
            0.397, 0.476, 0.448, 0.470, 0.474, 0.463, 0.494, 0.481, 0.513, 0.500, 0.474, 0.346,
            0.511, 0.332, 0.529, 0.355, 0.537, 0.374, 0.462, 0.341, 0.484, 0.276, 0.501, 0.258,
            0.513, 0.243, 0.449, 0.356, 0.460, 0.294, 0.472, 0.261, 0.484, 0.232, 0.440, 0.390,
            0.445, 0.339, 0.452, 0.307, 0.460, 0.283
        ],
        [
            0.654, 0.485, 0.611, 0.481, 0.577, 0.477, 0.546, 0.494, 0.526, 0.505, 0.580, 0.381,
            0.549, 0.380, 0.538, 0.420, 0.536, 0.460, 0.592, 0.364, 0.568, 0.289, 0.553, 0.253,
            0.545, 0.229, 0.605, 0.366, 0.595, 0.293, 0.590, 0.252, 0.585, 0.219, 0.618, 0.384,
            0.617, 0.328, 0.616, 0.296, 0.614, 0.268
        ]);

    public static IReadOnlyList<HandLandmarks> RealHeart5 => RealPair(
        [
            0.416, 0.484, 0.447, 0.492, 0.470, 0.486, 0.491, 0.488, 0.508, 0.496, 0.473, 0.396,
            0.493, 0.375, 0.503, 0.385, 0.506, 0.401, 0.464, 0.377, 0.481, 0.317, 0.493, 0.281,
            0.503, 0.250, 0.453, 0.375, 0.462, 0.314, 0.470, 0.276, 0.477, 0.242, 0.440, 0.384,
            0.443, 0.335, 0.448, 0.302, 0.455, 0.274
        ],
        [
            0.627, 0.491, 0.596, 0.490, 0.569, 0.481, 0.545, 0.482, 0.525, 0.482, 0.575, 0.394,
            0.549, 0.379, 0.534, 0.397, 0.527, 0.422, 0.585, 0.377, 0.569, 0.315, 0.556, 0.274,
            0.547, 0.239, 0.596, 0.378, 0.591, 0.314, 0.586, 0.271, 0.582, 0.230, 0.609, 0.391,
            0.611, 0.342, 0.610, 0.309, 0.608, 0.277
        ]);

    public static IReadOnlyList<HandLandmarks> RealNonHeart1 => RealPair(
        [
            0.445, 0.628, 0.411, 0.594, 0.385, 0.541, 0.375, 0.487, 0.377, 0.443, 0.414, 0.527,
            0.421, 0.453, 0.423, 0.471, 0.423, 0.493, 0.443, 0.530, 0.447, 0.430, 0.447, 0.369,
            0.448, 0.320, 0.466, 0.538, 0.461, 0.464, 0.454, 0.481, 0.453, 0.503, 0.485, 0.547,
            0.477, 0.489, 0.470, 0.502, 0.469, 0.522
        ],
        [
            0.349, 0.781, 0.333, 0.807, 0.314, 0.809, 0.295, 0.804, 0.284, 0.799, 0.304, 0.795,
            0.267, 0.806, 0.247, 0.820, 0.231, 0.834, 0.303, 0.779, 0.264, 0.794, 0.242, 0.809,
            0.226, 0.826, 0.304, 0.768, 0.269, 0.780, 0.250, 0.794, 0.236, 0.811, 0.306, 0.762,
            0.279, 0.771, 0.267, 0.784, 0.257, 0.798
        ]);

    public static IReadOnlyList<HandLandmarks> RealNonHeart2 => RealPair(
        [
            0.418, 0.521, 0.452, 0.521, 0.477, 0.513, 0.497, 0.526, 0.513, 0.548, 0.482, 0.442,
            0.503, 0.415, 0.513, 0.408, 0.519, 0.404, 0.471, 0.439, 0.491, 0.392, 0.501, 0.372,
            0.508, 0.355, 0.458, 0.447, 0.472, 0.399, 0.481, 0.371, 0.489, 0.348, 0.444, 0.466,
            0.451, 0.429, 0.458, 0.406, 0.467, 0.387
        ],
        [
            0.574, 0.527, 0.537, 0.502, 0.511, 0.475, 0.497, 0.454, 0.486, 0.433, 0.478, 0.466,
            0.444, 0.490, 0.422, 0.498, 0.408, 0.505, 0.489, 0.455, 0.469, 0.438, 0.468, 0.412,
            0.472, 0.396, 0.507, 0.451, 0.495, 0.418, 0.494, 0.385, 0.493, 0.363, 0.530, 0.454,
            0.524, 0.428, 0.519, 0.398, 0.511, 0.372
        ]);

    public static IReadOnlyList<HandLandmarks> RealNonHeart3 => RealPair(
        [
            0.457, 0.528, 0.502, 0.551, 0.537, 0.544, 0.562, 0.554, 0.583, 0.571, 0.532, 0.476,
            0.561, 0.495, 0.569, 0.538, 0.568, 0.573, 0.507, 0.466, 0.520, 0.430, 0.530, 0.438,
            0.536, 0.447, 0.482, 0.475, 0.489, 0.432, 0.500, 0.415, 0.509, 0.402, 0.459, 0.501,
            0.456, 0.471, 0.458, 0.456, 0.463, 0.441
        ],
        [
            0.623, 0.560, 0.572, 0.540, 0.537, 0.520, 0.513, 0.510, 0.499, 0.514, 0.506, 0.472,
            0.478, 0.488, 0.465, 0.497, 0.458, 0.511, 0.517, 0.466, 0.485, 0.477, 0.481, 0.474,
            0.485, 0.478, 0.532, 0.471, 0.505, 0.472, 0.509, 0.470, 0.516, 0.468, 0.551, 0.485,
            0.532, 0.463, 0.520, 0.445, 0.509, 0.430
        ]);

    public static IReadOnlyList<HandLandmarks> RealNonHeart4 => RealPair(
        [
            0.367, 0.583, 0.396, 0.585, 0.418, 0.575, 0.437, 0.572, 0.458, 0.573, 0.407, 0.490,
            0.429, 0.469, 0.446, 0.480, 0.457, 0.504, 0.397, 0.474, 0.409, 0.424, 0.427, 0.404,
            0.443, 0.397, 0.387, 0.471, 0.392, 0.419, 0.406, 0.393, 0.420, 0.379, 0.379, 0.478,
            0.379, 0.433, 0.386, 0.408, 0.397, 0.392
        ],
        [
            0.636, 0.590, 0.607, 0.576, 0.586, 0.560, 0.567, 0.559, 0.549, 0.558, 0.602, 0.483,
            0.579, 0.475, 0.560, 0.502, 0.548, 0.533, 0.611, 0.473, 0.602, 0.418, 0.590, 0.382,
            0.582, 0.350, 0.622, 0.480, 0.625, 0.423, 0.624, 0.384, 0.623, 0.350, 0.631, 0.499,
            0.639, 0.459, 0.643, 0.431, 0.644, 0.406
        ]);

    public static IReadOnlyList<HandLandmarks> RealNonHeart5 => RealPair(
        [
            0.692, 0.522, 0.664, 0.514, 0.641, 0.493, 0.618, 0.488, 0.595, 0.488, 0.665, 0.404,
            0.639, 0.381, 0.617, 0.405, 0.603, 0.432, 0.678, 0.391, 0.672, 0.324, 0.662, 0.279,
            0.655, 0.239, 0.691, 0.399, 0.695, 0.338, 0.696, 0.294, 0.695, 0.253, 0.701, 0.420,
            0.710, 0.379, 0.714, 0.350, 0.717, 0.320
        ],
        [
            0.318, 0.537, 0.355, 0.537, 0.381, 0.522, 0.406, 0.519, 0.430, 0.526, 0.360, 0.424,
            0.385, 0.397, 0.406, 0.418, 0.418, 0.446, 0.348, 0.403, 0.360, 0.336, 0.384, 0.333,
            0.399, 0.344, 0.336, 0.399, 0.339, 0.331, 0.357, 0.321, 0.372, 0.324, 0.326, 0.408,
            0.323, 0.352, 0.334, 0.335, 0.347, 0.332
        ]);

    private static IReadOnlyList<HandLandmarks> RealPair(double[] a, double[] b) => [RealHand(a), RealHand(b)];

    private static HandLandmarks RealHand(double[] xy)
    {
        var points = new HandPoint[21];
        for (var i = 0; i < 21; i++)
            points[i] = new HandPoint(xy[2 * i], xy[2 * i + 1], 0);
        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        return new HandLandmarks(points, 0.98, new NormRect(minX, minY, maxX - minX, maxY - minY));
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
