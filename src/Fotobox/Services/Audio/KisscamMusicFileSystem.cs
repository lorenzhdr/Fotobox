using System.IO;
using Fotobox.Core.Music;
using Windows.Storage;

namespace Fotobox.Services.Audio;

/// <summary>
/// Echtes Dateisystem der Musikbibliothek. Blockiert und wirft nach Belieben; die Bibliothek ruft es nur auf dem Threadpool (MTA) und
/// mit Frist auf (das ist auch die Voraussetzung für die WinRT-Aufrufe der Songlänge).
/// </summary>
public sealed class KisscamMusicFileSystem : IKisscamMusicFileSystem
{
    // Ein Schreiber zur Zeit (die temporäre Datei hat einen festen Namen).
    private static readonly object WriteLock = new();

    public void EnsureDirectory(string folder) => Directory.CreateDirectory(folder);

    public IReadOnlyList<KisscamFileEntry> ListSongs(string folder)
    {
        var result = new List<KisscamFileEntry>();
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            if (!KisscamMusicFiles.IsSong(file.Name))
                continue;
            result.Add(new KisscamFileEntry(file.Name, file.FullName, file.Length, file.LastWriteTimeUtc));
        }
        return result;
    }

    public string? ReadConfig(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public void WriteConfigAtomic(string path, string content)
    {
        lock (WriteLock)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            try
            {
                if (File.Exists(path))
                    File.Replace(temp, path, destinationBackupFileName: null);
                else
                    File.Move(temp, path);
            }
            catch
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // Aufräumen darf den eigentlichen Fehler nicht verdecken.
                }
                throw;
            }
        }
    }

    public void MoveBroken(string path, string brokenPath) => File.Move(path, brokenPath, overwrite: true);

    public TimeSpan? ReadDuration(string path)
    {
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        var properties = file.Properties.GetMusicPropertiesAsync().AsTask().GetAwaiter().GetResult();
        var duration = properties.Duration;
        return duration > TimeSpan.Zero ? duration : null;
    }

    public IDisposable Watch(string folder, Action changed, Action<Exception> failed)
    {
        var watcher = new FileSystemWatcher(folder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };
        // Nur die Musikendungen: Das Schreiben der Config (und ihrer temporären Datei) löst so keine Schleife aus.
        foreach (var extension in KisscamMusicFiles.Extensions)
            watcher.Filters.Add("*" + extension);
        watcher.Created += (_, _) => changed();
        watcher.Deleted += (_, _) => changed();
        watcher.Changed += (_, _) => changed();
        watcher.Renamed += (_, _) => changed();
        watcher.Error += (_, e) => failed(e.GetException());
        watcher.EnableRaisingEvents = true;
        return watcher;
    }
}
