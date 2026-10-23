using System.Windows;

namespace Fotobox.Services.Layout;

/// <summary>Geometrie des Fotostreifen-Layouts (alles in Seitenpixeln, ganzzahlig ausgerichtet).</summary>
/// <param name="Strips">Die beiden identischen Streifen (Hochformat: nebeneinander, Querformat: untereinander); beim Einzelstreifen nur die ganze Seite.</param>
/// <param name="Cells">Pro Streifen die Fotozellen in Anzeigereihenfolge.</param>
/// <param name="Footers">Pro Streifen das Textrechteck im Fußbereich (liegt in der Sicherheitszone, siehe <see cref="LayoutMath.SafeBottomMm"/>).</param>
/// <param name="CutStart">Beginn der Schnittlinie (nur Doppelstreifen).</param>
/// <param name="CutEnd">Ende der Schnittlinie (nur Doppelstreifen).</param>
public sealed record StripGeometry(
    IReadOnlyList<Rect> Strips,
    IReadOnlyList<IReadOnlyList<Rect>> Cells,
    IReadOnlyList<Rect> Footers,
    Point CutStart,
    Point CutEnd);

/// <summary>Reine, UI-freie Layout-Mathematik.</summary>
public static class LayoutMath
{
    /// <summary>Kein Text näher als dies an der Unterkante (Overscan beim randlosen Druck, Schnitttoleranz).</summary>
    public const double SafeBottomMm = 5;

    /// <summary>Kein Text näher als dies am seitlichen Rand.</summary>
    public const double SafeSideMm = 3;

    /// <summary>Höhe des Bildunterschrift-Blocks im Fußbereich (bis zu 2 Zeilen).</summary>
    public const double CaptionBlockMm = 11;

    /// <summary>Höhe des einzeiligen Datums im Fußbereich.</summary>
    public const double DateBlockMm = 4.5;

    /// <summary>Abstand zwischen Fotos und Text sowie zwischen Bildunterschrift und Datum.</summary>
    public const double TextGapMm = 1;

    public static int MmToPx(double mm, int dpi) => (int)Math.Round(mm / 25.4 * Math.Max(1, dpi));

    /// <summary>Nennhöhe des Textblocks (Bildunterschrift und/oder Datum) in Pixeln.</summary>
    public static double TextBlockHeight(bool hasCaption, bool hasDate, int dpi)
    {
        var mm = (hasCaption ? CaptionBlockMm : 0) + (hasDate ? DateBlockMm : 0) + (hasCaption && hasDate ? TextGapMm : 0);
        return MmToPx(mm, dpi);
    }

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

    /// <summary>
    /// Berechnet das Doppelstreifen-Layout für eine Seite mit <paramref name="count"/> Fotos je Streifen.
    /// Mit Text liegt das Textrechteck mindestens <see cref="SafeBottomMm"/> über der Streifenunterkante.
    /// </summary>
    public static StripGeometry ComputeStrips(int pageW, int pageH, int count, bool hasFooterText, int dpi = 300,
        bool hasCaption = true, bool hasDate = true)
    {
        count = Math.Max(1, count);
        var landscape = pageW > pageH;
        var hasText = hasFooterText && (hasCaption || hasDate);

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

            var inner = new Rect(strip.X + margin, strip.Y + margin,
                Math.Max(1, strip.Width - 2 * margin), Math.Max(1, strip.Height - 2 * margin));

            Rect area;
            if (hasText)
            {
                var textRect = FooterTextRect(strip, margin, hasCaption, hasDate, dpi);
                var textGap = Math.Max(1, MmToPx(TextGapMm, dpi));
                area = new Rect(inner.X, inner.Y, inner.Width, Math.Max(1, textRect.Top - textGap - inner.Y));
                footers.Add(textRect);
            }
            else
            {
                var footerH = Math.Min(margin, inner.Height / 2);
                area = new Rect(inner.X, inner.Y, inner.Width, Math.Max(1, inner.Height - footerH));
                footers.Add(new Rect(inner.X, area.Bottom, inner.Width, inner.Bottom - area.Bottom));
            }

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

    /// <summary>
    /// Einzelstreifen: <c>aspects.Count</c> Fotos einmal untereinander, jeweils vollständig (Seitenverhältnis des Fotos,
    /// nicht beschnitten), gleich breit, so groß wie möglich und horizontal sowie vertikal zentriert.
    /// Der Text steht einmal unten über die volle Breite in der Sicherheitszone.
    /// Gilt für Hoch- und Querformat (im Querformat entsteht eine schmalere, zentrierte Spalte).
    /// </summary>
    /// <param name="aspects">Breite/Höhe je Foto (ungültige Werte werden als 3:2 behandelt).</param>
    public static StripGeometry ComputeSingleStrip(int pageW, int pageH, IReadOnlyList<double> aspects,
        bool hasCaption, bool hasDate, int dpi = 300)
    {
        var count = Math.Max(1, aspects.Count);
        var page = new Rect(0, 0, pageW, pageH);
        var margin = Math.Max(1, Math.Round(0.04 * Math.Min(pageW, pageH)));
        var gap = Math.Max(1, Math.Round(margin * 0.6));
        var hasText = hasCaption || hasDate;

        var areaTop = margin;
        var textRect = Rect.Empty;
        double areaBottom = pageH - margin;
        if (hasText)
        {
            textRect = FooterTextRect(page, margin, hasCaption, hasDate, dpi);
            areaBottom = textRect.Top - Math.Max(1, MmToPx(TextGapMm, dpi));
        }

        var areaH = Math.Max(1, areaBottom - areaTop);
        var availH = Math.Max(1, areaH - gap * (count - 1));
        var availW = Math.Max(1, pageW - 2 * margin);

        var inv = new double[count];
        for (var i = 0; i < count; i++)
        {
            var a = i < aspects.Count ? aspects[i] : 1.5;
            inv[i] = a > 0 && double.IsFinite(a) ? 1.0 / a : 1.0 / 1.5;
        }

        var w = Math.Max(1, Math.Floor(Math.Min(availW, availH / inv.Sum())));
        double[] heights;
        double total;
        while (true)
        {
            heights = inv.Select(v => Math.Max(1, Math.Round(w * v))).ToArray();
            total = heights.Sum() + gap * (count - 1);
            if (total <= areaH || w <= 1)
                break;
            w--;
        }

        var x = Math.Floor((pageW - w) / 2);
        var y = areaTop + Math.Floor(Math.Max(0, areaH - total) / 2);
        var list = new List<Rect>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(new Rect(x, y, w, heights[i]));
            y += heights[i] + gap;
        }

        var footer = hasText ? textRect : new Rect(margin, pageH - margin, availW, 0);
        return new StripGeometry([page], [list], [footer], new Point(0, 0), new Point(0, 0));
    }

    /// <summary>Textrechteck unten im <paramref name="strip"/>: Abstand zur Unterkante ≥ Sicherheitszone, seitlich ≥ Rand und Sicherheitszone.</summary>
    private static Rect FooterTextRect(Rect strip, double margin, bool hasCaption, bool hasDate, int dpi)
    {
        var safeBottom = MmToPx(SafeBottomMm, dpi);
        var textGap = Math.Max(1, MmToPx(TextGapMm, dpi));
        var nominal = TextBlockHeight(hasCaption, hasDate, dpi);
        // Auf kleinen Seiten höchstens 40 % der Höhe für den Fußbereich.
        var textH = Math.Max(1, Math.Min(nominal, strip.Height * 0.4 - safeBottom - textGap));
        var inset = Math.Max(margin, MmToPx(SafeSideMm, dpi));
        var width = Math.Max(1, strip.Width - 2 * inset);
        return new Rect(strip.X + inset, strip.Bottom - safeBottom - textH, width, textH);
    }
}
