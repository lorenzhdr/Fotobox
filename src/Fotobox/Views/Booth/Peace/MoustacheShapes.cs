using System.Windows;
using System.Windows.Media;
using Fotobox.Core;

namespace Fotobox.Views.Booth.Peace;

/// <summary>Stützpunkt der Mittellinie eines Bartes im Einheitsmaß: Lage, Dicke und Wuchsrichtung (Einheitsvektor).</summary>
internal readonly record struct StripSample(double X, double Y, double Width, double Tx, double Ty);

/// <summary>
/// Alles, was die Haartextur über eine Bartform wissen muss: die Umrisse (Außen- und etwas schmalere Unterwolle) und die dicht
/// abgetastete Mittellinie von der linken bis zur rechten Spitze. Alles im Einheitsmaß (Breite 1, Ursprung = Mitte der Oberlippe,
/// y nach unten).
/// </summary>
internal sealed record ShapeData(Geometry Outline, Geometry Undercoat, IReadOnlyList<StripSample> Samples);

/// <summary>
/// Die Bartformen als eingefrorene Geometrien im Einheitsmaß: Breite 1, Mitte (0, 0) auf Höhe der Oberlippe, y nach unten,
/// achsensymmetrisch zur y-Achse. Eine Quelle für Livebild, Foto und Admin-Skizzen. Jede Form entsteht einmalig aus der Mittellinie
/// ihrer rechten Hälfte mit wechselnder Dicke (Catmull-Rom, runde Enden; links gespiegelt) und wird dann auf Breite 1 normiert.
/// Die Normierung verschiebt nur in x (zentrieren) und skaliert gleichmäßig um den Ursprung: In y wird bewusst nie zentriert, der
/// Ursprung bleibt auf der Oberlippe, auch bei Formen mit langen Hängeenden (Fu Manchu, Hufeisen) oder steilen Spitzen (Dalí).
/// </summary>
public static class MoustacheShapes
{
    /// <summary>
    /// Geometrie der Form. <see cref="MoustacheStyle.Random"/> ist keine Form: Der Aufrufer löst es vorher in eine konkrete Form
    /// auf; hier liefert es vorsichtshalber <see cref="MoustacheStyle.Twirl"/>.
    /// </summary>
    public static Geometry Get(MoustacheStyle style) => GetData(style).Outline;

    internal static ShapeData GetData(MoustacheStyle style) =>
        Shapes.TryGetValue(style, out var data) ? data : Shapes[MoustacheStyle.Twirl];

    // Anteil der Dicke, den die Unterwolle (dunkler Kern unter den Haaren) behält: Die Haare ragen darüber hinaus und
    // fransen den Rand aus.
    private const double UndercoatWidthFactor = 0.62;

    // ---- Mittellinien der rechten Hälfte: (x, y, Dicke), beginnend bei x = 0 ----

    private static (double X, double Y, double W)[] Pt(params (double X, double Y, double W)[] points) => points;

    /// <summary>Spirale der Zwirbelspitze: Mittelpunkt <paramref name="c"/>, Radius und Dicke nehmen ab, Winkel in Grad (y nach unten).</summary>
    private static IEnumerable<(double X, double Y, double W)> Spiral(
        (double X, double Y) c, double startDeg, double endDeg, double r0, double r1, double w0, double w1, int steps)
    {
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            var a = (startDeg + (endDeg - startDeg) * t) * Math.PI / 180;
            var r = r0 + (r1 - r0) * t;
            yield return (c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a), w0 + (w1 - w0) * t);
        }
    }

    /// <summary>Klassischer Zwirbelbart: kräftige Mitte, nach außen dünner, die Spitzen rollen sich zu einer Locke ein.</summary>
    private static readonly (double X, double Y, double W)[] TwirlHalf = Pt(
        (0.000, 0.012, 0.160),
        (0.090, 0.000, 0.176),
        (0.190, 0.022, 0.158),
        (0.285, 0.062, 0.118),
        (0.350, 0.088, 0.086))
        .Concat(Spiral((0.405, 0.000), 90, -250, 0.088, 0.020, 0.058, 0.012, 9)).ToArray();

    /// <summary>Walross: massig und buschig, die Enden hängen weit nach unten.</summary>
    private static readonly (double X, double Y, double W)[] WalrusHalf = Pt(
        (0.000, 0.014, 0.205),
        (0.090, -0.002, 0.236),
        (0.190, 0.014, 0.250),
        (0.290, 0.062, 0.232),
        (0.372, 0.140, 0.190),
        (0.430, 0.235, 0.140),
        (0.462, 0.330, 0.092),
        (0.478, 0.410, 0.050));

    /// <summary>Chevron: breiter, dicker Block mit fast waagerechter Unterkante und stumpfen Enden.</summary>
    private static readonly (double X, double Y, double W)[] ChevronHalf = Pt(
        (0.000, 0.016, 0.200),
        (0.120, 0.008, 0.232),
        (0.250, 0.010, 0.238),
        (0.350, 0.022, 0.222),
        (0.425, 0.040, 0.186),
        (0.470, 0.058, 0.124),
        (0.490, 0.070, 0.074));

    /// <summary>Pinselbart („Pencil“): schmal, leicht geschwungen, die feinen Spitzen kippen keck nach unten.</summary>
    private static readonly (double X, double Y, double W)[] BrushHalf = Pt(
        (0.000, 0.004, 0.066),
        (0.070, -0.008, 0.082),
        (0.170, -0.006, 0.090),
        (0.280, 0.010, 0.078),
        (0.380, 0.040, 0.054),
        (0.450, 0.078, 0.030),
        (0.495, 0.118, 0.010));

    /// <summary>Dalí / Lenker: dünne, lange Spitzen, die steil nach oben gezwirbelt sind.</summary>
    private static readonly (double X, double Y, double W)[] HandlebarHalf = Pt(
        (0.000, 0.010, 0.112),
        (0.090, 0.000, 0.124),
        (0.190, 0.018, 0.108),
        (0.285, 0.026, 0.078),
        (0.360, 0.008, 0.054),
        (0.420, -0.052, 0.040),
        (0.452, -0.140, 0.032),
        (0.470, -0.250, 0.025),
        (0.480, -0.370, 0.018),
        (0.485, -0.470, 0.012),
        (0.488, -0.545, 0.007));

    /// <summary>Fu Manchu: schlanker Balken, an den Mundwinkeln hängen lange, dünne Enden weit nach unten.</summary>
    private static readonly (double X, double Y, double W)[] FuManchuHalf = Pt(
        (0.000, 0.012, 0.110),
        (0.090, 0.002, 0.120),
        (0.190, 0.016, 0.102),
        (0.285, 0.040, 0.082),
        (0.360, 0.080, 0.068),
        (0.415, 0.150, 0.058),
        (0.445, 0.260, 0.050),
        (0.458, 0.400, 0.043),
        (0.462, 0.560, 0.035),
        (0.458, 0.720, 0.026),
        (0.450, 0.860, 0.016),
        (0.444, 0.960, 0.008));

    /// <summary>Hufeisen (Biker-Stil): dicker Bart, seitlich an den Mundwinkeln senkrecht bis etwa Kinnhöhe herunter.</summary>
    private static readonly (double X, double Y, double W)[] HorseshoeHalf = Pt(
        (0.000, 0.016, 0.170),
        (0.100, 0.006, 0.186),
        (0.200, 0.016, 0.182),
        (0.300, 0.040, 0.168),
        (0.385, 0.082, 0.150),
        (0.440, 0.155, 0.132),
        (0.462, 0.250, 0.122),
        (0.468, 0.380, 0.114),
        (0.466, 0.510, 0.104),
        (0.460, 0.610, 0.090),
        (0.455, 0.690, 0.062));

    // Muss nach den Mittellinien stehen (statische Felder werden in Textreihenfolge initialisiert).
    private static readonly Dictionary<MoustacheStyle, ShapeData> Shapes = new()
    {
        [MoustacheStyle.Twirl] = Build(TwirlHalf),
        [MoustacheStyle.Walrus] = Build(WalrusHalf),
        [MoustacheStyle.Chevron] = Build(ChevronHalf),
        [MoustacheStyle.Brush] = Build(BrushHalf),
        [MoustacheStyle.Handlebar] = Build(HandlebarHalf),
        [MoustacheStyle.FuManchu] = Build(FuManchuHalf),
        [MoustacheStyle.Horseshoe] = Build(HorseshoeHalf),
    };

    private static ShapeData Build((double X, double Y, double W)[] half)
    {
        // Ganze Mittellinie: linke Hälfte gespiegelt, danach die rechte. Eine einzige Figur, damit keine Naht in der Mitte entsteht
        // und die Spitzen exakt symmetrisch sind.
        var path = new List<(double X, double Y, double W)>();
        for (var i = half.Length - 1; i >= 1; i--)
            path.Add((-half[i].X, half[i].Y, half[i].W));
        path.AddRange(half);

        var full = Ribbon(path, widthFactor: 1);
        var core = Ribbon(path, widthFactor: UndercoatWidthFactor);

        var (shift, scale) = NormalizeParameters(full.Outline);
        var samples = full.Center.Select(c => new StripSample(
            (c.X - shift) * scale, c.Y * scale, c.W * scale, c.Tx, c.Ty)).ToList();
        return new ShapeData(
            ToGeometry(Transform(full.Outline, shift, scale)),
            ToGeometry(Transform(core.Outline, shift, scale)),
            samples);
    }

    // ---- Hilfsfunktionen ----

    private readonly record struct CenterPoint(double X, double Y, double W, double Tx, double Ty);

    /// <summary>Umriss einer Linie mit wechselnder Dicke (Catmull-Rom-Spline, runde Enden) samt abgetasteter Mittellinie.</summary>
    private static (List<(double X, double Y)> Outline, List<CenterPoint> Center) Ribbon(
        List<(double X, double Y, double W)> control, double widthFactor)
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

        var samples = new List<(double X, double Y, double W)>();
        for (var i = 0; i < n - 1; i++)
        {
            var p0 = At(i - 1);
            var p1 = At(i);
            var p2 = At(i + 1);
            var p3 = At(i + 2);
            for (var s = 0; s < perSegment; s++)
            {
                var t = (double)s / perSegment;
                samples.Add((CatmullRom(p0.X, p1.X, p2.X, p3.X, t), CatmullRom(p0.Y, p1.Y, p2.Y, p3.Y, t),
                    Math.Max(0.004, CatmullRom(p0.W, p1.W, p2.W, p3.W, t)) * widthFactor));
            }
        }
        samples.Add((control[n - 1].X, control[n - 1].Y, control[n - 1].W * widthFactor));

        var center = new List<CenterPoint>(samples.Count);
        var left = new List<(double X, double Y)>();
        var right = new List<(double X, double Y)>();
        for (var i = 0; i < samples.Count; i++)
        {
            var a = samples[Math.Max(0, i - 1)];
            var b = samples[Math.Min(samples.Count - 1, i + 1)];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9)
            {
                dx = 1;
                dy = 0;
                len = 1;
            }
            var tx = dx / len;
            var ty = dy / len;
            var nx = -ty;
            var ny = tx;
            var half = samples[i].W / 2;
            left.Add((samples[i].X + nx * half, samples[i].Y + ny * half));
            right.Add((samples[i].X - nx * half, samples[i].Y - ny * half));
            center.Add(new CenterPoint(samples[i].X, samples[i].Y, samples[i].W, tx, ty));
        }

        var outline = new List<(double X, double Y)>(left);
        AddCap(outline, center[^1], left[^1]);
        for (var i = right.Count - 1; i >= 0; i--)
            outline.Add(right[i]);
        AddCap(outline, center[0], right[0]);
        return (outline, center);
    }

    /// <summary>Halbkreis um <paramref name="c"/> ab <paramref name="from"/> (ohne die Endpunkte).</summary>
    private static void AddCap(List<(double X, double Y)> outline, CenterPoint c, (double X, double Y) from)
    {
        var a0 = Math.Atan2(from.Y - c.Y, from.X - c.X);
        // Die Kontur läuft mit negativem Drehsinn (y nach unten): Der Halbkreis führt immer über die Vorwärts- bzw. Rückwärtsrichtung.
        const double diff = -Math.PI;
        var r = c.W / 2;
        const int steps = 8;
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

    /// <summary>Verschiebung in x (Mitte der Bounding-Box auf 0) und gleichmäßiger Maßstab (Breite 1); y wird nie verschoben.</summary>
    private static (double Shift, double Scale) NormalizeParameters(List<(double X, double Y)> points)
    {
        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        return ((minX + maxX) / 2, 1.0 / (maxX - minX));
    }

    private static List<(double X, double Y)> Transform(List<(double X, double Y)> points, double shift, double scale) =>
        points.Select(p => ((p.X - shift) * scale, p.Y * scale)).ToList();

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
