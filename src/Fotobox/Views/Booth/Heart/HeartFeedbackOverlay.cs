using System.Windows;
using System.Windows.Media;
using Fotobox.Core.Fun;
using Fotobox.Core.Heart;

namespace Fotobox.Views.Booth.Heart;

/// <summary>
/// Haltefortschritt der Herzgeste: ein Herz zwischen den Händen, weißer Umriss, von unten mit <see cref="HeartView.Progress"/> rosa
/// gefüllt. Koordinaten wie die übrigen Overlays (normiert, ungespiegelt, Spiegelung übernimmt <see cref="Mirror"/>). Nur Darstellung,
/// nimmt keine Eingaben an; ohne <see cref="Heart"/> bleibt es leer.
/// </summary>
public sealed class HeartFeedbackOverlay : FrameworkElement
{
    /// <summary>Kleinste und größte gezeichnete Kantenlänge (DIPs bzw. Anteil der kürzeren Ansichtskante).</summary>
    private const double MinSize = 56, MaxViewFraction = 0.5;

    private static FrameworkPropertyMetadata RenderMetadata(object? defaultValue) =>
        new(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender);

    public static readonly DependencyProperty HeartProperty = DependencyProperty.Register(
        nameof(Heart), typeof(HeartView), typeof(HeartFeedbackOverlay), RenderMetadata(null));

    /// <summary>Auflösung des Kamerabilds; 0 = unbekannt (dann 16:9).</summary>
    public static readonly DependencyProperty ImageWidthProperty = DependencyProperty.Register(
        nameof(ImageWidth), typeof(double), typeof(HeartFeedbackOverlay), RenderMetadata(0.0));

    public static readonly DependencyProperty ImageHeightProperty = DependencyProperty.Register(
        nameof(ImageHeight), typeof(double), typeof(HeartFeedbackOverlay), RenderMetadata(0.0));

    /// <summary>Wie das Kamerabild in die Ansicht passt (Standard wie das Livebild der Gastansicht: füllend).</summary>
    public static readonly DependencyProperty FitProperty = DependencyProperty.Register(
        nameof(Fit), typeof(OverlayFit), typeof(HeartFeedbackOverlay), RenderMetadata(OverlayFit.UniformToFill));

    public static readonly DependencyProperty MirrorProperty = DependencyProperty.Register(
        nameof(Mirror), typeof(bool), typeof(HeartFeedbackOverlay), RenderMetadata(false));

    private static readonly Brush BackBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0x20, 0x08, 0x18)));
    private static readonly Brush FillBrush = Frozen(new LinearGradientBrush(Color.FromRgb(0xFF, 0x8A, 0xB4), Color.FromRgb(0xFF, 0x3D, 0x7F), new Point(0, 0), new Point(0.6, 1)));
    private static readonly Brush WhiteBrush = Frozen(new SolidColorBrush(Colors.White));

    public HeartFeedbackOverlay()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
    }

    /// <summary>Das Herz mit Haltefortschritt; null = nichts zeichnen.</summary>
    public HeartView? Heart
    {
        get => (HeartView?)GetValue(HeartProperty);
        set => SetValue(HeartProperty, value);
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

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Heart is not { } heart)
            return;

        var viewW = ActualWidth;
        var viewH = ActualHeight;
        if (!(viewW > 0) || !(viewH > 0))
            return;

        var center = OverlayMapper.MapPoint(heart.Center, ImageWidth, ImageHeight, viewW, viewH, Fit, Mirror);
        var image = OverlayMapper.Map(NormRect.Full, ImageWidth, ImageHeight, viewW, viewH, Fit, Mirror);
        // SizeNorm = Kantenlänge relativ zur Bildhöhe.
        var size = Math.Clamp(heart.SizeNorm * image.Height, MinSize, Math.Max(MinSize, MaxViewFraction * Math.Min(viewW, viewH)));
        if (!double.IsFinite(size) || !double.IsFinite(center.X) || !double.IsFinite(center.Y))
            return;

        Draw(drawingContext, new Point(center.X, center.Y), size, Math.Clamp(heart.Progress, 0, 1));
    }

    private static void Draw(DrawingContext dc, Point center, double size, double progress)
    {
        var geometry = KisscamFrameRenderer.HeartGeometry;
        const double Height = 0.95; // Höhe des Herzens im Einheitsquadrat
        var origin = new Point(center.X - size / 2, center.Y - size * Height / 2);

        dc.PushTransform(new MatrixTransform(new Matrix(size, 0, 0, size, origin.X, origin.Y)));
        try
        {
            dc.DrawGeometry(BackBrush, null, geometry);
            if (progress > 0)
            {
                // Von unten gefüllt: sichtbar ist der untere Anteil „progress“ der Herzhöhe.
                var top = Height * (1 - progress);
                dc.PushClip(new RectangleGeometry(new Rect(-0.1, top, 1.2, Height - top + 0.1)));
                dc.DrawGeometry(FillBrush, null, geometry);
                dc.Pop();
            }

            var pen = new Pen(WhiteBrush, Math.Max(3, size * 0.05) / size) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }
        finally
        {
            dc.Pop();
        }
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
