using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Input;

namespace Fotobox.Core;

/// <summary>
/// Baut den Hinweistext der Gastansicht („Drücke die Leertaste für ein Foto 📸“): entweder automatisch aus den eingestellten
/// Auslöse-Tasten oder aus einer eigenen Vorlage mit Platzhaltern <c>{Fototaste_1}</c>, <c>{Fototaste_2}</c> …, <c>{Fototasten}</c>
/// und <c>{Klick}</c> (Groß-/Kleinschreibung egal). UI-frei, damit Gastansicht und Admin-Vorschau denselben Text zeigen.
/// Die Video-Hinweiszeile („Drücke die Eingabetaste für ein Video 🎬“) nutzt dieselben Regeln mit <c>{Videotaste_N}</c> und
/// <c>{Videotasten}</c>; beide Platzhaltersätze und <c>{Klick}</c> gelten in beiden Zeilen. Der Stopp-Hinweis während der
/// Videoaufnahme („Drücke die Eingabetaste, um die Videoaufnahme zu beenden“) hat einen eigenen Satz: <c>{Taste}</c>,
/// <c>{Stopptaste_N}</c>, <c>{Stopptasten}</c>.
/// </summary>
public static class HintTextFormatter
{
    /// <summary>Höchstens so viele Tastennamen nennt der automatische Hinweistext.</summary>
    public const int AutomaticKeysMax = 3;

    private static readonly KeyChord SpaceKey = new(Key.Space, ModifierKeys.None);
    private static readonly KeyChord EnterKey = new(Key.Enter, ModifierKeys.None);

    // Ein Platzhalter: geschweifte Klammern ohne weitere Klammern darin.
    private static readonly Regex PlaceholderPattern = new(@"\{([^{}]*)\}", RegexOptions.CultureInvariant);
    private static readonly Regex KeyNumberPattern = new(@"^(Fototaste|Videotaste)_(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex StopKeyNumberPattern = new(@"^Stopptaste_(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.CultureInvariant);
    private static readonly Regex SpaceBeforePunctuation = new(@" +([,.!?])", RegexOptions.CultureInvariant);

    // Eingesetzte Werte stehen während der Bereinigung nur als Marke (Privatzeichen + Index), damit Leerzeichen oder
    // Satzzeichen in Tastennamen (z. B. „Ziffernblock ,“) von der Bereinigung unberührt bleiben.
    private const char MarkStart = '\uE000';
    private const char MarkEnd = '\uE001';
    private static readonly Regex MarkPattern = new($"{MarkStart}(\\d+){MarkEnd}", RegexOptions.CultureInvariant);

    /// <summary>
    /// Der automatische Text: „Drücke die Leertaste für ein Foto 📸“, bei mehreren Tasten „Drücke A, B oder C für ein Foto 📸“
    /// (höchstens <see cref="AutomaticKeysMax"/> Namen). Löst auch ein Mausklick aus: „… oder klicke für ein Foto 📸“
    /// bzw. „Drücke A, B – oder klicke – für ein Foto 📸“. Ohne Taste: „Klicke für ein Foto 📸“ (mit Mausauslöser) bzw.
    /// „Mach ein Foto 📸“ (kein Auslöser konfiguriert; in der Praxis ersetzt der Normalizer eine leere Tastenliste).
    /// </summary>
    public static string BuildAutomatic(IReadOnlyList<KeyChord> keys, bool click)
    {
        if (keys.Count == 0)
            return click ? "Klicke für ein Foto 📸" : "Mach ein Foto 📸";

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
    /// Der automatische Video-Text: „Drücke die Eingabetaste für ein Video 🎬“, bei mehreren Tasten „Drücke A, B oder C für ein
    /// Video 🎬“ (höchstens <see cref="AutomaticKeysMax"/> Namen). Leertaste und Eingabetaste mit Artikel, andere Tasten ohne.
    /// Ohne Video-Starttaste: leer (die Zeile entfällt). Kein Mausklick-Zusatz (Klicks lösen Fotos aus).
    /// </summary>
    public static string BuildAutomaticVideo(IReadOnlyList<KeyChord> videoKeys)
    {
        if (videoKeys.Count == 0)
            return "";

        if (videoKeys.Count == 1)
        {
            var single = videoKeys[0];
            var name = single == SpaceKey ? "die Leertaste" : single == EnterKey ? "die Eingabetaste" : single.DisplayName;
            return $"Drücke {name} für ein Video 🎬";
        }

        return $"Drücke {JoinNames(videoKeys.Take(AutomaticKeysMax).Select(k => k.DisplayName).ToList())} für ein Video 🎬";
    }

    /// <summary>
    /// Setzt die Platzhalter der Vorlage ein. Ohne Vorlage (null, leer, nur Leerraum) und wenn nach dem Einsetzen nichts übrig
    /// bleibt, gilt der automatische Text. Unbekannte <c>{…}</c> bleiben wörtlich stehen; nicht belegte <c>{Fototaste_N}</c>
    /// und <c>{Klick}</c> ohne Mausauslöser werden leer, doppelte Leerzeichen und Leerzeichen vor , . ! ? verschwinden.
    /// Die Tastennamen sind die Anzeigenamen („Leertaste“, ohne Artikel). Mit <paramref name="videoKeys"/> sind in der
    /// Fotozeile auch <c>{Videotaste_N}</c>/<c>{Videotasten}</c> belegt (ohne: leer).
    /// </summary>
    public static string Format(string? template, IReadOnlyList<KeyChord> keys, bool click, IReadOnlyList<KeyChord>? videoKeys = null)
    {
        if (string.IsNullOrWhiteSpace(template))
            return BuildAutomatic(keys, click);

        var result = Substitute(template, keys, videoKeys ?? [], click);
        return result.Length == 0 ? BuildAutomatic(keys, click) : result;
    }

    /// <summary>
    /// Wie <see cref="Format"/> für die Video-Hinweiszeile: <paramref name="template"/> mit <c>{Videotaste_N}</c>,
    /// <c>{Videotasten}</c> (aus <paramref name="videoKeys"/> = Video-Starttasten); <c>{Fototaste_N}</c>, <c>{Fototasten}</c>
    /// kommen aus <paramref name="photoKeys"/> (ohne: leer), <c>{Klick}</c> aus <paramref name="click"/> (Standard aus).
    /// Ohne Vorlage oder wenn nach dem Einsetzen nichts übrig bleibt, gilt <see cref="BuildAutomaticVideo"/> (ohne Starttaste: leer).
    /// </summary>
    public static string FormatVideo(string? template, IReadOnlyList<KeyChord> videoKeys, bool click = false, IReadOnlyList<KeyChord>? photoKeys = null)
    {
        if (string.IsNullOrWhiteSpace(template))
            return BuildAutomaticVideo(videoKeys);

        var result = Substitute(template, photoKeys ?? [], videoKeys, click);
        return result.Length == 0 ? BuildAutomaticVideo(videoKeys) : result;
    }

    /// <summary>
    /// Der automatische Stopp-Hinweis der Videoaufnahme: <see cref="VideoSettings.DefaultStopHintText"/> mit eingesetztem
    /// <c>{Taste}</c>, z. B. „Drücke die Eingabetaste, um die Videoaufnahme zu beenden“. Ohne Stopptaste: leer (kein Hinweis).
    /// </summary>
    public static string BuildAutomaticVideoStop(IReadOnlyList<KeyChord> stopKeys) =>
        stopKeys.Count == 0 ? "" : FormatVideoStop(VideoSettings.DefaultStopHintText, stopKeys);

    /// <summary>
    /// Wie <see cref="FormatVideo"/> für den Stopp-Hinweis während der Aufnahme (unten im Bild). Platzhalter:
    /// <c>{Taste}</c> = die Stopptasten für den Satz (eine Taste: „die Leertaste“/„die Eingabetaste“ mit Artikel, sonst nur der Name;
    /// mehrere Tasten wie in <see cref="BuildAutomaticVideo"/> ohne Artikel, „A, B oder C“, höchstens
    /// <see cref="AutomaticKeysMax"/> Namen), <c>{Stopptaste_N}</c> (einzelne Taste, nur der Name) und <c>{Stopptasten}</c> (alle,
    /// „A oder B“). Foto-/Videotasten-Platzhalter und <c>{Klick}</c> (die Maus beendet nie ein Video) gelten hier nicht und
    /// werden leer; unbekannte <c>{…}</c> bleiben wörtlich stehen. Ohne Vorlage (null, leer, nur Leerraum) oder wenn nach dem
    /// Einsetzen nichts übrig bleibt, gilt <see cref="VideoSettings.DefaultStopHintText"/>. Ohne Stopptaste: leer
    /// (der Hinweis entfällt).
    /// </summary>
    public static string FormatVideoStop(string? template, IReadOnlyList<KeyChord> stopKeys)
    {
        if (stopKeys.Count == 0)
            return "";

        var result = string.IsNullOrWhiteSpace(template) ? "" : SubstituteStop(template, stopKeys);
        return result.Length == 0 ? SubstituteStop(VideoSettings.DefaultStopHintText, stopKeys) : result;
    }

    /// <summary>
    /// Hinweise für den Admin zum Stopp-Hinweistext (leer = alles in Ordnung): <c>{Stopptaste_N}</c> mit N über der Anzahl der
    /// Stopptasten (<paramref name="stopKeyCount"/>), Foto-/Videotasten-Platzhalter und <c>{Klick}</c> (gibt es in dieser Zeile
    /// nicht) sowie unbekannte Platzhalter. Jeder Platzhalter wird nur einmal gemeldet.
    /// </summary>
    public static IReadOnlyList<string> FindVideoStopProblems(string? template, int stopKeyCount)
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
            if (name.Equals("Klick", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{m.Value} gibt es in dieser Zeile nicht (die Maus beendet kein Video) und bleibt leer.");
            }
            else if (TryGetStopKeyNumber(name, out var number))
            {
                if (number > stopKeyCount)
                    problems.Add($"{m.Value} ist nicht belegt ({DescribeKeyCount(stopKeyCount, "Stopptaste")}) und bleibt leer.");
            }
            else if (IsKnownName(name) || TryGetKeyNumber(name, out _, out _))
            {
                problems.Add($"{m.Value} gibt es in dieser Zeile nicht (hier gelten {{Taste}}, {{Stopptaste_N}} und {{Stopptasten}}) und bleibt leer.");
            }
            else if (!name.Equals("Taste", StringComparison.OrdinalIgnoreCase) && !name.Equals("Stopptasten", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"Unbekannter Platzhalter {m.Value} wird unverändert angezeigt.");
            }
        }
        return problems;
    }

    private static string SubstituteStop(string template, IReadOnlyList<KeyChord> stopKeys) =>
        Substitute(template, name => ResolveStop(name, stopKeys));

    /// <summary>Wert des Platzhalters im Stopp-Hinweis; null = unbekannter Name.</summary>
    private static string? ResolveStop(string name, IReadOnlyList<KeyChord> stopKeys)
    {
        if (name.Equals("Taste", StringComparison.OrdinalIgnoreCase))
        {
            if (stopKeys.Count == 0)
                return "";
            if (stopKeys.Count == 1)
            {
                var single = stopKeys[0];
                return single == SpaceKey ? "die Leertaste" : single == EnterKey ? "die Eingabetaste" : single.DisplayName;
            }
            return JoinNames(stopKeys.Take(AutomaticKeysMax).Select(k => k.DisplayName).ToList());
        }
        if (TryGetStopKeyNumber(name, out var number))
            return number <= stopKeys.Count ? stopKeys[number - 1].DisplayName : "";
        if (name.Equals("Stopptasten", StringComparison.OrdinalIgnoreCase))
            return JoinNames(stopKeys.Select(k => k.DisplayName).ToList());
        // Gehört zu den anderen Zeilen, gilt hier aber nicht: leer statt wörtlich.
        if (IsKnownName(name) || TryGetKeyNumber(name, out _, out _))
            return "";
        return null;
    }

    /// <summary><c>Stopptaste_N</c> mit N ≥ 1; zu große Zahlen gelten als gültig, aber nie belegt.</summary>
    private static bool TryGetStopKeyNumber(string name, out int number)
    {
        number = 0;
        var match = StopKeyNumberPattern.Match(name);
        if (!match.Success)
            return false;

        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number))
            number = int.MaxValue;
        return number >= 1;
    }

    /// <summary>Setzt die Platzhalter ein und bereinigt Leerraum; das Ergebnis kann leer sein.</summary>
    private static string Substitute(string template, IReadOnlyList<KeyChord> photoKeys, IReadOnlyList<KeyChord> videoKeys, bool click) =>
        Substitute(template, name => Resolve(name, photoKeys, videoKeys, click));

    /// <summary>Setzt die Platzhalter über <paramref name="resolve"/> ein (null = unbekannt) und bereinigt Leerraum.</summary>
    private static string Substitute(string template, Func<string, string?> resolve)
    {
        var values = new List<string>();
        var marked = PlaceholderPattern.Replace(template, m =>
        {
            var value = resolve(m.Groups[1].Value);
            if (value is null)
                return m.Value;   // unbekannt: wörtlich lassen
            if (value.Length == 0)
                return "";

            values.Add(value);
            return $"{MarkStart}{values.Count - 1}{MarkEnd}";
        });

        var cleaned = WhitespaceRun.Replace(marked, " ");
        cleaned = SpaceBeforePunctuation.Replace(cleaned, "$1").Trim();

        return MarkPattern.Replace(cleaned, m =>
            int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < values.Count
                ? values[i]
                : m.Value);
    }

    /// <summary>
    /// Hinweise für den Admin zur Vorlage (leer = alles in Ordnung): nicht belegte Tasten-Nummern und unbekannte Platzhalter.
    /// Jeder Platzhalter wird nur einmal gemeldet. <paramref name="keyCount"/> = Anzahl Fototasten, <paramref name="videoKeyCount"/> =
    /// Anzahl Video-Starttasten (für <c>{Videotaste_N}</c> in der Fotozeile; Standard 0).
    /// </summary>
    public static IReadOnlyList<string> FindProblems(string? template, int keyCount, int videoKeyCount = 0)
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
            if (TryGetKeyNumber(name, out var isVideo, out var number))
            {
                var count = isVideo ? videoKeyCount : keyCount;
                if (number > count)
                    problems.Add($"{m.Value} ist nicht belegt ({DescribeKeyCount(count, isVideo ? "Videotaste" : "Fototaste")}) und bleibt leer.");
            }
            else if (!IsKnownName(name))
            {
                problems.Add($"Unbekannter Platzhalter {m.Value} wird unverändert angezeigt.");
            }
        }
        return problems;
    }

    /// <summary>
    /// Wie <see cref="FindProblems"/> für die Videozeile: <paramref name="videoKeyCount"/> = Anzahl Video-Starttasten,
    /// <paramref name="photoKeyCount"/> = Anzahl Fototasten (für <c>{Fototaste_N}</c> in der Videozeile; Standard 0).
    /// </summary>
    public static IReadOnlyList<string> FindVideoProblems(string? template, int videoKeyCount, int photoKeyCount = 0) =>
        FindProblems(template, photoKeyCount, videoKeyCount);

    /// <summary>Wert des Platzhalters; null = unbekannter Name.</summary>
    private static string? Resolve(string name, IReadOnlyList<KeyChord> photoKeys, IReadOnlyList<KeyChord> videoKeys, bool click)
    {
        if (TryGetKeyNumber(name, out var isVideo, out var number))
        {
            var keys = isVideo ? videoKeys : photoKeys;
            return number <= keys.Count ? keys[number - 1].DisplayName : "";
        }
        if (name.Equals("Fototasten", StringComparison.OrdinalIgnoreCase))
            return JoinNames(photoKeys.Select(k => k.DisplayName).ToList());
        if (name.Equals("Videotasten", StringComparison.OrdinalIgnoreCase))
            return JoinNames(videoKeys.Select(k => k.DisplayName).ToList());
        if (name.Equals("Klick", StringComparison.OrdinalIgnoreCase))
            return click ? "oder klicke" : "";
        return null;
    }

    private static bool IsKnownName(string name) =>
        name.Equals("Fototasten", StringComparison.OrdinalIgnoreCase) || name.Equals("Videotasten", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Klick", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>Fototaste_N</c>/<c>Videotaste_N</c> mit N ≥ 1; zu große Zahlen gelten als gültig, aber nie belegt.</summary>
    private static bool TryGetKeyNumber(string name, out bool isVideo, out int number)
    {
        number = 0;
        isVideo = false;
        var match = KeyNumberPattern.Match(name);
        if (!match.Success)
            return false;

        isVideo = match.Groups[1].Value.Equals("Videotaste", StringComparison.OrdinalIgnoreCase);
        var digits = match.Groups[2].Value;
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number))
            number = int.MaxValue;
        return number >= 1;
    }

    /// <summary>„A“, „A oder B“, „A, B oder C“.</summary>
    private static string JoinNames(IReadOnlyList<string> names) =>
        names.Count <= 1
            ? string.Concat(names)
            : $"{string.Join(", ", names.Take(names.Count - 1))} oder {names[^1]}";

    private static string DescribeKeyCount(int count, string noun) => count switch
    {
        <= 0 => $"es ist keine {noun} belegt",
        1 => $"es ist 1 {noun} belegt",
        _ => $"es sind {count} {noun}n belegt",
    };
}
