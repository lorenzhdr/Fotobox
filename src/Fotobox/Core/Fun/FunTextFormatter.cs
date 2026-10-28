using System.Globalization;
using System.Text.RegularExpressions;

namespace Fotobox.Core.Fun;

/// <summary>Löst den Fun-Text auf: Einzahl/Mehrzahl, Standardtext bei leerem Eintrag, Platzhalter <c>{Anzahl}</c>.</summary>
public static class FunTextFormatter
{
    private static readonly Regex Placeholder = new(@"\{(?<name>[^{}]*)\}", RegexOptions.CultureInvariant);

    /// <summary>Text für <paramref name="count"/> erkannte Personen; bei ≤ 0 leer. Unbekannte Platzhalter bleiben wörtlich.</summary>
    public static string Resolve(FunSettings settings, int count)
    {
        if (count <= 0)
            return "";

        var text = count == 1 ? settings.SingularText : settings.PluralText;
        if (string.IsNullOrWhiteSpace(text))
            text = count == 1 ? FunSettings.DefaultSingularText : FunSettings.DefaultPluralText;

        return Placeholder.Replace(text, m =>
            string.Equals(m.Groups["name"].Value.Trim(), "Anzahl", StringComparison.OrdinalIgnoreCase)
                ? count.ToString(CultureInfo.InvariantCulture)
                : m.Value);
    }
}
