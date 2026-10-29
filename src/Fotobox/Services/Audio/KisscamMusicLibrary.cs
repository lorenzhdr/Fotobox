using System.IO;
using Fotobox.Core;
using Fotobox.Core.Music;

namespace Fotobox.Services.Audio;

/// <summary>
/// Die Musikbibliothek der Kisscam (Singleton für Gastansicht und Admin): listet die Songs des Musikordners, hält die Config
/// <c>kisscam-musik.json</c> und meldet Änderungen. <see cref="Current"/> ist ein unveränderlicher Stand und blockiert nie. Alle
/// Dateizugriffe laufen nacheinander auf einem Hintergrund-Worker, jeder als <c>Task.Run(...).WaitAsync(Frist)</c>; hängt ein alter
/// Zugriff noch, startet kein neuer (der Stand wird <see cref="KisscamLibraryStatus.FolderUnavailable"/>). Keine Methode wirft.
/// <para>
/// Bereinigt wird die Config (Einträge zu fehlenden Dateien) nur nach vollständig erfolgreicher Auflistung und gelesener Config. Eine
/// unlesbare Config (<see cref="KisscamLibraryStatus.ConfigBroken"/>) wird nie überschrieben: Erst die nächste Änderung im Admin
/// verschiebt sie nach <c>kisscam-musik.json.defekt</c>. Bei Lesefehlern bleibt der letzte bekannte Stand, und es wird nicht geschrieben,
/// solange nie eine Config gelesen wurde.
/// </para>
/// </summary>
public sealed class KisscamMusicLibrary : IKisscamMusicLibrary, IDisposable
{
    /// <summary>Frist zum Anlegen oder Prüfen des Ordners und zum Anlegen des Watchers.</summary>
    internal static readonly TimeSpan EnsureFolderTimeout = TimeSpan.FromSeconds(3);
    /// <summary>Frist zum Auflisten des Ordners.</summary>
    internal static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(3);
    /// <summary>Frist zum Lesen der Config.</summary>
    internal static readonly TimeSpan ReadConfigTimeout = TimeSpan.FromSeconds(2);
    /// <summary>Frist zum Schreiben (und ggf. Verschieben) der Config.</summary>
    internal static readonly TimeSpan WriteConfigTimeout = TimeSpan.FromSeconds(3);
    /// <summary>Frist zum Lesen der Länge eines Songs.</summary>
    internal static readonly TimeSpan DurationTimeout = TimeSpan.FromSeconds(2);
    /// <summary>Entprellung der Ordnerbeobachtung.</summary>
    internal static readonly TimeSpan WatcherDebounce = TimeSpan.FromMilliseconds(500);
    /// <summary>Entprellung des Speicherns nach einer Änderung.</summary>
    internal static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(300);
    /// <summary>Längste Wartezeit von <see cref="Dispose"/> (für das letzte Speichern).</summary>
    internal static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(1);

    private const int DurationPublishBatch = 10;

    private readonly IKisscamMusicFileSystem _fs;
    private readonly TimeProvider _time;
    private readonly IMusicPlayer? _player;
    private readonly object _gate = new();

    private KisscamLibrarySnapshot _snapshot = KisscamLibrarySnapshot.Empty;
    private bool _disposed;

    // Zustand des aktuellen Ordners (unter _gate); _epoch zählt Ordnerwechsel und macht ältere Aufträge wirkungslos.
    private int _epoch;
    private string? _folder;
    private KisscamMusicConfig _config = new();
    private bool _configBroken;
    private bool _configLoaded;
    private bool _configUnreliable;
    private bool _saveFailed;
    private int _dirtyVersion;
    private int _savedVersion;
    private IReadOnlyList<KisscamFileEntry> _listing = [];
    private readonly Dictionary<string, (DateTime LastWrite, long Length)> _unplayable = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime LastWrite, long Length, TimeSpan? Duration)> _durations = new(StringComparer.OrdinalIgnoreCase);
    private ITimer? _saveTimer;
    private ITimer? _watchTimer;
    private IDisposable? _watcher;
    private bool _watcherFailed;
    private bool _watcherStarting;
    private Task? _hungTask;
    private Task? _hungDurationTask;

    // Serieller Worker.
    private readonly Queue<Func<Task>> _queue = new();
    private bool _running;
    private bool _loadQueued;

    public KisscamMusicLibrary(IKisscamMusicFileSystem fileSystem, TimeProvider? time = null, IMusicPlayer? player = null)
    {
        _fs = fileSystem;
        _time = time ?? TimeProvider.System;
        _player = player;
        if (_player is not null)
            _player.PlaybackFailed += OnPlaybackFailed;
    }

    public KisscamLibrarySnapshot Current => Volatile.Read(ref _snapshot);

    public event EventHandler? Changed;

    // ------------------------------------------------------------------ Öffentliche Operationen

    public void SetFolder(string? folder)
    {
        try
        {
            SetFolderCore(folder);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Der Kisscam-Musikordner konnte nicht gesetzt werden.", ex);
        }
    }

    public void Refresh()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || _folder is null)
                    return;
                _unplayable.Clear();
                if (_loadQueued)
                    return;
                _loadQueued = true;
                var epoch = _epoch;
                EnqueueLocked(() => LoadAsync(epoch));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Der Kisscam-Musikordner konnte nicht aktualisiert werden.", ex);
        }
    }

    public void UpdateSong(string fileName, bool? enabled = null, int? startSeconds = null, int? volumePercent = null)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || _folder is null || string.IsNullOrEmpty(fileName))
                    return;
                var entry = _listing.FirstOrDefault(e => string.Equals(e.Name, fileName, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                    return;

                var song = _config.Songs.FirstOrDefault(s => string.Equals(s.FileName, entry.Name, StringComparison.OrdinalIgnoreCase));
                var current = song ?? new KisscamSongConfig { FileName = entry.Name };
                var next = current.Clone();
                if (enabled is { } newEnabled)
                    next.Enabled = newEnabled;
                if (startSeconds is { } newStart)
                    next.StartSeconds = KisscamMusicConfigCodec.ClampStart(newStart);
                if (volumePercent is { } newVolume)
                    next.VolumePercent = KisscamMusicConfigCodec.ClampVolume(newVolume);
                if (Same(current, next))
                    return;
                if (song is null)
                {
                    song = new KisscamSongConfig { FileName = entry.Name };
                    _config.Songs.Add(song);
                }
                song.Enabled = next.Enabled;
                song.StartSeconds = next.StartSeconds;
                song.VolumePercent = next.VolumePercent;

                _dirtyVersion++;
                _saveFailed = false;
                PublishLocked();
                ScheduleSaveLocked();
            }
            RaiseChanged();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Eine Kisscam-Songeinstellung konnte nicht übernommen werden.", ex);
        }
    }

    public void MarkUnplayable(string path)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || _folder is null || string.IsNullOrWhiteSpace(path))
                    return;
                var entry = _listing.FirstOrDefault(e => string.Equals(e.FullPath, path, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                    return;
                _unplayable[entry.FullPath] = (entry.LastWriteUtc, entry.Length);
                PublishLocked();
            }
            AppLog.Warn($"Kisscam-Song nicht abspielbar: {Path.GetFileName(path)}");
            RaiseChanged();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Ein Kisscam-Song konnte nicht als nicht abspielbar markiert werden.", ex);
        }
    }

    /// <summary>Beendet Watcher und Timer und speichert eine noch ausstehende Änderung (höchstens <see cref="DisposeWait"/>).</summary>
    public void Dispose()
    {
        SaveJob? job = null;
        IDisposable? watcher;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _saveTimer?.Dispose();
            _saveTimer = null;
            _watchTimer?.Dispose();
            _watchTimer = null;
            watcher = _watcher;
            _watcher = null;
            _queue.Clear();
            _running = false;
            if (_player is not null)
                _player.PlaybackFailed -= OnPlaybackFailed;
            if (_folder is not null && _dirtyVersion != _savedVersion && !_configUnreliable && !(_hungTask is { IsCompleted: false }))
                job = CaptureJobLocked();
        }

        try
        {
            if (job is not null)
            {
                var pending = job;
                if (!Task.Run(() => WriteJob(pending)).Wait(DisposeWait))
                    AppLog.Warn("Die Kisscam-Musikconfig konnte beim Beenden nicht rechtzeitig gespeichert werden.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Die Kisscam-Musikconfig konnte beim Beenden nicht gespeichert werden.", ex);
        }

        if (watcher is not null)
            _ = Task.Run(() => SafeDispose(watcher));
    }

    // ------------------------------------------------------------------ Ordnerwechsel

    private void SetFolderCore(string? folder)
    {
        string? resolved;
        try
        {
            resolved = Resolve(folder);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Der Kisscam-Musikordner ist ungültig.", ex);
            resolved = null;
        }

        IDisposable? oldWatcher;
        lock (_gate)
        {
            if (_disposed)
                return;
            if (resolved is not null && _folder is not null
                && string.Equals(_folder, resolved, StringComparison.OrdinalIgnoreCase)
                && _snapshot.Status != KisscamLibraryStatus.FolderUnavailable)
                return;

            // Eine ausstehende Änderung gehört noch dem alten Ordner: zuerst speichern.
            if (_folder is not null && _dirtyVersion != _savedVersion && !_configUnreliable)
            {
                var job = CaptureJobLocked();
                EnqueueLocked(() => ExecuteSaveAsync(job));
            }

            _epoch++;
            _folder = resolved;
            _config = new KisscamMusicConfig();
            _configBroken = false;
            _configLoaded = false;
            _configUnreliable = false;
            _saveFailed = false;
            _dirtyVersion = 0;
            _savedVersion = 0;
            _listing = [];
            _unplayable.Clear();
            _watcherFailed = false;
            _watcherStarting = false;
            _saveTimer?.Dispose();
            _saveTimer = null;
            _watchTimer?.Dispose();
            _watchTimer = null;
            oldWatcher = _watcher;
            _watcher = null;

            if (resolved is null)
            {
                _snapshot = new KisscamLibrarySnapshot(string.Empty, KisscamLibraryStatus.FolderUnavailable, [], false);
            }
            else
            {
                _snapshot = new KisscamLibrarySnapshot(resolved, KisscamLibraryStatus.Loading, [], false);
                _loadQueued = true;
                var epoch = _epoch;
                EnqueueLocked(() => LoadAsync(epoch));
            }
        }

        if (oldWatcher is not null)
            _ = Task.Run(() => SafeDispose(oldWatcher));
        RaiseChanged();
    }

    private static string Resolve(string? folder)
    {
        var path = string.IsNullOrWhiteSpace(folder) ? AppInfo.DefaultKisscamMusicFolder : folder.Trim();
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    // ------------------------------------------------------------------ Worker

    private void EnqueueLocked(Func<Task> work)
    {
        if (_disposed)
            return;
        _queue.Enqueue(work);
        if (_running)
            return;
        _running = true;
        _ = Task.Run(RunQueueAsync);
    }

    private async Task RunQueueAsync()
    {
        while (true)
        {
            Func<Task> work;
            lock (_gate)
            {
                if (_disposed || !_queue.TryDequeue(out work!))
                {
                    _running = false;
                    return;
                }
            }

            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Ein Auftrag der Kisscam-Musikbibliothek ist fehlgeschlagen.", ex);
            }
        }
    }

    private sealed class HungException : IOException
    {
        public HungException() : base("Ein früherer Dateizugriff hängt noch.")
        {
        }
    }

    // Ein Dateizugriff auf dem Threadpool mit Frist. Hängt ein früherer noch, startet kein neuer. Der hängende läuft im Hintergrund
    // weiter (die Aufgabe wird nie abgewartet), sein spätes Ergebnis wird verworfen.
    private async Task<T> RunFsAsync<T>(Func<T> func, TimeSpan timeout)
    {
        lock (_gate)
        {
            if (_hungTask is { IsCompleted: false })
                throw new HungException();
        }

        var task = Task.Run(func);
        _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        try
        {
            return await task.WaitAsync(timeout, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (_gate)
                _hungTask = task;
            throw;
        }
    }

    // ------------------------------------------------------------------ Laden

    private async Task LoadAsync(int epoch)
    {
        string folder;
        lock (_gate)
        {
            if (_disposed || epoch != _epoch || _folder is null)
                return;
            _loadQueued = false;
            folder = _folder;
        }

        try
        {
            await RunFsAsync(() => { _fs.EnsureDirectory(folder); return 0; }, EnsureFolderTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Unavailable(epoch, "angelegt", ex);
            return;
        }

        IReadOnlyList<KisscamFileEntry> entries;
        try
        {
            entries = await RunFsAsync(() => _fs.ListSongs(folder), ListTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Unavailable(epoch, "aufgelistet", ex);
            return;
        }

        var configPath = Path.Combine(folder, KisscamMusicFiles.ConfigFileName);
        var readFailed = false;
        KisscamMusicConfig? parsed = null;
        var broken = false;
        try
        {
            var text = await RunFsAsync(() => _fs.ReadConfig(configPath), ReadConfigTimeout).ConfigureAwait(false);
            if (text is null)
                parsed = null; // keine Datei: leere Config
            else if (KisscamMusicConfigCodec.TryParse(text, out var config))
                parsed = config;
            else
                broken = true;
        }
        catch (Exception ex)
        {
            readFailed = true;
            AppLog.Warn("Die Kisscam-Musikconfig konnte nicht gelesen werden.", ex);
        }

        var sorted = entries
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .Take(SettingsLimits.KisscamMusicMaxSongs)
            .ToList();
        var logBroken = false;
        lock (_gate)
        {
            if (_disposed || epoch != _epoch)
                return;

            var dirty = _dirtyVersion != _savedVersion;
            if (readFailed)
            {
                // Letzter Cache bleibt. Wurde nie eine Config gelesen, ist der leere Stand unzuverlässig: nicht darüber schreiben.
                if (!_configLoaded)
                    _configUnreliable = true;
            }
            else
            {
                _configUnreliable = false;
                _configLoaded = true;
                if (broken)
                {
                    logBroken = !_configBroken;
                    _configBroken = true;
                    if (!dirty)
                        _config = new KisscamMusicConfig();
                }
                else
                {
                    // Ist die Datei in Ordnung, ersetzt sie den Speicherstand nur, solange nichts Ungespeichertes aussteht.
                    _configBroken = false;
                    if (!dirty)
                        _config = parsed ?? new KisscamMusicConfig();
                }
            }

            _listing = sorted;

            // Bereinigen: nur nach vollständig erfolgreicher Auflistung und gelesener, intakter Config.
            if (!readFailed && !_configBroken && !_configUnreliable)
            {
                var present = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
                var removed = _config.Songs.RemoveAll(s => !present.Contains(s.FileName));
                if (removed > 0)
                    _dirtyVersion++;
            }

            // Marken „nicht abspielbar“ gelten nur für unveränderte Dateien.
            foreach (var key in _unplayable.Keys.ToList())
            {
                var entry = sorted.FirstOrDefault(e => string.Equals(e.FullPath, key, StringComparison.OrdinalIgnoreCase));
                if (entry is null || entry.LastWriteUtc != _unplayable[key].LastWrite || entry.Length != _unplayable[key].Length)
                    _unplayable.Remove(key);
            }

            PublishLocked(_configBroken ? KisscamLibraryStatus.ConfigBroken : KisscamLibraryStatus.Ready);
            if (_dirtyVersion != _savedVersion && !_configUnreliable)
                ScheduleSaveLocked();
        }

        if (logBroken)
            AppLog.Warn("Die Kisscam-Musikconfig ist unlesbar. Die Kisscam läuft ohne Musik, bis sie im Admin neu gespeichert wird.");
        RaiseChanged();

        await StartWatcherAsync(epoch, folder).ConfigureAwait(false);
        await LoadDurationsAsync(epoch, sorted).ConfigureAwait(false);
    }

    private void Unavailable(int epoch, string what, Exception ex)
    {
        AppLog.Warn(ex is HungException
            ? $"Der Kisscam-Musikordner wurde nicht {what}: ein früherer Zugriff hängt noch."
            : ex is TimeoutException
                ? $"Der Kisscam-Musikordner wurde nicht {what}: Zeitüberschreitung."
                : $"Der Kisscam-Musikordner wurde nicht {what}.", ex is HungException or TimeoutException ? null : ex);
        lock (_gate)
        {
            if (_disposed || epoch != _epoch)
                return;
            // Der letzte bekannte Stand der Songs bleibt erhalten (Cache), der Status zeigt die Störung.
            PublishLocked(KisscamLibraryStatus.FolderUnavailable);
        }
        RaiseChanged();
    }

    private async Task LoadDurationsAsync(int epoch, IReadOnlyList<KisscamFileEntry> entries)
    {
        var sinceLastPublish = 0;
        foreach (var entry in entries)
        {
            lock (_gate)
            {
                if (_disposed || epoch != _epoch)
                    return;
                if (_durations.TryGetValue(entry.FullPath, out var known) && known.LastWrite == entry.LastWriteUtc && known.Length == entry.Length)
                    continue;
                if (_hungDurationTask is { IsCompleted: false })
                    break;
            }

            TimeSpan? duration = null;
            var task = Task.Run(() => _fs.ReadDuration(entry.FullPath));
            _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            try
            {
                duration = await task.WaitAsync(DurationTimeout, _time).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                lock (_gate)
                    _hungDurationTask = task;
                AppLog.Warn($"Songlänge nicht rechtzeitig gelesen: {entry.Name}");
                break;
            }
            catch (Exception)
            {
                // Länge unbekannt (Format ohne Metadaten, Datei gesperrt): kein Fehler, nur keine Anzeige.
            }

            var publish = false;
            lock (_gate)
            {
                if (_disposed || epoch != _epoch)
                    return;
                _durations[entry.FullPath] = (entry.LastWriteUtc, entry.Length, duration);
                if (++sinceLastPublish >= DurationPublishBatch)
                {
                    sinceLastPublish = 0;
                    PublishLocked();
                    publish = true;
                }
            }
            if (publish)
                RaiseChanged();
        }

        if (sinceLastPublish == 0)
            return;
        lock (_gate)
        {
            if (_disposed || epoch != _epoch)
                return;
            PublishLocked();
        }
        RaiseChanged();
    }

    // ------------------------------------------------------------------ Ordnerbeobachtung

    private async Task StartWatcherAsync(int epoch, string folder)
    {
        lock (_gate)
        {
            if (_disposed || epoch != _epoch || _watcher is not null || _watcherFailed || _watcherStarting)
                return;
            if (_hungTask is { IsCompleted: false })
                return;
            _watcherStarting = true;
        }

        var task = Task.Run(() => _fs.Watch(folder, () => OnFolderChanged(epoch), ex => OnWatcherFailed(epoch, ex)));
        IDisposable? watcher = null;
        try
        {
            watcher = await task.WaitAsync(EnsureFolderTimeout, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            AppLog.Warn("Die Beobachtung des Kisscam-Musikordners wurde nicht rechtzeitig eingerichtet.");
            // Ein spätes Ergebnis wird sofort wieder beendet.
            _ = task.ContinueWith(static t => SafeDispose(t.Result), TaskContinuationOptions.OnlyOnRanToCompletion);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Die Beobachtung des Kisscam-Musikordners konnte nicht eingerichtet werden.", ex);
        }

        var keep = false;
        lock (_gate)
        {
            _watcherStarting = false;
            if (!_disposed && epoch == _epoch)
            {
                if (watcher is null)
                {
                    _watcherFailed = true;
                }
                else
                {
                    _watcher = watcher;
                    keep = true;
                }
            }
        }
        if (!keep && watcher is not null)
            _ = Task.Run(() => SafeDispose(watcher));
    }

    private void OnFolderChanged(int epoch)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || epoch != _epoch)
                    return;
                _watchTimer?.Dispose();
                _watchTimer = _time.CreateTimer(_ => OnWatchTimer(epoch), null, WatcherDebounce, Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Eine Änderung im Kisscam-Musikordner konnte nicht verarbeitet werden.", ex);
        }
    }

    private void OnWatchTimer(int epoch)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || epoch != _epoch || _loadQueued)
                    return;
                _loadQueued = true;
                EnqueueLocked(() => LoadAsync(epoch));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Der Kisscam-Musikordner konnte nicht neu gelesen werden.", ex);
        }
    }

    private void OnWatcherFailed(int epoch, Exception ex)
    {
        IDisposable? watcher = null;
        lock (_gate)
        {
            if (_disposed || epoch != _epoch)
                return;
            // Bei einem Fehler (z. B. Netzlaufwerk getrennt) bleibt nur noch der Knopf „Aktualisieren“.
            _watcherFailed = true;
            watcher = _watcher;
            _watcher = null;
        }
        AppLog.Warn("Die Beobachtung des Kisscam-Musikordners ist ausgefallen; der Ordner wird nur noch über „Aktualisieren“ neu gelesen.", ex);
        if (watcher is not null)
            _ = Task.Run(() => SafeDispose(watcher));
    }

    // ------------------------------------------------------------------ Speichern

    private sealed record SaveJob(int Epoch, string ConfigPath, KisscamMusicConfig Config, int Version, bool Broken);

    private SaveJob CaptureJobLocked() =>
        new(_epoch, Path.Combine(_folder!, KisscamMusicFiles.ConfigFileName), _config.Clone(), _dirtyVersion, _configBroken);

    private void ScheduleSaveLocked()
    {
        if (_disposed)
            return;
        _saveTimer?.Dispose();
        var epoch = _epoch;
        _saveTimer = _time.CreateTimer(_ => OnSaveTimer(epoch), null, SaveDebounce, Timeout.InfiniteTimeSpan);
    }

    private void OnSaveTimer(int epoch)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || epoch != _epoch || _folder is null || _dirtyVersion == _savedVersion)
                    return;
                if (_configUnreliable)
                {
                    // Die Config wurde nie gelesen: nicht darüber schreiben, aber den Fehler melden.
                    _saveFailed = true;
                    PublishLocked();
                }
                else
                {
                    var job = CaptureJobLocked();
                    EnqueueLocked(() => ExecuteSaveAsync(job));
                    return;
                }
            }
            RaiseChanged();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Die Kisscam-Musikconfig konnte nicht zum Speichern vorgemerkt werden.", ex);
        }
    }

    private async Task ExecuteSaveAsync(SaveJob job)
    {
        var success = false;
        try
        {
            await RunFsAsync(() => { WriteJob(job); return 0; }, WriteConfigTimeout).ConfigureAwait(false);
            success = true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Die Kisscam-Musikconfig konnte nicht gespeichert werden.", ex is TimeoutException or HungException ? null : ex);
        }

        lock (_gate)
        {
            if (_disposed || job.Epoch != _epoch)
                return;
            if (success)
            {
                _savedVersion = Math.Max(_savedVersion, job.Version);
                if (job.Broken)
                    _configBroken = false;
            }
            _saveFailed = !success;
            PublishLocked();
            // Weitere Änderungen während des Schreibens: gleich noch einmal.
            if (success && _dirtyVersion != _savedVersion)
                ScheduleSaveLocked();
        }
        RaiseChanged();
    }

    // Schreibt die Config; eine unlesbare alte Datei wird vorher nach .defekt verschoben (scheitert das, bleibt sie unberührt).
    private void WriteJob(SaveJob job)
    {
        if (job.Broken)
            _fs.MoveBroken(job.ConfigPath, Path.Combine(Path.GetDirectoryName(job.ConfigPath)!, KisscamMusicFiles.BrokenConfigFileName));
        _fs.WriteConfigAtomic(job.ConfigPath, KisscamMusicConfigCodec.Write(job.Config));
    }

    // ------------------------------------------------------------------ Stand

    // Baut den Stand aus Auflistung, Config, Längen und Markierungen (unter _gate).
    private void PublishLocked(KisscamLibraryStatus? explicitStatus = null)
    {
        // Ohne Vorgabe bleibt „Loading“/„FolderUnavailable“ stehen, sonst folgt der Status dem Zustand der Config.
        var status = explicitStatus
            ?? (_snapshot.Status is KisscamLibraryStatus.Loading or KisscamLibraryStatus.FolderUnavailable
                ? _snapshot.Status
                : _configBroken ? KisscamLibraryStatus.ConfigBroken : KisscamLibraryStatus.Ready);
        var byName = new Dictionary<string, KisscamSongConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _config.Songs)
            byName.TryAdd(s.FileName, s);

        var songs = new List<KisscamSong>(_listing.Count);
        foreach (var entry in _listing)
        {
            byName.TryGetValue(entry.Name, out var config);
            TimeSpan? duration = null;
            if (_durations.TryGetValue(entry.FullPath, out var known) && known.LastWrite == entry.LastWriteUtc && known.Length == entry.Length)
                duration = known.Duration;
            var playable = !(_unplayable.TryGetValue(entry.FullPath, out var mark)
                && mark.LastWrite == entry.LastWriteUtc && mark.Length == entry.Length);
            // Eine unlesbare Config liefert keine Häkchen (der Speicherstand ist dann leer): Die Kisscam läuft stumm.
            songs.Add(new KisscamSong(entry.Name, entry.FullPath, config?.Enabled ?? false,
                config?.StartSeconds ?? SettingsLimits.KisscamSongStartSecondsMin,
                config?.VolumePercent ?? SettingsLimits.KisscamSongVolumeDefault, duration, playable));
        }

        var next = new KisscamLibrarySnapshot(_folder ?? string.Empty, status, songs, _saveFailed);
        Volatile.Write(ref _snapshot, next);
    }

    private void RaiseChanged()
    {
        EventHandler? handler;
        lock (_gate)
        {
            if (_disposed)
                return;
            handler = Changed;
        }
        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Ein Changed-Handler der Kisscam-Musikbibliothek ist fehlgeschlagen.", ex);
        }
    }

    private void OnPlaybackFailed(object? sender, MusicPlaybackFailedEventArgs e) => MarkUnplayable(e.Path);

    private static bool Same(KisscamSongConfig a, KisscamSongConfig b) =>
        a.Enabled == b.Enabled && a.StartSeconds == b.StartSeconds && a.VolumePercent == b.VolumePercent;

    private static void SafeDispose(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Eine Ressource der Kisscam-Musikbibliothek konnte nicht freigegeben werden.", ex);
        }
    }
}
