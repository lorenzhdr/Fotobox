namespace Fotobox.Core.Peace;

/// <summary>
/// Letzter Bart-Zustand der Gastansicht für die Foto-Planung (Singleton, nur auf dem UI-Thread). Nur die Gastansicht schreibt hinein;
/// so gerät ein Bart-Test im Admin nie in ein echtes Foto.
/// </summary>
public sealed class GuestMoustacheState
{
    /// <summary>Aktueller Stand; null = kein Bart aktiv.</summary>
    public MoustachePlanInput? Current { get; set; }

    /// <summary>Stand für ein Foto festhalten.</summary>
    public MoustachePlanInput? Capture() => Current;
}
