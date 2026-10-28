using System.Runtime.InteropServices;
using Fotobox.Core;
using Fotobox.Core.Fun;

namespace Fotobox.Services.Audio;

/// <summary>
/// Spielt die selbst erzeugten Signaltöne (<see cref="ToneSynth"/>) über winmm <c>PlaySound</c> aus nativem Speicher. Wirft nie; Fehler
/// (z. B. kein Audiogerät) werden nur geloggt. <c>SoundPlayer</c> wäre hier ungeeignet: Er übergibt sein verwaltetes Byte-Array nur für
/// die Dauer des Aufrufs gepinnt, winmm liest aber asynchron weiter, und der GC könnte das Array währenddessen verschieben (Rauschen,
/// Zugriffsverletzung). Deshalb liegen die Töne in nativem Speicher. <c>PlaySound</c> nutzt die globale Windows-Wiedergabe, ein neuer
/// Ton würde den laufenden abschneiden: Folgt ein Ton, während der vorige noch klingt (Einrasten und Text kommen im selben Moment),
/// wartet er dessen Ende ab. Die Puffer entstehen erst beim ersten Ton, damit ein ausgeschaltetes Feature nichts kostet.
/// </summary>
public sealed class FunSoundPlayer : IFunSoundPlayer, IDisposable
{
    private const int MaxQueued = 3;
    private const int WavHeaderBytes = 44;

    // Flags von PlaySound (mmsystem.h).
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;

    private readonly object _gate = new();
    private readonly Dictionary<FunSoundCue, Entry> _entries = [];
    private readonly Queue<FunSoundCue> _queue = new();
    private readonly Timer _timer;
    private long _busyUntil;
    private bool _disposed;
    private bool _playFailureLogged;

    /// <summary>Ein vorbereiteter Ton: <see cref="IntPtr.Zero"/> als Puffer heißt nicht verfügbar.</summary>
    private sealed record Entry(IntPtr Buffer, TimeSpan Duration);

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
                if (HasPreparedEntryLocked())
                    StopPlayback();
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
            try
            {
                if (HasPreparedEntryLocked())
                    StopPlayback();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Signalton konnte nicht freigegeben werden.", ex);
            }
            // Die Puffer werden bewusst nicht freigegeben: winmm liest asynchron auf einem eigenen Thread, und es ist nicht
            // garantiert, dass das Stoppen synchron abgeschlossen ist. Ein Zugriff auf freigegebenen Speicher wäre schlimmer als
            // das Liegenlassen von wenigen KB; Dispose geschieht nur beim App-Ende.
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
        if (entry.Buffer == IntPtr.Zero)
            return;
        if (!PlaySound(entry.Buffer, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT)) // asynchron
        {
            if (!_playFailureLogged)
            {
                _playFailureLogged = true;
                AppLog.Warn("Signalton konnte nicht gespielt werden (kein Audiogerät?).");
            }
            return;
        }
        _busyUntil = Environment.TickCount64 + (long)entry.Duration.TotalMilliseconds + 15;
    }

    private bool HasPreparedEntryLocked()
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.Buffer != IntPtr.Zero)
                return true;
        }
        return false;
    }

    /// <summary>Hält die laufende Wiedergabe des Prozesses an (wie intern <c>SoundPlayer.Stop</c>).</summary>
    private static void StopPlayback() => PlaySound(IntPtr.Zero, IntPtr.Zero, SND_NODEFAULT);

    private Entry GetEntryLocked(FunSoundCue cue)
    {
        if (_entries.TryGetValue(cue, out var existing))
            return existing;

        var entry = new Entry(IntPtr.Zero, TimeSpan.Zero);
        var buffer = IntPtr.Zero;
        try
        {
            var bytes = ToneSynth.Create(cue);
            if (bytes.Length < WavHeaderBytes)
            {
                AppLog.Warn("Signalton konnte nicht vorbereitet werden (ungültige Wave-Daten).");
            }
            else
            {
                buffer = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                // 44-Byte-Kopf, PCM 16 Bit mono.
                var seconds = Math.Max(0, bytes.Length - WavHeaderBytes) / 2.0 / ToneSynth.SampleRate;
                entry = new Entry(buffer, TimeSpan.FromSeconds(seconds));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Signalton konnte nicht vorbereitet werden.", ex);
            if (buffer != IntPtr.Zero)
            {
                // Noch nie abgespielt, also gefahrlos freizugeben.
                Marshal.FreeHGlobal(buffer);
            }
            entry = new Entry(IntPtr.Zero, TimeSpan.Zero);
        }
        _entries[cue] = entry;
        return entry;
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);
}
