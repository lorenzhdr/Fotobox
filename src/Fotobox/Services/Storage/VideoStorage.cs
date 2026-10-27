using System.Globalization;
using System.IO;
using Fotobox.Core;
using Fotobox.Services.Camera;

namespace Fotobox.Services.Storage;

/// <summary>
/// Ziel für Videoaufnahmen (Gegenstück zu <see cref="PhotoStorage"/>): legt den Ordner an (Ausweichen auf den Standardordner
/// Videos\Fotobox+, wenn der konfigurierte nicht beschreibbar ist), prüft den freien Platz und vergibt eine temporäre Zieldatei.
/// Erst <see cref="CommitAsync"/> benennt sie in den endgültigen Namen um. Ordner- und Dateinamen sind kulturunabhängig.
/// </summary>
public sealed class VideoStorage : IVideoStorage
{
    // Verhindert Namenskollisionen zwischen parallelen Speichervorgängen im Prozess.
    private static readonly object NamingGate = new();

    // Alles, was an einem Netzpfad hängen kann, hat eine Frist.
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan FreeSpaceTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan CommitTimeout = TimeSpan.FromSeconds(30);

    private readonly string _defaultRoot;
    private readonly Func<string, long?> _freeSpace;

    /// <param name="defaultRoot">Standard- bzw. Ausweichordner (null = <see cref="AppInfo.DefaultVideoFolder"/>); für Tests.</param>
    /// <param name="freeSpaceProvider">Freier Platz in Bytes für einen Ordner, null = unbekannt (Prüfung entfällt); für Tests.</param>
    public VideoStorage(string? defaultRoot = null, Func<string, long?>? freeSpaceProvider = null)
    {
        _defaultRoot = string.IsNullOrWhiteSpace(defaultRoot) ? AppInfo.DefaultVideoFolder : defaultRoot;
        _freeSpace = freeSpaceProvider ?? QueryFreeSpace;
    }

    public string ResolveTargetFolder(VideoSettings video, DateTime timestamp)
    {
        ArgumentNullException.ThrowIfNull(video);
        return WithDateSubfolder(video, string.IsNullOrWhiteSpace(video.Folder) ? _defaultRoot : video.Folder, timestamp);
    }

    private static string WithDateSubfolder(VideoSettings video, string root, DateTime timestamp) =>
        video.DateSubfolder ? Path.Combine(root, timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : root;

    /// <summary>Benötigter freier Platz für eine Aufnahme in voller Länge (Schätzung inkl. Ton plus Reserve).</summary>
    internal static long RequiredFreeBytes(VideoSettings video, CameraResolution? resolution, TimeSpan maxDuration) =>
        VideoBitrate.RequiredFreeBytes(video.Quality, resolution, maxDuration, withAudio: true);

    /// <summary>Reicht der freie Platz? Unbekannter Platz (null) gilt als ausreichend; die Prüfung entfällt dann.</summary>
    internal static bool HasEnoughSpace(long? freeBytes, long requiredBytes) => freeBytes is not { } free || free >= requiredBytes;

    public async Task<VideoTarget> PrepareAsync(VideoSettings video, CameraResolution? resolution, TimeSpan maxDuration,
        DateTime timestamp, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        ct.ThrowIfCancellationRequested();

        var configured = ResolveTargetFolder(video, timestamp);
        var folder = configured;
        var prefix = ResolvePrefix(video.FilePrefix);

        var error = await ProbeAsync(folder, ct).ConfigureAwait(false);
        if (error is not null)
        {
            // Konfigurierter Ordner nicht beschreibbar bzw. nicht vorhanden → einmal auf den Standardordner ausweichen.
            var fallback = WithDateSubfolder(video, _defaultRoot, timestamp);
            if (PhotoStorage.SamePath(fallback, folder))
            {
                AppLog.Error($"Videos können nicht in '{folder}' gespeichert werden: {error}");
                throw new IOException($"Videos können nicht gespeichert werden: {error}");
            }

            AppLog.Warn($"Videos können nicht in '{folder}' gespeichert werden ({error}) – Ausweichordner '{fallback}' wird verwendet.");
            var fallbackError = await ProbeAsync(fallback, ct).ConfigureAwait(false);
            if (fallbackError is not null)
            {
                AppLog.Error($"Videos können auch nicht im Ausweichordner '{fallback}' gespeichert werden: {fallbackError}");
                throw new IOException($"Videos können nicht gespeichert werden: {error}");
            }

            folder = fallback;
        }

        // Bewusst außerhalb des Ordner-Rückfalls: InsufficientDiskSpaceException ist eine IOException und würde dort
        // sonst als Speicherfehler des Ordners gelten.
        await EnsureFreeSpaceAsync(folder, RequiredFreeBytes(video, resolution, maxDuration), ct).ConfigureAwait(false);

        var tempPath = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp.mp4");
        return new VideoTarget(folder, tempPath, prefix, timestamp);
    }

    // Legt den Ordner an und schreibt eine kleine Testdatei, auf dem Threadpool mit Frist. null = in Ordnung, sonst die Ursache.
    private static async Task<string?> ProbeAsync(string folder, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => FolderProbe.TryEnsureWritable(folder, out var error) ? null : error, ct)
                .WaitAsync(ProbeTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return "Zeitüberschreitung beim Zugriff auf den Ordner.";
        }
    }

    private async Task EnsureFreeSpaceAsync(string folder, long requiredBytes, CancellationToken ct)
    {
        long? free;
        try
        {
            free = await Task.Run(() => _freeSpace(folder), ct).WaitAsync(FreeSpaceTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            AppLog.Warn($"Freier Speicherplatz von '{folder}' nicht rechtzeitig ermittelt – Prüfung übersprungen.");
            return;
        }

        if (free is null)
            return;

        if (!HasEnoughSpace(free, requiredBytes))
        {
            var freeMb = free.Value / (1024 * 1024);
            var neededMb = (requiredBytes + 1024 * 1024 - 1) / (1024 * 1024);
            AppLog.Warn($"Zu wenig Speicherplatz für ein Video: {freeMb} MB frei, {neededMb} MB nötig ('{folder}').");
            throw new InsufficientDiskSpaceException(
                $"Nicht genug freier Speicherplatz für eine Videoaufnahme: {freeMb.ToString(CultureInfo.InvariantCulture)} MB frei, " +
                $"{neededMb.ToString(CultureInfo.InvariantCulture)} MB nötig.");
        }
    }

    // Freier Platz des Laufwerks; null bei Netzpfad (UNC) oder Fehler (Prüfung entfällt, Log).
    private static long? QueryFreeSpace(string folder)
    {
        try
        {
            var full = Path.GetFullPath(folder);
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
            {
                AppLog.Info($"Netzpfad '{folder}': Prüfung des freien Speicherplatzes entfällt.");
                return null;
            }

            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
                return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (PhotoStorage.IsStorageError(ex))
        {
            AppLog.Warn($"Freier Speicherplatz von '{folder}' nicht ermittelbar – Prüfung übersprungen.", ex);
            return null;
        }
    }

    // Leerer oder nach dem Bereinigen leerer Präfix → Video-Standard.
    internal static string ResolvePrefix(string? prefix) =>
        SettingsNormalizer.SanitizePrefix(prefix, VideoSettings.DefaultFilePrefix);

    public async Task<string> CommitAsync(VideoTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ct.ThrowIfCancellationRequested();
        try
        {
            return await Task.Run(() => CommitCore(target), ct).WaitAsync(CommitTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            AppLog.Error("Video konnte nicht rechtzeitig gespeichert werden.", ex);
            throw new IOException("Das Video konnte nicht rechtzeitig gespeichert werden.", ex);
        }
    }

    private static string CommitCore(VideoTarget target)
    {
        var info = new FileInfo(target.TempPath);
        if (!info.Exists)
            throw new FileNotFoundException("Die Videodatei fehlt.", target.TempPath);
        if (info.Length == 0)
            throw new IOException("Die Videodatei ist leer.");

        var baseName = string.Create(CultureInfo.InvariantCulture, $"{ResolvePrefix(target.FilePrefix)}_{target.Timestamp:yyyyMMdd_HHmmss}");
        lock (NamingGate)
        {
            // Kollision: Suffix _2, _3 …
            for (var attempt = 1; ; attempt++)
            {
                var stem = attempt == 1 ? baseName : $"{baseName}_{attempt}";
                var path = Path.Combine(target.Folder, stem + ".mp4");
                if (File.Exists(path))
                    continue;
                File.Move(target.TempPath, path);
                return path;
            }
        }
    }

    public void Discard(VideoTarget target)
    {
        try
        {
            if (target?.TempPath is { Length: > 0 } path)
                File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Temporäre Videodatei konnte nicht gelöscht werden.", ex);
        }
    }
}
