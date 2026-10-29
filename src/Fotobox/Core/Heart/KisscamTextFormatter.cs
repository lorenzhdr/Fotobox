using System.Globalization;
using System.Text.RegularExpressions;

namespace Fotobox.Core.Heart;

/// <summary>
/// Setzt den Kisscam-Hinweistext zusammen: ersetzt den Platzhalter <c>{Sekunden}</c> (Groß-/Kleinschreibung egal) durch die Dauer.
/// Unbekannte Platzhalter bleiben unverändert stehen; <see cref="FindUnknownPlaceholders"/> meldet sie für Hinweise im Admin.
/// </summary>
public static partial class KisscamTextFormatter
{
    /// <summary>Der einzige bekannte Platzhalter (ohne Klammern).</summary>
    public const string SecondsPlaceholder = "Sekunden";

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex PlaceholderRegex();

    /// <summary>Leerer oder nur aus Leerraum bestehender Text ergibt <see cref="HeartSettings.DefaultKisscamText"/>.</summary>
    public static string Format(string? template, int seconds)
    {
        var text = string.IsNullOrWhiteSpace(template) ? HeartSettings.DefaultKisscamText : template;
        return PlaceholderRegex().Replace(text, m =>
            string.Equals(m.Groups[1].Value, SecondsPlaceholder, StringComparison.OrdinalIgnoreCase)
                ? seconds.ToString(CultureInfo.InvariantCulture)
                : m.Value);
    }

    /// <summary>
    /// Unbekannte Platzhalter des Textes samt geschweiften Klammern, wie geschrieben, ohne Duplikate (Groß-/Kleinschreibung egal) in
    /// der Reihenfolge des ersten Auftretens. Leerer Text hat keine.
    /// </summary>
    public static IReadOnlyList<string> FindUnknownPlaceholders(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
            return [];
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in PlaceholderRegex().Matches(template))
        {
            if (string.Equals(m.Groups[1].Value, SecondsPlaceholder, StringComparison.OrdinalIgnoreCase))
                continue;
            if (seen.Add(m.Groups[1].Value))
                result.Add(m.Value);
        }
        return result;
    }
}
