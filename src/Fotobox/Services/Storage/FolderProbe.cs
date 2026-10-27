using System.IO;
using System.Security;

namespace Fotobox.Services.Storage;

/// <summary>Prüft, ob ein Ordner angelegt und beschrieben werden kann (echter Schreibtest mit kleiner Temp-Datei).</summary>
public static class FolderProbe
{
    /// <summary>
    /// Existiert der Ordner? Die Abfrage läuft auf dem Threadpool (ein hängender Netzpfad blockiert sonst den Aufrufer bis zum
    /// SMB-Timeout). <c>null</c> bei Zeitüberschreitung; der Hintergrundthread läuft dann noch bis zum Ende der Abfrage weiter.
    /// </summary>
    public static async Task<bool?> ExistsAsync(string folder, TimeSpan timeout)
    {
        try
        {
            return await Task.Run(() => Directory.Exists(folder)).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Der Ordner selbst oder der nächste vorhandene übergeordnete Ordner (Startordner für Auswahldialoge). Blockiert höchstens
    /// <paramref name="timeout"/>; <c>null</c>, wenn keiner gefunden wurde oder die Abfrage zu lange dauert.
    /// </summary>
    public static string? FindExistingFolderOrParent(string? folder, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;

        var task = Task.Run(() =>
        {
            try
            {
                var probe = folder;
                while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
                    probe = Path.GetDirectoryName(probe);
                return string.IsNullOrEmpty(probe) ? null : probe;
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException or SecurityException)
            {
                return null;
            }
        });
        return task.Wait(timeout) ? task.Result : null;
    }

    /// <summary>Legt den Ordner bei Bedarf an, schreibt eine kleine Temp-Datei und löscht sie wieder.</summary>
    /// <param name="folder">Zu prüfender Ordner.</param>
    /// <param name="error">Bei Misserfolg die Ursache (Systemmeldung), sonst <c>null</c>.</param>
    public static bool TryEnsureWritable(string folder, out string? error)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".fotobox-test-{Guid.NewGuid():N}.tmp");
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 1, FileOptions.DeleteOnClose))
            {
                fs.WriteByte(0);
                fs.Flush(true);
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException
                                       or ArgumentException or SecurityException)
        {
            error = ex.Message;
            return false;
        }
    }
}
