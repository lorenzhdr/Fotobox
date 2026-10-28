using System.Reflection;

namespace Fotobox.Core;

/// <summary>
/// Die Abschnitte von <see cref="AppSettings"/>, die zwischen Entwurf, Standardwerten und (später) Export/Import übertragbar sind:
/// alle öffentlichen Abschnittsobjekte außer <see cref="ExcludedProperties"/>. Neue Abschnitte sind automatisch dabei; soll ein
/// neuer Eintrag von <see cref="AppSettings"/> nicht übertragen werden, muss er in <see cref="ExcludedProperties"/> stehen
/// (ein Test erzwingt diese Entscheidung).
/// </summary>
public static class SettingsSections
{
    /// <summary>Eigenschaften von <see cref="AppSettings"/>, die nie übertragen werden: Format-Version und PIN.</summary>
    public static IReadOnlySet<string> ExcludedProperties { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(AppSettings.Version),
        nameof(AppSettings.Security),
    };

    /// <summary>Alle übertragbaren Abschnitte (Eigenschaften von <see cref="AppSettings"/> mit Klassentyp, ohne Ausschlüsse).</summary>
    public static IReadOnlyList<PropertyInfo> PortableSections { get; } = typeof(AppSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType.IsClass && p.PropertyType != typeof(string)
                    && p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0
                    && !ExcludedProperties.Contains(p.Name))
        .ToArray();

    /// <summary>
    /// Überschreibt alle übertragbaren Abschnitte von <paramref name="to"/> mit tiefen Kopien aus <paramref name="from"/>.
    /// <see cref="AppSettings.Version"/> und <see cref="AppSettings.Security"/> bleiben unverändert; <paramref name="from"/>
    /// wird nicht verändert und teilt keine Objekte mit <paramref name="to"/>.
    /// </summary>
    public static void CopyPortableSections(AppSettings from, AppSettings to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        // Eine tiefe Kopie (JSON-Roundtrip wie AppSettings.Clone) genügt: Ihre Abschnitte gehören danach allein dem Ziel.
        var copy = from.Clone();
        foreach (var section in PortableSections)
            section.SetValue(to, section.GetValue(copy));
    }
}
