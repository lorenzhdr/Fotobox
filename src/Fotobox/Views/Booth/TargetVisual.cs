using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Fotobox.Core;
using Fotobox.Core.Fun;

namespace Fotobox.Views.Booth;

/// <summary>
/// Eine Zielbox samt Beschriftung „TARGET n“, vollständig per <see cref="OnRender"/> gezeichnet (kein Schatten-Effekt: Das Leuchten
/// ist ein zweiter, breiterer, halbtransparenter Strich). Das Element füllt genau die Box; die Beschriftung liegt über der oberen
/// Kante (bzw. innen, wenn dort kein Platz ist) und darf deshalb über die Elementgrenzen hinausragen. Die Einrast-Animationen
/// laufen als Animationen auf <see cref="Pulse"/>, <see cref="ScanProgress"/>, <c>Opacity</c> und der Skalierung des Elements und
/// enden von selbst (keine Dauer-Animation, außer dem Blinken von „LOCKING…“, das mit dem Einrasten endet).
/// </summary>
internal sealed class TargetVisual : FrameworkElement
{
    private const double GlowFactor = 3.2;

    public static readonly DependencyProperty BoxStyleProperty = DependencyProperty.Register(
        nameof(BoxStyle), typeof(TargetBoxStyle), typeof(TargetVisual),
        new FrameworkPropertyMetadata(TargetBoxStyle.Corners, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ColorKindProperty = DependencyProperty.Register(
        nameof(ColorKind), typeof(TargetColor), typeof(TargetVisual),
        new FrameworkPropertyMetadata(TargetColor.Red, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Puls 0..1 (Strichstärke, Leuchten, leichtes Aufblähen); 0 im Ruhezustand.</summary>
    public static readonly DependencyProperty PulseProperty = DependencyProperty.Register(
        nameof(Pulse), typeof(double), typeof(TargetVisual),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Lage der Scan-Linie 0..1 von oben nach unten; negativ = keine Linie.</summary>
    public static readonly DependencyProperty ScanProgressProperty = DependencyProperty.Register(
        nameof(ScanProgress), typeof(double), typeof(TargetVisual),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Angezeigte Beschriftung (während des Einrastens „LOCKING…“).</summary>
    public static readonly DependencyProperty DisplayedLabelProperty = DependencyProperty.Register(
        nameof(DisplayedLabel), typeof(string), typeof(TargetVisual),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelInsideProperty = DependencyProperty.Register(
        nameof(LabelInside), typeof(bool), typeof(TargetVisual),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelOpacityProperty = DependencyProperty.Register(
        nameof(LabelOpacity), typeof(double), typeof(TargetVisual),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Maßstab gegenüber 1080p (Strichstärke, Schriftgröße).</summary>
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(double), typeof(TargetVisual),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly ScaleTransform _scale = new(1, 1);
    private FormattedText? _labelText;
    private string? _labelKey;

    public TargetVisual(int id)
    {
        Id = id;
        IsHitTestVisible = false;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _scale;
    }

    public int Id { get; }

    /// <summary>Ziel-Rechteck in Ansichtskoordinaten (die Position selbst wird weich nachgeführt).</summary>
    public ViewRect Target { get; set; }

    /// <summary>Beschriftung ohne Einrast-Zustand („TARGET n“).</summary>
    public string LabelText { get; set; } = "";

    /// <summary>Beginn des Einrastens, für das zuletzt eine Animation lief (verhindert doppelten Start).</summary>
    public long LockKey { get; set; }

    public bool IsLocking { get; private set; }

    public TargetBoxStyle BoxStyle { get => (TargetBoxStyle)GetValue(BoxStyleProperty); set => SetValue(BoxStyleProperty, value); }
    public TargetColor ColorKind { get => (TargetColor)GetValue(ColorKindProperty); set => SetValue(ColorKindProperty, value); }
    public double Pulse { get => (double)GetValue(PulseProperty); set => SetValue(PulseProperty, value); }
    public double ScanProgress { get => (double)GetValue(ScanProgressProperty); set => SetValue(ScanProgressProperty, value); }
    public string DisplayedLabel { get => (string)GetValue(DisplayedLabelProperty); set => SetValue(DisplayedLabelProperty, value); }
    public bool LabelInside { get => (bool)GetValue(LabelInsideProperty); set => SetValue(LabelInsideProperty, value); }
    public double LabelOpacity { get => (double)GetValue(LabelOpacityProperty); set => SetValue(LabelOpacityProperty, value); }
    public double Unit { get => (double)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    /// <summary>Höhe der Beschriftung samt Hintergrund (für die Entscheidung oben/innen).</summary>
    public static double LabelHeight(double unit) => Math.Round(LabelFontSize(unit) * 1.25 + 8 * unit);

    private static double LabelFontSize(double unit) => Math.Max(11, 22 * unit);

    // ---- Zustand und Animationen

    /// <summary>Setzt Beschriftung und Blinken je nach Einrast-Zustand.</summary>
    public void SetLocking(bool locking)
    {
        if (locking == IsLocking)
            return;
        IsLocking = locking;
        DisplayedLabel = locking ? "LOCKING…" : LabelText;
        if (locking)
        {
            var blink = new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(180))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            BeginAnimation(LabelOpacityProperty, blink);
        }
        else
        {
            BeginAnimation(LabelOpacityProperty, null);
            LabelOpacity = 1;
        }
    }

    /// <summary>Alle Animationen beenden und in den Ruhezustand zurück.</summary>
    public void StopAnimations()
    {
        BeginAnimation(PulseProperty, null);
        BeginAnimation(ScanProgressProperty, null);
        BeginAnimation(LabelOpacityProperty, null);
        BeginAnimation(OpacityProperty, null);
        BeginAnimation(System.Windows.Controls.Canvas.LeftProperty, null);
        BeginAnimation(System.Windows.Controls.Canvas.TopProperty, null);
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _scale.ScaleX = _scale.ScaleY = 1;
        Pulse = 0;
        ScanProgress = -1;
        LabelOpacity = 1;
        Opacity = 1;
    }

    /// <summary>Einrast-Animation der gewählten Art (Dauer insgesamt ca. <see cref="FunEngine.LockOnDuration"/>).</summary>
    public void StartLockOn(LockOnAnimation animation)
    {
        BeginAnimation(PulseProperty, null);
        BeginAnimation(ScanProgressProperty, null);
        BeginAnimation(OpacityProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        switch (animation)
        {
            case LockOnAnimation.Pulse:
                BeginAnimation(PulseProperty, PulseAnimation(0, 0.2));
                break;

            case LockOnAnimation.ScanPulse:
                var scan = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
                scan.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                scan.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.5))));
                BeginAnimation(ScanProgressProperty, scan);
                BeginAnimation(PulseProperty, PulseAnimation(0.5, 0.7 / 6));
                break;

            default: // ShrinkPulse
                var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                var shrink = new DoubleAnimation(1.8, 1, TimeSpan.FromSeconds(0.4)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
                _scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
                _scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink.Clone());
                BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.3)) { FillBehavior = FillBehavior.Stop });
                BeginAnimation(PulseProperty, PulseAnimation(0.4, 0.8 / 6));
                break;
        }
    }

    // Drei Pulse (hoch und wieder zurück); „half“ ist die Dauer des Anstiegs in Sekunden.
    private static DoubleAnimation PulseAnimation(double beginSeconds, double half) =>
        new(0, 1, TimeSpan.FromSeconds(half))
        {
            BeginTime = TimeSpan.FromSeconds(beginSeconds),
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3),
            FillBehavior = FillBehavior.Stop,
        };

    // ---- Zeichnen

    /// <summary>Farbe einer <see cref="TargetColor"/>.</summary>
    internal static Color ResolveColor(TargetColor color) => color switch
    {
        TargetColor.Green => Color.FromRgb(0x39, 0xFF, 0x14),
        TargetColor.Cyan => Color.FromRgb(0x00, 0xE5, 0xFF),
        TargetColor.Yellow => Color.FromRgb(0xFF, 0xD6, 0x0A),
        TargetColor.White => Color.FromRgb(0xFF, 0xFF, 0xFF),
        _ => Color.FromRgb(0xFF, 0x3B, 0x30),
    };

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        var unit = Math.Max(0.35, Unit);
        var color = ResolveColor(ColorKind);
        var pulse = Math.Clamp(Pulse, 0, 1);
        var thickness = 3 * unit * (1 + 0.8 * pulse);
        var glowAlpha = (byte)Math.Round(255 * (0.22 + 0.4 * pulse));

        var main = MakePen(color, 255, thickness);
        var glow = MakePen(color, glowAlpha, thickness * GlowFactor);

        // Beim Puls leicht aufblähen (um die Mitte), die Beschriftung bleibt an der Box.
        var inflate = pulse * 0.02 * Math.Min(w, h);
        var rect = new Rect(thickness / 2 - inflate, thickness / 2 - inflate,
            Math.Max(1, w - thickness + 2 * inflate), Math.Max(1, h - thickness + 2 * inflate));

        var geometry = BuildGeometry(rect, BoxStyle);
        dc.DrawGeometry(null, glow, geometry);
        dc.DrawGeometry(null, main, geometry);

        if (BoxStyle == TargetBoxStyle.CornersCrosshair)
        {
            var cross = BuildCrosshair(rect, unit);
            dc.DrawGeometry(null, glow, cross);
            dc.DrawGeometry(null, MakePen(color, 255, Math.Max(1.5, thickness * 0.7)), cross);
        }

        if (ScanProgress >= 0)
            DrawScan(dc, w, h, color, unit);

        DrawLabel(dc, w, unit, color);
    }

    private static Pen MakePen(Color color, byte alpha, double thickness)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Flat,
            EndLineCap = PenLineCap.Flat,
            LineJoin = PenLineJoin.Miter,
        };
        pen.Freeze();
        return pen;
    }

    private static Geometry BuildGeometry(Rect r, TargetBoxStyle style)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            if (style == TargetBoxStyle.Rectangle)
            {
                ctx.BeginFigure(r.TopLeft, false, true);
                ctx.LineTo(r.TopRight, true, false);
                ctx.LineTo(r.BottomRight, true, false);
                ctx.LineTo(r.BottomLeft, true, false);
            }
            else
            {
                var len = Math.Max(6, 0.22 * Math.Min(r.Width, r.Height));
                Corner(ctx, r.TopLeft, new Vector(len, 0), new Vector(0, len));
                Corner(ctx, r.TopRight, new Vector(-len, 0), new Vector(0, len));
                Corner(ctx, r.BottomRight, new Vector(-len, 0), new Vector(0, -len));
                Corner(ctx, r.BottomLeft, new Vector(len, 0), new Vector(0, -len));
            }
        }
        geometry.Freeze();
        return geometry;
    }

    // Eine L-Ecke: von „a“ über den Eckpunkt nach „b“.
    private static void Corner(StreamGeometryContext ctx, Point corner, Vector a, Vector b)
    {
        ctx.BeginFigure(corner + a, false, false);
        ctx.LineTo(corner, true, false);
        ctx.LineTo(corner + b, true, false);
    }

    // Kleines Fadenkreuz mit Lücke in der Mitte; bei hohen (Ganzkörper-)Boxen im Kopfbereich, sonst in der Mitte.
    private static Geometry BuildCrosshair(Rect r, double unit)
    {
        var cx = r.X + r.Width / 2;
        var cy = r.Height > 1.6 * r.Width ? r.Y + r.Height * 0.18 : r.Y + r.Height / 2;
        var arm = Math.Clamp(0.07 * Math.Min(r.Width, r.Height), 9 * unit, 30 * unit);
        var gap = arm * 0.3;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(cx - arm, cy), false, false);
            ctx.LineTo(new Point(cx - gap, cy), true, false);
            ctx.BeginFigure(new Point(cx + gap, cy), false, false);
            ctx.LineTo(new Point(cx + arm, cy), true, false);
            ctx.BeginFigure(new Point(cx, cy - arm), false, false);
            ctx.LineTo(new Point(cx, cy - gap), true, false);
            ctx.BeginFigure(new Point(cx, cy + gap), false, false);
            ctx.LineTo(new Point(cx, cy + arm), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private void DrawScan(DrawingContext dc, double w, double h, Color color, double unit)
    {
        var y = Math.Clamp(ScanProgress, 0, 1) * h;
        var trail = Math.Min(y, 0.3 * h);
        if (trail > 1)
        {
            var fade = new LinearGradientBrush(Color.FromArgb(0, color.R, color.G, color.B),
                Color.FromArgb(0x48, color.R, color.G, color.B), 90);
            fade.Freeze();
            dc.DrawRectangle(fade, null, new Rect(0, y - trail, w, trail));
        }
        dc.DrawLine(MakePen(color, 0x66, 9 * unit), new Point(0, y), new Point(w, y));
        dc.DrawLine(MakePen(color, 255, 2.5 * unit), new Point(0, y), new Point(w, y));
    }

    private void DrawLabel(DrawingContext dc, double w, double unit, Color color)
    {
        var text = DisplayedLabel;
        if (string.IsNullOrEmpty(text))
            return;

        var fontSize = LabelFontSize(unit);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var key = FormattableString.Invariant($"{text}|{fontSize}|{dpi}|{color}");
        if (_labelText is null || _labelKey != key)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            _labelText = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Consolas, Cascadia Mono, Courier New"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                fontSize, brush, dpi);
            _labelKey = key;
        }

        var padH = 8 * unit;
        var height = LabelHeight(unit);
        var width = _labelText.WidthIncludingTrailingWhitespace + 2 * padH;
        var top = LabelInside ? 8 * unit : -height;
        var background = new SolidColorBrush(Color.FromArgb(0xB8, 0x08, 0x0B, 0x10));
        background.Freeze();

        var opacity = Math.Clamp(LabelOpacity, 0, 1);
        if (opacity < 1)
            dc.PushOpacity(opacity);
        // Bei „innen“ sitzt das Etikett etwas eingerückt, damit es die Eckklammer nicht überdeckt.
        var left = LabelInside ? 6 * unit : 0;
        dc.DrawRectangle(background, null, new Rect(left, top, width, height));
        dc.DrawText(_labelText, new Point(left + padH, top + (height - _labelText.Height) / 2));
        if (opacity < 1)
            dc.Pop();
    }
}
