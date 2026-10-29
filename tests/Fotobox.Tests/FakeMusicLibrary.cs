using Fotobox.Core.Music;

namespace Fotobox.Tests;

/// <summary>Musikbibliothek-Attrappe: hält einen Stand im Speicher, zeichnet Aufrufe auf und meldet Änderungen sofort.</summary>
internal sealed class FakeMusicLibrary : IKisscamMusicLibrary
{
    private KisscamLibrarySnapshot _snapshot;

    public FakeMusicLibrary(params KisscamSong[] songs)
    {
        _snapshot = new KisscamLibrarySnapshot(@"C:\Musik", KisscamLibraryStatus.Ready, songs, false);
    }

    public KisscamLibrarySnapshot Current => _snapshot;

    public List<string?> Folders { get; } = [];
    public int Refreshes { get; private set; }
    public List<(string File, bool? Enabled, int? Start, int? Volume)> Updates { get; } = [];
    public List<string> Unplayable { get; } = [];

    public event EventHandler? Changed;

    /// <summary>Setzt den Stand und meldet <see cref="Changed"/>.</summary>
    public void Set(KisscamLibraryStatus status, bool saveFailed = false, params KisscamSong[] songs)
    {
        _snapshot = new KisscamLibrarySnapshot(_snapshot.Folder, status, songs, saveFailed);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Set(params KisscamSong[] songs) => Set(KisscamLibraryStatus.Ready, false, songs);

    public void SetFolder(string? folder)
    {
        Folders.Add(folder);
        _snapshot = _snapshot with { Folder = folder ?? string.Empty };
    }

    public void Refresh() => Refreshes++;

    public void UpdateSong(string fileName, bool? enabled = null, int? startSeconds = null, int? volumePercent = null)
    {
        Updates.Add((fileName, enabled, startSeconds, volumePercent));
        var songs = _snapshot.Songs.Select(s => !string.Equals(s.FileName, fileName, StringComparison.OrdinalIgnoreCase)
            ? s
            : s with
            {
                Enabled = enabled ?? s.Enabled,
                StartSeconds = startSeconds ?? s.StartSeconds,
                VolumePercent = volumePercent ?? s.VolumePercent,
            }).ToList();
        _snapshot = _snapshot with { Songs = songs };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkUnplayable(string path) => Unplayable.Add(path);

    public static KisscamSong Song(string fileName, bool enabled = false, int start = 0, int volume = 100,
        TimeSpan? duration = null, bool playable = true) =>
        new(fileName, @"C:\Musik\" + fileName, enabled, start, volume, duration, playable);
}

/// <summary>Zufallsquelle mit fester Antwort: <c>Next(n)</c> liefert <c>value % n</c>.</summary>
internal sealed class FixedRandom(int value) : Random
{
    public override int Next(int maxValue) => value % maxValue;
}
