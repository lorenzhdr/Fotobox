namespace Fotobox.Core.Peace;

/// <summary>
/// Haarfarbe eines Bartes (Peaceerkennung). Keine Einstellung: Sie wird je Person zufällig gewürfelt und bleibt je Spur stabil
/// (das Foto übernimmt die Farbe der Live-Spur). <see cref="Gray"/> ist „Salz und Pfeffer“. Nur hinten erweitern.
/// </summary>
public enum MoustacheColor { Black, DarkBrown, Brown, Ginger, Blond, Gray }

/// <summary>
/// Eine Quelle für die Bartauswahl: alle konkreten Formen, Bereinigung der Zufallsliste und das Würfeln von Form und Farbe
/// (Tracker, Foto-Planer und Admin-Skizzen würfeln nicht mehr selbst). Rein und UI-frei.
/// </summary>
public static class MoustacheStyles
{
    /// <summary>Alle konkreten Formen (ohne <see cref="MoustacheStyle.Random"/>) in Enum-Reihenfolge.</summary>
    public static IReadOnlyList<MoustacheStyle> All { get; } =
    [
        MoustacheStyle.Twirl, MoustacheStyle.Walrus, MoustacheStyle.Chevron, MoustacheStyle.Brush,
        MoustacheStyle.Handlebar, MoustacheStyle.FuManchu, MoustacheStyle.Horseshoe,
    ];

    /// <summary>Alle Haarfarben in Enum-Reihenfolge.</summary>
    public static IReadOnlyList<MoustacheColor> AllColors { get; } = Enum.GetValues<MoustacheColor>();

    /// <summary>Ist <paramref name="style"/> eine konkrete, bekannte Form (nicht Random, nicht undefiniert)?</summary>
    public static bool IsConcrete(MoustacheStyle style) => style != MoustacheStyle.Random && Enum.IsDefined(style);

    /// <summary>
    /// Bereinigt eine Zufallsliste: unbekannte Werte, <see cref="MoustacheStyle.Random"/> und Duplikate fallen weg, der Rest steht in
    /// Enum-Reihenfolge (die Reihenfolge hat keine Bedeutung; so ändert Aus- und Wiederanhaken das JSON nicht). Leer oder null ergibt alle Formen.
    /// </summary>
    public static List<MoustacheStyle> NormalizeRandomStyles(IEnumerable<MoustacheStyle>? styles)
    {
        var wanted = new HashSet<MoustacheStyle>((styles ?? []).Where(IsConcrete));
        var result = All.Where(wanted.Contains).ToList();
        return result.Count > 0 ? result : [.. All];
    }

    /// <summary>Wählt eine Form aus der Zufallsliste der Einstellungen (null = alle Formen); <c>random(n)</c> liefert 0 … n−1.</summary>
    public static MoustacheStyle PickRandom(PeaceSettings? settings, Func<int, int> random) =>
        PickRandom(settings?.RandomStyles, random);

    /// <summary>Wählt eine Form aus <paramref name="pool"/> (bereinigt; leer/null = alle Formen).</summary>
    public static MoustacheStyle PickRandom(IEnumerable<MoustacheStyle>? pool, Func<int, int> random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var styles = NormalizeRandomStyles(pool);
        return styles[Math.Clamp(random(styles.Count), 0, styles.Count - 1)];
    }

    /// <summary>Würfelt eine Haarfarbe; <c>random(n)</c> liefert 0 … n−1.</summary>
    public static MoustacheColor PickColor(Func<int, int> random)
    {
        ArgumentNullException.ThrowIfNull(random);
        return AllColors[Math.Clamp(random(AllColors.Count), 0, AllColors.Count - 1)];
    }
}
