namespace Fotobox.Core.Music;

/// <summary>
/// Die Kisscam-Musikbibliothek: Songs des Musikordners samt Config. Gemeinsamer Dienst von Gastansicht und Admin-Ansicht.
/// <see cref="Current"/> liefert immer sofort den letzten Stand und blockiert nie; alle Dateizugriffe laufen im Hintergrund mit
/// Frist. Keine Methode wirft.
/// </summary>
public interface IKisscamMusicLibrary
{
    /// <summary>Der aktuelle Stand (unveränderlich, nie blockierend).</summary>
    KisscamLibrarySnapshot Current { get; }

    /// <summary>Der Stand hat sich geändert; kann auf einem beliebigen Thread kommen.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Wählt den Musikordner (null/leer = <see cref="AppInfo.DefaultKisscamMusicFolder"/>), legt ihn bei Bedarf an und liest ihn neu.
    /// Derselbe Ordner bei laufendem Betrieb ist wirkungslos (außer er war nicht erreichbar).
    /// </summary>
    void SetFolder(string? folder);

    /// <summary>Liest Ordner und Config neu (Knopf „Aktualisieren“); hebt auch die Markierung „nicht abspielbar“ auf.</summary>
    void Refresh();

    /// <summary>
    /// Ändert Häkchen, Startzeit und/oder Lautstärke eines Songs (null = unverändert). Der Stand ändert sich sofort, die Config wird
    /// entprellt gespeichert. Unbekannte Dateinamen werden ignoriert.
    /// </summary>
    void UpdateSong(string fileName, bool? enabled = null, int? startSeconds = null, int? volumePercent = null);

    /// <summary>Markiert den Song mit diesem Pfad als nicht abspielbar (der Player hat ihn nicht öffnen können).</summary>
    void MarkUnplayable(string path);
}
