using System.Globalization;
using System.Windows.Markup;
using System.Windows.Media;
using Fotobox.Core;

namespace Fotobox.ViewModels.Admin;

/// <summary>Eintrag der Schriftauswahl. <c>Source</c> ist der gespeicherte Name (<c>FontFamily.Source</c>).</summary>
public sealed record FontOption(string Source, string Display, FontFamily Family, bool IsInstalled = true)
{
    /// <summary>Anzeigename (auch für UI Automation in ComboBox-Einträgen).</summary>
    public override string ToString() => Display;
}

/// <summary>
/// Alle Systemschriften, einmal pro Prozess im Hintergrund geladen und sortiert (die Aufzählung ist auf PCs mit
/// vielen Schriften spürbar langsam). Die Liste ist unveränderlich und darf von jedem Thread gelesen werden.
/// </summary>
public static class FontCatalog
{
    private static readonly Lazy<Task<IReadOnlyList<FontOption>>> Cache = new(() => Task.Run(Load));

    public static Task<IReadOnlyList<FontOption>> GetAsync() => Cache.Value;

    /// <summary>Ersatzschrift für nicht installierte Einträge und die Vorschau.</summary>
    public static FontFamily Fallback { get; } = new(LayoutSettings.DefaultCaptionFont);

    private static IReadOnlyList<FontOption> Load()
    {
        try
        {
            var languages = new List<XmlLanguage> { XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag) };
            var neutral = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (neutral.Length > 0)
                languages.Add(XmlLanguage.GetLanguage(neutral));

            var options = new Dictionary<string, FontOption>(StringComparer.OrdinalIgnoreCase);
            foreach (var family in Fonts.SystemFontFamilies)
            {
                try
                {
                    var source = family.Source;
                    if (string.IsNullOrWhiteSpace(source) || options.ContainsKey(source))
                        continue;
                    options[source] = new FontOption(source, DisplayNameOf(family, languages), family);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Eine Schrift konnte nicht gelesen werden.", ex);
                }
            }

            return options.Values
                .OrderBy(o => o.Display, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(o => o.Source, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            AppLog.Error("Die Schriftenliste konnte nicht geladen werden.", ex);
            return [];
        }
    }

    /// <summary>Lokaler (deutscher) Name, falls die Schrift einen hat; sonst <c>Source</c>.</summary>
    private static string DisplayNameOf(FontFamily family, List<XmlLanguage> languages)
    {
        foreach (var language in languages)
        {
            if (family.FamilyNames.TryGetValue(language, out var name) && !string.IsNullOrWhiteSpace(name))
                return name;
        }
        return family.Source;
    }
}
