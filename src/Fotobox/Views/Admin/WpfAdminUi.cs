using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Services.Storage;
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

    public string? PickAudioFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Musikdatei für die Kisscam auswählen",
            Filter = "Audiodateien (*.wav;*.mp3)|*.wav;*.mp3",
            CheckFileExists = true,
            Multiselect = false,
        };
        return (Owner is null ? dialog.ShowDialog() : dialog.ShowDialog(Owner)) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string? initialFolder, string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        // Existiert der Ordner noch nicht, den nächsten vorhandenen übergeordneten verwenden (mit Timeout: Netzpfad).
        var start = FolderProbe.FindExistingFolderOrParent(initialFolder, TimeSpan.FromSeconds(3));
        if (start is not null)
            dialog.InitialDirectory = start;
        return (Owner is null ? dialog.ShowDialog() : dialog.ShowDialog(Owner)) == true ? dialog.FolderName : null;
    }

    public bool Confirm(string title, string message) =>
        (Owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(Owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No))
        == MessageBoxResult.Yes;

    public void ShowInfo(string title, string message)
    {
        if (Owner is null)
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public string? PickExportFile(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Einstellungen exportieren",
            Filter = SettingsTransfer.FileFilter,
            DefaultExt = ".json",
            AddExtension = true,
            FileName = suggestedFileName,
            OverwritePrompt = true,
        };
        return (Owner is null ? dialog.ShowDialog() : dialog.ShowDialog(Owner)) == true ? dialog.FileName : null;
    }

    public string? PickImportFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Einstellungen importieren",
            Filter = SettingsTransfer.FileFilter,
            CheckFileExists = true,
            Multiselect = false,
        };
        return (Owner is null ? dialog.ShowDialog() : dialog.ShowDialog(Owner)) == true ? dialog.FileName : null;
    }

    public void OpenFolder(string path)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public BitmapSource RenderTestPage(PageSpec page) => TestPageRenderer.Render(page);
}
