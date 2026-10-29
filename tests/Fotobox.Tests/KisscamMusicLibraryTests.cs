using System.Diagnostics;
using System.IO;
using Fotobox.Core;
using Fotobox.Core.Music;
using Fotobox.Services.Audio;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Dateisystem-Attrappe der Musikbibliothek: Inhalt im Speicher, Zähler, steuerbare Fehler und Hänger.</summary>
internal sealed class FakeMusicFileSystem : IKisscamMusicFileSystem
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<KisscamFileEntry>> _listings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _texts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Path, string Content)> _writes = [];
    private readonly List<(string From, string To)> _moves = [];
    private int _ensureCalls, _listCalls, _readCalls, _durationCalls, _watchersStarted, _watchersDisposed;

    public Action<string>? BeforeEnsure { get; set; }
    public Action<string>? BeforeList { get; set; }
    public Action<string>? BeforeRead { get; set; }
    public Action<string>? BeforeWrite { get; set; }
    public Action<string>? BeforeMove { get; set; }
    public Action<string>? BeforeWatch { get; set; }
    public Func<string, TimeSpan?> DurationOf { get; set; } = _ => null;
    public Action? OnDuration { get; set; }

    public Action? WatchChanged { get; private set; }
    public Action<Exception>? WatchFailed { get; private set; }

    public int EnsureCalls => Volatile.Read(ref _ensureCalls);
    public int ListCalls => Volatile.Read(ref _listCalls);
    public int ReadCalls => Volatile.Read(ref _readCalls);
    public int DurationCalls => Volatile.Read(ref _durationCalls);
    public int WatchersStarted => Volatile.Read(ref _watchersStarted);
    public int WatchersDisposed => Volatile.Read(ref _watchersDisposed);

    public List<(string Path, string Content)> Writes { get { lock (_gate) return [.. _writes]; } }
    public List<(string From, string To)> Moves { get { lock (_gate) return [.. _moves]; } }

    public static string ConfigPath(string folder) => Path.Combine(folder, KisscamMusicFiles.ConfigFileName);

    public void SetSongs(string folder, params string[] names)
    {
        lock (_gate)
            _listings[folder] = names.Select(n => new KisscamFileEntry(n, Path.Combine(folder, n), 1000, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))).ToList();
    }

    public void AddSong(string folder, string name, long length = 1000, DateTime? lastWrite = null)
    {
        lock (_gate)
        {
            if (!_listings.TryGetValue(folder, out var list))
                _listings[folder] = list = [];
            list.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            list.Add(new KisscamFileEntry(name, Path.Combine(folder, name), length, lastWrite ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        }
    }

    public void SetConfigText(string folder, string? text)
    {
        lock (_gate)
        {
            if (text is null)
                _texts.Remove(ConfigPath(folder));
            else
                _texts[ConfigPath(folder)] = text;
        }
    }

    public void SetText(string path, string text)
    {
        lock (_gate)
            _texts[path] = text;
    }

    public string? ConfigText(string folder)
    {
        lock (_gate)
            return _texts.TryGetValue(ConfigPath(folder), out var text) ? text : null;
    }

    public string? TextOf(string path)
    {
        lock (_gate)
            return _texts.TryGetValue(path, out var text) ? text : null;
    }

    public void EnsureDirectory(string folder)
    {
        Interlocked.Increment(ref _ensureCalls);
        BeforeEnsure?.Invoke(folder);
        lock (_gate)
        {
            if (!_listings.ContainsKey(folder))
                _listings[folder] = [];
        }
    }

    public IReadOnlyList<KisscamFileEntry> ListSongs(string folder)
    {
        Interlocked.Increment(ref _listCalls);
        BeforeList?.Invoke(folder);
        lock (_gate)
            return _listings.TryGetValue(folder, out var list) ? [.. list.Where(e => KisscamMusicFiles.IsSong(e.Name))] : [];
    }

    public string? ReadConfig(string path)
    {
        Interlocked.Increment(ref _readCalls);
        BeforeRead?.Invoke(path);
        return TextOf(path);
    }

    public void WriteConfigAtomic(string path, string content)
    {
        BeforeWrite?.Invoke(path);
        lock (_gate)
        {
            _texts[path] = content;
            _writes.Add((path, content));
        }
    }

    public void MoveBroken(string path, string brokenPath)
    {
        BeforeMove?.Invoke(path);
        lock (_gate)
        {
            // Wie File.Move: Ohne Quelldatei wirft es, eine vorhandene Zieldatei wird ersetzt.
            if (!_texts.Remove(path, out var text))
                throw new FileNotFoundException("Quelle fehlt.", path);
            _texts[brokenPath] = text;
            _moves.Add((path, brokenPath));
        }
    }

    public TimeSpan? ReadDuration(string path)
    {
        Interlocked.Increment(ref _durationCalls);
        OnDuration?.Invoke();
        return DurationOf(path);
    }

    public IDisposable Watch(string folder, Action changed, Action<Exception> failed)
    {
        BeforeWatch?.Invoke(folder);
        WatchChanged = changed;
        WatchFailed = failed;
        Interlocked.Increment(ref _watchersStarted);
        return new Watcher(this);
    }

    private sealed class Watcher(FakeMusicFileSystem owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Increment(ref owner._watchersDisposed);
        }
    }
}

/// <summary>Hält einen Dateizugriff an, bis der Test ihn freigibt.</summary>
internal sealed class FsGate
{
    private readonly ManualResetEventSlim _entered = new(false);
    private readonly ManualResetEventSlim _release = new(false);

    public bool IsEntered => _entered.IsSet;

    /// <summary>Im Dateisystem-Zugriff aufrufen: meldet das Eintreten und wartet auf die Freigabe.</summary>
    public void Pass(string _)
    {
        _entered.Set();
        _release.Wait(TimeSpan.FromSeconds(30));
    }

    public void Release() => _release.Set();
}

/// <summary>Player-Attrappe, die nur PlaybackFailed auslösen kann.</summary>
internal sealed class FailingMusicPlayer : IMusicPlayer
{
    public bool IsPlaying => false;
    public int Subscribers { get; private set; }

    private EventHandler<MusicPlaybackFailedEventArgs>? _failed;

    public event EventHandler? PlaybackChanged { add { } remove { } }
    public event EventHandler<MusicPositionEventArgs>? PositionChanged { add { } remove { } }

    public event EventHandler<MusicPlaybackFailedEventArgs>? PlaybackFailed
    {
        add
        {
            _failed += value;
            Subscribers++;
        }
        remove
        {
            _failed -= value;
            Subscribers--;
        }
    }

    public void Play(string? path, int volumePercent, bool loop, TimeSpan startAt = default)
    {
    }

    public void FadeOut(TimeSpan duration)
    {
    }

    public void Stop()
    {
    }

    public void Fail(string path, bool timedOut = false) => _failed?.Invoke(this, new MusicPlaybackFailedEventArgs(path, timedOut));
}

// Bewusst ohne [Collection(WpfCollection.Name)]: Die Bibliothek nutzt weder WPF noch einen Dispatcher.
public sealed class KisscamMusicLibraryTests : IDisposable
{
    private const string Folder = @"X:\Musik\Kisscam";
    private const string OtherFolder = @"X:\Musik\Andere";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeMusicFileSystem _fs = new();
    private readonly List<KisscamMusicLibrary> _libraries = [];
    private readonly List<FsGate> _gates = [];

    public void Dispose()
    {
        foreach (var gate in _gates)
            gate.Release();
        foreach (var library in _libraries)
            library.Dispose();
    }

    private KisscamMusicLibrary Create(IMusicPlayer? player = null)
    {
        var library = new KisscamMusicLibrary(_fs, _time, player);
        _libraries.Add(library);
        return library;
    }

    private FsGate NewGate()
    {
        var gate = new FsGate();
        _gates.Add(gate);
        return gate;
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int seconds = 10)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (condition())
                return;
            await Task.Delay(5);
        }
        Assert.True(condition(), "Zeitüberschreitung beim Warten auf: " + what);
    }

    private static Task WaitReady(KisscamMusicLibrary library) =>
        WaitUntil(() => library.Current.Status == KisscamLibraryStatus.Ready, "Status Ready");

    // Lässt die Zeit in Schritten laufen, bis die Bedingung erfüllt ist (die Fristen der Dateizugriffe laufen auf der Testuhr).
    private async Task AdvanceUntil(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10) && !condition())
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }
        Assert.True(condition(), "Zeitüberschreitung beim Warten auf: " + what);
    }

    private async Task<KisscamMusicLibrary> StartedAsync(params string[] songs)
    {
        _fs.SetSongs(Folder, songs);
        var library = Create();
        library.SetFolder(Folder);
        await WaitReady(library);
        return library;
    }

    private static string ConfigJson(params (string File, bool Enabled, int Start, int Volume)[] songs) =>
        KisscamMusicConfigCodec.Write(new KisscamMusicConfig
        {
            Songs = songs.Select(s => new KisscamSongConfig { FileName = s.File, Enabled = s.Enabled, StartSeconds = s.Start, VolumePercent = s.Volume }).ToList(),
        });

    // ------------------------------------------------------------------ Laden

    [Fact]
    public void BeforeAnyFolder_TheSnapshotIsLoadingAndEmpty()
    {
        var library = Create();

        Assert.Equal(KisscamLibraryStatus.Loading, library.Current.Status);
        Assert.Empty(library.Current.Songs);
        Assert.Equal(0, _fs.EnsureCalls);
    }

    [Fact]
    public async Task SetFolder_CreatesTheFolder_ListsTheSongs_SortedAndUnchecked()
    {
        var library = await StartedAsync("zebra.mp3", "Alpha.wav", "mitte.m4a");

        Assert.Equal(1, _fs.EnsureCalls);
        Assert.Equal(Folder, library.Current.Folder);
        Assert.Equal(["Alpha.wav", "mitte.m4a", "zebra.mp3"], library.Current.Songs.Select(s => s.FileName));
        Assert.All(library.Current.Songs, s =>
        {
            Assert.False(s.Enabled); // neue Songs sind nicht angehakt
            Assert.Equal(0, s.StartSeconds);
            Assert.Equal(SettingsLimits.KisscamSongVolumeDefault, s.VolumePercent);
            Assert.True(s.Playable);
        });
        Assert.Equal(Path.Combine(Folder, "Alpha.wav"), library.Current.Songs[0].FullPath);
        Assert.Empty(_fs.Writes); // Auflisten allein schreibt nichts
    }

    [Fact]
    public async Task SetFolder_WithoutAFolder_UsesTheStandardFolder()
    {
        var library = Create();

        library.SetFolder(null);
        await WaitReady(library);

        Assert.Equal(AppInfo.DefaultKisscamMusicFolder, library.Current.Folder);
        Assert.Equal(AppInfo.DefaultKisscamMusicFolder, Path.GetFullPath(library.Current.Folder));
        library.SetFolder("   ");
        Assert.Equal(1, _fs.EnsureCalls); // derselbe Ordner: wirkungslos
    }

    [Fact]
    public async Task EmptyFolder_IsReady_WithoutSongs()
    {
        var library = await StartedAsync();

        Assert.Equal(KisscamLibraryStatus.Ready, library.Current.Status);
        Assert.Empty(library.Current.Songs);
        Assert.False(library.Current.HasEnabledSong);
    }

    [Fact]
    public async Task OtherExtensions_AreNotListed()
    {
        // Das Dateisystem filtert die vier Endungen; die Attrappe verhält sich ebenso (echter Test: KisscamMusicFileSystemTests).
        var library = await StartedAsync("a.mp3", "b.ogg", "c.flac", "kisscam-musik.json", "d.WMA");

        Assert.Equal(["a.mp3", "d.WMA"], library.Current.Songs.Select(s => s.FileName));
    }

    [Fact]
    public async Task AtMost500Songs_AreListed()
    {
        var library = await StartedAsync(Enumerable.Range(0, 520).Select(i => $"s{i:000}.mp3").ToArray());

        Assert.Equal(SettingsLimits.KisscamMusicMaxSongs, library.Current.Songs.Count);
        Assert.Equal("s000.mp3", library.Current.Songs[0].FileName);
    }

    [Fact]
    public async Task ExistingConfig_AppliesCheckStartTimeAndVolume()
    {
        _fs.SetConfigText(Folder, ConfigJson(("a.mp3", true, 42, 60), ("b.mp3", false, 0, 100)));

        var library = await StartedAsync("a.mp3", "b.mp3", "neu.mp3");

        var a = library.Current.Songs.Single(s => s.FileName == "a.mp3");
        Assert.True(a.Enabled);
        Assert.Equal(42, a.StartSeconds);
        Assert.Equal(60, a.VolumePercent);
        Assert.False(library.Current.Songs.Single(s => s.FileName == "neu.mp3").Enabled);
        Assert.True(library.Current.HasEnabledSong);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(_fs.Writes);
    }

    [Fact]
    public async Task ConfigMatching_IgnoresCase()
    {
        _fs.SetConfigText(Folder, ConfigJson(("LIED.MP3", true, 9, 70)));

        var library = await StartedAsync("Lied.mp3");

        var song = Assert.Single(library.Current.Songs);
        Assert.True(song.Enabled);
        Assert.Equal(9, song.StartSeconds);
    }

    // ------------------------------------------------------------------ Bereinigen

    [Fact]
    public async Task EntriesOfMissingFiles_AreRemoved_AfterASuccessfulListing()
    {
        _fs.SetConfigText(Folder, ConfigJson(("da.mp3", true, 5, 100), ("weg.mp3", true, 7, 100)));

        await StartedAsync("da.mp3");
        _time.Advance(KisscamMusicLibrary.SaveDebounce);
        await WaitUntil(() => _fs.Writes.Count == 1, "bereinigte Config geschrieben");

        Assert.True(KisscamMusicConfigCodec.TryParse(_fs.ConfigText(Folder), out var config));
        Assert.Equal(["da.mp3"], config.Songs.Select(s => s.FileName));
        Assert.True(config.Songs[0].Enabled);
    }

    [Fact]
    public async Task NothingIsCleaned_WhenTheListingFails()
    {
        var original = ConfigJson(("da.mp3", true, 5, 100), ("weg.mp3", true, 7, 100));
        _fs.SetConfigText(Folder, original);
        _fs.SetSongs(Folder, "da.mp3", "weg.mp3");
        var library = Create();
        library.SetFolder(Folder);
        await WaitReady(library);

        // Ein Aussetzer: Der Ordner ist kurz nicht lesbar (alle Dateien „fehlen“).
        _fs.SetSongs(Folder);
        _fs.BeforeList = _ => throw new IOException("Netzlaufwerk weg");
        library.Refresh();
        await WaitUntil(() => library.Current.Status == KisscamLibraryStatus.FolderUnavailable, "Ordner nicht erreichbar");
        _time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        Assert.Empty(_fs.Writes);
        Assert.Equal(original, _fs.ConfigText(Folder));
        // Der letzte Stand bleibt als Cache erhalten: Die Häkchen sind nicht verloren.
        Assert.Equal(["da.mp3", "weg.mp3"], library.Current.Songs.Select(s => s.FileName));
        Assert.All(library.Current.Songs, s => Assert.True(s.Enabled));
    }

    [Fact]
    public async Task NothingIsCleaned_WhenTheListingHangs()
    {
        var original = ConfigJson(("da.mp3", true, 5, 100));
        _fs.SetConfigText(Folder, original);
        _fs.SetSongs(Folder);
        var gate = NewGate();
        _fs.BeforeList = gate.Pass;
        var library = Create();

        library.SetFolder(Folder);
        await WaitUntil(() => gate.IsEntered, "Auflisten hängt");
        await AdvanceUntil(() => library.Current.Status == KisscamLibraryStatus.FolderUnavailable, "Frist des Auflistens");
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Empty(_fs.Writes);
        Assert.Equal(original, _fs.ConfigText(Folder));
    }

    [Fact]
    public async Task NothingIsCleaned_WhenTheConfigCannotBeRead_AndTheCacheStays()
    {
        _fs.SetConfigText(Folder, ConfigJson(("da.mp3", true, 5, 100), ("weg.mp3", true, 7, 100)));
        var library = await StartedAsync("da.mp3", "weg.mp3");
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(_fs.Writes);

        _fs.SetSongs(Folder, "da.mp3"); // weg.mp3 wäre jetzt zu bereinigen …
        _fs.BeforeRead = _ => throw new IOException("gesperrt"); // … aber die Config ist nicht lesbar
        library.Refresh();
        await WaitUntil(() => _fs.ReadCalls >= 2, "zweites Lesen");
        await WaitUntil(() => library.Current.Songs.Count == 1, "neue Liste");
        _time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        Assert.Empty(_fs.Writes);
        Assert.True(library.Current.Songs[0].Enabled); // der letzte Cache gilt weiter
        Assert.Equal(5, library.Current.Songs[0].StartSeconds);
    }

    [Fact]
    public async Task NeverReadConfig_IsNotOverwritten_AndSaveFailedIsReported()
    {
        var original = ConfigJson(("a.mp3", true, 5, 100));
        _fs.SetConfigText(Folder, original);
        _fs.SetSongs(Folder, "a.mp3");
        _fs.BeforeRead = _ => throw new IOException("gesperrt");
        var library = Create();
        library.SetFolder(Folder);
        await WaitReady(library);
        Assert.False(library.Current.Songs[0].Enabled); // nichts gelesen

        library.UpdateSong("a.mp3", enabled: true);
        _time.Advance(KisscamMusicLibrary.SaveDebounce);
        await WaitUntil(() => library.Current.SaveFailed, "Speichern gemeldet");
        await Task.Delay(100);

        Assert.Empty(_fs.Writes);
        Assert.Equal(original, _fs.ConfigText(Folder));
    }

    // ------------------------------------------------------------------ Defekte Config

    [Fact]
    public async Task BrokenConfig_IsReported_ShowsUncheckedSongs_AndIsNotTouched()
    {
        const string broken = "{ das ist kein json";
        _fs.SetConfigText(Folder, broken);
        _fs.SetSongs(Folder, "a.mp3");
        var library = Create();
        library.SetFolder(Folder);
        await WaitUntil(() => library.Current.Status == KisscamLibraryStatus.ConfigBroken, "Status ConfigBroken");
        _time.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(100);

        var song = Assert.Single(library.Current.Songs);
        Assert.False(song.Enabled);
        Assert.Null(KisscamSongPicker.Pick(library.Current, 80, new Random(1))); // die Gastansicht bleibt still
        Assert.Empty(_fs.Writes);
        Assert.Empty(_fs.Moves);
        Assert.Equal(broken, _fs.ConfigText(Folder));
    }

    [Fact]
    public async Task BrokenConfig_NextAdminChange_MovesTheOldFileAside_AndWritesAgain()
    {
        const string broken = "{ kaputt";
        _fs.SetConfigText(Folder, broken);
        _fs.SetSongs(Folder, "a.mp3");
        var library = Create();
        library.SetFolder(Folder);
        await WaitUntil(() => library.Current.Status == KisscamLibraryStatus.ConfigBroken, "Status ConfigBroken");

        library.UpdateSong("a.mp3", enabled: true, startSeconds: 12);
        Assert.Equal(KisscamLibraryStatus.ConfigBroken, library.Current.Status); // noch nichts geschrieben
        _time.Advance(KisscamMusicLibrary.SaveDebounce);
        await WaitUntil(() => _fs.Writes.Count == 1, "neu geschrieben");
        await WaitUntil(() => library.Current.Status == KisscamLibraryStatus.Ready, "Status Ready");

        var move = Assert.Single(_fs.Moves);
        Assert.Equal(FakeMusicFileSystem.ConfigPath(Folder), move.From);
        Assert.Equal(Path.Combine(Folder, "kisscam-musik.json.defekt"), move.To);
        Assert.Equal(broken, _fs.TextOf(move.To)); // die alte Datei bleibt erhalten
        Assert.True(KisscamMusicConfigCodec.TryParse(_fs.ConfigText(Folder), out var config));
        Assert.True(config.Songs.Single().Enabled);
        Assert.Equal(12, config.Songs.Single().StartSeconds);
        Assert.True(library.Current.Songs[0].Enabled);
    }

    [Fact]
    public async Task BrokenConfig_WhenMovingFails_TheOriginalStays_AndSaveFailedIsReported()
    {
        const string broken = "{ kaputt";
        _fs.SetConfigText(Folder, broken);
        _fs.SetSongs(Folder, "a.mp3");
        _fs.BeforeMove = _ => throw new IOException("gesperrt");
        var library = Create();
        library.SetFolder(Folder);
        await WaitUntil(() => library.Current.Status == KisscamLibraryStatus.ConfigBroken, "Status ConfigBroken");

        library.UpdateSong("a.mp3", enabled: true);
        _time.Advance(KisscamMusicLibrary.SaveDebounce);
        await WaitUntil(() => library.Current.SaveFailed, "Speichern gemeldet");

        Assert.Empty(_fs.Writes);
        Assert.Equal(broken, _fs.ConfigText(Folder));
        Assert.Equal(KisscamLibraryStatus.ConfigBroken, library.Current.Status);
    }

    [Fact]
    public async Task ConfigFixedByHand_IsPickedUpOnRefresh()
    {
        _fs.SetConfigText(Folder, "kaputt");
        _fs.SetSongs(Folder, "a.mp3");
        var library = Create();
        library.SetFolder(Folder);
        await WaitUntil(() => library.Current.Status == KisscamLibraryStatus.ConfigBroken, "Status ConfigBroken");

        _fs.SetConfigText(Folder, ConfigJson(("a.mp3", true, 3, 100)));
        library.Refresh();

        await WaitUntil(() => library.Current.Status == KisscamLibraryStatus.Ready, "Status Ready");
        Assert.True(library.Current.Songs[0].Enabled);
    }

    // ------------------------------------------------------------------ Änderungen und Speichern

    [Fact]
    public async Task UpdateSong_ChangesTheSnapshotAtOnce_AndRaisesChanged_ButSavesOnlyAfterTheDebounce()
    {
        var library = await StartedAsync("a.mp3", "b.mp3");
        var changed = 0;
        library.Changed += (_, _) => Interlocked.Increment(ref changed);

        library.UpdateSong("a.mp3", enabled: true);
        library.UpdateSong("a.mp3", startSeconds: 30);
        library.UpdateSong("a.mp3", volumePercent: 55);

        var song = library.Current.Songs[0];
        Assert.True(song.Enabled);
        Assert.Equal(30, song.StartSeconds);
        Assert.Equal(55, song.VolumePercent);
        Assert.Equal(3, Volatile.Read(ref changed));
        Assert.False(library.Current.Songs[1].Enabled);
        Assert.Empty(_fs.Writes);

        _time.Advance(KisscamMusicLibrary.SaveDebounce - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        Assert.Empty(_fs.Writes);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitUntil(() => _fs.Writes.Count == 1, "gespeichert");
        await Task.Delay(50);
        Assert.Single(_fs.Writes); // alle drei Änderungen in einer Datei
        Assert.True(KisscamMusicConfigCodec.TryParse(_fs.ConfigText(Folder), out var config));
        var saved = Assert.Single(config.Songs);
        Assert.Equal(("a.mp3", true, 30, 55), (saved.FileName, saved.Enabled, saved.StartSeconds, saved.VolumePercent));
    }

    [Fact]
    public async Task Debounce_RestartsWithEveryChange()
    {
        var library = await StartedAsync("a.mp3");

        library.UpdateSong("a.mp3", enabled: true);
        _time.Advance(TimeSpan.FromMilliseconds(200));
        library.UpdateSong("a.mp3", startSeconds: 5);
        _time.Advance(TimeSpan.FromMilliseconds(200));
        await Task.Delay(50);
        Assert.Empty(_fs.Writes);

        _time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntil(() => _fs.Writes.Count == 1, "gespeichert");
    }

    [Fact]
    public async Task UpdateSong_ClampsIntoTheLimits_AndIgnoresUnknownFilesAndNoChange()
    {
        var library = await StartedAsync("a.mp3");
        var changed = 0;
        library.Changed += (_, _) => Interlocked.Increment(ref changed);

        library.UpdateSong("a.mp3", startSeconds: 99999, volumePercent: 3);
        var song = library.Current.Songs[0];
        Assert.Equal(SettingsLimits.KisscamSongStartSecondsMax, song.StartSeconds);
        Assert.Equal(SettingsLimits.KisscamSongVolumeMin, song.VolumePercent);

        var before = Volatile.Read(ref changed);
        library.UpdateSong("gibts-nicht.mp3", enabled: true);
        library.UpdateSong("", enabled: true);
        library.UpdateSong("a.mp3"); // nichts zu ändern
        library.UpdateSong("a.mp3", enabled: false); // schon so
        Assert.Equal(before, Volatile.Read(ref changed));
        _time.Advance(KisscamMusicLibrary.SaveDebounce);
        await WaitUntil(() => _fs.Writes.Count == 1, "gespeichert");
        await Task.Delay(50);
        Assert.Single(_fs.Writes);
    }

    [Fact]
    public async Task UpdateSong_BeforeTheFolderIsLoaded_IsIgnored()
    {
        var library = Create();

        library.UpdateSong("a.mp3", enabled: true);
        await Task.Delay(20);

        Assert.Empty(library.Current.Songs);
        Assert.Empty(_fs.Writes);
    }

    [Fact]
    public async Task SaveFailure_IsReported_AndTheNextChangeTriesAgain()
    {
        var library = await StartedAsync("a.mp3");
        var failing = true;
        _fs.BeforeWrite = _ =>
        {
            if (failing)
                throw new IOException("Platte voll");
        };

        library.UpdateSong("a.mp3", enabled: true);
        _time.Advance(KisscamMusicLibrary.SaveDebounce);
        await WaitUntil(() => library.Current.SaveFailed, "Speichern gemeldet");
        Assert.True(library.Current.Songs[0].Enabled); // der Cache gilt trotzdem

        failing = false;
        library.UpdateSong("a.mp3", startSeconds: 9);
        Assert.False(library.Current.SaveFailed);
        _time.Advance(KisscamMusicLibrary.SaveDebounce);
        await WaitUntil(() => _fs.Writes.Count == 1, "gespeichert");
        await WaitUntil(() => !library.Current.SaveFailed, "Meldung zurückgenommen");
        Assert.True(KisscamMusicConfigCodec.TryParse(_fs.ConfigText(Folder), out var config));
        Assert.True(config.Songs.Single().Enabled); // beide Änderungen sind drin
        Assert.Equal(9, config.Songs.Single().StartSeconds);
    }

    [Fact]
    public async Task HangingWrite_TimesOut_AndIsReportedAsSaveFailed()
    {
        var library = await StartedAsync("a.mp3");
        var gate = NewGate();
        _fs.BeforeWrite = gate.Pass;

        library.UpdateSong("a.mp3", enabled: true);
        _time.Advance(KisscamMusicLibrary.SaveDebounce);
        await WaitUntil(() => gate.IsEntered, "Schreiben hängt");
        await AdvanceUntil(() => library.Current.SaveFailed, "Frist des Schreibens");

        Assert.True(library.Current.Songs[0].Enabled);
    }

    [Fact]
    public async Task HangingWrite_IsSavedAgain_OnceTheHangEnds()
    {
        var library = await StartedAsync("a.mp3");
        var gate = NewGate();
        _fs.BeforeWrite = gate.Pass;

        library.UpdateSong("a.mp3", enabled: true);
        _time.Advance(KisscamMusicLibrary.SaveDebounce);
        await WaitUntil(() => gate.IsEntered, "Schreiben hängt");
        await AdvanceUntil(() => library.Current.SaveFailed, "Frist des Schreibens");
        Assert.True(library.Current.Songs[0].Enabled);

        // Ohne neue Änderung: Das Ende des Hängers allein stößt das Speichern wieder an und nimmt die Meldung zurück.
        gate.Release();
        await AdvanceUntil(() => !library.Current.SaveFailed && _fs.Writes.Count >= 2, "erneut gespeichert");
        Assert.True(KisscamMusicConfigCodec.TryParse(_fs.ConfigText(Folder), out var config));
        Assert.True(config.Songs.Single().Enabled);
    }

    [Fact]
    public async Task SaveBlockedByAnEarlierHang_IsRetried_OnceTheHangEnds()
    {
        var library = await StartedAsync("a.mp3");
        var gate = NewGate();
        _fs.BeforeList = gate.Pass; // ein späteres Auflisten hängt und hält den Dateizugriff besetzt

        library.Refresh();
        await WaitUntil(() => gate.IsEntered, "Auflisten hängt");
        await AdvanceUntil(() => library.Current.Status == KisscamLibraryStatus.FolderUnavailable, "Frist des Auflistens");

        library.UpdateSong("a.mp3", enabled: true);
        _time.Advance(KisscamMusicLibrary.SaveDebounce);
        await WaitUntil(() => library.Current.SaveFailed, "Speichern gemeldet (früherer Zugriff hängt)");
        Assert.Empty(_fs.Writes);

        gate.Release();
        await AdvanceUntil(() => _fs.Writes.Count == 1 && !library.Current.SaveFailed, "nach dem Hänger gespeichert");
        await WaitReady(library);
    }

    [Fact]
    public async Task BrokenConfig_WhenWritingFailsAfterTheMove_TheNextTrySucceeds_WithoutMovingAgain()
    {
        const string broken = "{ kaputt";
        _fs.SetConfigText(Folder, broken);
        _fs.SetSongs(Folder, "a.mp3");
        var failing = true;
        _fs.BeforeWrite = _ =>
        {
            if (failing)
                throw new IOException("Platte voll");
        };
        var library = Create();
        library.SetFolder(Folder);
        await WaitUntil(() => library.Current.Status == KisscamLibraryStatus.ConfigBroken, "Status ConfigBroken");

        library.UpdateSong("a.mp3", enabled: true);
        _time.Advance(KisscamMusicLibrary.SaveDebounce);
        await WaitUntil(() => library.Current.SaveFailed, "Schreiben gescheitert");
        Assert.Single(_fs.Moves); // die defekte Datei liegt jetzt in .defekt
        Assert.Null(_fs.ConfigText(Folder));

        failing = false;
        library.UpdateSong("a.mp3", startSeconds: 7);
        _time.Advance(KisscamMusicLibrary.SaveDebounce);
        await WaitUntil(() => _fs.Writes.Count == 1, "zweiter Versuch geschrieben");
        await WaitUntil(() => !library.Current.SaveFailed && library.Current.Status == KisscamLibraryStatus.Ready, "Meldung zurückgenommen");

        Assert.Single(_fs.Moves);
        Assert.Equal(broken, _fs.TextOf(Path.Combine(Folder, "kisscam-musik.json.defekt"))); // die alte Datei bleibt erhalten
        Assert.True(KisscamMusicConfigCodec.TryParse(_fs.ConfigText(Folder), out var config));
        Assert.Equal(7, config.Songs.Single().StartSeconds);
    }

    [Fact]
    public async Task BrokenConfig_AnOlderBrokenCopy_IsReplacedByTheNewerOne()
    {
        const string broken = "{ neuer defekt";
        var brokenPath = Path.Combine(Folder, "kisscam-musik.json.defekt");
        _fs.SetConfigText(Folder, broken);
        _fs.SetText(brokenPath, "{ alter defekt");
        _fs.SetSongs(Folder, "a.mp3");
        var library = Create();
        library.SetFolder(Folder);
        await WaitUntil(() => library.Current.Status == KisscamLibraryStatus.ConfigBroken, "Status ConfigBroken");

        library.UpdateSong("a.mp3", enabled: true);
        _time.Advance(KisscamMusicLibrary.SaveDebounce);
        await WaitUntil(() => _fs.Writes.Count == 1, "geschrieben");

        Assert.Equal(broken, _fs.TextOf(brokenPath)); // es bleibt die zuletzt defekte Datei erhalten
    }

    // ------------------------------------------------------------------ Hänger und Fristen

    [Fact]
    public async Task HangingFolder_BecomesUnavailable_AfterTheDeadline_AndStartsNoSecondAccessWhileHung()
    {
        var gate = NewGate();
        _fs.BeforeEnsure = gate.Pass;
        var library = Create();

        library.SetFolder(Folder);
        await WaitUntil(() => gate.IsEntered, "Anlegen hängt");
        Assert.Equal(KisscamLibraryStatus.Loading, library.Current.Status); // Current blockiert nicht
        await AdvanceUntil(() => library.Current.Status == KisscamLibraryStatus.FolderUnavailable, "Frist des Anlegens");

        // Solange der alte Zugriff hängt, startet kein neuer.
        library.Refresh();
        library.Refresh();
        await Task.Delay(100);
        Assert.Equal(1, _fs.EnsureCalls);
        Assert.Equal(KisscamLibraryStatus.FolderUnavailable, library.Current.Status);

        // Endet der Hänger, liest die Bibliothek den Ordner von selbst neu (ohne „Aktualisieren“ und ohne neuen Admin).
        _fs.SetSongs(Folder, "a.mp3");
        gate.Release();
        await WaitReady(library);
        Assert.Equal(2, _fs.EnsureCalls);
        Assert.Equal(["a.mp3"], library.Current.Songs.Select(s => s.FileName));
    }

    [Fact]
    public async Task AfterAnAdminAbort_WhileTheDraftFolderHangs_TheSavedFolderComesBack_OnceTheHangEnds()
    {
        _fs.SetSongs(OtherFolder, "x.mp3");
        var library = await StartedAsync("a.mp3");
        var gate = NewGate();
        _fs.BeforeEnsure = folder =>
        {
            if (folder == OtherFolder)
                gate.Pass(folder);
        };

        library.SetFolder(OtherFolder); // der Entwurfsordner hängt (Netzlaufwerk) …
        await WaitUntil(() => gate.IsEntered, "Entwurfsordner hängt");
        await AdvanceUntil(() => library.Current.Status == KisscamLibraryStatus.FolderUnavailable, "Frist");
        library.SetFolder(Folder); // … der Admin wird abgebrochen: Der gespeicherte Ordner kann wegen des Hängers noch nicht gelesen werden
        await WaitUntil(() => library.Current.Folder == Folder && library.Current.Status == KisscamLibraryStatus.FolderUnavailable,
            "gespeicherter Ordner vorerst nicht lesbar");
        Assert.Empty(library.Current.Songs);

        gate.Release();

        await WaitReady(library);
        Assert.Equal(Folder, library.Current.Folder);
        Assert.Equal(["a.mp3"], library.Current.Songs.Select(s => s.FileName));
    }

    [Fact]
    public async Task HangingConfigRead_KeepsTheListing_AndDoesNotWrite()
    {
        _fs.SetSongs(Folder, "a.mp3");
        var gate = NewGate();
        _fs.BeforeRead = gate.Pass;
        var library = Create();

        library.SetFolder(Folder);
        await WaitUntil(() => gate.IsEntered, "Lesen hängt");
        await AdvanceUntil(() => library.Current.Status == KisscamLibraryStatus.Ready, "Frist des Lesens");

        Assert.Equal(["a.mp3"], library.Current.Songs.Select(s => s.FileName));
        Assert.False(library.Current.Songs[0].Enabled);
        library.UpdateSong("a.mp3", enabled: true);
        _time.Advance(KisscamMusicLibrary.SaveDebounce);
        await WaitUntil(() => library.Current.SaveFailed, "Speichern gemeldet"); // nie gelesen: nicht überschreiben
        Assert.Empty(_fs.Writes);
    }

    [Fact]
    public async Task Current_NeverBlocks_WhileTheFileSystemHangs()
    {
        var gate = NewGate();
        _fs.BeforeList = gate.Pass;
        var library = Create();
        library.SetFolder(Folder);
        await WaitUntil(() => gate.IsEntered, "Auflisten hängt");

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            _ = library.Current;
            library.UpdateSong("a.mp3", enabled: true);
            library.MarkUnplayable(Path.Combine(Folder, "a.mp3"));
        }

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Dauerte {sw.Elapsed}.");
    }

    [Fact]
    public async Task UnreadableFolder_ErrorLeadsToUnavailable_AndRefreshRecovers()
    {
        _fs.BeforeEnsure = _ => throw new UnauthorizedAccessException("kein Zugriff");
        var library = Create();

        library.SetFolder(Folder);
        await WaitUntil(() => library.Current.Status == KisscamLibraryStatus.FolderUnavailable, "Ordner nicht erreichbar");
        Assert.Empty(library.Current.Songs);

        _fs.BeforeEnsure = null;
        _fs.SetSongs(Folder, "a.mp3");
        library.Refresh();

        await WaitReady(library);
        Assert.Single(library.Current.Songs);
    }

    [Fact]
    public async Task SettingTheSameFolderAgain_AfterAnError_TriesAgain()
    {
        _fs.BeforeEnsure = _ => throw new IOException("weg");
        var library = Create();
        library.SetFolder(Folder);
        await WaitUntil(() => library.Current.Status == KisscamLibraryStatus.FolderUnavailable, "Ordner nicht erreichbar");

        _fs.BeforeEnsure = null;
        library.SetFolder(Folder);

        await WaitReady(library);
    }

    [Fact]
    public async Task InvalidFolderPath_IsUnavailable_WithoutFileAccess()
    {
        var library = Create();

        library.SetFolder("C:\\ungültig\0pfad");
        await Task.Delay(50);

        Assert.Equal(KisscamLibraryStatus.FolderUnavailable, library.Current.Status);
        Assert.Equal(0, _fs.EnsureCalls);
    }

    // ------------------------------------------------------------------ Ordnerwechsel

    [Fact]
    public async Task SettingADifferentFolder_ReloadsAndResets()
    {
        _fs.SetSongs(OtherFolder, "x.mp3");
        _fs.SetConfigText(OtherFolder, ConfigJson(("x.mp3", true, 1, 100)));
        var library = await StartedAsync("a.mp3");
        var gate = NewGate();
        _fs.BeforeList = gate.Pass; // hält das Laden an, damit der Zwischenstand sichtbar bleibt

        library.SetFolder(OtherFolder);
        Assert.Equal(KisscamLibraryStatus.Loading, library.Current.Status);
        Assert.Empty(library.Current.Songs); // nichts vom alten Ordner
        Assert.Equal(OtherFolder, library.Current.Folder);
        gate.Release();
        await WaitReady(library);

        Assert.Equal(OtherFolder, library.Current.Folder);
        Assert.Equal(["x.mp3"], library.Current.Songs.Select(s => s.FileName));
        Assert.True(library.Current.Songs[0].Enabled);
    }

    [Fact]
    public async Task AdminAbort_PointingBackAtTheSavedFolder_RestoresItsSongs_EvenWhenTheOtherLoadIsSlow()
    {
        _fs.SetSongs(OtherFolder, "x.mp3");
        var library = await StartedAsync("a.mp3");
        var gate = NewGate();
        _fs.BeforeList = folder =>
        {
            if (folder == OtherFolder)
                gate.Pass(folder);
        };

        library.SetFolder(OtherFolder); // der Admin lenkt auf den Entwurfsordner …
        await WaitUntil(() => gate.IsEntered, "Entwurfsordner wird gelesen");
        library.SetFolder(Folder); // … und beim Abbrechen zurück
        gate.Release();
        await WaitUntil(() => library.Current.Folder == Folder && library.Current.Status == KisscamLibraryStatus.Ready, "gespeicherter Ordner geladen");
        await Task.Delay(100); // der späte Abschluss des anderen Ordners darf nichts mehr ändern

        Assert.Equal(Folder, library.Current.Folder);
        Assert.Equal(["a.mp3"], library.Current.Songs.Select(s => s.FileName));
    }

    [Fact]
    public async Task APendingChange_IsSaved_BeforeTheFolderSwitches()
    {
        var library = await StartedAsync("a.mp3");
        library.UpdateSong("a.mp3", enabled: true);

        library.SetFolder(OtherFolder); // noch vor Ablauf der Entprellung
        await WaitUntil(() => _fs.Writes.Count == 1, "gespeichert");

        Assert.Equal(FakeMusicFileSystem.ConfigPath(Folder), _fs.Writes[0].Path);
        Assert.True(KisscamMusicConfigCodec.TryParse(_fs.Writes[0].Content, out var config));
        Assert.True(config.Songs.Single().Enabled);
        await WaitReady(library);
        Assert.Equal(OtherFolder, library.Current.Folder);
    }

    // ------------------------------------------------------------------ Beobachtung des Ordners

    [Fact]
    public async Task FolderChanges_AreDebounced_AndReloadOnce()
    {
        var library = await StartedAsync("a.mp3");
        await WaitUntil(() => _fs.WatchersStarted == 1, "Beobachtung läuft");
        var lists = _fs.ListCalls;

        _fs.AddSong(Folder, "neu.mp3");
        for (var i = 0; i < 5; i++)
        {
            _fs.WatchChanged!();
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }
        await Task.Delay(50);
        Assert.Equal(lists, _fs.ListCalls); // 400 ms seit der ersten, 100 ms Abstand: noch nichts

        _time.Advance(KisscamMusicLibrary.WatcherDebounce);
        await WaitUntil(() => library.Current.Songs.Count == 2, "neuer Song erscheint");
        await Task.Delay(50);

        Assert.Equal(lists + 1, _fs.ListCalls);
        Assert.False(library.Current.Songs.Single(s => s.FileName == "neu.mp3").Enabled);
    }

    [Fact]
    public async Task ASongThatDisappears_IsRemovedFromTheList_AndFromTheConfig()
    {
        _fs.SetConfigText(Folder, ConfigJson(("a.mp3", true, 4, 100), ("b.mp3", true, 8, 100)));
        var library = await StartedAsync("a.mp3", "b.mp3");

        await WaitUntil(() => _fs.WatchersStarted == 1, "Beobachtung läuft");
        _fs.SetSongs(Folder, "a.mp3");
        _fs.WatchChanged!();
        _time.Advance(KisscamMusicLibrary.WatcherDebounce);
        await WaitUntil(() => library.Current.Songs.Count == 1, "Song verschwindet");
        _time.Advance(KisscamMusicLibrary.SaveDebounce);
        await WaitUntil(() => _fs.Writes.Count == 1, "bereinigt");

        Assert.True(KisscamMusicConfigCodec.TryParse(_fs.ConfigText(Folder), out var config));
        Assert.Equal(["a.mp3"], config.Songs.Select(s => s.FileName));
    }

    [Fact]
    public async Task WatcherError_SwitchesTheWatcherOff_RefreshStillWorks()
    {
        var library = await StartedAsync("a.mp3");
        await WaitUntil(() => _fs.WatchersStarted == 1, "Beobachtung läuft");
        var lists = _fs.ListCalls;

        _fs.WatchFailed!(new IOException("Netzlaufwerk getrennt"));
        await WaitUntil(() => _fs.WatchersDisposed == 1, "Beobachtung beendet");
        _fs.WatchChanged!();
        _time.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.Equal(lists, _fs.ListCalls); // keine Änderungsmeldung mehr

        _fs.AddSong(Folder, "neu.mp3");
        library.Refresh();
        await WaitUntil(() => library.Current.Songs.Count == 2, "Aktualisieren wirkt");
        await Task.Delay(50);
        Assert.Equal(1, _fs.WatchersStarted); // nicht erneut angelegt
    }

    [Fact]
    public async Task WatcherCreationThatHangs_IsAbandoned_AfterTheDeadline_WithoutBlockingTheList()
    {
        _fs.SetSongs(Folder, "a.mp3");
        var gate = NewGate();
        _fs.BeforeWatch = gate.Pass;
        var library = Create();

        library.SetFolder(Folder);
        await WaitReady(library);
        await WaitUntil(() => gate.IsEntered, "Beobachtung wird angelegt");
        Assert.Equal(["a.mp3"], library.Current.Songs.Select(s => s.FileName)); // die Liste steht schon

        for (var i = 0; i < 5; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(4)); // über die Frist hinaus (die Frist läuft auf der Testuhr)
            await Task.Delay(20);
        }
        gate.Release(); // das späte Ergebnis wird sofort wieder beendet
        await WaitUntil(() => _fs.WatchersDisposed == 1, "spätes Ergebnis beendet");
    }

    [Fact]
    public async Task ChangingTheFolder_EndsTheOldWatcher()
    {
        var library = await StartedAsync("a.mp3");
        await WaitUntil(() => _fs.WatchersStarted == 1, "Beobachtung läuft");

        library.SetFolder(OtherFolder);

        await WaitUntil(() => _fs.WatchersDisposed == 1, "alte Beobachtung beendet");
        await WaitUntil(() => _fs.WatchersStarted == 2, "neue Beobachtung läuft");
    }

    // ------------------------------------------------------------------ Songlänge

    [Fact]
    public async Task Durations_AreReadAfterwards_AndCachedPerFileStamp()
    {
        _fs.DurationOf = path => path.EndsWith("a.mp3") ? TimeSpan.FromSeconds(185) : null;
        var library = await StartedAsync("a.mp3", "b.mp3");

        await WaitUntil(() => library.Current.Songs.Any(s => s.Duration is not null), "Länge gelesen");
        Assert.Equal(TimeSpan.FromSeconds(185), library.Current.Songs[0].Duration);
        Assert.Null(library.Current.Songs[1].Duration);
        await WaitUntil(() => _fs.DurationCalls == 2, "beide Längen versucht");

        library.Refresh();
        await WaitUntil(() => _fs.ListCalls == 2, "zweites Auflisten");
        await Task.Delay(100);
        Assert.Equal(2, _fs.DurationCalls); // unveränderte Dateien: aus dem Cache
        Assert.Equal(TimeSpan.FromSeconds(185), library.Current.Songs[0].Duration);

        _fs.AddSong(Folder, "a.mp3", length: 2000); // geändert: neu lesen
        library.Refresh();
        await WaitUntil(() => _fs.DurationCalls == 3, "geänderte Datei neu gelesen");
    }

    [Fact]
    public async Task DurationThatHangs_IsAbandoned_AfterTheDeadline_AndTheFolderStaysReady()
    {
        var gate = NewGate();
        _fs.OnDuration = () => gate.Pass(string.Empty);
        _fs.DurationOf = _ => TimeSpan.FromSeconds(60);
        var library = await StartedAsync("a.mp3", "b.mp3");

        await WaitUntil(() => gate.IsEntered, "Länge hängt");
        for (var i = 0; i < 5; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(3)); // über die Frist hinaus (die Frist läuft auf der Testuhr)
            await Task.Delay(20);
        }

        Assert.Equal(KisscamLibraryStatus.Ready, library.Current.Status);
        Assert.Equal(1, _fs.DurationCalls); // solange sie hängt, wird keine weitere gelesen
        Assert.All(library.Current.Songs, s => Assert.Null(s.Duration));
        Assert.Equal(2, library.Current.Songs.Count);

        library.Refresh(); // der Hänger läuft noch: auch jetzt wird nichts Neues gestartet
        await WaitUntil(() => _fs.ListCalls == 2, "zweites Auflisten");
        await Task.Delay(100);
        Assert.Equal(1, _fs.DurationCalls);

        gate.Release(); // der Hänger endet: Beim nächsten Mal wird wieder gelesen
        await Task.Delay(100);
        library.Refresh();
        await WaitUntil(() => _fs.DurationCalls >= 2, "Länge wird wieder gelesen");
        await WaitUntil(() => library.Current.Songs.Any(s => s.Duration is not null), "Länge angezeigt");
    }

    [Fact]
    public async Task HangingDuration_DoesNotDelayASave_NorARefresh()
    {
        var gate = NewGate();
        _fs.OnDuration = () => gate.Pass(string.Empty);
        _fs.DurationOf = _ => TimeSpan.FromSeconds(60);
        var library = await StartedAsync("a.mp3", "b.mp3");
        await WaitUntil(() => gate.IsEntered, "Länge hängt");

        // Die Testuhr bleibt unter der Frist der Länge: Gespeichert und neu gelesen wird trotzdem sofort.
        library.UpdateSong("a.mp3", enabled: true);
        _time.Advance(KisscamMusicLibrary.SaveDebounce);
        await WaitUntil(() => _fs.Writes.Count == 1, "gespeichert, obwohl eine Länge noch gelesen wird");
        library.Refresh();
        await WaitUntil(() => _fs.ListCalls == 2, "neu gelesen, obwohl eine Länge noch gelesen wird");
        Assert.Equal(1, _fs.DurationCalls);
    }

    [Fact]
    public async Task Durations_ContinueWithTheNewerListing_AfterARefresh()
    {
        var gate = NewGate();
        _fs.OnDuration = () => gate.Pass(string.Empty);
        _fs.DurationOf = _ => TimeSpan.FromSeconds(60);
        var library = await StartedAsync("a.mp3", "b.mp3");
        await WaitUntil(() => gate.IsEntered, "Länge hängt");

        _fs.AddSong(Folder, "c.mp3");
        library.Refresh();
        await WaitUntil(() => _fs.ListCalls == 2, "neu gelesen");
        gate.Release();

        await WaitUntil(() => library.Current.Songs.Count == 3 && library.Current.Songs.All(s => s.Duration is not null), "alle Längen gelesen");
    }

    // ------------------------------------------------------------------ Nicht abspielbar

    [Fact]
    public async Task PlaybackFailed_MarksTheSongUnplayable_UntilRefreshOrFileChange()
    {
        var player = new FailingMusicPlayer();
        _fs.SetSongs(Folder, "a.mp3", "b.mp3");
        var library = Create(player);
        library.SetFolder(Folder);
        await WaitReady(library);
        library.UpdateSong("a.mp3", enabled: true);
        await WaitUntil(() => _fs.WatchersStarted == 1, "Beobachtung läuft");

        player.Fail(Path.Combine(Folder, "a.mp3"));
        player.Fail(@"Z:\fremd\x.mp3"); // nicht aus diesem Ordner: ignoriert

        Assert.False(library.Current.Songs[0].Playable);
        Assert.True(library.Current.Songs[1].Playable);
        Assert.Null(KisscamSongPicker.Pick(library.Current, 80, new Random(1)));

        // Eine unveränderte Datei bleibt markiert, auch wenn der Ordner (Beobachtung) neu gelesen wird.
        _fs.WatchChanged!();
        _time.Advance(KisscamMusicLibrary.WatcherDebounce);
        await WaitUntil(() => _fs.ListCalls == 2, "neu gelesen");
        await Task.Delay(50);
        Assert.False(library.Current.Songs[0].Playable);

        // Geänderte Datei: Die Markierung verfällt.
        _fs.AddSong(Folder, "a.mp3", length: 5555);
        _fs.WatchChanged!();
        _time.Advance(KisscamMusicLibrary.WatcherDebounce);
        await WaitUntil(() => _fs.ListCalls == 3, "neu gelesen");
        await WaitUntil(() => library.Current.Songs[0].Playable, "Markierung verfallen");

        // „Aktualisieren“ hebt die Markierung ebenfalls auf.
        player.Fail(Path.Combine(Folder, "a.mp3"));
        Assert.False(library.Current.Songs[0].Playable);
        library.Refresh();
        await WaitUntil(() => library.Current.Songs[0].Playable, "Aktualisieren hebt auf");
    }

    [Fact]
    public async Task PlaybackFailed_ByTheOpenTimeoutAlone_DoesNotMarkTheSong()
    {
        var player = new FailingMusicPlayer();
        _fs.SetSongs(Folder, "a.mp3");
        var library = Create(player);
        library.SetFolder(Folder);
        await WaitReady(library);
        var changed = 0;
        library.Changed += (_, _) => Interlocked.Increment(ref changed);

        player.Fail(Path.Combine(Folder, "a.mp3"), timedOut: true);

        Assert.True(library.Current.Songs[0].Playable);
        Assert.Equal(0, Volatile.Read(ref changed));

        player.Fail(Path.Combine(Folder, "a.mp3")); // ein echter Fehler markiert weiterhin
        Assert.False(library.Current.Songs[0].Playable);
    }

    [Fact]
    public async Task FilesThatDifferOnlyInCase_AppearOnce()
    {
        _fs.SetSongs(Folder, "Lied.mp3", "lied.mp3", "zweites.mp3");
        var library = Create();
        library.SetFolder(Folder);
        await WaitReady(library);

        Assert.Equal(["Lied.mp3", "zweites.mp3"], library.Current.Songs.Select(s => s.FileName));
    }

    [Fact]
    public async Task MarkUnplayable_DoesNotWriteTheConfig()
    {
        var library = await StartedAsync("a.mp3");

        library.MarkUnplayable(Path.Combine(Folder, "a.mp3"));
        _time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        Assert.Empty(_fs.Writes);
    }

    // ------------------------------------------------------------------ Dispose

    [Fact]
    public async Task Dispose_IsFast_EvenWhenTheFileSystemHangs_AndLaterCallsAreNoOps()
    {
        var gate = NewGate();
        _fs.BeforeList = gate.Pass;
        var library = Create();
        library.SetFolder(Folder);
        await WaitUntil(() => gate.IsEntered, "Auflisten hängt");
        var changed = 0;
        library.Changed += (_, _) => Interlocked.Increment(ref changed);

        var sw = Stopwatch.StartNew();
        library.Dispose();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Dispose dauerte {sw.Elapsed}.");
        library.SetFolder(OtherFolder);
        library.Refresh();
        library.UpdateSong("a.mp3", enabled: true);
        library.MarkUnplayable("x.mp3");
        library.Dispose();
        gate.Release();
        await Task.Delay(100);
        Assert.Equal(0, Volatile.Read(ref changed)); // nach dem Ende kein Ereignis mehr
        Assert.Equal(1, _fs.EnsureCalls);
    }

    [Fact]
    public async Task Dispose_SavesAPendingChange_WithinTheDeadline()
    {
        var library = await StartedAsync("a.mp3");
        library.UpdateSong("a.mp3", enabled: true);

        library.Dispose();

        Assert.Single(_fs.Writes);
        Assert.True(KisscamMusicConfigCodec.TryParse(_fs.ConfigText(Folder), out var config));
        Assert.True(config.Songs.Single().Enabled);
    }

    [Fact]
    public async Task Dispose_WithAHangingWrite_StillReturnsInAboutOneSecond()
    {
        var library = await StartedAsync("a.mp3");
        var gate = NewGate();
        _fs.BeforeWrite = gate.Pass;
        library.UpdateSong("a.mp3", enabled: true);

        var sw = Stopwatch.StartNew();
        library.Dispose();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Dispose dauerte {sw.Elapsed}.");
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromThePlayer_AndEndsTheWatcher()
    {
        var player = new FailingMusicPlayer();
        _fs.SetSongs(Folder, "a.mp3");
        var library = Create(player);
        Assert.Equal(1, player.Subscribers);
        library.SetFolder(Folder);
        await WaitReady(library);
        await WaitUntil(() => _fs.WatchersStarted == 1, "Beobachtung läuft");

        library.Dispose();

        Assert.Equal(0, player.Subscribers);
        await WaitUntil(() => _fs.WatchersDisposed == 1, "Beobachtung beendet");
    }

    [Fact]
    public async Task RapidSetAndDispose_DoesNotThrow_OrLeaveWork()
    {
        for (var i = 0; i < 20; i++)
        {
            var library = Create();
            library.SetFolder(i % 2 == 0 ? Folder : OtherFolder);
            library.Refresh();
            library.UpdateSong("a.mp3", enabled: true);
            library.Dispose();
        }

        await Task.Delay(100);
    }

    [Fact]
    public async Task ThrowingChangedHandler_DoesNotBreakTheLibrary()
    {
        _fs.SetSongs(Folder, "a.mp3");
        var library = Create();
        library.Changed += (_, _) => throw new InvalidOperationException("Testfehler");

        library.SetFolder(Folder);

        await WaitReady(library);
        library.UpdateSong("a.mp3", enabled: true);
        Assert.True(library.Current.Songs[0].Enabled);
    }
}
