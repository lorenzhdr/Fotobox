using System.Windows.Media.Imaging;
using Fotobox.Core;

namespace Fotobox.ViewModels.Admin;

/// <summary>UI-Dienste der Admin-Ansicht (Dialoge, Explorer, Testseite) – für Tests austauschbar.</summary>
public interface IAdminUi
{
    /// <summary>Dateiauswahl für das Overlay (PNG). null = abgebrochen.</summary>
    string? PickOverlayFile();

    /// <summary>Ordnerauswahl mit dem Dialogtitel <paramref name="title"/>. null = abgebrochen.</summary>
    string? PickFolder(string? initialFolder, string title);

    bool Confirm(string title, string message);

    /// <summary>Öffnet den Ordner im Explorer (legt ihn nicht an; der Aufrufer prüft vorher, ob er existiert).</summary>
    void OpenFolder(string path);

    /// <summary>Rendert die Testseite in exakt der Pixelgröße der Seite. Läuft auf dem UI-Thread.</summary>
    BitmapSource RenderTestPage(PageSpec page);
}
