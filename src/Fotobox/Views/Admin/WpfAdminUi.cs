using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.ViewModels.Admin;
using Microsoft.Win32;

namespace Fotobox.Views.Admin;

/// <summary>WPF-Implementierung der Dialog- und Explorer-Dienste für die Admin-Ansicht.</summary>
public sealed class WpfAdminUi : IAdminUi
{
    /// <summary>Besitzerfenster der Dialoge (das Admin-Fenster); wird nach dessen Erzeugung gesetzt.</summary>
    public Window? Owner { get; set; }

    public string? PickOverlayFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Overlay auswählen",
            Filter = "PNG-Bilder (*.png)|*.png",
            CheckFileExists = true,
            Multiselect = false,
        };
        return (Owner is null ? dialog.ShowDialog() : dialog.ShowDialog(Owner)) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string? initialFolder)
    {
        var dialog = new OpenFolderDialog { Title = "Speicherordner für die Fotos auswählen" };
        if (!string.IsNullOrWhiteSpace(initialFolder))
        {
            // Existiert der Ordner noch nicht, den nächsten vorhandenen übergeordneten verwenden.
            var probe = initialFolder;
            while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
                probe = Path.GetDirectoryName(probe);
            if (!string.IsNullOrEmpty(probe))
                dialog.InitialDirectory = probe;
        }
        return (Owner is null ? dialog.ShowDialog() : dialog.ShowDialog(Owner)) == true ? dialog.FolderName : null;
    }

    public bool Confirm(string title, string message) =>
        (Owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(Owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No))
        == MessageBoxResult.Yes;

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public BitmapSource RenderTestPage(PageSpec page) => TestPageRenderer.Render(page);
}
