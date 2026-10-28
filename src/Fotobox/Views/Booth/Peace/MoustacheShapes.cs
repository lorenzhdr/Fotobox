using System.Windows;
using System.Windows.Media;
using Fotobox.Core;

namespace Fotobox.Views.Booth.Peace;

/// <summary>
/// Die Bartformen als eingefrorene Geometrien im Einheitsmaß: Breite 1, Mitte (0, 0) auf Höhe der Oberlippe, y nach unten,
/// achsensymmetrisch zur y-Achse. Eine Quelle für Livebild, Foto und Admin-Skizzen. Die Umrisse entstehen einmalig aus
/// Mittellinien mit wechselnder Dicke (Catmull-Rom) und werden dann auf Breite 1 normiert.
/// </summary>
public static class MoustacheShapes
{
    /// <summary>
    /// Geometrie der Form. <see cref="MoustacheStyle.Random"/> ist keine Form: Der Aufrufer löst es vorher in eine konkrete Form
    /// auf; hier liefert es vorsichtshalber <see cref="MoustacheStyle.Twirl"/>.
    /// </summary>
    public static Geometry Get(MoustacheStyle style) =>
        Shapes.TryGetValue(style, out var geometry) ? geometry : Shapes[MoustacheStyle.Twirl];

    // ---- Mittellinien der rechten Hälfte: (x, y, Dicke), beginnend bei x = 0 ----

    /// <summary>Klassischer Zwirbelbart: kräftige Mitte, nach außen dünner, Spitzen rollen sich nach oben ein.</summary>
    private static readonly (double X, double Y, double W)[] TwirlHalf =
    [
        (0.000, 0.014, 0.150),
        (0.080, 0.000, 0.172),
        (0.170, 0.020, 0.150),
        (0.260, 0.060, 0.108),
        (0.340, 0.100, 0.070),
        (0.410, 0.108, 0.046),
        (0.465, 0.082, 0.036),
        (0.495, 0.030, 0.032),
        (0.495, -0.030, 0.030),
        (0.462, -0.074, 0.028),
        (0.415, -0.082, 0.025),
        (0.380, -0.052, 0.021),
        (0.388, -0.022, 0.017),
        (0.418, -0.020, 0.012),
    ];

    /// <summary>Walross: buschig und breit, die Enden hängen weit nach unten.</summary>
    private static readonly (double X, double Y, double W)[] WalrusHalf =
    [
        (0.000, 0.012, 0.185),
        (0.090, -0.004, 0.215),
        (0.190, 0.012, 0.225),
        (0.290, 0.060, 0.205),
        (0.370, 0.130, 0.165),
        (0.430, 0.215, 0.115),
        (0.465, 0.295, 0.070),
        (0.480, 0.360, 0.036),
    ];

    /// <summary>Pinselbart („Pencil“): schmal, leicht geschwungen, feine Spitzen.</summary>
    private static readonly (double X, double Y, double W)[] BrushHalf =
    [
        (0.000, 0.004, 0.058),
        (0.070, -0.008, 0.078),
        (0.170, -0.004, 0.086),
        (0.280, 0.012, 0.074),
        (0.380, 0.040, 0.050),
        (0.450, 0.074, 0.028),
        (0.500, 0.108, 0.010),
    ];

    // Muss nach den Mittellinien stehen (statische Felder werden in Textreihenfolge initialisiert).
    private static readonly Dictionary<MoustacheStyle, Geometry> Shapes = new()
    {
        [MoustacheStyle.Twirl] = Build(TwirlHalf, caps: true),
        [MoustacheStyle.Walrus] = Build(WalrusHalf, caps: true),
        [MoustacheStyle.Chevron] = BuildChevron(),
        [MoustacheStyle.Brush] = Build(BrushHalf, caps: true),
    };

    private static Geometry Build((double X, double Y, double W)[] half, bool caps)
    {
        // Ganze Mittellinie: linke Hälfte gespiegelt, danach die rechte. Eine einzige Figur, damit keine Naht in der Mitte entsteht.
        var path = new List<(double X, double Y, double W)>();
        for (var i = half.Length - 1; i >= 1; i--)
            path.Add((-half[i].X, half[i].Y, half[i].W));
        path.AddRange(half);

        var outline = Ribbon(path, caps);
        return ToGeometry(Normalize(outline));
    }

    /// <summary>Chevron: dicker Block mit gerader Unterkante, Einkerbung unter der Nase und abgerundeten Enden.</summary>
    private static Geometry BuildChevron()
    {
        const int steps = 90;
        const double bottom = 0.105;
        var top = new List<(double X, double Y)>();
        var low = new List<(double X, double Y)>();
        for (var i = 0; i <= steps; i++)
        {
            var theta = -Math.PI / 2 + Math.PI * i / steps;
            var x = 0.5 * Math.Sin(theta);
            var u = Math.Abs(x) / 0.5;
            // Dicke: voll in der Mitte, gegen die Enden schnell zur Rundung hin abnehmend.
            var thickness = 0.235 * Math.Pow(Math.Max(0, 1 - Math.Pow(u, 4.5)), 0.55) * (1 - 0.18 * u * u);
            var notch = 0.035 * Math.Exp(-Math.Pow(x / 0.055, 2));
            var yTop = bottom - thickness + notch;
            top.Add((x, Math.Min(yTop, bottom - 0.002)));
            low.Add((x, bottom));
        }

        var outline = new List<(double X, double Y)>(top);
        for (var i = low.Count - 1; i >= 0; i--)
            outline.Add(low[i]);
        return ToGeometry(Normalize(outline));
    }

    // ---- Hilfsfunktionen ----

    /// <summary>Umrisspunkte einer Linie mit wechselnder Dicke (Catmull-Rom-Spline, runde Enden).</summary>
    private static List<(double X, double Y)> Ribbon(List<(double X, double Y, double W)> control, bool caps)
    {
        const int perSegment = 14;
        var n = control.Count;

        (double X, double Y, double W) At(int i)
        {
            if (i < 0)
                return Extrapolate(control[0], control[1]);
            if (i >= n)
                return Extrapolate(control[n - 1], control[n - 2]);
            return control[i];
        }

        var center = new List<(double X, double Y, double W)>();
        for (var i = 0; i < n - 1; i++)
        {
            var p0 = At(i - 1);
            var p1 = At(i);
            var p2 = At(i + 1);
            var p3 = At(i + 2);
            for (var s = 0; s < perSegment; s++)
            {
                var t = (double)s / perSegment;
                center.Add((CatmullRom(p0.X, p1.X, p2.X, p3.X, t), CatmullRom(p0.Y, p1.Y, p2.Y, p3.Y, t),
                    Math.Max(0.004, CatmullRom(p0.W, p1.W, p2.W, p3.W, t))));
            }
        }
        center.Add(control[n - 1]);

        var left = new List<(double X, double Y)>();
        var right = new List<(double X, double Y)>();
        for (var i = 0; i < center.Count; i++)
        {
            var a = center[Math.Max(0, i - 1)];
            var b = center[Math.Min(center.Count - 1, i + 1)];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9)
            {
                dx = 1;
                dy = 0;
                len = 1;
            }
            var nx = -dy / len;
            var ny = dx / len;
            var half = center[i].W / 2;
            left.Add((center[i].X + nx * half, center[i].Y + ny * half));
            right.Add((center[i].X - nx * half, center[i].Y - ny * half));
        }

        var outline = new List<(double X, double Y)>(left);
        if (caps)
            AddCap(outline, center[^1], left[^1]);
        for (var i = right.Count - 1; i >= 0; i--)
            outline.Add(right[i]);
        if (caps)
            AddCap(outline, center[0], right[0]);
        return outline;
    }

    /// <summary>Halbkreis um <paramref name="c"/> ab <paramref name="from"/> (ohne die Endpunkte).</summary>
    private static void AddCap(List<(double X, double Y)> outline, (double X, double Y, double W) c, (double X, double Y) from)
    {
        var a0 = Math.Atan2(from.Y - c.Y, from.X - c.X);
        // Die Kontur läuft mit negativem Drehsinn (y nach unten): Der Halbkreis führt immer über die Vorwärts- bzw. Rückwärtsrichtung.
        const double diff = -Math.PI;
        var r = c.W / 2;
        const int steps = 6;
        for (var i = 1; i < steps; i++)
        {
            var a = a0 + diff * i / steps;
            outline.Add((c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a)));
        }
    }

    private static (double X, double Y, double W) Extrapolate((double X, double Y, double W) end, (double X, double Y, double W) next) =>
        (2 * end.X - next.X, 2 * end.Y - next.Y, 2 * end.W - next.W);

    private static double CatmullRom(double p0, double p1, double p2, double p3, double t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return 0.5 * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
    }

    /// <summary>Mittig in x ausrichten, auf Breite 1 skalieren (y gleich, Ursprung der Oberlippe bleibt).</summary>
    private static List<(double X, double Y)> Normalize(List<(double X, double Y)> points)
    {
        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var shift = (minX + maxX) / 2;
        var scale = 1.0 / (maxX - minX);
        return points.Select(p => ((p.X - shift) * scale, p.Y * scale)).ToList();
    }

    private static Geometry ToGeometry(List<(double X, double Y)> outline)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(outline[0].X, outline[0].Y), isFilled: true, isClosed: true);
            ctx.PolyLineTo(outline.Skip(1).Select(p => new Point(p.X, p.Y)).ToList(), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        return geometry;
    }
}
