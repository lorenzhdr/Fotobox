using System.Runtime.CompilerServices;
using System.Windows.Input;
using Fotobox.Views;

namespace Fotobox.Tests;

/// <summary>
/// Simulierte Tastendrücke und Mausradereignisse dürfen nicht von echt gedrückten Tasten am Testrechner abhängen
/// (z. B. Strg beim Tippen während des Testlaufs: aus „Enter“ würde „Strg+Enter“ und die Video-Taste griffe nicht).
/// Deshalb gilt im ganzen Testlauf „keine Zusatztaste“; gesetzt einmalig vor dem ersten Test.
/// </summary>
internal static class FixedModifierState
{
    [ModuleInitializer]
    internal static void Initialize() => ModifierState.Source = static () => ModifierKeys.None;
}
