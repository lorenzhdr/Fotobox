using System.Windows.Input;

namespace Fotobox.Views;

/// <summary>
/// Gedrückte Zusatztasten (Strg/Alt/Umschalt/Win) für Tasten- und Mausradauswertung. Einzige Stelle, die dafür
/// <see cref="Keyboard.Modifiers"/> liest: Die Tests setzen <see cref="Source"/> einmalig fest auf „keine“, damit eine
/// gerade echt gedrückte Taste am Rechner simulierte Tastendrücke nicht verändert.
/// </summary>
internal static class ModifierState
{
    internal static Func<ModifierKeys> Source { get; set; } = () => Keyboard.Modifiers;

    internal static ModifierKeys Current => Source();
}
