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
}
