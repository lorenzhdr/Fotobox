using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;

namespace Fotobox.Views.Booth.Peace;

/// <summary>
/// Zeichnet Bärte, den Haltefortschritt der Peace-Hand und (im Admin) die Handpunkte über dem Livebild (nur Darstellung, nimmt keine
/// Eingaben an). Zwischen zwei Aktualisierungen wandern Bärte und Hände über rund 120 ms linear zur neuen Lage (Zuordnung per
/// Gesichts- bzw. Hand-Id); der Takt (<see cref="CompositionTarget.Rendering"/>) ist nur angemeldet, solange etwas wandert und das
/// Element sichtbar und geladen ist.
/// </summary>
public sealed class PeaceOverlay : FrameworkElement
{
    private const double InterpolationMs = 120;

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
    private long _glideStart;
    private bool _subscribed;

    public PeaceOverlay()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
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

    /// <summary>Ist der Animationstakt gerade angemeldet (für Tests).</summary>
    internal bool IsAnimating => _subscribed;

    // ---- Bewegung zwischen zwei Aktualisierungen ----

    private double Progress01()
    {
        var elapsed = (Stopwatch.GetTimestamp() - _glideStart) * 1000.0 / Stopwatch.Frequency;
        return Math.Clamp(elapsed / InterpolationMs, 0, 1);
    }

    private void OnMoustachesChanged()
    {
        var t = Progress01();
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
        RestartGlide(changed);
    }

    private void OnHandsChanged()
    {
        var t = Progress01();
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
        RestartGlide(changed);
    }

    private void RestartGlide(bool changed)
    {
        if (!changed)
            return;
        _glideStart = Stopwatch.GetTimestamp();
        UpdateSubscription();
    }

    private void UpdateSubscription()
    {
        var wanted = IsLoaded && IsVisible && (_moustacheGlides.Count > 0 || _handGlides.Count > 0) && Progress01() < 1;
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
        if (Progress01() >= 1)
            Unsubscribe();
    }

    // ---- Zeichnen ----

    protected override void OnRender(DrawingContext drawingContext)
    {
        var viewW = ActualWidth;
        var viewH = ActualHeight;
        if (!(viewW > 0) || !(viewH > 0))
            return;

        var t = Progress01();
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
            var pose = _moustacheGlides.TryGetValue(view.FaceId, out var glide) ? Lerp(glide.From, glide.To, t) : view.Pose;
            var center = OverlayMapper.MapPoint(pose.Center, ImageWidth, ImageHeight, viewW, viewH, Fit, Mirror);
            MoustacheRenderer.Draw(drawingContext, view.Style, new Point(center.X, center.Y), pose.WidthPx * scale, pose.AngleRad, Mirror);
        }

        foreach (var hand in Hands ?? [])
        {
            var box = hand.Box;
            HandPoint[]? points = null;
            if (_handGlides.TryGetValue(hand.TrackId, out var glide))
            {
                box = LerpBox(glide.FromBox, glide.ToBox, t);
                points = LerpPoints(glide.From, glide.To, t);
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
