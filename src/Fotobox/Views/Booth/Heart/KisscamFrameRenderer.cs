using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Fotobox.Core.Heart;
using Fotobox.Core.Peace;

namespace Fotobox.Views.Booth.Heart;

/// <summary>
/// Zeichnet den Jumbotron-Rahmen der Kisscam: Rahmenband mit Lichterkette, Banner mit Text und vier Eckherzen. Wird live (im
/// <c>OnRender</c> eines Elements) und im Foto (im <c>DrawingVisual</c> des Layouts) benutzt; alle Maße kommen aus
/// <see cref="KisscamFrameGeometry"/>. Die Herzen sind Bézier-Geometrien, keine Emojis (die würden in einer RenderTargetBitmap nicht farbig).
/// </summary>
public static class KisscamFrameRenderer
{
    /// <summary>Kleinster Schriftanteil des Banner-Textes, danach wird mit „…“ gekürzt.</summary>
    public const double MinTextScale = 0.5;

    private static readonly Typeface BannerFace =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Herz im Einheitsquadrat (Breite 1, Höhe 0,95), zwei Lappen und Spitze unten.</summary>
    private static readonly Geometry HeartUnit = CreateHeart();

    /// <summary>Dieselbe Herzform (eingefroren, Einheitsquadrat, Höhe 0,95) für weitere Zeichner, z. B. die Haltefortschrittsanzeige.</summary>
    internal static Geometry HeartGeometry => HeartUnit;

    /// <summary>
    /// Zeichnet den Rahmen in <paramref name="area"/>. <paramref name="dpi"/> = Auflösung der Zielfläche (96 für das Livebild bzw. eine
    /// <see cref="RenderTargetBitmap"/> mit 96 dpi), bestimmt nur die Pixelausrichtung des Textes. Wirft bei gültigen Eingaben nie.
    /// </summary>
    public static void Draw(DrawingContext dc, Rect area, PhotoFrame frame, double dpi)
    {
        ArgumentNullException.ThrowIfNull(dc);
        ArgumentNullException.ThrowIfNull(frame);
        if (area.Width <= 0 || area.Height <= 0 || !double.IsFinite(area.Width) || !double.IsFinite(area.Height))
            return;

        var ppd = dpi > 0 && double.IsFinite(dpi) ? dpi / 96.0 : 1.0;
        var colors = KisscamPalette.For(frame.Color);
        var text = (frame.BannerText ?? string.Empty).Trim();

        // Text zuerst messen: Das Banner wächst mit dem Text (bis zur Obergrenze der Geometrie).
        var geo = KisscamFrameGeometry.For(area.Width, area.Height);
        FormattedText? label = null;
        if (text.Length > 0 && geo.FontEm > 0)
        {
            var probe = MakeText(text, geo.FontEm, colors.Text, ppd);
            geo = KisscamFrameGeometry.For(area.Width, area.Height, probe.WidthIncludingTrailingWhitespace + 2 * geo.BannerPadding);
            label = FitText(text, geo, colors.Text, ppd);
        }

        dc.PushTransform(new TranslateTransform(area.X, area.Y));
        try
        {
            DrawBand(dc, geo, colors);
            DrawLights(dc, geo, colors);
            DrawHearts(dc, geo, colors);
            if (label is not null)
                DrawBanner(dc, geo, colors, label);
        }
        finally
        {
            dc.Pop();
        }
    }

    // ---------- Rahmenband ----------

    private static void DrawBand(DrawingContext dc, KisscamFrameGeometry geo, KisscamColors c)
    {
        var t = geo.Thickness;
        if (t <= 0)
            return;

        var outer = new Rect(0, 0, geo.Width, geo.Height);
        var inner = new Rect(t, t, Math.Max(0, geo.Width - 2 * t), Math.Max(0, geo.Height - 2 * t));
        var band = new GeometryGroup { FillRule = FillRule.EvenOdd };
        band.Children.Add(new RectangleGeometry(outer));
        band.Children.Add(new RectangleGeometry(inner));
        band.Freeze();
        dc.DrawGeometry(Solid(c.Frame), null, band);

        // Weicher Schatten des Rahmens nach innen (Tiefe), danach die helle Innenkante.
        var shade = Math.Min(t, Math.Min(inner.Width, inner.Height) / 2);
        if (shade > 0)
        {
            var dark = Color.FromArgb(120, c.FrameDark.R, c.FrameDark.G, c.FrameDark.B);
            var clear = Color.FromArgb(0, c.FrameDark.R, c.FrameDark.G, c.FrameDark.B);
            dc.DrawRectangle(Gradient(dark, clear, false), null, new Rect(inner.X, inner.Y, inner.Width, shade));
            dc.DrawRectangle(Gradient(clear, dark, false), null, new Rect(inner.X, inner.Bottom - shade, inner.Width, shade));
            dc.DrawRectangle(Gradient(dark, clear, true), null, new Rect(inner.X, inner.Y, shade, inner.Height));
            dc.DrawRectangle(Gradient(clear, dark, true), null, new Rect(inner.Right - shade, inner.Y, shade, inner.Height));
        }

        var line = t * 0.12;
        var pen = new Pen(Solid(c.Accent), line);
        pen.Freeze();
        dc.DrawRectangle(null, pen, new Rect(inner.X - line / 2, inner.Y - line / 2, inner.Width + line, inner.Height + line));
    }

    private static void DrawLights(DrawingContext dc, KisscamFrameGeometry geo, KisscamColors c)
    {
        var r = geo.LightRadius;
        if (r <= 0 || geo.Lights.Count == 0)
            return;

        var halo = new RadialGradientBrush(
            [new GradientStop(Color.FromArgb(150, c.Accent.R, c.Accent.G, c.Accent.B), 0.25), new GradientStop(Color.FromArgb(0, c.Accent.R, c.Accent.G, c.Accent.B), 1)]);
        halo.Freeze();
        var coreA = Solid(Mix(c.Accent, Colors.White, 0.5));
        var coreB = Solid(Mix(c.Heart, Colors.White, 0.35));
        for (var i = 0; i < geo.Lights.Count; i++)
        {
            var p = geo.Lights[i];
            dc.DrawEllipse(halo, null, new Point(p.X, p.Y), r * 2.6, r * 2.6);
            dc.DrawEllipse(i % 2 == 0 ? coreA : coreB, null, new Point(p.X, p.Y), r, r);
        }
    }

    // ---------- Banner ----------

    private static void DrawBanner(DrawingContext dc, KisscamFrameGeometry geo, KisscamColors c, FormattedText label)
    {
        var b = new Rect(geo.Banner.X, geo.Banner.Y, geo.Banner.Width, geo.Banner.Height);
        if (b.Width <= 0 || b.Height <= 0)
            return;

        var radius = Math.Min(geo.BannerCornerRadius, Math.Min(b.Width, b.Height) / 2);
        var shadowOffset = geo.Thickness * 0.5;
        dc.DrawRoundedRectangle(Solid(Color.FromArgb(100, 0, 0, 0)), null,
            new Rect(b.X, b.Y + shadowOffset, b.Width, b.Height), radius, radius);

        var fill = Gradient(Mix(c.Banner, Colors.White, 0.28), c.Banner, false);
        var edge = new Pen(Solid(c.BannerEdge), Math.Max(1, geo.Thickness * 0.22)) { LineJoin = PenLineJoin.Round };
        edge.Freeze();
        dc.DrawRoundedRectangle(fill, edge, b, radius, radius);

        var origin = new Point(b.X + (b.Width - label.Width) / 2, b.Y + (b.Height - label.Height) / 2);
        // Heller Text bekommt einen dezenten Schatten, dunkler (Gold) nicht.
        if (Luminance(c.Text) > 0.5)
        {
            var shadow = label.Height * 0.035;
            label.SetForegroundBrush(Solid(Color.FromArgb(120, 0, 0, 0)));
            dc.DrawText(label, new Point(origin.X + shadow, origin.Y + shadow * 1.4));
            label.SetForegroundBrush(Solid(c.Text));
        }

        dc.DrawText(label, origin);
    }

    /// <summary>
    /// Einzeiliger Text in der Bannerbreite: passt er nicht, wird schrittweise bis <see cref="MinTextScale"/> verkleinert, danach mit „…“
    /// gekürzt.
    /// </summary>
    private static FormattedText FitText(string text, KisscamFrameGeometry geo, Color color, double ppd)
    {
        var available = Math.Max(1, geo.Banner.Width - 2 * geo.BannerPadding);
        var em = geo.FontEm;
        var min = em * MinTextScale;
        var current = em;
        while (true)
        {
            var probe = MakeText(text, current, color, ppd);
            if (probe.WidthIncludingTrailingWhitespace <= available + 0.5 || current <= min)
                break;
            current = Math.Max(min, current * 0.94);
        }

        var result = MakeText(text, current, color, ppd);
        if (result.WidthIncludingTrailingWhitespace > available + 0.5)
        {
            result.MaxTextWidth = available;
            result.MaxLineCount = 1;
            result.Trimming = TextTrimming.CharacterEllipsis;
        }

        return result;
    }

    private static FormattedText MakeText(string text, double em, Color color, double ppd) =>
        new(text, German, FlowDirection.LeftToRight, BannerFace, Math.Max(1, em), Solid(color), ppd);

    // ---------- Eckherzen ----------

    private static void DrawHearts(DrawingContext dc, KisscamFrameGeometry geo, KisscamColors c)
    {
        var fill = new LinearGradientBrush(Mix(c.Heart, Colors.White, 0.35), c.Heart, new Point(0, 0), new Point(0.7, 1));
        fill.Freeze();
        var shadow = Solid(Color.FromArgb(90, 0, 0, 0));
        var edge = new Pen(Solid(c.HeartEdge), 0.06) { LineJoin = PenLineJoin.Round };
        edge.Freeze();
        var highlight = Solid(Color.FromArgb(150, 255, 255, 255));

        foreach (var r in geo.CornerHearts)
        {
            if (r.Width <= 0)
                continue;
            var size = r.Width;
            var offset = new Matrix(size, 0, 0, size, r.X, r.Y + size * 0.025);
            dc.PushTransform(new MatrixTransform(offset));
            dc.PushTransform(new TranslateTransform(0.035, 0.05));
            dc.DrawGeometry(shadow, null, HeartUnit);
            dc.Pop();
            dc.DrawGeometry(fill, edge, HeartUnit);
            dc.DrawEllipse(highlight, null, new Point(0.27, 0.22), 0.11, 0.07);
            dc.Pop();
        }
    }

    private static Geometry CreateHeart()
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(0.5, 0.95), true, true);
            ctx.BezierTo(new Point(0.18, 0.72), new Point(0.0, 0.50), new Point(0.0, 0.30), true, true);
            ctx.BezierTo(new Point(0.0, 0.12), new Point(0.14, 0.0), new Point(0.27, 0.0), true, true);
            ctx.BezierTo(new Point(0.40, 0.0), new Point(0.48, 0.10), new Point(0.5, 0.22), true, true);
            ctx.BezierTo(new Point(0.52, 0.10), new Point(0.60, 0.0), new Point(0.73, 0.0), true, true);
            ctx.BezierTo(new Point(0.86, 0.0), new Point(1.0, 0.12), new Point(1.0, 0.30), true, true);
            ctx.BezierTo(new Point(1.0, 0.50), new Point(0.82, 0.72), new Point(0.5, 0.95), true, true);
        }

        g.Freeze();
        return g;
    }

    // ---------- Hilfen ----------

    private static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Gradient(Color from, Color to, bool horizontal)
    {
        var brush = new LinearGradientBrush(from, to, new Point(0, 0), horizontal ? new Point(1, 0) : new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}
