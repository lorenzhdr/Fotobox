using System.Windows;

namespace Fotobox.Services.Layout;

/// <summary>Geometrie des Fotostreifen-Layouts (alles in Seitenpixeln, ganzzahlig ausgerichtet).</summary>
/// <param name="Strips">Die beiden identischen Streifen (Hochformat: nebeneinander, Querformat: untereinander).</param>
/// <param name="Cells">Pro Streifen die Fotozellen in Anzeigereihenfolge.</param>
/// <param name="Footers">Pro Streifen der Fußbereich für Text.</param>
/// <param name="CutStart">Beginn der Schnittlinie.</param>
/// <param name="CutEnd">Ende der Schnittlinie.</param>
public sealed record StripGeometry(
    IReadOnlyList<Rect> Strips,
    IReadOnlyList<IReadOnlyList<Rect>> Cells,
    IReadOnlyList<Rect> Footers,
    Point CutStart,
    Point CutEnd);

/// <summary>Reine, UI-freie Layout-Mathematik.</summary>
public static class LayoutMath
{
    /// <summary>
    /// Zentriertes Quell-Rechteck (in Quellpixeln), das beim Skalieren auf das Zielseitenverhältnis
    /// die Zielfläche ohne Verzerrung vollständig füllt (crop-to-fill).
    /// </summary>
    public static Rect CropToFill(double srcW, double srcH, double dstW, double dstH)
    {
        if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0)
            return new Rect(0, 0, Math.Max(srcW, 0), Math.Max(srcH, 0));

        var srcAspect = srcW / srcH;
        var dstAspect = dstW / dstH;
        if (srcAspect > dstAspect)
        {
            var w = srcH * dstAspect;
            return new Rect((srcW - w) / 2, 0, w, srcH);
        }

        var h = srcW / dstAspect;
        return new Rect(0, (srcH - h) / 2, srcW, h);
    }

    /// <summary>Berechnet das Streifen-Layout für eine Seite mit <paramref name="count"/> Fotos je Streifen.</summary>
    public static StripGeometry ComputeStrips(int pageW, int pageH, int count, bool hasFooterText)
    {
        count = Math.Max(1, count);
        var landscape = pageW > pageH;

        Rect[] strips;
        if (landscape)
        {
            var h0 = pageH / 2;
            strips = [new Rect(0, 0, pageW, h0), new Rect(0, h0, pageW, pageH - h0)];
        }
        else
        {
            var w0 = pageW / 2;
            strips = [new Rect(0, 0, w0, pageH), new Rect(w0, 0, pageW - w0, pageH)];
        }

        var cells = new List<IReadOnlyList<Rect>>();
        var footers = new List<Rect>();
        foreach (var strip in strips)
        {
            var margin = Math.Max(1, Math.Round(0.04 * Math.Min(strip.Width, strip.Height)));
            var gap = Math.Max(1, Math.Round(margin * 0.6));
            var footerH = hasFooterText
                ? Math.Round(strip.Height * (landscape ? 0.20 : 0.10))
                : margin;

            var inner = new Rect(strip.X + margin, strip.Y + margin,
                Math.Max(1, strip.Width - 2 * margin), Math.Max(1, strip.Height - 2 * margin));
            footerH = Math.Min(footerH, inner.Height / 2);
            var area = new Rect(inner.X, inner.Y, inner.Width, Math.Max(1, inner.Height - footerH));
            footers.Add(new Rect(inner.X, area.Bottom, inner.Width, inner.Bottom - area.Bottom));

            var list = new List<Rect>(count);
            if (landscape)
            {
                var cw = Math.Max(1, Math.Floor((area.Width - gap * (count - 1)) / count));
                for (var i = 0; i < count; i++)
                    list.Add(new Rect(area.X + i * (cw + gap), area.Y, cw, area.Height));
            }
            else
            {
                var ch = Math.Max(1, Math.Floor((area.Height - gap * (count - 1)) / count));
                for (var i = 0; i < count; i++)
                    list.Add(new Rect(area.X, area.Y + i * (ch + gap), area.Width, ch));
            }
            cells.Add(list);
        }

        var cutStart = landscape ? new Point(0, strips[1].Y) : new Point(strips[1].X, 0);
        var cutEnd = landscape ? new Point(pageW, strips[1].Y) : new Point(strips[1].X, pageH);
        return new StripGeometry(strips, cells, footers, cutStart, cutEnd);
    }
}
