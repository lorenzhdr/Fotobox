using System.Windows.Media.Imaging;
using Fotobox.Core;

namespace Fotobox.ViewModels.Admin;

/// <summary>UI-Dienste der Admin-Ansicht (Dialoge, Explorer, Testseite) – für Tests austauschbar.</summary>
public interface IAdminUi
{
    /// <summary>Dateiauswahl für das Overlay (PNG). null = abgebrochen.</summary>
    string? PickOverlayFile();

    /// <summary>Dateidialog für eine Audiodatei (WAV/MP3), z. B. die Kisscam-Musik. null = abgebrochen.</summary>
    string? PickAudioFile() => null;

    /// <summary>Ordnerauswahl mit dem Dialogtitel <paramref name="title"/>. null = abgebrochen.</summary>
    string? PickFolder(string? initialFolder, string title);

    bool Confirm(string title, string message);

    /// <summary>Hinweis mit OK-Taste (Ergebnis oder Fehler von Export/Import).</summary>
    void ShowInfo(string title, string message);

    /// <summary>„Speichern unter“-Dialog für die Einstellungsdatei mit vorgeschlagenem Dateinamen. null = abgebrochen.</summary>
    string? PickExportFile(string suggestedFileName);

    /// <summary>Dateiauswahl für den Import einer Einstellungsdatei. null = abgebrochen.</summary>
    string? PickImportFile();

    /// <summary>Öffnet den Ordner im Explorer (legt ihn nicht an; der Aufrufer prüft vorher, ob er existiert).</summary>
    void OpenFolder(string path);

    /// <summary>Rendert die Testseite in exakt der Pixelgröße der Seite. Läuft auf dem UI-Thread.</summary>
    BitmapSource RenderTestPage(PageSpec page);
}
