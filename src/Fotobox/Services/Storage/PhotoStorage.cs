using System.Globalization;
using System.IO;
using System.Security;
using System.Windows.Media.Imaging;
using Fotobox.Core;

namespace Fotobox.Services.Storage;

/// <summary>
/// Speichert Layout und Originale als JPEG (zuerst temporäre Datei, dann Umbenennen). Ist der konfigurierte Ordner
/// nicht beschreibbar oder nicht vorhanden, wird auf den Standardordner (Bilder\Fotobox) ausgewichen; die Pfade im
/// Ergebnis nennen den tatsächlich verwendeten Ordner. Ordner- und Dateinamen sind kulturunabhängig.
/// </summary>
public sealed class PhotoStorage : IPhotoStorage
{
    // Verhindert Namenskollisionen zwischen parallelen Speichervorgängen im Prozess.
    private static readonly object NamingGate = new();

    private readonly string _defaultRoot;

    /// <param name="defaultRoot">Standard- bzw. Ausweichordner (null = Bilder\Fotobox); nur für Tests gedacht.</param>
    public PhotoStorage(string? defaultRoot = null)
    {
        _defaultRoot = string.IsNullOrWhiteSpace(defaultRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Fotobox")
            : defaultRoot;
    }

    public string ResolveTargetFolder(StorageSettings storage, DateTime timestamp) =>
        WithDateSubfolder(storage, string.IsNullOrWhiteSpace(storage.PhotoFolder) ? _defaultRoot : storage.PhotoFolder, timestamp);

    private static string WithDateSubfolder(StorageSettings storage, string root, DateTime timestamp) =>
        storage.DateSubfolder ? Path.Combine(root, timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : root;

    public async Task<SavedPhotoSet> SaveAsync(BitmapSource layout, IReadOnlyList<BitmapSource> originals,
        StorageSettings storage, DateTime timestamp, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(originals);
        ArgumentNullException.ThrowIfNull(storage);
        ct.ThrowIfCancellationRequested();

        var folder = ResolveTargetFolder(storage, timestamp);
        var quality = Math.Clamp(storage.JpegQuality, SettingsLimits.JpegQualityMin, SettingsLimits.JpegQualityMax);
        var prefix = SettingsNormalizer.SanitizePrefix(storage.FilePrefix);
        var sources = storage.SaveOriginals ? originals.ToList() : [];

        FreezeIfPossible(layout);
        foreach (var o in sources)
            FreezeIfPossible(o);

        try
        {
            return await SaveToFolderAsync(layout, sources, folder, prefix, quality, timestamp, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            // Konfigurierter Ordner nicht beschreibbar bzw. nicht vorhanden → Ausweichen auf den Standardordner.
            var fallback = WithDateSubfolder(storage, _defaultRoot, timestamp);
            if (SamePath(fallback, folder))
            {
                AppLog.Error($"Fotos konnten nicht in '{folder}' gespeichert werden.", ex);
                throw new IOException($"Fotos können nicht gespeichert werden: {ex.Message}", ex);
            }

            AppLog.Warn($"Fotos konnten nicht in '{folder}' gespeichert werden – Ausweichordner '{fallback}' wird verwendet.", ex);
            try
            {
                return await SaveToFolderAsync(layout, sources, fallback, prefix, quality, timestamp, ct).ConfigureAwait(false);
            }
            catch (Exception ex2) when (IsStorageError(ex2))
            {
                AppLog.Error($"Fotos konnten auch nicht im Ausweichordner '{fallback}' gespeichert werden.", ex2);
                throw new IOException($"Fotos können nicht gespeichert werden: {ex.Message}", ex);
            }
        }
    }

    private static Task<SavedPhotoSet> SaveToFolderAsync(BitmapSource layout, List<BitmapSource> sources, string folder,
        string prefix, int quality, DateTime timestamp, CancellationToken ct) =>
        Task.Run(() => SaveCore(layout, sources, folder, prefix, quality, timestamp, ct), ct);

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsStorageError(Exception ex) =>
        ex is UnauthorizedAccessException or IOException or NotSupportedException or ArgumentException or SecurityException;

    private static SavedPhotoSet SaveCore(BitmapSource layout, List<BitmapSource> originals, string folder,
        string prefix, int quality, DateTime timestamp, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);

        var tmpFiles = new List<string>();
        var moved = new List<string>();
        try
        {
            // Alles zuerst als temporäre Dateien schreiben (Layout zuerst, dann Originale).
            var sources = new List<BitmapSource>(originals.Count + 1) { layout };
            sources.AddRange(originals);
            foreach (var src in sources)
            {
                ct.ThrowIfCancellationRequested();
                var tmp = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
                tmpFiles.Add(tmp);
                WriteJpeg(src, tmp, quality);
            }

            ct.ThrowIfCancellationRequested();
            var baseName = string.Create(CultureInfo.InvariantCulture, $"{prefix}_{timestamp:yyyyMMdd_HHmmss}");

            lock (NamingGate)
            {
                // Kollision: Suffix _2, _3 … bis Layout- und alle Original-Namen frei sind.
                string[] targets;
                for (var attempt = 1; ; attempt++)
                {
                    var stem = attempt == 1 ? baseName : $"{baseName}_{attempt}";
                    targets = new string[sources.Count];
                    targets[0] = Path.Combine(folder, stem + ".jpg");
                    for (var i = 1; i < targets.Length; i++)
                        targets[i] = Path.Combine(folder, $"{stem}_orig{i}.jpg");
                    if (!targets.Any(File.Exists))
                        break;
                }

                try
                {
                    for (var i = 0; i < targets.Length; i++)
                    {
                        File.Move(tmpFiles[i], targets[i]);
                        moved.Add(targets[i]);
                    }
                }
                catch
                {
                    foreach (var m in moved)
                        TryDelete(m);
                    throw;
                }

                return new SavedPhotoSet(targets[0], targets.Skip(1).ToList());
            }
        }
        finally
        {
            foreach (var t in tmpFiles)
                TryDelete(t);
        }
    }

    private static void WriteJpeg(BitmapSource source, string path, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(fs);
        fs.Flush(true);
    }

    private static void FreezeIfPossible(BitmapSource source)
    {
        if (!source.IsFrozen && source.CanFreeze)
            source.Freeze();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* best effort */ }
    }
}
