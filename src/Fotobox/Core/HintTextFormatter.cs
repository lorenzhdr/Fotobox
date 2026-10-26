using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Input;

namespace Fotobox.Core;

/// <summary>
/// Baut den Hinweistext der Gastansicht („Drücke die Leertaste für ein Foto 📸“): entweder automatisch aus den eingestellten
/// Auslöse-Tasten oder aus einer eigenen Vorlage mit Platzhaltern <c>{Fototaste_1}</c>, <c>{Fototaste_2}</c> …, <c>{Fototasten}</c>
/// und <c>{Klick}</c> (Groß-/Kleinschreibung egal). UI-frei, damit Gastansicht und Admin-Vorschau denselben Text zeigen.
/// </summary>
public static class HintTextFormatter
{
    /// <summary>Höchstens so viele Tastennamen nennt der automatische Hinweistext.</summary>
    public const int AutomaticKeysMax = 3;

    private static readonly KeyChord SpaceKey = new(Key.Space, ModifierKeys.None);

    // Ein Platzhalter: geschweifte Klammern ohne weitere Klammern darin.
    private static readonly Regex PlaceholderPattern = new(@"\{([^{}]*)\}", RegexOptions.CultureInvariant);
    private static readonly Regex KeyNumberPattern = new(@"^Fototaste_(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.CultureInvariant);
    private static readonly Regex SpaceBeforePunctuation = new(@" +([,.!?])", RegexOptions.CultureInvariant);

    // Eingesetzte Werte stehen während der Bereinigung nur als Marke (Privatzeichen + Index), damit Leerzeichen oder
    // Satzzeichen in Tastennamen (z. B. „Ziffernblock ,“) von der Bereinigung unberührt bleiben.
    private const char MarkStart = '';
    private const char MarkEnd = '';
    private static readonly Regex MarkPattern = new("(\\d+)", RegexOptions.CultureInvariant);

    /// <summary>
    /// Der automatische Text: „Drücke die Leertaste für ein Foto 📸“, bei mehreren Tasten „Drücke A, B oder C für ein Foto 📸“
    /// (höchstens <see cref="AutomaticKeysMax"/> Namen). Löst auch ein Mausklick aus: „… oder klicke für ein Foto 📸“
    /// bzw. „Drücke A, B – oder klicke – für ein Foto 📸“.
    /// </summary>
    public static string BuildAutomatic(IReadOnlyList<KeyChord> keys, bool click)
    {
        if (keys.Count == 1)
        {
            var single = keys[0];
            var name = single == SpaceKey ? "die Leertaste" : single.DisplayName;
            return click
                ? $"Drücke {name} oder klicke für ein Foto 📸"
                : $"Drücke {name} für ein Foto 📸";
        }

        var list = JoinNames(keys.Take(AutomaticKeysMax).Select(k => k.DisplayName).ToList());
        return click
            ? $"Drücke {list} – oder klicke – für ein Foto 📸"
            : $"Drücke {list} für ein Foto 📸";
    }

    /// <summary>
    /// Setzt die Platzhalter der Vorlage ein. Ohne Vorlage (null, leer, nur Leerraum) und wenn nach dem Einsetzen nichts übrig
    /// bleibt, gilt der automatische Text. Unbekannte <c>{…}</c> bleiben wörtlich stehen; nicht belegte <c>{Fototaste_N}</c>
    /// und <c>{Klick}</c> ohne Mausauslöser werden leer, doppelte Leerzeichen und Leerzeichen vor , . ! ? verschwinden.
    /// Die Tastennamen sind die Anzeigenamen („Leertaste“, ohne Artikel).
    /// </summary>
    public static string Format(string? template, IReadOnlyList<KeyChord> keys, bool click)
    {
        if (string.IsNullOrWhiteSpace(template))
            return BuildAutomatic(keys, click);

        var values = new List<string>();
        var marked = PlaceholderPattern.Replace(template, m =>
        {
            var value = Resolve(m.Groups[1].Value, keys, click);
            if (value is null)
                return m.Value;   // unbekannt: wörtlich lassen
            if (value.Length == 0)
                return "";

            values.Add(value);
            return $"{MarkStart}{values.Count - 1}{MarkEnd}";
        });

        var cleaned = WhitespaceRun.Replace(marked, " ");
        cleaned = SpaceBeforePunctuation.Replace(cleaned, "$1").Trim();

        var result = MarkPattern.Replace(cleaned, m =>
            int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < values.Count
                ? values[i]
                : m.Value);
        return result.Length == 0 ? BuildAutomatic(keys, click) : result;
    }

    /// <summary>
    /// Hinweise für den Admin zur Vorlage (leer = alles in Ordnung): nicht belegte Fototasten-Nummern und unbekannte Platzhalter.
    /// Jeder Platzhalter wird nur einmal gemeldet.
    /// </summary>
    public static IReadOnlyList<string> FindProblems(string? template, int keyCount)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(template))
            return problems;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in PlaceholderPattern.Matches(template))
        {
            if (!seen.Add(m.Value))
                continue;

            var name = m.Groups[1].Value;
            if (TryGetKeyNumber(name, out var number))
            {
                if (number > keyCount)
                    problems.Add($"{m.Value} ist nicht belegt ({DescribeKeyCount(keyCount)}) und bleibt leer.");
            }
            else if (!IsKnownName(name))
            {
                problems.Add($"Unbekannter Platzhalter {m.Value} wird unverändert angezeigt.");
            }
        }
        return problems;
    }

    /// <summary>Wert des Platzhalters; null = unbekannter Name.</summary>
    private static string? Resolve(string name, IReadOnlyList<KeyChord> keys, bool click)
    {
        if (TryGetKeyNumber(name, out var number))
            return number <= keys.Count ? keys[number - 1].DisplayName : "";
        if (name.Equals("Fototasten", StringComparison.OrdinalIgnoreCase))
            return JoinNames(keys.Select(k => k.DisplayName).ToList());
        if (name.Equals("Klick", StringComparison.OrdinalIgnoreCase))
            return click ? "oder klicke" : "";
        return null;
    }

    private static bool IsKnownName(string name) =>
        name.Equals("Fototasten", StringComparison.OrdinalIgnoreCase) || name.Equals("Klick", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>Fototaste_N</c> mit N ≥ 1; zu große Zahlen gelten als gültig, aber nie belegt.</summary>
    private static bool TryGetKeyNumber(string name, out int number)
    {
        number = 0;
        var match = KeyNumberPattern.Match(name);
        if (!match.Success)
            return false;

        var digits = match.Groups[1].Value;
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number))
            number = int.MaxValue;
        return number >= 1;
    }

    /// <summary>„A“, „A oder B“, „A, B oder C“.</summary>
    private static string JoinNames(IReadOnlyList<string> names) =>
        names.Count <= 1
            ? string.Concat(names)
            : $"{string.Join(", ", names.Take(names.Count - 1))} oder {names[^1]}";

    private static string DescribeKeyCount(int count) => count switch
    {
        <= 0 => "es ist keine Fototaste belegt",
        1 => "es ist 1 Fototaste belegt",
        _ => $"es sind {count} Fototasten belegt",
    };
}
