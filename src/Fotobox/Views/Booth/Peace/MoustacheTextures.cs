using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Core.Peace;

namespace Fotobox.Views.Booth.Peace;

/// <summary>
/// Vorberechnete Haartextur eines Bartes: eingefrorenes Bild (96 dpi, Pbgra32) samt seiner Lage im Einheitsmaß der Form
/// (<see cref="UnitRect"/>: Ursprung = Mitte der Oberlippe, Breite der Form = 1; das Bild ragt mit Rand für Haarfransen und
/// Schatten darüber hinaus). Gezeichnet wird mit demselben Maßstab wie die Geometrie: Die Breite kommt von der Form, nicht vom Bild.
/// </summary>
public sealed record MoustacheTexture(BitmapSource Bitmap, Rect UnitRect, int PixelsPerUnit);

/// <summary>
/// Erzeugt und cacht die Haartexturen je (Form, Farbe, Auflösung). Das Bild ist offscreen vorberechnet: Unterwolle, viele feine
/// Haarstriche (fester Seed, deterministisch) entlang der Wuchsrichtung, am Rand ausfransend, dazu ein weicher Schatten auf der
/// Lippe darunter (Box-Weichzeichner auf den Pixeln, ohne WPF-Effekte). Im Livebild läuft danach nur <c>DrawImage</c>.
/// <para>
/// Die Haare werden für die rechte Hälfte gewürfelt und gespiegelt, der Schatten wird nur senkrecht versetzt: Das Bild ist exakt
/// symmetrisch (<see cref="MoustacheRenderer.Draw"/> spiegelt über den Winkel). Auflösungsstufen: Es wird die kleinste Stufe gewählt,
/// die mindestens so breit ist wie der Bart gezeichnet wird (also höchstens 2-fach verkleinert), ein breiter Bart im Foto bekommt so
/// eine eigene, höher aufgelöste Textur. <see cref="Get"/> ist threadsicher; das Erzeugen braucht einen STA-Thread (WPF-Rendern),
/// das Ergebnis ist eingefroren und überall nutzbar.
/// </para>
/// </summary>
public static class MoustacheTextures
{
    /// <summary>Auflösungsstufen in Pixeln je Einheit (= Pixelbreite des Bartes ohne Rand).</summary>
    public static IReadOnlyList<int> Tiers { get; } = [256, 512, 1024, 2048];

    // Rand um die Form (Einheitsmaß): Haarfransen und Schatten (unten mehr, der Schatten ist nach unten versetzt).
    private const double MarginSide = 0.05, MarginTop = 0.05, MarginBottom = 0.11;
    // Schatten auf der Lippe: Versatz nach unten, Weichzeichnerradius und Deckkraft.
    private const double ShadowOffset = 0.026, ShadowBlur = 0.016, ShadowOpacity = 0.42;
    private static readonly Color ShadowColor = Color.FromRgb(0x24, 0x10, 0x08);

    private static readonly ConcurrentDictionary<(MoustacheStyle, MoustacheColor, int), Lazy<MoustacheTexture>> Cache = new();

    /// <summary>
    /// Textur für einen Bart, der <paramref name="widthPx"/> Pixel breit gezeichnet wird (gecacht; dieselbe Instanz für denselben
    /// Schlüssel). <see cref="MoustacheStyle.Random"/> gilt als Zwirbel.
    /// </summary>
    public static MoustacheTexture Get(MoustacheStyle style, MoustacheColor color, double widthPx) =>
        GetTier(style, color, TierFor(widthPx));

    /// <summary>Textur einer festen Stufe (<see cref="Tiers"/>; andere Werte werden auf die nächste Stufe gesetzt).</summary>
    public static MoustacheTexture GetTier(MoustacheStyle style, MoustacheColor color, int pixelsPerUnit)
    {
        var ppu = TierFor(pixelsPerUnit);
        var key = (Normalize(style), color, ppu);
        return Cache.GetOrAdd(key, k => new Lazy<MoustacheTexture>(() => Create(k.Item1, k.Item2, k.Item3))).Value;
    }

    /// <summary>Kleinste Stufe, die mindestens <paramref name="widthPx"/> breit ist (größer als die größte Stufe → die größte).</summary>
    public static int TierFor(double widthPx)
    {
        foreach (var tier in Tiers)
        {
            if (widthPx <= tier)
                return tier;
        }
        return Tiers[^1];
    }

    /// <summary>Ist die Textur schon erzeugt?</summary>
    public static bool IsCached(MoustacheStyle style, MoustacheColor color, int pixelsPerUnit) =>
        Cache.TryGetValue((Normalize(style), color, TierFor(pixelsPerUnit)), out var lazy) && lazy.IsValueCreated;

    /// <summary>
    /// Erzeugt die Texturen der angegebenen Stufen für alle Farben und die angegebenen Formen im Hintergrund (eigener STA-Thread,
    /// niedrige Priorität), damit das erste Erscheinen eines Bartes nicht ruckelt. Doppelte Arbeit entsteht nicht: Wer dieselbe
    /// Textur gleichzeitig braucht, wartet auf die laufende Erzeugung.
    /// </summary>
    public static Thread PrewarmInBackground(IEnumerable<MoustacheStyle> styles, params int[] tiers)
    {
        var styleList = styles.Select(Normalize).Distinct().ToList();
        var tierList = tiers.Length > 0 ? tiers : [Tiers[0], Tiers[1]];
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var tier in tierList)
                {
                    foreach (var style in styleList)
                    {
                        foreach (var color in MoustacheStyles.AllColors)
                            GetTier(style, color, tier);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Bartzexturen konnten nicht vorberechnet werden.", ex);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Bart-Texturen",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    private static MoustacheStyle Normalize(MoustacheStyle style) => MoustacheStyles.IsConcrete(style) ? style : MoustacheStyle.Twirl;

    // ---- Erzeugen ----

    /// <summary>Erzeugt die Textur ohne Cache (deterministisch: gleiche Eingabe, gleiche Pixel).</summary>
    internal static MoustacheTexture Create(MoustacheStyle style, MoustacheColor color, int pixelsPerUnit)
    {
        style = Normalize(style);
        var data = MoustacheShapes.GetData(style);
        var bounds = data.Outline.Bounds;

        // Bild und Einheitsmaß hängen exakt zusammen: Pixel = (Einheit − Ursprung) · Auflösung, symmetrisch um x = 0.
        var width = Math.Max(2, (int)Math.Ceiling((1 + 2 * MarginSide) * pixelsPerUnit / 2) * 2);
        var top = bounds.Top - MarginTop;
        var height = Math.Max(2, (int)Math.Ceiling((bounds.Bottom + MarginBottom - top) * pixelsPerUnit));
        var rect = new Rect(-width / (2.0 * pixelsPerUnit), top, width / (double)pixelsPerUnit, height / (double)pixelsPerUnit);

        var hair = RenderHair(data, color, style, rect, width, height, pixelsPerUnit);
        var pixels = new byte[width * 4 * height];
        hair.CopyPixels(pixels, width * 4, 0);
        ApplyShadow(pixels, width, height, pixelsPerUnit);

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return new MoustacheTexture(bitmap, rect, pixelsPerUnit);
    }

    private static BitmapSource RenderHair(ShapeData data, MoustacheColor color, MoustacheStyle style, Rect rect, int width, int height, int ppu)
    {
        var stops = MoustachePalette.Stops(color);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Einheitsmaß → Pixel
            dc.PushTransform(new MatrixTransform(ppu, 0, 0, ppu, -rect.X * ppu, -rect.Y * ppu));
            dc.DrawGeometry(UndercoatBrush(stops), null, data.Undercoat);
            // Bei kleinen Stufen sind einzelne Haare kleiner als ein Pixel: weniger Haare ergeben dasselbe Bild in kürzerer Zeit.
            var densityScale = Math.Clamp(ppu / 1024.0, 0.5, 1.0);
            new HairPainter(data, stops, MoustachePalette.Speckle(color), SeedFor(style), densityScale).Paint(dc);
            dc.Pop();
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static Brush UndercoatBrush(IReadOnlyList<Color> stops)
    {
        var brush = new LinearGradientBrush(
            [
                new GradientStop(MoustachePalette.Ramp(stops, 0.30), 0.0),
                new GradientStop(MoustachePalette.Ramp(stops, 0.05), 1.0),
            ],
            new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    private static int SeedFor(MoustacheStyle style) => 7919 + 101 * (int)style;

    /// <summary>
    /// Schatten: Der Alphakanal wird dreimal mit einem Box-Filter weichgezeichnet, nach unten versetzt und unter das Haar gelegt
    /// (vormultipliziert, Pbgra32).
    /// </summary>
    private static void ApplyShadow(byte[] pixels, int width, int height, int ppu)
    {
        var alpha = new float[width * height];
        for (var i = 0; i < alpha.Length; i++)
            alpha[i] = pixels[i * 4 + 3] / 255f;

        var radius = Math.Max(1, (int)Math.Round(ShadowBlur * ppu));
        var scratch = new float[Math.Max(width, height)];
        for (var pass = 0; pass < 3; pass++)
        {
            BoxBlurHorizontal(alpha, width, height, radius, scratch);
            BoxBlurVertical(alpha, width, height, radius, scratch);
        }

        var offset = (int)Math.Round(ShadowOffset * ppu);
        float sr = ShadowColor.R / 255f, sg = ShadowColor.G / 255f, sb = ShadowColor.B / 255f;
        for (var y = 0; y < height; y++)
        {
            var srcY = y - offset;
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var sa = srcY >= 0 ? alpha[srcY * width + x] * (float)ShadowOpacity : 0f;
                if (sa <= 0f)
                    continue;
                var inverse = 1f - pixels[i + 3] / 255f;
                var k = sa * inverse; // Anteil des Schattens, der nicht vom Haar verdeckt ist
                pixels[i] = (byte)Math.Min(255, pixels[i] + (int)Math.Round(sb * 255f * k));
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] + (int)Math.Round(sg * 255f * k));
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] + (int)Math.Round(sr * 255f * k));
                pixels[i + 3] = (byte)Math.Min(255, pixels[i + 3] + (int)Math.Round(255f * k));
            }
        }
    }

    private static void BoxBlurHorizontal(float[] data, int width, int height, int radius, float[] row)
    {
        var window = 2 * radius + 1;
        for (var y = 0; y < height; y++)
        {
            var offset = y * width;
            float sum = 0;
            for (var x = 0; x <= radius && x < width; x++)
                sum += data[offset + x];
            for (var x = 0; x < width; x++)
            {
                row[x] = sum / window;
                var add = x + radius + 1;
                var remove = x - radius;
                if (add < width)
                    sum += data[offset + add];
                if (remove >= 0)
                    sum -= data[offset + remove];
            }
            Array.Copy(row, 0, data, offset, width);
        }
    }

    private static void BoxBlurVertical(float[] data, int width, int height, int radius, float[] column)
    {
        var window = 2 * radius + 1;
        for (var x = 0; x < width; x++)
        {
            float sum = 0;
            for (var y = 0; y <= radius && y < height; y++)
                sum += data[y * width + x];
            for (var y = 0; y < height; y++)
            {
                column[y] = sum / window;
                var add = y + radius + 1;
                var remove = y - radius;
                if (add < height)
                    sum += data[add * width + x];
                if (remove >= 0)
                    sum -= data[remove * width + x];
            }
            for (var y = 0; y < height; y++)
                data[y * width + x] = column[y];
        }
    }

    // ---- Haare ----

    /// <summary>Würfelt und zeichnet die Haarstriche einer Form (rechte Hälfte, gespiegelt) in Einheitskoordinaten.</summary>
    private sealed class HairPainter
    {
        private const double CenterOverlap = 0.03; // Haare dürfen knapp links der Mitte beginnen (ihr Spiegelbild füllt die Mitte)
        private const int ColorLevels = 14;
        private const int ThicknessLevels = 6;
        private const double ThicknessMin = 0.0010, ThicknessMax = 0.0075;

        private readonly IReadOnlyList<StripSample> _samples;
        private readonly IReadOnlyList<Color> _stops;
        private readonly double _speckle;
        private readonly double _densityScale;
        private readonly Random _random;
        private readonly double[] _cumulative;
        private readonly int[] _indices;
        private readonly double _area;
        private readonly double _minY, _maxY;

        // Gebündelte Linien: je (Dicke, Farbstufe) eine Geometrie.
        private readonly StreamGeometryContext?[,] _contexts = new StreamGeometryContext?[ThicknessLevels, ColorLevels];
        private readonly StreamGeometry?[,] _geometries = new StreamGeometry?[ThicknessLevels, ColorLevels];
        private StreamGeometry? _highlight;
        private StreamGeometryContext? _highlightContext;

        public HairPainter(ShapeData data, IReadOnlyList<Color> stops, double speckle, int seed, double densityScale)
        {
            _samples = data.Samples;
            _stops = stops;
            _speckle = speckle;
            _densityScale = densityScale;
            _random = new Random(seed);

            // Nur die rechte Hälfte (x ≥ 0) wird gewürfelt; die Wuchsrichtung zeigt von der Mitte nach außen.
            var indices = new List<int>();
            var cumulative = new List<double>();
            double area = 0;
            for (var i = 1; i < _samples.Count; i++)
            {
                var a = _samples[i - 1];
                var b = _samples[i];
                if (b.X < -CenterOverlap || a.X < -CenterOverlap)
                    continue;
                var ds = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                area += ds * (a.Width + b.Width) / 2;
                indices.Add(i);
                cumulative.Add(area);
            }
            _indices = [.. indices];
            _cumulative = [.. cumulative];
            _area = area;
            _minY = _samples.Min(s => s.Y - s.Width / 2);
            _maxY = _samples.Max(s => s.Y + s.Width / 2);
        }

        public void Paint(DrawingContext dc)
        {
            // Dicke Haare als Körper, feine darüber, einzelne abstehende Haare, zuletzt Glanzlichter.
            Layer(new LayerSpec(Density: 22000, ThicknessMin: 0.0044, ThicknessMax: 0.0074, LengthMin: 0.045, LengthMax: 0.090,
                Spread: 0.92, Jitter: 0.20, Pieces: 3, Highlight: false));
            Layer(new LayerSpec(Density: 70000, ThicknessMin: 0.0018, ThicknessMax: 0.0032, LengthMin: 0.028, LengthMax: 0.060,
                Spread: 1.10, Jitter: 0.26, Pieces: 3, Highlight: false));
            Layer(new LayerSpec(Density: 1500, ThicknessMin: 0.0015, ThicknessMax: 0.0026, LengthMin: 0.040, LengthMax: 0.085,
                Spread: 1.28, Jitter: 0.55, Pieces: 3, Highlight: false, EdgeOnly: true));
            Layer(new LayerSpec(Density: 9000, ThicknessMin: 0.0012, ThicknessMax: 0.0020, LengthMin: 0.030, LengthMax: 0.070,
                Spread: 1.00, Jitter: 0.20, Pieces: 2, Highlight: true));

            // Dicke von grob nach fein, innerhalb jeder Dicke dunkel → hell: feine, helle Haare liegen obenauf.
            for (var t = 0; t < ThicknessLevels; t++)
            {
                for (var c = 0; c < ColorLevels; c++)
                {
                    var ctx = _contexts[t, c];
                    if (ctx is null)
                        continue;
                    ctx.Close();
                    var geometry = _geometries[t, c]!;
                    geometry.Freeze();
                    var thickness = ThicknessMin * Math.Pow(ThicknessMax / ThicknessMin, t / (double)(ThicknessLevels - 1));
                    dc.DrawGeometry(null, MakePen(MoustachePalette.Ramp(_stops, c / (double)(ColorLevels - 1)), thickness, 1.0), geometry);
                }
            }

            if (_highlightContext is not null)
            {
                _highlightContext.Close();
                _highlight!.Freeze();
                dc.DrawGeometry(null, MakePen(Lighten(MoustachePalette.Ramp(_stops, 1.0), 0.18), 0.0016, 0.42), _highlight);
            }
        }

        private readonly record struct LayerSpec(double Density, double ThicknessMin, double ThicknessMax, double LengthMin, double LengthMax,
            double Spread, double Jitter, int Pieces, bool Highlight, bool EdgeOnly = false);

        private void Layer(LayerSpec spec)
        {
            var count = (int)Math.Round(_area * spec.Density * _densityScale);
            for (var n = 0; n < count; n++)
            {
                // Ausgangspunkt: Stelle auf der Mittellinie (nach Fläche gewichtet), quer dazu versetzt.
                var pick = _random.NextDouble() * _area;
                var k = Array.BinarySearch(_cumulative, pick);
                if (k < 0)
                    k = ~k;
                k = Math.Min(k, _indices.Length - 1);
                var i = _indices[k];
                var a = _samples[i - 1];
                var b = _samples[i];
                var f = _random.NextDouble();
                var x = a.X + (b.X - a.X) * f;
                var y = a.Y + (b.Y - a.Y) * f;
                var w = a.Width + (b.Width - a.Width) * f;
                var tx = a.Tx + (b.Tx - a.Tx) * f;
                var ty = a.Ty + (b.Ty - a.Ty) * f;
                var tl = Math.Sqrt(tx * tx + ty * ty);
                if (tl > 0) { tx /= tl; ty /= tl; }

                var u = (_random.NextDouble() * 2 - 1) * spec.Spread;
                if (spec.EdgeOnly)
                    u = Math.Sign(u == 0 ? 1 : u) * (0.78 + 0.5 * _random.NextDouble());
                var nx = -ty;
                var ny = tx;
                var px = x + nx * u * w / 2;
                var py = y + ny * u * w / 2;

                // Wuchsrichtung: nach außen entlang der Mittellinie, nahe der Mitte stärker nach unten (Philtrum).
                var down = 0.14 + 1.6 * Math.Exp(-Math.Pow(x / 0.12, 2));
                var vx = tx;
                var vy = ty + down;
                var angle = Math.Atan2(vy, vx) + Gauss() * spec.Jitter;

                var length = spec.LengthMin + (spec.LengthMax - spec.LengthMin) * _random.NextDouble();
                var thickness = spec.ThicknessMin + (spec.ThicknessMax - spec.ThicknessMin) * _random.NextDouble();
                thickness = Math.Min(thickness, Math.Max(ThicknessMin, 0.5 * w));
                // Auf dünnen Spitzen kürzer, damit sie nicht zu Borsten ausfransen.
                length = Math.Min(length, Math.Max(0.02, 3.5 * w + 0.03));

                // Helligkeit: außen und weit von der Mitte heller, je Haar gestreut; Grau (Salz und Pfeffer) streut stark.
                var edge = Math.Min(1, Math.Abs(u));
                var baseLevel = 0.22 + 0.26 * edge * edge + 0.14 * Math.Min(1, Math.Abs(x) / 0.5);
                var level = _speckle > 0 ? _random.NextDouble() : baseLevel + 0.16 * Gauss();
                var rootShift = _speckle > 0 ? -0.04 : -0.20;
                var tipShift = _speckle > 0 ? 0.04 : 0.20;
                var bend = Gauss() * 0.20;

                if (spec.Highlight)
                {
                    // Glanzlichter bevorzugt in der oberen Hälfte der Form.
                    var rel = _maxY > _minY ? (py - _minY) / (_maxY - _minY) : 0.5;
                    if (_random.NextDouble() > Math.Clamp(1.15 - 1.3 * rel, 0.12, 1.0))
                        continue;
                }

                AddStrand(spec, px, py, angle, length, thickness, level, rootShift, tipShift, bend);
            }
        }

        // Ein Haar aus mehreren kurzen Stücken: zur Spitze dünner und heller, leicht gebogen. Gespiegelt auch links.
        private void AddStrand(LayerSpec spec, double px, double py, double angle, double length, double thickness,
            double level, double rootShift, double tipShift, double bend)
        {
            var pieceLength = length / spec.Pieces;
            double x = px, y = py;
            for (var p = 0; p < spec.Pieces; p++)
            {
                var mid = spec.Pieces == 1 ? 0.5 : p / (double)(spec.Pieces - 1);
                var taper = 1.0 - 0.45 * mid;
                var a = angle + bend * (p + 0.5);
                var nx = x + Math.Cos(a) * pieceLength;
                var ny = y + Math.Sin(a) * pieceLength;

                if (spec.Highlight)
                {
                    Segment(ref _highlight, ref _highlightContext, x, y, nx, ny);
                }
                else
                {
                    var tier = Math.Clamp(thickness * taper, ThicknessMin, ThicknessMax);
                    var ti = (int)Math.Round(Math.Log(tier / ThicknessMin) / Math.Log(ThicknessMax / ThicknessMin) * (ThicknessLevels - 1));
                    var shade = Math.Clamp(level + rootShift + (tipShift - rootShift) * mid, 0, 1);
                    var ci = (int)Math.Round(shade * (ColorLevels - 1));
                    ref var ctx = ref _contexts[ti, ci];
                    ref var geometry = ref _geometries[ti, ci];
                    Segment(ref geometry, ref ctx, x, y, nx, ny);
                }
                x = nx;
                y = ny;
            }
        }

        private static void Segment(ref StreamGeometry? geometry, ref StreamGeometryContext? context, double x0, double y0, double x1, double y1)
        {
            if (context is null)
            {
                geometry = new StreamGeometry();
                context = geometry.Open();
            }
            // rechte Seite und ihr Spiegelbild
            context.BeginFigure(new Point(x0, y0), isFilled: false, isClosed: false);
            context.LineTo(new Point(x1, y1), isStroked: true, isSmoothJoin: false);
            context.BeginFigure(new Point(-x0, y0), isFilled: false, isClosed: false);
            context.LineTo(new Point(-x1, y1), isStroked: true, isSmoothJoin: false);
        }

        private static Pen MakePen(Color color, double thickness, double opacity)
        {
            var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * opacity), color.R, color.G, color.B));
            brush.Freeze();
            var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();
            return pen;
        }

        private static Color Lighten(Color c, double amount) => Color.FromRgb(
            (byte)Math.Round(c.R + (255 - c.R) * amount), (byte)Math.Round(c.G + (255 - c.G) * amount), (byte)Math.Round(c.B + (255 - c.B) * amount));

        // Standardnormalverteilt (Box-Muller), begrenzt auf ±3.
        private double Gauss()
        {
            var u1 = 1.0 - _random.NextDouble();
            var u2 = _random.NextDouble();
            return Math.Clamp(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2), -3, 3);
        }
    }
}
