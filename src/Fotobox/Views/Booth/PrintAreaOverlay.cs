using System.Windows;
using System.Windows.Media;
using Fotobox.Core;
using Fotobox.Core.Fun;

namespace Fotobox.Views.Booth;

/// <summary>
/// Markiert im Livebild den Ausschnitt, der beim Drucken zu sehen ist („Druckbereich anzeigen“): Linien an den Schnittkanten, dazu
/// optional ein abgedunkelter Bereich außerhalb, oder vier Eckmarken. Koordinaten wie die übrigen Overlays: <see cref="Area"/> ist normiert
/// auf das ungespiegelte Kamerabild, <see cref="Mirror"/> und <see cref="Fit"/> übernehmen Spiegelung und Einpassung
/// (<see cref="OverlayMapper"/>). Reine Darstellung: nimmt keine Eingaben an (Klicks und Mausrad erreichen das Fenster) und liegt nie im
/// Foto. Ohne <see cref="Area"/> bleibt es leer.
/// </summary>
public sealed class PrintAreaOverlay : FrameworkElement
{
    /// <summary>Schenkellänge der Eckmarken als Anteil der kürzeren Seite des Druckbereichs, mindestens das Dreifache der Linienstärke.</summary>
    internal const double CornerLegFraction = 0.06;

    private static FrameworkPropertyMetadata RenderMetadata(object? defaultValue) =>
        new(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender);

    /// <summary>Druckbereich, normiert auf das ungespiegelte Kamerabild; null = nichts zeichnen.</summary>
    public static readonly DependencyProperty AreaProperty = DependencyProperty.Register(
        nameof(Area), typeof(NormRect?), typeof(PrintAreaOverlay), RenderMetadata(null));

    /// <summary>Auflösung des Kamerabilds; 0 = unbekannt (dann 16:9).</summary>
    public static readonly DependencyProperty ImageWidthProperty = DependencyProperty.Register(
        nameof(ImageWidth), typeof(double), typeof(PrintAreaOverlay), RenderMetadata(0.0));

    public static readonly DependencyProperty ImageHeightProperty = DependencyProperty.Register(
        nameof(ImageHeight), typeof(double), typeof(PrintAreaOverlay), RenderMetadata(0.0));

    /// <summary>Wie das Kamerabild in die Ansicht passt (Standard wie das Livebild der Gastansicht: füllend).</summary>
    public static readonly DependencyProperty FitProperty = DependencyProperty.Register(
        nameof(Fit), typeof(OverlayFit), typeof(PrintAreaOverlay), RenderMetadata(OverlayFit.UniformToFill));

    public static readonly DependencyProperty MirrorProperty = DependencyProperty.Register(
        nameof(Mirror), typeof(bool), typeof(PrintAreaOverlay), RenderMetadata(false));

    public static readonly DependencyProperty AreaStyleProperty = DependencyProperty.Register(
        nameof(AreaStyle), typeof(PrintAreaStyle), typeof(PrintAreaOverlay), RenderMetadata(PrintAreaStyle.LinesShaded));

    public static readonly DependencyProperty AreaColorProperty = DependencyProperty.Register(
        nameof(AreaColor), typeof(PrintAreaColor), typeof(PrintAreaOverlay), RenderMetadata(PrintAreaColor.White));

    public static readonly DependencyProperty OpacityPercentProperty = DependencyProperty.Register(
        nameof(OpacityPercent), typeof(int), typeof(PrintAreaOverlay), RenderMetadata(SettingsLimits.PrintAreaOpacityDefault));

    public static readonly DependencyProperty ThicknessPxProperty = DependencyProperty.Register(
        nameof(ThicknessPx), typeof(int), typeof(PrintAreaOverlay), RenderMetadata(SettingsLimits.PrintAreaThicknessDefault));

    public static readonly DependencyProperty ShadePercentProperty = DependencyProperty.Register(
        nameof(ShadePercent), typeof(int), typeof(PrintAreaOverlay), RenderMetadata(SettingsLimits.PrintAreaShadeDefault));

    public PrintAreaOverlay()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
    }

    public NormRect? Area
    {
        get => (NormRect?)GetValue(AreaProperty);
        set => SetValue(AreaProperty, value);
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

    /// <summary>Form der Markierung (nicht <c>Style</c>: der Name gehört <see cref="FrameworkElement"/>).</summary>
    public PrintAreaStyle AreaStyle
    {
        get => (PrintAreaStyle)GetValue(AreaStyleProperty);
        set => SetValue(AreaStyleProperty, value);
    }

    public PrintAreaColor AreaColor
    {
        get => (PrintAreaColor)GetValue(AreaColorProperty);
        set => SetValue(AreaColorProperty, value);
    }

    /// <summary>Deckkraft der Linien und Eckmarken in Prozent.</summary>
    public int OpacityPercent
    {
        get => (int)GetValue(OpacityPercentProperty);
        set => SetValue(OpacityPercentProperty, value);
    }

    /// <summary>Linienstärke in Bildschirmpunkten.</summary>
    public int ThicknessPx
    {
        get => (int)GetValue(ThicknessPxProperty);
        set => SetValue(ThicknessPxProperty, value);
    }

    /// <summary>Abdunklung außerhalb des Druckbereichs in Prozent (nur bei <see cref="PrintAreaStyle.LinesShaded"/>).</summary>
    public int ShadePercent
    {
        get => (int)GetValue(ShadePercentProperty);
        set => SetValue(ShadePercentProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Area is not { } area)
            return;

        var viewW = ActualWidth;
        var viewH = ActualHeight;
        if (!(viewW > 0) || !(viewH > 0))
            return;

        var rect = OverlayMapper.Map(area, ImageWidth, ImageHeight, viewW, viewH, Fit, Mirror);
        if (rect.IsEmpty || !IsFinite(rect))
            return;

        var image = OverlayMapper.Map(NormRect.Full, ImageWidth, ImageHeight, viewW, viewH, Fit, Mirror);
        // Die Einstellung ist auf 1–10 begrenzt (Normalizer); hier nur gegen unsinnige Werte abgesichert (die Admin-Skizze rechnet größer).
        var thickness = Math.Max(1, ThicknessPx);

        if (AreaStyle == PrintAreaStyle.LinesShaded)
        {
            var shade = Math.Clamp(ShadePercent, 0, 100) / 100.0;
            var outside = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, viewW, viewH)),
                new RectangleGeometry(new Rect(rect.X, rect.Y, rect.Width, rect.Height)));
            outside.Freeze();
            var shadeBrush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(shade * 255), 0, 0, 0));
            shadeBrush.Freeze();
            drawingContext.DrawGeometry(shadeBrush, null, outside);
        }

        var color = PrintAreaPalette.ColorOf(AreaColor);
        var alpha = (byte)Math.Round(Math.Clamp(OpacityPercent, 0, 100) / 100.0 * 255);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Miter, StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        pen.Freeze();

        foreach (var stroke in BuildStrokes(rect, image, AreaStyle, thickness))
        {
            var figure = new PathFigure { StartPoint = stroke[0], IsClosed = false };
            for (var i = 1; i < stroke.Length; i++)
                figure.Segments.Add(new LineSegment(stroke[i], true));
            var geometry = new PathGeometry([figure]);
            geometry.Freeze();
            drawingContext.DrawGeometry(null, pen, geometry);
        }
    }

    /// <summary>
    /// Die Striche der Markierung in Ansichtskoordinaten. Linien: eine je Kante des Druckbereichs, die nicht am Rand des Kamerabilds
    /// liegt (bei seitlichem Zuschnitt also zwei senkrechte, sonst zwei waagerechte). Eckmarken: vier L-Striche; Schenkel an einer
    /// Bildrandkante rücken um die halbe Linienstärke nach innen, damit sie nicht halb abgeschnitten werden.
    /// </summary>
    internal static List<Point[]> BuildStrokes(ViewRect rect, ViewRect image, PrintAreaStyle style, double thickness)
    {
        var strokes = new List<Point[]>();
        var (left, top, right, bottom) = InteriorEdges(rect, image);

        if (style == PrintAreaStyle.Corners)
        {
            var half = thickness / 2;
            var leg = Math.Max(CornerLegFraction * Math.Min(rect.Width, rect.Height), thickness * 3);
            leg = Math.Min(leg, Math.Min(rect.Width, rect.Height) / 2);

            var x0 = rect.X + (left ? 0 : half);
            var x1 = rect.Right - (right ? 0 : half);
            var y0 = rect.Y + (top ? 0 : half);
            var y1 = rect.Bottom - (bottom ? 0 : half);

            strokes.Add([new Point(x0 + leg, y0), new Point(x0, y0), new Point(x0, y0 + leg)]);
            strokes.Add([new Point(x1 - leg, y0), new Point(x1, y0), new Point(x1, y0 + leg)]);
            strokes.Add([new Point(x0 + leg, y1), new Point(x0, y1), new Point(x0, y1 - leg)]);
            strokes.Add([new Point(x1 - leg, y1), new Point(x1, y1), new Point(x1, y1 - leg)]);
            return strokes;
        }

        if (left)
            strokes.Add([new Point(rect.X, rect.Y), new Point(rect.X, rect.Bottom)]);
        if (right)
            strokes.Add([new Point(rect.Right, rect.Y), new Point(rect.Right, rect.Bottom)]);
        if (top)
            strokes.Add([new Point(rect.X, rect.Y), new Point(rect.Right, rect.Y)]);
        if (bottom)
            strokes.Add([new Point(rect.X, rect.Bottom), new Point(rect.Right, rect.Bottom)]);
        return strokes;
    }

    /// <summary>
    /// Welche Kanten des Druckbereichs liegen im Inneren des Kamerabilds (Abstand zur Bildkante über 1 Punkt)? Reihenfolge
    /// links, oben, rechts, unten – in Ansichtskoordinaten, also unabhängig von der Spiegelung.
    /// </summary>
    internal static (bool Left, bool Top, bool Right, bool Bottom) InteriorEdges(ViewRect rect, ViewRect image) =>
        (rect.X - image.X > 1, rect.Y - image.Y > 1, image.Right - rect.Right > 1, image.Bottom - rect.Bottom > 1);

    private static bool IsFinite(ViewRect r) =>
        double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Width) && double.IsFinite(r.Height);
}
