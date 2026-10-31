using System.Windows.Controls;
using System.Windows.Media;

namespace Fotobox.Views.Booth;

/// <summary>
/// Leistungsanzeige der Gastansicht (oben rechts): eine Zeile je eingeschaltetem Wert, wahlweise mit schmalem Balken. Nur Anzeige
/// (nimmt keine Eingaben an), ohne Effekte, nie im Foto oder Video. Die Warnfarben gehören der Gastansicht (nicht der Admin-Palette).
/// </summary>
public partial class PerformanceOverlay : UserControl
{
    /// <summary>Farbe eines Werts ab Warnstufe „Warnung“ (70 %).</summary>
    public static readonly Color WarnColor = Color.FromRgb(0xFF, 0xD5, 0x4A);

    /// <summary>Farbe eines Werts ab Warnstufe „kritisch“ (90 %).</summary>
    public static readonly Color CriticalColor = Color.FromRgb(0xFF, 0x5A, 0x5A);

    public PerformanceOverlay()
    {
        InitializeComponent();
    }
}
