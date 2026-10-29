using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Alle Tests, die WPF-Fenster, XAML oder Rendering nutzen (StaFact/WpfFact), laufen nacheinander.
/// Sonst laden mehrere STA-Threads gleichzeitig BAML (die gemeinsame <c>WpfSharedBamlSchemaContext</c>-Sperre) und
/// der Testhost bleibt sporadisch für immer hängen (Monitor-Wartezeit auf STA-Threads, die dabei Nachrichten abarbeiten).
/// Im echten Programm gibt es nur einen UI-Thread, das Problem tritt dort nicht auf.
/// </summary>
[CollectionDefinition(Name)]
public sealed class WpfCollection
{
    public const string Name = "Wpf";

    /// <summary>
    /// Registriert das pack://-Schema (statischer Konstruktor von <see cref="System.Windows.Application"/>), ohne eine
    /// Application anzulegen. Sonst schlägt ein Pack-URI mit „The URI prefix is not recognized“ fehl, wenn der Test
    /// allein läuft und noch kein anderer Test WPF initialisiert hat.
    /// </summary>
    public static void EnsurePackUris() =>
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(System.Windows.Application).TypeHandle);
}
