using System.IO;
using System.Media;
using Fotobox.Core;
using Fotobox.Core.Fun;

namespace Fotobox.Services.Audio;

/// <summary>
/// Spielt die selbst erzeugten Signaltöne (<see cref="ToneSynth"/>) über <see cref="SoundPlayer"/>. Wirft nie; Fehler (z. B. kein
/// Audiogerät) werden nur geloggt. <see cref="SoundPlayer"/> nutzt die globale Windows-Wiedergabe, ein neuer Ton würde den laufenden
/// abschneiden: Folgt ein Ton, während der vorige noch klingt (Einrasten und Text kommen im selben Moment), wartet er dessen Ende ab.
/// Die Player entstehen erst beim ersten Ton, damit ein ausgeschaltetes Feature nichts kostet.
/// </summary>
public sealed class FunSoundPlayer : IFunSoundPlayer, IDisposable
{
    private const int MaxQueued = 3;

    private readonly object _gate = new();
    private readonly Dictionary<FunSoundCue, Entry> _entries = [];
    private readonly Queue<FunSoundCue> _queue = new();
    private readonly Timer _timer;
    private long _busyUntil;
    private bool _disposed;

    private sealed record Entry(SoundPlayer? Player, MemoryStream? Stream, TimeSpan Duration);

    public FunSoundPlayer()
    {
        _timer = new Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Play(FunSoundCue cue)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                var now = Environment.TickCount64;
                if (now < _busyUntil)
                {
                    if (_queue.Count < MaxQueued)
                        _queue.Enqueue(cue);
                    _timer.Change(Math.Max(1, _busyUntil - now), Timeout.Infinite);
                    return;
                }
                StartLocked(cue);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Signalton konnte nicht gespielt werden.", ex);
        }
    }

    public void Stop()
    {
        try
        {
            lock (_gate)
            {
                _queue.Clear();
                _busyUntil = 0;
                if (_disposed)
                    return;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                // Nur anhalten, was schon einmal gespielt wurde (sonst legt Stop nichts an).
                foreach (var entry in _entries.Values)
                    entry.Player?.Stop();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Signalton konnte nicht angehalten werden.", ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _queue.Clear();
            _timer.Dispose();
            foreach (var entry in _entries.Values)
            {
                try
                {
                    entry.Player?.Stop();
                    entry.Player?.Dispose();
                    entry.Stream?.Dispose();
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Signalton konnte nicht freigegeben werden.", ex);
                }
            }
            _entries.Clear();
        }
    }

    private void OnTimer()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || _queue.Count == 0)
                    return;

                var now = Environment.TickCount64;
                if (now < _busyUntil)
                {
                    _timer.Change(Math.Max(1, _busyUntil - now), Timeout.Infinite);
                    return;
                }
                StartLocked(_queue.Dequeue());
                if (_queue.Count > 0)
                    _timer.Change(Math.Max(1, _busyUntil - Environment.TickCount64), Timeout.Infinite);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Signalton konnte nicht gespielt werden.", ex);
        }
    }

    private void StartLocked(FunSoundCue cue)
    {
        var entry = GetEntryLocked(cue);
        if (entry.Player is null)
            return;
        entry.Player.Play(); // asynchron
        _busyUntil = Environment.TickCount64 + (long)entry.Duration.TotalMilliseconds + 15;
    }

    private Entry GetEntryLocked(FunSoundCue cue)
    {
        if (_entries.TryGetValue(cue, out var existing))
            return existing;

        Entry entry;
        try
        {
            var bytes = ToneSynth.Create(cue);
            var stream = new MemoryStream(bytes, writable: false);
            var player = new SoundPlayer(stream);
            player.Load();
            // 44-Byte-Kopf, PCM 16 Bit mono.
            var seconds = Math.Max(0, bytes.Length - 44) / 2.0 / ToneSynth.SampleRate;
            entry = new Entry(player, stream, TimeSpan.FromSeconds(seconds));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Signalton konnte nicht vorbereitet werden.", ex);
            entry = new Entry(null, null, TimeSpan.Zero);
        }
        _entries[cue] = entry;
        return entry;
    }
}
