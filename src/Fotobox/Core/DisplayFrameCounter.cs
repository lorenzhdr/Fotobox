namespace Fotobox.Core;

/// <summary>
/// Zähler der tatsächlich auf dem Bildschirm gezeigten Livebilder der Gastansicht (für die Leistungsanzeige). Die Vorschau zählt nach
/// jedem geschriebenen Bild (<see cref="Increment"/>, UI-Thread); die Rate rechnet der Leser aus Differenz und Zeit.
/// </summary>
public sealed class DisplayFrameCounter
{
    private long _count;

    /// <summary>Anzahl der bisher gezeigten Bilder (thread-sicher gelesen, nur steigend).</summary>
    public long Count => Interlocked.Read(ref _count);

    /// <summary>Zählt ein gezeigtes Bild.</summary>
    public void Increment() => Interlocked.Increment(ref _count);
}
