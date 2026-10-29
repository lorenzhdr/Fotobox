namespace Fotobox.Core.Music;

/// <summary>Eine Musikdatei der obersten Ebene des Musikordners.</summary>
public sealed record KisscamFileEntry(string Name, string FullPath, long Length, DateTime LastWriteUtc);

/// <summary>
/// Dateisystemzugriffe der Musikbibliothek. Alle Methoden dürfen blockieren und werfen; die Bibliothek ruft sie ausschließlich auf
/// dem Threadpool und mit Frist auf. Austauschbar, damit Tests hängende oder fehlerhafte Zugriffe nachstellen können.
/// </summary>
public interface IKisscamMusicFileSystem
{
    /// <summary>Legt den Ordner an (ein vorhandener wird verwendet).</summary>
    void EnsureDirectory(string folder);

    /// <summary>Die Musikdateien (Endungen aus <see cref="KisscamMusicFiles.Extensions"/>) der obersten Ebene, nicht rekursiv.</summary>
    IReadOnlyList<KisscamFileEntry> ListSongs(string folder);

    /// <summary>Inhalt der Config oder <c>null</c>, wenn die Datei nicht existiert.</summary>
    string? ReadConfig(string path);

    /// <summary>Schreibt die Config atomar (temporäre Datei, dann ersetzen).</summary>
    void WriteConfigAtomic(string path, string content);

    /// <summary>Verschiebt eine unlesbare Config nach <paramref name="brokenPath"/> (eine vorhandene Zieldatei wird ersetzt).</summary>
    void MoveBroken(string path, string brokenPath);

    /// <summary>Länge des Songs oder <c>null</c>, wenn sie sich nicht ermitteln lässt.</summary>
    TimeSpan? ReadDuration(string path);

    /// <summary>
    /// Beobachtet den Ordner auf Änderungen an Musikdateien (nur die vier Endungen, nicht die Config). <paramref name="changed"/> bei
    /// jeder Änderung, <paramref name="failed"/> wenn die Beobachtung ausfällt. Das Ergebnis beendet sie.
    /// </summary>
    IDisposable Watch(string folder, Action changed, Action<Exception> failed);
}
