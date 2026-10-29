namespace Fotobox.Core.Heart;

/// <summary>Rechteck des Kisscam-Rahmens (Pixel bzw. DIPs, Ursprung oben links der Zeichenfläche).</summary>
public readonly record struct FrameRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
    public FrameRect Inflate(double d) => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);
}

/// <summary>Punkt des Kisscam-Rahmens (Lichterkette).</summary>
public readonly record struct FramePoint(double X, double Y);

/// <summary>
/// Maße des Jumbotron-Rahmens der Kisscam für eine Zeichenfläche (<paramref name="Width"/> × <paramref name="Height"/>). Eine
/// Geometrie für Livebild und Foto: alle Maße hängen nur von der kürzeren Kante <c>m</c> ab (Rahmenstärke, Banner, Eckherzen,
/// Lichter), das Banner zusätzlich von der Breite. UI-frei (nur <c>double</c>), damit sie ohne WPF testbar bleibt.
/// </summary>
/// <param name="Thickness">Rahmenstärke (vier Bänder entlang der Kanten).</param>
/// <param name="Banner">Banner oben mittig; ragt etwas in den Rahmen hinein.</param>
/// <param name="BannerCornerRadius">Eckradius des Banners.</param>
/// <param name="BannerPadding">Innenabstand des Banners links und rechts neben dem Text.</param>
/// <param name="FontEm">Nennschrifthöhe (Em) des Banner-Textes.</param>
/// <param name="CornerHearts">Die vier Eckherzen (oben links, oben rechts, unten links, unten rechts), quadratische Rechtecke innerhalb des Rahmens.</param>
/// <param name="Lights">Lichterkette auf der Mitte des Rahmenbandes (ohne Stellen unter dem Banner).</param>
/// <param name="LightRadius">Radius eines Lichtpunkts.</param>
public sealed record KisscamFrameGeometry(
    double Width, double Height, double Thickness, FrameRect Banner, double BannerCornerRadius, double BannerPadding, double FontEm,
    IReadOnlyList<FrameRect> CornerHearts, IReadOnlyList<FramePoint> Lights, double LightRadius)
{
    /// <summary>Rahmenstärke relativ zur kürzeren Kante.</summary>
    public const double ThicknessFactor = 0.03;
    /// <summary>Bannerbreite relativ zur Breite, wenn der Text keine größere braucht.</summary>
    public const double BannerWidthFactor = 0.46;
    /// <summary>Größte Bannerbreite relativ zur Breite.</summary>
    public const double BannerMaxWidthFactor = 0.90;
    public const double BannerHeightFactor = 0.13;
    public const double BannerRadiusFactor = 0.03;
    public const double FontFactor = 0.075;
    public const double HeartFactor = 0.11;
    public const double HeartGapFactor = 0.02;
    public const double LightSpacingFactor = 0.05;
    public const double LightRadiusFactor = 0.0075;
    /// <summary>Anteil der Rahmenstärke, um den das Banner in den Rahmen hineinragt (Banner beginnt bei dieser Tiefe).</summary>
    public const double BannerInsetFactor = 0.35;

    /// <summary>
    /// Berechnet die Maße. <paramref name="minBannerWidth"/> = Mindestbreite des Banners (gemessene Textbreite plus Innenabstand);
    /// das Banner ist mindestens <see cref="BannerWidthFactor"/> · Breite und höchstens so breit, dass es weder
    /// <see cref="BannerMaxWidthFactor"/> · Breite überschreitet noch die Eckherzen berührt. Ungültige Maße (≤ 0) ergeben eine leere Geometrie.
    /// </summary>
    public static KisscamFrameGeometry For(double width, double height, double minBannerWidth = 0)
    {
        if (!(width > 0) || !(height > 0) || !double.IsFinite(width) || !double.IsFinite(height))
            return new KisscamFrameGeometry(Math.Max(0, width), Math.Max(0, height), 0, default, 0, 0, 0, [], [], 0);

        var m = Math.Min(width, height);
        var t = ThicknessFactor * m;
        var em = FontFactor * m;
        var heart = HeartFactor * m;
        var gap = HeartGapFactor * m;

        // Eckherzen: innerhalb des Rahmens, mit Abstand zum Rahmen.
        var h0 = t + gap;
        List<FrameRect> hearts =
        [
            new(h0, h0, heart, heart),
            new(width - h0 - heart, h0, heart, heart),
            new(h0, height - h0 - heart, heart, heart),
            new(width - h0 - heart, height - h0 - heart, heart, heart),
        ];

        // Banner: zentriert, höchstens so breit, dass es 0,9 · Breite nicht überschreitet und die oberen Herzen nicht berührt.
        var maxWidth = Math.Min(BannerMaxWidthFactor * width, width - 2 * (h0 + heart + gap));
        maxWidth = Math.Max(maxWidth, Math.Min(BannerWidthFactor * width, width));
        var bannerWidth = Math.Clamp(Math.Max(BannerWidthFactor * width, minBannerWidth), 0, maxWidth);
        var bannerHeight = BannerHeightFactor * m;
        var banner = new FrameRect((width - bannerWidth) / 2, t * BannerInsetFactor, bannerWidth, bannerHeight);

        // Lichterkette auf der Mitte des Rahmenbandes, gleichmäßig verteilt, nicht unter dem Banner.
        var lights = new List<FramePoint>();
        var lightBox = banner.Inflate(t * 0.5);
        var half = t / 2;
        var w = width - t;
        var h = height - t;
        var perimeter = 2 * (w + h);
        var count = Math.Max(4, (int)Math.Round(perimeter / (LightSpacingFactor * m)));
        for (var i = 0; i < count; i++)
        {
            var p = FramePointAt(perimeter * i / count, half, w, h);
            if (!lightBox.Contains(p.X, p.Y))
                lights.Add(p);
        }

        return new KisscamFrameGeometry(width, height, t, banner, BannerRadiusFactor * m, em * 0.9, em, hearts, lights, LightRadiusFactor * m);
    }

    /// <summary>Punkt auf dem Rechteck mit Rand <paramref name="inset"/> in der Breite <paramref name="w"/> und Höhe <paramref name="h"/>, im Uhrzeigersinn ab oben links.</summary>
    private static FramePoint FramePointAt(double distance, double inset, double w, double h)
    {
        if (distance < w)
            return new FramePoint(inset + distance, inset);
        distance -= w;
        if (distance < h)
            return new FramePoint(inset + w, inset + distance);
        distance -= h;
        if (distance < w)
            return new FramePoint(inset + w - distance, inset + h);
        distance -= w;
        return new FramePoint(inset, inset + h - distance);
    }
}
