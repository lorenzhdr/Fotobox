using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Fotobox.Core.Tracking;
using Fotobox.Views.Booth.Peace;

namespace Fotobox.Views.Booth;

/// <summary>
/// Zeichnet ein <see cref="TrackingOverlayFrame"/> in einen <see cref="DrawingContext"/> (für <see cref="TrackingOverlay"/> und die
/// Render-Tests): Personen, Ziele, Gesichter, Hände, Bärte, Herzen und Warum-Zeilen in zwei Stilen mit denselben Daten. Technik = volle
/// Rahmen, Linien und Punkte; SciFi = Eckenrahmen, Fadenkreuze und Scanner-Linien. Rein Darstellung: ohne Effekte, ohne Animation, ohne
/// Zustand (alles wird je Bild aus dem Frame gezeichnet). Koordinaten laufen über <see cref="OverlayMapper"/> (inklusive Spiegelung);
/// ungültige oder weit außerhalb liegende Werte werden übersprungen.
/// </summary>
internal sealed class TrackingOverlayPainter
{
    /// <summary>Weiter als dieser Abstand vom Ansichtsrand zeichnet nichts (schützt vor absurden Koordinaten).</summary>
    private const double FarLimit = 20000;

    private static readonly FontFamily Mono = new("Consolas, Cascadia Mono, Courier New");
    private static readonly Typeface MonoFace = new(Mono, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Dictionary<Color, Brush> Cache = [];

    private readonly DrawingContext _dc;
    private readonly TrackingOverlayFrame _frame;
    private readonly TrackingOverlaySettings _opt;
    private readonly double _viewW, _viewH, _ppd;
    private readonly bool _mirror;
    private readonly OverlayFit _fit;
    private readonly bool _sci;
    private readonly double _base;   // Maßeinheit der Ansicht (Höhe / 1080)
    private readonly double _unit;   // Maßeinheit mit eingestellter Größe
    private readonly Palette _pal;

    private TrackingOverlayPainter(DrawingContext dc, TrackingOverlayFrame frame, double viewW, double viewH, bool mirror, OverlayFit fit, double ppd)
    {
        _dc = dc;
        _frame = frame;
        _opt = frame.Options;
        _viewW = viewW;
        _viewH = viewH;
        _mirror = mirror;
        _fit = fit;
        _ppd = ppd > 0 ? ppd : 1;
        _sci = _opt.Style == TrackingOverlayStyle.SciFi;
        _base = Math.Clamp(viewH / 1080.0, 0.45, 2.5);
        _unit = _base * Math.Clamp(_opt.ScalePercent, SettingsLimits.TrackingOverlayScaleMin, SettingsLimits.TrackingOverlayScaleMax) / 100.0;
        _pal = _sci ? Palette.SciFi : Palette.Technik;
    }

    /// <summary>Zeichnet den Frame in eine Ansicht der Größe <paramref name="viewW"/> × <paramref name="viewH"/>.</summary>
    public static void Paint(DrawingContext dc, TrackingOverlayFrame frame, double viewW, double viewH, bool mirror, OverlayFit fit, double pixelsPerDip)
    {
        if (frame is null || !(viewW > 0) || !(viewH > 0) || !double.IsFinite(viewW) || !double.IsFinite(viewH))
            return;
        new TrackingOverlayPainter(dc, frame, viewW, viewH, mirror, fit, pixelsPerDip).PaintAll();
    }

    private void PaintAll()
    {
        // Reihenfolge von unten nach oben: Personen, Ziele, Gesichter, Bärte, Hände, Herzen, Warum-Zeilen, Merker.
        foreach (var person in _frame.Persons)
            Person(person);
        foreach (var target in _frame.Targets)
            Target(target);
        foreach (var face in _frame.Faces)
            Face(face);
        foreach (var moustache in _frame.Moustaches)
            Moustache(moustache);
        foreach (var hand in _frame.Hands)
            Hand(hand);
        foreach (var heart in _frame.Hearts)
            Heart(heart);
        foreach (var pair in _frame.Pairs)
            Pair(pair);
        Flags();
    }

    // ---------------------------------------------------------------- Ebenen

    private void Person(TrackingPersonItem p)
    {
        if (!TryRect(p.Box, personLayer: true, out var r))
            return;
        var brush = _pal.Person;
        if (_sci)
        {
            _dc.DrawRectangle(Alpha(brush, 0x14), null, r);
            Brackets(r, new Pen(brush, 2.5 * _unit), Math.Min(r.Width, r.Height) * 0.18, ticks: true);
            var y = r.Y + r.Height * 0.5;
            _dc.DrawLine(new Pen(Alpha(brush, 0x50), 1.5 * _unit), new Point(r.X, y), new Point(r.Right, y));
        }
        else
        {
            _dc.DrawRectangle(null, new Pen(brush, 2.5 * _unit), r);
        }
        var text = p.Kind == DetectionKind.Body
            ? string.Create(TrackingOverlayFrame.Culture, $"PERSON {p.Confidence * 100:0} %")
            : "GESICHT";
        Label(text, r.X, r.Y, brush, below: false);
    }

    private void Target(TrackingTargetItem t)
    {
        if (!TryRect(t.Box, personLayer: true, out var r))
            return;
        var brush = _pal.Target;
        var pen = new Pen(brush, (t.IsLockingOn ? 5 : 3.5) * _unit);
        var inset = 6 * _unit;
        var inner = new Rect(r.X - inset, r.Y - inset, r.Width + 2 * inset, r.Height + 2 * inset);
        Brackets(inner, pen, Math.Min(inner.Width, inner.Height) * 0.22, ticks: false);
        Label(t.IsLockingOn ? t.Label + " · EINRASTEN" : t.Label, inner.X, inner.Bottom, brush, below: true);
    }

    private void Face(TrackingFaceItem f)
    {
        var face = f.Face;
        var brush = _pal.Face;
        if (TryRect(face.Box, personLayer: false, out var r))
        {
            if (_sci)
                Brackets(r, new Pen(brush, 2 * _unit), Math.Min(r.Width, r.Height) * 0.22, ticks: false);
            else
                _dc.DrawRectangle(null, new Pen(brush, 1.5 * _unit), r);
            Label(string.Create(TrackingOverlayFrame.Culture, $"GESICHT {f.Id}  {face.Score:0.00}"), r.X, r.Y, brush, below: false);
        }

        if (_sci)
        {
            foreach (var p in new[] { face.RightEye, face.LeftEye, face.Nose, face.MouthRight, face.MouthLeft })
                Reticle(p, brush, 6 * _unit);
            return;
        }
        var outline = new Pen(Solid(Colors.White), 1.5 * _unit);
        Dot(face.RightEye, _pal.Eye, outline, 4.5 * _unit);
        Dot(face.LeftEye, _pal.Eye, outline, 4.5 * _unit);
        Dot(face.Nose, _pal.Nose, outline, 4.5 * _unit);
        Dot(face.MouthRight, _pal.Mouth, outline, 4.5 * _unit);
        Dot(face.MouthLeft, _pal.Mouth, outline, 4.5 * _unit);
    }

    private void Moustache(TrackingMoustacheItem m)
    {
        if (!TryPoint(m.Pose.Center, personLayer: false, out var c) || !(m.Pose.WidthPx > 0) || !double.IsFinite(m.Pose.WidthPx))
            return;
        var scale = ImageScale(personLayer: false);
        var width = m.Pose.WidthPx * scale;
        var angle = _mirror ? -m.Pose.AngleRad : m.Pose.AngleRad;   // die Zeichnung des Bartes negiert den Winkel bei Spiegelung ebenso
        var dx = Math.Cos(angle) * width / 2;
        var dy = Math.Sin(angle) * width / 2;
        var brush = _pal.Moustache;
        var pen = new Pen(brush, 3 * _unit) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var a = new Point(c.X - dx, c.Y - dy);
        var b = new Point(c.X + dx, c.Y + dy);
        _dc.DrawLine(pen, a, b);
        if (_sci)
        {
            Reticle(new Point(a.X, a.Y), brush, 5 * _unit);
            Reticle(new Point(b.X, b.Y), brush, 5 * _unit);
        }
        else
        {
            _dc.DrawEllipse(brush, null, c, 4.5 * _unit, 4.5 * _unit);
        }
        Label(string.Create(TrackingOverlayFrame.Culture, $"BART {m.FaceId}  {m.Pose.WidthPx:0} px"), c.X - width / 2, c.Y + Math.Abs(dy) + 4 * _unit, brush, below: true);
    }

    private void Hand(TrackingHandItem h)
    {
        var lineBrush = h.IsPeace && _opt.ShowGestures ? _pal.Peace : _pal.HandLine;
        var dotBrush = h.IsPeace && _opt.ShowGestures ? _pal.Peace : _pal.Hand;
        var hasBox = TryRect(h.Box, personLayer: false, out var box);

        if (_opt.ShowHands)
        {
            DrawBones(h, lineBrush, dotBrush);
            if (hasBox)
            {
                if (_sci)
                    Brackets(box, new Pen(dotBrush, 2 * _unit), Math.Min(box.Width, box.Height) * 0.2, ticks: false);
                else
                    _dc.DrawRectangle(null, new Pen(Alpha(dotBrush, 0xA0), 1.5 * _unit) { DashStyle = DashStyles.Dash }, box);
            }
        }
        if (!hasBox)
            return;

        var text = string.Create(TrackingOverlayFrame.Culture, $"HAND {h.Number}  {h.Score:0.00}");
        if (_opt.ShowGestures && h.IsPeace)
            text += "  PEACE";
        Label(text, box.X, box.Y, dotBrush, below: false);

        // Haltefortschritt der Peaceerkennung unter der Hand.
        var y = box.Bottom + 4 * _unit;
        if (_opt.ShowGestures && h.Progress > 0)
        {
            var progress = Math.Clamp(h.Progress, 0, 1);
            var barH = 6 * _unit;
            _dc.DrawRectangle(Alpha(Solid(Colors.White), 0x50), null, new Rect(box.X, y, box.Width, barH));
            _dc.DrawRectangle(_pal.Peace, null, new Rect(box.X, y, box.Width * progress, barH));
            y += barH + 4 * _unit;
        }

        foreach (var line in h.Verdicts)
            y = Label(line, box.X, y, _pal.Text, below: true, small: true, always: true) + 2 * _unit;
    }

    private void DrawBones(TrackingHandItem h, Brush lineBrush, Brush dotBrush)
    {
        var pts = new Point?[h.Points.Count];
        for (var i = 0; i < pts.Length; i++)
        {
            if (TryPoint(new NormPoint(h.Points[i].X, h.Points[i].Y), personLayer: false, out var p))
                pts[i] = p;
        }

        var linePen = new Pen(_sci ? Alpha(lineBrush, 0xB0) : lineBrush, (_sci ? 1.5 : 2.5) * _unit) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        foreach (var (a, b) in PeaceOverlay.Bones)
        {
            if (a < pts.Length && b < pts.Length && pts[a] is { } pa && pts[b] is { } pb)
                _dc.DrawLine(linePen, pa, pb);
        }

        for (var i = 0; i < pts.Length; i++)
        {
            if (pts[i] is not { } p)
                continue;
            var tip = i is 4 or 8 or 12 or 16 or 20;
            if (_sci)
            {
                if (tip)
                    Reticle(p, dotBrush, 6 * _unit);
                else
                    _dc.DrawEllipse(dotBrush, null, p, 2.5 * _unit, 2.5 * _unit);
            }
            else
            {
                _dc.DrawEllipse(dotBrush, null, p, (tip ? 5 : 4) * _unit, (tip ? 5 : 4) * _unit);
            }
        }
    }

    private void Heart(TrackingHeartItem h)
    {
        if (!TryPoint(h.Center, personLayer: false, out var c))
            return;
        var image = OverlayMapper.Map(NormRect.Full, _frame.GestureImageWidth, _frame.GestureImageHeight, _viewW, _viewH, _fit, _mirror);
        var size = Math.Clamp(h.SizeNorm * image.Height, 24 * _unit, Math.Max(24 * _unit, 0.5 * Math.Min(_viewW, _viewH)));
        if (!double.IsFinite(size))
            return;
        var r = new Rect(c.X - size / 2, c.Y - size / 2, size, size);
        var brush = _pal.Heart;
        if (_sci)
            Brackets(r, new Pen(brush, 3 * _unit), size * 0.25, ticks: true);
        else
            _dc.DrawRectangle(null, new Pen(brush, 3 * _unit) { DashStyle = DashStyles.Dash }, r);
        var progress = Math.Clamp(h.Progress, 0, 1);
        var barH = 7 * _unit;
        _dc.DrawRectangle(Alpha(Solid(Colors.White), 0x50), null, new Rect(r.X, r.Bottom + 4 * _unit, r.Width, barH));
        _dc.DrawRectangle(brush, null, new Rect(r.X, r.Bottom + 4 * _unit, r.Width * progress, barH));
        Label(string.Create(TrackingOverlayFrame.Culture, $"HERZ {ShapeName(h.Shape)}  {progress * 100:0} %"), r.X, r.Y, brush, below: false);
    }

    private void Pair(TrackingPairItem pair)
    {
        if (!TryPoint(pair.At, personLayer: false, out var p))
            return;
        var y = p.Y;
        foreach (var line in pair.Lines)
            y = Label(line, p.X, y, _pal.Heart, below: true, small: true, always: true) + 2 * _unit;
    }

    // Merker für Gesten, die sich nicht einer Hand zuordnen lassen (oben links, unter der Statuszeile).
    private void Flags()
    {
        if (!_opt.ShowGestures)
            return;
        var flags = new List<string>(3);
        if (_frame.PeaceNow)
            flags.Add("PEACE");
        if (_frame.MiddleFingerNow)
            flags.Add("MITTELFINGER");
        if (_frame.HeartNow)
            flags.Add("HERZ");
        if (flags.Count == 0)
            return;
        var y = (_opt.ShowStatusLine ? 112 : 44) * _base;
        foreach (var flag in flags)
            y = Label("● " + flag + " erkannt", 48 * _base, y, _pal.Flag, below: true, always: true) + 4 * _unit;
    }

    // ---------------------------------------------------------------- Zeichenhilfen

    private void Brackets(Rect r, Pen pen, double length, bool ticks)
    {
        length = Math.Max(6 * _unit, Math.Min(length, Math.Min(r.Width, r.Height) / 2));
        void Corner(double x, double y, int sx, int sy)
        {
            _dc.DrawLine(pen, new Point(x, y), new Point(x + sx * length, y));
            _dc.DrawLine(pen, new Point(x, y), new Point(x, y + sy * length));
        }
        Corner(r.X, r.Y, 1, 1);
        Corner(r.Right, r.Y, -1, 1);
        Corner(r.X, r.Bottom, 1, -1);
        Corner(r.Right, r.Bottom, -1, -1);
        if (!ticks)
            return;
        var t = 6 * _unit;
        var mx = r.X + r.Width / 2;
        var my = r.Y + r.Height / 2;
        _dc.DrawLine(pen, new Point(mx, r.Y), new Point(mx, r.Y + t));
        _dc.DrawLine(pen, new Point(mx, r.Bottom), new Point(mx, r.Bottom - t));
        _dc.DrawLine(pen, new Point(r.X, my), new Point(r.X + t, my));
        _dc.DrawLine(pen, new Point(r.Right, my), new Point(r.Right - t, my));
    }

    // Fadenkreuz mit Ring (SciFi).
    private void Reticle(NormPoint p, Brush brush, double radius)
    {
        if (TryPoint(p, personLayer: false, out var c))
            Reticle(c, brush, radius);
    }

    private void Reticle(Point c, Brush brush, double radius)
    {
        var pen = new Pen(brush, 1.5 * _unit);
        _dc.DrawEllipse(null, pen, c, radius, radius);
        var arm = radius * 1.6;
        _dc.DrawLine(pen, new Point(c.X - arm, c.Y), new Point(c.X - radius * 0.5, c.Y));
        _dc.DrawLine(pen, new Point(c.X + radius * 0.5, c.Y), new Point(c.X + arm, c.Y));
        _dc.DrawLine(pen, new Point(c.X, c.Y - arm), new Point(c.X, c.Y - radius * 0.5));
        _dc.DrawLine(pen, new Point(c.X, c.Y + radius * 0.5), new Point(c.X, c.Y + arm));
    }

    private void Dot(NormPoint p, Brush brush, Pen outline, double radius)
    {
        if (TryPoint(p, personLayer: false, out var c))
            _dc.DrawEllipse(brush, outline, c, radius, radius);
    }

    /// <summary>
    /// Beschriftung auf dunklem Grund. <paramref name="below"/> false: oberhalb von <paramref name="y"/> (passt es nicht in die Ansicht,
    /// darunter); true: ab <paramref name="y"/> nach unten. Gibt die untere Kante der Beschriftung zurück (0 ohne Beschriftungen).
    /// </summary>
    private double Label(string text, double x, double y, Brush fg, bool below, bool small = false, bool always = false)
    {
        if (!_opt.ShowLabels && !always)
            return y;
        if (string.IsNullOrEmpty(text) || !double.IsFinite(x) || !double.IsFinite(y))
            return y;
        var ft = new FormattedText(text, TrackingOverlayFrame.Culture, FlowDirection.LeftToRight, MonoFace,
            Math.Max(9, (small ? 12 : 14) * _unit), fg, _ppd) { MaxTextWidth = Math.Max(10, _viewW), MaxTextHeight = Math.Max(10, _viewH), Trimming = TextTrimming.None };
        var pad = 3 * _unit;
        var w = ft.WidthIncludingTrailingWhitespace + 2 * pad;
        var h = ft.Height + 2 * pad;
        var top = below ? y : y - h;
        if (top < 0)
            top = below ? y : y + 2 * _unit;
        var left = Math.Clamp(x, 0, Math.Max(0, _viewW - w));
        top = Math.Clamp(top, 0, Math.Max(0, _viewH - h));
        _dc.DrawRectangle(_pal.Chip, null, new Rect(left, top, w, h));
        _dc.DrawText(ft, new Point(left + pad, top + pad));
        return top + h;
    }


    private static string ShapeName(HeartShape shape) => shape switch
    {
        HeartShape.Classic => "KLASSISCH",
        HeartShape.Roof => "DACH",
        HeartShape.Inverted => "UMGEKEHRT",
        HeartShape.FingerHeart => "FINGERHERZ",
        _ => shape.ToString().ToUpperInvariant(),
    };

    // ---------------------------------------------------------------- Koordinaten

    private (double W, double H) ImageSize(bool personLayer) =>
        personLayer ? (_frame.PersonImageWidth, _frame.PersonImageHeight) : (_frame.GestureImageWidth, _frame.GestureImageHeight);

    private double ImageScale(bool personLayer)
    {
        var (w, h) = ImageSize(personLayer);
        if (!(w > 0) || !(h > 0) || !double.IsFinite(w) || !double.IsFinite(h))
        {
            w = OverlayMapper.DefaultAspect * 1000;
            h = 1000;
        }
        var sx = _viewW / w;
        var sy = _viewH / h;
        return _fit == OverlayFit.Uniform ? Math.Min(sx, sy) : Math.Max(sx, sy);
    }

    private bool TryPoint(NormPoint p, bool personLayer, out Point result)
    {
        result = default;
        if (!double.IsFinite(p.X) || !double.IsFinite(p.Y))
            return false;
        var (w, h) = ImageSize(personLayer);
        var m = OverlayMapper.MapPoint(p, w, h, _viewW, _viewH, _fit, _mirror);
        if (!double.IsFinite(m.X) || !double.IsFinite(m.Y) || Math.Abs(m.X) > FarLimit || Math.Abs(m.Y) > FarLimit)
            return false;
        result = new Point(m.X, m.Y);
        return true;
    }

    private bool TryRect(NormRect box, bool personLayer, out Rect result)
    {
        result = default;
        if (!double.IsFinite(box.X) || !double.IsFinite(box.Y) || !double.IsFinite(box.Width) || !double.IsFinite(box.Height))
            return false;
        var (w, h) = ImageSize(personLayer);
        var m = OverlayMapper.Map(box, w, h, _viewW, _viewH, _fit, _mirror);
        if (m.IsEmpty || Math.Abs(m.X) > FarLimit || Math.Abs(m.Y) > FarLimit || m.Width > FarLimit || m.Height > FarLimit)
            return false;
        result = new Rect(m.X, m.Y, m.Width, m.Height);
        return true;
    }

    // ---------------------------------------------------------------- Farben

    private static Brush Solid(Color color)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(color, out var brush))
            {
                var solid = new SolidColorBrush(color);
                solid.Freeze();
                Cache[color] = brush = solid;
            }
            return brush;
        }
    }

    private static Brush Alpha(Brush brush, byte alpha) =>
        brush is SolidColorBrush s ? Solid(Color.FromArgb(alpha, s.Color.R, s.Color.G, s.Color.B)) : brush;

    private static Brush Rgb(string hex) => Solid((Color)ColorConverter.ConvertFromString(hex));

    private sealed class Palette
    {
        public required Brush Person { get; init; }
        public required Brush Target { get; init; }
        public required Brush Face { get; init; }
        public required Brush Eye { get; init; }
        public required Brush Nose { get; init; }
        public required Brush Mouth { get; init; }
        public required Brush Hand { get; init; }
        public required Brush HandLine { get; init; }
        public required Brush Peace { get; init; }
        public required Brush Moustache { get; init; }
        public required Brush Heart { get; init; }
        public required Brush Text { get; init; }
        public required Brush Flag { get; init; }
        public required Brush Chip { get; init; }

        public static Palette Technik { get; } = new()
        {
            Person = Rgb("#4DD0E1"),
            Target = Rgb("#F5C26B"),
            Face = Rgb("#A5D6A7"),
            Eye = Rgb("#4FC3F7"),
            Nose = Rgb("#FFB74D"),
            Mouth = Rgb("#F06292"),
            Hand = Rgb("#FFD60A"),
            HandLine = Rgb("#FFFFFF"),
            Peace = Rgb("#3DDC84"),
            Moustache = Rgb("#FF9F43"),
            Heart = Rgb("#FF5C8A"),
            Text = Rgb("#EEF2FF"),
            Flag = Rgb("#FF8A80"),
            Chip = Rgb("#B3000000"),
        };

        public static Palette SciFi { get; } = new()
        {
            Person = Rgb("#00E5FF"),
            Target = Rgb("#FFC400"),
            Face = Rgb("#69F0AE"),
            Eye = Rgb("#69F0AE"),
            Nose = Rgb("#69F0AE"),
            Mouth = Rgb("#69F0AE"),
            Hand = Rgb("#B2FF59"),
            HandLine = Rgb("#B2FF59"),
            Peace = Rgb("#00E676"),
            Moustache = Rgb("#FF4DD2"),
            Heart = Rgb("#FF4081"),
            Text = Rgb("#CFFAFF"),
            Flag = Rgb("#FF8A80"),
            Chip = Rgb("#A0001018"),
        };
    }
}
