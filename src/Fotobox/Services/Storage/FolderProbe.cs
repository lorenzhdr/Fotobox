using System.IO;
using System.Security;

namespace Fotobox.Services.Storage;

/// <summary>Prüft, ob ein Ordner angelegt und beschrieben werden kann (echter Schreibtest mit kleiner Temp-Datei).</summary>
public static class FolderProbe
{
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
