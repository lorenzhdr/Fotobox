using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;

namespace Fotobox.Views.Booth.Peace;

/// <summary>
/// Zeichnet Bärte, den Haltefortschritt der Peace-Hand und (im Admin) die Handpunkte über dem Livebild (nur Darstellung, nimmt keine
/// Eingaben an). Zwischen zwei Aktualisierungen wandern Bärte und Hände linear zur neuen Lage (Zuordnung per Gesichts- bzw.
/// Hand-Id) – über den zuletzt gemessenen Abstand der Aktualisierungen (<see cref="MinGlideMs"/> bis <see cref="MaxGlideMs"/>, vor der
/// ersten Messung <see cref="DefaultGlideMs"/>), damit die Bewegung weder stehen bleibt noch der Lage hinterherläuft; der Takt (<see cref="CompositionTarget.Rendering"/>) ist nur angemeldet, solange etwas wandert und das
/// Element sichtbar und geladen ist.
/// </summary>
public sealed class PeaceOverlay : FrameworkElement
{
    /// <summary>Kürzeste und längste Gleitdauer sowie der Wert vor der ersten Messung (Millisekunden).</summary>
    public const double MinGlideMs = 40, MaxGlideMs = 200, DefaultGlideMs = 120;

    private static FrameworkPropertyMetadata RenderMetadata(object? defaultValue) =>
        new(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender);

    private static FrameworkPropertyMetadata RenderMetadata(object? defaultValue, PropertyChangedCallback changed) =>
        new(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender, changed);

    public static readonly DependencyProperty MoustachesProperty = DependencyProperty.Register(
        nameof(Moustaches), typeof(IReadOnlyList<MoustacheView>), typeof(PeaceOverlay),
        RenderMetadata(null, (d, _) => ((PeaceOverlay)d).OnMoustachesChanged()));

    public static readonly DependencyProperty HandsProperty = DependencyProperty.Register(
        nameof(Hands), typeof(IReadOnlyList<HandView>), typeof(PeaceOverlay),
        RenderMetadata(null, (d, _) => ((PeaceOverlay)d).OnHandsChanged()));

    /// <summary>Gesichter mit ihren fünf Punkten; gezeichnet nur mit <see cref="ShowFaceLandmarks"/> (Admin-Vorschau).</summary>
    public static readonly DependencyProperty FacesProperty = DependencyProperty.Register(
        nameof(Faces), typeof(IReadOnlyList<FaceLandmarks>), typeof(PeaceOverlay), RenderMetadata(null));

    public static readonly DependencyProperty ShowFaceLandmarksProperty = DependencyProperty.Register(
        nameof(ShowFaceLandmarks), typeof(bool), typeof(PeaceOverlay), RenderMetadata(false));

    /// <summary>Auflösung des Kamerabilds; 0 = unbekannt (dann 16:9).</summary>
    public static readonly DependencyProperty ImageWidthProperty = DependencyProperty.Register(
        nameof(ImageWidth), typeof(double), typeof(PeaceOverlay), RenderMetadata(0.0));

    public static readonly DependencyProperty ImageHeightProperty = DependencyProperty.Register(
        nameof(ImageHeight), typeof(double), typeof(PeaceOverlay), RenderMetadata(0.0));

    /// <summary>Wie das Kamerabild in die Ansicht passt (Standard wie das Livebild der Gastansicht: füllend).</summary>
    public static readonly DependencyProperty FitProperty = DependencyProperty.Register(
        nameof(Fit), typeof(OverlayFit), typeof(PeaceOverlay), RenderMetadata(OverlayFit.UniformToFill));

    public static readonly DependencyProperty MirrorProperty = DependencyProperty.Register(
        nameof(Mirror), typeof(bool), typeof(PeaceOverlay), RenderMetadata(false));

    public static readonly DependencyProperty ShowHoldProgressProperty = DependencyProperty.Register(
        nameof(ShowHoldProgress), typeof(bool), typeof(PeaceOverlay), RenderMetadata(false));

    public static readonly DependencyProperty ShowLandmarksProperty = DependencyProperty.Register(
        nameof(ShowLandmarks), typeof(bool), typeof(PeaceOverlay), RenderMetadata(false));

    private static readonly Brush GreenBrush = Frozen(Color.FromRgb(0x3D, 0xDC, 0x84));
    private static readonly Brush WhiteBrush = Frozen(Colors.White);
    private static readonly Brush YellowBrush = Frozen(Color.FromRgb(0xFF, 0xD6, 0x0A));
    private static readonly Brush EyeBrush = Frozen(Color.FromRgb(0x4F, 0xC3, 0xF7));
    private static readonly Brush NoseBrush = Frozen(Color.FromRgb(0xFF, 0xB7, 0x4D));
    private static readonly Brush MouthBrush = Frozen(Color.FromRgb(0xF0, 0x62, 0x92));
    private static readonly Brush BackRingBrush = Frozen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));

    /// <summary>Knochenlinien der 21 Handpunkte (MediaPipe).</summary>
    private static readonly (int A, int B)[] Bones =
    [
        (0, 1), (1, 2), (2, 3), (3, 4),
        (0, 5), (5, 6), (6, 7), (7, 8),
        (9, 10), (10, 11), (11, 12),
        (13, 14), (14, 15), (15, 16),
        (0, 17), (17, 18), (18, 19), (19, 20),
        (5, 9), (9, 13), (13, 17),
    ];

    private readonly Dictionary<int, Glide> _moustacheGlides = [];
    private readonly Dictionary<int, HandGlide> _handGlides = [];
    private long _moustacheGlideStart, _handGlideStart;
    private double _moustacheGlideMs = DefaultGlideMs, _handGlideMs = DefaultGlideMs;
    private long _lastMoustacheUpdate, _lastHandUpdate;
    private bool _subscribed;

    // Plopp (Wachsen beim ersten Erscheinen) und Wippen: nur im Livebild, pro Bild berechnet (der 10-Hz-Takt des Ansichtsmodells
    // reicht dafür nicht). Das Foto zeigt den Bart in Endgröße ohne beides.
    private readonly Dictionary<int, long> _popStart = [];
    private readonly HashSet<int> _popped = [];
    private readonly Dictionary<int, (WobbleState State, long At)> _wobble = [];

    /// <summary>Zeitquelle für Plopp und Wippen (Zähler wie <see cref="Stopwatch.GetTimestamp"/>); Tests setzen eine eigene.</summary>
    internal Func<long> Clock { get; set; } = Stopwatch.GetTimestamp;

    public PeaceOverlay()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => Unsubscribe();
        IsVisibleChanged += (_, _) => UpdateSubscription();
    }

    public IReadOnlyList<MoustacheView>? Moustaches
    {
        get => (IReadOnlyList<MoustacheView>?)GetValue(MoustachesProperty);
        set => SetValue(MoustachesProperty, value);
    }

    public IReadOnlyList<HandView>? Hands
    {
        get => (IReadOnlyList<HandView>?)GetValue(HandsProperty);
        set => SetValue(HandsProperty, value);
    }

    public IReadOnlyList<FaceLandmarks>? Faces
    {
        get => (IReadOnlyList<FaceLandmarks>?)GetValue(FacesProperty);
        set => SetValue(FacesProperty, value);
    }

    /// <summary>Zeichnet die fünf Gesichtspunkte (Augen, Nase, Mundwinkel) jedes Gesichts; nie in der Gastansicht.</summary>
    public bool ShowFaceLandmarks
    {
        get => (bool)GetValue(ShowFaceLandmarksProperty);
        set => SetValue(ShowFaceLandmarksProperty, value);
    }

    public double ImageWidth
    {
        get => (double)GetValue(ImageWidthProperty);
        set => SetValue(ImageWidthProperty, value);
    }

    public double ImageHeight
    {
        get => (double)GetValue(ImageHeightProperty);
        set => SetValue(ImageHeightProperty, value);
    }

    public OverlayFit Fit
    {
        get => (OverlayFit)GetValue(FitProperty);
        set => SetValue(FitProperty, value);
    }

    public bool Mirror
    {
        get => (bool)GetValue(MirrorProperty);
        set => SetValue(MirrorProperty, value);
    }

    public bool ShowHoldProgress
    {
        get => (bool)GetValue(ShowHoldProgressProperty);
        set => SetValue(ShowHoldProgressProperty, value);
    }

    public bool ShowLandmarks
    {
        get => (bool)GetValue(ShowLandmarksProperty);
        set => SetValue(ShowLandmarksProperty, value);
    }

    /// <summary>Aktuelle Gleitdauer der Bärte in Millisekunden (für Tests).</summary>
    internal double MoustacheGlideMs => _moustacheGlideMs;

    /// <summary>Ist der Animationstakt gerade angemeldet (für Tests).</summary>
    internal bool IsAnimating => _subscribed;

    // ---- Bewegung zwischen zwei Aktualisierungen ----

    /// <summary>Gleitdauer aus dem Abstand zweier Aktualisierungen: geglättet mit der vorigen Dauer und auf 40–200 ms begrenzt.</summary>
    internal static double GlideDuration(double previousMs, double intervalMs) =>
        Math.Clamp(0.5 * previousMs + 0.5 * intervalMs, MinGlideMs, MaxGlideMs);

    private static double Progress01(long start, double durationMs)
    {
        var elapsed = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        return Math.Clamp(elapsed / durationMs, 0, 1);
    }

    private double MoustacheProgress() => Progress01(_moustacheGlideStart, _moustacheGlideMs);

    private double HandProgress() => Progress01(_handGlideStart, _handGlideMs);

    private double Seconds(long since) => (Clock() - since) / (double)Stopwatch.Frequency;

    // Neu erschienene Bärte starten das Plopp (nur Spuren, die in dieser Sitzung noch nicht erschienen waren; eine Sitzung endet,
    // wenn die Liste leer wird); der Wippzustand wird mit dem Zeitpunkt der Aktualisierung verankert.
    private void UpdateMotion()
    {
        var views = Moustaches ?? [];
        if (views.Count == 0)
        {
            _popped.Clear();
            _popStart.Clear();
            _wobble.Clear();
            return;
        }

        var now = Clock();
        _wobble.Clear();
        foreach (var view in views)
        {
            if (_popped.Add(view.FaceId))
                _popStart[view.FaceId] = now;
            if (!view.Wobble.IsRest)
                _wobble[view.FaceId] = (view.Wobble, now);
        }
        foreach (var id in _popStart.Keys.Where(id => views.All(v => v.FaceId != id)).ToList())
            _popStart.Remove(id);
    }

    internal double PopScaleOf(int faceId) =>
        _popStart.TryGetValue(faceId, out var start) ? MoustachePop.Scale(Seconds(start)) : 1;

    internal WobbleState CurrentWobbleOf(int faceId) =>
        _wobble.TryGetValue(faceId, out var w) ? w.State.Advance(Seconds(w.At)) : WobbleState.Rest;

    // Läuft noch ein Plopp, oder schwingt ein Bart noch?
    private bool IsMoving()
    {
        foreach (var (_, start) in _popStart)
        {
            if (MoustachePop.IsRunning(Seconds(start)))
                return true;
        }
        foreach (var (id, _) in _wobble)
        {
            if (!CurrentWobbleOf(id).IsRest)
                return true;
        }
        return false;
    }

    private void OnMoustachesChanged()
    {
        UpdateMotion();
        var t = MoustacheProgress();
        var current = new Dictionary<int, MoustachePose>();
        foreach (var (id, glide) in _moustacheGlides)
            current[id] = Lerp(glide.From, glide.To, t);

        _moustacheGlides.Clear();
        var changed = false;
        foreach (var view in Moustaches ?? [])
        {
            var from = current.TryGetValue(view.FaceId, out var pose) ? pose : view.Pose;
            _moustacheGlides[view.FaceId] = new Glide(from, view.Pose);
            changed |= from != view.Pose;
        }
        if (changed)
        {
            var now = Stopwatch.GetTimestamp();
            if (_lastMoustacheUpdate != 0)
                _moustacheGlideMs = GlideDuration(_moustacheGlideMs, (now - _lastMoustacheUpdate) * 1000.0 / Stopwatch.Frequency);
            _lastMoustacheUpdate = now;
            _moustacheGlideStart = now;
        }
        UpdateSubscription();
    }

    private void OnHandsChanged()
    {
        var t = HandProgress();
        var current = new Dictionary<int, (HandPoint[] Points, NormRect Box)>();
        foreach (var (id, glide) in _handGlides)
            current[id] = (LerpPoints(glide.From, glide.To, t), LerpBox(glide.FromBox, glide.ToBox, t));

        _handGlides.Clear();
        var changed = false;
        foreach (var view in Hands ?? [])
        {
            var to = view.Points.ToArray();
            var from = to;
            var fromBox = view.Box;
            if (current.TryGetValue(view.TrackId, out var cur) && cur.Points.Length == to.Length)
            {
                from = cur.Points;
                fromBox = cur.Box;
                changed |= !from.AsSpan().SequenceEqual(to) || fromBox != view.Box;
            }
            _handGlides[view.TrackId] = new HandGlide(from, to, fromBox, view.Box);
        }
        if (changed)
        {
            var now = Stopwatch.GetTimestamp();
            if (_lastHandUpdate != 0)
                _handGlideMs = GlideDuration(_handGlideMs, (now - _lastHandUpdate) * 1000.0 / Stopwatch.Frequency);
            _lastHandUpdate = now;
            _handGlideStart = now;
            UpdateSubscription();
        }
    }

    // Läuft noch eine Bewegung (Bart oder Hand)?
    private bool IsGliding() =>
        (_moustacheGlides.Count > 0 && MoustacheProgress() < 1) || (_handGlides.Count > 0 && HandProgress() < 1) || IsMoving();

    private void UpdateSubscription()
    {
        var wanted = IsLoaded && IsVisible && IsGliding();
        if (wanted && !_subscribed)
        {
            CompositionTarget.Rendering += OnRendering;
            _subscribed = true;
        }
        else if (!wanted && _subscribed)
        {
            Unsubscribe();
        }
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
            return;
        CompositionTarget.Rendering -= OnRendering;
        _subscribed = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        InvalidateVisual();
        if (!IsGliding())
            Unsubscribe();
    }

    // ---- Zeichnen ----

    protected override void OnRender(DrawingContext drawingContext)
    {
        var viewW = ActualWidth;
        var viewH = ActualHeight;
        if (!(viewW > 0) || !(viewH > 0))
            return;

        var tMoustache = MoustacheProgress();
        var tHand = HandProgress();
        var unit = Math.Clamp(viewH / 1080.0, 0.45, 2.5);
        var imageW = ImageWidth;
        var imageH = ImageHeight;
        if (!(imageW > 0) || !(imageH > 0) || !double.IsFinite(imageW) || !double.IsFinite(imageH))
        {
            imageW = OverlayMapper.DefaultAspect * 1000;
            imageH = 1000;
        }
        var sx = viewW / imageW;
        var sy = viewH / imageH;
        var scale = Fit == OverlayFit.Uniform ? Math.Min(sx, sy) : Math.Max(sx, sy);

        foreach (var view in Moustaches ?? [])
        {
            var pose = _moustacheGlides.TryGetValue(view.FaceId, out var glide) ? Lerp(glide.From, glide.To, tMoustache) : view.Pose;
            var center = OverlayMapper.MapPoint(pose.Center, ImageWidth, ImageHeight, viewW, viewH, Fit, Mirror);
            var wobble = CurrentWobbleOf(view.FaceId);
            MoustacheRenderer.Draw(drawingContext, view.Style, view.Color, new Point(center.X, center.Y), pose.WidthPx * scale, pose.AngleRad, Mirror,
                PopScaleOf(view.FaceId), wobble.Offset, wobble.Angle);
        }

        if (ShowFaceLandmarks)
        {
            foreach (var face in Faces ?? [])
                DrawFacePoints(drawingContext, face, viewW, viewH, unit);
        }

        foreach (var hand in Hands ?? [])
        {
            var box = hand.Box;
            HandPoint[]? points = null;
            if (_handGlides.TryGetValue(hand.TrackId, out var glide))
            {
                box = LerpBox(glide.FromBox, glide.ToBox, tHand);
                points = LerpPoints(glide.From, glide.To, tHand);
            }

            if (ShowHoldProgress && hand.Progress > 0)
                DrawProgressRing(drawingContext, hand.Progress, box, imageW, imageH, viewW, viewH, unit);
            if (ShowLandmarks)
                DrawLandmarks(drawingContext, points ?? hand.Points.ToArray(), hand.IsPeace, viewW, viewH, unit);
        }
    }

    private void DrawProgressRing(DrawingContext dc, double progress, NormRect box, double imageW, double imageH, double viewW, double viewH, double unit)
    {
        var rect = OverlayMapper.Map(box, imageW, imageH, viewW, viewH, Fit, Mirror);
        if (rect.IsEmpty)
            return;

        var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        var radius = Math.Max(24 * unit, 0.6 * Math.Max(rect.Width, rect.Height));
        var thickness = 14 * unit;
        progress = Math.Clamp(progress, 0, 1);

        dc.DrawEllipse(null, new Pen(BackRingBrush, thickness), center, radius, radius);
        if (progress <= 0.001)
            return;

        var full = progress >= 0.999;
        var pen = new Pen(full ? GreenBrush : WhiteBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (full)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var start = new Point(center.X, center.Y - radius);
        var angle = progress * 2 * Math.PI - Math.PI / 2;
        var end = new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
        var arc = new StreamGeometry();
        using (var ctx = arc.Open())
        {
            ctx.BeginFigure(start, isFilled: false, isClosed: false);
            ctx.ArcTo(end, new Size(radius, radius), 0, isLargeArc: progress > 0.5, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        arc.Freeze();
        dc.DrawGeometry(null, pen, arc);
    }

    // Die fünf YuNet-Punkte: Augen (blau), Nase (orange), Mundwinkel (rosa), weiß umrandet.
    private void DrawFacePoints(DrawingContext dc, FaceLandmarks face, double viewW, double viewH, double unit)
    {
        var outline = new Pen(WhiteBrush, 2 * unit);
        void Dot(NormPoint p, Brush brush)
        {
            var m = OverlayMapper.MapPoint(p, ImageWidth, ImageHeight, viewW, viewH, Fit, Mirror);
            dc.DrawEllipse(brush, outline, new Point(m.X, m.Y), 6 * unit, 6 * unit);
        }

        Dot(face.RightEye, EyeBrush);
        Dot(face.LeftEye, EyeBrush);
        Dot(face.Nose, NoseBrush);
        Dot(face.MouthRight, MouthBrush);
        Dot(face.MouthLeft, MouthBrush);
    }

    private void DrawLandmarks(DrawingContext dc, HandPoint[] points, bool isPeace, double viewW, double viewH, double unit)
    {
        if (points.Length == 0)
            return;

        var mapped = new Point[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var p = OverlayMapper.MapPoint(new NormPoint(points[i].X, points[i].Y), ImageWidth, ImageHeight, viewW, viewH, Fit, Mirror);
            mapped[i] = new Point(p.X, p.Y);
        }

        var dotBrush = isPeace ? GreenBrush : YellowBrush;
        var linePen = new Pen(isPeace ? GreenBrush : WhiteBrush, 3 * unit) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        foreach (var (a, b) in Bones)
        {
            if (a < mapped.Length && b < mapped.Length)
                dc.DrawLine(linePen, mapped[a], mapped[b]);
        }
        foreach (var point in mapped)
            dc.DrawEllipse(dotBrush, null, point, 5 * unit, 5 * unit);
    }

    // ---- Hilfen ----

    private readonly record struct Glide(MoustachePose From, MoustachePose To);

    private readonly record struct HandGlide(HandPoint[] From, HandPoint[] To, NormRect FromBox, NormRect ToBox);

    private static MoustachePose Lerp(MoustachePose a, MoustachePose b, double t)
    {
        var delta = b.AngleRad - a.AngleRad;
        while (delta > Math.PI) delta -= 2 * Math.PI;
        while (delta < -Math.PI) delta += 2 * Math.PI;
        return new MoustachePose(
            new NormPoint(a.Center.X + (b.Center.X - a.Center.X) * t, a.Center.Y + (b.Center.Y - a.Center.Y) * t),
            a.WidthPx + (b.WidthPx - a.WidthPx) * t,
            a.AngleRad + delta * t);
    }

    private static HandPoint[] LerpPoints(HandPoint[] a, HandPoint[] b, double t)
    {
        var result = new HandPoint[b.Length];
        for (var i = 0; i < b.Length; i++)
        {
            var from = i < a.Length ? a[i] : b[i];
            result[i] = new HandPoint(from.X + (b[i].X - from.X) * t, from.Y + (b[i].Y - from.Y) * t, from.Z + (b[i].Z - from.Z) * t);
        }
        return result;
    }

    private static NormRect LerpBox(NormRect a, NormRect b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Width + (b.Width - a.Width) * t, a.Height + (b.Height - a.Height) * t);

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
